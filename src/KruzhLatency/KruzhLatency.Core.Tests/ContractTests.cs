using KruzhLatency.Core.Abstractions;
using KruzhLatency.Core.Hardware;
using KruzhLatency.Core.Tests.Fakes;
using KruzhLatency.Core.Tweaks;

namespace KruzhLatency.Core.Tests;

public class RegValueTests
{
    [Fact]
    public void Binary_SeComparaPorContenido_YNoComparteElArray()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var a = RegValue.Binary(bytes);
        bytes[0] = 99; // mutar el original no debe afectar al valor

        Assert.Equal(RegValue.Binary(new byte[] { 1, 2, 3 }), a);
        Assert.NotEqual(RegValue.Binary(new byte[] { 1, 2, 4 }), a);
    }

    [Fact]
    public void MismoDatoDistintoTipo_NoSonIguales()
    {
        Assert.NotEqual(RegValue.String("0"), RegValue.DWord(0));
    }

    [Fact]
    public void DWord_AceptaValoresMayoresAIntMax()
    {
        // Ej. NetworkThrottlingIndex = 0xFFFFFFFF, NtfsDisableLastAccessUpdate = 0x80000003 (causa del crash de v3)
        var v = RegValue.DWord(0xFFFFFFFF);
        Assert.Equal(0xFFFFFFFFu, v.AsDWord());
    }
}

public class TweakDefinitionValidationTests
{
    private static TweakDefinition Valid(Action<TweakDefinitionBuilder>? tweak = null)
    {
        var b = new TweakDefinitionBuilder();
        tweak?.Invoke(b);
        return b.Build();
    }

    [Fact]
    public void DefinicionCompleta_EsValida()
    {
        Assert.Empty(Valid().Validate());
    }

    [Fact]
    public void SinWhyNiSource_EsInvalida()
    {
        var t = Valid(b => { b.Why = ""; b.Source = " "; });
        var errors = t.Validate();
        Assert.Contains(errors, e => e.Contains("Why"));
        Assert.Contains(errors, e => e.Contains("Source"));
    }

    [Fact]
    public void OpcionSinOperaciones_EsInvalida()
    {
        var t = Valid(b => b.Options = new[] { new TweakOption("On", "On", "", Array.Empty<Operation>()) });
        Assert.Contains(t.Validate(), e => e.Contains("no tiene operaciones"));
    }

    [Theory]
    [InlineData(Profile.Normal)]
    [InlineData(Profile.Competitivo)]
    [InlineData(Profile.Profesional)]
    public void RompeAnticheat_SoloPuedeEstarEnEntusiasta(Profile perfilNoPermitido)
    {
        var t = Valid(b =>
        {
            b.Anticheat = AnticheatImpact.MayBreakAnticheat;
            b.Profiles = new HashSet<Profile> { perfilNoPermitido, Profile.Entusiasta };
        });
        Assert.Contains(t.Validate(), e => e.Contains("ADR-004"));
    }

    [Fact]
    public void RompeAnticheat_EnEntusiastaSolo_EsValido()
    {
        var t = Valid(b =>
        {
            b.Anticheat = AnticheatImpact.MayBreakAnticheat;
            b.Security = SecurityImpact.ReducesSecurity;
            b.Profiles = new HashSet<Profile> { Profile.Entusiasta };
        });
        Assert.Empty(t.Validate());
    }

    [Fact]
    public void DefaultOptionKeyInexistente_EsInvalido()
    {
        var t = Valid(b => b.DefaultOptionKey = "NoExiste");
        Assert.Contains(t.Validate(), e => e.Contains("DefaultOptionKey"));
    }

    [Fact]
    public void RequiresReboot_SeDerivaDeLasOperaciones()
    {
        Assert.False(Valid().RequiresReboot);
    }
}

public class TweakStateEvaluatorTests
{
    private const string Key = @"SYSTEM\Test";
    private static readonly HardwareProfile Hw = new() { WindowsBuild = 26100, Chassis = ChassisKind.Desktop };

    private static TweakDefinition TwoOpTweak(Func<HardwareProfile, Gate>? applicable = null) => new TweakDefinitionBuilder
    {
        Options = new[]
        {
            new TweakOption("On", "On", "", new Operation[]
            {
                new TestDwordOperation(Key, "A", 1),
                new TestDwordOperation(Key, "B", 1),
            })
        },
        Applicable = applicable ?? (_ => Gate.Pass())
    }.Build();

    [Fact]
    public void NingunaOperacionEnObjetivo_EsNotApplied()
    {
        var sys = new FakeSystemContext();
        Assert.Equal(TweakStatus.NotApplied, TweakStateEvaluator.Evaluate(TwoOpTweak(), Hw, sys).Status);
    }

    [Fact]
    public void UnaDeDos_EsPartial_NoApplied()
    {
        // Bug B7 de v3: se miraba un solo valor y el tweak figuraba "aplicado" a medias.
        var sys = new FakeSystemContext();
        sys.Registry.SetValue(RegHive.LocalMachine, Key, "A", RegValue.DWord(1));

        var state = TweakStateEvaluator.Evaluate(TwoOpTweak(), Hw, sys);

        Assert.Equal(TweakStatus.Partial, state.Status);
        Assert.Equal("On", state.ActiveOptionKey);
    }

    [Fact]
    public void TodasEnObjetivo_EsApplied()
    {
        var sys = new FakeSystemContext();
        sys.Registry.SetValue(RegHive.LocalMachine, Key, "A", RegValue.DWord(1));
        sys.Registry.SetValue(RegHive.LocalMachine, Key, "B", RegValue.DWord(1));
        Assert.Equal(TweakStatus.Applied, TweakStateEvaluator.Evaluate(TwoOpTweak(), Hw, sys).Status);
    }

    [Fact]
    public void GateDeAplicabilidadFalla_EsNotApplicable_ConMotivo()
    {
        var sys = new FakeSystemContext();
        var state = TweakStateEvaluator.Evaluate(TwoOpTweak(_ => Gate.Fail("Solo NVIDIA")), Hw, sys);
        Assert.Equal(TweakStatus.NotApplicable, state.Status);
        Assert.Equal("Solo NVIDIA", state.Applicability.Reason);
    }

    [Fact]
    public void OperacionesNoPresentes_NoCuentan()
    {
        // Una op sobre una clave que no existe en esta PC (ej. NIC sin ese keyword) no impide "Applied".
        var sys = new FakeSystemContext();
        sys.Registry.SetValue(RegHive.LocalMachine, Key, "A", RegValue.DWord(1));
        var tweak = new TweakDefinitionBuilder
        {
            Options = new[]
            {
                new TweakOption("On", "On", "", new Operation[]
                {
                    new TestDwordOperation(Key, "A", 1),
                    new TestDwordOperation(@"SYSTEM\NoExiste", "X", 1, onlyIfKeyExists: true),
                })
            }
        }.Build();

        Assert.Equal(TweakStatus.Applied, TweakStateEvaluator.Evaluate(tweak, Hw, sys).Status);
    }

    [Fact]
    public void TodoNoPresente_EsNotApplicable()
    {
        var sys = new FakeSystemContext();
        var tweak = new TweakDefinitionBuilder
        {
            Options = new[] { new TweakOption("On", "On", "", new Operation[] { new TestDwordOperation(@"SYSTEM\NoExiste", "X", 1, onlyIfKeyExists: true) }) }
        }.Build();
        Assert.Equal(TweakStatus.NotApplicable, TweakStateEvaluator.Evaluate(tweak, Hw, sys).Status);
    }

    [Fact]
    public void ConVariasOpciones_DetectaCualEstaAplicada()
    {
        var sys = new FakeSystemContext();
        sys.Registry.SetValue(RegHive.LocalMachine, Key, "Mode", RegValue.DWord(2));
        var tweak = new TweakDefinitionBuilder
        {
            Options = new[]
            {
                new TweakOption("Uno", "Uno", "", new Operation[] { new TestDwordOperation(Key, "Mode", 1) }),
                new TweakOption("Dos", "Dos", "", new Operation[] { new TestDwordOperation(Key, "Mode", 2) }),
            }
        }.Build();

        var state = TweakStateEvaluator.Evaluate(tweak, Hw, sys);
        Assert.Equal(TweakStatus.Applied, state.Status);
        Assert.Equal("Dos", state.ActiveOptionKey);
    }
}

public class OperationContractTests
{
    private const string Key = @"SYSTEM\Test";

    [Fact]
    public void CaptureApplyRestore_ValorQueNoExistia_SeBorraAlRestaurar()
    {
        var sys = new FakeSystemContext();
        var op = new TestDwordOperation(Key, "V", 7);

        var snap = op.Capture(sys);
        op.Apply(sys);
        Assert.Equal(OpState.Target, op.Read(sys));

        op.Restore(sys, snap);
        Assert.Null(sys.Registry.GetValue(RegHive.LocalMachine, Key, "V"));
        Assert.True(sys.Registry.KeyExists(RegHive.LocalMachine, Key)); // nunca se borra la clave
    }

    [Fact]
    public void CaptureApplyRestore_ValorExistente_VuelveExacto()
    {
        var sys = new FakeSystemContext();
        sys.Registry.SetValue(RegHive.LocalMachine, Key, "V", RegValue.DWord(3));
        var op = new TestDwordOperation(Key, "V", 7);

        var snap = op.Capture(sys);
        op.Apply(sys);
        op.Restore(sys, snap);

        Assert.Equal(RegValue.DWord(3), sys.Registry.GetValue(RegHive.LocalMachine, Key, "V"));
    }

    [Fact]
    public void Restore_ConSnapshotDeOtraOperacion_Lanza()
    {
        var sys = new FakeSystemContext();
        var a = new TestDwordOperation(Key, "A", 1);
        var b = new TestDwordOperation(Key, "B", 1);
        Assert.Throws<InvalidOperationException>(() => b.Restore(sys, a.Capture(sys)));
    }
}

public class FakeSystemTests
{
    [Fact]
    public void Registro_EsCaseInsensitive_YDeleteNoBorraLaClave()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Foo", "Start", RegValue.DWord(3));

        Assert.Equal(RegValue.DWord(3), reg.GetValue(RegHive.LocalMachine, @"system\currentcontrolset\services\FOO", "start"));
        reg.DeleteValue(RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Foo", "Start");
        Assert.True(reg.KeyExists(RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Foo"));
        Assert.Null(reg.GetValue(RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Foo", "Start"));
        Assert.Contains("Foo", reg.GetSubKeyNames(RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services"));
    }

    [Fact]
    public void Registro_HivesSonIndependientes()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegHive.CurrentUser, @"Control Panel\Mouse", "MouseSpeed", RegValue.String("0"));
        Assert.Null(reg.GetValue(RegHive.LocalMachine, @"Control Panel\Mouse", "MouseSpeed"));
    }

    [Fact]
    public void Servicios_NoExistente_DevuelveNull_YDisabledNoArranca()
    {
        var svc = new FakeServiceManager();
        Assert.Null(svc.Get("NoExiste"));

        svc.Install("DiagTrack", ServiceStartMode.Automatic);
        svc.SetStartMode("DiagTrack", ServiceStartMode.Disabled, false);
        Assert.True(svc.Stop("DiagTrack", TimeSpan.FromSeconds(1)));
        Assert.False(svc.Start("DiagTrack", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Energia_EscribirEnEsquemaInexistente_Lanza_YDuplicarCopiaValores()
    {
        var sub = Guid.NewGuid();
        var setting = Guid.NewGuid();
        var power = new FakePowerSettings(FakeSystemContext.DefaultScheme);
        power.WriteAcValue(FakeSystemContext.DefaultScheme, sub, setting, 1);

        Assert.Throws<InvalidOperationException>(() => power.WriteAcValue(Guid.NewGuid(), sub, setting, 1));

        var copy = power.DuplicateScheme(FakeSystemContext.DefaultScheme, "KRUZH");
        Assert.Equal(1u, power.ReadAcValue(copy, sub, setting));
    }

    [Fact]
    public void Dispositivos_ReinicioPuedeDejarCodigoDeError()
    {
        var dm = new FakeDeviceManager { ProblemCodeAfterRestart = _ => 10 };
        dm.Add(new DeviceInfo(@"PCI\VEN_10DE&DEV_2684\1", "GPU", "Display", Guid.Empty, true, 0, null,
            new InterruptCapabilities(true, true, true, 16)));

        var r = dm.Restart(@"PCI\VEN_10DE&DEV_2684\1", TimeSpan.FromSeconds(5));

        Assert.False(r.IsHealthy);
        Assert.Equal(10u, dm.GetDevice(@"PCI\VEN_10DE&DEV_2684\1")!.ProblemCode);
    }

    [Fact]
    public async Task Procesos_SinRespuestaProgramada_FallaConExitCode()
    {
        var p = new FakeProcessRunner();
        var r = await p.RunAsync("bcdedit.exe", new[] { "/enum" }, TimeSpan.FromSeconds(1));
        Assert.False(r.Succeeded);
        Assert.Single(p.Calls);
    }
}

/// <summary>Builder de tests con valores válidos por defecto.</summary>
public sealed class TweakDefinitionBuilder
{
    public string Why { get; set; } = "Motivo de prueba.";
    public string Source { get; set; } = "Fuente de prueba.";
    public AnticheatImpact Anticheat { get; set; } = AnticheatImpact.Safe;
    public SecurityImpact Security { get; set; } = SecurityImpact.None;
    public IReadOnlySet<Profile> Profiles { get; set; } = new HashSet<Profile> { Profile.Competitivo, Profile.Profesional };
    public string? DefaultOptionKey { get; set; }
    public Func<HardwareProfile, Gate> Applicable { get; set; } = _ => Gate.Pass();
    public IReadOnlyList<TweakOption> Options { get; set; } = new[]
    {
        new TweakOption("On", "On", "", new Operation[] { new TestDwordOperation(@"SYSTEM\Test", "X", 1) })
    };

    public TweakDefinition Build() => new()
    {
        Id = "TestTweak",
        Code = "T00",
        Title = "Tweak de prueba",
        Category = TweakCategory.SystemCpu,
        Why = Why,
        Source = Source,
        Anticheat = Anticheat,
        Security = Security,
        Profiles = Profiles,
        DefaultOptionKey = DefaultOptionKey,
        Applicable = Applicable,
        Options = Options
    };
}
