using KruzhLatency.Core.Devices;

namespace KruzhLatency.Core.Tests;

/// <summary>Árbol PnP real del autor (Ryzen 5600X + RTX 2060 SUPER + RTL8125/RTL8111), exportado con CfgMgr32 y anonimizado.</summary>
public class DeviceTreeTests
{
    internal static DeviceTree AuthorTree() =>
        DeviceTree.FromJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "devices-author-5600x-rtx2060s.json")));

    [Fact]
    public void Fixture_CargaCompleto_ConRaizUnicaYSinPadresHuerfanos()
    {
        var tree = AuthorTree();

        Assert.True(tree.All.Count > 150);
        var root = Assert.Single(tree.All, n => n.ParentInstanceId is null);
        Assert.Equal(@"HTREE\ROOT\0", root.InstanceId);
        Assert.All(tree.All.Where(n => n.ParentInstanceId is not null), n => Assert.NotNull(tree.Parent(n)));
        Assert.Contains(tree.All, n => !n.IsPresent); // fantasmas incluidos
    }

    [Fact]
    public void GpuActiva_TieneSusFuncionesHermanas_AudioUsbCyUcsi()
    {
        var tree = AuthorTree();
        var gpu = Assert.Single(tree.All, n => n.IsPresent && n.ClassName == "Display" && n.IsPci);

        Assert.Equal(0u, gpu.PciFunction);
        var siblings = tree.PciSiblings(gpu).OrderBy(n => n.PciFunction).ToList();
        Assert.Equal(new uint?[] { 1, 2, 3 }, siblings.Select(s => s.PciFunction));
        Assert.Contains("High Definition Audio", siblings[0].Description); // controladora HDA (clase System); MEDIA es su hijo HDAUDIO
        Assert.Equal("USB", siblings[1].ClassName.ToUpperInvariant());
    }

    [Fact]
    public void Mouse_CadenaDePadres_LlegaAUnaControladoraUsbPci_PasandoPorBridges()
    {
        var tree = AuthorTree();
        var mouse = tree.All.First(n => n.IsPresent && n.ClassName == "Mouse");

        var chain = tree.Ancestors(mouse).ToList();
        Assert.Contains(chain, n => n.IsPci && n.ClassName == "USB");   // xHCI que no se puede tocar (ADR-007)
        Assert.Contains(chain, n => n.Description.Contains("PCI-to-PCI Bridge") || n.Description.Contains("host CPU bridge"));
        Assert.Equal(@"HTREE\ROOT\0", chain[^1].InstanceId);
    }

    [Fact]
    public void Nics_InterrupcionesMsiX_LeidasDelHardware()
    {
        var tree = AuthorTree();
        var nics = tree.All.Where(n => n.IsPresent && n.IsPci && n.ClassName == "Net").ToList();

        Assert.Equal(2, nics.Count);
        Assert.All(nics, n => Assert.True(n.Interrupts is { SupportsMsiX: true }));
    }

    [Fact]
    public void Json_IdaYVuelta_SinPerderDatos()
    {
        var tree = AuthorTree();
        var again = DeviceTree.FromJson(DeviceTree.ToJson(tree.All));

        Assert.Equal(tree.All.Count, again.All.Count);
        var gpu = tree.All.First(n => n.ClassName == "Display" && n.IsPci);
        var gpu2 = again.Get(gpu.InstanceId)!;
        Assert.Equal(gpu.HardwareIds, gpu2.HardwareIds);
        Assert.Equal(gpu.Driver, gpu2.Driver);
        Assert.Equal(gpu.Interrupts, gpu2.Interrupts);
    }

    [Fact]
    public void Ancestors_CortaCiclos()
    {
        var a = Node("A", "B");
        var b = Node("B", "A");
        var tree = new DeviceTree(new[] { a, b });

        Assert.Single(tree.Ancestors(a));
    }

    internal static DeviceNode Node(string id, string? parent, string cls = "System", string enumerator = "ROOT") =>
        new(id, parent, id, null, cls, Guid.Empty, enumerator, Array.Empty<string>(), Array.Empty<string>(),
            true, false, 0, null, null, null, null, null, null, null);
}
