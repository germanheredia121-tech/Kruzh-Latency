using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KruzhLatency.Controls;

public sealed class SettingsCard : ContentControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingsCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TechnicalPathProperty =
        DependencyProperty.Register(nameof(TechnicalPath), typeof(string), typeof(SettingsCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingsCard), new PropertyMetadata("\uE713"));

    public static readonly DependencyProperty StatusVisibilityProperty =
        DependencyProperty.Register(nameof(StatusVisibility), typeof(Visibility), typeof(SettingsCard), new PropertyMetadata(Visibility.Collapsed));

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string TechnicalPath
    {
        get => (string)GetValue(TechnicalPathProperty);
        set => SetValue(TechnicalPathProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public Visibility StatusVisibility
    {
        get => (Visibility)GetValue(StatusVisibilityProperty);
        set => SetValue(StatusVisibilityProperty, value);
    }

    public SettingsCard()
    {
        this.DefaultStyleKey = typeof(SettingsCard);
        this.HorizontalAlignment = HorizontalAlignment.Stretch;
        this.HorizontalContentAlignment = HorizontalAlignment.Right;
    }
}

