using System.Text.RegularExpressions;

namespace KruzhLatency.Core.Network;

/// <summary>Un componente ligado a una placa de red (protocolo o filtro NDIS), como lo devuelve MSFT_NetAdapterBindingSettingData.</summary>
public sealed record NetBinding(string AdapterName, string ComponentId, string DisplayName, bool Enabled);

/// <summary>Driver de una placa de red física: archivo .sys del servicio.</summary>
public sealed record NicDriverFact(string InstanceId, string Description, string? DriverFile);

public sealed record NetworkStackFacts(
    IReadOnlyList<NetBinding> Bindings,
    /// <summary>Nombres de programas instalados (DisplayName de Uninstall).</summary>
    IReadOnlyList<string> InstalledApps,
    IReadOnlyList<NicDriverFact> Drivers);

public enum NetworkAdviceKind { UninstallSoftware, ThirdPartyFilter, SwitchDriver }

public sealed record NetworkAdvice(string Id, NetworkAdviceKind Kind, string Title, string Detail, string Link);

/// <summary>
/// Spec 002 / B5: lo que se mete en el camino de cada paquete. Filtros NDIS de terceros (cFosSpeed, Killer, Npcap…),
/// "optimizadores" de red instalados y drivers Realtek NetAdapterCx en lugar del NDIS clásico.
/// Solo recomienda: desinstalar software o cambiar de driver lo hace el usuario.
/// </summary>
public static class NetworkStackAdvisor
{
    public const string AppsSettings = "ms-settings:appsfeatures";
    public const string RealtekDownloads = "https://www.realtek.com/Download/List?cate_id=584";

    private sealed record Known(string Pattern, string Title, string Detail);

    private static readonly Known[] KnownFilters =
    {
        new("cfos", "cFosSpeed (filtro de tráfico)",
            "Reordena la cola de paquetes con su propio driver en el camino de cada paquete. Lo instalan MSI LAN Manager, ASUS GameFirst y otros. En una conexión sin saturar agrega latencia y DPC: desinstalalo."),
        new("killer|kfe|knd", "Killer Prioritization Engine",
            "Clasifica y prioriza tráfico con un filtro en el driver. Con la red sin saturar no ayuda y suma DPC: desinstalá Killer Control Center / Intelligence Center (dejá solo el driver)."),
        new("npcap|npf|winpcap", "Npcap / WinPcap",
            "Captura de paquetes (Wireshark). Si no lo usás, desinstalalo: es un filtro más en cada paquete."),
        new("vbox|vmware|oracle", "Puente de red de máquinas virtuales",
            "VirtualBox/VMware agregan un filtro de puente a la placa. Si no usás máquinas virtuales, desinstalalas o quitá el filtro."),
        new("rivet|smartbyte", "Rivet / SmartByte",
            "Software de priorización de tráfico (Dell/Alienware/Killer). Desinstalalo."),
    };

    private static readonly (string Pattern, string Title, string Detail)[] KnownApps =
    {
        ("cfosspeed", "cFosSpeed", "Filtro de tráfico en el camino de cada paquete."),
        ("msi lan manager", "MSI LAN Manager", "Es cFosSpeed con otra marca: instala un filtro NDIS."),
        ("dragon center", "MSI Dragon Center", "Instala servicios en segundo plano y, en muchas placas, MSI LAN Manager (cFosSpeed)."),
        ("msi center", "MSI Center", "Instala servicios en segundo plano y módulos opcionales de red (cFosSpeed)."),
        ("killer control center", "Killer Control Center", "Instala el motor de priorización de Killer."),
        ("killer intelligence center", "Killer Intelligence Center", "Instala el motor de priorización de Killer."),
        ("gamefirst", "ASUS GameFirst", "Basado en cFosSpeed: filtro de tráfico."),
        ("smartbyte", "SmartByte", "Priorización de tráfico (Rivet Networks)."),
    };

    /// <summary>Drivers Realtek NetAdapterCx (Windows 11): rtcx21x64.sys, rt25cx21x64.sys…</summary>
    private static readonly Regex RealtekCx = new(@"^rt\w*cx\w*\.sys$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<NetworkAdvice> Evaluate(NetworkStackFacts facts)
    {
        var advice = new List<NetworkAdvice>();

        foreach (var group in facts.Bindings.Where(b => b.Enabled && IsThirdParty(b.ComponentId))
                                            .GroupBy(b => b.ComponentId, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var known = KnownFilters.FirstOrDefault(k => Regex.IsMatch(first.ComponentId, k.Pattern, RegexOptions.IgnoreCase));
            string adapters = string.Join(", ", group.Select(b => b.AdapterName).Distinct());
            advice.Add(new NetworkAdvice($"NetFilter:{first.ComponentId}", NetworkAdviceKind.ThirdPartyFilter,
                known?.Title ?? $"Componente de red de terceros: {first.DisplayName}",
                (known?.Detail ?? "Un programa agregó este componente a tu placa de red; procesa cada paquete. Si no sabés para qué está, desinstalá el programa que lo instaló.") +
                $" (ligado a: {adapters}; ID {first.ComponentId})",
                AppsSettings));
        }

        foreach (var (pattern, title, detail) in KnownApps)
        {
            var app = facts.InstalledApps.FirstOrDefault(a => a.Contains(pattern, StringComparison.OrdinalIgnoreCase));
            if (app is null) continue;
            advice.Add(new NetworkAdvice($"NetApp:{title}", NetworkAdviceKind.UninstallSoftware, $"Programa instalado: {app}",
                $"{detail} Desinstalalo desde Aplicaciones instaladas.", AppsSettings));
        }

        foreach (var d in facts.Drivers.Where(d => d.DriverFile is not null && RealtekCx.IsMatch(d.DriverFile)))
        {
            advice.Add(new NetworkAdvice($"NetDriver:{d.InstanceId}", NetworkAdviceKind.SwitchDriver,
                $"{d.Description}: driver NetAdapterCx ({d.DriverFile})",
                "Windows instaló el driver Realtek NetAdapterCx. El driver NDIS clásico de Realtek (rt640x64.sys) expone todas las opciones avanzadas y en las mediciones de DPC rinde mejor. " +
                "Descargá \"Win10/Win11 Auto Installation Program (NDIS)\" de la página de Realtek e instalalo.",
                RealtekDownloads));
        }

        return advice;
    }

    /// <summary>Microsoft usa ms_* (y vms_* para Hyper-V); todo lo demás lo agregó un programa.</summary>
    public static bool IsThirdParty(string componentId) =>
        !componentId.StartsWith("ms_", StringComparison.OrdinalIgnoreCase) &&
        !componentId.StartsWith("vms_", StringComparison.OrdinalIgnoreCase);
}
