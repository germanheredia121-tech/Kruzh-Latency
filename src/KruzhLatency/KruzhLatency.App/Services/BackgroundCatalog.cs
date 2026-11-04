using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KruzhLatency.Core.Tweaks;
using KruzhLatency.Infra.Debloat;
using KruzhLatency.Models;
using Microsoft.Win32;
using RiskLevel = KruzhLatency.Models.RiskLevel;
using TweakCategory = KruzhLatency.Models.TweakCategory;

namespace KruzhLatency.Services;

/// <summary>
/// Programas de Microsoft que quedan corriendo en segundo plano: Edge (arranque anticipado y modo en segundo plano),
/// Optimización de distribución (P2P), OneDrive y las tareas de avisos de Windows. Edge no se desinstala:
/// WebView2 (que usan KRUZH, Discord, Steam y otras apps) depende de él.
/// </summary>
public sealed partial class DeclarativeTweakEngine
{
    private const string EdgePolicies = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EdgeAutoLaunchPrefix = "MicrosoftEdgeAutoLaunch_";

    private void BuildBackgroundItems()
    {
        BuildEdgeItem();

        _allTweaks.Add(RegistryPolicyItem("DeliveryOptimizationNoP2P", "Optimización de distribución sin P2P", "Segundo plano",
            "Windows descarga las actualizaciones solo de los servidores de Microsoft, sin compartirlas con otras PCs de tu red ni de internet. " +
            "Así no usa tu subida mientras jugás.",
            "Microsoft Learn: Delivery Optimization reference (DODownloadMode = 0, HTTP only)",
            Profile.Competitivo,
            new[] { (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0) }));

        BuildWindowsNagTasksItem();
        BuildOneDriveItem();
    }

    // ---------------------------------------------------------------- Edge

    private static IReadOnlyList<string> EdgeAutoLaunchValues()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        return run?.GetValueNames().Where(n => n.StartsWith(EdgeAutoLaunchPrefix, StringComparison.OrdinalIgnoreCase)).ToList()
               ?? new List<string>();
    }

    private void BuildEdgeItem()
    {
        const string id = "EdgeBackgroundOff";
        string edgeExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = id,
            CodeNumber = "AP",
            Title = "Microsoft Edge sin procesos en segundo plano",
            Category = TweakCategory.Apps,
            CategoryDisplay = "Segundo plano",
            TechnicalDescription = "Apaga el arranque anticipado de Edge (deja procesos de Edge cargados desde que prendés la PC) y que siga corriendo " +
                                   "después de cerrarlo, y saca su entrada de inicio automático. Edge sigue instalado y funciona igual al abrirlo: " +
                                   "no se desinstala porque WebView2, que usan muchas apps, depende de él.",
            WhatItTouches = $@"HKLM\{EdgePolicies} -> StartupBoostEnabled = 0, BackgroundModeEnabled = 0 · HKCU\{RunKey} -> {EdgeAutoLaunchPrefix}* (se borra)",
            SourceAttribution = "Microsoft Learn: Microsoft Edge policies (StartupBoostEnabled, BackgroundModeEnabled)",
            Risk = RiskLevel.Low,
            InCompetitivo = true,
            InProfesional = true,
            ApplicableGate = _ => File.Exists(edgeExe)
                ? ApplicabilityResult.Applicable()
                : ApplicabilityResult.NotApplicable("Edge no está instalado."),
            ReadLiveState = () => ReadRegDword(RegistryHive.LocalMachine, EdgePolicies, "StartupBoostEnabled") == 0 &&
                                  ReadRegDword(RegistryHive.LocalMachine, EdgePolicies, "BackgroundModeEnabled") == 0 &&
                                  EdgeAutoLaunchValues().Count == 0,
            ApplyAction = _ =>
            {
                _audit.SetRegistryValue(id, RegistryHive.LocalMachine, EdgePolicies, "StartupBoostEnabled", 0, RegistryValueKind.DWord);
                _audit.SetRegistryValue(id, RegistryHive.LocalMachine, EdgePolicies, "BackgroundModeEnabled", 0, RegistryValueKind.DWord);
                foreach (string name in EdgeAutoLaunchValues())
                {
                    _audit.DeleteRegistryValue(id, RegistryHive.CurrentUser, RunKey, name);
                }
                return (true, "Edge ya no queda corriendo en segundo plano.");
            }
        });
    }

    // ---------------------------------------------------------------- avisos de Windows (tareas por usuario)

    /// <summary>"SoftLanding" (consejos y contenido promocional) y el aviso de "terminar de configurar el dispositivo".</summary>
    private static IReadOnlyList<string> WindowsNagTasks() =>
        ScheduledTaskStore.List(@"\SoftLanding", recursive: true)
            .Concat(ScheduledTaskStore.List(@"\", recursive: false)
                .Where(p => p.TrimStart('\\').StartsWith("PostponeDeviceSetupToast", StringComparison.OrdinalIgnoreCase)))
            .ToList();

    private void BuildWindowsNagTasksItem()
    {
        const string id = "WindowsNagTasksOff";
        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = id,
            CodeNumber = "AP",
            Title = "Tareas de consejos y avisos de Windows",
            Category = TweakCategory.Apps,
            CategoryDisplay = "Publicidad y sugerencias",
            TechnicalDescription = "Desactiva las tareas programadas que bajan consejos y contenido promocional (SoftLanding) y la que muestra " +
                                   "\"Terminemos de configurar tu dispositivo\". Se desactivan, no se borran: revertir las vuelve a activar.",
            WhatItTouches = @"Programador de tareas: \SoftLanding\<tu usuario>\*, \PostponeDeviceSetupToast_*",
            SourceAttribution = "Programador de tareas de Windows (tareas por usuario de Windows 11)",
            Risk = RiskLevel.Low,
            InCompetitivo = true,
            InProfesional = true,
            ApplicableGate = _ => WindowsNagTasks().Count == 0
                ? ApplicabilityResult.NotApplicable("Tu Windows no tiene estas tareas.")
                : ApplicabilityResult.Applicable(),
            ReadLiveState = () => WindowsNagTasks().All(t => ScheduledTaskStore.IsEnabled(t) != true),
            ApplyAction = _ =>
            {
                var results = WindowsNagTasks().Select(t => _audit.SetScheduledTaskEnabled(id, t, false)).ToList();
                return results.All(ok => ok)
                    ? (true, $"{results.Count} tareas de avisos desactivadas.")
                    : (false, $"{results.Count(ok => ok)} de {results.Count} desactivadas; Windows no dejó cambiar el resto.");
            }
        });
    }

    // ---------------------------------------------------------------- OneDrive

    private void BuildOneDriveItem()
    {
        const string id = "OneDriveRemove";
        const string policyKey = @"SOFTWARE\Policies\Microsoft\Windows\OneDrive";

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = id,
            CodeNumber = "AP",
            Title = "Desinstalar OneDrive",
            Category = TweakCategory.Apps,
            CategoryDisplay = "Segundo plano",
            TechnicalDescription = "Desinstala OneDrive con su propio desinstalador: deja de sincronizar en segundo plano (disco, red y CPU). " +
                                   "Tus archivos quedan en la carpeta OneDrive de tu usuario, sin sincronizar; el Escritorio y Documentos siguen donde están. " +
                                   "Además activa la política que impide que OneDrive vuelva a usarse solo después de una actualización de Windows.\n" +
                                   "⚠ Si tenés archivos que están solo en la nube, KRUZH no desinstala: bajalos primero (clic derecho → \"Mantener siempre en este dispositivo\"). " +
                                   "Revertir quita la política; para volver a instalar OneDrive: winget install Microsoft.OneDrive.",
            WhatItTouches = $@"OneDriveSetup.exe /uninstall · HKLM\{policyKey} -> DisableFileSyncNGSC = 1",
            SourceAttribution = "Microsoft Learn: Uninstall OneDrive · OneDrive policy DisableFileSyncNGSC",
            Risk = RiskLevel.Medium,
            InMinimal = false, InCompetitivo = false, InProfesional = false, InEntusiasta = false, // decisión del usuario
            EvaluateGate = _ => EvaluationResult.Neutral("Solo si no usás OneDrive para sincronizar tus archivos."),
            ApplicableGate = _ => OneDriveStore.Find() is null && ReadRegDword(RegistryHive.LocalMachine, policyKey, "DisableFileSyncNGSC") == 1
                ? ApplicabilityResult.NotApplicable("OneDrive no está instalado.")
                : ApplicabilityResult.Applicable(),
            ReadLiveState = () => OneDriveStore.Find() is null &&
                                  ReadRegDword(RegistryHive.LocalMachine, policyKey, "DisableFileSyncNGSC") == 1,
            ApplyAction = _ =>
            {
                var cloudOnly = OneDriveStore.CloudOnlyFiles(max: 10);
                if (cloudOnly.Count > 0)
                {
                    return (false, $"Hay {(cloudOnly.Count >= 10 ? "10 o más" : cloudOnly.Count.ToString())} archivos que están solo en la nube " +
                                   $"(por ejemplo {Path.GetFileName(cloudOnly[0])}). Bajalos (clic derecho → \"Mantener siempre en este dispositivo\") o borralos y volvé a aplicar.");
                }

                var (ok, detail) = OneDriveStore.Uninstall();
                if (!ok) return (false, detail);
                _audit.SetRegistryValue(id, RegistryHive.LocalMachine, policyKey, "DisableFileSyncNGSC", 1, RegistryValueKind.DWord);
                return (true, detail);
            },
            CustomRevertFallback = () => (false, "OneDrive no se reinstala solo: winget install Microsoft.OneDrive (o desde microsoft.com/onedrive).")
        });
    }
}
