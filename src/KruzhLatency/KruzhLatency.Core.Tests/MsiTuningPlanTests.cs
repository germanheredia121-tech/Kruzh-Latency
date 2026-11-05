using KruzhLatency.Core.Benchmark;

namespace KruzhLatency.Core.Tests;

public class MsiTuningPlanTests
{
    [Fact]
    public void Limites_SoloMsiXConVariosMensajes()
    {
        Assert.Empty(MsiTuningPlan.LimitCandidates(msix: false, maxMessages: 8));
        Assert.Empty(MsiTuningPlan.LimitCandidates(msix: true, maxMessages: 1));
        Assert.Equal(new[] { 1 }, MsiTuningPlan.LimitCandidates(true, 2));
        Assert.Equal(new[] { 1, 2, 4 }, MsiTuningPlan.LimitCandidates(true, 8));
        Assert.Equal(new[] { 1, 2, 4 }, MsiTuningPlan.LimitCandidates(true, 2048)); // tope de 3 candidatos
    }

    [Fact]
    public void Elegir_GanaElDeMenorP99SoloSiSuperaElRuido()
    {
        var baseline = new[] { 40.0, 41, 39 };
        var cands = new Dictionary<int, IReadOnlyList<double>>
        {
            [1] = new[] { 30.0, 31, 29 },
            [2] = new[] { 25.0, 26, 24 },
            [4] = new[] { 39.5, 40, 40.5 }, // dentro del ruido
        };
        var best = MsiTuningPlan.PickBest(baseline, cands);
        Assert.NotNull(best);
        Assert.Equal(2, best!.Value.Value);
    }

    [Fact]
    public void Elegir_NingunoGana_Null() =>
        Assert.Null(MsiTuningPlan.PickBest(new[] { 40.0, 41, 39 }, new Dictionary<int, IReadOnlyList<double>> { [1] = new[] { 45.0, 46, 44 } }));
}
