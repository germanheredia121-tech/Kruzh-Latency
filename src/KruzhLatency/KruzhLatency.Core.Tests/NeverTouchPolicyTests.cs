using KruzhLatency.Core.Devices;

namespace KruzhLatency.Core.Tests;

/// <summary>ADR-007 contra el árbol real del autor: nada crítico puede salir como "deshabilitable".</summary>
public class NeverTouchPolicyTests
{
    private static readonly DeviceTree Tree = DeviceTreeTests.AuthorTree();

    private static DeviceNode ByHwId(string prefix) =>
        Tree.All.First(n => n.IsPresent && n.HasHardwareIdPrefix(prefix));

    private static DeviceNode Gpu => Tree.All.Single(n => n.IsPresent && n.IsPci && n.ClassName == "Display");
    private static DeviceNode Rtl8125 => ByHwId(@"PCI\VEN_10EC&DEV_8125");
    private static DeviceNode Rtl8111 => ByHwId(@"PCI\VEN_10EC&DEV_8168");
    private static DeviceNode Speakers => Tree.All.First(n => n.IsPresent && n.ClassName == "AudioEndpoint");

    private static DeviceSafetyContext AuthorContext => new(
        ActiveGpuInstanceId: Gpu.InstanceId,
        BootDiskInstanceId: Tree.All.First(n => n.IsPresent && n.ClassName == "DiskDrive").InstanceId,
        LinkedNicInstanceIds: new[] { Rtl8125.InstanceId },
        DefaultAudioEndpointInstanceId: Speakers.InstanceId);

    private static IReadOnlyDictionary<string, DeviceProtection> Eval() => NeverTouchPolicy.Evaluate(Tree, AuthorContext);

    [Fact]
    public void TodosLosBridgesPci_Protegidos_ElErrorDelChat()
    {
        var eval = Eval();
        var bridges = Tree.All.Where(n => n.IsPci && n.CompatibleIds.Any(c => c.StartsWith(@"PCI\CC_06"))).ToList();

        Assert.True(bridges.Count >= 20);
        Assert.All(bridges, b => Assert.Equal(ProtectionReason.PciBridge, eval[b.InstanceId].Reason));
    }

    [Fact]
    public void GpuActivaYSuCadena_Protegidas_PeroSusFuncionesHermanasNo()
    {
        var eval = Eval();

        Assert.True(eval.ContainsKey(Gpu.InstanceId));
        Assert.All(Tree.Ancestors(Gpu), a => Assert.True(eval.ContainsKey(a.InstanceId)));
        // Audio HDMI (fn1), xHCI USB-C (fn2) y UCSI (fn3) los decide B1, no esta lista.
        Assert.All(Tree.PciSiblings(Gpu), s => Assert.False(eval.ContainsKey(s.InstanceId), s.DisplayName));
    }

    [Fact]
    public void ControladorasUsbDelMouseYTeclado_Protegidas_LaUsbDeLaGpuNo()
    {
        var eval = Eval();
        var inputControllers = Tree.All
            .Where(n => n.IsPresent && (n.ClassName == "Mouse" || n.ClassName == "Keyboard"))
            .SelectMany(Tree.Ancestors)
            .Where(a => a.IsPci && a.ClassName == "USB")
            .Distinct()
            .ToList();

        Assert.NotEmpty(inputControllers);
        Assert.All(inputControllers, c => Assert.True(eval.ContainsKey(c.InstanceId)));
        var nvidiaXhci = Tree.PciSiblings(Gpu).Single(s => s.ClassName == "USB" && s.PciFunction == 2);
        Assert.DoesNotContain(nvidiaXhci, inputControllers);
        Assert.False(eval.ContainsKey(nvidiaXhci.InstanceId));
    }

    [Fact]
    public void NicConEnlace_Protegida_LaSegundaNicNo()
    {
        var eval = Eval();

        Assert.Equal(ProtectionReason.LinkedNic, eval[Rtl8125.InstanceId].Reason);
        Assert.False(eval.ContainsKey(Rtl8111.InstanceId));
    }

    [Fact]
    public void AudioPredeterminado_ProtegeCodecYControladora_ElAudioDeLaGpuNo()
    {
        var eval = Eval();
        var chain = Tree.Ancestors(Speakers).ToList();

        Assert.True(eval.ContainsKey(Speakers.InstanceId));
        Assert.All(chain, a => Assert.True(eval.ContainsKey(a.InstanceId)));
        Assert.Contains(chain, a => a.IsPci && a.HasHardwareIdPrefix(@"PCI\VEN_1022&DEV_1487")); // HDA de la placa
        var nvidiaHdaudio = Tree.All.First(n => n.HasHardwareIdPrefix(@"HDAUDIO\FUNC_01&VEN_10DE"));
        Assert.False(eval.ContainsKey(nvidiaHdaudio.InstanceId));
    }

    [Fact]
    public void Almacenamiento_TpmPspProcesadoresYDisplayDeRespaldo_Protegidos()
    {
        var eval = Eval();

        Assert.True(eval.ContainsKey(Tree.All.First(n => n.IsPresent && n.ClassName == "SCSIAdapter" && n.IsPci).InstanceId)); // NVMe
        Assert.Equal(ProtectionReason.SecurityDevice, eval[Tree.All.First(n => n.ClassName == "SecurityDevices").InstanceId].Reason);
        Assert.Equal(ProtectionReason.SecurityDevice, eval[Tree.All.First(n => n.CompatibleIds.Any(c => c.StartsWith(@"PCI\CC_1080"))).InstanceId].Reason);
        Assert.All(Tree.All.Where(n => n.ClassName == "Processor"), p => Assert.True(eval.ContainsKey(p.InstanceId)));
        Assert.Equal(ProtectionReason.FallbackDisplay, eval[ByHwId(@"ROOT\BasicDisplay").InstanceId].Reason);
        Assert.Equal(ProtectionReason.TreeRoot, eval[@"HTREE\ROOT\0"].Reason);
    }

    [Fact]
    public void LoQueElChatDeshabilita_NoQuedaBloqueado()
    {
        var eval = Eval();

        Assert.False(eval.ContainsKey(ByHwId(@"ACPI\VEN_PNP&DEV_0103").InstanceId)); // HPET
        Assert.All(Tree.All.Where(n => n.IsPresent && n.Enumerator == "SWD" &&
                                       (n.ClassName == "SoftwareDevice" || n.ClassName == "PrintQueue" || n.ClassName == "AudioProcessingObject")),
            n => Assert.False(eval.ContainsKey(n.InstanceId), n.DisplayName)); // Wavetable, RRAS, Print Queue, Voice Clarity
    }

    [Fact]
    public void SinContexto_SeProtegenTodasLasGpu()
    {
        var eval = NeverTouchPolicy.Evaluate(Tree, new DeviceSafetyContext());

        Assert.Equal(ProtectionReason.ActiveGpu, eval[Gpu.InstanceId].Reason);
        Assert.False(eval.ContainsKey(Rtl8125.InstanceId)); // sin dato de enlace no se inventa protección de red
    }

    [Fact]
    public void ServiciosDeDisplay_EnLaListaProtegida()
    {
        Assert.Contains("NVDisplay.ContainerLocalSystem", NeverTouchPolicy.ProtectedServices);
    }
}
