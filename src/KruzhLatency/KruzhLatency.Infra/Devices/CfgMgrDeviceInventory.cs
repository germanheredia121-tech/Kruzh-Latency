using System.Runtime.InteropServices;
using KruzhLatency.Core.Abstractions;
using KruzhLatency.Core.Devices;

namespace KruzhLatency.Infra.Devices;

/// <summary>
/// Inventario de dispositivos vía CfgMgr32 (la misma API que usa el Administrador de dispositivos):
/// todos los devnodes registrados (presentes y fantasmas) con padre, IDs, clase, bus/address, problema, driver
/// e interrupciones. Enumerar es solo lectura; <see cref="Restart"/> es la única escritura y la usa el auto-tune MSI.
/// </summary>
public sealed partial class CfgMgrDeviceInventory : IDeviceInventory, IDeviceManager
{
    public IReadOnlyList<DeviceNode> Snapshot()
    {
        var result = new List<DeviceNode>();
        foreach (var id in AllInstanceIds())
        {
            var node = Read(id);
            if (node is not null) result.Add(node);
        }
        return result;
    }

    public IReadOnlyList<DeviceInfo> EnumerateDevices() => Snapshot().Select(n => n.ToDeviceInfo()).ToList();

    public DeviceInfo? GetDevice(string instanceId) => Read(instanceId)?.ToDeviceInfo();

    public DeviceRestartResult Restart(string instanceId, TimeSpan timeout)
    {
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS)
        {
            return new DeviceRestartResult(false, 0, "El dispositivo no está presente.");
        }

        // CM_DISABLE_UI_NOT_OK: si el driver no admite apagarse en caliente, falla en vez de pedir reinicio.
        uint cr = CM_Disable_DevNode(devInst, CM_DISABLE_UI_NOT_OK);
        if (cr != CR_SUCCESS)
        {
            return new DeviceRestartResult(false, ProblemOf(devInst), $"No se puede reiniciar en caliente (CR 0x{cr:X}); requiere reiniciar la PC.");
        }

        cr = CM_Enable_DevNode(devInst, 0);
        var deadline = DateTime.UtcNow + timeout;
        uint problem = ProblemOf(devInst);
        while (problem != 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(200);
            problem = ProblemOf(devInst);
        }

        return cr == CR_SUCCESS
            ? new DeviceRestartResult(true, problem, problem == 0 ? "OK" : $"Code {problem}")
            : new DeviceRestartResult(false, problem, $"No se pudo volver a habilitar (CR 0x{cr:X}).");
    }

    // ------------------------------------------------------------ lectura

    private static IEnumerable<string> AllInstanceIds()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out uint len, null, CM_GETIDLIST_FILTER_NONE) != CR_SUCCESS || len == 0)
            {
                return Array.Empty<string>();
            }
            var buffer = new char[len];
            uint cr = CM_Get_Device_ID_ListW(null, buffer, len, CM_GETIDLIST_FILTER_NONE);
            if (cr == CR_BUFFER_SMALL) continue; // apareció un dispositivo entre las dos llamadas
            if (cr != CR_SUCCESS) return Array.Empty<string>();
            return SplitMultiSz(buffer);
        }
        return Array.Empty<string>();
    }

    private static DeviceNode? Read(string instanceId)
    {
        bool present = CM_Locate_DevNodeW(out uint devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS;
        if (!present && CM_Locate_DevNodeW(out devInst, instanceId, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS)
        {
            return null;
        }

        uint problem = present ? ProblemOf(devInst) : (GetUInt32(devInst, DevPKey.ProblemCode) ?? 0);
        uint configFlags = GetUInt32(devInst, DevPKey.ConfigFlags) ?? 0;

        string? parent = GetString(devInst, DevPKey.Parent);
        if (parent is null && present && CM_Get_Parent(out uint parentInst, devInst, 0) == CR_SUCCESS)
        {
            parent = GetDeviceId(parentInst);
        }

        int slash = instanceId.IndexOf('\\');
        string enumerator = GetString(devInst, DevPKey.EnumeratorName) ?? (slash > 0 ? instanceId[..slash] : instanceId);

        return new DeviceNode(
            InstanceId: instanceId,
            ParentInstanceId: string.IsNullOrEmpty(parent) ? null : parent,
            Description: GetString(devInst, DevPKey.DeviceDesc) ?? instanceId,
            FriendlyName: GetString(devInst, DevPKey.FriendlyName),
            ClassName: GetString(devInst, DevPKey.Class) ?? "",
            ClassGuid: GetGuid(devInst, DevPKey.ClassGuid) ?? Guid.Empty,
            Enumerator: enumerator,
            HardwareIds: GetStringList(devInst, DevPKey.HardwareIds),
            CompatibleIds: GetStringList(devInst, DevPKey.CompatibleIds),
            IsPresent: present,
            IsDisabled: (configFlags & CONFIGFLAG_DISABLED) != 0 || problem == CM_PROB_DISABLED,
            ProblemCode: problem,
            Service: GetString(devInst, DevPKey.Service),
            Manufacturer: GetString(devInst, DevPKey.Manufacturer),
            LocationInfo: GetString(devInst, DevPKey.LocationInfo),
            BusNumber: GetUInt32(devInst, DevPKey.BusNumber),
            Address: GetUInt32(devInst, DevPKey.Address),
            Driver: ReadDriver(devInst),
            Interrupts: ReadInterrupts(devInst));
    }

    private static DeviceDriverInfo? ReadDriver(uint devInst)
    {
        string? provider = GetString(devInst, DevPKey.DriverProvider);
        string? version = GetString(devInst, DevPKey.DriverVersion);
        string? inf = GetString(devInst, DevPKey.DriverInfPath);
        if (provider is null && version is null && inf is null) return null;

        DateOnly? date = GetFileTime(devInst, DevPKey.DriverDate) is { } ft ? DateOnly.FromDateTime(ft) : null;
        bool generic = string.Equals(provider, "Microsoft", StringComparison.OrdinalIgnoreCase) &&
                       inf is not null && !inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase);
        return new DeviceDriverInfo(provider ?? "", version ?? "", date, inf ?? "", generic);
    }

    private static InterruptCapabilities? ReadInterrupts(uint devInst)
    {
        if (GetUInt32(devInst, DevPKey.PciInterruptSupport) is not { } support) return null;
        uint max = GetUInt32(devInst, DevPKey.PciInterruptMessageMaximum) ?? 0;
        return new InterruptCapabilities(
            SupportsLineBased: (support & 0x1) != 0,
            SupportsMsi: (support & 0x2) != 0,
            SupportsMsiX: (support & 0x4) != 0,
            MaxMessages: max);
    }

    private static uint ProblemOf(uint devInst) =>
        CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0) == CR_SUCCESS && (status & DN_HAS_PROBLEM) != 0
            ? problem
            : 0;

    private static string? GetDeviceId(uint devInst)
    {
        var buffer = new char[MAX_DEVICE_ID_LEN + 1];
        return CM_Get_Device_IDW(devInst, buffer, (uint)buffer.Length, 0) == CR_SUCCESS
            ? new string(buffer).TrimEnd('\0')
            : null;
    }

    // ------------------------------------------------------------ propiedades tipadas

    private static byte[]? GetRaw(uint devInst, in DEVPROPKEY key, uint expectedType)
    {
        uint size = 0;
        uint cr = CM_Get_DevNode_PropertyW(devInst, key, out uint type, null, ref size, 0);
        if (cr != CR_BUFFER_SMALL || size == 0) return null;
        var buffer = new byte[size];
        cr = CM_Get_DevNode_PropertyW(devInst, key, out type, buffer, ref size, 0);
        return cr == CR_SUCCESS && type == expectedType ? buffer : null;
    }

    private static string? GetString(uint devInst, in DEVPROPKEY key)
    {
        var raw = GetRaw(devInst, key, DEVPROP_TYPE_STRING);
        if (raw is null) return null;
        var s = MemoryMarshal.Cast<byte, char>(raw).ToString().TrimEnd('\0');
        return s.Length == 0 ? null : s;
    }

    private static IReadOnlyList<string> GetStringList(uint devInst, in DEVPROPKEY key)
    {
        var raw = GetRaw(devInst, key, DEVPROP_TYPE_STRING_LIST);
        return raw is null ? Array.Empty<string>() : SplitMultiSz(MemoryMarshal.Cast<byte, char>(raw).ToArray());
    }

    private static uint? GetUInt32(uint devInst, in DEVPROPKEY key)
    {
        var raw = GetRaw(devInst, key, DEVPROP_TYPE_UINT32);
        return raw is { Length: >= 4 } ? BitConverter.ToUInt32(raw, 0) : null;
    }

    private static Guid? GetGuid(uint devInst, in DEVPROPKEY key)
    {
        var raw = GetRaw(devInst, key, DEVPROP_TYPE_GUID);
        return raw is { Length: 16 } ? new Guid(raw) : null;
    }

    private static DateTime? GetFileTime(uint devInst, in DEVPROPKEY key)
    {
        var raw = GetRaw(devInst, key, DEVPROP_TYPE_FILETIME);
        if (raw is not { Length: 8 }) return null;
        long ft = BitConverter.ToInt64(raw, 0);
        return ft > 0 ? DateTime.FromFileTimeUtc(ft) : null;
    }

    private static List<string> SplitMultiSz(char[] buffer) =>
        new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();

    // ------------------------------------------------------------ CfgMgr32

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DEVPROPKEY
    {
        public readonly Guid FmtId;
        public readonly uint Pid;

        public DEVPROPKEY(string fmtId, uint pid)
        {
            FmtId = new Guid(fmtId);
            Pid = pid;
        }
    }

    private static class DevPKey
    {
        private const string Device = "a45c254e-df1c-4efd-8020-67d146a850e0";
        private const string DeviceRel = "4340a6c5-93fa-4706-972c-7b648008a5a7";
        private const string DriverProps = "a8b865dd-2e3d-4094-ad97-e593a70c75d6";
        private const string Pci = "3ab22e31-8264-4b4e-9af5-a8d2d8e33e62";

        public static readonly DEVPROPKEY DeviceDesc = new(Device, 2);
        public static readonly DEVPROPKEY HardwareIds = new(Device, 3);
        public static readonly DEVPROPKEY CompatibleIds = new(Device, 4);
        public static readonly DEVPROPKEY Service = new(Device, 6);
        public static readonly DEVPROPKEY Class = new(Device, 9);
        public static readonly DEVPROPKEY ClassGuid = new(Device, 10);
        public static readonly DEVPROPKEY ConfigFlags = new(Device, 12);
        public static readonly DEVPROPKEY Manufacturer = new(Device, 13);
        public static readonly DEVPROPKEY FriendlyName = new(Device, 14);
        public static readonly DEVPROPKEY LocationInfo = new(Device, 15);
        public static readonly DEVPROPKEY BusNumber = new(Device, 23);
        public static readonly DEVPROPKEY EnumeratorName = new(Device, 24);
        public static readonly DEVPROPKEY Address = new(Device, 30);

        public static readonly DEVPROPKEY ProblemCode = new(DeviceRel, 3);
        public static readonly DEVPROPKEY Parent = new(DeviceRel, 8);

        public static readonly DEVPROPKEY DriverDate = new(DriverProps, 2);
        public static readonly DEVPROPKEY DriverVersion = new(DriverProps, 3);
        public static readonly DEVPROPKEY DriverInfPath = new(DriverProps, 5);
        public static readonly DEVPROPKEY DriverProvider = new(DriverProps, 9);

        public static readonly DEVPROPKEY PciInterruptSupport = new(Pci, 14);
        public static readonly DEVPROPKEY PciInterruptMessageMaximum = new(Pci, 15);
    }

    private const uint CR_SUCCESS = 0x00;
    private const uint CR_BUFFER_SMALL = 0x1A;
    private const uint CM_GETIDLIST_FILTER_NONE = 0;
    private const uint CM_LOCATE_DEVNODE_NORMAL = 0;
    private const uint CM_LOCATE_DEVNODE_PHANTOM = 1;
    private const uint CM_DISABLE_UI_NOT_OK = 0x4;
    private const uint DN_HAS_PROBLEM = 0x400;
    private const uint CM_PROB_DISABLED = 22;
    private const uint CONFIGFLAG_DISABLED = 0x1;
    private const int MAX_DEVICE_ID_LEN = 200;

    private const uint DEVPROP_TYPE_UINT32 = 0x07;
    private const uint DEVPROP_TYPE_GUID = 0x0D;
    private const uint DEVPROP_TYPE_FILETIME = 0x10;
    private const uint DEVPROP_TYPE_STRING = 0x12;
    private const uint DEVPROP_TYPE_STRING_LIST = 0x2012;

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_List_SizeW(out uint pulLen, string? pszFilter, uint ulFlags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_ListW(string? pszFilter, [Out] char[] buffer, uint bufferLen, uint ulFlags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceId, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_DevNode_PropertyW(uint dnDevInst, in DEVPROPKEY propertyKey, out uint propertyType,
        [Out] byte[]? propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_IDW(uint dnDevInst, [Out] char[] buffer, uint bufferLen, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Disable_DevNode(uint dnDevInst, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Enable_DevNode(uint dnDevInst, uint ulFlags);
}
