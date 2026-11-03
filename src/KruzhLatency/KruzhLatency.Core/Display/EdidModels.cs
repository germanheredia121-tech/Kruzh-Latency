namespace KruzhLatency.Core.Display;

/// <summary>Dónde está definido un timing: define la resolución del pixel clock y por eso los Hz que se pueden lograr.</summary>
public enum TimingSource
{
    /// <summary>Detailed Timing Descriptor de 18 bytes (bloque base o CTA-861). Pixel clock en pasos de 10 kHz.</summary>
    DetailedDescriptor,
    /// <summary>DisplayID 1.x Type I Detailed Timing. Pasos de 10 kHz.</summary>
    DisplayIdType1,
    /// <summary>DisplayID 2.0 Type VII Detailed Timing. Pasos de 1 kHz.</summary>
    DisplayIdType7
}

/// <summary>Un modo de video completo (activo + blanking) con su pixel clock real.</summary>
public sealed record DetailedTiming(
    TimingSource Source,
    long PixelClockHz,
    int HActive, int HBlank, int HFrontPorch, int HSyncWidth,
    int VActive, int VBlank, int VFrontPorch, int VSyncWidth,
    bool Interlaced,
    bool HSyncPositive, bool VSyncPositive,
    bool IsPreferred = false)
{
    public int HTotal => HActive + HBlank;
    public int VTotal => VActive + VBlank;
    public int HBackPorch => HBlank - HFrontPorch - HSyncWidth;
    public int VBackPorch => VBlank - VFrontPorch - VSyncWidth;

    /// <summary>Refresco exacto en Hz (no redondeado).</summary>
    public double RefreshHz => HTotal == 0 || VTotal == 0 ? 0 : (double)PixelClockHz / ((long)HTotal * VTotal) * (Interlaced ? 2 : 1);

    public double HorizontalKhz => HTotal == 0 ? 0 : PixelClockHz / (double)HTotal / 1000.0;

    public string Label => $"{HActive}x{VActive}{(Interlaced ? "i" : "")} @ {RefreshHz:0.000} Hz";
}

public sealed record StandardTiming(int Width, int Height, int RefreshHz);

/// <summary>Descriptor de rango (0xFD): límites de frecuencia que declara el monitor.</summary>
public sealed record RangeLimits(int MinVHz, int MaxVHz, int MinHKhz, int MaxHKhz, int MaxPixelClockMHz);

public sealed record EdidBaseBlock(
    string ManufacturerId,
    ushort ProductCode,
    uint SerialNumber,
    int WeekOfManufacture,
    int YearOfManufacture,
    string Version,
    bool IsDigital,
    int? BitsPerColor,
    string? Interface,
    int ScreenWidthCm,
    int ScreenHeightCm,
    double? Gamma,
    IReadOnlyList<string> EstablishedTimings,
    IReadOnlyList<StandardTiming> StandardTimings,
    IReadOnlyList<DetailedTiming> DetailedTimings,
    string? MonitorName,
    string? SerialText,
    IReadOnlyList<string> TextDescriptors,
    RangeLimits? Range,
    int ExtensionCount,
    bool ChecksumValid);

/// <summary>Bloque de datos de CTA-861 (video, audio, VSDB, HDR, FreeSync…), ya interpretado en texto legible.</summary>
public sealed record CtaDataBlock(int TagCode, string Name, string Summary, byte[] Payload);

public sealed record SupportedVideoMode(int Vic, bool Native, string Description);

public sealed record AudioFormat(string Format, int MaxChannels, IReadOnlyList<int> SampleRatesKhz);

public sealed record CtaExtension(
    int Revision,
    bool Underscan,
    bool BasicAudio,
    bool YCbCr444,
    bool YCbCr422,
    IReadOnlyList<CtaDataBlock> DataBlocks,
    IReadOnlyList<SupportedVideoMode> VideoModes,
    IReadOnlyList<AudioFormat> Audio,
    IReadOnlyList<DetailedTiming> DetailedTimings,
    /// <summary>Rango de FreeSync (VSDB de AMD), si lo declara.</summary>
    (int MinHz, int MaxHz)? FreeSyncRange,
    bool HasHdmiVsdb,
    bool HasHdrStaticMetadata,
    bool ChecksumValid);

public sealed record DisplayIdDataBlock(int Tag, string Name, int Revision, byte[] Payload);

public sealed record DisplayIdExtension(
    /// <summary>"1.2", "1.3" o "2.0".</summary>
    string Version,
    IReadOnlyList<DisplayIdDataBlock> DataBlocks,
    IReadOnlyList<DetailedTiming> DetailedTimings,
    bool ChecksumValid);

/// <summary>Bloque de extensión que no se decodifica (se informa el tag).</summary>
public sealed record UnknownExtension(int Tag, bool ChecksumValid);

public sealed record Edid(
    byte[] Raw,
    EdidBaseBlock Base,
    IReadOnlyList<CtaExtension> Cta,
    IReadOnlyList<DisplayIdExtension> DisplayId,
    IReadOnlyList<UnknownExtension> OtherExtensions)
{
    /// <summary>Todos los timings detallados (base + CTA + DisplayID), el preferido primero.</summary>
    public IEnumerable<DetailedTiming> AllDetailedTimings =>
        Base.DetailedTimings.Concat(Cta.SelectMany(c => c.DetailedTimings)).Concat(DisplayId.SelectMany(d => d.DetailedTimings));

    /// <summary>Modo nativo: el de mayor resolución y, a igual resolución, mayor refresco.</summary>
    public DetailedTiming? NativeMax =>
        AllDetailedTimings.OrderByDescending(t => (long)t.HActive * t.VActive).ThenByDescending(t => t.RefreshHz).FirstOrDefault();
}
