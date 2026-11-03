using KruzhLatency.Core.Devices;
using KruzhLatency.Core.Tweaks;

namespace KruzhLatency.Core.Tests;

/// <summary>B1: el motor detecta en el árbol real del autor lo que en el chat se deshabilitó a mano.</summary>
public class DeviceRuleEngineTests
{
    private static readonly DeviceTree Tree = DeviceTreeTests.AuthorTree();

    private static DeviceNode First(Func<DeviceNode, bool> f) => Tree.All.First(n => n.IsPresent && f(n));
    private static DeviceNode Gpu => First(n => n.IsPci && n.ClassName == "Display");
    private static DeviceNode Rtl8125 => First(n => n.HasHardwareIdPrefix(@"PCI\VEN_10EC&DEV_8125"));
    private static DeviceNode Rtl8111 => First(n => n.HasHardwareIdPrefix(@"PCI\VEN_10EC&DEV_8168"));
    private static DeviceNode Speakers => First(n => n.ClassName == "AudioEndpoint");

    private static DeviceRuleContext AuthorContext(string? defaultAudio = null, IReadOnlyCollection<string>? wireless = null) => new(
        new DeviceSafetyContext(
            ActiveGpuInstanceId: Gpu.InstanceId,
            LinkedNicInstanceIds: new[] { Rtl8125.InstanceId },
            DefaultAudioEndpointInstanceId: defaultAudio ?? Speakers.InstanceId),
        wireless);

    private static Dictionary<string, DeviceRecommendation> Run(DeviceTree tree, DeviceRuleContext ctx) =>
        DeviceRuleEngine.Evaluate(tree, ctx).ToDictionary(r => r.RuleId);

    [Fact]
    public void ArbolDelAutor_DetectaLoDelChat()
    {
        var recs = Run(Tree, AuthorContext());

        foreach (var id in new[] { "KernelDebugNic", "CompositeBus", "NdisVirtualBus", "RdpBus", "UmBus", "VirtualDrive", "RrasRoot",
                                   "GpuHdmiAudio", "GpuUsbC", "SecondNic", "SystemSpeaker", "Hpet", "GsWavetable", "VoiceClarity", "PrintQueues" })
        {
            Assert.True(recs.ContainsKey(id), $"falta la regla {id}");
        }

        // Los 3 hermanos de la RTX 2060 SUPER (fn1 audio, fn2 xHCI, fn3 UCSI), como en el chat.
        var gpuSiblings = recs["GpuHdmiAudio"].Devices.Concat(recs["GpuUsbC"].Devices).Select(d => d.PciFunction).OrderBy(f => f);
        Assert.Equal(new uint?[] { 1, 2, 3 }, gpuSiblings);
        Assert.Equal(Rtl8111.InstanceId, Assert.Single(recs["SecondNic"].Devices).InstanceId);
    }

    [Fact]
    public void NingunaRecomendacion_IncluyeAlgoProtegido()
    {
        var ctx = AuthorContext();
        var prot = NeverTouchPolicy.Evaluate(Tree, ctx.Safety);

        var all = DeviceRuleEngine.Evaluate(Tree, ctx).SelectMany(r => r.Devices).ToList();
        Assert.NotEmpty(all);
        Assert.All(all, d => Assert.False(prot.ContainsKey(d.InstanceId), d.DisplayName));
        Assert.All(all, d => Assert.True(d.IsPresent));
        Assert.DoesNotContain(all, d => d.IsPci && d.CompatibleIds.Any(c => c.StartsWith(@"PCI\CC_06"))); // nunca un bridge
    }

    [Fact]
    public void AudioHdmiComoSalidaPredeterminada_NoSeDeshabilita()
    {
        var hdmiEndpoint = Tree.All.First(n => n.ClassName == "AudioEndpoint" &&
                                               Tree.Ancestors(n).Any(a => a.HasHardwareIdPrefix(@"HDAUDIO\FUNC_01&VEN_10DE")));
        var recs = Run(Tree, AuthorContext(defaultAudio: hdmiEndpoint.InstanceId));

        Assert.False(recs.ContainsKey("GpuHdmiAudio"));
    }

    [Fact]
    public void SinDatosDeEnlace_NoSeProponeNadaDeRed()
    {
        var recs = Run(Tree, new DeviceRuleContext(new DeviceSafetyContext(ActiveGpuInstanceId: Gpu.InstanceId)));

        Assert.False(recs.ContainsKey("SecondNic"));
        Assert.False(recs.ContainsKey("WifiWithEthernet"));
    }

    [Fact]
    public void WifiConCableConectado_SeProponeLaWifi_NoComoSegundaNic()
    {
        var recs = Run(Tree, AuthorContext(wireless: new[] { Rtl8111.InstanceId })); // se simula que la 8111 es Wi-Fi

        Assert.Equal(Rtl8111.InstanceId, Assert.Single(recs["WifiWithEthernet"].Devices).InstanceId);
        Assert.False(recs.ContainsKey("SecondNic"));
    }

    [Fact]
    public void ConImpresorasReales_NoSeTocanLasColas()
    {
        var ctx = AuthorContext() with { HasRealPrinters = true };

        Assert.False(Run(Tree, ctx).ContainsKey("PrintQueues"));
    }

    [Fact]
    public void JoystickVirtualDelTeclado_SeDeshabilita_UnGamepadRealNo()
    {
        // Nodo XInput del Huntsman (mismo VID/PID que el teclado) + un control de Xbox real enchufado.
        var keyboardUsb = Tree.All.First(n => n.IsPresent && n.InstanceId.StartsWith(@"USB\VID_1532&PID_02B0&MI_01"));
        var keyboardPad = DeviceTreeTests.Node(@"HID\VID_1532&PID_02B0&MI_03&IG_00\9&aaaa&0&0000", keyboardUsb.InstanceId, "HIDClass", "HID") with
        {
            HardwareIds = new[] { @"HID\VID_1532&PID_02B0&MI_03&IG_00", "HID_DEVICE_UP:0001_U:0005", "HID_DEVICE" }
        };
        var xboxPad = DeviceTreeTests.Node(@"USB\VID_045E&PID_028E\ANON0001", keyboardUsb.ParentInstanceId, "XnaComposite", "USB") with
        {
            HardwareIds = new[] { @"USB\VID_045E&PID_028E" },
            CompatibleIds = new[] { @"USB\MS_COMP_XUSB10" }
        };
        var tree = new DeviceTree(Tree.All.Append(keyboardPad).Append(xboxPad));

        var rec = Run(tree, AuthorContext())["KeyboardGameControllers"];

        Assert.Equal(keyboardPad.InstanceId, Assert.Single(rec.Devices).InstanceId);
        Assert.Equal(Profile.Competitivo, rec.From);
    }

    [Fact]
    public void UsbCDeLaGpuConAlgoEnchufado_NoSeToca()
    {
        var xhci = Tree.PciSiblings(Gpu).Single(s => s.PciFunction == 2);
        var hub = Tree.Descendants(xhci).FirstOrDefault(d => d.InstanceId.StartsWith(@"USB\ROOT_HUB")) ?? xhci;
        var device = DeviceTreeTests.Node(@"USB\VID_0BDA&PID_8153\ANON0002", hub.InstanceId, "Net", "USB");
        var tree = new DeviceTree(Tree.All.Append(device));

        Assert.False(Run(tree, AuthorContext()).ContainsKey("GpuUsbC"));
    }
}
