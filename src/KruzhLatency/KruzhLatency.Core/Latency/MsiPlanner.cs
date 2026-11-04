using KruzhLatency.Core.Devices;

namespace KruzhLatency.Core.Latency;

/// <param name="RemoveMessageLimit">MSI-X: se borra MessageNumberLimit (un límite heredado puede forzar menos vectores o el modo línea).</param>
public sealed record MsiTarget(DeviceNode Device, string Kind, bool RemoveMessageLimit);

/// <summary>
/// E8: MSI (MSISupported = 1) solo en dispositivos PCI presentes que declaran soporte de MSI o MSI-X
/// (DEVPKEY_PciDevice_InterruptSupport), en las clases que importan para la latencia: controladoras USB xHCI,
/// audio HD, GPU, red y NVMe. Nunca "a todos los PCI".
/// </summary>
public static class MsiPlanner
{
    private static readonly (string ClassPrefix, string Kind)[] Classes =
    {
        ("0C0330", "USB xHCI"),
        ("0403", "Audio HD"),
        ("03", "GPU"),
        ("0200", "Red"),
        ("010802", "NVMe"),
    };

    /// <summary>Tipo del dispositivo ("USB xHCI", "GPU"…) o null si no es de una clase que importe para la latencia.</summary>
    public static string? KindOf(DeviceNode n) =>
        n.IsPci ? Classes.FirstOrDefault(c => DeviceRuleEngine.PciClassStarts(n, c.ClassPrefix)).Kind : null;

    /// <summary>Orden de presentación de los tipos (GPU y USB primero).</summary>
    public static int Order(string kind) => kind switch { "GPU" => 0, "USB xHCI" => 1, "Red" => 2, "NVMe" => 3, "Audio HD" => 4, _ => 5 };

    public static IReadOnlyList<MsiTarget> Targets(DeviceTree tree)
    {
        var list = new List<MsiTarget>();
        foreach (var n in tree.All.Where(n => n.IsPci && n.IsPresent && !n.IsDisabled && n.ProblemCode == 0))
        {
            if (n.Interrupts is not { } caps || !(caps.SupportsMsi || caps.SupportsMsiX)) continue;
            if (KindOf(n) is not { } kind) continue;
            list.Add(new MsiTarget(n, kind, caps.SupportsMsiX));
        }
        return list.OrderBy(t => Array.FindIndex(Classes, c => c.Kind == t.Kind))
                   .ThenBy(t => t.Device.InstanceId, StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }
}
