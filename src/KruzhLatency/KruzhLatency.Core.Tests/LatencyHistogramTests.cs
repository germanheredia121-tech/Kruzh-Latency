using KruzhLatency.Core.Latency;

namespace KruzhLatency.Core.Tests;

public class LatencyHistogramTests
{
    [Fact]
    public void Percentiles_ConDistribucionConocida()
    {
        var h = new LatencyHistogram();
        for (int i = 0; i < 990; i++) h.Add(5.3);   // 99 % en ~5 µs
        for (int i = 0; i < 10; i++) h.Add(250.7);  // 1 % en ~250 µs

        Assert.Equal(1000, h.Count);
        Assert.Equal(6, h.Percentile(50));     // borde superior del casillero de 5 µs
        Assert.Equal(6, h.Percentile(99));
        Assert.Equal(250.7, h.Percentile(99.9), 1);
        Assert.Equal(250.7, h.MaxUs, 3);
    }

    [Fact]
    public void Desborde_SeReportaComoElMaximo()
    {
        var h = new LatencyHistogram();
        h.Add(10);
        h.Add(4200);
        Assert.Equal(4200, h.Percentile(100));
        Assert.Equal(0, new LatencyHistogram().Percentile(99));
    }

    [Fact]
    public void Merge_SumaTodo()
    {
        var a = new LatencyHistogram(); a.Add(3); a.Add(7);
        var b = new LatencyHistogram(); b.Add(900);
        a.Merge(b);
        Assert.Equal(3, a.Count);
        Assert.Equal(900, a.MaxUs);
        Assert.Equal(910 / 3.0, a.MeanUs, 6);
    }

    [Fact]
    public void ValoresInvalidos_SeIgnoran()
    {
        var h = new LatencyHistogram();
        h.Add(-1); h.Add(double.NaN); h.Add(double.PositiveInfinity);
        Assert.Equal(0, h.Count);
    }
}
