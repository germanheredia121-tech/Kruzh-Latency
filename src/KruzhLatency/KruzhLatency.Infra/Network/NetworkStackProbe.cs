using System.Management;
using KruzhLatency.Core.Network;
using KruzhLatency.Infra.Devices;
using Microsoft.Win32;

namespace KruzhLatency.Infra.Network;

/// <summary>
/// Solo lectura: componentes ligados a cada placa (WMI root\StandardCimv2 MSFT_NetAdapterBindingSettingData, el mismo
/// dato que Get-NetAdapterBinding), programas instalados (claves Uninstall) y archivo .sys del driver de cada placa física.
/// </summary>
public static class NetworkStackProbe
{
    public static NetworkStackFacts Detect() => new(Bindings(), InstalledApps(), Drivers());

    private static IReadOnlyList<NetBinding> Bindings()
    {
        var list = new List<NetBinding>();
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\StandardCimv2"),
                new ObjectQuery("SELECT Name, ComponentID, DisplayName, Enabled FROM MSFT_NetAdapterBindingSettingData"));
            foreach (ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    list.Add(new NetBinding(o["Name"]?.ToString() ?? "", o["ComponentID"]?.ToString() ?? "",
                        o["DisplayName"]?.ToString() ?? "", o["Enabled"] is true));
                }
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        return list;
    }

    private static IReadOnlyList<string> InstalledApps()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Registry64) })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var root = baseKey.OpenSubKey(uninstall);
                if (root is null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub);
                    if (k?.GetValue("DisplayName") is string name && !string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
                }
            }
            catch (System.Security.SecurityException) { }
        }
        return names.ToList();
    }

    private static IReadOnlyList<NicDriverFact> Drivers()
    {
        var list = new List<NicDriverFact>();
        foreach (var nic in DeviceContextProbe.NetworkAdapters().Where(a =>
                     a.InstanceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) || a.InstanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)))
        {
            using var cls = Registry.LocalMachine.OpenSubKey(nic.ClassKeyPath);
            using var en = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{nic.InstanceId}");
            string? service = en?.GetValue("Service") as string;
            string? file = null;
            if (service is not null)
            {
                using var svc = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}");
                if (svc?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string image)
                {
                    file = Path.GetFileName(image.Trim('"'));
                }
            }
            list.Add(new NicDriverFact(nic.InstanceId, cls?.GetValue("DriverDesc") as string ?? nic.InstanceId, file));
        }
        return list;
    }
}
