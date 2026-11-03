namespace KruzhLatency.Core.Devices;

public enum GhostCategory { UsbAndHid, Bluetooth, Storage, Network, OldGpu }

/// <summary>Dispositivos fantasma (no conectados) de una categoría, en orden de borrado (hijos antes que padres).</summary>
public sealed record GhostGroup(GhostCategory Category, string Title, string Why, IReadOnlyList<DeviceNode> Devices);

/// <summary>
/// Spec 002 / B2: entradas fantasma del Administrador de dispositivos que conviene borrar.
/// No se proponen: fantasmas "ocultos" porque un padre presente está deshabilitado (vuelven al habilitarlo),
/// monitores (los limpia C4 junto con la caché de pantallas), ni nodos de software/ROOT/ACPI de Windows.
/// Borrar un fantasma es irreversible pero inocuo: si el dispositivo se vuelve a conectar, Windows lo reinstala.
/// </summary>
public static class GhostDevicePlanner
{
    public static IReadOnlyList<GhostGroup> Plan(DeviceTree tree)
    {
        var buckets = new Dictionary<GhostCategory, List<DeviceNode>>();
        foreach (var n in tree.All.Where(n => !n.IsPresent))
        {
            if (HiddenByDisabledParent(tree, n)) continue;
            if (Categorize(tree, n) is not { } cat) continue;
            if (!buckets.TryGetValue(cat, out var list)) buckets[cat] = list = new List<DeviceNode>();
            list.Add(n);
        }

        return buckets
            .OrderBy(b => b.Key)
            .Select(b => new GhostGroup(b.Key, TitleOf(b.Key), WhyOf(b.Key),
                b.Value.OrderByDescending(n => tree.Ancestors(n).Count()).ThenBy(n => n.InstanceId, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
    }

    private static bool HiddenByDisabledParent(DeviceTree tree, DeviceNode n) =>
        tree.Ancestors(n).Any(a => a.IsPresent && a.IsDisabled);

    internal static GhostCategory? Categorize(DeviceTree tree, DeviceNode n)
    {
        string e = n.Enumerator.ToUpperInvariant();
        string cls = n.ClassName;

        if (e is "DISPLAY" || Is(cls, "Monitor")) return null; // C4
        if (e is "SWD" or "SW" or "ROOT" or "ACPI" or "ACPI_HAL" or "HTREE" or "UEFI") return null;

        if (e.StartsWith("BTH") || Is(cls, "Bluetooth")) return GhostCategory.Bluetooth;

        if (e is "USBSTOR" or "SCSI" or "WPDBUSENUM" || Is(cls, "DiskDrive") || Is(cls, "WPD") || Is(cls, "CDROM") ||
            (e == "STORAGE" && n.InstanceId.StartsWith(@"STORAGE\Volume\", StringComparison.OrdinalIgnoreCase)))
            return GhostCategory.Storage;

        if (n.IsPci && Is(cls, "Display")) return GhostCategory.OldGpu;
        if (e == "HDAUDIO" && tree.Parent(n) is { IsPresent: false, IsPci: true } p && tree.PciSiblings(p).Concat(new[] { p }).Any(s => Is(s.ClassName, "Display")))
            return GhostCategory.OldGpu;

        if (Is(cls, "Net") && (n.IsPci || e == "USB")) return GhostCategory.Network;

        if (n.InstanceId.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase)) return null; // hub raíz de una controladora
        if (e is "USB" or "HID" || (!n.IsPci && n.InstanceId.Contains("VID_", StringComparison.OrdinalIgnoreCase)))
            return GhostCategory.UsbAndHid;

        return null;
    }

    private static string TitleOf(GhostCategory c) => c switch
    {
        GhostCategory.UsbAndHid => "Fantasmas USB / HID (mouses, teclados, joysticks desconectados)",
        GhostCategory.Bluetooth => "Fantasmas Bluetooth",
        GhostCategory.Storage => "Fantasmas de almacenamiento (pendrives, discos, volúmenes)",
        GhostCategory.Network => "Placas de red que ya no están",
        GhostCategory.OldGpu => "Placas de video anteriores",
        _ => c.ToString()
    };

    private static string WhyOf(GhostCategory c) => c switch
    {
        GhostCategory.UsbAndHid => "Cada mouse, teclado o joystick que se enchufó alguna vez deja nodos HID y XInput registrados; los virtuales de software de periféricos (Razer, Logitech…) también. Limpiarlos evita que se vuelvan a enumerar y ordena el Administrador.",
        GhostCategory.Bluetooth => "Dispositivos Bluetooth que ya no están emparejados ni conectados.",
        GhostCategory.Storage => "Pendrives y discos que se conectaron alguna vez, con sus volúmenes.",
        GhostCategory.Network => "Placas de red de otra PC, de otra ranura o reemplazadas: sus entradas quedan en la pila de red.",
        GhostCategory.OldGpu => "GPU y su audio HDMI de una placa anterior.",
        _ => ""
    };

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
