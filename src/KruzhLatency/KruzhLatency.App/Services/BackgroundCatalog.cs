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
        BuildHardwareAccelItem();
        BuildSvcHostGroupingItem();
    }

    // ---------------------------------------------------------------- aceleración por hardware (CPU como límite)

    private void BuildHardwareAccelItem()
    {
        IReadOnlyList<KruzhLatency.Core.Debloat.AccelApp> apps;
        try { apps = HardwareAccelerationProbe.Detect(); }
        catch (Exception ex) { App.LogError("Aceleración por hardware: detectar", ex); return; }
        if (apps.Count == 0) return;

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "AppsHardwareAccelOn",
            CodeNumber = "AP",
            Title = "Aceleración por hardware en Discord y el navegador",
            Category = TweakCategory.Apps,
            CategoryDisplay = "Segundo plano",
            TechnicalDescription = "En CS2 y VALORANT a cientos de FPS el límite es la CPU, no la GPU. Si Discord o el navegador tienen la aceleración por hardware " +
                                   "APAGADA, dibujan su interfaz y decodifican video (streams, YouTube, Twitch) con la CPU y compiten con el juego. " +
                                   "Activada, ese trabajo va a la GPU, que tiene margen, y el video usa su decodificador dedicado (NVDEC/VCN), separado del render del juego. " +
                                   "El consejo de apagarla solo sirve si tu juego está limitado por la GPU.\n" +
                                   "En tu PC: " + string.Join(" · ", apps.Select(a => $"{a.Name}: {(a.Enabled ? "activada ✓" : "APAGADA")}")) + ".\n" +
                                   "Para activarla: " + string.Join(" ", apps.Where(a => !a.Enabled).Select(a => $"{a.Name}: {a.HowToEnable}")).TrimEnd() +
                                   (apps.All(a => a.Enabled) ? "ya está activada en todas." : ""),
            WhatItTouches = "Nada: KRUZH solo lee la configuración de cada app (Local State de Chromium, settings.json de Discord). El cambio lo hacés en la app.",
            SourceAttribution = "Chromium / Discord: aceleración de hardware · PresentMon (CPU vs GPU Busy) · medición en docs/AUDITORIA.md",
            Risk = RiskLevel.Low,
            InMinimal = false, InCompetitivo = false, InProfesional = false, InEntusiasta = false,
            EvaluateGate = _ => apps.All(a => a.Enabled)
                ? EvaluationResult.Recommended("Ya está activada en todas tus apps.")
                : EvaluationResult.Recommended("Activala: con la CPU como límite, apagarla resta FPS en el juego."),
            ReadLiveState = () => HardwareAccelerationProbe.Detect().All(a => a.Enabled),
            ApplyAction = _ =>
            {
                var off = HardwareAccelerationProbe.Detect().Where(a => !a.Enabled).ToList();
                return off.Count == 0
                    ? (true, "La aceleración por hardware ya está activada en todas tus apps.")
                    : (false, "Activala en: " + string.Join(" ", off.Select(a => $"{a.Name}: {a.HowToEnable}")));
            }
        });
    }

    // ---------------------------------------------------------------- servicios agrupados en menos svchost

    private void BuildSvcHostGroupingItem()
    {
        const string id = "SvcHostGrouping";
        const string key = @"SYSTEM\CurrentControlSet\Control";
        const int neverSplit = unchecked((int)0xFFFFFFFF);

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = id,
            CodeNumber = "SV",
            Title = "Agrupar servicios en menos procesos svchost",
            Category = TweakCategory.Services,
            CategoryDisplay = "Rendimiento en segundo plano",
            TechnicalDescription = "Con más de 3,5 GB de RAM, Windows pone cada servicio en su propio proceso svchost (por eso hay 60 o más). " +
                                   "Subiendo el umbral vuelven a compartir procesos, como en Windows 10 anterior a 1703 y en los Windows modificados: " +
                                   "baja mucho la cantidad de procesos y algo de memoria. La CPU que usan los servicios es la misma, así que no esperes FPS por esto solo: " +
                                   "medí antes y después. Si un servicio falla, arrastra a los de su grupo. Requiere reiniciar.",
            WhatItTouches = $@"HKLM\{key} -> SvcHostSplitThresholdInKB = 0xFFFFFFFF",
            SourceAttribution = "Microsoft Learn: Changes to Service Host grouping in Windows 10 (SvcHostSplitThresholdInKB)",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = false, InCompetitivo = false, InProfesional = false, InEntusiasta = false, // sin medición propia todavía (ADR-002)
            EvaluateGate = _ => EvaluationResult.Neutral("Opcional: menos procesos y algo menos de RAM; el efecto en FPS no está medido."),
            ReadLiveState = () => ReadRegDword(RegistryHive.LocalMachine, key, "SvcHostSplitThresholdInKB") == neverSplit,
            ApplyAction = _ =>
            {
                _audit.SetRegistryValue(id, RegistryHive.LocalMachine, key, "SvcHostSplitThresholdInKB", neverSplit, RegistryValueKind.DWord);
                return (true, "Servicios agrupados: reiniciá para ver menos procesos svchost.");
            }
        });
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
                                   "Si hay hasta 50 archivos que están solo en la nube, KRUZH los descarga primero para que no pierdas acceso desde la PC; si hay más, te pide que los bajes vos. " +
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
                const int autoDownloadLimit = 50;
                var cloudOnly = OneDriveStore.CloudOnlyFiles(max: autoDownloadLimit + 1);
                if (cloudOnly.Count > autoDownloadLimit)
                {
                    return (false, $"Hay más de {autoDownloadLimit} archivos que están solo en la nube: KRUZH no los baja solo para no descargarte gigas sin avisar. " +
                                   "Clic derecho en la carpeta OneDrive → \"Mantener siempre en este dispositivo\", esperá que terminen y volvé a aplicar.");
                }
                if (cloudOnly.Count > 0)
                {
                    // Pocos archivos: se descargan solos antes de desinstalar, así no se pierde el acceso desde la PC.
                    var pending = OneDriveStore.Hydrate(cloudOnly, TimeSpan.FromMinutes(3));
                    if (pending.Count > 0)
                    {
                        return (false, $"No se pudieron bajar {pending.Count} archivos que están solo en la nube (por ejemplo {Path.GetFileName(pending[0])}). " +
                                       "Revisá tu conexión o abrilos a mano, y volvé a aplicar.");
                    }
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
