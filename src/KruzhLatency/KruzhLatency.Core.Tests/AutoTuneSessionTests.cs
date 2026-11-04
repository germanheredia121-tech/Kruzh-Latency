using System.Xml.Linq;
using KruzhLatency.Core.Benchmark;

namespace KruzhLatency.Core.Tests;

public class AutoTuneSessionTests
{
    private static AutoTuneSession Session() => new()
    {
        Kind = "msi",
        TweakIds = { "AutoMsi" },
        Steps =
        {
            new AutoTuneStep { Id = "a", Title = "USB: sin MSI" },
            new AutoTuneStep { Id = "b", Title = "USB: con MSI" },
            new AutoTuneStep { Id = "c", Title = "GPU: con MSI", NeedsReboot = true },
        }
    };

    [Fact]
    public void Avanza_PasoAPaso()
    {
        var s = Session();
        Assert.Equal("a", s.Current!.Id);
        s.Start("a");
        s.Finish("a", ok: true, "180 µs p99");
        Assert.Equal("b", s.Current!.Id);
        s.Start("b");
        s.Finish("b", ok: false, "el dispositivo no arrancó");
        Assert.Equal("c", s.Current!.Id);
        Assert.False(s.IsFinished);
    }

    [Fact]
    public void SobreviveAlReinicio_ElPasoEnCursoSigueSiendoElActual()
    {
        var s = Session();
        s.Start("a"); s.Finish("a", true, "ok");
        s.Start("c");          // se reinicia la PC en medio de este paso
        s.Reboots++;
        s.Data["c:pre"] = "{\"limit\":1}";

        var back = AutoTuneSession.FromJson(s.ToJson())!;
        Assert.Equal("c", back.Current!.Id);
        Assert.Equal(AutoTuneStepStatus.Running, back.Current.Status);
        Assert.Equal(1, back.Reboots);
        Assert.Equal("{\"limit\":1}", back.Data["c:pre"]);
        Assert.Equal(new[] { "AutoMsi" }, back.TweakIds);
    }

    [Fact]
    public void Abort_SaltaLoQueFalta()
    {
        var s = Session();
        s.Start("a"); s.Finish("a", true, "ok");
        s.Abort("cancelado por el usuario");
        Assert.True(s.IsFinished);
        Assert.Equal(AutoTuneStepStatus.Done, s.Step("a").Status);
        Assert.All(s.Steps.Skip(1), x => Assert.Equal(AutoTuneStepStatus.Skipped, x.Status));
    }

    [Fact]
    public void JsonCorrupto_DevuelveNull() => Assert.Null(AutoTuneSession.FromJson("{ no es json"));

    [Fact]
    public void LimiteDeReinicios()
    {
        var s = Session();
        s.Reboots = AutoTuneSession.MaxReboots;
        Assert.True(s.RebootBudgetExceeded);
    }

    [Fact]
    public void TareaDeRetomado_ElevadaYConArgumento()
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var doc = XDocument.Parse(AutoTuneResume.TaskXml(@"C:\Program Files\KRUZH\KruzhLatency.exe", @"PC\yo"));
        Assert.Equal("HighestAvailable", doc.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal(AutoTuneResume.Argument, doc.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal("PT20S", doc.Descendants(ns + "Delay").Single().Value);
    }
}
