using System.Runtime.InteropServices;
using KruzhLatency.Core.Devices;
using KruzhLatency.Core.Display;
using Microsoft.Win32;

namespace KruzhLatency.Infra.Display;

/// <summary>
/// Monitores reales y fantasmas (devnodes DISPLAY\…), su EDID y EDID_OVERRIDE del registro y la señal actual
/// (QueryDisplayConfig: pixel clock, totales y Hz exactos de lo que sale por el cable). Solo lectura.
/// </summary>
public static partial class MonitorProbe
{
    public static IReadOnlyList<MonitorInfo> Detect(DeviceTree tree)
    {
        var active = ActiveModes();
        var result = new List<MonitorInfo>();
        foreach (var n in tree.All.Where(n => n.Enumerator.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase)))
        {
            active.TryGetValue(n.InstanceId, out var current);
            var (edid, over) = ReadEdids(n.InstanceId);
            string name = edid is not null && TryName(edid) is { } edidName ? edidName : n.DisplayName;
            result.Add(new MonitorInfo(n.InstanceId, name, n.IsPresent, n.ParentInstanceId, edid, over, current));
        }
        return result.OrderByDescending(m => m.IsPresent).ThenBy(m => m.Name).ToList();
    }

    private static string? TryName(byte[] edid)
    {
        try { return EdidParser.Parse(edid).Base.MonitorName; }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>EDID y EDID_OVERRIDE (subclave con valores "0", "1"… de 128 bytes cada uno, formato de CRU).</summary>
    public static (byte[]? Edid, byte[]? Override) ReadEdids(string instanceId)
    {
        using var dp = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters");
        byte[]? edid = dp?.GetValue("EDID") as byte[];
        byte[]? over = null;
        using (var ov = dp?.OpenSubKey("EDID_OVERRIDE"))
        {
            if (ov is not null)
            {
                var blocks = ov.GetValueNames()
                    .Select(v => (Ok: int.TryParse(v, out int i), Index: i, Data: ov.GetValue(v) as byte[]))
                    .Where(x => x.Ok && x.Data is { Length: 128 })
                    .OrderBy(x => x.Index)
                    .SelectMany(x => x.Data!)
                    .ToArray();
                over = blocks.Length > 0 ? blocks : null;
            }
        }
        return (edid, over);
    }

    // ------------------------------------------------------------ QueryDisplayConfig

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    private const int PathSize = 72, ModeSize = 64, TargetNameSize = 420;
    private const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1, DISPLAYCONFIG_MODE_INFO_TYPE_TARGET = 2;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    /// <summary>Instancia del monitor → modo activo.</summary>
    public static Dictionary<string, ActiveDisplayMode> ActiveModes()
    {
        var map = new Dictionary<string, ActiveDisplayMode>(StringComparer.OrdinalIgnoreCase);
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0) return map;

        IntPtr paths = Marshal.AllocHGlobal((int)(pathCount * PathSize));
        IntPtr modes = Marshal.AllocHGlobal((int)(modeCount * ModeSize));
        IntPtr name = Marshal.AllocHGlobal(TargetNameSize);
        try
        {
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return map;

            for (int i = 0; i < pathCount; i++)
            {
                IntPtr p = paths + i * PathSize;
                // sourceInfo: LUID(0) id(8) modeInfoIdx(12) status(16) · targetInfo desde 20: LUID(20) id(28) modeInfoIdx(32)
                // outputTechnology(36) rotation(40) scaling(44) refreshRate(48,52) scanLine(56) available(60) status(64) · flags(68)
                long targetAdapter = Marshal.ReadInt64(p, 20);
                uint targetId = (uint)Marshal.ReadInt32(p, 28);
                uint sourceModeIdx = (uint)Marshal.ReadInt32(p, 12);
                uint targetModeIdx = (uint)Marshal.ReadInt32(p, 32);
                uint tech = (uint)Marshal.ReadInt32(p, 36);
                uint scaling = (uint)Marshal.ReadInt32(p, 44);

                string? instance = TargetInstanceId(name, targetAdapter, targetId);
                if (instance is null || targetModeIdx >= modeCount) continue;

                IntPtr tm = modes + (int)targetModeIdx * ModeSize;
                if ((uint)Marshal.ReadInt32(tm, 0) != DISPLAYCONFIG_MODE_INFO_TYPE_TARGET) continue;
                // targetVideoSignalInfo desde 16: pixelRate(16) hSync(24,28) vSync(32,36) active(40,44) total(48,52)
                long pixelRate = Marshal.ReadInt64(tm, 16);
                uint vNum = (uint)Marshal.ReadInt32(tm, 32), vDen = (uint)Marshal.ReadInt32(tm, 36);
                int actW = Marshal.ReadInt32(tm, 40), actH = Marshal.ReadInt32(tm, 44);
                int totW = Marshal.ReadInt32(tm, 48), totH = Marshal.ReadInt32(tm, 52);

                int deskW = actW, deskH = actH;
                if (sourceModeIdx < modeCount)
                {
                    IntPtr sm = modes + (int)sourceModeIdx * ModeSize;
                    if ((uint)Marshal.ReadInt32(sm, 0) == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
                    {
                        deskW = Marshal.ReadInt32(sm, 16);
                        deskH = Marshal.ReadInt32(sm, 20);
                    }
                }

                map[instance] = new ActiveDisplayMode(deskW, deskH, actW, actH, totW, totH, pixelRate,
                    vDen == 0 ? 0 : (double)vNum / vDen, Technology(tech), Scaling(scaling));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(paths);
            Marshal.FreeHGlobal(modes);
            Marshal.FreeHGlobal(name);
        }
        return map;
    }

    /// <summary>DISPLAYCONFIG_TARGET_DEVICE_NAME.monitorDevicePath (\\?\DISPLAY#GBT2404#5&amp;…#{guid}) → DISPLAY\GBT2404\5&amp;…</summary>
    private static string? TargetInstanceId(IntPtr buffer, long adapter, uint targetId)
    {
        for (int i = 0; i < TargetNameSize; i += 4) Marshal.WriteInt32(buffer, i, 0);
        Marshal.WriteInt32(buffer, 0, (int)DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME);
        Marshal.WriteInt32(buffer, 4, TargetNameSize);
        Marshal.WriteInt64(buffer, 8, adapter);
        Marshal.WriteInt32(buffer, 16, (int)targetId);
        if (DisplayConfigGetDeviceInfo(buffer) != 0) return null;

        // header(20) flags(20) tech(24) edidIds(28,30) connector(32) friendlyName[64](36) devicePath[128](164)
        string path = Marshal.PtrToStringUni(buffer + 164) ?? "";
        if (!path.StartsWith(@"\\?\", StringComparison.Ordinal)) return null;
        var parts = path[4..].Split('#');
        return parts.Length >= 3 ? $@"{parts[0]}\{parts[1]}\{parts[2]}" : null;
    }

    private static string Technology(uint t) => t switch
    {
        0 => "VGA", 4 => "DVI", 5 => "HDMI", 6 => "LVDS", 10 => "DisplayPort", 11 => "DisplayPort (interno)",
        12 => "UDI", 13 => "UDI (interno)", 14 => "SDTV", 15 => "Miracast", 16 => "Indirecto (cableado)",
        17 => "Indirecto (virtual)", 0x80000000 => "Interno", _ => $"Tipo {t}"
    };

    private static string Scaling(uint s) => s switch
    {
        1 => "Identidad", 2 => "Centrado", 3 => "Estirado", 4 => "Relación de aspecto", 5 => "Personalizado", 128 => "Preferido",
        _ => $"Modo {s}"
    };

    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [LibraryImport("user32.dll")]
    private static partial int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, IntPtr pathArray,
        ref uint numModeInfoArrayElements, IntPtr modeInfoArray, IntPtr currentTopologyId);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo(IntPtr requestPacket);
}
