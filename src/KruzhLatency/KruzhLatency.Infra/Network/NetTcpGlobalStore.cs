using System.Management;

namespace KruzhLatency.Infra.Network;

/// <summary>
/// Ajustes globales de TCP/IP por WMI (root\StandardCimv2), en reemplazo de "netsh int tcp set global":
/// se leen números, nunca texto localizado.
/// - MSFT_NetOffloadGlobalSetting (singleton): ReceiveSegmentCoalescing, ReceiveSideScaling (0 = Disabled, 1 = Enabled).
/// - MSFT_NetTCPSetting (una instancia por plantilla: Internet, InternetCustom, …): AutoTuningLevelLocal
///   (0 Disabled, 1 HighlyRestricted, 2 Restricted, 3 Normal, 4 Experimental) y EcnCapability (0 Disabled, 1 Enabled).
/// Un ajuste se identifica como "MSFT_NetOffloadGlobalSetting" o "MSFT_NetTCPSetting:&lt;SettingName&gt;" + propiedad.
/// </summary>
public static class NetTcpGlobalStore
{
    public const string OffloadClass = "MSFT_NetOffloadGlobalSetting";
    public const string TcpClass = "MSFT_NetTCPSetting";

    public const uint AutoTuningDisabled = 0, AutoTuningRestricted = 2, AutoTuningNormal = 3;

    private static readonly ManagementScope Scope = new(@"\\.\root\StandardCimv2");

    /// <summary>Destino para el journal: la clase, o clase:plantilla para MSFT_NetTCPSetting.</summary>
    public static string Target(string className, string? settingName = null) =>
        settingName is null ? className : $"{className}:{settingName}";

    /// <summary>Plantillas TCP que tienen el ajuste definido (las que Windows aplica a conexiones).</summary>
    public static IReadOnlyList<string> TcpTemplates(string property)
    {
        var names = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, new ObjectQuery($"SELECT SettingName, {property} FROM {TcpClass}"));
            foreach (ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    if (o["SettingName"] is string n && o[property] is not null) names.Add(n);
                }
            }
        }
        catch (ManagementException)
        {
            // clase no disponible: sin plantillas
        }
        return names;
    }

    /// <summary>null = la clase/plantilla no existe o no tiene ese valor.</summary>
    public static uint? Read(string target, string property)
    {
        try
        {
            using var obj = Find(target);
            return obj?[property] is { } v ? Convert.ToUInt32(v, System.Globalization.CultureInfo.InvariantCulture) : null;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>Escribe y verifica leyendo de nuevo; lanza si Windows no tomó el valor.</summary>
    public static void Write(string target, string property, uint value)
    {
        using (var obj = Find(target) ?? throw new InvalidOperationException($"{target} no existe en este Windows."))
        {
            obj[property] = value;
            obj.Put();
        }
        if (Read(target, property) != value)
        {
            throw new InvalidOperationException($"{target}.{property}: Windows no aceptó el valor {value}.");
        }
    }

    private static ManagementObject? Find(string target)
    {
        int colon = target.IndexOf(':');
        string className = colon < 0 ? target : target[..colon];
        if (className is not (OffloadClass or TcpClass)) throw new ArgumentException($"Clase no soportada: {className}", nameof(target));

        string query = colon < 0
            ? $"SELECT * FROM {className}"
            : $"SELECT * FROM {className} WHERE SettingName = '{target[(colon + 1)..].Replace("'", "")}'";
        using var searcher = new ManagementObjectSearcher(Scope, new ObjectQuery(query));
        return searcher.Get().Cast<ManagementObject>().FirstOrDefault();
    }
}
