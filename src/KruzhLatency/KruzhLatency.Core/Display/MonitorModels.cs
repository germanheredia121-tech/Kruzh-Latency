namespace KruzhLatency.Core.Display;

/// <summary>Señal que sale hoy por el cable hacia un monitor activo (QueryDisplayConfig).</summary>
public sealed record ActiveDisplayMode(
    int DesktopWidth,
    int DesktopHeight,
    int SignalWidth,
    int SignalHeight,
    int HTotal,
    int VTotal,
    long PixelRateHz,
    double RefreshHz,
    /// <summary>DisplayPort, HDMI, DVI, interno…</summary>
    string Connection,
    /// <summary>Escalado del camino: Identidad, Centrado, Estirado, Relación de aspecto, Personalizado, Preferido.</summary>
    string Scaling);

/// <summary>Un monitor (devnode DISPLAY\…) con lo que Windows sabe de él.</summary>
public sealed record MonitorInfo(
    string InstanceId,
    string Name,
    bool IsPresent,
    /// <summary>GPU (o adaptador) del que cuelga; para fantasmas puede ser el Microsoft Basic Display Driver.</summary>
    string? ParentInstanceId,
    byte[]? Edid,
    /// <summary>EDID_OVERRIDE (lo que escribe CRU), bloques concatenados; null si no hay override.</summary>
    byte[]? EdidOverride,
    ActiveDisplayMode? Current)
{
    /// <summary>El EDID que Windows usa: el override si existe, si no el del monitor.</summary>
    public byte[]? EffectiveEdid => EdidOverride ?? Edid;
    public bool HasOverride => EdidOverride is { Length: > 0 };
}

/// <summary>Análisis listo para mostrar en la sección Pantalla.</summary>
public sealed record MonitorAnalysis(
    MonitorInfo Monitor,
    Edid? Parsed,
    string? ParseError,
    DetailedTiming? NativeMax,
    double UncompressedGbps,
    bool NeedsDscOnHbr2,
    bool NeedsDscOnHbr3,
    /// <summary>El modo actual no es el máximo que declara el EDID (resolución o Hz).</summary>
    bool RunningBelowMax,
    IReadOnlyList<string> Notes);

public static class MonitorAnalyzer
{
    public static MonitorAnalysis Analyze(MonitorInfo m)
    {
        Edid? edid = null;
        string? error = null;
        var notes = new List<string>();
        if (m.EffectiveEdid is { } raw)
        {
            try { edid = EdidParser.Parse(raw); }
            catch (InvalidDataException ex) { error = ex.Message; }
        }
        else
        {
            error = "Windows no tiene EDID guardado para este monitor.";
        }

        var native = edid?.NativeMax;
        // 8 bits por color (RGB 24 bpp): lo que usa un juego en SDR.
        double gbps = native is null ? 0 : RefreshCalculator.DataRateGbps(native.PixelClockHz);
        bool dscHbr2 = gbps > RefreshCalculator.DisplayPortPayloadGbps("HBR2");
        bool dscHbr3 = gbps > RefreshCalculator.DisplayPortPayloadGbps("HBR3");

        bool below = false;
        if (native is not null && m.Current is { } cur)
        {
            below = cur.SignalWidth < native.HActive || cur.SignalHeight < native.VActive || cur.RefreshHz + 0.5 < native.RefreshHz;
            if (cur.RefreshHz + 0.5 < native.RefreshHz)
                notes.Add($"Estás a {cur.RefreshHz:0.###} Hz y el monitor llega a {native.RefreshHz:0.###} Hz: cambialo en Configuración → Pantalla → Pantalla avanzada.");
        }
        if (m.HasOverride) notes.Add("Hay un EDID_OVERRIDE activo (CRU u otra herramienta): Windows usa ese EDID y no el del monitor.");
        if (edid?.Cta.FirstOrDefault()?.FreeSyncRange is { } fs) notes.Add($"El monitor declara FreeSync/VRR {fs.MinHz}–{fs.MaxHz} Hz.");
        if (!m.IsPresent) notes.Add("Monitor fantasma: no está conectado. Se puede limpiar (C4).");
        if (native is not null)
            notes.Add(dscHbr2
                ? $"El modo máximo pide {gbps:0.00} Gbps: en DisplayPort HBR2 necesita DSC."
                : $"El modo máximo pide {gbps:0.00} Gbps sin comprimir: entra en DisplayPort HBR2 sin DSC.");

        return new MonitorAnalysis(m, edid, error, native, gbps, dscHbr2, dscHbr3, below, notes);
    }
}
