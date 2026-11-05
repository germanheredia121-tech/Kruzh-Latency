using KruzhLatency.Core.Benchmark;

namespace KruzhLatency.Core.Tests;

public class OnboardingPlanTests
{
    [Fact]
    public void Orden_ReinicioMsiGpuDispositivos()
    {
        var q = OnboardingPlan.Queue(tweaksNeedReboot: true, msi: true, gpuAffinity: true, deviceAffinity: true);
        Assert.Equal(new[] { OnboardingPlan.Reboot, OnboardingPlan.Msi, OnboardingPlan.GpuAffinity, OnboardingPlan.DeviceAffinity }, q);
    }

    [Fact]
    public void SinMediciones_NoReinicia_AunqueLosTweaksLoPidan() =>
        Assert.Empty(OnboardingPlan.Queue(tweaksNeedReboot: true, msi: false, gpuAffinity: false, deviceAffinity: false));

    [Fact]
    public void SinTweaksConReinicio_ArrancaMidiendo() =>
        Assert.Equal(OnboardingPlan.Msi, OnboardingPlan.Queue(false, true, false, false)[0]);

    [Fact]
    public void Conflictos_MsiYAfinidad()
    {
        Assert.Equal(new[] { OnboardingPlan.ManualMsiTweak }, OnboardingPlan.ConflictingTweaks(true, false, false));
        Assert.Equal(new[] { OnboardingPlan.ManualAffinityTweak }, OnboardingPlan.ConflictingTweaks(false, true, false));
        Assert.Equal(new[] { OnboardingPlan.ManualAffinityTweak }, OnboardingPlan.ConflictingTweaks(false, false, true));
        Assert.Empty(OnboardingPlan.ConflictingTweaks(false, false, false));
    }

    [Fact]
    public void Estimacion_CrecePorMedicion()
    {
        int soloMsi = OnboardingPlan.EstimatedMinutes(true, false, false, msiDevices: 5, cores: 6);
        int todo = OnboardingPlan.EstimatedMinutes(true, true, true, msiDevices: 5, cores: 6);
        Assert.True(soloMsi > 0);
        Assert.True(todo > soloMsi);
        Assert.Equal(0, OnboardingPlan.EstimatedMinutes(false, false, false, 5, 6));
    }
}
