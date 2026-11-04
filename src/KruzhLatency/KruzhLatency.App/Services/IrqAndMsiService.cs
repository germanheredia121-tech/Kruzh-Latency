using System;
using System.Collections.Generic;
using System.Linq;
using KruzhLatency.Models;
using KruzhLatency.Core.Devices;
using KruzhLatency.Core.Latency;
using KruzhLatency.Infra.Devices;
using Microsoft.Win32;

namespace KruzhLatency.Services;

public sealed class IrqAndMsiService
{
    private readonly TransactionalAuditService _audit;
    private readonly HardwareChassisService _hwService;

    public IrqAndMsiService(TransactionalAuditService audit, HardwareChassisService hwService)
    {
        _audit = audit;
        _hwService = hwService;
    }

    /// <summary>
    /// Estado de MSI y afinidad de los dispositivos que importan para la latencia (GPU, USB, red, NVMe, audio HD), solo los
    /// presentes y con el nombre que muestra el Administrador de dispositivos (CfgMgr), no el DeviceDesc crudo del registro.
    /// </summary>
    public List<PciMsiDeviceItem> EnumeratePciDevices()
    {
        var tree = new DeviceTree(new CfgMgrDeviceInventory().Snapshot());
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        var results = new List<PciMsiDeviceItem>();
        foreach (var n in tree.All.Where(n => n.IsPci && n.IsPresent && !n.IsDisabled))
        {
            if (MsiPlanner.KindOf(n) is not { } kind) continue;
            using var msiKey = hklm.OpenSubKey($@"{InterruptPath(n.InstanceId)}\MessageSignaledInterruptProperties");
            using var affKey = hklm.OpenSubKey($@"{InterruptPath(n.InstanceId)}\Affinity Policy");

            int core = -1;
            if (affKey?.GetValue("DevicePolicy") is int policy && policy == 4 && affKey.GetValue("AssignmentSetOverride") is byte[] maskBytes)
            {
                ulong mask = InterruptAffinityPlanner.FromMaskBytes(maskBytes);
                if (mask != 0) core = System.Numerics.BitOperations.TrailingZeroCount(mask);
            }

            results.Add(new PciMsiDeviceItem
            {
                FriendlyName = n.DisplayName,
                Category = kind,
                PnpInstanceId = n.InstanceId,
                MsiCapable = n.Interrupts is { } caps && (caps.SupportsMsi || caps.SupportsMsiX),
                MsiSupported = msiKey?.GetValue("MSISupported") is int v ? v == 1 : null,
                AssignedCoreIndex = core,
                ChangedByKruzh = _audit.HasBackupForTweak(AutoMsiService.TweakPrefix + n.InstanceId)
                              || _audit.HasBackupForTweak(AutoAffinityService.TweakPrefix + n.InstanceId)
                              || (kind == "GPU" && _audit.HasBackupForTweak(AutoGpuAffinityService.TweakId)),
            });
        }

        return results
            .OrderBy(d => MsiPlanner.Order(d.Category))
            .ThenBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }


    /// <summary>Foto del árbol de dispositivos (si el llamador no tiene una).</summary>
    private static DeviceTree Tree(DeviceTree? tree) => tree ?? new DeviceTree(new CfgMgrDeviceInventory().Snapshot());

    private static string InterruptPath(string instanceId) =>
        $@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters\Interrupt Management";

    // ------------------------------------------------------------------ E8: MSI

    /// <summary>
    /// E8: MSISupported = 1 en xHCI, audio HD, GPU, red y NVMe que declaran soporte de MSI/MSI-X; en MSI-X además se borra
    /// MessageNumberLimit. La prioridad de interrupción ya no se toca acá: la decide el reparto por núcleo (E7).
    /// </summary>
    public (bool Success, string Summary) ApplySafePciMsiMode(string tweakId = "PciMsiModeV3", DeviceTree? tree = null)
    {
        var targets = MsiPlanner.Targets(Tree(tree));
        if (targets.Count == 0) return (false, "Ningún dispositivo declara soporte de MSI.");

        foreach (var t in targets)
        {
            string msiPath = $@"{InterruptPath(t.Device.InstanceId)}\MessageSignaledInterruptProperties";
            _audit.SetRegistryValue(tweakId, RegistryHive.LocalMachine, msiPath, "MSISupported", 1, RegistryValueKind.DWord);
            if (t.RemoveMessageLimit)
            {
                _audit.DeleteRegistryValue(tweakId, RegistryHive.LocalMachine, msiPath, "MessageNumberLimit");
            }
        }
        return (true, $"MSI activado en {targets.Count} dispositivos ({string.Join(", ", targets.GroupBy(t => t.Kind).Select(g => $"{g.Key} ×{g.Count()}"))}). Reiniciá y mirá el Monitor: si algo empeora, revertí.");
    }

    public bool IsPciMsiModeAllEnabled(DeviceTree? tree = null)
    {
        var targets = MsiPlanner.Targets(Tree(tree));
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        return targets.Count > 0 && targets.All(t =>
        {
            using var k = hklm.OpenSubKey($@"{InterruptPath(t.Device.InstanceId)}\MessageSignaledInterruptProperties");
            return k?.GetValue("MSISupported") is int v && v == 1 &&
                   (!t.RemoveMessageLimit || k.GetValue("MessageNumberLimit") is null);
        });
    }

    // ------------------------------------------------------------------ E7: afinidad por núcleo físico

    public AffinityPlan? PlanAffinity(DeviceTree? tree = null, bool gpuTwoCores = true) =>
        InterruptAffinityPlanner.Plan(_hwService.CurrentProfile.CoreTopology, InterruptAffinityPlanner.Collect(Tree(tree)), gpuTwoCores);

    /// <summary>
    /// E7: DevicePolicy = 4 + AssignmentSetOverride según el planificador (núcleo 0 libre, un núcleo por controladora USB,
    /// GPU en uno o dos núcleos, NVMe aparte). La red no lleva override: sus colas RSS las fija el ítem de la placa (E2).
    /// </summary>
    public (bool Success, string Summary) ApplyOptimalPciIrqAffinity(string tweakId = "PciIrqSmartBalancer", DeviceTree? tree = null, bool gpuTwoCores = true)
    {
        if (PlanAffinity(tree, gpuTwoCores) is not { } plan)
        {
            return (false, "Hacen falta al menos 4 núcleos físicos para separar las interrupciones.");
        }

        foreach (var a in plan.Assignments)
        {
            string affPath = $@"{InterruptPath(a.Device.InstanceId)}\Affinity Policy";
            if (a.MaskBytes is { } bytes)
            {
                _audit.SetRegistryValue(tweakId, RegistryHive.LocalMachine, affPath, "DevicePolicy", 4, RegistryValueKind.DWord);
                _audit.SetRegistryValue(tweakId, RegistryHive.LocalMachine, affPath, "AssignmentSetOverride", bytes, RegistryValueKind.Binary);
            }
            else
            {
                // Red: sin override (una afinidad vieja pisaría a RSS).
                _audit.DeleteRegistryValue(tweakId, RegistryHive.LocalMachine, affPath, "DevicePolicy");
                _audit.DeleteRegistryValue(tweakId, RegistryHive.LocalMachine, affPath, "AssignmentSetOverride");
            }

            if (a.DevicePriority is { } prio)
            {
                _audit.SetRegistryValue(tweakId, RegistryHive.LocalMachine, affPath, "DevicePriority", prio, RegistryValueKind.DWord);
            }
            else
            {
                _audit.DeleteRegistryValue(tweakId, RegistryHive.LocalMachine, affPath, "DevicePriority");
            }
        }

        return (true, Describe(plan) + " Reiniciá para que se aplique.");
    }

    /// <summary>Aplicado = cada dispositivo del plan tiene exactamente la máscara y la prioridad planeadas.</summary>
    public bool IsPciIrqAffinityActive(DeviceTree? tree = null, bool gpuTwoCores = true)
    {
        if (PlanAffinity(tree, gpuTwoCores) is not { } plan || plan.Assignments.Count == 0) return false;
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        return plan.Assignments.All(a =>
        {
            using var k = hklm.OpenSubKey($@"{InterruptPath(a.Device.InstanceId)}\Affinity Policy");
            bool maskOk = a.Mask is { } m
                ? k?.GetValue("DevicePolicy") is int p && p == 4 &&
                  k.GetValue("AssignmentSetOverride") is byte[] b && InterruptAffinityPlanner.FromMaskBytes(b) == m
                : k?.GetValue("AssignmentSetOverride") is null;
            bool prioOk = a.DevicePriority is { } prio ? k?.GetValue("DevicePriority") is int dp && dp == prio : k?.GetValue("DevicePriority") is null;
            return maskOk && prioOk;
        });
    }

    public static string Describe(AffinityPlan plan)
    {
        static string Cpus(IReadOnlyList<int> t) => t.Count == 1 ? $"CPU {t[0]}" : "CPU " + string.Join(" + ", t);
        var parts = new List<string>();
        if (plan.Rss is { } r) parts.Add($"Red: RSS en CPU {r.BaseProcessor}{(r.MaxProcessor != r.BaseProcessor ? $"–{r.MaxProcessor}" : "")}");
        foreach (var a in plan.Assignments.Where(a => a.Mask is not null))
        {
            parts.Add($"{a.Device.Name}: {Cpus(a.Threads)}{(a.DevicePriority == InterruptAffinityPlanner.UsbPriorityHigh ? " (prioridad alta)" : "")}{(a.SharesCore ? " (compartido)" : "")}");
        }
        return "CPU 0 libre. " + string.Join(" · ", parts) + "." + (plan.Notes.Count > 0 ? " " + string.Join(" ", plan.Notes) : "");
    }
}
