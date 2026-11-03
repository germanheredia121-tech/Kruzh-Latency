using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace KruzhLatency.Models;

public enum TweakCategory
{
    MouseInput,
    UsbPowerBus,
    GpuDisplay,
    NetworkDesync,
    SystemCpu,
    IrqMsiAudio,
    StorageMemory,
    FpsBoostGames,
    Services,
    Devices,
    Apps
}

public enum RecommendationLevel
{
    Recommended,
    Neutral,
    NotRecommended
}

public enum AnticheatSafety
{
    Safe,
    WarningCanBlockOrBan
}

public enum SecurityImpact
{
    None,
    ReducesSecurity
}

public enum RiskLevel
{
    Low,
    Medium,
    High,
    Critical
}

public enum PresetProfileKind
{
    Minimal,
    Competitivo,
    Profesional,
    Entusiasta,
    Custom
}

public sealed record ApplicabilityResult(bool IsApplicable, string Reason)
{
    public static ApplicabilityResult Applicable(string reason = "Compatible con tu hardware") => new(true, reason);
    public static ApplicabilityResult NotApplicable(string reason) => new(false, reason);
}

public sealed record EvaluationResult(RecommendationLevel Level, string Reason)
{
    public static EvaluationResult Recommended(string reason) => new(RecommendationLevel.Recommended, reason);
    public static EvaluationResult Neutral(string reason) => new(RecommendationLevel.Neutral, reason);
    public static EvaluationResult NotRecommended(string warning) => new(RecommendationLevel.NotRecommended, warning);
}

public sealed record AnticheatResult(AnticheatSafety Safety, string WarningMessage)
{
    public static AnticheatResult Safe(string note = "100% seguro para Vanguard, FACEIT AC y ESEA") => new(AnticheatSafety.Safe, note);
    public static AnticheatResult Unsafe(string warning) => new(AnticheatSafety.WarningCanBlockOrBan, warning);
}

public sealed partial class TweakOptionChoice : ObservableObject
{
    public string Key { get; set; } = string.Empty;
    public string ShortLabel { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ParentTweakId { get; set; } = string.Empty;

    [ObservableProperty]
    private bool _isActiveOption;

    public string CompositeTag => $"{ParentTweakId}|{Key}";

    public Brush SegmentBackgroundBrush => IsActiveOption
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(30, 255, 255, 255))
        : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    public Brush SegmentBackground => SegmentBackgroundBrush;

    public Brush SegmentBorderBrush => IsActiveOption
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(60, 255, 255, 255))
        : new SolidColorBrush(Windows.UI.Color.FromArgb(14, 255, 255, 255));

    public Brush SegmentForegroundBrush => IsActiveOption
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 240, 242, 244))
        : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 139, 145, 152));

    public Brush SegmentForeground => SegmentForegroundBrush;

    partial void OnIsActiveOptionChanged(bool value)
    {
        OnPropertyChanged(nameof(SegmentBackgroundBrush));
        OnPropertyChanged(nameof(SegmentBackground));
        OnPropertyChanged(nameof(SegmentBorderBrush));
        OnPropertyChanged(nameof(SegmentForegroundBrush));
        OnPropertyChanged(nameof(SegmentForeground));
    }
}

public sealed class TweakCategoryGroup
{
    public string SectionTitle { get; set; } = string.Empty;
    public List<Services.DeclarativeTweakItem> Items { get; set; } = new();
}

public sealed class TweakExecutionReport
{
    public string TweakId { get; set; } = string.Empty;
    public string TweakTitle { get; set; } = string.Empty;
    public bool Success { get; set; }
    public bool Verified { get; set; }
    public bool WasAlreadyInTargetState { get; set; }
    public string Message { get; set; } = string.Empty;
}
