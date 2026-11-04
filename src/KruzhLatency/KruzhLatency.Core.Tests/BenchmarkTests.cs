using KruzhLatency.Core.Benchmark;

namespace KruzhLatency.Core.Tests;

/// <summary>Spec 003 / G1: estadísticas de frames, parser de PresentMon y comparador con margen de ruido.</summary>
public class BenchmarkTests
{
    private static string Fixture(string n) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", n));

    [Fact]
    public void PresentMon_CsvReal_SeLeePorNombreDeColumna()
    {
        var frames = PresentMonCsv.Parse(Fixture("presentmon-cs2-sample.csv"), "cs2.exe");

        Assert.Equal(3000, frames.Count);
        Assert.All(frames, f => Assert.True(f.FrameMs > 0));
        Assert.Contains(frames, f => f.CpuBusyMs is > 0);
        Assert.Contains(frames, f => f.GpuBusyMs is > 0);
        Assert.Empty(PresentMonCsv.Parse(Fixture("presentmon-cs2-sample.csv"), "otro.exe"));
    }

    [Fact]
    public void FrameStats_SobreCapturaReal_ValoresPlausibles()
    {
        var stats = FrameStats.From(PresentMonCsv.Parse(Fixture("presentmon-cs2-sample.csv")))!;
        Assert.InRange(stats.AvgFps, 100, 2000);
        Assert.True(stats.Low1Fps < stats.AvgFps);
        Assert.True(stats.Low01Fps <= stats.Low1Fps);
    }

    [Fact]
    public void FrameStats_FramesConocidos()
    {
        // 990 frames de 2 ms y 10 de 10 ms: el percentil 99 cae en el borde entre ambos.
        var frames = Enumerable.Repeat(new PresentMonFrame(2, null, null), 990).Concat(Enumerable.Repeat(new PresentMonFrame(10, null, null), 10)).ToList();
        var s = FrameStats.From(frames)!;
        Assert.Equal(1000 / 2.08, s.AvgFps, 3);
        Assert.Equal(100, s.Low01Fps, 3);       // 0,1 % → 10 ms
        Assert.Null(FrameStats.From(frames.Take(50).ToList())); // muy pocos frames
    }

    private static FrameStats Run(double avg, double low) => new(10_000, avg, low, low * 0.8, null, null);

    private static Dictionary<string, IReadOnlyList<FrameStats>> Trials(params (string Name, double[] Lows)[] c) =>
        c.ToDictionary(x => x.Name, x => (IReadOnlyList<FrameStats>)x.Lows.Select(l => Run(520, l)).ToList());

    [Fact]
    public void Comparador_GanaSoloSiSuperaElRuido()
    {
        var d = TrialComparer.Decide(Trials(
            (TrialComparer.Default, new[] { 180.0, 182, 181 }),
            ("CPU 4", new[] { 195.0, 196, 194 }),   // +7,7 %
            ("CPU 8", new[] { 183.0, 182, 184 })));  // +1,1 %: dentro del ruido

        Assert.Equal("CPU 4", d.Winner);
        Assert.False(d.Table.Single(c => c.Candidate == "CPU 8").BeatsDefault);
    }

    [Fact]
    public void Comparador_SiNadieGana_NoSeTocaNada()
    {
        var d = TrialComparer.Decide(Trials(
            (TrialComparer.Default, new[] { 181.0, 179, 180 }),
            ("CPU 2", new[] { 170.0, 172, 171 }),
            ("CPU 10", new[] { 182.0, 181, 183 })));

        Assert.Null(d.Winner);
        Assert.Contains("no se cambia nada", d.Reason);
    }

    [Fact]
    public void Comparador_RuidoAlto_SubeElMargen()
    {
        // Default muy ruidoso (σ ≈ 10): +5 % no alcanza.
        var d = TrialComparer.Decide(Trials(
            (TrialComparer.Default, new[] { 170.0, 190, 180 }),
            ("CPU 4", new[] { 189.0, 189, 189 })));

        Assert.True(d.MarginPct > 10);
        Assert.Null(d.Winner);
    }

    [Fact]
    public void Comparador_SinSuficientesCorridasDelDefault_NoDecide()
    {
        var d = TrialComparer.Decide(Trials((TrialComparer.Default, new[] { 180.0 }), ("CPU 4", new[] { 250.0, 250, 250 })));
        Assert.Null(d.Winner);
        Assert.Empty(d.Table);
    }

    [Fact]
    public void Comparador_NoGanaSiPierdePromedio()
    {
        var trials = Trials((TrialComparer.Default, new[] { 180.0, 181, 180 }));
        trials["CPU 4"] = new[] { Run(450, 195), Run(452, 196), Run(451, 195) }; // +8 % de low pero -13 % de promedio
        Assert.Null(TrialComparer.Decide(trials).Winner);
    }
}
