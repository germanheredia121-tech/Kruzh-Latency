using System.Globalization;

namespace KruzhLatency.Core.Benchmark;

/// <summary>Un frame capturado por PresentMon (tiempos en milisegundos; null = la columna no estaba o decía NA).</summary>
public sealed record PresentMonFrame(double FrameMs, double? CpuBusyMs, double? GpuBusyMs);

/// <summary>
/// Resumen de una corrida. Las "lows" son percentiles de tiempo de frame pasados a FPS, igual que el VProf de CS2
/// (P1 = FPS del frame en el percentil 99 de duración).
/// </summary>
public sealed record FrameStats(int Frames, double AvgFps, double Low1Fps, double Low01Fps, double? AvgCpuBusyMs, double? AvgGpuBusyMs)
{
    public static FrameStats? From(IReadOnlyList<PresentMonFrame> frames)
    {
        var times = frames.Select(f => f.FrameMs).Where(t => t > 0 && double.IsFinite(t)).OrderBy(t => t).ToArray();
        if (times.Length < 100) return null; // muy pocos frames para que las lows signifiquen algo

        double avgMs = times.Average();
        double? cpu = Average(frames.Select(f => f.CpuBusyMs));
        double? gpu = Average(frames.Select(f => f.GpuBusyMs));
        return new FrameStats(times.Length, 1000 / avgMs, 1000 / Percentile(times, 99), 1000 / Percentile(times, 99.9), cpu, gpu);
    }

    /// <summary>Percentil con interpolación lineal sobre un arreglo ya ordenado.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) throw new ArgumentException("Sin datos.", nameof(sorted));
        double rank = p / 100 * (sorted.Count - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    private static double? Average(IEnumerable<double?> values)
    {
        var v = values.Where(x => x is > 0).Select(x => x!.Value).ToList();
        return v.Count == 0 ? null : v.Average();
    }
}

/// <summary>Lee el CSV de PresentMon por nombre de columna (el orden cambia entre versiones).</summary>
public static class PresentMonCsv
{
    public static IReadOnlyList<PresentMonFrame> Parse(string csv, string? processName = null)
    {
        using var reader = new StringReader(csv);
        string? header = reader.ReadLine();
        if (header is null) return Array.Empty<PresentMonFrame>();
        var cols = header.Split(',').Select((name, i) => (name: name.Trim(), i)).ToDictionary(c => c.name, c => c.i, StringComparer.OrdinalIgnoreCase);
        if (!cols.TryGetValue("MsBetweenPresents", out int frameCol)) throw new FormatException("El CSV no tiene la columna MsBetweenPresents.");
        int appCol = cols.GetValueOrDefault("Application", -1);
        int cpuCol = cols.GetValueOrDefault("MsCPUBusy", -1);
        int gpuCol = cols.GetValueOrDefault("MsGPUBusy", -1);

        var frames = new List<PresentMonFrame>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var f = line.Split(',');
            if (f.Length <= frameCol) continue;
            if (processName is not null && appCol >= 0 && !string.Equals(f[appCol], processName, StringComparison.OrdinalIgnoreCase)) continue;
            if (Num(f, frameCol) is not { } ms) continue;
            frames.Add(new PresentMonFrame(ms, Num(f, cpuCol), Num(f, gpuCol)));
        }
        return frames;
    }

    private static double? Num(string[] f, int i) =>
        i >= 0 && i < f.Length && double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
}
