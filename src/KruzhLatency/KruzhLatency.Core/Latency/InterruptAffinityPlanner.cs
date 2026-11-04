using KruzhLatency.Core.Devices;
using KruzhLatency.Core.Hardware;

namespace KruzhLatency.Core.Latency;

public enum IrqDeviceKind { UsbController, Gpu, Nic, Nvme }

/// <summary>Dispositivo PCI cuyas interrupciones se ubican. <paramref name="HostsInput"/>: tiene mouse/teclado colgando (USB).</summary>
public sealed record IrqDevice(string InstanceId, string Name, IrqDeviceKind Kind, bool HostsInput = false);

/// <summary>
/// Afinidad de un dispositivo: Mask = hilos lógicos (AssignmentSetOverride, DevicePolicy = 4); null = sin override
/// (la placa de red reparte por RSS). DevicePriority null = sin prioridad (se borra si había una).
/// </summary>
public sealed record IrqAssignment(IrqDevice Device, ulong? Mask, int? DevicePriority, bool SharesCore)
{
    public IReadOnlyList<int> Threads => Mask is { } m ? Enumerable.Range(0, 64).Where(b => (m & (1UL << b)) != 0).ToList() : Array.Empty<int>();

    /// <summary>Máscara en bytes little-endian sin ceros a la derecha (formato de AssignmentSetOverride).</summary>
    public byte[]? MaskBytes => Mask is { } m ? InterruptAffinityPlanner.ToMaskBytes(m) : null;
}

/// <summary>Procesadores de RSS (keywords estándar de NDIS): base, último procesador y cantidad.</summary>
public sealed record RssPlacement(int BaseProcessor, int MaxProcessor, int MaxProcessors);

public sealed record AffinityPlan(IReadOnlyList<IrqAssignment> Assignments, RssPlacement? Rss, IReadOnlyList<string> Notes);

/// <summary>
/// E7 (+ E2): reparto de interrupciones por núcleo físico. Reglas:
/// - El núcleo 0 queda libre para el kernel (nunca se asigna nada).
/// - Solo el primer hilo de cada núcleo; nunca E-cores para GPU/USB; en CPUs con varios L3 se usa el CCD con más L3
///   (V-Cache en X3D), si no el CCD 0.
/// - Orden: red (RSS en el 2.º núcleo) → cada controladora USB en su núcleo (la del mouse primero, DevicePriority = 3)
///   → GPU → 2.º núcleo de la GPU (opcional) → NVMe.
/// - Si no alcanzan los núcleos, el dispositivo comparte el núcleo del dispositivo menos importante ya ubicado.
/// </summary>
public static class InterruptAffinityPlanner
{
    public const int UsbPriorityHigh = 3;

    // Importancia (menor = más importante) para decidir con quién se comparte.
    private const int RankUsb = 0, RankGpu = 1, RankNic = 2, RankNvme = 3, RankGpuSecond = 4;

    /// <summary>Junta del árbol los dispositivos PCI presentes y habilitados que interesan (xHCI, GPU, red, NVMe).</summary>
    public static IReadOnlyList<IrqDevice> Collect(DeviceTree tree)
    {
        var inputHosts = UsbPowerPlanner.InputPlacement(tree)
            .Select(p => p.Controller.InstanceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var list = new List<IrqDevice>();
        foreach (var n in tree.All.Where(n => n.IsPci && n.IsPresent && !n.IsDisabled && n.ProblemCode == 0))
        {
            IrqDeviceKind? kind =
                DeviceRuleEngine.PciClassStarts(n, "0C0330") ? IrqDeviceKind.UsbController :
                DeviceRuleEngine.PciClassStarts(n, "03") ? IrqDeviceKind.Gpu :
                DeviceRuleEngine.PciClassStarts(n, "0200") ? IrqDeviceKind.Nic :
                DeviceRuleEngine.PciClassStarts(n, "010802") ? IrqDeviceKind.Nvme :
                null;
            if (kind is not { } k) continue;
            // Una controladora USB sin nada conectado (ej. el USB-C de la placa de video) no gasta un núcleo.
            if (k == IrqDeviceKind.UsbController && !HostsUsbDevice(tree, n)) continue;
            list.Add(new IrqDevice(n.InstanceId, n.DisplayName, k, inputHosts.Contains(n.InstanceId)));
        }
        return list.OrderBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <returns>null si la CPU no tiene al menos 4 núcleos utilizables (no hay forma de aislar sin saturar).</returns>
    public static AffinityPlan? Plan(IReadOnlyList<LogicalProcessor> topology, IReadOnlyList<IrqDevice> devices, bool gpuTwoCores)
    {
        if (topology.Count == 0) return null;
        var notes = new List<string>();

        // Núcleo físico → hilos (ordenados). El núcleo que contiene al hilo 0 es el del kernel.
        var cores = topology.Where(t => t.Index < 64)
            .GroupBy(t => t.PhysicalCoreIndex)
            .Select(g => new Core(g.Key, g.OrderBy(t => t.Index).Select(t => t.Index).ToList(),
                                  g.First().IsEfficiencyCore, g.First().L3GroupIndex, g.First().L3SizeKb))
            .OrderBy(c => c.Threads[0])
            .ToList();
        if (cores.Count < 4) return null;
        int kernelCore = cores.First(c => c.Threads.Contains(0)).Id;

        // Grupo L3 preferido: el de más L3 (V-Cache); empate → el que tiene al núcleo 0.
        int kernelL3 = cores.First(c => c.Id == kernelCore).L3Group;
        int preferredL3 = cores
            .GroupBy(c => c.L3Group)
            .OrderByDescending(g => g.Max(c => c.L3SizeKb))
            .ThenBy(g => g.Key == kernelL3 ? 0 : 1)
            .ThenBy(g => g.Key)
            .First().Key;
        if (cores.Select(c => c.L3Group).Distinct().Count() > 1)
        {
            notes.Add(cores.Select(c => c.L3SizeKb).Distinct().Count() > 1
                ? "Interrupciones en el CCD con V-Cache (donde corre el juego)."
                : "Interrupciones en el primer CCD.");
        }

        // Orden de candidatos: P-cores del L3 preferido, P-cores de otros L3, E-cores (solo red/NVMe).
        var free = cores.Where(c => c.Id != kernelCore)
            .OrderBy(c => c.IsEfficiency ? 2 : c.L3Group == preferredL3 ? 0 : 1)
            .ThenBy(c => c.Threads[0])
            .ToList();
        var occupants = new Dictionary<int, List<int>>(); // núcleo → rangos de lo que tiene
        var assignments = new List<IrqAssignment>();

        Core? Take(int rank, bool allowEfficiency)
        {
            var pick = free.FirstOrDefault(c => allowEfficiency || !c.IsEfficiency);
            if (pick is not null)
            {
                free.Remove(pick);
                occupants[pick.Id] = new List<int> { rank };
                return pick;
            }
            return null;
        }

        Core Share(int rank, bool allowEfficiency)
        {
            // El núcleo cuyo ocupante más importante es el menos importante; empate → el último ubicado.
            var pick = occupants
                .Select(o => cores.First(c => c.Id == o.Key))
                .Where(c => allowEfficiency || !c.IsEfficiency)
                .Select((c, order) => (Core: c, Rank: occupants[c.Id].Min(), Order: order))
                .OrderByDescending(x => x.Rank).ThenByDescending(x => x.Order)
                .First().Core;
            occupants[pick.Id].Add(rank);
            return pick;
        }

        (Core Core, bool Shared) Place(int rank, bool allowEfficiency)
        {
            if (Take(rank, allowEfficiency) is { } own) return (own, false);
            return (Share(rank, allowEfficiency), true);
        }

        // 1) Red: RSS en el 2.º núcleo (todos los hilos de ese núcleo; 2 colas = 2 hilos con SMT).
        var nics = devices.Where(d => d.Kind == IrqDeviceKind.Nic).ToList();
        RssPlacement? rss = null;
        if (nics.Count > 0)
        {
            var (nicCore, _) = Place(RankNic, allowEfficiency: false);
            var threads = nicCore.Threads.Take(2).ToList();
            rss = new RssPlacement(threads[0], threads[^1], threads.Count);
            assignments.AddRange(nics.Select(n => new IrqAssignment(n, null, null, false)));
        }

        // 2) Cada controladora USB en su núcleo (la del mouse/teclado primero), prioridad alta.
        foreach (var usb in devices.Where(d => d.Kind == IrqDeviceKind.UsbController)
                                   .OrderByDescending(d => d.HostsInput).ThenBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase))
        {
            var (core, shared) = Place(RankUsb, allowEfficiency: false);
            assignments.Add(new IrqAssignment(usb, 1UL << core.Threads[0], UsbPriorityHigh, shared));
        }

        // 3) GPU (todas las GPU comparten su núcleo) y 4) su 2.º núcleo para el pico de video + juego.
        var gpus = devices.Where(d => d.Kind == IrqDeviceKind.Gpu).ToList();
        if (gpus.Count > 0)
        {
            var (gpuCore, gpuShared) = Place(RankGpu, allowEfficiency: false);
            ulong gpuMask = 1UL << gpuCore.Threads[0];
            if (gpuTwoCores && Take(RankGpuSecond, allowEfficiency: false) is { } second)
            {
                gpuMask |= 1UL << second.Threads[0];
            }
            else if (gpuTwoCores)
            {
                notes.Add("No quedó un núcleo libre para el 2.º núcleo de la GPU: queda en uno solo.");
            }
            assignments.AddRange(gpus.Select(g => new IrqAssignment(g, gpuMask, null, gpuShared)));
        }

        // 5) NVMe: todas en un núcleo (si no queda, comparte con lo menos importante: el 2.º núcleo de la GPU en el caso típico).
        var nvmes = devices.Where(d => d.Kind == IrqDeviceKind.Nvme).ToList();
        if (nvmes.Count > 0)
        {
            var (nvmeCore, shared) = Place(RankNvme, allowEfficiency: true);
            assignments.AddRange(nvmes.Select(n => new IrqAssignment(n, 1UL << nvmeCore.Threads[0], null, shared)));
        }

        if (assignments.Any(a => a.SharesCore))
        {
            notes.Add("Hay más dispositivos que núcleos libres: algunos comparten núcleo con el menos importante.");
        }
        return new AffinityPlan(assignments, rss, notes);
    }

    private static bool HostsUsbDevice(DeviceTree tree, DeviceNode controller) =>
        tree.Descendants(controller).Any(d => d.IsPresent &&
                                              d.Enumerator.Equals("USB", StringComparison.OrdinalIgnoreCase) &&
                                              !d.InstanceId.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase));

    public static byte[] ToMaskBytes(ulong mask)
    {
        byte[] full = BitConverter.GetBytes(mask); // x64/ARM64: little-endian
        int last = full.Length - 1;
        while (last > 0 && full[last] == 0) last--;
        return full[..(last + 1)];
    }

    public static ulong FromMaskBytes(byte[] bytes)
    {
        ulong mask = 0;
        for (int b = 0; b < Math.Min(8, bytes.Length); b++) mask |= (ulong)bytes[b] << (b * 8);
        return mask;
    }

    private sealed record Core(int Id, List<int> Threads, bool IsEfficiency, int L3Group, int L3SizeKb);
}
