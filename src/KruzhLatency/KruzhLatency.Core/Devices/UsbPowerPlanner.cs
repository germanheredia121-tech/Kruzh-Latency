namespace KruzhLatency.Core.Devices;

public enum UsbControllerLocation { Cpu, Chipset, Unknown }

/// <summary>Dónde está conectado un mouse/teclado.</summary>
public sealed record InputPortPlacement(DeviceNode Input, DeviceNode Controller, UsbControllerLocation Location);

/// <summary>
/// Spec 002 / B6: energía de USB/HID y ubicación de los periféricos de entrada.
/// - Dispositivos USB a los que se les apaga el ahorro de energía del hub (EnhancedPowerManagementEnabled / AllowIdleIrpInD3
///   en Device Parameters) y la casilla "Permitir que el equipo apague este dispositivo".
/// - Si el mouse/teclado cuelga de la controladora USB del chipset en vez de la del CPU (AMD), se aconseja cambiar de puerto:
///   el camino por el chipset suma un salto PCIe más.
/// </summary>
public static class UsbPowerPlanner
{
    /// <summary>Nodos USB presentes y habilitados (dispositivos y hubs, sin interfaces &amp;MI_ que heredan del padre).</summary>
    public static IReadOnlyList<DeviceNode> PowerTargets(DeviceTree tree) =>
        tree.All.Where(n => n.IsPresent && !n.IsDisabled &&
                            n.Enumerator.Equals("USB", StringComparison.OrdinalIgnoreCase) &&
                            !n.InstanceId.Split('\\')[1].Contains("&MI_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.InstanceId, StringComparer.OrdinalIgnoreCase)
                .ToList();

    /// <summary>Nodos que pueden tener la casilla de energía (USB y HID presentes; Infra filtra los que la exponen).</summary>
    public static IReadOnlyList<DeviceNode> PowerCheckboxCandidates(DeviceTree tree) =>
        tree.All.Where(n => n.IsPresent && !n.IsDisabled &&
                            (n.Enumerator.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
                             n.Enumerator.Equals("HID", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(n => n.InstanceId, StringComparer.OrdinalIgnoreCase)
                .ToList();

    public static IReadOnlyList<InputPortPlacement> InputPlacement(DeviceTree tree)
    {
        var result = new List<InputPortPlacement>();
        foreach (var input in tree.All.Where(n => n.IsPresent && (Is(n, "Mouse") || Is(n, "Keyboard"))))
        {
            var controller = tree.Ancestors(input).FirstOrDefault(a => a.IsPci && DeviceRuleEngine.PciClassStarts(a, "0C03"));
            if (controller is null) continue; // PS/2, Bluetooth, etc.
            result.Add(new InputPortPlacement(input, controller, Locate(tree, controller)));
        }
        return result;
    }

    /// <summary>
    /// Solo AMD (en Intel casi todos los puertos son del PCH). Chipset si la xHCI está detrás de 2+ bridges PCI
    /// (X570/X670: detrás del switch del chipset) o comparte dispositivo PCI con un bridge (B550/B650: el chipset
    /// expone xHCI, SATA y su switch como funciones del mismo dispositivo). CPU en otro caso.
    /// </summary>
    public static UsbControllerLocation Locate(DeviceTree tree, DeviceNode controller)
    {
        if (!controller.HasHardwareIdPrefix(@"PCI\VEN_1022")) return UsbControllerLocation.Unknown;

        int bridges = tree.Ancestors(controller).Count(a => a.IsPci && DeviceRuleEngine.PciClassStarts(a, "0604"));
        bool bridgeSibling = tree.PciSiblings(controller).Any(s => DeviceRuleEngine.PciClassStarts(s, "0604"));
        return bridges >= 2 || bridgeSibling ? UsbControllerLocation.Chipset : UsbControllerLocation.Cpu;
    }

    private static bool Is(DeviceNode n, string cls) => string.Equals(n.ClassName, cls, StringComparison.OrdinalIgnoreCase);
}
