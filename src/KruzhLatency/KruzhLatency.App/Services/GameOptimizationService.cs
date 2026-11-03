using System;
using System.IO;
using System.Linq;
using KruzhLatency.Models;
using Microsoft.Win32;

namespace KruzhLatency.Services;

/// <summary>
/// Optimizaciones POR JUEGO (CS2 / VALORANT / exe agregado a mano), dentro del límite anticheat (ADR-004):
/// solo IFEO PerfOptions, preferencia de GPU y política QoS. Nunca toca archivos ni memoria del juego.
/// Cada juego tiene su propio id en el journal ("Game:&lt;exe&gt;") para poder revertirlo por separado.
/// Interino hasta T34 (catálogo declarativo por juego en Core).
/// </summary>
public sealed class GameOptimizationService
{
    private readonly TransactionalAuditService _audit;

    public GameOptimizationService(TransactionalAuditService audit)
    {
        _audit = audit;
    }

    public static string JournalId(GameTargetItem game) => $"Game:{game.ExecutableName.ToLowerInvariant()}";

    private static string IfeoPerfPath(GameTargetItem game) =>
        $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{game.ExecutableName}\PerfOptions";

    private static string QosPath(GameTargetItem game) =>
        $@"SOFTWARE\Policies\Microsoft\Windows\QoS\KRUZH_{Path.GetFileNameWithoutExtension(game.ExecutableName)}";

    private const string GpuPrefPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    // ---------------------------------------------------------------- lectura del estado REAL

    public bool IsHighPriorityApplied(GameTargetItem game) =>
        ReadDword(RegistryHive.LocalMachine, IfeoPerfPath(game), "CpuPriorityClass") == 3;

    public bool IsQosApplied(GameTargetItem game) =>
        string.Equals(ReadString(RegistryHive.LocalMachine, QosPath(game), "DSCP Value"), "46", StringComparison.Ordinal);

    public bool IsGpuPreferenceApplied(GameTargetItem game) =>
        !string.IsNullOrWhiteSpace(game.FullExePath) &&
        (ReadString(RegistryHive.CurrentUser, GpuPrefPath, game.FullExePath)?.Contains("GpuPreference=2", StringComparison.OrdinalIgnoreCase) ?? false);

    public bool IsAnyApplied(GameTargetItem game) =>
        IsHighPriorityApplied(game) || IsQosApplied(game) || IsGpuPreferenceApplied(game);

    // ---------------------------------------------------------------- aplicar / revertir (llamar desde background)

    public (bool Success, string Detail) Apply(GameTargetItem game, bool highPriority, bool qos, bool gpuPreference)
    {
        try
        {
            string id = JournalId(game);
            // Partimos del estado original: lo que el usuario desmarcó vuelve a como estaba.
            _audit.RevertTweakFromJournal(id, out _);

            if (highPriority)
            {
                _audit.SetRegistryValue(id, RegistryHive.LocalMachine, IfeoPerfPath(game), "CpuPriorityClass", 3, RegistryValueKind.DWord);
                _audit.SetRegistryValue(id, RegistryHive.LocalMachine, IfeoPerfPath(game), "IoPriority", 3, RegistryValueKind.DWord);
            }

            if (qos)
            {
                string p = QosPath(game);
                _audit.SetRegistryValue(id, RegistryHive.LocalMachine, p, "Version", "1.0", RegistryValueKind.String);
                _audit.SetRegistryValue(id, RegistryHive.LocalMachine, p, "Application Name", game.ExecutableName, RegistryValueKind.String);
                _audit.SetRegistryValue(id, RegistryHive.LocalMachine, p, "Protocol", "UDP", RegistryValueKind.String);
                _audit.SetRegistryValue(id, RegistryHive.LocalMachine, p, "Local Port", "*", RegistryValueKind.String);
                _audit.SetRegistryValue(id, RegistryHive.LocalMachine, p, "Remote Port", "*", RegistryValueKind.String);
                _audit.SetRegistryValue(id, RegistryHive.LocalMachine, p, "Local IP", "*", RegistryValueKind.String);
                _audit.SetRegistryValue(id, RegistryHive.LocalMachine, p, "Local IP Prefix", "*", RegistryValueKind.String);
                _audit.SetRegistryValue(id, RegistryHive.LocalMachine, p, "Remote IP", "*", RegistryValueKind.String);
                _audit.SetRegistryValue(id, RegistryHive.LocalMachine, p, "Remote IP Prefix", "*", RegistryValueKind.String);
                _audit.SetRegistryValue(id, RegistryHive.LocalMachine, p, "DSCP Value", "46", RegistryValueKind.String);
                _audit.SetRegistryValue(id, RegistryHive.LocalMachine, p, "Throttle Rate", "-1", RegistryValueKind.String);
            }

            if (gpuPreference && !string.IsNullOrWhiteSpace(game.FullExePath))
            {
                _audit.SetRegistryValue(id, RegistryHive.CurrentUser, GpuPrefPath, game.FullExePath, "GpuPreference=2;", RegistryValueKind.String);
            }

            int count = (highPriority ? 1 : 0) + (qos ? 1 : 0) + (gpuPreference ? 1 : 0);
            return (true, count == 0
                ? $"{game.Name}: sin optimizaciones activas (valores originales restaurados)."
                : $"{game.Name}: {count} optimización(es) aplicadas.");
        }
        catch (Exception ex)
        {
            App.LogError($"Game apply {game.ExecutableName}", ex);
            return (false, $"{game.Name}: error al aplicar ({ex.Message}).");
        }
    }

    public (bool Success, string Detail) Revert(GameTargetItem game)
    {
        try
        {
            bool reverted = _audit.RevertTweakFromJournal(JournalId(game), out string msg);
            return (true, reverted ? $"{game.Name}: valores originales restaurados." : $"{game.Name}: no había cambios para revertir.");
        }
        catch (Exception ex)
        {
            App.LogError($"Game revert {game.ExecutableName}", ex);
            return (false, $"{game.Name}: error al revertir ({ex.Message}).");
        }
    }

    // ---------------------------------------------------------------- helpers

    private static int? ReadDword(RegistryHive hive, string subKey, string name)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var k = baseKey.OpenSubKey(subKey);
        return k?.GetValue(name) as int?;
    }

    private static string? ReadString(RegistryHive hive, string subKey, string name)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var k = baseKey.OpenSubKey(subKey);
        return k?.GetValue(name)?.ToString();
    }

    /// <summary>
    /// Preferencia de GPU solo tiene sentido con más de una GPU física (iGPU + dedicada, típico en laptops).
    /// Con una sola GPU no hace nada, así que no se ofrece.
    /// </summary>
    public static bool HasMultiplePhysicalGpus()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var pci = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI");
            if (pci == null) return false;
            int count = 0;
            foreach (string dev in pci.GetSubKeyNames())
            {
                using var devKey = pci.OpenSubKey(dev);
                if (devKey == null) continue;
                // Instancias PCI de clase Display (excluye adaptadores virtuales, que no son PCI) y PRESENTES:
                // la subclave volátil "Control" solo existe mientras el dispositivo está iniciado (descarta GPUs viejas).
                if (devKey.GetSubKeyNames().Any(inst =>
                {
                    using var ik = devKey.OpenSubKey(inst);
                    if (ik == null || !string.Equals(ik.GetValue("Class")?.ToString(), "Display", StringComparison.OrdinalIgnoreCase)) return false;
                    using var control = ik.OpenSubKey("Control");
                    return control != null;
                }))
                {
                    count++;
                }
            }
            return count >= 2;
        }
        catch
        {
            return false;
        }
    }
}
