namespace KruzhLatency.Core.Network;

/// <summary>Un hueco en el flujo de paquetes del servidor: durante <see cref="GapMs"/> no llegó nada.</summary>
public sealed record FlowGap(double AtSeconds, double GapMs);

public sealed record FlowReport(
    string Remote,
    int Packets,
    double DurationSeconds,
    double PacketsPerSecond,
    double MedianIntervalMs,
    double P99IntervalMs,
    double JitterMs,
    IReadOnlyList<FlowGap> Gaps,
    string Summary);

/// <summary>
/// Monitor de la partida: el tráfico de CS2 está cifrado, pero el servidor manda paquetes a ritmo constante (un "tick").
/// Sin leer su contenido se puede ver si llegan parejos: un hueco mucho más largo que el intervalo normal es pérdida o
/// demora real en ese momento, y la variación entre intervalos es el jitter que siente el juego.
/// </summary>
public static class FlowAnalysis
{
    /// <summary>Un hueco cuenta si dura al menos esto y al menos 3 intervalos normales.</summary>
    public const double MinGapMs = 50;

    public static FlowReport Analyze(string remote, IReadOnlyList<double> arrivalsMs)
    {
        var t = arrivalsMs.OrderBy(x => x).ToArray();
        if (t.Length < 10)
            return new FlowReport(remote, t.Length, 0, 0, 0, 0, 0, Array.Empty<FlowGap>(),
                "Llegaron muy pocos paquetes del servidor: ¿estabas en una partida mientras se grababa?");

        var intervals = new double[t.Length - 1];
        for (int i = 1; i < t.Length; i++) intervals[i - 1] = t[i] - t[i - 1];
        var sorted = intervals.OrderBy(x => x).ToArray();
        double median = sorted[sorted.Length / 2];
        double p99 = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))];
        double jitter = intervals.Length < 2 ? 0 : intervals.Zip(intervals.Skip(1), (a, b) => Math.Abs(b - a)).Average();
        double duration = (t[^1] - t[0]) / 1000.0;

        double gapThreshold = Math.Max(MinGapMs, 3 * median);
        var gaps = new List<FlowGap>();
        for (int i = 0; i < intervals.Length; i++)
        {
            if (intervals[i] >= gapThreshold) gaps.Add(new FlowGap((t[i] - t[0]) / 1000.0, intervals[i]));
        }

        double pps = t.Length / Math.Max(duration, 0.001);
        string summary = gaps.Count == 0
            ? $"Flujo parejo: {pps:0} paquetes/s, intervalo normal {median:0.0} ms, jitter {jitter:0.0} ms y ningún hueco de más de {gapThreshold:0} ms."
            : $"{gaps.Count} hueco(s) de más de {gapThreshold:0} ms sin paquetes del servidor (el mayor: {gaps.Max(g => g.GapMs):0} ms). " +
              $"Ritmo normal {pps:0} paquetes/s, jitter {jitter:0.0} ms. Cada hueco es un momento en que el juego tuvo que adivinar: " +
              "rivales que saltan o disparos que no registran.";
        return new FlowReport(remote, t.Length, duration, pps, median, p99, jitter, gaps, summary);
    }
}
