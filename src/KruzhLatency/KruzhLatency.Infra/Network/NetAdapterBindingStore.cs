using System.Management;

namespace KruzhLatency.Infra.Network;

/// <summary>Placa física con enlace (MSFT_NetAdapter): Name es la clave de sus bindings.</summary>
public sealed record PhysicalAdapter(string Name, string Description, bool Connected);

/// <summary>
/// E6: protocolos ligados a cada placa (lo que muestran las casillas de Propiedades de la conexión),
/// por WMI root\StandardCimv2 MSFT_NetAdapterBindingSettingData con sus métodos Enable/Disable. Sin netsh ni texto localizado.
/// </summary>
public static class NetAdapterBindingStore
{
    private static readonly ManagementScope Scope = new(@"\\.\root\StandardCimv2");

    /// <summary>Placas físicas (no virtuales). MediaConnectState 1 = conectada.</summary>
    public static IReadOnlyList<PhysicalAdapter> PhysicalAdapters()
    {
        var list = new List<PhysicalAdapter>();
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope,
                new ObjectQuery("SELECT Name, InterfaceDescription, MediaConnectState, HardwareInterface, Virtual FROM MSFT_NetAdapter"));
            foreach (ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    if (o["HardwareInterface"] is not true || o["Virtual"] is true || o["Name"] is not string name) continue;
                    bool connected = o["MediaConnectState"] is { } s && Convert.ToUInt32(s, System.Globalization.CultureInfo.InvariantCulture) == 1;
                    list.Add(new PhysicalAdapter(name, o["InterfaceDescription"] as string ?? name, connected));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // sin la clase (Windows recortado) no hay placas que ofrecer
        }
        return list;
    }

    /// <summary>null = la placa no tiene ese protocolo instalado.</summary>
    public static bool? IsEnabled(string adapterName, string componentId)
    {
        try
        {
            using var obj = Find(adapterName, componentId);
            return obj?["Enabled"] as bool?;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public static void SetEnabled(string adapterName, string componentId, bool enabled)
    {
        using (var obj = Find(adapterName, componentId)
            ?? throw new InvalidOperationException($"{adapterName} no tiene el protocolo {componentId}."))
        {
            obj.InvokeMethod(enabled ? "Enable" : "Disable", null);
        }
        if (IsEnabled(adapterName, componentId) != enabled)
        {
            throw new InvalidOperationException($"{adapterName}: Windows no {(enabled ? "ligó" : "desligó")} {componentId}.");
        }
    }

    private static ManagementObject? Find(string adapterName, string componentId)
    {
        static string Esc(string s) => s.Replace(@"\", @"\\").Replace("'", @"\'");
        using var searcher = new ManagementObjectSearcher(Scope, new ObjectQuery(
            $"SELECT * FROM MSFT_NetAdapterBindingSettingData WHERE Name = '{Esc(adapterName)}' AND ComponentID = '{Esc(componentId)}'"));
        return searcher.Get().Cast<ManagementObject>().FirstOrDefault();
    }
}
