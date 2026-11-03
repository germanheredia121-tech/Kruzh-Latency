using KruzhLatency.Core.Abstractions;
using KruzhLatency.Infra.Wmi;

namespace KruzhLatency.Infra.Tests;

/// <summary>Partes puras de WmiBcdStore (sin tocar el BCD): IDs de elementos y formato del journal.</summary>
public class WmiBcdStoreTests
{
    [Theory]
    [InlineData(BcdElement.UsePlatformClock, 0x260000A2u)] // Microsoft Learn: BcdOSLoaderElementTypes
    [InlineData(BcdElement.TscSyncPolicy, 0x250000A6u)]
    [InlineData(BcdElement.HypervisorLaunchType, 0x250000F0u)]
    [InlineData(BcdElement.UsePlatformTick, 0x260000A4u)]  // tabla ReactOS / Geoff Chappell
    [InlineData(BcdElement.DisableDynamicTick, 0x260000A5u)]
    public void TiposDeElemento(BcdElement element, uint expected) => Assert.Equal(expected, WmiBcdStore.TypeOf(element));

    [Fact]
    public void NombresDeBcdedit_IdaYVuelta()
    {
        foreach (var e in Enum.GetValues<BcdElement>())
        {
            Assert.Equal(e, WmiBcdStore.FromBcdEditName(WmiBcdStore.BcdEditName(e)));
        }
        Assert.Null(WmiBcdStore.FromBcdEditName("nx"));
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("Sí", true)]   // journal de versiones anteriores en español
    [InlineData("No", false)]
    [InlineData("no", false)]
    public void Booleanos_DelJournal(string text, bool expected) =>
        Assert.Equal(expected, WmiBcdStore.Parse(BcdElement.DisableDynamicTick, text)!.Boolean);

    [Fact]
    public void Serializacion_NeutraAlIdioma()
    {
        Assert.Equal("yes", WmiBcdStore.Serialize(BcdValue.Of(true)));
        Assert.Equal("0", WmiBcdStore.Serialize(BcdValue.Of(0UL)));
        Assert.Null(WmiBcdStore.Parse(BcdElement.UsePlatformClock, "<DELETED>"));
        Assert.Equal(1UL, WmiBcdStore.Parse(BcdElement.HypervisorLaunchType, "1")!.Integer);
    }

    [IntegrationFact]
    [Trait("Category", "Integration")]
    public void Lectura_DelBcdReal_NoLanza()
    {
        var store = new WmiBcdStore();
        foreach (var e in Enum.GetValues<BcdElement>()) _ = store.Read(e);
    }
}
