namespace KruzhLatency.Core.Latency;

/// <summary>
/// Spec 003: en qué drivers anota Windows las interrupciones de cada tipo de dispositivo. Muchos drivers no atienden sus ISR/DPC
/// directamente: los KMDF (USBXHCI, placas NetAdapterCx) pasan por Wdf01000.sys, los miniport NDIS por ndis.sys, y la GPU
/// también por dxgkrnl.sys. Medir solo el .sys del servicio da "cero interrupciones" en esos casos.
/// Wdf01000.sys es compartido por varios drivers: por eso la carga de cada medición es específica del dispositivo.
/// </summary>
public static class InterruptDrivers
{
    public static IReadOnlyList<string> For(string msiKind, string? serviceImage)
    {
        string[] extra = msiKind switch
        {
            "USB xHCI" => new[] { "USBXHCI.SYS", "ucx01000.sys", "Wdf01000.sys" },
            "Red" => new[] { "ndis.sys", "NetAdapterCx.sys", "Wdf01000.sys" },
            "GPU" => new[] { "dxgkrnl.sys" },
            "Audio HD" => new[] { "HDAudBus.sys", "portcls.sys" },
            "NVMe" => new[] { "stornvme.sys", "storport.sys" },
            _ => Array.Empty<string>(),
        };
        return (serviceImage is { Length: > 0 } ? extra.Prepend(serviceImage) : extra)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
