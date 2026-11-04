namespace KruzhLatency.Core.Benchmark;

public sealed record LatencyDecision(bool KeepAlternative, double BaseMeanUs, double AltMeanUs, double DeltaPct, double MarginPct, string Reason);

/// <summary>
/// Spec 003 / G6–G7: compara la latencia p99 de un driver (ISR + DPC, µs) entre el estado actual y una alternativa.
/// Menos es mejor. La alternativa se queda solo si baja el p99 más que el ruido: máx(5 %, 2 desvíos del estado actual).
/// La latencia de interrupciones es más ruidosa que los FPS, por eso el mínimo es 5 % y no 2 %.
/// </summary>
public static class LatencyComparer
{
    public const double MinMarginPct = 5.0;
    public const int MinTrials = 3;

    public static LatencyDecision Decide(IReadOnlyList<double> baseP99, IReadOnlyList<double> altP99)
    {
        if (baseP99.Count < MinTrials || altP99.Count < MinTrials)
        {
            return new LatencyDecision(false, 0, 0, 0, 0, $"Hacen falta {MinTrials} mediciones de cada estado.");
        }
        double b = baseP99.Average(), a = altP99.Average();
        if (b <= 0)
        {
            return new LatencyDecision(false, b, a, 0, 0, "El driver no generó interrupciones durante la medición: no hay con qué comparar.");
        }

        double margin = Math.Max(MinMarginPct, 2 * Std(baseP99) / b * 100);
        double delta = (a - b) / b * 100;
        bool keep = delta < -margin;
        string reason = keep
            ? $"p99 {b:F0} → {a:F0} µs ({delta:+0;-0} %, ruido {margin:F0} %): se queda."
            : $"p99 {b:F0} → {a:F0} µs ({delta:+0;-0} %, ruido {margin:F0} %): no mejora, vuelve como estaba.";
        return new LatencyDecision(keep, b, a, delta, margin, reason);
    }

    private static double Std(IReadOnlyList<double> v)
    {
        if (v.Count < 2) return 0;
        double m = v.Average();
        return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / (v.Count - 1));
    }
}
