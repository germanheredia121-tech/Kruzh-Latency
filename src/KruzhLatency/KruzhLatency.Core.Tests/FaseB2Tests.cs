using System.Text.Json;
using KruzhLatency.Core.Devices;
using KruzhLatency.Core.Network;

namespace KruzhLatency.Core.Tests;

/// <summary>B2 (fantasmas), B3 (dispositivos con error), B5 (pila de red) y B6 (USB/HID) contra el árbol real del autor.</summary>
public class FaseB2Tests
{
    private static readonly DeviceTree Tree = DeviceTreeTests.AuthorTree();

    private static DeviceNode Node(string id, string? parent, string cls, string enumerator, bool present = true, uint problem = 0,
        string[]? hw = null, string[]? compat = null) =>
        DeviceTreeTests.Node(id, parent, cls, enumerator) with
        {
            IsPresent = present,
            ProblemCode = problem,
            HardwareIds = hw ?? Array.Empty<string>(),
            CompatibleIds = compat ?? Array.Empty<string>()
        };

    // ------------------------------------------------------------ B2

    [Fact]
    public void Fantasmas_DelAutor_SoloLosVirtualesDeRazer()
    {
        var groups = GhostDevicePlanner.Plan(Tree);

        var usb = Assert.Single(groups);
        Assert.Equal(GhostCategory.UsbAndHid, usb.Category);
        Assert.All(usb.Devices, d => Assert.Contains("VID_1532", d.InstanceId)); // Razer, como en el chat
        Assert.All(usb.Devices, d => Assert.False(d.IsPresent));
    }

    [Fact]
    public void Fantasmas_OcultosPorPadreDeshabilitado_NoSeProponen()
    {
        var all = GhostDevicePlanner.Plan(Tree).SelectMany(g => g.Devices).Select(d => d.InstanceId).ToList();

        Assert.DoesNotContain(all, id => id.StartsWith(@"HDAUDIO\FUNC_01&VEN_10DE")); // su controladora está deshabilitada
        Assert.DoesNotContain(all, id => id.StartsWith(@"USB\ROOT_HUB"));
        Assert.DoesNotContain(all, id => id.StartsWith(@"SWD\MSRRAS"));               // WAN miniports del RRAS deshabilitado
        Assert.DoesNotContain(all, id => id.StartsWith(@"SWD\MMDEVAPI"));
    }

    [Fact]
    public void Fantasmas_Categorias_YOrdenHijosPrimero()
    {
        var root = Tree.All.First(n => n.ParentInstanceId is null);
        var usbCtl = Tree.All.First(n => n.IsPresent && n.IsPci && n.ClassName == "USB" && !n.IsDisabled);
        var pad = Node(@"USB\VID_045E&PID_028E\ANON01", usbCtl.InstanceId, "XnaComposite", "USB", present: false);
        var padHid = Node(@"HID\VID_045E&PID_028E&IG_00\9&x&0&0000", pad.InstanceId, "HIDClass", "HID", present: false);
        var stick = Node(@"USBSTOR\Disk&Ven_Kingston\ANON02&0", usbCtl.InstanceId, "DiskDrive", "USBSTOR", present: false);
        var bt = Node(@"BTHENUM\{0000110b}_VID&0001\ANON03", root.InstanceId, "Bluetooth", "BTHENUM", present: false);
        var oldNic = Node(@"PCI\VEN_8086&DEV_15B8\ANON04", root.InstanceId, "Net", "PCI", present: false);
        var monitor = Node(@"DISPLAY\GBT2404\ANON05", root.InstanceId, "Monitor", "DISPLAY", present: false);
        var tree = new DeviceTree(Tree.All.Concat(new[] { pad, padHid, stick, bt, oldNic, monitor }));

        var g = GhostDevicePlanner.Plan(tree).ToDictionary(x => x.Category);

        Assert.Contains(g[GhostCategory.UsbAndHid].Devices, d => d.InstanceId == pad.InstanceId);
        var order = g[GhostCategory.UsbAndHid].Devices.Select(d => d.InstanceId).ToList();
        Assert.True(order.IndexOf(padHid.InstanceId) < order.IndexOf(pad.InstanceId)); // hijo antes que padre
        Assert.Equal(stick.InstanceId, Assert.Single(g[GhostCategory.Storage].Devices).InstanceId);
        Assert.Equal(bt.InstanceId, Assert.Single(g[GhostCategory.Bluetooth].Devices).InstanceId);
        Assert.Equal(oldNic.InstanceId, Assert.Single(g[GhostCategory.Network].Devices).InstanceId);
        Assert.DoesNotContain(g.Values.SelectMany(x => x.Devices), d => d.InstanceId == monitor.InstanceId); // lo maneja C4
    }

    // ------------------------------------------------------------ B3

    [Fact]
    public void Errores_DelAutor_Ninguno()
    {
        Assert.Empty(DeviceIssueAdvisor.Evaluate(Tree, new DeviceSafetyContext()));
    }

    [Fact]
    public void Errores_RaidSinDiscos_YUsbQueFalla_SeDeshabilitan_SinDriverSeLinkea()
    {
        var root = Tree.All.First(n => n.ParentInstanceId is null);
        var usbCtl = Tree.All.First(n => n.IsPresent && n.IsPci && n.ClassName == "USB" && !n.IsDisabled);
        var raid = Node(@"PCI\VEN_1022&DEV_7916\ANON10", root.InstanceId, "", "PCI", problem: 28,
            hw: new[] { @"PCI\VEN_1022&DEV_7916" }, compat: new[] { @"PCI\CC_010400" });
        var unknownUsb = Node(@"USB\VID_0000&PID_0002\ANON11", usbCtl.InstanceId, "USB", "USB", problem: 43,
            hw: new[] { @"USB\DEVICE_DESCRIPTOR_FAILURE" });
        var smbus = Node(@"PCI\VEN_1022&DEV_790B\ANON12", root.InstanceId, "", "PCI", problem: 28,
            hw: new[] { @"PCI\VEN_1022&DEV_790B" }, compat: new[] { @"PCI\CC_0C0500" });
        var tree = new DeviceTree(Tree.All.Concat(new[] { raid, unknownUsb, smbus }));

        var issues = DeviceIssueAdvisor.Evaluate(tree, new DeviceSafetyContext()).ToDictionary(i => i.Device.InstanceId);

        Assert.Equal(DeviceIssueAction.Disable, issues[raid.InstanceId].Action);
        Assert.Equal(DeviceIssueAction.Disable, issues[unknownUsb.InstanceId].Action);
        // SMBus sin driver: está protegido (plataforma) → nunca se deshabilita; se ofrece el driver de chipset de AMD.
        Assert.Equal(DeviceIssueAction.InstallDriver, issues[smbus.InstanceId].Action);
        Assert.Contains("amd.com", issues[smbus.InstanceId].Link);
    }

    // ------------------------------------------------------------ B5

    private static NetworkStackFacts AuthorNetwork(IEnumerable<NetBinding>? extra = null, IEnumerable<string>? apps = null, IEnumerable<NicDriverFact>? drivers = null)
    {
        var bindings = JsonSerializer.Deserialize<List<NetBinding>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "network-bindings-author.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return new NetworkStackFacts(bindings.Concat(extra ?? Array.Empty<NetBinding>()).ToList(),
            (apps ?? Array.Empty<string>()).ToList(),
            (drivers ?? new[] { new NicDriverFact("PCI\\VEN_10EC&DEV_8125\\x", "Realtek PCIe 2.5GbE Family Controller", "rt640x64.sys") }).ToList());
    }

    [Fact]
    public void Red_DelAutor_LimpiaSinAvisos()
    {
        Assert.Empty(NetworkStackAdvisor.Evaluate(AuthorNetwork())); // todo ms_/vms_ y driver NDIS rt640x64
    }

    [Fact]
    public void Red_cFosYProgramas_YDriverCx_SeAvisan()
    {
        var facts = AuthorNetwork(
            extra: new[] { new NetBinding("Ethernet", "cfos_speed", "cFosSpeed Packet Scheduler", true),
                           new NetBinding("Ethernet", "insecure_npcap", "Npcap Packet Driver", false) }, // deshabilitado: no se avisa
            apps: new[] { "MSI Center", "Steam" },
            drivers: new[] { new NicDriverFact("PCI\\VEN_10EC&DEV_8125\\x", "Realtek Gaming 2.5GbE Family Controller", "rtcx21x64.sys") });

        var advice = NetworkStackAdvisor.Evaluate(facts).ToDictionary(a => a.Id);

        Assert.Equal(NetworkAdviceKind.ThirdPartyFilter, advice["NetFilter:cfos_speed"].Kind);
        Assert.Contains("cFosSpeed", advice["NetFilter:cfos_speed"].Title);
        Assert.False(advice.ContainsKey("NetFilter:insecure_npcap"));
        Assert.True(advice.ContainsKey("NetApp:MSI Center"));
        Assert.Equal(NetworkAdviceKind.SwitchDriver, Assert.Single(advice.Values, a => a.Kind == NetworkAdviceKind.SwitchDriver).Kind);
        Assert.Equal(3, advice.Count); // cfos + MSI Center + driver
    }

    // ------------------------------------------------------------ B6

    [Fact]
    public void Usb_DelAutor_MouseYTecladoEnLaControladoraDelCpu()
    {
        var placement = UsbPowerPlanner.InputPlacement(Tree);

        Assert.NotEmpty(placement);
        Assert.All(placement, p => Assert.Equal(UsbControllerLocation.Cpu, p.Location));
        Assert.All(placement, p => Assert.True(p.Controller.HasHardwareIdPrefix(@"PCI\VEN_1022&DEV_149C")));
    }

    [Fact]
    public void Usb_ControladoraDelChipsetB550_SeDetecta()
    {
        var chipsetXhci = Tree.All.First(n => n.IsPresent && n.HasHardwareIdPrefix(@"PCI\VEN_1022&DEV_43EE"));

        Assert.Equal(UsbControllerLocation.Chipset, UsbPowerPlanner.Locate(Tree, chipsetXhci));
    }

    [Fact]
    public void Usb_ObjetivosDeEnergia_SinInterfacesNiDeshabilitados()
    {
        var targets = UsbPowerPlanner.PowerTargets(Tree);

        Assert.NotEmpty(targets);
        Assert.All(targets, t => Assert.DoesNotContain("&MI_", t.InstanceId));
        Assert.All(targets, t => Assert.True(t.IsPresent && !t.IsDisabled));
        Assert.Contains(targets, t => t.InstanceId.StartsWith(@"USB\ROOT_HUB30"));
    }
}
