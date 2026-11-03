namespace KruzhLatency.Core.Display;

/// <summary>Resultado de llevar un timing a un refresco objetivo con la resolución de pixel clock de un tipo de bloque.</summary>
public sealed record RefreshOption(TimingSource Source, long PixelClockHz, double RefreshHz, double TargetHz)
{
    /// <summary>Exacto = el desvío no se ve ni en el tercer decimal (lo que muestran CRU y el panel de NVIDIA).</summary>
    public bool IsExact => Math.Abs(RefreshHz - TargetHz) < 0.0005;

    public double DeviationHz => RefreshHz - TargetHz;

    public string BlockName => Source switch
    {
        TimingSource.DisplayIdType7 => "DisplayID 2.0 (Type VII)",
        TimingSource.DisplayIdType1 => "DisplayID 1.3 (Type I)",
        _ => "Detailed Timing (base / CTA-861)"
    };
}

public sealed record DisplayIdRecommendation(RefreshOption Type1, RefreshOption Type7, RefreshOption Chosen, string Reason);

/// <summary>
/// Hz exactos (spec 002 / F16): el pixel clock se guarda en pasos de 10 kHz (DTD y DisplayID Type I) o 1 kHz
/// (DisplayID 2.0 Type VII). Para un total horizontal × vertical dado, solo ciertos refrescos son alcanzables.
/// </summary>
public static class RefreshCalculator
{
    public static long PixelClockStepHz(TimingSource source) => source == TimingSource.DisplayIdType7 ? 1_000 : 10_000;

    /// <summary>Pixel clock más cercano al objetivo que el bloque puede codificar, y el refresco que resulta.</summary>
    public static RefreshOption Best(TimingSource source, int hTotal, int vTotal, double targetHz)
    {
        long step = PixelClockStepHz(source);
        double ideal = targetHz * hTotal * vTotal;
        long pclk = (long)Math.Round(ideal / step, MidpointRounding.AwayFromZero) * step;
        return new RefreshOption(source, pclk, (double)pclk / ((long)hTotal * vTotal), targetHz);
    }

    /// <summary>ADR-009: se elige el bloque que da el refresco exacto; si ambos son exactos, DisplayID 1.3 (más compatible).</summary>
    public static DisplayIdRecommendation RecommendDisplayId(int hTotal, int vTotal, double targetHz)
    {
        var t1 = Best(TimingSource.DisplayIdType1, hTotal, vTotal, targetHz);
        var t7 = Best(TimingSource.DisplayIdType7, hTotal, vTotal, targetHz);

        if (t1.IsExact)
            return new DisplayIdRecommendation(t1, t7, t1, $"DisplayID 1.3 ya da {t1.RefreshHz:0.000} Hz exactos y es el más compatible.");
        if (t7.IsExact)
            return new DisplayIdRecommendation(t1, t7, t7,
                $"DisplayID 1.3 solo llega a {t1.RefreshHz:0.000} Hz (pasos de 10 kHz); DisplayID 2.0 da {t7.RefreshHz:0.000} Hz exactos.");
        var closer = Math.Abs(t7.DeviationHz) < Math.Abs(t1.DeviationHz) ? t7 : t1;
        return new DisplayIdRecommendation(t1, t7, closer,
            $"Ninguno es exacto con este total ({hTotal}x{vTotal}); el más cercano es {closer.BlockName} con {closer.RefreshHz:0.000} Hz.");
    }

    /// <summary>
    /// Timing VESA CVT con blanking reducido (v1), el que CRU llama "Automatic PC": blanking horizontal fijo de 160
    /// (front 48, sync 32, back 80), front porch vertical 3, back porch mínimo 6 y blanking vertical mínimo de 460 µs.
    /// El pixel clock que devuelve es el EXACTO para el refresco pedido; después cada bloque lo cuantiza a su paso.
    /// </summary>
    public static DetailedTiming CvtReducedBlanking(int hActive, int vActive, double refreshHz, TimingSource source = TimingSource.DisplayIdType7)
    {
        const double minVBlankUs = 460.0;
        const int hBlank = 160, hFront = 48, hSync = 32, vFront = 3, minVBackPorch = 6;
        int vSync = VSyncForAspect(hActive, vActive);

        double hPeriodEstUs = (1_000_000.0 / refreshHz - minVBlankUs) / vActive;
        int vbiLines = (int)Math.Floor(minVBlankUs / hPeriodEstUs) + 1;
        int vBlank = Math.Max(vbiLines, vFront + vSync + minVBackPorch);

        int hTotal = hActive + hBlank, vTotal = vActive + vBlank;
        long exactPclk = (long)Math.Round(refreshHz * hTotal * vTotal);
        return new DetailedTiming(source, exactPclk, hActive, hBlank, hFront, hSync, vActive, vBlank, vFront, vSync,
            Interlaced: false, HSyncPositive: true, VSyncPositive: false);
    }

    /// <summary>Ancho de sync vertical de CVT según la relación de aspecto (4:3=4, 16:9=5, 16:10=6, 5:4=7, otras=10).</summary>
    private static int VSyncForAspect(int w, int h)
    {
        bool Is(int a, int b) => w * b == h * a;
        if (Is(4, 3)) return 4;
        if (Is(16, 9)) return 5;
        if (Is(16, 10)) return 6;
        if (Is(5, 4) || Is(15, 9)) return 7;
        return 10;
    }

    /// <summary>
    /// Ancho de banda sin comprimir (Gbps) de un modo: pixel clock × bits por píxel. Sirve para saber si hace falta DSC.
    /// </summary>
    public static double DataRateGbps(long pixelClockHz, int bitsPerColor = 8) => pixelClockHz * bitsPerColor * 3 / 1e9;

    /// <summary>Capacidad útil de DisplayPort (después de codificación 8b/10b o 128b/132b) en Gbps.</summary>
    public static double DisplayPortPayloadGbps(string link) => link switch
    {
        "RBR" => 4 * 1.62 * 0.8,
        "HBR" => 4 * 2.7 * 0.8,
        "HBR2" => 4 * 5.4 * 0.8,   // 17.28
        "HBR3" => 4 * 8.1 * 0.8,   // 25.92
        "UHBR10" => 4 * 10 * 128 / 132.0,
        "UHBR13.5" => 4 * 13.5 * 128 / 132.0,
        "UHBR20" => 4 * 20 * 128 / 132.0,
        _ => throw new ArgumentOutOfRangeException(nameof(link))
    };
}
