using KruzhLatency.Core.Debloat;
using KruzhLatency.Core.Tweaks;

namespace KruzhLatency.Core.Tests;

/// <summary>T41: catálogo de apps a quitar y lista de intocables.</summary>
public class AppxCatalogTests
{
    private static InstalledAppx Pkg(string name, bool framework = false, bool system = false) =>
        new(name, name + "_8wekyb3d8bbwe", name + "_1.0.0.0_x64__8wekyb3d8bbwe", framework, system);

    [Fact]
    public void Plan_SoloLoInstalado_YAgrupadoPorRegla()
    {
        var plan = AppxCatalog.Plan(new[]
        {
            Pkg("Microsoft.BingNews"), Pkg("Microsoft.BingWeather"), Pkg("Microsoft.GamingApp"), Pkg("Microsoft.XboxGamingOverlay"),
            Pkg("king.com.CandyCrushSaga"), Pkg("Microsoft.WindowsCalculator")
        }).ToDictionary(r => r.Rule.Id);

        Assert.Single(plan["News"].Packages);
        Assert.Equal(2, plan["XboxApps"].Packages.Count);
        Assert.Single(plan["ThirdParty"].Packages);
        Assert.False(plan.ContainsKey("Outlook"));                        // no está instalado
        Assert.DoesNotContain(plan.Values.SelectMany(r => r.Packages), p => p.Name == "Microsoft.WindowsCalculator");
    }

    [Theory]
    [InlineData("Microsoft.WindowsStore")]
    [InlineData("Microsoft.DesktopAppInstaller")]
    [InlineData("Microsoft.VCLibs.140.00")]
    [InlineData("Microsoft.UI.Xaml.2.8")]
    [InlineData("Microsoft.XboxIdentityProvider")]
    [InlineData("NVIDIACorp.NVIDIAControlPanel")]
    [InlineData("Microsoft.SecHealthUI")]
    public void Protegidos_NuncaSeProponen(string name)
    {
        Assert.True(AppxCatalog.IsProtected(name));
        Assert.Empty(AppxCatalog.Plan(new[] { Pkg(name) }));
    }

    [Fact]
    public void NingunaRegla_NombraAlgoProtegido()
    {
        Assert.All(AppxCatalog.Rules.SelectMany(r => r.NamePrefixes), p => Assert.False(AppxCatalog.IsProtected(p), p));
    }

    [Fact]
    public void FrameworksYSistema_NoSeTocan()
    {
        Assert.Empty(AppxCatalog.Plan(new[] { Pkg("Microsoft.BingNews", framework: true), Pkg("Microsoft.BingWeather", system: true) }));
    }

    [Fact]
    public void Xbox_YAppsDeUsoPersonal_TienenAviso_YNoEstanEnNormal()
    {
        foreach (var id in new[] { "XboxApps", "Outlook", "PhoneLink", "Teams", "QuickAssist" })
        {
            var rule = AppxCatalog.Rules.Single(r => r.Id == id);
            Assert.NotNull(rule.Warning);
            Assert.NotEqual(Profile.Normal, rule.From);
        }
    }

    [Fact]
    public void TareasDeTelemetria_RutasAbsolutasDelProgramador()
    {
        Assert.All(TelemetryTasks.All, t => Assert.StartsWith(@"\Microsoft\Windows\", t.Path));
    }
}
