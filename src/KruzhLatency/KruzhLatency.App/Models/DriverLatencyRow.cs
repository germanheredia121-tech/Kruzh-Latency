using Microsoft.UI.Xaml;
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

    /// <summary>Causa probable (D1). Vacío si el driver está dentro de los objetivos.</summary>
    public string Cause { get; init; } = string.Empty;
    public Visibility CauseVisibility => string.IsNullOrEmpty(Cause) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Hay ítems del catálogo que atacan este driver y todavía no están aplicados.</summary>
    public bool CanFix { get; init; }
    public Visibility FixVisibility => CanFix ? Visibility.Visible : Visibility.Collapsed;
}
