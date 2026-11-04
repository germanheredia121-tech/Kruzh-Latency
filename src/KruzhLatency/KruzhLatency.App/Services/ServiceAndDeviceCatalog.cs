using System;
using System.Collections.Generic;
using System.Linq;
using KruzhLatency.Core.Devices;
using KruzhLatency.Infra.Devices;
using KruzhLatency.Models;
using Microsoft.Win32;
using Profile = KruzhLatency.Core.Tweaks.Profile;

namespace KruzhLatency.Services;

/// <summary>
/// Pestañas "Servicios" y "Device Manager" de Optimizaciones. Cada servicio / dispositivo es un DeclarativeTweakItem
/// más: misma mecánica de pendientes, perfiles, aplicación en lote con overlay, journal y revert.
/// Dinámico: solo se crean ítems para lo que existe en esta PC, con condiciones de hardware (impresoras, Bluetooth, SSD…).
/// Límite anticheat (ADR-004): nada de lo que requieren FACEIT AC / Vanguard ni servicios críticos se toca nunca.
/// </summary>
public sealed partial class DeclarativeTweakEngine
{
    /// <summary>Servicios que ningún perfil toca (anticheat + críticos del sistema / red / audio / seguridad).</summary>
    public static readonly IReadOnlySet<string> ProtectedServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Anticheats
        "vgc", "vgk", "FACEIT", "FACEITService", "BEService", "EasyAntiCheat", "EasyAntiCheat_EOS",
        // TPM / seguridad que Vanguard y FACEIT pueden requerir
        "TBS", "WinDefend", "SecurityHealthService", "mpssvc", "BFE", "CryptSvc", "KeyIso", "VaultSvc",
        // Núcleo del sistema
        "RpcSs", "RpcEptMapper", "DcomLaunch", "Winmgmt", "EventLog", "PlugPlay", "Power", "ProfSvc", "Schedule",
        "SENS", "SystemEventsBroker", "BrokerInfrastructure", "CoreMessagingRegistrar", "StateRepository", "UserManager",
        "TrustedInstaller", "msiserver", "gpsvc", "LSM", "SamSs", "Themes",
        // Red y audio (necesarios para jugar)
        "Dhcp", "Dnscache", "NlaSvc", "nsi", "netprofm", "Netman", "WlanSvc", "LanmanWorkstation",
        "Audiosrv", "AudioEndpointBuilder", "MMCSS"
    };

    private enum MinProfile { Normal, Competitivo, Profesional, Entusiasta }

    private sealed record ServiceRule(
        string Name,
        string Title,
        string Section,
        string Why,
        MinProfile From,
        int TargetStart,                 // 4 = Deshabilitado, 3 = Manual
        Func<bool>? Condition = null,    // condición dinámica de hardware/uso
        string? ConditionReason = null,  // por qué NO aplica si la condición falla
        bool ReducesSecurity = false);

    private void BuildServiceAndDeviceCatalog()
    {
        foreach (var rule in ServiceRules())
        {
            if (ProtectedServices.Contains(rule.Name)) continue; // red de seguridad: nunca un protegido
            if (NeverTouchPolicy.ProtectedServices.Contains(rule.Name)) continue; // servicios de display (ADR-007)
            if (!ServiceExists(rule.Name)) continue;            // dinámico: solo lo que existe en esta PC
            _allTweaks.Add(BuildServiceItem(rule));
        }

        BuildDeviceItems();
    }

    // ---------------------------------------------------------------- servicios

    private IEnumerable<ServiceRule> ServiceRules()
    {
        const string telemetry = "Telemetría y diagnóstico";
        const string unused = "Funciones que no se usan para jugar";
        const string background = "Rendimiento en segundo plano";
        const string hardware = "Según tu hardware";
        const string xbox = "Xbox";
        const string updates = "Actualizaciones (solo Entusiasta)";

        yield return new("DiagTrack", "Telemetría de Windows", telemetry,
            "Envía datos de uso a Microsoft en segundo plano (uso de disco y red mientras jugás).", MinProfile.Normal, 4);
        yield return new("dmwappushservice", "Mensajes push de telemetría (WAP)", telemetry,
            "Complemento de la telemetría de Windows.", MinProfile.Normal, 4);
        yield return new("WerSvc", "Informe de errores de Windows", telemetry,
            "Recolecta y envía informes cuando algo falla; genera picos de disco al ocurrir.", MinProfile.Competitivo, 4);
        yield return new("PcaSvc", "Asistente de compatibilidad de programas", telemetry,
            "Monitorea los programas que abrís para sugerir modos de compatibilidad.", MinProfile.Competitivo, 4);
        yield return new("InventorySvc", "Inventario y evaluación de compatibilidad", telemetry,
            "Releva programas y hardware instalados para la evaluación de compatibilidad de Windows (alimenta la telemetría). Queda en manual.",
            MinProfile.Competitivo, 3);
        yield return new("DPS", "Directivas de diagnóstico", telemetry,
            "Detección automática de problemas. Sin él, el solucionador de problemas de Windows hace menos.", MinProfile.Profesional, 4);
        yield return new("WdiServiceHost", "Host del servicio de diagnóstico", telemetry,
            "Parte del sistema de diagnóstico de Windows.", MinProfile.Profesional, 4);
        yield return new("WdiSystemHost", "Host del sistema de diagnóstico", telemetry,
            "Parte del sistema de diagnóstico de Windows.", MinProfile.Profesional, 4);

        yield return new("MapsBroker", "Mapas descargados", unused,
            "Administra mapas sin conexión. No se usa en una PC de juego.", MinProfile.Normal, 4);
        yield return new("RetailDemo", "Modo demostración de tienda", unused,
            "Solo se usa en PCs de exhibición en comercios.", MinProfile.Normal, 4);
        yield return new("RemoteRegistry", "Registro remoto", unused,
            "Permite modificar el registro desde otra PC. Innecesario y es una superficie de ataque.", MinProfile.Normal, 4);
        yield return new("Fax", "Fax", unused, "Envío y recepción de fax.", MinProfile.Normal, 4);
        yield return new("lfsvc", "Geolocalización", unused,
            "Provee la ubicación a las apps. Ningún juego competitivo la usa.", MinProfile.Competitivo, 4);
        yield return new("WpcMonSvc", "Control parental", unused, "Filtros de Microsoft Family.", MinProfile.Competitivo, 4);
        yield return new("PhoneSvc", "Servicio telefónico", unused, "Llamadas desde la PC vinculada al teléfono.", MinProfile.Competitivo, 4);
        yield return new("wisvc", "Programa Windows Insider", unused, "Solo hace falta si usás builds Insider.", MinProfile.Competitivo, 4);
        yield return new("SEMgrSvc", "Pagos y NFC", unused, "Pagos con NFC. No aplica en una PC de escritorio.", MinProfile.Competitivo, 4);
        yield return new("SSDPSRV", "Detección SSDP", unused,
            "Busca dispositivos UPnP en la red local (televisores, DLNA). CS2 y VALORANT usan servidores dedicados y no lo necesitan.",
            MinProfile.Competitivo, 4);
        yield return new("upnphost", "Host de dispositivo UPnP", unused,
            "Publica esta PC como dispositivo UPnP en la red local. Depende de SSDP.", MinProfile.Competitivo, 4);
        yield return new("CDPSvc", "Plataforma de dispositivos conectados", unused,
            "Compartir con dispositivos cercanos, historial del portapapeles en la nube y vínculo con el teléfono. Queda en manual: arranca si lo usás.",
            MinProfile.Profesional, 3);
        yield return new("iphlpsvc", "Aplicación auxiliar IP", unused,
            "Túneles de transición a IPv6 (Teredo, 6to4, IP-HTTPS). El chat de grupo de Xbox usa Teredo; CS2 y VALORANT no.",
            MinProfile.Profesional, 4);
        yield return new("TrkWks", "Seguimiento de vínculos distribuidos", unused,
            "Mantiene accesos directos a archivos movidos entre PCs de una red.", MinProfile.Profesional, 4);
        yield return new("SharedAccess", "Conexión compartida a Internet", unused,
            "Compartir tu conexión con otros dispositivos.", MinProfile.Profesional, 4);

        var hw = _hwService.CurrentProfile;
        yield return new("SysMain", "SysMain (Superfetch)", background,
            "Precarga apps en RAM y lee el disco en segundo plano. Con SSD y 32 GB o más no aporta y genera actividad de disco. " +
            "Ojo: también maneja la compresión de memoria; con menos RAM, apagarlo hace que Windows vaya al disco en vez de comprimir (tirones).",
            MinProfile.Competitivo, 4,
            () => hw.Storage == StorageKind.NvmeOrSsd && hw.RamTotalGb >= 32,
            "Con menos de 32 GB de RAM (o disco mecánico) se mantiene: sin SysMain no hay compresión de memoria y el juego termina leyendo del disco.");
        yield return new("DoSvc", "Optimización de distribución", background,
            "Descarga actualizaciones en segundo plano y las comparte con otras PCs. Queda en manual: Windows Update lo arranca cuando lo necesita. " +
            "Para que no suba actualizaciones a otras PCs mientras jugás, ver \"Optimización de distribución sin P2P\" en Apps.",
            MinProfile.Competitivo, 3);
        yield return new("WSearch", "Indexación de Windows Search", background,
            "Indexa archivos en segundo plano (picos de disco y CPU). La búsqueda sigue funcionando, solo más lenta.",
            MinProfile.Profesional, 4);

        yield return new("Spooler", "Cola de impresión", hardware,
            "No tenés impresoras instaladas: el servicio no hace nada.", MinProfile.Competitivo, 4,
            () => !HasRealPrinters(), "Tenés impresoras instaladas: se mantiene.");
        yield return new("bthserv", "Soporte de Bluetooth", hardware,
            "No hay un adaptador Bluetooth presente.", MinProfile.Competitivo, 4,
            () => !HasPresentDevicesForService("BTHUSB"), "Hay un adaptador Bluetooth: se mantiene.");
        yield return new("WbioSrvc", "Biometría de Windows", hardware,
            "No hay lector de huellas ni cámara de Windows Hello.", MinProfile.Competitivo, 4,
            () => !HasBiometricDevices(), "Hay un dispositivo biométrico: se mantiene.");
        yield return new("SCardSvr", "Tarjetas inteligentes", hardware,
            "Lectores de tarjeta inteligente (uso corporativo).", MinProfile.Profesional, 4);
        yield return new("StiSvc", "Adquisición de imágenes (escáneres)", hardware,
            "Escáneres y algunas cámaras de fotos por USB. Las webcams no lo usan.", MinProfile.Profesional, 4);

        yield return new("XblAuthManager", "Xbox Live: autenticación", xbox,
            "Solo lo usan los juegos de Xbox / Game Pass para PC. CS2 y VALORANT no.", MinProfile.Profesional, 3);
        yield return new("XblGameSave", "Xbox Live: guardado de partidas", xbox,
            "Sincroniza partidas guardadas de juegos de Xbox / Game Pass.", MinProfile.Profesional, 3);
        yield return new("XboxNetApiSvc", "Xbox Live: red", xbox,
            "Red de juegos de Xbox / Game Pass.", MinProfile.Profesional, 3);

        yield return new("wuauserv", "Windows Update", updates,
            "Queda en manual: no descarga ni instala en segundo plano mientras jugás. Actualizá a mano seguido.",
            MinProfile.Entusiasta, 3, ReducesSecurity: true);
        yield return new("UsoSvc", "Orquestador de actualizaciones", updates,
            "Programa las búsquedas e instalaciones automáticas de Windows Update.", MinProfile.Entusiasta, 3, ReducesSecurity: true);
    }

    private DeclarativeTweakItem BuildServiceItem(ServiceRule rule)
    {
        string id = $"Svc:{rule.Name}";
        string regPath = $@"SYSTEM\CurrentControlSet\Services\{rule.Name}";
        string action = rule.TargetStart == 4 ? "Deshabilitado" : "Manual";
        return new DeclarativeTweakItem
        {
            Id = id,
            CodeNumber = "SV",
            Title = $"{rule.Title} ({rule.Name})",
            Category = TweakCategory.Services,
            CategoryDisplay = rule.Section,
            TechnicalDescription = rule.Why,
            WhatItTouches = $@"Servicio {rule.Name} → {action} (inicio original guardado para revertir)",
            SourceAttribution = "Catálogo KRUZH (servicios no necesarios para jugar)",
            Risk = rule.ReducesSecurity ? RiskLevel.Medium : RiskLevel.Low,
            SecurityImpact = rule.ReducesSecurity ? SecurityImpact.ReducesSecurity : SecurityImpact.None,
            RequiresReboot = false,
            InMinimal = rule.From == MinProfile.Normal,
            InCompetitivo = rule.From <= MinProfile.Competitivo,
            InProfesional = rule.From <= MinProfile.Profesional,
            InEntusiasta = true,
            ApplicableGate = _ =>
            {
                if (rule.Condition != null && !rule.Condition())
                {
                    return ApplicabilityResult.NotApplicable(rule.ConditionReason ?? "No aplica en esta PC.");
                }
                return ApplicabilityResult.Applicable("Servicio presente en esta PC.");
            },
            EvaluateGate = _ => EvaluationResult.Recommended($"Se pone en {action} desde el perfil {rule.From}."),
            ReadLiveState = () => ReadRegDword(RegistryHive.LocalMachine, regPath, "Start") == rule.TargetStart,
            ApplyAction = _ =>
            {
                try
                {
                    _audit.SetServiceStartup(id, rule.Name, rule.TargetStart, stopIfRunning: rule.TargetStart == 4);
                }
                catch (KruzhLatency.Infra.Services.ServiceProtectedException ex)
                {
                    return (false, ex.Message);
                }
                bool ok = ReadRegDword(RegistryHive.LocalMachine, regPath, "Start") == rule.TargetStart;
                return ok
                    ? (true, $"{rule.Title}: {action}.")
                    : (false, $"Windows no permitió cambiar {rule.Name} (servicio protegido).");
            }
        };
    }

    // ---------------------------------------------------------------- dispositivos (spec 002 / B1)

    /// <summary>
    /// Ítems del Device Manager: el motor de reglas de Core (DeviceRuleEngine) decide sobre el árbol PnP real
    /// y todo pasa por la lista de nunca-tocar (ADR-007). Solo aparecen dispositivos presentes en esta PC.
    /// </summary>
    private void BuildDeviceItems()
    {
        DeviceTree tree;
        DeviceRuleContext context;
        IReadOnlyList<DeviceRecommendation> recommendations;
        try
        {
            tree = new DeviceTree(new CfgMgrDeviceInventory().Snapshot());
            context = DeviceContextProbe.Detect(tree);
            recommendations = DeviceRuleEngine.Evaluate(tree, context);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Inventario de dispositivos falló: {ex.Message}");
            return; // sin inventario no se propone nada (nunca a ciegas)
        }

        foreach (var rec in recommendations)
        {
            _allTweaks.Add(BuildDeviceItem(rec));
        }

        BuildGhostItems(tree);                      // B2
        BuildIssueItems(tree, context.Safety);      // B3
        BuildInputPortAdvice(tree);                 // B6
        BuildInputSeparationAdvice(tree);           // E9
    }

    private DeclarativeTweakItem BuildDeviceItem(DeviceRecommendation rec)
    {
        string id = $"Dev:{rec.RuleId}";
        var instances = rec.Devices.Select(d => d.InstanceId).ToList();
        string description = rec.Warning is null ? rec.Why : $"{rec.Why}\n⚠ {rec.Warning}";
        return new DeclarativeTweakItem
        {
            Id = id,
            CodeNumber = "DV",
            Title = rec.Devices.Count > 1 ? $"{rec.Title} ({rec.Devices.Count})" : rec.Title,
            Category = TweakCategory.Devices,
            CategoryDisplay = rec.Section,
            TechnicalDescription = description,
            WhatItTouches = "Deshabilitar en el Administrador de dispositivos: " +
                            string.Join(", ", rec.Devices.Select(d => $"{d.DisplayName} ({d.InstanceId})")),
            SourceAttribution = "Catálogo KRUZH (spec 002) · reglas por topología/IDs, nunca toca bridges, GPU activa, USB del mouse/teclado, NIC con enlace ni audio predeterminado (ADR-007)",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = rec.From == Profile.Normal,
            InCompetitivo = rec.From <= Profile.Competitivo,
            InProfesional = rec.From <= Profile.Profesional,
            InEntusiasta = true,
            EvaluateGate = _ => rec.Warning is null
                ? EvaluationResult.Recommended($"Se deshabilita desde el perfil {rec.From}.")
                : EvaluationResult.Neutral(rec.Warning),
            ReadLiveState = () => instances.All(TransactionalAuditService.IsDeviceDisabled),
            ApplyAction = _ =>
            {
                foreach (var inst in instances)
                {
                    _audit.SetDeviceEnabled(id, inst, enable: false);
                }
                return (true, $"{rec.Title}: deshabilitado.");
            }
        };
    }

    // ---------------------------------------------------------------- detección

    private static bool ServiceExists(string name)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var k = hklm.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
        return k?.GetValue("Start") is int && k.GetValue("ImagePath") != null;
    }

    /// <summary>Impresoras reales (excluye las virtuales que trae Windows / Office).</summary>
    private static bool HasRealPrinters()
    {
        string[] virtualPrinters = { "Microsoft Print to PDF", "Microsoft XPS Document Writer", "Fax", "OneNote", "Send To OneNote" };
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var printers = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Print\Printers");
        return printers?.GetSubKeyNames().Any(p => !virtualPrinters.Any(v => p.StartsWith(v, StringComparison.OrdinalIgnoreCase))) ?? false;
    }

    /// <summary>Services\&lt;driver&gt;\Enum\Count = cantidad de dispositivos PRESENTES que usan ese driver.</summary>
    private static bool HasPresentDevicesForService(string driverService) =>
        ReadRegDword(RegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{driverService}\Enum", "Count") > 0;

    private static bool HasBiometricDevices()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var cls = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{53D29EF7-377C-4D14-864B-EB3A85769359}");
        return cls?.GetSubKeyNames().Any(s => s.Length == 4 && char.IsDigit(s[0])) ?? false;
    }
}
