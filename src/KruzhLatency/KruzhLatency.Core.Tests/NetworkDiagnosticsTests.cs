using KruzhLatency.Core.Network;

namespace KruzhLatency.Core.Tests;

public class NetworkDiagnosticsTests
{
    private static HopSamples Hop(int ttl, string? ip, int sent, int lost, double rtt = 10) =>
        new(ttl, ip, sent, Enumerable.Range(0, sent - lost).Select(i => rtt + (i % 3)).ToList());

    [Fact]
    public void PerdidaSoloEnUnSaltoDelMedio_NoEsReal()
    {
        var r = PathAnalysis.Analyze(new[]
        {
            Hop(1, "192.168.1.1", 100, 0, 1),
            Hop(2, "100.70.0.1", 100, 0, 5),
            Hop(3, "200.1.1.1", 100, 40, 8),   // router que no prioriza pings
            Hop(4, "200.2.2.2", 100, 0, 12),
            Hop(5, "155.133.1.1", 100, 0, 15),
        }, "155.133.1.1");

        Assert.Null(r.LossOrigin);
        Assert.Equal(HopVerdict.RateLimited, r.Hops[2].Verdict);
        Assert.Contains("Sin pérdida real", r.Summary);
    }

    [Fact]
    public void PerdidaQueEmpiezaEnElProveedorYSigue_EsRealYSeUbica()
    {
        var r = PathAnalysis.Analyze(new[]
        {
            Hop(1, "192.168.1.1", 100, 0, 1),
            Hop(2, "100.70.0.1", 100, 5, 5),
            Hop(3, "200.1.1.1", 100, 6, 8),
            Hop(4, "200.2.2.2", 100, 5, 12),
            Hop(5, "155.133.1.1", 100, 5, 15),
        }, "155.133.1.1");

        Assert.NotNull(r.LossOrigin);
        Assert.Equal(2, r.LossOrigin!.Hop.Ttl);
        Assert.Equal(HopZone.IspAccess, r.LossOrigin.Zone);
        Assert.Equal(HopVerdict.LossInherited, r.Hops[3].Verdict);
        Assert.Contains("tu proveedor", r.Summary);
    }

    [Fact]
    public void SaltoQueNoResponde_EsSilencioso() =>
        Assert.Equal(HopVerdict.Silent, PathAnalysis.Analyze(new[] { Hop(1, "192.168.1.1", 10, 0), Hop(2, null, 10, 10), Hop(3, "8.8.8.8", 10, 0) }, "8.8.8.8").Hops[1].Verdict);

    [Theory]
    [InlineData("192.168.1.1", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("100.64.5.5", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("200.45.1.1", false)]
    public void Privadas_Y_Cgnat(string ip, bool expected) => Assert.Equal(expected, PathAnalysis.IsPrivateOrCgnat(ip));

    [Fact]
    public void FlujoParejo_SinHuecos()
    {
        var arrivals = Enumerable.Range(0, 1000).Select(i => i * 15.625).ToList(); // 64 paquetes/s
        var f = FlowAnalysis.Analyze("1.2.3.4:27015", arrivals);
        Assert.Empty(f.Gaps);
        Assert.InRange(f.PacketsPerSecond, 63, 65);
        Assert.Contains("Bajada pareja", f.Summary);
    }

    [Fact]
    public void FlujoConUnHueco_LoDetectaConSuMomento()
    {
        var arrivals = Enumerable.Range(0, 500).Select(i => i * 15.625).Concat(Enumerable.Range(0, 500).Select(i => 500 * 15.625 + 120 + i * 15.625)).ToList();
        var f = FlowAnalysis.Analyze("1.2.3.4:27015", arrivals);
        var g = Assert.Single(f.Gaps);
        Assert.InRange(g.GapMs, 130, 140);
        Assert.InRange(g.AtSeconds, 7.7, 7.9);
    }

    [Fact]
    public void PocosPaquetes_AvisaQueNoHabiaPartida() =>
        Assert.Contains("muy pocos", FlowAnalysis.Analyze("x", new double[] { 1, 2, 3 }).Summary);

    private static List<double> Steady(int n, double every) => Enumerable.Range(0, n).Select(i => i * every).ToList();

    private static List<double> WithGapAt(int n, double every, int afterIndex, double gapMs) =>
        Enumerable.Range(0, n).Select(i => i * every + (i > afterIndex ? gapMs : 0)).ToList();

    [Fact]
    public void Subida_HuecoDeEnvio_SeDescribeComoComandosTarde()
    {
        var up = FlowAnalysis.Analyze("x", WithGapAt(1000, 7.8, 400, 90), FlowDirection.ToServer);
        Assert.Single(up.Gaps);
        Assert.Contains("CS2 no mandó nada", up.Summary);
    }

    [Fact]
    public void Veredicto_HuecosSoloDeSubida_CulpaALaPc()
    {
        var down = FlowAnalysis.Analyze("x", Steady(1000, 15.625));
        var up = FlowAnalysis.Analyze("x", WithGapAt(2000, 7.8, 900, 90), FlowDirection.ToServer);
        Assert.Contains("la causa está en tu PC", FlowAnalysis.Verdict(down, up));
    }

    [Fact]
    public void Veredicto_HuecosSoloDeBajada_CulpaALaRed()
    {
        var down = FlowAnalysis.Analyze("x", WithGapAt(1000, 15.625, 500, 120));
        var up = FlowAnalysis.Analyze("x", Steady(2000, 7.8), FlowDirection.ToServer);
        Assert.Contains("red de bajada", FlowAnalysis.Verdict(down, up));
    }

    [Fact]
    public void Veredicto_HuecosSimultaneos_LaPcEnteraSeFreno()
    {
        // El mismo corte de 120 ms a los ~7,8 s en las dos direcciones.
        var down = FlowAnalysis.Analyze("x", WithGapAt(1000, 15.625, 500, 120));
        var up = FlowAnalysis.Analyze("x", WithGapAt(2000, 7.8125, 1000, 120), FlowDirection.ToServer);
        Assert.Equal(1, FlowAnalysis.SimultaneousGaps(down, up));
        Assert.Contains("la PC entera se frenó", FlowAnalysis.Verdict(down, up));
    }

    [Fact]
    public void Veredicto_TodoParejo_MandaAProbarLaRuta() =>
        Assert.Contains("probá la ruta", FlowAnalysis.Verdict(
            FlowAnalysis.Analyze("x", Steady(1000, 15.625)), FlowAnalysis.Analyze("x", Steady(2000, 7.8), FlowDirection.ToServer)));
}
