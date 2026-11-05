namespace KruzhLatency.Core.Benchmark;

/// <summary>
/// Asistente de optimización completa: qué corre después de aplicar los tweaks elegidos y en qué orden.
/// <list type="number">
/// <item>Reinicio, si algún tweak lo pide y hay mediciones: se mide el sistema como va a quedar, no a medio aplicar.</item>
/// <item>MSI: cambia el modo de interrupción de cada dispositivo, así que va antes que la afinidad.</item>
/// <item>Afinidad de la GPU (con el modo MSI ya elegido).</item>
/// <item>Afinidad del USB del mouse y del NVMe.</item>
/// </list>
/// </summary>
public static class OnboardingPlan
{
    public const string Reboot = "reboot";
    public const string Msi = "msi";
    public const string GpuAffinity = "gpu-affinity";
    public const string DeviceAffinity = "affinity";

    /// <summary>Tweaks del catálogo que tocan lo mismo que una medición (se sacan del lote si se elige medir).</summary>
    public const string ManualMsiTweak = "PciMsiModeV3";
    public const string ManualAffinityTweak = "PciIrqSmartBalancer";

    public static IReadOnlyList<string> Queue(bool tweaksNeedReboot, bool msi, bool gpuAffinity, bool deviceAffinity)
    {
        var q = new List<string>();
        bool anyMeasure = msi || gpuAffinity || deviceAffinity;
        if (tweaksNeedReboot && anyMeasure) q.Add(Reboot);
        if (msi) q.Add(Msi);
        if (gpuAffinity) q.Add(GpuAffinity);
        if (deviceAffinity) q.Add(DeviceAffinity);
        return q;
    }

    /// <summary>
    /// Tweaks manuales que no deben aplicarse si se mide lo mismo: "MSI en todos" pisaría lo que decide la medición de MSI,
    /// y el reparto fijo de interrupciones (I02) toca la misma afinidad que miden la GPU y el USB/NVMe.
    /// </summary>
    public static IReadOnlyList<string> ConflictingTweaks(bool msi, bool gpuAffinity, bool deviceAffinity)
    {
        var ids = new List<string>();
        if (msi) ids.Add(ManualMsiTweak);
        if (gpuAffinity || deviceAffinity) ids.Add(ManualAffinityTweak);
        return ids;
    }

    /// <summary>Minutos aproximados de las mediciones (para avisar antes de empezar). Sin contar reinicios.</summary>
    public static int EstimatedMinutes(bool msi, bool gpuAffinity, bool deviceAffinity, int msiDevices, int cores)
    {
        double m = 0;
        if (msi) m += msiDevices * 2 * 0.8;           // actual + invertido, 3 ventanas de ~14 s cada uno
        if (gpuAffinity) m += (cores + 1) * 1.2;      // "como viene" + un núcleo por vez
        if (deviceAffinity) m += (cores + 2) * 0.8;   // USB: default + núcleos; NVMe: default + propuesta
        return (int)Math.Ceiling(m);
    }
}
