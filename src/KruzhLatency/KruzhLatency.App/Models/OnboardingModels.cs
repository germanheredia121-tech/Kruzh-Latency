using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using KruzhLatency.Services;
using Microsoft.UI.Xaml;

namespace KruzhLatency.Models;

public enum OnboardingPageKind { Profile, Items, Measurements, Summary }

/// <summary>Una pantalla del asistente de optimización completa.</summary>
public sealed class OnboardingPage
{
    public OnboardingPageKind Kind { get; init; }
    public string StepName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public List<OnboardingRow> Rows { get; } = new();
}

/// <summary>
/// Una fila con casilla: un tweak del catálogo o una medición. Lo que ya está aplicado se muestra marcado y bloqueado
/// (el asistente solo suma; revertir se hace desde Optimizaciones o Monitor).
/// </summary>
public sealed partial class OnboardingRow : ObservableObject
{
    public DeclarativeTweakItem? Tweak { get; init; }
    /// <summary>Clave de la medición (OnboardingPlan.Msi…) cuando la fila no es un tweak.</summary>
    public string? MeasurementKey { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Tooltip { get; init; } = string.Empty;
    public List<string> Badges { get; init; } = new();
    public bool IsLocked { get; init; }
    /// <summary>Opción que se aplica al marcar un tweak con opciones (la del perfil, o la recomendada).</summary>
    public string? OptionKey { get; set; }

    [ObservableProperty]
    private bool _isChecked;

    public Action<OnboardingRow>? Changed { get; set; }

    public bool IsEnabled => !IsLocked;
    public Visibility BadgesVisibility => Badges.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DescriptionVisibility => string.IsNullOrWhiteSpace(Description) ? Visibility.Collapsed : Visibility.Visible;

    partial void OnIsCheckedChanged(bool value) => Changed?.Invoke(this);
}
