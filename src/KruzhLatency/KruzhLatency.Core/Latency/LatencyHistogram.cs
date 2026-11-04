namespace KruzhLatency.Core.Latency;

/// <summary>
/// Spec 003 / G3: histograma de latencias en microsegundos para sacar percentiles (p99) de ISR/DPC sin guardar cada evento.
/// Casilleros de 1 µs hasta 1000 µs; lo que pasa de 1000 µs va a un casillero de desborde (se reporta como el máximo visto).
/// No es thread-safe: el llamador sincroniza.
/// </summary>
public sealed class LatencyHistogram
{
    public const int Buckets = 1000;
    private readonly long[] _counts = new long[Buckets + 1];

    public long Count { get; private set; }
    public double MaxUs { get; private set; }
    public double TotalUs { get; private set; }
    public double MeanUs => Count == 0 ? 0 : TotalUs / Count;

    public void Add(double us)
    {
        if (us < 0 || !double.IsFinite(us)) return;
        int b = us >= Buckets ? Buckets : (int)us;
        _counts[b]++;
        Count++;
        TotalUs += us;
        if (us > MaxUs) MaxUs = us;
    }

    public void Merge(LatencyHistogram other)
    {
        for (int i = 0; i <= Buckets; i++) _counts[i] += other._counts[i];
        Count += other.Count;
        TotalUs += other.TotalUs;
        MaxUs = Math.Max(MaxUs, other.MaxUs);
    }

    /// <summary>Percentil (0–100) con resolución de 1 µs (borde superior del casillero). 0 si no hay datos.</summary>
    public double Percentile(double p)
    {
        if (Count == 0) return 0;
        long target = (long)Math.Ceiling(p / 100 * Count);
        if (target < 1) target = 1;
        long seen = 0;
        for (int i = 0; i <= Buckets; i++)
        {
            seen += _counts[i];
            if (seen >= target) return i == Buckets ? MaxUs : Math.Min(i + 1, MaxUs);
        }
        return MaxUs;
    }
}
