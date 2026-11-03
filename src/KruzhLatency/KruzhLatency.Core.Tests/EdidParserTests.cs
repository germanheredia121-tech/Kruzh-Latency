using KruzhLatency.Core.Display;

namespace KruzhLatency.Core.Tests;

/// <summary>C1: EDID real del Gigabyte GS25F2 del autor (fábrica: bloque base + CTA-861) y cálculo de Hz exactos.</summary>
public class EdidParserTests
{
    private static Edid Gs25f2() =>
        EdidParser.Parse(Convert.FromBase64String(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "edid-gs25f2-factory.b64"))));

    [Fact]
    public void BloqueBase_IdentidadYCapacidades()
    {
        var b = Gs25f2().Base;

        Assert.Equal("GBT", b.ManufacturerId);
        Assert.Equal(0x2404, b.ProductCode);
        Assert.Equal("GS25F2", b.MonitorName);
        Assert.Equal("000000000000", b.SerialText); // anonimizado en el fixture
        Assert.Equal("1.4", b.Version);
        Assert.True(b.IsDigital);
        Assert.Equal("DisplayPort", b.Interface);
        Assert.Equal(10, b.BitsPerColor);
        Assert.Equal(new RangeLimits(48, 200, 222, 222, 500), b.Range);
        Assert.Contains("640x480 @ 60 Hz", b.EstablishedTimings);
        Assert.Contains(new StandardTiming(1280, 960, 60), b.StandardTimings);
        Assert.True(b.ChecksumValid);
    }

    [Fact]
    public void Timings_Nativo1080p200_Exacto()
    {
        var e = Gs25f2();

        var preferred = Assert.Single(e.Base.DetailedTimings);
        Assert.True(preferred.IsPreferred);
        Assert.Equal(60.0, preferred.RefreshHz, 3);

        var native = e.NativeMax!;
        Assert.Equal((1920, 1080), (native.HActive, native.VActive));
        Assert.Equal((2080, 1110), (native.HTotal, native.VTotal));
        Assert.Equal(461_760_000, native.PixelClockHz);
        Assert.Equal(200.000, native.RefreshHz, 3);
        Assert.Equal((48, 32, 80), (native.HFrontPorch, native.HSyncWidth, native.HBackPorch));
    }

    [Fact]
    public void Cta861_DataBlocks()
    {
        var cta = Assert.Single(Gs25f2().Cta);

        Assert.Equal(3, cta.Revision);
        Assert.True(cta.ChecksumValid);
        Assert.Equal((48, 200), cta.FreeSyncRange);
        Assert.True(cta.HasHdrStaticMetadata);
        Assert.False(cta.HasHdmiVsdb); // monitor DisplayPort
        Assert.Contains(cta.VideoModes, v => v.Vic == 16 && v.Native);
        Assert.Equal("LPCM", Assert.Single(cta.Audio).Format);
        Assert.Equal(3, cta.DetailedTimings.Count);
        Assert.Contains(cta.DataBlocks, d => d.Name == "Speaker Allocation");
    }

    [Fact]
    public void Cvt_AutomaticPC_ReproduceLosRelojesDelChat()
    {
        // "Automatic PC" de CRU = CVT reducido. El chat midió 495.040 MHz (1080p) y 304.704 MHz (4:3) a 200 Hz.
        var native = RefreshCalculator.CvtReducedBlanking(1920, 1080, 200);
        var fourThree = RefreshCalculator.CvtReducedBlanking(1280, 960, 200);

        Assert.Equal((2080, 1190), (native.HTotal, native.VTotal));
        Assert.Equal(495_040_000, native.PixelClockHz);
        Assert.Equal((1440, 1058), (fourThree.HTotal, fourThree.VTotal));
        Assert.Equal(304_704_000, fourThree.PixelClockHz);
    }

    [Fact]
    public void DisplayId_4x3_A200Hz_Type1NoLlega_Type7Exacto()
    {
        // Lo que vio el autor en CRU: DisplayID 1.3 daba 200.003 Hz y DisplayID 2.0, 200.000 Hz.
        var r = RefreshCalculator.RecommendDisplayId(1440, 1058, 200);

        Assert.False(r.Type1.IsExact);
        Assert.InRange(Math.Abs(r.Type1.DeviationHz), 0.001, 0.005);
        Assert.True(r.Type7.IsExact);
        Assert.Equal(304_704_000, r.Type7.PixelClockHz);
        Assert.Equal(TimingSource.DisplayIdType7, r.Chosen.Source);
    }

    [Fact]
    public void DisplayId_SiAmbosSonExactos_Gana13()
    {
        var r = RefreshCalculator.RecommendDisplayId(2080, 1110, 200); // nativo de fábrica: 461.76 MHz

        Assert.True(r.Type1.IsExact && r.Type7.IsExact);
        Assert.Equal(TimingSource.DisplayIdType1, r.Chosen.Source);
    }

    [Fact]
    public void DisplayId_DescriptorType7_SeDecodificaConPasosDe1kHz()
    {
        // Type VII de 1280x960: pixel clock 304.704 MHz → 304704 kHz - 1 = 0x04A63F; resto de campos "+1".
        byte[] t =
        {
            0x3F, 0xA6, 0x04, 0x80,       // pclk-1 (LE) + opciones (preferido)
            0xFF, 0x04, 0x9F, 0x00,       // hActive 1280, hBlank 160
            0x2F, 0x80, 0x1F, 0x00,       // hFront 48 (+ polaridad +), hSync 32
            0xBF, 0x03, 0x61, 0x00,       // vActive 960, vBlank 98
            0x02, 0x00, 0x03, 0x00        // vFront 3, vSync 4
        };
        var timing = EdidParser.ParseDisplayIdTiming(t, TimingSource.DisplayIdType7);

        Assert.Equal(304_704_000, timing.PixelClockHz);
        Assert.Equal((1280, 960, 1440, 1058), (timing.HActive, timing.VActive, timing.HTotal, timing.VTotal));
        Assert.True(timing.IsPreferred);
        Assert.True(timing.HSyncPositive);
        Assert.Equal(200.000, timing.RefreshHz, 3);
    }

    [Fact]
    public void Dsc_1080p200_NoHaceFaltaEnHbr2()
    {
        double need = RefreshCalculator.DataRateGbps(461_760_000, bitsPerColor: 8);

        Assert.InRange(need, 11.0, 11.2);
        Assert.True(need < RefreshCalculator.DisplayPortPayloadGbps("HBR2"));
    }

    [Fact]
    public void NoEsEdid_Lanza()
    {
        Assert.Throws<InvalidDataException>(() => EdidParser.Parse(new byte[128]));
    }
}

/// <summary>C2: análisis de un monitor (modo actual vs máximo, override, DSC) con el EDID real.</summary>
public class MonitorAnalyzerTests
{
    private static byte[] Edid() =>
        Convert.FromBase64String(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "edid-gs25f2-factory.b64")));

    private static ActiveDisplayMode Mode(double hz, int w = 1920, int h = 1080) =>
        new(w, h, w, h, 2080, 1110, (long)(2080 * 1110 * hz), hz, "DisplayPort", "Identidad");

    [Fact]
    public void A200Hz_EstaAlMaximo_SinDsc()
    {
        var a = MonitorAnalyzer.Analyze(new MonitorInfo(@"DISPLAY\GBT2404\X", "GS25F2", true, null, Edid(), null, Mode(200)));

        Assert.False(a.RunningBelowMax);
        Assert.False(a.NeedsDscOnHbr2);
        Assert.Equal(200.0, a.NativeMax!.RefreshHz, 3);
        Assert.Contains(a.Notes, n => n.Contains("FreeSync/VRR 48–200"));
    }

    [Fact]
    public void A60Hz_AvisaQueEstaDebajo()
    {
        var a = MonitorAnalyzer.Analyze(new MonitorInfo(@"DISPLAY\GBT2404\X", "GS25F2", true, null, Edid(), null, Mode(60)));

        Assert.True(a.RunningBelowMax);
        Assert.Contains(a.Notes, n => n.Contains("60 Hz") && n.Contains("200 Hz"));
    }

    [Fact]
    public void ConOverride_UsaElOverride_YLoAvisa()
    {
        var over = Edid()[..128]; // solo bloque base (como un CRU que borró el CTA-861)
        var m = new MonitorInfo(@"DISPLAY\GBT2404\X", "GS25F2", true, null, Edid(), over, null);
        var a = MonitorAnalyzer.Analyze(m);

        Assert.True(m.HasOverride);
        Assert.Empty(a.Parsed!.Cta);
        Assert.Contains(a.Notes, n => n.Contains("EDID_OVERRIDE"));
    }

    [Fact]
    public void Fantasma_SinEdid_NoLanza()
    {
        var a = MonitorAnalyzer.Analyze(new MonitorInfo(@"DISPLAY\GBT2404\Y", "Generic Monitor", false, null, null, null, null));

        Assert.Null(a.Parsed);
        Assert.NotNull(a.ParseError);
        Assert.Contains(a.Notes, n => n.Contains("fantasma"));
    }
}
