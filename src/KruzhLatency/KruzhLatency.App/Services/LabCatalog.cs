using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using KruzhLatency.Infra.Network;
using KruzhLatency.Models;
using Microsoft.Win32;

namespace KruzhLatency.Services;

/// <summary>
/// ADR-011: "Laboratorio (sin evidencia)". Tweaks populares sin evidencia de efecto (chat5 y guías de la comunidad):
/// nunca en perfiles, se aplican a mano, con journal y revert exacto. Los que bajan la seguridad muestran el modal rojo.
/// Además, los ítems útiles del chat5 que faltaban (atajos de accesibilidad, mejoras de audio, guía de parámetros de CS2).
/// </summary>
public sealed partial class DeclarativeTweakEngine
{
    private const string LabSection = "Laboratorio (sin evidencia)";
    private const string LabNote = "\n🧪 Laboratorio: no hay evidencia de que mejore algo. Aplicalo solo, medí antes y después " +
                                   "(benchmark de CS2 y PresentMon) y revertí si no ganás.";
    private const string Chat5 = "Guía de la comunidad (chat5) · sin documentación de efecto";

    private sealed record LabValue(RegistryHive Hive, string Path, string Name, object Value, RegistryValueKind Kind);

    private static bool IsAt(LabValue v) => v.Kind == RegistryValueKind.DWord
        ? ReadRegDword(v.Hive, v.Path, v.Name) == (int)v.Value
        : string.Equals(ReadRegString(v.Hive, v.Path, v.Name), Convert.ToString(v.Value), StringComparison.Ordinal);

    private static string Describe(LabValue v) =>
        $@"{(v.Hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\{v.Path} -> {v.Name} = {(v.Value is int i && i > 9 ? $"0x{i:X}" : v.Value)}";

    /// <summary>Ítem de valores de registro (DWORD o texto) con journal. Por defecto es de Laboratorio (sin perfiles).</summary>
    private DeclarativeTweakItem RegistryValuesItem(string id, string code, string title, TweakCategory category, string section,
        string description, string source, RiskLevel risk, bool reboot, LabValue[] values,
        bool reducesSecurity = false, Func<HardwareProfile, ApplicabilityResult>? applicable = null,
        bool inCompetitivo = false, bool inProfesional = false, EvaluationResult? evaluation = null) => new()
    {
        Id = id,
        CodeNumber = code,
        Title = title,
        Category = category,
        CategoryDisplay = section,
        TechnicalDescription = description,
        WhatItTouches = string.Join(" · ", values.Select(Describe)),
        SourceAttribution = source,
        Risk = risk,
        SecurityImpact = reducesSecurity ? SecurityImpact.ReducesSecurity : SecurityImpact.None,
        RequiresReboot = reboot,
        InMinimal = false,
        InCompetitivo = inCompetitivo,
        InProfesional = inProfesional,
        InEntusiasta = inCompetitivo || inProfesional,
        ApplicableGate = applicable ?? (_ => ApplicabilityResult.Applicable()),
        EvaluateGate = _ => evaluation ?? EvaluationResult.Neutral("Laboratorio: medí antes y después."),
        ReadLiveState = () => values.All(IsAt),
        ApplyAction = _ =>
        {
            foreach (var v in values) _audit.SetRegistryValue(id, v.Hive, v.Path, v.Name, v.Value, v.Kind);
            return (true, reboot ? $"{title}: aplicado. Reiniciá para que tome efecto." : $"{title}: aplicado.");
        }
    };

    private void BuildChat5Items()
    {
        // ---------------------------------------------------------------- útiles (catálogo normal)
        const string acc = @"Control Panel\Accessibility";
        _allTweaks.Add(RegistryValuesItem("AccessibilityHotkeysOff", "I03", "Atajos de accesibilidad apagados (Shift x5, Bloq Num, Shift derecho)",
            TweakCategory.MouseInput, "Mouse & Teclado",
            "Apaga solo los atajos de teclado que activan Teclas especiales (Shift 5 veces), Teclas de alternancia (Bloq Num 5 s), " +
            "Teclas filtro (Shift derecho 8 s) y Teclas de mouse. En partida, apretar Shift repetido abre la ventana de Teclas especiales y te saca del juego. " +
            "Las funciones de accesibilidad siguen disponibles desde Configuración.",
            "Microsoft Learn: STICKYKEYS / TOGGLEKEYS / FILTERKEYS / MOUSEKEYS (bandera *_HOTKEYACTIVE)",
            RiskLevel.Low, reboot: false,
            new[]
            {
                new LabValue(RegistryHive.CurrentUser, $@"{acc}\StickyKeys", "Flags", "506", RegistryValueKind.String),
                new LabValue(RegistryHive.CurrentUser, $@"{acc}\ToggleKeys", "Flags", "58", RegistryValueKind.String),
                new LabValue(RegistryHive.CurrentUser, $@"{acc}\Keyboard Response", "Flags", "122", RegistryValueKind.String),
                new LabValue(RegistryHive.CurrentUser, $@"{acc}\MouseKeys", "Flags", "58", RegistryValueKind.String),
            },
            inCompetitivo: true, inProfesional: true,
            evaluation: EvaluationResult.Recommended("Evita que Windows te saque del juego por apretar Shift varias veces.")));

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "AudioEnhancementsOff",
            CodeNumber = "I04",
            Title = "Mejoras de audio y sonido espacial apagados",
            Category = TweakCategory.IrqMsiAudio,
            CategoryDisplay = "IRQ, MSI & Audio",
            TechnicalDescription = "Las \"mejoras de audio\" (APO) y el sonido espacial procesan cada muestra antes de que llegue a tus auriculares: suman latencia " +
                                   "y alteran el sonido de pasos. En Sonido → tu dispositivo → Propiedades → Opciones avanzadas: destildá \"Habilitar mejoras de audio\" " +
                                   "y en Sonido espacial elegí Desactivado. Hacelo en auriculares y micrófono. KRUZH abre el panel: el cambio lo hacés vos.",
            WhatItTouches = "Nada: abre mmsys.cpl.",
            SourceAttribution = "Microsoft Learn: Audio Processing Objects (APO) · chat5",
            Risk = RiskLevel.Low,
            InMinimal = false, InCompetitivo = false, InProfesional = false, InEntusiasta = false,
            EvaluateGate = _ => EvaluationResult.Recommended("Se hace a mano en el panel de Sonido."),
            ReadLiveState = () => false,
            ApplyAction = _ =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl") { UseShellExecute = true })?.Dispose();
                    return (true, "Se abrió el panel de Sonido.");
                }
                catch (Exception ex) { return (false, $"No se pudo abrir el panel de Sonido: {ex.Message}"); }
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "Cs2LaunchGuide",
            CodeNumber = "F02",
            Title = "Parámetros de CS2 (guía)",
            Category = TweakCategory.FpsBoostGames,
            CategoryDisplay = "FPS Boost & Juegos",
            TechnicalDescription = "KRUZH no escribe en la configuración del juego (límite anticheat, ADR-004). Ponelos vos:\n" +
                                   "• Opciones de lanzamiento en Steam: -nojoy (no carga el soporte de joystick). -threads N no tiene evidencia de mejora: si lo probás, medí.\n" +
                                   "• Consola: engine_low_latency_sleep_after_client_tick true · fps_max con un tope que tu PC sostenga (o 0) · cl_net_buffer_ticks 0.\n" +
                                   "• Video: NVIDIA Reflex en Activado (sin Boost con la CPU como límite).\n" +
                                   "Medí cada cambio con el benchmark: los parámetros de la consola cambian entre versiones del juego.",
            WhatItTouches = "Nada: es una guía.",
            SourceAttribution = "Valve: Counter-Strike 2 launch options / consola · chat5",
            Risk = RiskLevel.Low,
            InMinimal = false, InCompetitivo = false, InProfesional = false, InEntusiasta = false,
            EvaluateGate = _ => EvaluationResult.Neutral("Guía: lo configurás en Steam y en la consola de CS2."),
            ReadLiveState = () => false,
            ApplyAction = _ => (false, "Es una guía: configuralo en Steam (Propiedades de CS2 → Opciones de lanzamiento) y en la consola del juego.")
        });

        // ---------------------------------------------------------------- Laboratorio
        _allTweaks.Add(RegistryValuesItem("LabFthOff", "L01", "Fault Tolerant Heap apagado",
            TweakCategory.SystemCpu, LabSection,
            "El Fault Tolerant Heap aplica parches de memoria a programas que se cuelgan seguido (puede afectar a un juego después de varios crashes). " +
            "Apagado, Windows no los aplica." + LabNote,
            "Microsoft Learn: Fault Tolerant Heap (HKLM\\SOFTWARE\\Microsoft\\FTH Enabled) · " + Chat5,
            RiskLevel.Low, reboot: true,
            new[] { new LabValue(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\FTH", "Enabled", 0, RegistryValueKind.DWord) }));

        _allTweaks.Add(RegistryValuesItem("LabDxgKrnlTelemetryOff", "L02", "Telemetría de dxgkrnl apagada",
            TweakCategory.GpuDisplay, LabSection,
            "Valor que circula en guías para apagar la telemetría del kernel gráfico. Microsoft no lo documenta: puede no hacer nada." + LabNote,
            Chat5, RiskLevel.Low, reboot: true,
            new[] { new LabValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "DxgKrnlTelemetry", 0, RegistryValueKind.DWord) }));

        _allTweaks.Add(RegistryValuesItem("LabUlpsOff", "L03", "ULPS apagado (GPU AMD)",
            TweakCategory.GpuDisplay, LabSection,
            "EnableUlps = 0 evita que el driver de AMD apague la GPU en reposo. En placas NVIDIA no existe (las guías lo copian igual)." + LabNote,
            "AMD: Ultra Low Power State (EnableUlps) · " + Chat5, RiskLevel.Low, reboot: true,
            new[] { new LabValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000", "EnableUlps", 0, RegistryValueKind.DWord) },
            applicable: hw => hw.GpuVendor == GpuVendorKind.Amd
                ? ApplicabilityResult.Applicable()
                : ApplicabilityResult.NotApplicable("Solo para GPUs AMD: en NVIDIA el driver no lee este valor.")));

        _allTweaks.Add(RegistryValuesItem("LabNdisPnPCapabilities", "L04", "NDIS: sin ahorro de energía por defecto en placas nuevas",
            TweakCategory.NetworkDesync, LabSection,
            "DefaultPnPCapabilities = 0x24 cambia el valor de energía que NDIS asigna a las placas de red. KRUZH ya desmarca el ahorro en tu placa (ítem \"Placa de red\")." + LabNote,
            Chat5, RiskLevel.Low, reboot: true,
            new[] { new LabValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\NDIS\Parameters", "DefaultPnPCapabilities", 0x24, RegistryValueKind.DWord) }));

        _allTweaks.Add(RegistryValuesItem("LabTcpTaskOffloadOff", "L05", "Descarga de tareas TCP a la placa apagada",
            TweakCategory.NetworkDesync, LabSection,
            "Con DisableTaskOffload = 1 la placa de red deja de calcular checksums y segmentar: ese trabajo pasa a la CPU. " +
            "⚠ Si tu juego está limitado por la CPU (CS2 y VALORANT casi siempre), esto puede bajar FPS." + LabNote,
            "Microsoft Learn: DisableTaskOffload (Tcpip\\Parameters) · " + Chat5, RiskLevel.Medium, reboot: true,
            new[] { new LabValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "DisableTaskOffload", 1, RegistryValueKind.DWord) }));

        BuildTcpTimestampsLabItem();
        BuildNdisCaptureUnbindLabItem();
        BuildVolumeFiltersLabItem();

        // ---------------------------------------------------------------- Laboratorio que baja la seguridad
        _allTweaks.Add(RegistryValuesItem("LabSpectreMitigationsOff", "L08", "Mitigaciones de Spectre/Meltdown apagadas",
            TweakCategory.SystemCpu, LabSection,
            "⛔ Baja la seguridad: el CPU queda expuesto a Spectre v2, Meltdown y variantes (un programa malicioso podría leer memoria de otros). " +
            "En Ryzen 5000 la mitigación cuesta muy poco, así que la ganancia esperable es mínima." + LabNote,
            "Microsoft Learn: KB4073119 (FeatureSettingsOverride = 3, FeatureSettingsOverrideMask = 3) · " + Chat5,
            RiskLevel.Critical, reboot: true,
            new[]
            {
                new LabValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "FeatureSettingsOverride", 3, RegistryValueKind.DWord),
                new LabValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "FeatureSettingsOverrideMask", 3, RegistryValueKind.DWord),
            },
            reducesSecurity: true));

        const string defender = @"SOFTWARE\Policies\Microsoft\Windows Defender";
        _allTweaks.Add(RegistryValuesItem("LabDefenderLayersOff", "L09", "Defender: capas de inspección apagadas",
            TweakCategory.SystemCpu, LabSection,
            "⛔ Baja la seguridad: Defender sigue activo, pero deja de escanear archivos descargados y adjuntos (IOAV), scripts, " +
            "y deja de inspeccionar el tráfico de red buscando exploits (reconocimiento de protocolos). " +
            "Si \"Protección contra alteraciones\" está activada, Windows puede ignorar estas políticas." + LabNote,
            "Microsoft Learn: Microsoft Defender Antivirus policies (DisableIOAVProtection, DisableScriptScanning, NIS DisableProtocolRecognition) · " + Chat5,
            RiskLevel.Critical, reboot: false,
            new[]
            {
                new LabValue(RegistryHive.LocalMachine, $@"{defender}\Real-Time Protection", "DisableIOAVProtection", 1, RegistryValueKind.DWord),
                new LabValue(RegistryHive.LocalMachine, $@"{defender}\Real-Time Protection", "DisableScriptScanning", 1, RegistryValueKind.DWord),
                new LabValue(RegistryHive.LocalMachine, $@"{defender}\NIS", "DisableProtocolRecognition", 1, RegistryValueKind.DWord),
            },
            reducesSecurity: true));

        BuildSmbNetbiosLabItem();
    }

    // ---------------------------------------------------------------- L06: timestamps TCP

    private void BuildTcpTimestampsLabItem()
    {
        const string id = "LabTcpTimestampsOff", prop = "Timestamps";
        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = id,
            CodeNumber = "L06",
            Title = "Timestamps TCP apagados",
            Category = TweakCategory.NetworkDesync,
            CategoryDisplay = LabSection,
            TechnicalDescription = "Las marcas de tiempo de TCP (RFC 7323) agregan 12 bytes por paquete TCP. CS2 y VALORANT usan UDP para la partida, " +
                                   "así que esto solo afecta descargas y el cliente." + LabNote,
            WhatItTouches = @"WMI MSFT_NetTCPSetting (Internet, InternetCustom) -> Timestamps = 0 (Disabled)",
            SourceAttribution = "Microsoft Learn: Set-NetTCPSetting -Timestamps · " + Chat5,
            Risk = RiskLevel.Low,
            InMinimal = false, InCompetitivo = false, InProfesional = false, InEntusiasta = false,
            EvaluateGate = _ => EvaluationResult.Neutral("Laboratorio: medí antes y después."),
            ApplicableGate = _ => ClientTcpTemplates(prop).Count == 0
                ? ApplicabilityResult.NotApplicable("Este Windows no expone las plantillas TCP por WMI.")
                : ApplicabilityResult.Applicable(),
            ReadLiveState = () => ClientTcpTemplates(prop).All(t => NetTcpGlobalStore.Read(NetTcpGlobalStore.Target(NetTcpGlobalStore.TcpClass, t), prop) == 0),
            ApplyAction = _ =>
            {
                foreach (string t in ClientTcpTemplates(prop))
                {
                    _audit.SetNetTcpGlobal(id, NetTcpGlobalStore.Target(NetTcpGlobalStore.TcpClass, t), prop, 0);
                }
                return (true, "Timestamps TCP apagados.");
            }
        });
    }

    // ---------------------------------------------------------------- L07: captura de paquetes y LLDP desligados

    private void BuildNdisCaptureUnbindLabItem()
    {
        var physical = NetAdapterBindingStore.PhysicalAdapters();
        var adapters = physical.Where(a => a.Connected).ToList();
        if (adapters.Count == 0) adapters = physical.ToList();
        if (adapters.Count == 0) return;

        string[] components = { "ms_ndiscap", "ms_lldp" };
        List<(string Adapter, string Component)> Present() =>
            adapters.SelectMany(a => components.Select(c => (Adapter: a.Name, Component: c)))
                    .Where(p => NetAdapterBindingStore.IsEnabled(p.Adapter, p.Component) is not null)
                    .ToList();

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "LabNdisCaptureLldpUnbind",
            CodeNumber = "L07",
            Title = "Desligar captura de paquetes (ndiscap) y LLDP",
            Category = TweakCategory.NetworkDesync,
            CategoryDisplay = LabSection,
            TechnicalDescription = "ms_ndiscap es el filtro que usa \"netsh trace\" para capturar paquetes (sin captura activa no procesa nada) y ms_lldp " +
                                   "anuncia la PC con LLDP en la red local. Se desligan de la placa; revertir los vuelve a ligar." + LabNote,
            WhatItTouches = "WMI MSFT_NetAdapterBindingSettingData.Disable -> ms_ndiscap, ms_lldp en: " + string.Join(", ", adapters.Select(a => a.Name)),
            SourceAttribution = "Microsoft Learn: Disable-NetAdapterBinding · " + Chat5,
            Risk = RiskLevel.Low,
            InMinimal = false, InCompetitivo = false, InProfesional = false, InEntusiasta = false,
            EvaluateGate = _ => EvaluationResult.Neutral("Laboratorio: medí antes y después."),
            ApplicableGate = _ => Present().Count == 0
                ? ApplicabilityResult.NotApplicable("Tu placa no tiene estos componentes.")
                : ApplicabilityResult.Applicable(),
            ReadLiveState = () => Present().All(p => NetAdapterBindingStore.IsEnabled(p.Adapter, p.Component) == false),
            ApplyAction = _ =>
            {
                var targets = Present();
                foreach (var (adapter, component) in targets) _audit.SetAdapterBinding("LabNdisCaptureLldpUnbind", adapter, component, enabled: false);
                return (true, $"ndiscap y LLDP desligados de {targets.Select(t => t.Adapter).Distinct().Count()} placa(s).");
            }
        });
    }

    // ---------------------------------------------------------------- L10: filtros de volumen iorate / rdyboost

    private void BuildVolumeFiltersLabItem()
    {
        const string id = "LabVolumeFiltersOff";
        const string volumeClass = @"SYSTEM\CurrentControlSet\Control\Class\{71a27cdd-812a-11d0-bec7-08002be2092f}";
        string[] removable = { "iorate", "rdyboost" };

        string[] CurrentFilters()
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = hklm.OpenSubKey(volumeClass);
            return k?.GetValue("LowerFilters") as string[] ?? Array.Empty<string>();
        }

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = id,
            CodeNumber = "L10",
            Title = "Filtros de disco iorate y ReadyBoost quitados",
            Category = TweakCategory.StorageMemory,
            CategoryDisplay = LabSection,
            TechnicalDescription = "iorate (control de tasa de E/S) y rdyboost (ReadyBoost) son filtros que procesan cada lectura y escritura de disco. " +
                                   "Se sacan de la lista de filtros de volumen y sus servicios quedan deshabilitados; fvevol (BitLocker) y el resto quedan intactos. " +
                                   "⚠ Son controladores de arranque: si algo falla al revertir, Windows puede no iniciar. Creá un punto de restauración antes." + LabNote,
            WhatItTouches = $@"HKLM\{volumeClass} -> LowerFilters sin iorate/rdyboost · servicios iorate y rdyboost → Deshabilitado",
            SourceAttribution = "Microsoft Learn: Filter drivers (LowerFilters) · " + Chat5,
            Risk = RiskLevel.Critical,
            RequiresReboot = true,
            InMinimal = false, InCompetitivo = false, InProfesional = false, InEntusiasta = false,
            EvaluateGate = _ => EvaluationResult.Neutral("Laboratorio con riesgo de arranque: punto de restauración antes."),
            ApplicableGate = _ => CurrentFilters().Any(f => removable.Contains(f, StringComparer.OrdinalIgnoreCase))
                ? ApplicabilityResult.Applicable()
                : ApplicabilityResult.NotApplicable("Tu Windows no tiene estos filtros cargados."),
            ReadLiveState = () => !CurrentFilters().Any(f => removable.Contains(f, StringComparer.OrdinalIgnoreCase)),
            ApplyAction = _ =>
            {
                // Orden: primero el filtro fuera de la lista y recién después el servicio deshabilitado
                // (un filtro listado con su servicio deshabilitado impide arrancar).
                var kept = CurrentFilters().Where(f => !removable.Contains(f, StringComparer.OrdinalIgnoreCase)).ToArray();
                _audit.SetRegistryValue(id, RegistryHive.LocalMachine, volumeClass, "LowerFilters", kept, RegistryValueKind.MultiString);
                foreach (string svc in removable) _audit.SetServiceStartup(id, svc, 4, stopIfRunning: false);
                return (true, "Filtros quitados. Reiniciá.");
            }
        });
    }

    // ---------------------------------------------------------------- L11: SMB / NetBIOS

    private void BuildSmbNetbiosLabItem()
    {
        const string id = "LabSmbNetbiosOff";
        string[] services = { "LanmanServer", "srv2", "srvnet", "NetBT" };
        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = id,
            CodeNumber = "L11",
            Title = "Servidor SMB y NetBIOS deshabilitados",
            Category = TweakCategory.Services,
            CategoryDisplay = LabSection,
            TechnicalDescription = "⛔ Baja la seguridad de otra forma, pero también rompe funciones: deja de funcionar compartir carpetas e impresoras desde esta PC " +
                                   "y la resolución de nombres NetBIOS en la red local. Deshabilita el servicio Servidor y sus controladores (srv2, srvnet) y NetBT." + LabNote,
            WhatItTouches = "Servicios " + string.Join(", ", services) + " → Deshabilitado (inicio original guardado)",
            SourceAttribution = "Microsoft Learn: SMB server / NetBT services · " + Chat5,
            Risk = RiskLevel.High,
            SecurityImpact = SecurityImpact.ReducesSecurity,
            RequiresReboot = true,
            InMinimal = false, InCompetitivo = false, InProfesional = false, InEntusiasta = false,
            EvaluateGate = _ => EvaluationResult.Neutral("Laboratorio: solo si no compartís nada en la red local."),
            ReadLiveState = () => services.All(s => ReadRegDword(RegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{s}", "Start") is null or 4),
            ApplyAction = _ =>
            {
                var failed = new List<string>();
                foreach (string s in services)
                {
                    try { _audit.SetServiceStartup(id, s, 4, stopIfRunning: false); }
                    catch (KruzhLatency.Infra.Services.ServiceProtectedException) { failed.Add(s); }
                }
                return failed.Count == 0
                    ? (true, "SMB servidor y NetBIOS deshabilitados. Reiniciá.")
                    : (false, $"Windows protege {string.Join(", ", failed)}: el resto se deshabilitó.");
            }
        });
    }
}
