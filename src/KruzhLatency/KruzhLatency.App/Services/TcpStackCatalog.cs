using System;
using System.Collections.Generic;
using System.Linq;
using KruzhLatency.Infra.Network;
using KruzhLatency.Models;
using Microsoft.Win32;

namespace KruzhLatency.Services;

/// <summary>
/// Fase E de red: auto-tuning de TCP por WMI (E3), Ndu.sys (E5) y protocolos ligados a la placa con enlace (E6).
/// Todo se lee y escribe por WMI o registro (nunca texto de netsh) y queda en el journal.
/// </summary>
public sealed partial class DeclarativeTweakEngine
{
    private const string NetSection = "Red & Desync";

    /// <summary>Plantillas TCP que usa un Windows de escritorio (Internet + InternetCustom) y que tienen el ajuste.</summary>
    private static IReadOnlyList<string> ClientTcpTemplates(string property) =>
        NetTcpGlobalStore.TcpTemplates(property)
            .Where(n => n.StartsWith("Internet", StringComparison.OrdinalIgnoreCase))
            .ToList();

    private void BuildTcpStackItems()
    {
        BuildAutoTuningItem();
        BuildNduItem();
        BuildBindingItems();
    }

    // ---------------------------------------------------------------- E3: auto-tuning de la ventana de recepción

    private void BuildAutoTuningItem()
    {
        const string id = "TcpAutoTuningLevel";
        const string prop = "AutoTuningLevelLocal";

        uint?[] Levels() => ClientTcpTemplates(prop)
            .Select(t => NetTcpGlobalStore.Read(NetTcpGlobalStore.Target(NetTcpGlobalStore.TcpClass, t), prop))
            .ToArray();

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = id,
            CodeNumber = "N10",
            Title = "Auto-Tuning de la ventana TCP",
            Category = TweakCategory.NetworkDesync,
            CategoryDisplay = NetSection,
            TechnicalDescription = "Deja el auto-tuning de la ventana de recepción en Normal, el valor de fábrica de Windows. " +
                                   "Otros optimizadores lo dejan en Disabled (descargas lentas) o Experimental (ventanas enormes que suman espera en cola); esto lo vuelve a Normal. " +
                                   "Restricted es una alternativa para conexiones de fibra directa sin pérdidas.",
            WhatItTouches = @"WMI root\StandardCimv2 MSFT_NetTCPSetting (Internet, InternetCustom) -> AutoTuningLevelLocal",
            SourceAttribution = "Microsoft Learn: Set-NetTCPSetting -AutoTuningLevelLocal · TCP receive window auto-tuning",
            Risk = RiskLevel.Low,
            InCompetitivo = true,
            InProfesional = true,
            SelectedOptionKey = "Normal",
            Options = new List<TweakOptionChoice>
            {
                new() { Key = "Normal", ShortLabel = "Normal", DisplayName = "Normal (default de Windows) [RECOMENDADO]", Description = "La ventana crece según el ancho de banda real." },
                new() { Key = "Restricted", ShortLabel = "Restricted", DisplayName = "Restricted (fibra directa)", Description = "Ventana más chica: menos datos en vuelo, descargas grandes algo más lentas." }
            },
            ApplicableGate = _ => ClientTcpTemplates(prop).Count == 0
                ? ApplicabilityResult.NotApplicable("Este Windows no expone las plantillas TCP por WMI.")
                : ApplicabilityResult.Applicable(),
            ReadLiveState = () =>
            {
                var levels = Levels();
                return levels.Length > 0 && levels.All(l => l is NetTcpGlobalStore.AutoTuningNormal or NetTcpGlobalStore.AutoTuningRestricted);
            },
            ReadLiveOptionKey = () =>
            {
                var levels = Levels();
                return levels.Length > 0 && levels.All(l => l == NetTcpGlobalStore.AutoTuningRestricted) ? "Restricted" : "Normal";
            },
            ApplyAction = opt =>
            {
                uint level = opt == "Restricted" ? NetTcpGlobalStore.AutoTuningRestricted : NetTcpGlobalStore.AutoTuningNormal;
                foreach (string t in ClientTcpTemplates(prop))
                {
                    _audit.SetNetTcpGlobal(id, NetTcpGlobalStore.Target(NetTcpGlobalStore.TcpClass, t), prop, level);
                }
                return (true, $"Auto-Tuning en {(level == NetTcpGlobalStore.AutoTuningRestricted ? "Restricted" : "Normal")} (verificado por WMI).");
            }
        });
    }

    // ---------------------------------------------------------------- E5: Ndu.sys

    private void BuildNduItem()
    {
        const string id = "NduDriverOff";
        const string key = @"SYSTEM\CurrentControlSet\Services\Ndu";

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = id,
            CodeNumber = "N11",
            Title = "Ndu.sys (monitor de uso de datos) apagado",
            Category = TweakCategory.NetworkDesync,
            CategoryDisplay = NetSection,
            TechnicalDescription = "Ndu.sys (Windows Network Data Usage) es un filtro que cuenta cada paquete por aplicación dentro de la pila de red; " +
                                   "aparece seguido en LatencyMon con picos de DPC. Se deja sin arrancar (Start = 4).\n" +
                                   "⚠ Deja de funcionar \"Uso de datos\" en Configuración → Red. Requiere reiniciar.",
            WhatItTouches = $@"HKLM\{key} -> Start = 4 (deshabilitado)",
            SourceAttribution = "Microsoft Learn: Data usage (Settings) · reportes de LatencyMon (ndu.sys) · BoringBoredom PC-Tuning",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InProfesional = true,
            ApplicableGate = _ => ReadRegDword(RegistryHive.LocalMachine, key, "Start") is null
                ? ApplicabilityResult.NotApplicable("Ndu no está instalado en este Windows.")
                : ApplicabilityResult.Applicable(),
            ReadLiveState = () => ReadRegDword(RegistryHive.LocalMachine, key, "Start") == 4,
            ApplyAction = _ =>
            {
                _audit.SetRegistryValue(id, RegistryHive.LocalMachine, key, "Start", 4, RegistryValueKind.DWord);
                return (true, "Ndu deshabilitado. Reiniciá para que deje de cargarse.");
            }
        });
    }

    // ---------------------------------------------------------------- E6: protocolos ligados a la placa con enlace

    private void BuildBindingItems()
    {
        var physical = NetAdapterBindingStore.PhysicalAdapters();
        var adapters = physical.Where(a => a.Connected).ToList();
        if (adapters.Count == 0) adapters = physical.ToList();
        if (adapters.Count == 0) return;

        string names = string.Join(", ", adapters.Select(a => $"{a.Name} ({a.Description})"));

        // (placa, protocolo) que existen hoy: un protocolo que la placa no tiene no se toca.
        List<(string Adapter, string Component)> Present(params string[] components) =>
            adapters.SelectMany(a => components.Select(c => (Adapter: a.Name, Component: c)))
                    .Where(p => NetAdapterBindingStore.IsEnabled(p.Adapter, p.Component) is not null)
                    .ToList();

        bool AllUnbound(string[] components) =>
            Present(components).All(p => NetAdapterBindingStore.IsEnabled(p.Adapter, p.Component) == false);

        (bool, string) Unbind(string tweakId, string[] components, string label)
        {
            var targets = Present(components);
            foreach (var (adapter, component) in targets)
            {
                _audit.SetAdapterBinding(tweakId, adapter, component, enabled: false);
            }
            return (true, $"{label}: desligado de {targets.Select(t => t.Adapter).Distinct().Count()} placa(s).");
        }

        ApplicabilityResult Has(string[] components) => Present(components).Count == 0
            ? ApplicabilityResult.NotApplicable("Tu placa no tiene este protocolo instalado.")
            : ApplicabilityResult.Applicable();

        string[] lltd = { "ms_lltdio", "ms_rspndr" };
        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "BindLltdOff",
            CodeNumber = "N12",
            Title = "Desligar LLTD (mapa de red) de la placa",
            Category = TweakCategory.NetworkDesync,
            CategoryDisplay = NetSection,
            TechnicalDescription = "El Link-Layer Topology Discovery (asignador de E/S y respondedor) arma el \"mapa de red\" mandando y contestando sondeos en segundo plano. " +
                                   "En una PC de juego no se usa. Se desmarca la casilla en las propiedades de la conexión; revertir la vuelve a marcar.",
            WhatItTouches = $"WMI MSFT_NetAdapterBindingSettingData.Disable -> ms_lltdio, ms_rspndr en: {names}",
            SourceAttribution = "Microsoft Learn: Disable-NetAdapterBinding · Link Layer Topology Discovery",
            Risk = RiskLevel.Low,
            InCompetitivo = true,
            InProfesional = true,
            ApplicableGate = _ => Has(lltd),
            ReadLiveState = () => AllUnbound(lltd),
            ApplyAction = _ => Unbind("BindLltdOff", lltd, "LLTD")
        });

        string[] sharing = { "ms_server", "ms_msclient" };
        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "BindFileSharingOff",
            CodeNumber = "N13",
            Title = "Desligar Compartir archivos e impresoras de la placa",
            Category = TweakCategory.NetworkDesync,
            CategoryDisplay = NetSection,
            TechnicalDescription = "Desliga \"Cliente para redes Microsoft\" y \"Compartir archivos e impresoras\" (SMB) de la placa: dejan de anunciar y escuchar en la red local.\n" +
                                   "⚠ Desde esta PC no vas a ver carpetas compartidas ni impresoras de red, y nadie va a poder entrar a las tuyas. Revertir lo vuelve a ligar.",
            WhatItTouches = $"WMI MSFT_NetAdapterBindingSettingData.Disable -> ms_server, ms_msclient en: {names}",
            SourceAttribution = "Microsoft Learn: Disable-NetAdapterBinding · File and Printer Sharing for Microsoft Networks",
            Risk = RiskLevel.Medium,
            InProfesional = true,
            EvaluateGate = _ => EvaluationResult.Neutral("Solo si no usás carpetas compartidas ni impresoras de red."),
            ApplicableGate = _ => Has(sharing),
            ReadLiveState = () => AllUnbound(sharing),
            ApplyAction = _ => Unbind("BindFileSharingOff", sharing, "Compartir archivos e impresoras")
        });

        string[] ipv6 = { "ms_tcpip6" };
        const string tcpip6Key = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters";
        const int preferIpv4 = 0x20;
        bool PrefersIpv4() => ((ReadRegDword(RegistryHive.LocalMachine, tcpip6Key, "DisabledComponents") ?? 0) & preferIpv4) != 0;

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "Ipv6PreferOrUnbind",
            CodeNumber = "N14",
            Title = "IPv6: preferir IPv4 o desligarlo",
            Category = TweakCategory.NetworkDesync,
            CategoryDisplay = NetSection,
            TechnicalDescription = "Preferir IPv4 (recomendado) deja IPv6 activo pero hace que Windows use IPv4 primero (DisabledComponents con el bit 0x20). " +
                                   "Desligar quita IPv6 de la placa.\n⚠ Microsoft desaconseja desligar IPv6: algunas funciones de Windows que dependen de IPv6 (DirectAccess, Teredo para el chat de grupo de Xbox) dejan de andar." +
                                   "Preferir IPv4 requiere reiniciar.",
            WhatItTouches = $@"Preferir IPv4: HKLM\{tcpip6Key} -> DisabledComponents |= 0x20 · Desligar: WMI MSFT_NetAdapterBindingSettingData.Disable -> ms_tcpip6 en: {names}",
            SourceAttribution = "Microsoft Learn: Guidance for configuring IPv6 in Windows for advanced users (DisabledComponents)",
            Risk = RiskLevel.Medium,
            RequiresReboot = true,
            EvaluateGate = _ => EvaluationResult.Neutral("Solo perfil Entusiasta: el beneficio es chico y Microsoft no recomienda desligar IPv6."),
            SelectedOptionKey = "PreferIPv4",
            Options = new List<TweakOptionChoice>
            {
                new() { Key = "PreferIPv4", ShortLabel = "Preferir IPv4", DisplayName = "Preferir IPv4 sobre IPv6 [RECOMENDADO]", Description = "IPv6 sigue disponible; las conexiones salen por IPv4." },
                new() { Key = "Unbind", ShortLabel = "Desligar", DisplayName = "Desligar IPv6 de la placa", Description = "La placa queda solo con IPv4." }
            },
            ApplicableGate = _ => Has(ipv6),
            ReadLiveState = () => PrefersIpv4() || AllUnbound(ipv6),
            ReadLiveOptionKey = () => AllUnbound(ipv6) ? "Unbind" : "PreferIPv4",
            ApplyAction = opt =>
            {
                if (opt == "Unbind") return Unbind("Ipv6PreferOrUnbind", ipv6, "IPv6");

                // No se vuelve a ligar IPv6: si estaba desligado (por KRUZH o de antes), se respeta; revertir lo restaura.
                int current = ReadRegDword(RegistryHive.LocalMachine, tcpip6Key, "DisabledComponents") ?? 0;
                _audit.SetRegistryValue("Ipv6PreferOrUnbind", RegistryHive.LocalMachine, tcpip6Key, "DisabledComponents", current | preferIpv4, RegistryValueKind.DWord);
                return (true, "Windows va a preferir IPv4. Reiniciá para que lo tome.");
            }
        });
    }
}
