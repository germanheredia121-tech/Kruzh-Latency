using KruzhLatency.Core.Display;

namespace KruzhLatency.Core.Devices;

/// <summary>Un aviso de hardware o conexión que KRUZH no puede arreglar solo (hay que mover un cable o seguir una guía).</summary>
public sealed record SetupWarning(string Title, string Detail, string Action);

/// <summary>
/// Al final de la optimización completa: lo que depende de dónde está enchufado cada cosa y del EDID del monitor.
/// <list type="bullet">
/// <item>Mouse en la controladora USB del chipset habiendo una del CPU (AMD): un salto PCIe más y comparte controladora con
/// lo demás del chipset.</item>
/// <item>Mouse detrás de un hub USB (puerto frontal, hub del monitor o de la placa): el hub agrega su propia agenda.</item>
/// <item>Mouse en una controladora USB 2.0 antigua (EHCI/OHCI/UHCI) en vez de xHCI.</item>
/// <item>Teclado en la misma controladora que el mouse: comparten interrupción y DPC.</item>
/// <item>EDID: dañado, con override (CRU), monitores fantasma del mismo modelo ("doble EDID") o Hz por debajo del máximo.</item>
/// </list>
/// </summary>
public static class SetupAdvisor
{
    public static IReadOnlyList<SetupWarning> Inputs(DeviceTree tree)
    {
        var list = new List<SetupWarning>();
        var placement = UsbPowerPlanner.InputPlacement(tree);
        var mice = UsbPowerPlanner.PrimaryMice(tree).ToList(); // sin la interfaz "mouse" de los teclados
        if (mice.Count == 0) return list;

        bool hasCpuController = tree.All.Any(n => n.IsPresent && n.IsPci && DeviceRuleEngine.PciClassStarts(n, "0C0330") &&
                                                  UsbPowerPlanner.Locate(tree, n) == UsbControllerLocation.Cpu);
        // Un mismo receptor expone varios "mouse": se avisa una vez por controladora.
        foreach (var m in mice.GroupBy(p => p.Controller.InstanceId).Select(g => g.First()))
        {
            string name = UsbRootName(tree, m.Input);
            if (m.Location == UsbControllerLocation.Chipset && hasCpuController)
            {
                list.Add(new SetupWarning($"Mouse en un USB del chipset ({name})",
                    "Ese puerto depende del chipset: cada reporte hace un salto PCIe más y comparte controladora con lo demás que cuelga del chipset.",
                    "Pasalo a un puerto trasero que dependa del procesador (en Ryzen suelen ser los USB 3 junto al BIOS Flashback o los de arriba del panel; KRUZH lo vuelve a verificar al abrir)."));
            }
            if (!DeviceRuleEngine.PciClassStarts(m.Controller, "0C0330"))
            {
                list.Add(new SetupWarning($"Mouse en una controladora USB 2.0 ({name})",
                    "La controladora es EHCI/OHCI/UHCI, anterior a xHCI: agenda más gruesa y más trabajo de CPU por reporte.",
                    "Conectalo a un puerto que dependa de una controladora xHCI (USB 3)."));
            }
            if (HubBetween(tree, m.Input, m.Controller) is { } hub)
            {
                list.Add(new SetupWarning($"Mouse detrás de un hub USB ({name})",
                    $"Entre el mouse y la controladora hay un hub ({hub.DisplayName}): puerto frontal, hub del monitor o de la placa. El hub suma su propia agenda.",
                    "Conectalo directo a un puerto trasero de la placa."));
            }

            var keyboardsHere = placement.Where(p => IsClass(p.Input, "Keyboard") &&
                                                     p.Controller.InstanceId == m.Controller.InstanceId &&
                                                     !SameUsbDevice(tree, p.Input, m.Input)).ToList();
            int controllers = tree.All.Count(n => n.IsPresent && !n.IsDisabled && n.IsPci && DeviceRuleEngine.PciClassStarts(n, "0C0330"));
            if (keyboardsHere.Count > 0 && controllers > 1)
            {
                list.Add(new SetupWarning("Teclado y mouse en la misma controladora USB",
                    $"El teclado ({UsbRootName(tree, keyboardsHere[0].Input)}) comparte interrupción y DPC con el mouse: con polling alto o RGB animado, un reporte del mouse puede esperar al del teclado.",
                    "Dejá el mouse solo en su controladora y pasá el teclado a un puerto de la otra (en Ryzen: el teclado al chipset, el mouse al procesador)."));
            }
        }
        return list;
    }

    public static IReadOnlyList<SetupWarning> Displays(IReadOnlyList<MonitorAnalysis> monitors)
    {
        var list = new List<SetupWarning>();
        const string cru = "Seguí los pasos de CRU en Pantalla (KRUZH arma la guía para tu monitor y verifica el resultado).";
        var present = monitors.Where(a => a.Monitor.IsPresent).ToList();

        foreach (var a in present)
        {
            string name = a.Monitor.Name;
            if (a.ParseError is { } err)
            {
                list.Add(new SetupWarning($"EDID raro en {name}", $"No se pudo leer bien el EDID: {err}", cru));
                continue;
            }
            if (a.Monitor.HasOverride)
            {
                list.Add(new SetupWarning($"EDID modificado en {name}",
                    "Windows usa un EDID_OVERRIDE (CRU u otra herramienta) en vez del que manda el monitor. Si no lo hiciste vos con la guía, puede dejar modos o Hz mal declarados.",
                    cru));
            }
            if (a.RunningBelowMax && a.NativeMax is { } max && a.Monitor.Current is { } cur)
            {
                list.Add(new SetupWarning($"{name} por debajo de su máximo",
                    $"Ahora: {cur.SignalWidth}×{cur.SignalHeight} a {cur.RefreshHz:0.##} Hz. El EDID declara {max.HActive}×{max.VActive} a {max.RefreshHz:0.##} Hz.",
                    "Elegí el máximo en Configuración → Pantalla → Pantalla avanzada, o seguí los pasos de CRU en Pantalla si ese modo no aparece."));
            }
        }

        // "Doble EDID": el mismo modelo de monitor registrado más de una vez (conectado + fantasmas de otros puertos o cables).
        // No cuenta el fantasma que cuelga del Microsoft Basic Display Adapter: Windows lo vuelve a crear cada vez que se
        // reinicia el driver de la GPU (actualización, restart64 de CRU, mediciones de KRUZH) y no se usa con el driver cargado.
        foreach (var g in monitors.Where(a => a.Monitor.IsPresent || !IsBasicDisplayGhost(a.Monitor))
                                  .GroupBy(a => ModelKey(a.Monitor.InstanceId)).Where(g => g.Count() > 1 && g.Any(a => a.Monitor.IsPresent)))
        {
            int ghosts = g.Count(a => !a.Monitor.IsPresent);
            bool differentEdids = g.Select(a => a.Monitor.EffectiveEdid is { } e ? Convert.ToBase64String(e) : "").Distinct().Count() > 1;
            list.Add(new SetupWarning($"EDID duplicado de {g.First(a => a.Monitor.IsPresent).Monitor.Name}",
                $"Windows tiene {g.Count()} registros de este monitor ({ghosts} sin conectar{(differentEdids ? ", con EDID distintos" : "")}). " +
                "Es típico después de cambiar de puerto o de cable, y CRU muestra varias entradas: es fácil editar la que no se usa.",
                "Limpiá los monitores fantasma (Optimizaciones → Device Manager) y después seguí los pasos de CRU en Pantalla."));
        }
        return list;
    }

    private static bool IsBasicDisplayGhost(MonitorInfo m) =>
        m.ParentInstanceId is { } p && p.StartsWith(@"ROOT\BasicDisplay", StringComparison.OrdinalIgnoreCase);

    /// <summary>DISPLAY\GSM5B7F\5&amp;xxx&amp;0&amp;UID… → GSM5B7F (el ID de modelo del EDID).</summary>
    private static string ModelKey(string instanceId)
    {
        var parts = instanceId.Split('\\');
        return parts.Length > 1 ? parts[1].ToUpperInvariant() : instanceId.ToUpperInvariant();
    }

    private static bool IsClass(DeviceNode n, string cls) => string.Equals(n.ClassName, cls, StringComparison.OrdinalIgnoreCase);

    private static bool IsUsbDevice(DeviceNode n) =>
        n.Enumerator.Equals("USB", StringComparison.OrdinalIgnoreCase) &&
        n.InstanceId.Split('\\') is { Length: > 1 } p && !p[1].Contains("&MI_", StringComparison.OrdinalIgnoreCase) &&
        !p[1].StartsWith("ROOT_HUB", StringComparison.OrdinalIgnoreCase);

    private static bool IsHub(DeviceNode n) =>
        IsUsbDevice(n) && (n.Service is { } s && s.StartsWith("usbhub", StringComparison.OrdinalIgnoreCase) ||
                           n.CompatibleIds.Any(c => c.StartsWith(@"USB\Class_09", StringComparison.OrdinalIgnoreCase)));

    /// <summary>El dispositivo USB físico (no interfaz, no hub) del que cuelga un mouse/teclado.</summary>
    private static DeviceNode? UsbRoot(DeviceTree tree, DeviceNode input) =>
        tree.Ancestors(input).FirstOrDefault(a => IsUsbDevice(a) && !IsHub(a));

    private static string UsbRootName(DeviceTree tree, DeviceNode input)
    {
        var root = UsbRoot(tree, input);
        if (root is null) return input.DisplayName;
        var vidPid = root.InstanceId.Split('\\')[1];
        return $"{root.DisplayName} {vidPid}";
    }

    private static bool SameUsbDevice(DeviceTree tree, DeviceNode a, DeviceNode b) =>
        UsbRoot(tree, a) is { } ra && UsbRoot(tree, b) is { } rb && ra.InstanceId == rb.InstanceId;

    private static DeviceNode? HubBetween(DeviceTree tree, DeviceNode input, DeviceNode controller)
    {
        foreach (var a in tree.Ancestors(input))
        {
            if (a.InstanceId == controller.InstanceId) return null;
            if (IsHub(a)) return a;
        }
        return null;
    }
}
