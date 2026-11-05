using System.Diagnostics;

namespace KruzhLatency.Infra.Devices;

/// <summary>
/// Software de periféricos que suele tener abiertos los dispositivos USB/HID y vetar su reinicio (Windows lo registra como
/// Kernel-PnP 225: "la aplicación … detuvo la expulsión del dispositivo"). Solo se listan procesos por nombre: no se abren.
/// </summary>
public static class PeripheralSoftware
{
    private static readonly (string Process, string Name)[] Known =
    {
        ("RazerAppEngine", "Razer Synapse"),
        ("Razer Synapse 3", "Razer Synapse 3"),
        ("lghub", "Logitech G HUB"),
        ("lghub_agent", "Logitech G HUB"),
        ("iCUE", "Corsair iCUE"),
        ("SteelSeriesGG", "SteelSeries GG"),
        ("SteelSeriesEngine", "SteelSeries Engine"),
        ("ArmouryCrate", "ASUS Armoury Crate"),
        ("WootilityHelper", "Wootility"),
        ("HyperXNGenuity", "HyperX NGENUITY"),
    };

    public static IReadOnlyList<string> Running()
    {
        var names = new HashSet<string>(Process.GetProcesses().Select(p => { using (p) return p.ProcessName; }), StringComparer.OrdinalIgnoreCase);
        return Known.Where(k => names.Contains(k.Process)).Select(k => k.Name).Distinct().ToList();
    }
}
