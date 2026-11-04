using KruzhLatency.Core.Benchmark;

namespace KruzhLatency.Core.Tests;

public class LatencyComparerTests
{
    [Fact]
    public void BajaClara_SeQueda()
    {
        var d = LatencyComparer.Decide(new[] { 120.0, 118, 122 }, new[] { 80.0, 82, 79 });
        Assert.True(d.KeepAlternative);
        Assert.InRange(d.DeltaPct, -35, -30);
    }

    [Fact]
    public void DentroDelRuido_VuelveComoEstaba()
    {
        var d = LatencyComparer.Decide(new[] { 100.0, 101, 99 }, new[] { 97.0, 98, 96 }); // -3 %: menos que el 5 % mínimo
        Assert.False(d.KeepAlternative);
        Assert.Contains("vuelve como estaba", d.Reason);
    }

    [Fact]
    public void Empeora_VuelveComoEstaba() =>
        Assert.False(LatencyComparer.Decide(new[] { 60.0, 61, 59 }, new[] { 90.0, 95, 92 }).KeepAlternative);

    [Fact]
    public void BaseMuyRuidosa_SubeElMargen()
    {
        var d = LatencyComparer.Decide(new[] { 60.0, 140, 100 }, new[] { 80.0, 80, 80 }); // -20 % pero σ = 40
        Assert.True(d.MarginPct > 20);
        Assert.False(d.KeepAlternative);
    }

    [Fact]
    public void SinInterrupciones_NoDecide()
    {
        var d = LatencyComparer.Decide(new[] { 0.0, 0, 0 }, new[] { 10.0, 10, 10 });
        Assert.False(d.KeepAlternative);
        Assert.Contains("no generó interrupciones", d.Reason);
    }

    [Fact]
    public void PocasMediciones_NoDecide() =>
        Assert.False(LatencyComparer.Decide(new[] { 100.0 }, new[] { 10.0, 10, 10 }).KeepAlternative);
}
