using KruzhLatency.Core.Devices;
using KruzhLatency.Core.Display;

namespace KruzhLatency.Core.Tests;

public class SetupAdvisorTests
{
    // ---------------------------------------------------------------- árbol sintético (Ryzen: xHCI del CPU y del chipset)

    private static DeviceNode N(string id, string? parent, string cls, string en, string? svc = null, string[]? hw = null, string[]? compat = null,
                                uint? bus = null, uint? addr = null) =>
        DeviceTreeTests.Node(id, parent, cls, en) with
        {
            Service = svc,
            HardwareIds = hw ?? Array.Empty<string>(),
            CompatibleIds = compat ?? Array.Empty<string>(),
            BusNumber = bus,
            Address = addr,
        };

    private static DeviceNode Bridge(string id, string parent, uint bus, uint addr) =>
        N(id, parent, "System", "PCI", hw: new[] { @"PCI\VEN_1022&DEV_1483" }, compat: new[] { @"PCI\CC_060400" }, bus: bus, addr: addr);

    private static DeviceNode Xhci(string id, string parent, string hwId, string cc = "0C0330", uint bus = 0, uint addr = 0) =>
        N(id, parent, "USB", "PCI", "USBXHCI", new[] { hwId }, new[] { @"PCI\CC_" + cc }, bus, addr);

    /// <summary>Raíz → bridge del CPU → xHCI CPU (bus 5). Raíz → bridge → bridge (chipset) → xHCI chipset (bus 2).</summary>
    private static List<DeviceNode> Base() => new()
    {
        DeviceTreeTests.Node(@"HTREE\ROOT\0", null),
        Bridge(@"PCI\BR_CPU", @"HTREE\ROOT\0", 0, 0x00080001),
        Xhci(@"PCI\XHCI_CPU", @"PCI\BR_CPU", @"PCI\VEN_1022&DEV_149C", bus: 5, addr: 0x00000003),
        Bridge(@"PCI\BR_ROOT", @"HTREE\ROOT\0", 0, 0x00010002),
        Bridge(@"PCI\BR_CHIPSET", @"PCI\BR_ROOT", 1, 0x00000000),
        Xhci(@"PCI\XHCI_CHIPSET", @"PCI\BR_CHIPSET", @"PCI\VEN_1022&DEV_43EE", bus: 2, addr: 0x00000000),
        N(@"USB\ROOT_HUB30\CPU", @"PCI\XHCI_CPU", "USB", "USB", "USBHUB3"),
        N(@"USB\ROOT_HUB30\CHIP", @"PCI\XHCI_CHIPSET", "USB", "USB", "USBHUB3"),
    };

    private static IEnumerable<DeviceNode> Mouse(string hubParent, string tag = "M") => new[]
    {
        N($@"USB\VID_046D&PID_C54D\{tag}", hubParent, "USB", "USB", "usbccgp"),
        N($@"USB\VID_046D&PID_C54D&MI_00\{tag}", $@"USB\VID_046D&PID_C54D\{tag}", "HIDClass", "USB", "HidUsb"),
        N($@"HID\VID_046D&PID_C54D&MI_00\{tag}", $@"USB\VID_046D&PID_C54D&MI_00\{tag}", "Mouse", "HID", "mouhid"),
    };

    private static IEnumerable<DeviceNode> Keyboard(string hubParent) => new[]
    {
        N(@"USB\VID_1532&PID_02B0\K", hubParent, "USB", "USB", "usbccgp"),
        N(@"USB\VID_1532&PID_02B0&MI_00\K", @"USB\VID_1532&PID_02B0\K", "HIDClass", "USB", "HidUsb"),
        N(@"HID\VID_1532&PID_02B0&MI_00\K", @"USB\VID_1532&PID_02B0&MI_00\K", "Keyboard", "HID", "kbdhid"),
    };

    private static DeviceTree Tree(params IEnumerable<DeviceNode>[] extra) => new(Base().Concat(extra.SelectMany(e => e)));

    // ---------------------------------------------------------------- USB

    [Fact]
    public void MouseEnElCpu_TecladoEnElChipset_SinAvisos() =>
        Assert.Empty(SetupAdvisor.Inputs(Tree(Mouse(@"USB\ROOT_HUB30\CPU"), Keyboard(@"USB\ROOT_HUB30\CHIP"))));

    [Fact]
    public void MouseEnElChipset_Avisa()
    {
        var w = SetupAdvisor.Inputs(Tree(Mouse(@"USB\ROOT_HUB30\CHIP")));
        Assert.Contains(w, x => x.Title.Contains("chipset"));
    }

    [Fact]
    public void MouseDetrasDeUnHub_Avisa()
    {
        var hub = new[] { N(@"USB\VID_05E3&PID_0608\H", @"USB\ROOT_HUB30\CPU", "USB", "USB", "USBHUB3", compat: new[] { @"USB\Class_09" }) };
        var w = SetupAdvisor.Inputs(Tree(hub, Mouse(@"USB\VID_05E3&PID_0608\H")));
        Assert.Contains(w, x => x.Title.Contains("hub"));
        Assert.DoesNotContain(w, x => x.Title.Contains("chipset"));
    }

    [Fact]
    public void TecladoYMouseEnLaMismaControladora_Avisa()
    {
        var w = SetupAdvisor.Inputs(Tree(Mouse(@"USB\ROOT_HUB30\CPU"), Keyboard(@"USB\ROOT_HUB30\CPU")));
        Assert.Contains(w, x => x.Title.Contains("misma controladora"));
    }

    [Fact]
    public void ReceptorConTecladoYMouseIntegrados_NoCuentaComoTecladoAparte()
    {
        // El receptor Logitech expone un "teclado" en MI_01: es el mismo aparato, no un teclado compartiendo controladora.
        var kbdInReceiver = new[]
        {
            N(@"USB\VID_046D&PID_C54D&MI_01\M", @"USB\VID_046D&PID_C54D\M", "HIDClass", "USB", "HidUsb"),
            N(@"HID\VID_046D&PID_C54D&MI_01\M", @"USB\VID_046D&PID_C54D&MI_01\M", "Keyboard", "HID", "kbdhid"),
        };
        Assert.Empty(SetupAdvisor.Inputs(Tree(Mouse(@"USB\ROOT_HUB30\CPU"), kbdInReceiver)));
    }

    [Fact]
    public void ControladoraUsb2Antigua_Avisa()
    {
        var ehci = new[]
        {
            Xhci(@"PCI\EHCI", @"PCI\BR_CPU", @"PCI\VEN_8086&DEV_1C26", cc: "0C0320", bus: 5, addr: 0x00000001),
            N(@"USB\ROOT_HUB20\E", @"PCI\EHCI", "USB", "USB", "usbhub"),
        };
        var w = SetupAdvisor.Inputs(Tree(ehci, Mouse(@"USB\ROOT_HUB20\E")));
        Assert.Contains(w, x => x.Title.Contains("USB 2.0"));
    }

    // ---------------------------------------------------------------- EDID

    private static MonitorAnalysis Mon(string id, bool present, byte[]? edid = null, byte[]? over = null, string? error = null) =>
        new(new MonitorInfo(id, "GS25F2", present, null, edid, over, null), null, error, null, 0, false, false, false, Array.Empty<string>());

    [Fact]
    public void MonitorLimpio_SinAvisos() =>
        Assert.Empty(SetupAdvisor.Displays(new[] { Mon(@"DISPLAY\GSM5B7F\1", true, new byte[] { 1 }) }));

    [Fact]
    public void MismoModeloRegistradoDosVeces_AvisaDobleEdid()
    {
        var w = SetupAdvisor.Displays(new[]
        {
            Mon(@"DISPLAY\GSM5B7F\1", true, new byte[] { 1 }),
            Mon(@"DISPLAY\GSM5B7F\2", false, new byte[] { 2 }),
        });
        var dup = Assert.Single(w);
        Assert.Contains("duplicado", dup.Title);
        Assert.Contains("EDID distintos", dup.Detail);
        Assert.Contains("CRU", dup.Action);
    }

    [Fact]
    public void FantasmaDelDriverGenericoDeWindows_NoEsDobleEdid()
    {
        // Lo que se ve en la PC del autor: DISPLAY\GBT2404\1&...&UID0 colgando de ROOT\BasicDisplay\0000, recreado al reiniciar el driver.
        var basic = new MonitorAnalysis(new MonitorInfo(@"DISPLAY\GSM5B7F\2", "GS25F2", false, @"ROOT\BasicDisplay\0000", new byte[] { 2 }, null, null),
                                        null, null, null, 0, false, false, false, Array.Empty<string>());
        Assert.Empty(SetupAdvisor.Displays(new[] { Mon(@"DISPLAY\GSM5B7F\1", true, new byte[] { 1 }), basic }));
    }

    [Fact]
    public void FantasmaDeOtroModelo_NoEsDobleEdid() =>
        Assert.Empty(SetupAdvisor.Displays(new[]
        {
            Mon(@"DISPLAY\GSM5B7F\1", true, new byte[] { 1 }),
            Mon(@"DISPLAY\AOC2590\1", false, new byte[] { 2 }),
        }));

    [Fact]
    public void EdidIlegibleYOverride_Avisan()
    {
        Assert.Contains("raro", Assert.Single(SetupAdvisor.Displays(new[] { Mon(@"DISPLAY\A\1", true, error: "checksum") })).Title);
        Assert.Contains("modificado", Assert.Single(SetupAdvisor.Displays(new[] { Mon(@"DISPLAY\A\1", true, new byte[] { 1 }, over: new byte[] { 9 }) })).Title);
    }
}
