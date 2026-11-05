namespace KruzhLatency.Core.Benchmark;

/// <summary>
/// Spec 003 / F3 completa (spec 001 y ADR-005): además del modo MSI, se miden la prioridad de interrupción y el límite de
/// mensajes, como MSI Utility v3 pero eligiendo por medición y no a ojo.
/// <list type="bullet">
/// <item>Prioridad (<c>Affinity Policy\DevicePriority</c>): "como viene Windows" contra Alta (3). Es una sugerencia para
/// Windows sin evidencia de mejora: solo se queda si mide mejor que el ruido.</item>
/// <item>Límite (<c>MessageNumberLimit</c>): solo en MSI-X con más de un mensaje. "Sin límite" contra potencias de 2 menores
/// que el máximo (hasta 3 candidatos: 1, 2, 4). Menos mensajes = menos colas y núcleos: rara vez gana, pero se mide.</item>
/// </list>
/// </summary>
public static class MsiTuningPlan
{
    public const int HighPriority = 3;
    public const int MaxLimitCandidates = 3;

    public static IReadOnlyList<int> LimitCandidates(bool msix, uint maxMessages)
    {
        if (!msix || maxMessages <= 1) return Array.Empty<int>();
        var list = new List<int>();
        for (int n = 1; n < maxMessages && list.Count < MaxLimitCandidates; n *= 2) list.Add(n);
        return list;
    }

    /// <summary>El candidato que gana al estado actual por encima del ruido y con menor p99 medio; null si ninguno gana.</summary>
    public static (int Value, LatencyDecision Decision)? PickBest(IReadOnlyList<double> baseline, IReadOnlyDictionary<int, IReadOnlyList<double>> candidates)
    {
        (int Value, LatencyDecision Decision)? best = null;
        foreach (var (value, p99) in candidates)
        {
            var d = LatencyComparer.Decide(baseline, p99);
            if (!d.KeepAlternative) continue;
            if (best is null || d.AltMeanUs < best.Value.Decision.AltMeanUs) best = (value, d);
        }
        return best;
    }
}
