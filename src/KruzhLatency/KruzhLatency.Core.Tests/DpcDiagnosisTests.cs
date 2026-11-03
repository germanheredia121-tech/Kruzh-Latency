using KruzhLatency.Core.Latency;

namespace KruzhLatency.Core.Tests;

/// <summary>D1: objetivos del chat y mapa driver → causa → acción.</summary>
public class DpcDiagnosisTests
{
    [Theory]
    [InlineData("ndis.sys", DpcFixArea.Network)]
    [InlineData("nvlddmkm.sys", DpcFixArea.Gpu)]
    [InlineData("dxgkrnl.sys", DpcFixArea.Gpu)]
    [InlineData("Wdf01000.sys", DpcFixArea.Usb)]
    [InlineData("HDAudBus.sys", DpcFixArea.Audio)]
    [InlineData("storport.sys", DpcFixArea.Storage)]
    [InlineData("Netwtw10.sys", DpcFixArea.Network)]
    [InlineData("foo.sys", DpcFixArea.None)]
    public void DriverMapeaASuArea(string file, DpcFixArea area) =>
        Assert.Equal(area, DpcDiagnosis.Diagnose(file, 100, 2).Area);

    [Fact]
    public void Objetivos_Pico300_Promedio8()
    {
        Assert.False(DpcDiagnosis.Diagnose("ndis.sys", 300, 7.9).NeedsAttention);
        Assert.True(DpcDiagnosis.Diagnose("ndis.sys", 301, 1).OverPeak);
        Assert.True(DpcDiagnosis.Diagnose("dxgkrnl.sys", 50, 22.1).OverAverage); // el caso del chat: 22.1 µs de promedio
    }

    [Fact]
    public void Red_ApuntaATodasLasPlacasPorPrefijo()
    {
        var d = DpcDiagnosis.Diagnose("ndis.sys", 900, 3);

        Assert.True(DpcDiagnosis.Matches(d.FixItemIds, @"Nic:PCI\VEN_10EC&DEV_8125\X"));
        Assert.True(DpcDiagnosis.Matches(d.FixItemIds, "Dev:SecondNic"));
        Assert.False(DpcDiagnosis.Matches(d.FixItemIds, "DisableHdcp"));
    }

    [Fact]
    public void DriverDesconocido_SinAjustes_ConConsejo()
    {
        var d = DpcDiagnosis.Diagnose("vendor.sys", 2000, 30);

        Assert.Empty(d.FixItemIds);
        Assert.Contains("actualizalo", d.Cause);
    }
}
