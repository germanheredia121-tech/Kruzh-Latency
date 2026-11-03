namespace KruzhLatency.Models;

/// <summary>Una fila de "Cambios aplicados" en Monitor: un tweak con cambios activos, en lenguaje humano.</summary>
public sealed class JournalSummaryItem
{
    public string TweakId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}
