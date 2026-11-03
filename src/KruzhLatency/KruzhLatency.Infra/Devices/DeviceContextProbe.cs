using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using KruzhLatency.Core.Devices;
using Microsoft.Win32;

namespace KruzhLatency.Infra.Devices;

/// <summary>Una placa de red: devnode, clave de clase (donde viven sus opciones avanzadas) y estado del enlace.</summary>
public sealed record NetworkAdapterFact(string InstanceId, string ClassKeyPath, bool IsWireless, bool HasLink);

/// <summary>
/// Hechos del sistema que no salen del árbol PnP y que necesitan ADR-007 y las reglas de spec 002.
/// Solo lectura: EnumDisplayDevices, NetworkInterface + clave de clase Net, MMDevice (salida de audio por defecto).
/// </summary>
public static class DeviceContextProbe
{
    private const string NetClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    public static DeviceRuleContext Detect(DeviceTree tree)
    {
        var nics = NetworkAdapters();
        var safety = new DeviceSafetyContext(
            ActiveGpuInstanceId: SafeRun(() => ActiveGpu(tree)),
            LinkedNicInstanceIds: nics.Where(n => n.HasLink).Select(n => n.InstanceId).ToList(),
            DefaultAudioEndpointInstanceId: SafeRun(DefaultAudioEndpoint));
        return new DeviceRuleContext(
            safety,
            WirelessNicInstanceIds: nics.Where(n => n.IsWireless).Select(n => n.InstanceId).ToList(),
            HasRealPrinters: SafeRun<bool?>(() => HasRealPrinters()) ?? true); // ante la duda, no se tocan las colas
    }

    // ------------------------------------------------------------ red

    public static IReadOnlyList<NetworkAdapterFact> NetworkAdapters()
    {
        var interfaces = new Dictionary<string, NetworkInterface>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()) interfaces[ni.Id] = ni;
        }
        catch (NetworkInformationException) { }

        var result = new List<NetworkAdapterFact>();
        using var cls = Registry.LocalMachine.OpenSubKey(NetClassKey);
        if (cls is null) return result;
        foreach (var sub in cls.GetSubKeyNames().Where(s => s.Length == 4 && s.All(char.IsDigit)))
        {
            using var k = cls.OpenSubKey(sub);
            if (k?.GetValue("DeviceInstanceID") is not string instance || k.GetValue("NetCfgInstanceId") is not string cfg) continue;
            interfaces.TryGetValue(cfg, out var ni);
            result.Add(new NetworkAdapterFact(
                instance,
                $@"{NetClassKey}\{sub}",
                IsWireless: ni?.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                HasLink: ni?.OperationalStatus == OperationalStatus.Up));
        }
        return result;
    }

    // ------------------------------------------------------------ GPU con el escritorio

    private static string? ActiveGpu(DeviceTree tree)
    {
        string? fallback = null;
        for (uint i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0 || string.IsNullOrEmpty(dd.DeviceID)) continue;

            // DeviceID = primer hardware ID del adaptador (PCI\VEN_..&DEV_..&SUBSYS_..&REV_..).
            var node = tree.All.FirstOrDefault(n => n.IsPresent &&
                n.HardwareIds.Any(h => h.Equals(dd.DeviceID, StringComparison.OrdinalIgnoreCase)));
            if (node is null) continue;
            if ((dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0) return node.InstanceId;
            fallback ??= node.InstanceId;
        }
        return fallback;
    }

    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

#pragma warning disable SYSLIB1054 // struct con cadenas ByValTStr: DllImport clásico
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(string? device, uint devNum, ref DISPLAY_DEVICE displayDevice, uint flags);
#pragma warning restore SYSLIB1054

    // ------------------------------------------------------------ salida de audio por defecto

    /// <summary>Devnode del endpoint de reproducción por defecto: SWD\MMDEVAPI\{0.0.0.00000000}.{guid}.</summary>
    private static string? DefaultAudioEndpoint()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 0 /* eConsole */, out var device) != 0 || device is null) return null;
            try
            {
                return device.GetId(out var id) == 0 && !string.IsNullOrEmpty(id) ? @"SWD\MMDEVAPI\" + id : null;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    // ------------------------------------------------------------ impresoras

    /// <summary>Impresoras reales (excluye las virtuales que trae Windows / Office).</summary>
    private static bool HasRealPrinters()
    {
        string[] virtualPrinters = { "Microsoft Print to PDF", "Microsoft XPS Document Writer", "Fax", "OneNote", "Send To OneNote" };
        using var printers = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Print\Printers");
        return printers?.GetSubKeyNames().Any(p => !virtualPrinters.Any(v => p.StartsWith(v, StringComparison.OrdinalIgnoreCase))) ?? false;
    }

    private static T? SafeRun<T>(Func<T> f)
    {
        try { return f(); }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ExternalException or UnauthorizedAccessException) { return default; }
    }
}
