using KruzhLatency.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KruzhLatency.Controls;

/// <summary>Ítem "Agregar juego" de la biblioteca: va como último elemento de la grilla, al lado del último juego.</summary>
public sealed class AddGameTile
{
    public static readonly AddGameTile Instance = new();
}

/// <summary>Elige la plantilla de la biblioteca: card de juego o card "Agregar juego".</summary>
public sealed partial class GameTileTemplateSelector : DataTemplateSelector
{
    public DataTemplate? GameTemplate { get; set; }
    public DataTemplate? AddTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is AddGameTile ? AddTemplate : GameTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
