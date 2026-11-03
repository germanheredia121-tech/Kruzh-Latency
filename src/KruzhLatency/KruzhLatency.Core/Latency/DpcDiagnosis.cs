namespace KruzhLatency.Core.Latency;

public enum DpcFixArea { None, Network, Gpu, Audio, Usb, Storage, Platform }

/// <summary>Diagnóstico de un driver medido: si pasa los objetivos, la causa probable y qué ítems de KRUZH lo atacan.</summary>
public sealed record DriverDiagnosis(
    string FileName,
    double MaxUs,
    double AvgUs,
    bool OverPeak,
    bool OverAverage,
    DpcFixArea Area,
    string Cause,
    /// <summary>Ids de ítems del catálogo (un id que termina en ":" es prefijo, ej. "Nic:" = todas las placas de red).</summary>
    IReadOnlyList<string> FixItemIds)
{
    public bool NeedsAttention => OverPeak || OverAverage;
}

/// <summary>
/// Spec 002 / D1 (F17): objetivos del chat (pico ≤ 300 µs, promedio &lt; 8 µs por driver) y el mapa driver → causa →
/// acción. Se decide por el nombre del archivo .sys (estable), no por la descripción (traducida).
/// </summary>
public static class DpcDiagnosis
{
    public const double PeakTargetUs = 300;
    public const double AverageTargetUs = 8;

    private sealed record Rule(DpcFixArea Area, string[] Files, string Cause, string[] Fixes);

    private static readonly Rule[] Rules =
    {
        new(DpcFixArea.Network,
            new[] { "ndis.sys", "tcpip.sys", "netio.sys", "afd.sys", "rt640x64.sys", "rtcx21x64.sys", "rt25cx21x64.sys", "e1d68x64.sys", "e2f68.sys", "e2fn.sys", "netwtw", "netadaptercx.sys" },
            "Placa de red y pila NDIS: interrupt moderation, ahorro de energía del chip, placas o adaptadores virtuales sin uso.",
            new[] { "Nic:", "Dev:WifiWithEthernet", "Dev:SecondNic", "Dev:RrasRoot", "Dev:NdisVirtualBus", "NicLsoAndFlowControlOff" }),
        new(DpcFixArea.Gpu,
            new[] { "dxgkrnl.sys", "nvlddmkm.sys", "amdkmdag.sys", "atikmpag.sys", "igdkmd64.sys", "igdkmdn64.sys", "dxgmms2.sys", "watchdog.sys" },
            "Driver de video: modo MSI y afinidad de interrupciones, MPO estable, HDCP y las funciones extra de la placa. Al medir, minimizá KRUZH (redibujar la ventana también suma).",
            new[] { "PciMsiModeV3", "PciIrqSmartBalancer", "MpoStableOverlayMinFps", "DisableHdcp", "Dev:GpuUsbC" }),
        new(DpcFixArea.Audio,
            new[] { "hdaudbus.sys", "portcls.sys", "hdaudio.sys", "rtkvhd64.sys", "ks.sys", "usbaudio.sys", "usbaudio2.sys" },
            "Audio: la controladora de audio HDMI de la GPU y el procesamiento de voz (Voice Clarity).",
            new[] { "Dev:GpuHdmiAudio", "Dev:VoiceClarity" }),
        new(DpcFixArea.Usb,
            new[] { "wdf01000.sys", "usbxhci.sys", "usbport.sys", "usbhub3.sys", "usbhub.sys", "ucx01000.sys", "hidusb.sys", "hidclass.sys", "mouhid.sys", "kbdhid.sys" },
            "USB/HID: suspensión selectiva y ahorro de energía de los puertos, nodos virtuales de joystick del teclado.",
            new[] { "UsbHidPowerSavingOff", "Dev:KeyboardGameControllers" }),
        new(DpcFixArea.Storage,
            new[] { "storport.sys", "stornvme.sys", "storahci.sys", "ataport.sys", "iastorvd.sys", "rcraid.sys", "amdsata.sys" },
            "Almacenamiento: estados de bajo consumo del enlace PCIe (ASPM) del disco o la controladora.",
            new[] { "PcieAspmDisable" }),
        new(DpcFixArea.Platform,
            new[] { "acpi.sys", "intelppm.sys", "amdppm.sys", "processr.sys", "pci.sys" },
            "Plataforma: estados de reposo del CPU (C-states) y ahorro de energía de la placa.",
            new[] { "CpuIdleAndCoreParkingDesktop", "ProcessorMinState100", "PcieAspmDisable" }),
    };

    public static DriverDiagnosis Diagnose(string fileName, double maxUs, double avgUs)
    {
        string file = fileName.Trim().ToLowerInvariant();
        var rule = Rules.FirstOrDefault(r => r.Files.Any(f => f.EndsWith(".sys", StringComparison.Ordinal) ? file == f : file.StartsWith(f, StringComparison.Ordinal)));
        bool overPeak = maxUs > PeakTargetUs;
        bool overAvg = avgUs >= AverageTargetUs;
        return rule is null
            ? new DriverDiagnosis(fileName, maxUs, avgUs, overPeak, overAvg, DpcFixArea.None,
                "Sin ajuste automático para este driver: actualizalo o reinstalalo desde la página del fabricante.", Array.Empty<string>())
            : new DriverDiagnosis(fileName, maxUs, avgUs, overPeak, overAvg, rule.Area, rule.Cause, rule.Fixes);
    }

    /// <summary>¿El id de un ítem del catálogo coincide con alguno de los arreglos (con soporte de prefijos "X:")?</summary>
    public static bool Matches(IReadOnlyList<string> fixes, string itemId) =>
        fixes.Any(f => f.EndsWith(':') ? itemId.StartsWith(f, StringComparison.OrdinalIgnoreCase) : string.Equals(f, itemId, StringComparison.OrdinalIgnoreCase));
}
