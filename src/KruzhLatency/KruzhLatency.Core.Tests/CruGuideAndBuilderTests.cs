using KruzhLatency.Core.Display;

namespace KruzhLatency.Core.Tests;

/// <summary>C5 (guía de CRU) y C6 (generador de EDID) con el EDID real del GS25F2.</summary>
public class CruGuideAndBuilderTests
{
    private static byte[] Factory() =>
        Convert.FromBase64String(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "edid-gs25f2-factory.b64")));

    private static MonitorAnalysis Analysis(byte[]? over = null, ActiveDisplayMode? mode = null) =>
        MonitorAnalyzer.Analyze(new MonitorInfo(@"DISPLAY\GBT2404\X", "GS25F2", true, null, Factory(), over, mode));

    private static CruGuide Guide(int w = 1280, int h = 960) =>
        CruGuideBuilder.Build(new CruGuideRequest(Analysis(), w, h, GpuVendor.Nvidia));

    // ------------------------------------------------------------ C5

    [Fact]
    public void Guia_1280x960_DisplayId20_ConLosValoresDelChat()
    {
        var g = Guide();

        Assert.Equal("2.0", g.DisplayIdVersion);                           // 1.3 da 200.00x, 2.0 da 200.000
        Assert.Equal(304_704_000, g.StretchedChoice.Chosen.PixelClockHz);
        Assert.Equal((2080, 1110), (g.Native.HTotal, g.Native.VTotal));
        var all = string.Join("\n", g.Steps.SelectMany(s => s.Lines));
        Assert.Contains("200.000 Hz", all);
        Assert.Contains("Automatic PC", all);
        Assert.Contains("restart64", all);                                 // se advierte NO usarlo
        Assert.Contains("Realizar escalado en: Pantalla", all);
        Assert.Contains("el audio por el cable", all);                     // CTA-861 tiene audio, HDR y FreeSync
        Assert.Contains("FreeSync", all);
        Assert.Equal(CruGuideBuilder.CruUrl, g.Steps[0].Link);
        Assert.Contains(g.Steps, s => s.Title == "Si algo sale mal");
    }

    [Fact]
    public void Guia_Amd_UsaAdrenalin()
    {
        var g = CruGuideBuilder.Build(new CruGuideRequest(Analysis(), 1280, 960, GpuVendor.Amd));

        Assert.Contains(g.Steps, s => s.Title.Contains("AMD"));
        Assert.DoesNotContain(g.Steps, s => s.Title.Contains("NVIDIA"));
    }

    [Fact]
    public void Verificar_ConEdidDeFabrica_FallaLoQueCorresponde()
    {
        var checks = CruGuideBuilder.Verify(Guide(), Analysis()).ToDictionary(c => c.Name);

        Assert.False(checks["EDID modificado activo"].Ok);
        Assert.False(checks["Bloque DisplayID 2.0"].Ok);
        Assert.False(checks["1280x960 @ 200 Hz"].Ok);
    }

    // ------------------------------------------------------------ C6

    [Fact]
    public void Generador_DisplayId20_IdaYVueltaPorElParser()
    {
        var native = EdidParser.Parse(Factory()).NativeMax!;
        var (n, s) = EdidBuilder.ProposedTimings(native, 1280, 960);
        var bytes = EdidBuilder.BuildDisplayIdOverride(Factory(), displayId20: true, new[] { n, s });

        var e = EdidParser.Parse(bytes);
        Assert.Equal(256, bytes.Length);
        Assert.True(e.Base.ChecksumValid);
        Assert.Equal(1, e.Base.ExtensionCount);
        Assert.Empty(e.Cta);                                               // CTA-861 reemplazado
        var did = Assert.Single(e.DisplayId);
        Assert.True(did.ChecksumValid);
        Assert.Equal("2.0", did.Version);
        Assert.Equal(2, did.DetailedTimings.Count);
        Assert.All(did.DetailedTimings, t => Assert.Equal(200.000, t.RefreshHz, 3));
        Assert.True(did.DetailedTimings[0].IsPreferred);
        Assert.Equal(304_704_000, did.DetailedTimings[1].PixelClockHz);
        Assert.Equal((48, 32, 80, 3, 4), (did.DetailedTimings[1].HFrontPorch, did.DetailedTimings[1].HSyncWidth, did.DetailedTimings[1].HBackPorch,
                                          did.DetailedTimings[1].VFrontPorch, did.DetailedTimings[1].VSyncWidth));
        Assert.Equal("GS25F2", e.Base.MonitorName);                       // bloque base intacto
    }

    [Fact]
    public void Generador_DisplayId13_RedondeaA10kHz()
    {
        var native = EdidParser.Parse(Factory()).NativeMax!;
        var (n, s) = EdidBuilder.ProposedTimings(native, 1280, 960);
        var did = EdidParser.Parse(EdidBuilder.BuildDisplayIdOverride(Factory(), displayId20: false, new[] { n, s })).DisplayId.Single();

        Assert.Equal("1.3", did.Version);
        Assert.Equal(200.000, did.DetailedTimings[0].RefreshHz, 3);        // nativo: exacto también en 1.3
        Assert.NotEqual(200.000, Math.Round(did.DetailedTimings[1].RefreshHz, 3)); // la 4:3 no
    }

    [Fact]
    public void Verificar_ConElEdidGenerado_TodoOk()
    {
        var guide = Guide();
        var over = EdidBuilder.BuildDisplayIdOverride(Factory(), displayId20: true, new[] { guide.Native, guide.Stretched });
        var mode = new ActiveDisplayMode(1920, 1080, 1920, 1080, 2080, 1110, 461_760_000, 200, "DisplayPort", "Identidad");

        var checks = CruGuideBuilder.Verify(guide, Analysis(over, mode));

        Assert.All(checks, c => Assert.True(c.Ok, $"{c.Name}: {c.Detail}"));
    }
}

/// <summary>C6: script de vuelta atrás y tareas programadas (ADR-009).</summary>
public class EdidRollbackPlanTests
{
    private static readonly EdidRollbackPaths Paths = new(@"DISPLAY\GBT2404\5&3771edd3&0&UID176386", @"C:\ProgramData\KruzhLatency", null);

    [Fact]
    public void Script_SinConfirmacion_BorraOverrideYReinicia()
    {
        var s = EdidRollbackPlan.BuildScript(Paths);

        int check = s.IndexOf(@"if exist ""C:\ProgramData\KruzhLatency\edid-confirmed.flag"" goto confirmed", StringComparison.Ordinal);
        int delete = s.IndexOf(@"reg delete ""HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\GBT2404\5&3771edd3&0&UID176386\Device Parameters\EDID_OVERRIDE"" /f", StringComparison.Ordinal);
        int reboot = s.IndexOf("shutdown /r", StringComparison.Ordinal);
        Assert.True(check >= 0 && delete > check && reboot > delete);
        Assert.DoesNotContain("reg import", s);       // no había override previo
        Assert.DoesNotContain("restart64", s);
        Assert.Contains(@"schtasks /Delete /TN ""KRUZH\EdidConfirm"" /F", s);
        Assert.Contains(@"schtasks /Delete /TN ""KRUZH\EdidRollback"" /F", s);
    }

    [Fact]
    public void Script_ConOverridePrevio_LoRestaura()
    {
        var s = EdidRollbackPlan.BuildScript(Paths with { PreviousOverrideReg = @"C:\ProgramData\KruzhLatency\edid-previo.reg" });

        Assert.True(s.IndexOf("reg import", StringComparison.Ordinal) > s.IndexOf("reg delete", StringComparison.Ordinal));
    }

    [Fact]
    public void Tareas_RollbackComoSystemA60s_ConfirmacionDelUsuario()
    {
        var rollback = EdidRollbackPlan.RollbackTaskArgs(Paths);
        var confirm = EdidRollbackPlan.ConfirmTaskArgs(@"C:\Apps\KRUZH\KruzhLatency.exe");

        Assert.Contains("/SC ONLOGON /DELAY 0001:00 /RU SYSTEM", rollback);
        Assert.Contains("edid-rollback.cmd", rollback);
        Assert.Contains("--edid-confirm", confirm);
        Assert.Contains("/RL HIGHEST", confirm);
        Assert.DoesNotContain("/RU SYSTEM", confirm);   // la confirmación corre en la sesión del usuario
    }
}
