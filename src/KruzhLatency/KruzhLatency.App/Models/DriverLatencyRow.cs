using Microsoft.UI.Xaml.Media;

namespace KruzhLatency.Models;

/// <summary>Fila de "Drivers con más latencia" en Monitor, ya formateada para mostrar.</summary>
public sealed class DriverLatencyRow
{
    public string Title { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string MaxText { get; init; } = string.Empty;
    public string AvgText { get; init; } = string.Empty;
    public string CountText { get; init; } = string.Empty;
    public Brush? MaxBrush { get; init; }
}
