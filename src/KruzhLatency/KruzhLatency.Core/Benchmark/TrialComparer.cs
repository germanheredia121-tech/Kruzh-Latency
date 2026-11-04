namespace KruzhLatency.Core.Benchmark;

/// <summary>Resultado agregado de un candidato (ej. "GPU en CPU 4") sobre varias corridas.</summary>
public sealed record CandidateSummary(string Candidate, int Trials, double MeanAvgFps, double MeanLow1Fps, double StdLow1Fps, double DeltaLow1Pct, bool BeatsDefault);

public sealed record TrialDecision(string? Winner, double MarginPct, IReadOnlyList<CandidateSummary> Table, string Reason);

/// <summary>
/// ADR-012: compara candidatos contra "como viene Windows". El criterio principal es el 1% low; un candidato gana solo si
/// su 1% low supera al del default por más que el ruido (máx(2 %, 2 desvíos del default)) y su FPS medio no cae por más que ese margen.
/// Si ninguno gana, la decisión es no tocar nada.
/// </summary>
public static class TrialComparer
{
    public const string Default = "default";
    public const double MinMarginPct = 2.0;
    public const int MinTrials = 3;

    public static TrialDecision Decide(IReadOnlyDictionary<string, IReadOnlyList<FrameStats>> trials)
    {
        if (!trials.TryGetValue(Default, out var baseline) || baseline.Count < MinTrials)
        {
            return new TrialDecision(null, 0, Array.Empty<CandidateSummary>(), $"Hacen falta al menos {MinTrials} corridas del default para medir el ruido.");
        }

        double baseLow = baseline.Average(s => s.Low1Fps);
        double baseAvg = baseline.Average(s => s.AvgFps);
        double marginPct = Math.Max(MinMarginPct, 2 * Std(baseline.Select(s => s.Low1Fps)) / baseLow * 100);

        var table = trials
            .Where(t => t.Value.Count > 0)
            .Select(t =>
            {
                double low = t.Value.Average(s => s.Low1Fps);
                double avg = t.Value.Average(s => s.AvgFps);
                double deltaLow = (low - baseLow) / baseLow * 100;
                double deltaAvg = (avg - baseAvg) / baseAvg * 100;
                bool beats = t.Key != Default && t.Value.Count >= MinTrials && deltaLow > marginPct && deltaAvg > -marginPct;
                return new CandidateSummary(t.Key, t.Value.Count, avg, low, Std(t.Value.Select(s => s.Low1Fps)), deltaLow, beats);
            })
            .OrderByDescending(c => c.MeanLow1Fps)
            .ToList();

        var winner = table.Where(c => c.BeatsDefault).OrderByDescending(c => c.MeanLow1Fps).FirstOrDefault();
        string reason = winner is null
            ? $"Ninguna opción supera a \"como viene Windows\" por más del ruido medido ({marginPct:F1} %): no se cambia nada."
            : $"{winner.Candidate} mejora el 1% low {winner.DeltaLow1Pct:+0.0;-0.0} % (ruido {marginPct:F1} %).";
        return new TrialDecision(winner?.Candidate, marginPct, table, reason);
    }

    private static double Std(IEnumerable<double> values)
    {
        var v = values.ToList();
        if (v.Count < 2) return 0;
        double mean = v.Average();
        return Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Count - 1));
    }
}
