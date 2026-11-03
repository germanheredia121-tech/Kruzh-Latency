namespace KruzhLatency.Core.Display;

/// <summary>
/// Spec 002 / C6 (ADR-009): arma un EDID nuevo = bloque base original + una extensión DisplayID 1.3 (Type I) o
/// 2.0 (Type VII) con los timings pedidos, reemplazando CTA-861. Es la misma operación que la guía de CRU, sin CRU.
/// Función pura: el resultado se valida releyéndolo con <see cref="EdidParser"/>.
/// </summary>
public static class EdidBuilder
{
    public static byte[] BuildDisplayIdOverride(ReadOnlySpan<byte> originalEdid, bool displayId20, IReadOnlyList<DetailedTiming> timings)
    {
        if (originalEdid.Length < 128) throw new ArgumentException("EDID incompleto.", nameof(originalEdid));
        if (timings.Count == 0 || timings.Count > 6) throw new ArgumentException("Entre 1 y 6 timings (entran en un bloque de 128 bytes).", nameof(timings));

        var result = new byte[256];
        originalEdid[..128].CopyTo(result);
        result[126] = 1; // una sola extensión: la DisplayID
        result[127] = ChecksumByte(result.AsSpan(0, 127));

        var ext = result.AsSpan(128, 128);
        var source = displayId20 ? TimingSource.DisplayIdType7 : TimingSource.DisplayIdType1;
        int payload = 3 + 20 * timings.Count;      // encabezado del data block + descriptores
        ext[0] = 0x70;                             // tag de extensión DisplayID
        ext[1] = displayId20 ? (byte)0x20 : (byte)0x13;
        ext[2] = (byte)payload;
        ext[3] = 0x00;                             // tipo de producto / uso: igual que el bloque base
        ext[4] = 0x00;                             // sin extensiones DisplayID adicionales

        int p = 5;
        ext[p++] = displayId20 ? (byte)0x22 : (byte)0x03; // Type VII / Type I Detailed Timing
        ext[p++] = 0x00;                                    // revisión
        ext[p++] = (byte)(20 * timings.Count);
        for (int i = 0; i < timings.Count; i++)
        {
            WriteDescriptor(ext.Slice(p, 20), timings[i] with { Source = source }, preferred: i == 0);
            p += 20;
        }

        // Checksum de la sección DisplayID (bytes 1..5+payload) y del bloque EDID completo.
        ext[5 + payload] = ChecksumByte(ext.Slice(1, 4 + payload));
        ext[127] = ChecksumByte(ext[..127]);
        return result;
    }

    /// <summary>Descriptor de 20 bytes (inverso de <see cref="EdidParser.ParseDisplayIdTiming"/>). Todos los campos van "-1".</summary>
    public static void WriteDescriptor(Span<byte> d, DetailedTiming t, bool preferred)
    {
        long step = RefreshCalculator.PixelClockStepHz(t.Source);
        long raw = (long)Math.Round((double)t.PixelClockHz / step) - 1;
        if (raw < 0 || raw > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(t), "Pixel clock fuera de rango.");
        d[0] = (byte)raw; d[1] = (byte)(raw >> 8); d[2] = (byte)(raw >> 16);
        d[3] = (byte)((preferred ? 0x80 : 0) | (t.Interlaced ? 0x10 : 0) | AspectCode(t.HActive, t.VActive));
        Put(d, 4, t.HActive - 1);
        Put(d, 6, t.HBlank - 1);
        Put(d, 8, (t.HFrontPorch - 1) | (t.HSyncPositive ? 0x8000 : 0));
        Put(d, 10, t.HSyncWidth - 1);
        Put(d, 12, t.VActive - 1);
        Put(d, 14, t.VBlank - 1);
        Put(d, 16, (t.VFrontPorch - 1) | (t.VSyncPositive ? 0x8000 : 0));
        Put(d, 18, t.VSyncWidth - 1);
    }

    private static void Put(Span<byte> d, int offset, int value)
    {
        if (value is < 0 or > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(value));
        d[offset] = (byte)value;
        d[offset + 1] = (byte)(value >> 8);
    }

    /// <summary>Código de relación de aspecto de DisplayID: 0=1:1, 1=5:4, 2=4:3, 3=15:9, 4=16:9, 5=16:10, 6=64:27, 7=256:135, 8=sin definir.</summary>
    private static byte AspectCode(int w, int h)
    {
        bool Is(int a, int b) => w * b == h * a;
        return Is(1, 1) ? (byte)0 : Is(5, 4) ? (byte)1 : Is(4, 3) ? (byte)2 : Is(15, 9) ? (byte)3 : Is(16, 9) ? (byte)4
             : Is(16, 10) ? (byte)5 : Is(64, 27) ? (byte)6 : Is(256, 135) ? (byte)7 : (byte)8;
    }

    private static byte ChecksumByte(ReadOnlySpan<byte> covered)
    {
        int sum = 0;
        foreach (byte b in covered) sum += b;
        return (byte)((256 - sum % 256) % 256);
    }

    /// <summary>
    /// Los timings que propone KRUZH (mismo resultado que la guía manual): el nativo de fábrica y la 4:3 con CVT reducido,
    /// con el pixel clock exacto para el refresco nativo redondeado.
    /// </summary>
    public static (DetailedTiming Native, DetailedTiming Stretched) ProposedTimings(DetailedTiming native, int stretchedW, int stretchedH)
    {
        double hz = Math.Round(native.RefreshHz);
        var nativeExact = native with { PixelClockHz = (long)Math.Round(hz * native.HTotal * native.VTotal) };
        var stretched = RefreshCalculator.CvtReducedBlanking(stretchedW, stretchedH, hz);
        return (nativeExact, stretched);
    }
}
