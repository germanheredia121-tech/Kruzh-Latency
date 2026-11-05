namespace KruzhLatency.Core.Network;

/// <summary>Un hueco en el flujo de paquetes: durante <see cref="GapMs"/> no pasó nada en esa dirección.</summary>
public sealed record FlowGap(double AtSeconds, double GapMs);

/// <summary>Dirección del flujo: lo que manda el servidor (bajada) o lo que manda el juego al servidor (subida).</summary>
public enum FlowDirection { FromServer, ToServer }

public sealed record FlowReport(
    string Remote,
    FlowDirection Direction,
    int Packets,
    double DurationSeconds,
    double PacketsPerSecond,
    double MedianIntervalMs,
    double P99IntervalMs,
    double JitterMs,
    IReadOnlyList<FlowGap> Gaps,
    string Summary);

/// <summary>
/// Monitor de la partida: el tráfico de CS2 está cifrado, pero va a ritmo constante (un "tick").
/// Sin leer su contenido se puede ver si va parejo: un hueco mucho más largo que el intervalo normal es un momento sin
/// datos, y la variación entre intervalos es el jitter que siente el juego.
/// En la subida se registra el momento en que el juego entrega el paquete a Windows: un hueco ahí nace en la PC (el juego
/// no mandó), no en la red. La pérdida de subida en la red no se ve desde la PC: para eso está la prueba de ruta.
/// </summary>
public static class FlowAnalysis
{
    /// <summary>Un hueco cuenta si dura al menos esto y al menos 3 intervalos normales.</summary>
    public const double MinGapMs = 50;

    /// <summary>Dos huecos (uno de cada dirección) se consideran el mismo momento si empiezan a menos de esto.</summary>
    public const double SameMomentSeconds = 0.25;

    /// <param name="timesMs">Momento de cada paquete, en ms desde el inicio de la grabación (el mismo reloj para las dos direcciones).</param>
    public static FlowReport Analyze(string remote, IReadOnlyList<double> timesMs, FlowDirection direction = FlowDirection.FromServer)
    {
        bool down = direction == FlowDirection.FromServer;
        var t = timesMs.OrderBy(x => x).ToArray();
        if (t.Length < 10)
            return new FlowReport(remote, direction, t.Length, 0, 0, 0, 0, 0, Array.Empty<FlowGap>(),
                down ? "Llegaron muy pocos paquetes del servidor: ¿estabas en una partida mientras se grababa?"
                     : "CS2 mandó muy pocos paquetes al servidor: ¿estabas en una partida mientras se grababa?");

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
            if (intervals[i] >= gapThreshold) gaps.Add(new FlowGap(t[i] / 1000.0, intervals[i]));
        }

        double pps = t.Length / Math.Max(duration, 0.001);
        string summary;
        if (down)
            summary = gaps.Count == 0
                ? $"Bajada pareja: {pps:0} paquetes/s del servidor, intervalo normal {median:0.0} ms, jitter {jitter:0.0} ms y ningún hueco de más de {gapThreshold:0} ms."
                : $"Bajada: {gaps.Count} hueco(s) de más de {gapThreshold:0} ms sin paquetes del servidor (el mayor: {gaps.Max(g => g.GapMs):0} ms). " +
                  $"Ritmo normal {pps:0} paquetes/s, jitter {jitter:0.0} ms. Cada hueco es un momento en que el juego tuvo que adivinar: " +
                  "rivales que saltan o disparos que no registran.";
        else
            summary = gaps.Count == 0
                ? $"Subida pareja: CS2 mandó {pps:0} paquetes/s al servidor, intervalo normal {median:0.0} ms, jitter {jitter:0.0} ms y ningún hueco de más de {gapThreshold:0} ms."
                : $"Subida: {gaps.Count} hueco(s) de más de {gapThreshold:0} ms en que CS2 no mandó nada al servidor (el mayor: {gaps.Max(g => g.GapMs):0} ms). " +
                  $"Ritmo normal {pps:0} paquetes/s, jitter {jitter:0.0} ms. Tus comandos de esos momentos llegan tarde al servidor.";
        return new FlowReport(remote, direction, t.Length, duration, pps, median, p99, jitter, gaps, summary);
    }

    /// <summary>Huecos de subida que empiezan en el mismo momento que uno de bajada.</summary>
    public static int SimultaneousGaps(FlowReport down, FlowReport up) =>
        up.Gaps.Count(u => down.Gaps.Any(d => Math.Abs(d.AtSeconds - u.AtSeconds) <= SameMomentSeconds));

    /// <summary>Dónde está el problema, comparando las dos direcciones grabadas a la vez.</summary>
    public static string Verdict(FlowReport down, FlowReport up)
    {
        if (down.Packets < 10 || up.Packets < 10) return "";
        bool downGaps = down.Gaps.Count > 0, upGaps = up.Gaps.Count > 0;

        if (!downGaps && !upGaps)
            return "Las dos direcciones van parejas desde tu PC. Si CS2 igual marca pérdida o \"miss\", pasa después de salir de tu PC " +
                   "(router, proveedor o ruta hasta Valve): probá la ruta hasta el servidor.";
        if (upGaps && !downGaps)
            return "La bajada está limpia y los huecos son solo de envío: la causa está en tu PC, no en la red. El juego se frenó " +
                   "y no mandó tus comandos a tiempo (picos de CPU, programas en segundo plano, drivers). Si CS2 marca \"miss\", lo más probable es que venga de esto.";
        if (downGaps && !upGaps)
            return "Tu PC manda parejo, pero hay momentos en que no llega nada del servidor: el problema está en la red de bajada " +
                   "(router, proveedor o ruta). Probá la ruta para ver dónde empieza.";

        int together = SimultaneousGaps(down, up);
        return together * 2 >= up.Gaps.Count
            ? $"Hay huecos en las dos direcciones y {together} de {up.Gaps.Count} caen en el mismo momento: la PC entera se frenó " +
              "(driver de red, placa de red o un pico del sistema), no el servidor."
            : "Hay huecos en las dos direcciones en momentos distintos: los de subida nacen en tu PC y los de bajada en la red. " +
              "Revisá los dos: cerrá programas en segundo plano y probá la ruta.";
    }
}
