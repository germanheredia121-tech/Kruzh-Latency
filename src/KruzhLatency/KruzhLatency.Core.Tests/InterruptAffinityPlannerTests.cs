using KruzhLatency.Core.Devices;
using KruzhLatency.Core.Hardware;
using KruzhLatency.Core.Latency;
using KruzhLatency.Core.Network;

namespace KruzhLatency.Core.Tests;

/// <summary>E7 + E2 + E8: reparto de interrupciones contra la topología y el árbol reales del autor y CPUs sintéticas.</summary>
public class InterruptAffinityPlannerTests
{
    private static string F(string n) => Path.Combine(AppContext.BaseDirectory, "Fixtures", n);

    private static IReadOnlyList<LogicalProcessor> Ryzen5600X() =>
        CpuTopologyParser.Parse(Convert.FromBase64String(File.ReadAllText(F("topology-ryzen5600x-core.b64"))),
                                Convert.FromBase64String(File.ReadAllText(F("topology-ryzen5600x-cache.b64"))));

    private static DeviceTree AuthorTree() => DeviceTree.FromJson(File.ReadAllText(F("devices-author-5600x-rtx2060s.json")));

    /// <summary>P-cores con SMT (2 hilos) salvo que se indique; los E-cores sin SMT, al final.</summary>
    private static List<LogicalProcessor> Topology(int pCores, int eCores = 0, bool smt = true, int ccds = 1, int[]? l3Kb = null)
    {
        var list = new List<LogicalProcessor>();
        int thread = 0, core = 0;
        int perCcd = pCores / ccds;
        for (int c = 0; c < pCores; c++, core++)
        {
            int ccd = c / perCcd;
            int size = l3Kb?[ccd] ?? 32768;
            list.Add(new LogicalProcessor(thread++, core, false, false, ccd, size));
            if (smt) list.Add(new LogicalProcessor(thread++, core, false, true, ccd, size));
        }
        for (int e = 0; e < eCores; e++, core++)
        {
            list.Add(new LogicalProcessor(thread++, core, true, false, 0, 32768));
        }
        return list;
    }

    private static List<IrqDevice> Devices(int usb = 2, bool nvme = true)
    {
        var list = Enumerable.Range(0, usb)
            .Select(i => new IrqDevice($@"PCI\USB{i}", $"xHCI {i}", IrqDeviceKind.UsbController, HostsInput: i == 0))
            .ToList();
        list.Add(new IrqDevice(@"PCI\GPU", "GPU", IrqDeviceKind.Gpu));
        list.Add(new IrqDevice(@"PCI\NIC", "NIC", IrqDeviceKind.Nic));
        if (nvme) list.Add(new IrqDevice(@"PCI\NVME", "NVMe", IrqDeviceKind.Nvme));
        return list;
    }

    private static IrqAssignment Of(AffinityPlan p, IrqDeviceKind k, int i = 0) => p.Assignments.Where(a => a.Device.Kind == k).ElementAt(i);

    // ------------------------------------------------------------ PC real del autor

    [Fact]
    public void Collect_PcDelAutor_SinElUsbCDeLaPlacaDeVideo()
    {
        var devs = InterruptAffinityPlanner.Collect(AuthorTree());

        Assert.Equal(2, devs.Count(d => d.Kind == IrqDeviceKind.UsbController)); // la xHCI de la RTX (10DE:1ADA) no tiene nada conectado
        Assert.DoesNotContain(devs, d => d.InstanceId.Contains("DEV_1ADA"));
        Assert.True(devs.Single(d => d.InstanceId.Contains("DEV_149C")).HostsInput); // mouse/teclado en la xHCI del CPU
        Assert.Single(devs, d => d.Kind == IrqDeviceKind.Gpu);
        Assert.Single(devs, d => d.Kind == IrqDeviceKind.Nvme);
    }

    [Fact]
    public void Ryzen5600X_ComoEnElChat_Rss2a3_Usb4y6_Gpu8y10_NvmeComparte10()
    {
        var plan = InterruptAffinityPlanner.Plan(Ryzen5600X(), InterruptAffinityPlanner.Collect(AuthorTree()), gpuTwoCores: true)!;

        Assert.Equal(new RssPlacement(2, 3, 2), plan.Rss);
        var usb = plan.Assignments.Where(a => a.Device.Kind == IrqDeviceKind.UsbController).ToList();
        Assert.Equal(new[] { 4 }, usb[0].Threads);
        Assert.Contains("DEV_149C", usb[0].Device.InstanceId); // la del mouse primero
        Assert.Equal(new[] { 6 }, usb[1].Threads);
        Assert.All(usb, u => Assert.Equal(3, u.DevicePriority));
        var gpu = Of(plan, IrqDeviceKind.Gpu);
        Assert.Equal(new[] { 8, 10 }, gpu.Threads);
        Assert.Equal(new byte[] { 0x00, 0x05 }, gpu.MaskBytes); // máscara "00 05" del chat
        Assert.Null(gpu.DevicePriority);
        var nvme = Of(plan, IrqDeviceKind.Nvme);
        Assert.Equal(new[] { 10 }, nvme.Threads);
        Assert.True(nvme.SharesCore);
        Assert.Null(Of(plan, IrqDeviceKind.Nic).Mask); // la red va por RSS
    }

    [Fact]
    public void Ryzen5600X_GpuUnNucleo_NvmeTieneElSuyo()
    {
        var plan = InterruptAffinityPlanner.Plan(Ryzen5600X(), InterruptAffinityPlanner.Collect(AuthorTree()), gpuTwoCores: false)!;
        Assert.Equal(new[] { 8 }, Of(plan, IrqDeviceKind.Gpu).Threads);
        Assert.Equal(new[] { 10 }, Of(plan, IrqDeviceKind.Nvme).Threads);
        Assert.DoesNotContain(plan.Assignments, a => a.SharesCore);
    }

    // ------------------------------------------------------------ reglas generales

    public static TheoryData<string> Cpus => new() { "5600X", "13700K", "7950X3D", "4c", "4c-smt" };

    private static List<LogicalProcessor> Cpu(string name) => name switch
    {
        "5600X" => Ryzen5600X().ToList(),
        "13700K" => Topology(8, eCores: 8),
        "7950X3D" => Topology(16, ccds: 2, l3Kb: new[] { 98304, 32768 }),
        "4c" => Topology(4, smt: false),
        _ => Topology(4),
    };

    [Theory]
    [MemberData(nameof(Cpus))]
    public void NadaEnCpu0_NiEnHilosSmt_NiGpuUsbEnECores(string cpu)
    {
        var topo = Cpu(cpu);
        int kernelCore = topo.Single(x => x.Index == 0).PhysicalCoreIndex;
        int CoreOf(int thread) => topo.Single(x => x.Index == thread).PhysicalCoreIndex;

        foreach (bool two in new[] { true, false })
        {
            var plan = InterruptAffinityPlanner.Plan(topo, Devices(), two)!;
            var threads = plan.Assignments.SelectMany(a => a.Threads).ToList();

            Assert.All(threads, t => Assert.NotEqual(kernelCore, CoreOf(t)));
            Assert.All(threads, t => Assert.False(topo.Single(x => x.Index == t).IsSmtSibling));
            Assert.NotEqual(kernelCore, CoreOf(plan.Rss!.BaseProcessor));
            foreach (var a in plan.Assignments.Where(a => a.Device.Kind is IrqDeviceKind.Gpu or IrqDeviceKind.UsbController))
            {
                Assert.All(a.Threads, t => Assert.False(topo.Single(x => x.Index == t).IsEfficiencyCore));
            }
        }
    }

    [Theory]
    [InlineData("5600X")]
    [InlineData("13700K")]
    [InlineData("7950X3D")]
    public void ConNucleosDeSobra_RssNoPisaUsbNiGpu(string cpu)
    {
        var topo = Cpu(cpu);
        int CoreOf(int thread) => topo.Single(x => x.Index == thread).PhysicalCoreIndex;
        var plan = InterruptAffinityPlanner.Plan(topo, Devices(), gpuTwoCores: true)!;

        var rssCore = CoreOf(plan.Rss!.BaseProcessor);
        Assert.Equal(rssCore, CoreOf(plan.Rss.MaxProcessor));
        Assert.DoesNotContain(plan.Assignments.Where(a => a.Device.Kind is IrqDeviceKind.Gpu or IrqDeviceKind.UsbController)
                                              .SelectMany(a => a.Threads), t => CoreOf(t) == rssCore);
        Assert.DoesNotContain(plan.Assignments, a => a.SharesCore && a.Device.Kind != IrqDeviceKind.Nvme);
    }

    [Fact]
    public void X3D_UsaElCcdConVCache()
    {
        // V-Cache en el CCD 1 (al revés de lo habitual) para comprobar que se elige por tamaño de L3.
        var topo = Topology(16, ccds: 2, l3Kb: new[] { 32768, 98304 });
        var plan = InterruptAffinityPlanner.Plan(topo, Devices(), gpuTwoCores: true)!;
        Assert.All(plan.Assignments.SelectMany(a => a.Threads), t => Assert.Equal(1, topo.Single(x => x.Index == t).L3GroupIndex));
        Assert.Contains(plan.Notes, n => n.Contains("V-Cache"));
    }

    [Fact]
    public void PocosPCores_NvmeVaAUnECore_GpuNunca()
    {
        var plan = InterruptAffinityPlanner.Plan(Topology(4, eCores: 8), Devices(usb: 2), gpuTwoCores: true)!;
        // 4 P-cores (hilos 0-7): kernel, red, USB, USB → la GPU comparte un P-core y el NVMe usa un E-core.
        Assert.True(Of(plan, IrqDeviceKind.Gpu).SharesCore);
        Assert.All(Of(plan, IrqDeviceKind.Gpu).Threads, t => Assert.True(t < 8));
        Assert.False(Of(plan, IrqDeviceKind.Nvme).SharesCore);
        Assert.True(Of(plan, IrqDeviceKind.Nvme).Threads[0] >= 8);
    }

    [Fact]
    public void CuatroNucleos_ComparteConElMenosImportante_NuncaConElMouse()
    {
        var plan = InterruptAffinityPlanner.Plan(Topology(4, smt: false), Devices(usb: 2), gpuTwoCores: true)!;
        // CPU 1 red, CPU 2 USB del mouse, CPU 3 USB 2 → la GPU comparte con la red (lo menos importante).
        Assert.Equal(new[] { 2 }, Of(plan, IrqDeviceKind.UsbController, 0).Threads);
        Assert.Equal(new[] { 1 }, Of(plan, IrqDeviceKind.Gpu).Threads);
        Assert.Equal(new RssPlacement(1, 1, 1), plan.Rss);
        Assert.NotEmpty(plan.Notes);
    }

    [Fact]
    public void MenosDe4Nucleos_SinPlan() =>
        Assert.Null(InterruptAffinityPlanner.Plan(Topology(3), Devices(), true));

    [Fact]
    public void Mascara_IdaYVuelta()
    {
        Assert.Equal(new byte[] { 0x10 }, InterruptAffinityPlanner.ToMaskBytes(1UL << 4));
        Assert.Equal(0x500UL, InterruptAffinityPlanner.FromMaskBytes(new byte[] { 0x00, 0x05 }));
    }

    // ------------------------------------------------------------ E2: RSS en la placa

    [Fact]
    public void Rtl8125_RssEnElNucleoDeLaRed_AunqueElDriverNoDeclareLosKeywords()
    {
        var nic = NicAdvancedProperties.FromJson(File.ReadAllText(F("ndi-params-rtl8125.json")));
        var c = NicTuningPlanner.Plan(nic, 6, new RssPlacement(2, 3, 2))!.Changes.ToDictionary(x => x.Keyword);

        Assert.Equal("2", c["*RssBaseProcNumber"].Target);
        Assert.Equal("3", c["*RssMaxProcNumber"].Target);
        Assert.Equal("2", c["*MaxRssProcessors"].Target);
        Assert.Equal(NicTuningPlanner.RssProfileClosestStatic, c["*RSSProfile"].Target);
        Assert.Equal("2", c["*NumRssQueues"].Target);
        Assert.DoesNotContain("*RssBaseProcNumber", NicTuningPlanner.Plan(nic, 6)!.Changes.Select(x => x.Keyword)); // sin plan, no se fija
    }

    // ------------------------------------------------------------ E8: MSI

    [Fact]
    public void Msi_PcDelAutor_AudioHdYUsbIncluidos_SoloConSoporteDeclarado()
    {
        var targets = MsiPlanner.Targets(AuthorTree());

        Assert.Equal(2, targets.Count(t => t.Kind == "USB xHCI"));
        Assert.Contains(targets, t => t.Kind == "Audio HD");
        Assert.All(targets, t => Assert.True(t.Device.Interrupts!.SupportsMsi || t.Device.Interrupts.SupportsMsiX));
        Assert.All(targets, t => Assert.Equal(t.Device.Interrupts!.SupportsMsiX, t.RemoveMessageLimit));
        Assert.False(targets.Single(t => t.Kind == "GPU").RemoveMessageLimit); // la RTX es MSI, no MSI-X
        Assert.DoesNotContain(targets, t => t.Device.CompatibleIds.Any(c => c.StartsWith(@"PCI\CC_0604", StringComparison.OrdinalIgnoreCase)));
    }
}
