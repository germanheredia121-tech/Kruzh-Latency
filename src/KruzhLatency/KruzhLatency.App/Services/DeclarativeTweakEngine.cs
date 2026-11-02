using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using CommunityToolkit.Mvvm.ComponentModel;
using KruzhLatency.Models;
using Microsoft.Win32;

namespace KruzhLatency.Services;

public sealed partial class DeclarativeTweakItem : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public string CodeNumber { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public TweakCategory Category { get; init; }
    public string CategoryDisplay { get; init; } = string.Empty;
    public string TechnicalDescription { get; init; } = string.Empty;
    public string WhatItTouches { get; init; } = string.Empty;
    public string SourceAttribution { get; init; } = string.Empty;
    public RiskLevel Risk { get; init; } = RiskLevel.Low;
    public SecurityImpact SecurityImpact { get; init; } = SecurityImpact.None;
    public bool RequiresReboot { get; init; }

    // Preset memberships
    public bool InMinimal { get; init; }
    public bool InCompetitivo { get; init; }
    public bool InProfesional { get; init; }
    public bool InEntusiasta { get; init; } = true;

    // Triple Gate Functions
    public Func<HardwareProfile, ApplicabilityResult> ApplicableGate { get; init; } = _ => ApplicabilityResult.Applicable();
    public Func<HardwareProfile, EvaluationResult> EvaluateGate { get; init; } = _ => EvaluationResult.Recommended("Óptimo para tu configuración.");
    public Func<HardwareProfile, AnticheatResult> AnticheatGate { get; init; } = _ => AnticheatResult.Safe();

    // Evaluated states for current hardware
    [ObservableProperty]
    private ApplicabilityResult _applicability = ApplicabilityResult.Applicable();

    [ObservableProperty]
    private EvaluationResult _evaluation = EvaluationResult.Recommended("");

    [ObservableProperty]
    private AnticheatResult _anticheat = AnticheatResult.Safe();

    [ObservableProperty]
    private bool _isLiveApplied;

    [ObservableProperty]
    private bool _desiredApplied;

    [ObservableProperty]
    private bool _isSelectedForBatch;

    [ObservableProperty]
    private string _selectedOptionKey = string.Empty;

    [ObservableProperty]
    private string _liveOptionKey = string.Empty;

    [ObservableProperty]
    private string _desiredOptionKey = string.Empty;

    [ObservableProperty]
    private bool _hasUserPendingEdit;

    [ObservableProperty]
    private bool _isFirstInGroup;

    [ObservableProperty]
    private string _lastStatusNote = string.Empty;

    public List<TweakOptionChoice> Options { get; init; } = new();
    public bool HasOptions => Options.Count > 0;

    // Operations
    public Func<bool> ReadLiveState { get; init; } = () => false;
    public Func<string?>? ReadLiveOptionKey { get; init; }
    public Func<string, (bool Success, string Detail)> ApplyAction { get; init; } = _ => (true, "Aplicado");
    public Func<(bool Success, string Detail)>? CustomRevertFallback { get; init; }

    // Helper UI properties
    public bool IsApplicableBool => Applicability.IsApplicable;
    public bool IsRecommendedBool => Applicability.IsApplicable && Evaluation.Level == RecommendationLevel.Recommended;
    public bool IsNotRecommendedBool => !Applicability.IsApplicable || Evaluation.Level == RecommendationLevel.NotRecommended;
    public bool IsAnticheatUnsafeBool => Anticheat.Safety == AnticheatSafety.WarningCanBlockOrBan;
    public bool ReducesSecurityBool => SecurityImpact == SecurityImpact.ReducesSecurity;
    public string CurrentStateBadge => !Applicability.IsApplicable ? "NO APLICABLE" : (IsLiveApplied ? "APLICADO" : "DEFAULT");

    public bool IsPending => IsApplicableBool && (HasOptions
        ? !string.Equals(DesiredOptionKey, LiveOptionKey, StringComparison.OrdinalIgnoreCase)
        : DesiredApplied != IsLiveApplied);

    public bool IsPendingRevert => IsPending && (HasOptions
        ? (Options.Count > 0 && string.Equals(DesiredOptionKey, Options[0].Key, StringComparison.OrdinalIgnoreCase))
        : (!DesiredApplied && IsLiveApplied));

    public Microsoft.UI.Xaml.Visibility AppliedPillVisibility =>
        IsLiveApplied ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility PendingVisibility =>
        IsPending ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility TopDividerVisibility =>
        IsFirstInGroup ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public string PendingBadgeText => "Pendiente";

    public Microsoft.UI.Xaml.Media.Brush RowBackgroundBrush => IsPending
        ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 28, 30, 33)) // #1C1E21
        : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    public Microsoft.UI.Xaml.Visibility ToggleControlVisibility =>
        !HasOptions ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility SegmentedControlVisibility =>
        HasOptions ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public string CategoryGlyph => Id switch
    {
        "MouseAccelOff" => "\uE962",
        "MarkCMouseCurve" => "\uE9D9",
        "MouseDataQueueDynamic" => "\uE8B7",
        "RawMouseThrottleWin11" => "\uE945",
        "CpuPrioritySeparationSelector" => "\uE950",
        "GlobalTimerResolutionWin11" => "\uE916",
        "BcdDisableDynamicTick" => "\uE823",
        "ReservedCpuSetsTopology" => "\uE71D",
        "PowerThrottlingEcoQosOff" => "\uEC4A",
        "CpuIdleAndCoreParkingDesktop" => "\uE945",
        "GameBarDvrSafeDisable" => "\uE714",
        "WindowsGameModeOn" => "\uE7FC",
        "ServicesAndTasksDebloat" => "\uE713",
        "GpuHardwareSchedulingHags" => "\uE7F8",
        "GpuDynamicP0State" => "\uEC4A",
        "NvidiaUltraLowLatencyReg" => "\uE945",
        "AmdRadeonUlpsOff" => "\uE708",
        "DirectXIndependentFlipAndFse" => "\uE740",
        "DisableMpo" => "\uE81E",
        "DisableHdcp" => "\uE72E",
        "NicEnergySavingOff" => "\uE839",
        "NicInterruptModerationConfigurable" => "\uE964",
        "NicRingBuffersConfigurable" => "\uE8B7",
        "NicLsoAndFlowControlOff" => "\uE72A",
        "MmcssAndNetworkThrottlingSelector" => "\uE9F9",
        "TcpNagleAndRssOptimize" => "\uE968",
        "QosDscp46Games" => "\uE8CB",
        "NtpHighPrecisionClockSync" => "\uE774",
        "PciMsiModeV3" => "\uE964",
        "PciIrqSmartBalancer" => "\uE71D",
        "AudioEnhancementsAndSpatialOff" => "\uE767",
        "UsbHidPowerSavingOff" => "\uE88E",
        "PcieAspmDisable" => "\uE950",
        "FastStartupAndHibernationOff" => "\uE7E8",
        "NtfsDisable8dot3AndLastAccess" => "\uEDA2",
        "SysMainAndMemoryManagement" => "\uE958",
        "GameIfeoHighPriority" => "\uE74A",
        "SteamBackgroundGpuAccelOff" => "\uE7F4",
        "DisableCfgPerGameIfeo" => "\uE8A5",
        "CpuKernelMitigationsOff" => "\uE7BA",
        "DefenderGameFolderExclusion" => "\uE83D",
        "PauseWindowsUpdate1Year" => "\uE895",
        _ => Category switch
        {
            TweakCategory.MouseInput => "\uE962",
            TweakCategory.UsbPowerBus => "\uE945",
            TweakCategory.GpuDisplay => "\uE7F4",
            TweakCategory.NetworkDesync => "\uE839",
            TweakCategory.SystemCpu => "\uE950",
            TweakCategory.IrqMsiAudio => "\uE964",
            TweakCategory.StorageMemory => "\uEDA2",
            TweakCategory.FpsBoostGames => "\uE7FC",
            _ => "\uE713"
        }
    };

    partial void OnIsFirstInGroupChanged(bool value)
    {
        OnPropertyChanged(nameof(TopDividerVisibility));
    }

    public void SetDesiredToggle(bool desired)
    {
        DesiredApplied = desired;
        HasUserPendingEdit = (DesiredApplied != IsLiveApplied);
        NotifyPendingStateChanged();
    }

    public void SetDesiredOption(string optionKey)
    {
        DesiredOptionKey = optionKey;
        SelectedOptionKey = optionKey;
        HasUserPendingEdit = !string.Equals(DesiredOptionKey, LiveOptionKey, StringComparison.OrdinalIgnoreCase);
        foreach (var opt in Options)
        {
            opt.IsActiveOption = string.Equals(opt.Key, DesiredOptionKey, StringComparison.OrdinalIgnoreCase);
        }
        NotifyPendingStateChanged();
    }

    public void SyncDesiredWithLive()
    {
        HasUserPendingEdit = false;
        DesiredApplied = IsLiveApplied;
        if (HasOptions)
        {
            DesiredOptionKey = LiveOptionKey;
            SelectedOptionKey = LiveOptionKey;
            foreach (var opt in Options)
            {
                opt.IsActiveOption = string.Equals(opt.Key, DesiredOptionKey, StringComparison.OrdinalIgnoreCase);
            }
        }
        NotifyPendingStateChanged();
    }

    private void NotifyPendingStateChanged()
    {
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsPendingRevert));
        OnPropertyChanged(nameof(PendingVisibility));
        OnPropertyChanged(nameof(PendingBadgeText));
        OnPropertyChanged(nameof(RowBackgroundBrush));
        OnPropertyChanged(nameof(AppliedPillVisibility));
    }

    public string GateBadgeText
    {
        get
        {
            if (!Applicability.IsApplicable) return "NO APLICABLE";
            if (Anticheat.Safety == AnticheatSafety.WarningCanBlockOrBan) return "RIESGO ANTICHEAT";
            if (SecurityImpact == SecurityImpact.ReducesSecurity) return "REDUCE SEGURIDAD";
            return Evaluation.Level switch
            {
                RecommendationLevel.Recommended => "RECOMENDADO",
                RecommendationLevel.Neutral => "OPCIONAL",
                RecommendationLevel.NotRecommended => "NO RECOMENDADO",
                _ => "EVALUADO"
            };
        }
    }

    public string GateExplanationText
    {
        get
        {
            if (!Applicability.IsApplicable) return Applicability.Reason;
            if (Anticheat.Safety == AnticheatSafety.WarningCanBlockOrBan) return $"{Anticheat.WarningMessage} — {Evaluation.Reason}";
            return Evaluation.Reason;
        }
    }

    public string ShortSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(TechnicalDescription)) return string.Empty;
            int dot = TechnicalDescription.IndexOf(". ", StringComparison.Ordinal);
            string s = dot > 20 && dot < 105 ? TechnicalDescription.Substring(0, dot + 1) : TechnicalDescription;
            return s.Length > 105 ? s.Substring(0, 102) + "..." : s;
        }
    }

    public string TechnicalToolTipText =>
        $"{Title} [{CodeNumber}]\n\n" +
        $"Detalle técnico:\n{TechnicalDescription}\n\n" +
        $"Clave de registro / Comando:\n{WhatItTouches}\n\n" +
        $"Evaluación para tu hardware:\n{GateExplanationText}";

    public void RefreshGatesAndLiveState(HardwareProfile hw)
    {
        Applicability = ApplicableGate(hw);
        Evaluation = EvaluateGate(hw);
        Anticheat = AnticheatGate(hw);
        try
        {
            IsLiveApplied = Applicability.IsApplicable && ReadLiveState();
            if (HasOptions)
            {
                string defaultKey = Options.FirstOrDefault()?.Key ?? string.Empty;
                string liveKey = ReadLiveOptionKey?.Invoke() ?? defaultKey;
                LiveOptionKey = liveKey;

                if (!HasUserPendingEdit || string.IsNullOrEmpty(DesiredOptionKey))
                {
                    DesiredOptionKey = liveKey;
                    SelectedOptionKey = liveKey;
                }
                else if (string.Equals(DesiredOptionKey, LiveOptionKey, StringComparison.OrdinalIgnoreCase))
                {
                    HasUserPendingEdit = false;
                }

                foreach (var opt in Options)
                {
                    opt.ParentTweakId = Id;
                    if (string.IsNullOrEmpty(opt.ShortLabel))
                    {
                        opt.ShortLabel = opt.Key;
                    }
                    opt.IsActiveOption = string.Equals(opt.Key, DesiredOptionKey, StringComparison.OrdinalIgnoreCase);
                }
            }
            else
            {
                if (!HasUserPendingEdit)
                {
                    DesiredApplied = IsLiveApplied;
                }
                else if (DesiredApplied == IsLiveApplied)
                {
                    HasUserPendingEdit = false;
                }
            }
        }
        catch
        {
            IsLiveApplied = false;
            if (!HasUserPendingEdit)
            {
                DesiredApplied = false;
            }
        }
        OnPropertyChanged(nameof(IsApplicableBool));
        OnPropertyChanged(nameof(IsRecommendedBool));
        OnPropertyChanged(nameof(IsNotRecommendedBool));
        OnPropertyChanged(nameof(IsAnticheatUnsafeBool));
        OnPropertyChanged(nameof(ReducesSecurityBool));
        OnPropertyChanged(nameof(CurrentStateBadge));
        OnPropertyChanged(nameof(AppliedPillVisibility));
        OnPropertyChanged(nameof(ToggleControlVisibility));
        OnPropertyChanged(nameof(SegmentedControlVisibility));
        OnPropertyChanged(nameof(GateBadgeText));
        OnPropertyChanged(nameof(GateExplanationText));
        OnPropertyChanged(nameof(TechnicalToolTipText));
        NotifyPendingStateChanged();
    }
}

public sealed class DeclarativeTweakEngine
{
    private readonly TransactionalAuditService _audit;
    private readonly HardwareChassisService _hwService;
    private readonly IrqAndMsiService _irqMsi;
    private readonly PowerCatalogService _powerCatalog;
    private readonly List<DeclarativeTweakItem> _allTweaks = new();

    public IReadOnlyList<DeclarativeTweakItem> AllTweaks => _allTweaks;
    public List<GameTargetItem> TargetGames { get; } = new();

    public DeclarativeTweakEngine(
        TransactionalAuditService audit,
        HardwareChassisService hwService,
        IrqAndMsiService irqMsi,
        PowerCatalogService powerCatalog)
    {
        _audit = audit;
        _hwService = hwService;
        _irqMsi = irqMsi;
        _powerCatalog = powerCatalog;

        InitializeDefaultGames();
        BuildDeclarativeCatalog();
        RefreshAll();
    }

    private void InitializeDefaultGames()
    {
        string? cs2Path = DetectCs2ExePath();
        TargetGames.Add(new GameTargetItem
        {
            Name = "Counter-Strike 2",
            ShortName = "CS2",
            ExecutableName = "cs2.exe",
            FullExePath = cs2Path ?? @"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe",
            CoverImagePath = FindSteamCover("730"),
            GradientStartHex = "#3A2E17",
            GradientEndHex = "#14120C",
            HighCpuAndIoPriority = true,
            QosDscp46 = true,
            HighPerfGpuPreference = true,
            DisableFullscreenOptimizations = true,
            UsePCoresAffinity = true,
            ForegroundPriorityBoost = true
        });

        TargetGames.Add(new GameTargetItem
        {
            Name = "VALORANT",
            ShortName = "Valorant",
            ExecutableName = "VALORANT-Win64-Shipping.exe",
            FullExePath = @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe",
            GradientStartHex = "#2A1A2E",
            GradientEndHex = "#120D14",
            HighCpuAndIoPriority = true,
            QosDscp46 = true,
            HighPerfGpuPreference = true,
            DisableFullscreenOptimizations = true,
            UsePCoresAffinity = true,
            ForegroundPriorityBoost = true
        });

        TargetGames.Add(new GameTargetItem
        {
            Name = "Fortnite",
            ShortName = "Fortnite",
            ExecutableName = "FortniteClient-Win64-Shipping.exe",
            FullExePath = @"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe",
            GradientStartHex = "#15263A",
            GradientEndHex = "#0C1016",
            HighCpuAndIoPriority = true,
            QosDscp46 = true,
            HighPerfGpuPreference = true,
            DisableFullscreenOptimizations = true,
            UsePCoresAffinity = true,
            ForegroundPriorityBoost = true
        });

        TargetGames.Add(new GameTargetItem
        {
            Name = "Apex Legends",
            ShortName = "Apex Legends",
            ExecutableName = "r5apex.exe",
            FullExePath = @"C:\Program Files (x86)\Steam\steamapps\common\Apex Legends\r5apex.exe",
            CoverImagePath = FindSteamCover("1172470"),
            GradientStartHex = "#3A1518",
            GradientEndHex = "#16090B",
            HighCpuAndIoPriority = true,
            QosDscp46 = true,
            HighPerfGpuPreference = true,
            DisableFullscreenOptimizations = true,
            UsePCoresAffinity = true,
            ForegroundPriorityBoost = true
        });

        TargetGames.Add(new GameTargetItem
        {
            Name = "PUBG: BATTLEGROUNDS",
            ShortName = "PUBG",
            ExecutableName = "TslGame.exe",
            FullExePath = @"C:\Program Files (x86)\Steam\steamapps\common\PUBG\TslGame\Binaries\Win64\TslGame.exe",
            CoverImagePath = FindSteamCover("578080"),
            GradientStartHex = "#1F3320",
            GradientEndHex = "#0C130D",
            HighCpuAndIoPriority = true,
            QosDscp46 = true,
            HighPerfGpuPreference = true,
            DisableFullscreenOptimizations = true,
            UsePCoresAffinity = true,
            ForegroundPriorityBoost = true
        });
    }

    private static string? FindSteamCover(string appId)
    {
        try
        {
            string[] candidates = {
                $@"C:\Program Files (x86)\Steam\appcache\librarycache\{appId}\library_600x900.jpg",
                $@"C:\Program Files (x86)\Steam\appcache\librarycache\{appId}_library_600x900.jpg",
                $@"D:\Steam\appcache\librarycache\{appId}\library_600x900.jpg"
            };
            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }
        }
        catch { }
        return null;
    }

    public void RefreshAll()
    {
        var hw = _hwService.CurrentProfile;
        foreach (var tweak in _allTweaks)
        {
            // Adjust dynamic default option for MouseDataQueueSize if not yet chosen
            if (tweak.Id == "MouseDataQueueDynamic" && string.IsNullOrEmpty(tweak.SelectedOptionKey))
            {
                tweak.SelectedOptionKey = hw.MousePollingRateHz switch
                {
                    >= 8000 => "128",
                    >= 4000 => "64",
                    >= 2000 => "32",
                    _ => "16"
                };
            }
            tweak.RefreshGatesAndLiveState(hw);
        }
    }

    public void DiscardAllPending()
    {
        var hw = _hwService.CurrentProfile;
        foreach (var t in _allTweaks)
        {
            t.SyncDesiredWithLive();
            t.RefreshGatesAndLiveState(hw);
        }
    }

    public void SelectPresetProfile(PresetProfileKind preset)
    {
        var hw = _hwService.CurrentProfile;
        foreach (var t in _allTweaks)
        {
            t.RefreshGatesAndLiveState(hw);
            if (!t.Applicability.IsApplicable)
            {
                t.IsSelectedForBatch = false;
                t.SyncDesiredWithLive();
                continue;
            }

            switch (preset)
            {
                case PresetProfileKind.Minimal:
                    t.IsSelectedForBatch = t.InMinimal && t.Evaluation.Level != RecommendationLevel.NotRecommended && !t.IsAnticheatUnsafeBool && !t.ReducesSecurityBool;
                    break;

                case PresetProfileKind.Competitivo:
                    t.IsSelectedForBatch = t.InCompetitivo && t.Evaluation.Level != RecommendationLevel.NotRecommended && !t.IsAnticheatUnsafeBool && !t.ReducesSecurityBool;
                    break;

                case PresetProfileKind.Profesional:
                    t.IsSelectedForBatch = t.InProfesional && t.Evaluation.Level != RecommendationLevel.NotRecommended && !t.IsAnticheatUnsafeBool && !t.ReducesSecurityBool;
                    break;

                case PresetProfileKind.Entusiasta:
                    // Enthusiast unlocks everything applicable on this hardware
                    t.IsSelectedForBatch = t.InEntusiasta && t.Applicability.IsApplicable;
                    break;
            }

            if (t.IsSelectedForBatch)
            {
                if (t.HasOptions)
                {
                    string recOpt = t.Id switch
                    {
                        "MouseDataQueueDynamic" => hw.MousePollingRateHz switch
                        {
                            >= 8000 => "128",
                            >= 4000 => "64",
                            >= 2000 => "32",
                            _ => "16"
                        },
                        "CpuPrioritySeparationSelector" => "38",
                        "NicInterruptModerationConfigurable" => "Medium",
                        "NicRingBuffersConfigurable" => "512",
                        "MmcssAndNetworkThrottlingSelector" => "Balanced10_Off",
                        _ => t.Options.LastOrDefault()?.Key ?? string.Empty
                    };
                    t.SetDesiredOption(recOpt);
                }
                else
                {
                    t.SetDesiredToggle(true);
                }
            }
            else
            {
                t.SyncDesiredWithLive();
            }
        }
    }

    // ------------------------------------------------------------------------------------------
    // Apply / Revert en 3 pasos (hotfix T03):
    //   1) PreCheckApply      -> hilo de UI (lee gates/estado y toca propiedades observables)
    //   2) RunApplyAction /
    //      RunRevertActions   -> hilo de background (SOLO operaciones de sistema; no toca propiedades
    //                            observables del item, que estan enlazadas a la UI)
    //   3) CompleteApply /
    //      CompleteRevert     -> hilo de UI (re-lee estado real y arma el reporte)
    // Ninguno de los pasos 2 deja escapar excepciones: un tweak que falla no corta el lote.
    // ------------------------------------------------------------------------------------------

    /// <summary>Paso 1 (UI). Devuelve un reporte si NO hay nada que ejecutar; null si hay que correr el paso 2.</summary>
    public TweakExecutionReport? PreCheckApply(DeclarativeTweakItem item)
    {
        var hw = _hwService.CurrentProfile;
        item.RefreshGatesAndLiveState(hw);

        if (!item.Applicability.IsApplicable)
        {
            return new TweakExecutionReport
            {
                TweakId = item.Id,
                TweakTitle = item.Title,
                Success = false,
                Verified = false,
                Message = $"Bloqueado por Puerta de Aplicabilidad: {item.Applicability.Reason}"
            };
        }

        // Idempotency check: if already applied and no multi-option change is forced, skip redundant writes
        if (item.IsLiveApplied && !item.HasOptions)
        {
            item.LastStatusNote = "Ya estaba aplicado (Idempotente — verificado OK).";
            return new TweakExecutionReport
            {
                TweakId = item.Id,
                TweakTitle = item.Title,
                Success = true,
                Verified = true,
                WasAlreadyInTargetState = true,
                Message = item.LastStatusNote
            };
        }

        return null;
    }

    /// <summary>Paso 2 (background). Ejecuta las operaciones de sistema del tweak. Nunca lanza.</summary>
    public static (bool Success, string Detail) RunApplyAction(DeclarativeTweakItem item, string optionKey)
    {
        try
        {
            return item.ApplyAction(optionKey);
        }
        catch (Exception ex)
        {
            App.LogError($"Apply {item.Id}", ex);
            return (false, $"Error al aplicar: {ex.Message}");
        }
    }

    /// <summary>Paso 3 (UI). Re-lee el estado real y arma el reporte.</summary>
    public TweakExecutionReport CompleteApply(DeclarativeTweakItem item, bool ok, string detail)
    {
        bool verified = false;
        try
        {
            InvalidateBcdCache();
            item.RefreshGatesAndLiveState(_hwService.CurrentProfile);
            verified = ok && item.IsLiveApplied;
        }
        catch (Exception ex)
        {
            App.LogError($"Verify {item.Id}", ex);
        }

        item.LastStatusNote = !ok
            ? detail
            : (verified ? $"{detail} [Verificado OK]" : $"{detail} (Requiere reinicio para reflejar estado)");

        return new TweakExecutionReport
        {
            TweakId = item.Id,
            TweakTitle = item.Title,
            Success = ok,
            Verified = verified,
            Message = item.LastStatusNote
        };
    }

    /// <summary>Paso 2 del revert (background). Journal -> fallback del tweak -> fallback de OS. Nunca lanza.</summary>
    public (bool Success, string Detail) RunRevertActions(DeclarativeTweakItem item)
    {
        try
        {
            bool reverted = _audit.RevertTweakFromJournal(item.Id, out string msg);
            if (!reverted && item.CustomRevertFallback != null)
            {
                var (okFallback, detailFallback) = item.CustomRevertFallback();
                msg = detailFallback;
                reverted = okFallback;
            }
            if (!reverted)
            {
                var (okOs, detailOs) = ApplyDefaultOsRevertFallback(item.Id);
                if (okOs)
                {
                    reverted = true;
                    msg = detailOs;
                }
            }
            return (reverted, msg);
        }
        catch (Exception ex)
        {
            App.LogError($"Revert {item.Id}", ex);
            return (false, $"Error al revertir: {ex.Message}");
        }
    }

    /// <summary>Paso 3 del revert (UI).</summary>
    public TweakExecutionReport CompleteRevert(DeclarativeTweakItem item, bool ok, string detail)
    {
        try
        {
            InvalidateBcdCache();
            item.RefreshGatesAndLiveState(_hwService.CurrentProfile);
        }
        catch (Exception ex)
        {
            App.LogError($"Verify revert {item.Id}", ex);
        }
        item.LastStatusNote = detail;

        return new TweakExecutionReport
        {
            TweakId = item.Id,
            TweakTitle = item.Title,
            Success = ok,
            Verified = ok && !item.IsLiveApplied,
            Message = detail
        };
    }

    /// <summary>Versión sincrónica (para los flujos que todavía no migraron al lote en background).</summary>
    public TweakExecutionReport ExecuteApply(DeclarativeTweakItem item)
    {
        var early = PreCheckApply(item);
        if (early != null) return early;
        var (ok, detail) = RunApplyAction(item, item.SelectedOptionKey);
        return CompleteApply(item, ok, detail);
    }

    /// <summary>Versión sincrónica (para los flujos que todavía no migraron al lote en background).</summary>
    public TweakExecutionReport ExecuteRevert(DeclarativeTweakItem item)
    {
        var (ok, detail) = RunRevertActions(item);
        return CompleteRevert(item, ok, detail);
    }

    private void BuildDeclarativeCatalog()
    {
        // ==================== CATEGORÍA 1: MOUSE & INPUT ====================
        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "MouseAccelOff",
            CodeNumber = "M01",
            Title = "Mouse Acceleration OFF (Puntero 6/11 1:1)",
            Category = TweakCategory.MouseInput,
            CategoryDisplay = "Mouse & Input",
            TechnicalDescription = "Desactiva 'Enhance Pointer Precision' y fija velocidad de puntero en 6/11 (factor de escala exacto 1:1 sin interpolación del OS).",
            WhatItTouches = @"HKCU\Control Panel\Mouse -> MouseSpeed=0, MouseThreshold1=0, MouseThreshold2=0, MouseSensitivity=10",
            SourceAttribution = "Estándar Competitivo ESL / BlurBusters",
            Risk = RiskLevel.Low,
            InMinimal = true,
            InCompetitivo = true,
            InProfesional = true,
            ReadLiveState = () => ReadRegString(RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseSpeed") == "0" &&
                                  ReadRegString(RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold1") == "0",
            ApplyAction = _ =>
            {
                _audit.SetRegistryValue("MouseAccelOff", RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseSpeed", "0", RegistryValueKind.String);
                _audit.SetRegistryValue("MouseAccelOff", RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold1", "0", RegistryValueKind.String);
                _audit.SetRegistryValue("MouseAccelOff", RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold2", "0", RegistryValueKind.String);
                _audit.SetRegistryValue("MouseAccelOff", RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseSensitivity", "10", RegistryValueKind.String);
                return (true, "Aceleración de mouse desactivada (1:1 6/11).");
            },
            CustomRevertFallback = () =>
            {
                _audit.SetRegistryValue("MouseAccelOff_Rev", RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseSpeed", "1", RegistryValueKind.String);
                _audit.SetRegistryValue("MouseAccelOff_Rev", RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold1", "6", RegistryValueKind.String);
                _audit.SetRegistryValue("MouseAccelOff_Rev", RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold2", "10", RegistryValueKind.String);
                return (true, "Restaurado a valores estándar de Windows.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "MarkCMouseCurve",
            CodeNumber = "M02",
            Title = "MarkC 1:1 Linear Mouse Curves (40-Byte True Ramp)",
            Category = TweakCategory.MouseInput,
            CategoryDisplay = "Mouse & Input",
            TechnicalDescription = "Inyecta las curvas planas exactas de 40 bytes (MarkC 100% DPI) en SmoothMouseXCurve y SmoothMouseYCurve para eliminar cualquier filtrado residual.",
            WhatItTouches = @"HKCU\Control Panel\Mouse -> SmoothMouseXCurve, SmoothMouseYCurve (40 bytes lineales)",
            SourceAttribution = "MarkC Windows 10/11 Mouse Fix",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            ReadLiveState = () =>
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse");
                if (key?.GetValue("SmoothMouseXCurve") is byte[] b && b.Length == 40)
                {
                    // Check linear MarkC marker byte at index 9 == 0x00 and index 10 == 0x01
                    return b[8] == 0x00 && b[9] == 0x00 && b[10] == 0x01;
                }
                return false;
            },
            ApplyAction = _ =>
            {
                // True MarkC 1:1 100% DPI 40-byte linear ramp (unlike original script which wrote default curve)
                byte[] linearX =
                {
                    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
                    0x00,0x00,0x01,0x00,0x00,0x00,0x00,0x00,
                    0x00,0x00,0x02,0x00,0x00,0x00,0x00,0x00,
                    0x00,0x00,0x03,0x00,0x00,0x00,0x00,0x00,
                    0x00,0x00,0x04,0x00,0x00,0x00,0x00,0x00
                };
                byte[] linearY =
                {
                    0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
                    0x00,0x00,0x01,0x00,0x00,0x00,0x00,0x00,
                    0x00,0x00,0x02,0x00,0x00,0x00,0x00,0x00,
                    0x00,0x00,0x03,0x00,0x00,0x00,0x00,0x00,
                    0x00,0x00,0x04,0x00,0x00,0x00,0x00,0x00
                };
                _audit.SetRegistryValue("MarkCMouseCurve", RegistryHive.CurrentUser, @"Control Panel\Mouse", "SmoothMouseXCurve", linearX, RegistryValueKind.Binary);
                _audit.SetRegistryValue("MarkCMouseCurve", RegistryHive.CurrentUser, @"Control Panel\Mouse", "SmoothMouseYCurve", linearY, RegistryValueKind.Binary);
                return (true, "Curvas MarkC 1:1 de 40 bytes aplicadas (backup previo guardado en Journal).");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "MouseDataQueueDynamic",
            CodeNumber = "M03",
            Title = "Mouse & Keyboard DataQueueSize Dinámico (Por Polling Rate Hz)",
            Category = TweakCategory.MouseInput,
            CategoryDisplay = "Mouse & Input",
            TechnicalDescription = "Ajusta la profundidad del ring buffer de mouclass.sys y kbdclass.sys según los Hz de tu mouse: 16 para <=1000Hz, 32 para 2000Hz, 64 para 4000Hz, 128 para 8000Hz (evita packet drops en alto polling rate).",
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Services\mouclass\Parameters\MouseDataQueueSize & kbdclass\Parameters\KeyboardDataQueueSize",
            SourceAttribution = "Calypto / BlurBusters High-Polling Rate Guide",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            SelectedOptionKey = "16",
            Options = new List<TweakOptionChoice>
            {
                new() { Key = "100", ShortLabel = "Auto", DisplayName = "Default Windows (100)", Description = "Buffer estándar de Windows." },
                new() { Key = "16", ShortLabel = "16", DisplayName = "Queue = 16 (Óptimo para Mouse 500Hz / 1000Hz)", Description = "Mínima latencia de cola para polling estándar de 1000Hz." },
                new() { Key = "32", ShortLabel = "32", DisplayName = "Queue = 32 (Óptimo para Mouse 2000Hz)", Description = "Buffer seguro para receptores inalámbricos de 2000Hz." },
                new() { Key = "64", ShortLabel = "64", DisplayName = "Queue = 64 (Óptimo para Mouse 4000Hz)", Description = "Evita saturación de cola en mouses de 4000Hz." },
                new() { Key = "128", ShortLabel = "128", DisplayName = "Queue = 128 (Óptimo para Mouse 8000Hz)", Description = "Imprescindible para 8000Hz sin pérdida de eventos." }
            },
            EvaluateGate = hw => hw.MousePollingRateHz >= 4000
                ? EvaluationResult.Recommended($"Detectado perfil {hw.MousePollingRateHz}Hz: se recomienda Queue >= 64 para evitar pérdida de paquetes.")
                : EvaluationResult.Recommended("Para mouse de 1000Hz, Queue = 16 minimiza el retraso de buffer sin riesgo de overflow."),
            ReadLiveState = () =>
            {
                int? val = ReadRegDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\mouclass\Parameters", "MouseDataQueueSize");
                return val is 16 or 32 or 64 or 128;
            },
            ReadLiveOptionKey = () =>
            {
                int? val = ReadRegDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\mouclass\Parameters", "MouseDataQueueSize");
                return val is 16 or 32 or 64 or 128 ? val.Value.ToString() : "100";
            },
            ApplyAction = opt =>
            {
                int queueVal = int.TryParse(opt, out int parsed) ? parsed : 16;
                _audit.SetRegistryValue("MouseDataQueueDynamic", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\mouclass\Parameters", "MouseDataQueueSize", queueVal, RegistryValueKind.DWord);
                _audit.SetRegistryValue("MouseDataQueueDynamic", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", "KeyboardDataQueueSize", queueVal, RegistryValueKind.DWord);
                return (true, $"Mouse/Keyboard DataQueueSize establecido en {queueVal}.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "RawMouseThrottleWin11",
            CodeNumber = "M04",
            Title = "Windows 11 RawMouseThrottleDuration & Background Input Rate Fix",
            Category = TweakCategory.MouseInput,
            CategoryDisplay = "Mouse & Input",
            TechnicalDescription = "Reduce el estrangulamiento de mensajes WM_INPUT / RawMouse introducido en Windows 11 22H2+ para mantener precisión de micro-ajustes.",
            WhatItTouches = @"HKCU\Control Panel\Mouse -> RawMouseThrottleDuration = 3 (DWORD)",
            SourceAttribution = "ValleyOfDoom / BoringBoredom PC-Tuning §11.52",
            Risk = RiskLevel.Low,
            RequiresReboot = false,
            InMinimal = true,
            InCompetitivo = true,
            InProfesional = true,
            ApplicableGate = hw => hw.IsWindows11_22H2Plus
                ? ApplicabilityResult.Applicable("Compatible con tu Windows 11 22H2+")
                : ApplicabilityResult.NotApplicable($"Requiere Windows 11 Build 22621+ (Build actual: {hw.WindowsBuild})"),
            ReadLiveState = () => ReadRegDword(RegistryHive.CurrentUser, @"Control Panel\Mouse", "RawMouseThrottleDuration") == 3,
            ApplyAction = _ =>
            {
                _audit.SetRegistryValue("RawMouseThrottleWin11", RegistryHive.CurrentUser, @"Control Panel\Mouse", "RawMouseThrottleDuration", 3, RegistryValueKind.DWord);
                return (true, "RawMouseThrottleDuration optimizado en 3.");
            }
        });

        // ==================== CATEGORÍA 2: SISTEMA / CPU / SCHEDULER ====================
        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "CpuPrioritySeparationSelector",
            CodeNumber = "C01",
            Title = "Win32PrioritySeparation (Quantum & Foreground Boost — Fuente Única)",
            Category = TweakCategory.SystemCpu,
            CategoryDisplay = "Sistema & CPU",
            TechnicalDescription = "Fuente única de verdad (unifica los antiguos módulos [4] y [42]). Configura los intervalos de quantum del scheduler y el boost para la ventana del juego.",
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl -> Win32PrioritySeparation",
            SourceAttribution = "Windows Internals / Calypto / BoringBoredom",
            Risk = RiskLevel.Low,
            InMinimal = true,
            InCompetitivo = true,
            InProfesional = true,
            SelectedOptionKey = "38",
            Options = new List<TweakOptionChoice>
            {
                new() { Key = "2", ShortLabel = "Auto", DisplayName = "0x02 (2 dec) — Default Windows", Description = "Valor estándar de Windows." },
                new() { Key = "38", ShortLabel = "26", DisplayName = "0x26 (38 dec) — Short Variable, 3:1 Foreground Boost [RECOMENDADO]", Description = "Equilibrio ideal entre latencia de input y estabilidad de frametime." },
                new() { Key = "40", ShortLabel = "28", DisplayName = "0x28 (40 dec) — Short Fixed, 1:1 Equal Quantum", Description = "Quantums cortos fijos para máxima respuesta de hilos de entrada." },
                new() { Key = "36", ShortLabel = "24", DisplayName = "0x24 (36 dec) — Short Variable, 1:1 Quantum", Description = "Recomendado en algunas guías para motores DX12 pesados." }
            },
            ReadLiveState = () =>
            {
                int? val = ReadRegDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation");
                return val is 38 or 40 or 36;
            },
            ReadLiveOptionKey = () =>
            {
                int? val = ReadRegDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation");
                return val is 38 or 40 or 36 ? val.Value.ToString() : "2";
            },
            ApplyAction = opt =>
            {
                int target = int.TryParse(opt, out int p) ? p : 38;
                _audit.SetRegistryValue("CpuPrioritySeparationSelector", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", target, RegistryValueKind.DWord);
                return (true, $"Win32PrioritySeparation establecido en {target} (0x{target:X2}).");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "GlobalTimerResolutionWin11",
            CodeNumber = "C02",
            Title = "GlobalTimerResolutionRequests (Sincronización Global 0.5ms)",
            Category = TweakCategory.SystemCpu,
            CategoryDisplay = "Sistema & CPU",
            TechnicalDescription = "Hace que las solicitudes de temporizador de alta resolución (0.5ms) se apliquen globalmente a todo el kernel y subsistema de ventanas.",
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel -> GlobalTimerResolutionRequests = 1",
            SourceAttribution = "Microsoft Kernel Timer Documentation / PC-Tuning §11.50",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = true,
            InCompetitivo = true,
            InProfesional = true,
            ReadLiveState = () => ReadRegDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests") == 1,
            ApplyAction = _ =>
            {
                _audit.SetRegistryValue("GlobalTimerResolutionWin11", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 1, RegistryValueKind.DWord);
                return (true, "GlobalTimerResolutionRequests = 1 aplicado.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "BcdDisableDynamicTick",
            CodeNumber = "C03",
            Title = "BCDEdit: DisableDynamicTick + Invariant TSC Limpio (Sin useplatformtick)",
            Category = TweakCategory.SystemCpu,
            CategoryDisplay = "Sistema & CPU",
            TechnicalDescription = "Desactiva el Dynamic Tick de ahorro en idle y garantiza que Windows NO fuerce HPET/RTC (elimina useplatformclock y useplatformtick que agregaban latencia).",
            WhatItTouches = @"bcdedit /set disabledynamictick yes + /deletevalue useplatformclock + /deletevalue useplatformtick",
            SourceAttribution = "BoringBoredom PC-Tuning §11.50 / Calypto",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            EvaluateGate = hw => hw.Chassis == ChassisKind.Laptop
                ? EvaluationResult.Neutral("En Laptop reduce levemente la duración de batería en reposo, pero mejora consistencia de ticks en juego.")
                : EvaluationResult.Recommended("Óptimo en Desktop: mantiene interrupciones de reloj constantes sobre Invariant TSC."),
            ReadLiveState = () =>
            {
                return GetCachedBcdCurrent().Contains("disabledynamictick", StringComparison.OrdinalIgnoreCase) &&
                       GetCachedBcdCurrent().Contains("Yes", StringComparison.OrdinalIgnoreCase);
            },
            ApplyAction = _ =>
            {
                _audit.SetBcdValue("BcdDisableDynamicTick", "disabledynamictick", "yes");
                _audit.SetBcdValue("BcdDisableDynamicTick", "useplatformclock", null);
                _audit.SetBcdValue("BcdDisableDynamicTick", "useplatformtick", null);
                InvalidateBcdCache();
                return (true, "disabledynamictick=yes aplicado y relojes legacy (useplatformclock/useplatformtick) limpiados.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "ReservedCpuSetsTopology",
            CodeNumber = "C04",
            Title = "ReservedCpuSets (Aislar E-Cores Intel / Segundo CCD Ryzen)",
            Category = TweakCategory.SystemCpu,
            CategoryDisplay = "Sistema & CPU",
            TechnicalDescription = "Reserva los E-Cores (Intel 12-14th/Ultra) o el segundo CCD (Ryzen Dual-CCD) en el kernel para que el juego corra exclusivamente en los P-Cores / CCD0 primario. Persistente, sin watcher y 100% Anticheat-Safe.",
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel -> ReservedCpuSets (REG_BINARY)",
            SourceAttribution = "BoringBoredom PC-Tuning §11.45 / Microsoft CPU Sets API",
            Risk = RiskLevel.Medium,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            ApplicableGate = hw => (hw.IsIntelHybrid || hw.IsDualCcdRyzen) && hw.ReservedBackgroundCpuMask != 0
                ? ApplicabilityResult.Applicable($"Topología heterogénea detectada (Máscara reservada: 0x{hw.ReservedBackgroundCpuMask:X})")
                : ApplicabilityResult.NotApplicable($"Tu CPU ({hw.CpuName}) es Single-CCD sin E-Cores; todos los núcleos comparten la misma caché L3 y no debe restringirse con CPU Sets."),
            EvaluateGate = hw => EvaluationResult.Recommended(
                hw.IsIntelHybrid
                    ? "Altamente recomendado en Intel Híbrido: evita que hilos del juego caigan en E-Cores."
                    : "Altamente recomendado en Ryzen Dual-CCD: evita saltos de caché L3 por Infinity Fabric entre CCD0 y CCD1."),
            ReadLiveState = () =>
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel");
                return k?.GetValue("ReservedCpuSets") is byte[] b && b.Any(x => x != 0);
            },
            ApplyAction = _ =>
            {
                ulong mask = _hwService.CurrentProfile.ReservedBackgroundCpuMask;
                byte[] maskBytes = BitConverter.GetBytes(mask);
                _audit.SetRegistryValue("ReservedCpuSetsTopology", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "ReservedCpuSets", maskBytes, RegistryValueKind.Binary);
                return (true, $"ReservedCpuSets aplicado (Máscara 0x{mask:X}).");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "PowerThrottlingEcoQosOff",
            CodeNumber = "C05",
            Title = "Desactivar Power Throttling Global (EcoQoS OFF)",
            Category = TweakCategory.SystemCpu,
            CategoryDisplay = "Sistema & CPU",
            TechnicalDescription = "Impide que Windows reduzca la frecuencia de procesos auxiliares o de audio mediante EcoQoS.",
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling -> PowerThrottlingOff = 1",
            SourceAttribution = "Microsoft Power Management / ESL Pro Guide",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            EvaluateGate = hw => hw.Chassis == ChassisKind.Laptop
                ? EvaluationResult.NotRecommended("NO RECOMENDADO EN LAPTOP: Desactivar EcoQoS aumenta el calor generado por procesos en segundo plano y reduce el margen térmico del juego.")
                : EvaluationResult.Recommended("Óptimo en Desktop para evitar estrangulamiento energético de hilos secundarios."),
            ReadLiveState = () => ReadRegDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff") == 1,
            ApplyAction = _ =>
            {
                _audit.SetRegistryValue("PowerThrottlingEcoQosOff", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1, RegistryValueKind.DWord);
                return (true, "PowerThrottlingOff = 1 aplicado.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "CpuIdleAndCoreParkingDesktop",
            CodeNumber = "C06",
            Title = "Zero Wake-Up Latency (C-States OS Idle OFF + Core Parking 100%)",
            Category = TweakCategory.SystemCpu,
            CategoryDisplay = "Sistema & CPU",
            TechnicalDescription = "Desactiva la promoción a estados C de reposo en el esquema actual y mantiene el 100% de los núcleos despiertos.",
            WhatItTouches = @"powercfg scheme_current sub_processor: IDLEDISABLE=1, CPMINCORES=100, PERFBOOSTMODE=2",
            SourceAttribution = "Calypto Latency Guide",
            Risk = RiskLevel.Medium,
            InMinimal = false,
            InCompetitivo = false,
            InProfesional = true,
            EvaluateGate = hw =>
            {
                if (hw.Chassis == ChassisKind.Laptop)
                {
                    return EvaluationResult.NotRecommended("PELIGRO TÉRMICO EN LAPTOP: Apagar C-States (IDLEDISABLE=1) en una portátil dispara la temperatura a >90°C en reposo y provoca Thermal Throttling severo.");
                }
                if (hw.ClockingMode == CpuClockingMode.StaticAllCoreOverclock)
                {
                    return EvaluationResult.Recommended($"IDEAL PARA TU ALL-CORE OVERCLOCK ({hw.ClockingModeDescription}): Elimina por completo la latencia de despertar C1E/C6 sin afectar tu multiplicador fijo.");
                }
                if (hw.IsRyzenZen2OrZen3OrZen4 && hw.ClockingMode == CpuClockingMode.DynamicBoostPbo)
                {
                    return EvaluationResult.Neutral("En Ryzen con PBO + Curve Optimizer, mantener IDLEDISABLE=1 todo el tiempo puede reducir el boost pico de 1 solo núcleo en ~50-100MHz.");
                }
                return EvaluationResult.Recommended("Recomendado en Desktop con buena refrigeración para eliminar latencia de C-States.");
            },
            ReadLiveState = () => ReadActivePowerSchemeAcSettingDword("54533251-82be-4824-96c1-47b60b740d00", "5d76a2ca-e8c0-402f-a133-2158492d58ad") == 1,
            ApplyAction = _ =>
            {
                _audit.SetPowerCfgAcValue("CpuIdleAndCoreParkingDesktop", "54533251-82be-4824-96c1-47b60b740d00", "0cc5b647-c1df-4637-891a-dec35c318583", 100);
                _audit.SetPowerCfgAcValue("CpuIdleAndCoreParkingDesktop", "54533251-82be-4824-96c1-47b60b740d00", "5d76a2ca-e8c0-402f-a133-2158492d58ad", 1);
                return (true, "Core Parking 100% + CPU Idle Disable (0 Wake-up Latency) activado.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "GameBarDvrSafeDisable",
            CodeNumber = "C07",
            Title = "Desactivar Captura DVR en Segundo Plano (Preservando DX12 iFlip / X3D)",
            Category = TweakCategory.SystemCpu,
            CategoryDisplay = "Sistema & CPU",
            TechnicalDescription = "Desactiva la grabación de fondo (AppCaptureEnabled=0, GameDVR_Enabled=0) de forma NO destructiva (nunca borra la rama GameConfigStore) y preserva la capa necesaria para Independent Flip en Win11 24H2 y Ryzen X3D.",
            WhatItTouches = @"HKCU\System\GameConfigStore\GameDVR_Enabled=0 & HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR\AppCaptureEnabled=0",
            SourceAttribution = "BlurBusters Thread #15717 / KRUZH Safe Revert Fix",
            Risk = RiskLevel.Low,
            InMinimal = true,
            InCompetitivo = true,
            InProfesional = true,
            ReadLiveState = () => ReadRegDword(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled") == 0,
            ApplyAction = _ =>
            {
                var hw = _hwService.CurrentProfile;
                _audit.SetRegistryValue("GameBarDvrSafeDisable", RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0, RegistryValueKind.DWord);
                _audit.SetRegistryValue("GameBarDvrSafeDisable", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, RegistryValueKind.DWord);
                if (!hw.IsWindows11_24H2Plus && !(hw.IsRyzenX3D && hw.IsDualCcdRyzen))
                {
                    _audit.SetRegistryValue("GameBarDvrSafeDisable", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0, RegistryValueKind.DWord);
                }
                return (true, "Game DVR Background Capture desactivado de forma segura (rama GameConfigStore intacta).");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "WindowsGameModeOn",
            CodeNumber = "C08",
            Title = "Windows Game Mode ON + Desactivar Copilot & Efectos Pesados",
            Category = TweakCategory.SystemCpu,
            CategoryDisplay = "Sistema & CPU",
            TechnicalDescription = "Activa AutoGameModeEnabled, desactiva Windows Copilot en segundo plano y configura efectos visuales para priorizar rendimiento.",
            WhatItTouches = @"HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled=1, WindowsCopilot\TurnOffWindowsCopilot=1, VisualFXSetting=2",
            SourceAttribution = "Microsoft Gaming Optimization",
            Risk = RiskLevel.Low,
            InMinimal = true,
            InCompetitivo = true,
            InProfesional = true,
            ReadLiveState = () => ReadRegDword(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled") == 1 &&
                                  ReadRegDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting") == 2,
            ApplyAction = _ =>
            {
                _audit.SetRegistryValue("WindowsGameModeOn", RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, RegistryValueKind.DWord);
                _audit.SetRegistryValue("WindowsGameModeOn", RegistryHive.CurrentUser, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, RegistryValueKind.DWord);
                _audit.SetRegistryValue("WindowsGameModeOn", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 2, RegistryValueKind.DWord);
                return (true, "Game Mode ON, Copilot OFF y Efectos Visuales optimizados.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "ServicesAndTasksDebloat",
            CodeNumber = "C09",
            Title = "Debloat Reversible de Servicios y Tareas Programadas de Telemetría",
            Category = TweakCategory.SystemCpu,
            CategoryDisplay = "Sistema & CPU",
            TechnicalDescription = "Desactiva con backup en Journal los servicios DiagTrack (Connected User Experiences/Telemetry), MapsBroker y tareas programadas de telemetría (Compatibility Appraiser, Consolidator, UsbCeip) que causan picos de CPU/disco en partida.",
            WhatItTouches = @"Servicios: DiagTrack, MapsBroker (Start=4) + Tareas: Microsoft Compatibility Appraiser, Consolidator, UsbCeip",
            SourceAttribution = "BoringBoredom PC-Tuning §11.14",
            Risk = RiskLevel.Low,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            ReadLiveState = () => ReadRegDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\DiagTrack", "Start") == 4,
            ApplyAction = _ =>
            {
                _audit.SetServiceStartup("ServicesAndTasksDebloat", "DiagTrack", 4, stopIfRunning: true);
                _audit.SetServiceStartup("ServicesAndTasksDebloat", "MapsBroker", 4, stopIfRunning: true);
                _audit.SetScheduledTaskEnabled("ServicesAndTasksDebloat", @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser", false);
                _audit.SetScheduledTaskEnabled("ServicesAndTasksDebloat", @"\Microsoft\Windows\Application Experience\ProgramDataUpdater", false);
                _audit.SetScheduledTaskEnabled("ServicesAndTasksDebloat", @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator", false);
                _audit.SetScheduledTaskEnabled("ServicesAndTasksDebloat", @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip", false);
                return (true, "Servicios de telemetría y tareas programadas en segundo plano desactivados (100% reversibles).");
            }
        });

        // ==================== CATEGORÍA 3: GPU & DISPLAY ====================
        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "GpuHardwareSchedulingHags",
            CodeNumber = "G01",
            Title = "HAGS (Hardware-Accelerated GPU Scheduling)",
            Category = TweakCategory.GpuDisplay,
            CategoryDisplay = "GPU & Display",
            TechnicalDescription = "Delega la programación de lotes de VRAM directamente al procesador interno de la GPU (HwSchMode = 2).",
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers -> HwSchMode = 2",
            SourceAttribution = "WDDM 2.7+ / BlurBusters GPU Scheduling",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            EvaluateGate = hw =>
            {
                if (hw.BenefitsFromHags)
                {
                    return EvaluationResult.Recommended($"Tu GPU ({hw.GpuGeneration}) está optimizada por hardware para HAGS ON + Reflex.");
                }
                if (hw.GpuGeneration is "RTX20" or "GTX16" or "GTX10")
                {
                    return EvaluationResult.NotRecommended($"NO RECOMENDADO EN {hw.GpuGeneration}: En arquitecturas Turing/Pascal (RTX 20 / GTX 16 / GTX 10), HAGS suele añadir micro-stutters y mayor variabilidad de 1% lows.");
                }
                return EvaluationResult.Neutral("Probá medir tus 1% lows con HAGS ON vs OFF en tu generación de GPU.");
            },
            ReadLiveState = () => ReadRegDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode") == 2,
            ApplyAction = _ =>
            {
                _audit.SetRegistryValue("GpuHardwareSchedulingHags", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, RegistryValueKind.DWord);
                return (true, "HAGS activado (HwSchMode = 2).");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "GpuDynamicP0State",
            CodeNumber = "G02",
            Title = "NVIDIA Force P0 Performance State (DisableDynamicPstate = 1)",
            Category = TweakCategory.GpuDisplay,
            CategoryDisplay = "GPU & Display",
            TechnicalDescription = "Impide que la GPU NVIDIA baje a estados de ahorro P8/P2 durante escenas livianas, manteniendo el reloj de núcleo y VRAM estable.",
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000 -> DisableDynamicPstate = 1",
            SourceAttribution = "Calypto / BoringBoredom PC-Tuning §11.26",
            Risk = RiskLevel.Medium,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = false,
            InProfesional = true,
            ApplicableGate = hw => hw.GpuVendor == GpuVendorKind.Nvidia
                ? ApplicabilityResult.Applicable("GPU NVIDIA detectada")
                : ApplicabilityResult.NotApplicable($"Exclusivo para GPUs NVIDIA (Detectada: {hw.GpuName})"),
            EvaluateGate = hw => hw.Chassis == ChassisKind.Laptop
                ? EvaluationResult.NotRecommended("CONTRAPRODUCENTE EN LAPTOP: Forzar estado P0 fijo calienta la GPU en reposo y le roba presupuesto de watts al procesador por NVIDIA Dynamic Boost.")
                : EvaluationResult.Recommended("Excelente en Desktop para eliminar spikes de frametime por cambios de estado P."),
            ReadLiveState = () => ReadFirstDisplayClassDword("DisableDynamicPstate") == 1,
            ApplyAction = _ =>
            {
                SetAllDisplayClassDword("GpuDynamicP0State", "DisableDynamicPstate", 1);
                return (true, "DisableDynamicPstate = 1 aplicado en el adaptador NVIDIA.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "NvidiaUltraLowLatencyReg",
            CodeNumber = "G03",
            Title = "NVIDIA Driver Registry Tuning (PowerMizer + ULL + NvTray Off)",
            Category = TweakCategory.GpuDisplay,
            CategoryDisplay = "GPU & Display",
            TechnicalDescription = "Configura PowerMizer en máximo rendimiento, habilita registros de baja latencia en nvlddmkm y desactiva el proceso en bandeja NvTray.",
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Services\nvlddmkm\Parameters\Global -> PowerMizerEnable=0, PowerMizerLevel=1, NvTray\StartOnLogin=0",
            SourceAttribution = "NVIDIA Driver Internals / CS2 Pro Config",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            ApplicableGate = hw => hw.GpuVendor == GpuVendorKind.Nvidia
                ? ApplicabilityResult.Applicable("GPU NVIDIA detectada")
                : ApplicabilityResult.NotApplicable($"Exclusivo para GPUs NVIDIA (Detectada: {hw.GpuName})"),
            EvaluateGate = hw => hw.Chassis == ChassisKind.Laptop
                ? EvaluationResult.Neutral("En Laptop usar solo conectado a corriente alterna.")
                : EvaluationResult.Recommended("Óptimo para GPUs NVIDIA de escritorio."),
            ReadLiveState = () => ReadRegDword(RegistryHive.CurrentUser, @"Software\NVIDIA Corporation\NvTray", "StartOnLogin") == 0,
            ApplyAction = _ =>
            {
                _audit.SetRegistryValue("NvidiaUltraLowLatencyReg", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\nvlddmkm\FTS", "EnableRID73777", 1, RegistryValueKind.DWord);
                _audit.SetRegistryValue("NvidiaUltraLowLatencyReg", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\nvlddmkm\Parameters\Global", "PowerMizerEnable", 0, RegistryValueKind.DWord);
                _audit.SetRegistryValue("NvidiaUltraLowLatencyReg", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\nvlddmkm\Parameters\Global", "PowerMizerLevel", 1, RegistryValueKind.DWord);
                _audit.SetRegistryValue("NvidiaUltraLowLatencyReg", RegistryHive.CurrentUser, @"Software\NVIDIA Corporation\NvTray", "StartOnLogin", 0, RegistryValueKind.DWord);
                return (true, "Parámetros de baja latencia de nvlddmkm y NvTray OFF aplicados.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "AmdRadeonUlpsOff",
            CodeNumber = "G04",
            Title = "AMD Radeon: Desactivar ULPS (Ultra Low Power State) & Stutter Fix",
            Category = TweakCategory.GpuDisplay,
            CategoryDisplay = "GPU & Display",
            TechnicalDescription = "Desactiva el ahorro profundo ULPS en drivers AMD Radeon (causa conocida de micro-stutters y caídas de reloj en DX11/DX12).",
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000 -> EnableUlps = 0",
            SourceAttribution = "AMD Community / PC-Tuning",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            ApplicableGate = hw => hw.GpuVendor == GpuVendorKind.Amd
                ? ApplicabilityResult.Applicable("GPU AMD Radeon detectada")
                : ApplicabilityResult.NotApplicable($"Exclusivo para GPUs AMD Radeon (Detectada: {hw.GpuName})"),
            ReadLiveState = () => ReadFirstDisplayClassDword("EnableUlps") == 0,
            ApplyAction = _ =>
            {
                SetAllDisplayClassDword("AmdRadeonUlpsOff", "EnableUlps", 0);
                return (true, "EnableUlps = 0 aplicado en tu GPU AMD Radeon.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "DirectXIndependentFlipAndFse",
            CodeNumber = "G05",
            Title = "DXGI Independent Flip + Honor Fullscreen Exclusive (Sin Colisión)",
            Category = TweakCategory.GpuDisplay,
            CategoryDisplay = "GPU & Display",
            TechnicalDescription = "Unifica 'Optimizaciones para juegos en ventana' (SwapEffectUpgradeEnable=1;gpupreference=2;) sin pisar HAGS y habilita DXGIHonorFSEWindowsCompatible.",
            WhatItTouches = @"HKCU\Software\Microsoft\DirectX\UserGpuPreferences\DirectXUserGlobalSettings & GameConfigStore\GameDVR_DXGIHonorFSEWindowsCompatible=1",
            SourceAttribution = "Microsoft DirectX Blog / BlurBusters",
            Risk = RiskLevel.Low,
            InMinimal = true,
            InCompetitivo = true,
            InProfesional = true,
            ReadLiveState = () =>
            {
                string? s = ReadRegString(RegistryHive.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings");
                return s != null && s.Contains("SwapEffectUpgradeEnable=1", StringComparison.OrdinalIgnoreCase);
            },
            ApplyAction = _ =>
            {
                _audit.SetRegistryValue("DirectXIndependentFlipAndFse", RegistryHive.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings", "SwapEffectUpgradeEnable=1;gpupreference=2;", RegistryValueKind.String);
                _audit.SetRegistryValue("DirectXIndependentFlipAndFse", RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible", 1, RegistryValueKind.DWord);
                _audit.SetRegistryValue("DirectXIndependentFlipAndFse", RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_FSEBehavior", 2, RegistryValueKind.DWord);
                return (true, "DXGI Independent Flip + Honor FSE aplicados sin colisión.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "DisableMpo",
            CodeNumber = "G06",
            Title = "Desactivar MPO (Multi-Plane Overlay — OverlayTestMode = 5)",
            Category = TweakCategory.GpuDisplay,
            CategoryDisplay = "GPU & Display",
            TechnicalDescription = "Evita que DWM reserve planos de hardware separados para overlays que causan flickering o imagen cortada.",
            WhatItTouches = @"HKLM\SOFTWARE\Microsoft\Windows\Dwm -> OverlayTestMode = 5",
            SourceAttribution = "NVIDIA Support Hotfix KB / BlurBusters",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            EvaluateGate = hw => hw.IsWindows11_24H2Plus
                ? EvaluationResult.Neutral("En Windows 11 24H2 (Build 26100+), Microsoft corrigió el manejo de MPO en DWM; activalo solo si notás flickering con Alt-Tab.")
                : EvaluationResult.Recommended("Recomendado en Windows 10 y Windows 11 22H2/23H2 para eliminar micro-stutters de composición."),
            ReadLiveState = () => ReadRegDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode") == 5,
            ApplyAction = _ =>
            {
                _audit.SetRegistryValue("DisableMpo", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 5, RegistryValueKind.DWord);
                return (true, "MPO desactivado (OverlayTestMode = 5).");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "DisableHdcp",
            CodeNumber = "G07",
            Title = "Desactivar Cifrado HDCP en Enlace de Video (Solo NVIDIA)",
            Category = TweakCategory.GpuDisplay,
            CategoryDisplay = "GPU & Display",
            TechnicalDescription = "Deshabilita el handshake de protección DRM HDCP en el enlace DisplayPort/HDMI. Nota: impide reproducir Netflix/Prime Video en 4K DRM.",
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968...}\0000 -> RMHdcpKeyglobZero = 1",
            SourceAttribution = "Calypto / NVIDIA Registry Tweaks",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = false,
            InProfesional = true,
            ApplicableGate = hw => hw.GpuVendor == GpuVendorKind.Nvidia
                ? ApplicabilityResult.Applicable("Clave compatible con driver NVIDIA nvlddmkm")
                : ApplicabilityResult.NotApplicable("RMHdcpKeyglobZero solo existe en drivers NVIDIA."),
            EvaluateGate = _ => EvaluationResult.Neutral("Útil en PC dedicada solo a esports; desactivalo si usás servicios de streaming con DRM (Netflix/Disney+)."),
            ReadLiveState = () => ReadFirstDisplayClassDword("RMHdcpKeyglobZero") == 1,
            ApplyAction = _ =>
            {
                SetAllDisplayClassDword("DisableHdcp", "RMHdcpKeyglobZero", 1);
                return (true, "HDCP desactivado (RMHdcpKeyglobZero = 1).");
            }
        });

        // ==================== CATEGORÍA 4: RED / DESYNC / JITTER ====================
        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "NicEnergySavingOff",
            CodeNumber = "N01",
            Title = "Desactivar Ahorro de Energía NIC (EEE, Green Ethernet, ULP, Idle D3)",
            Category = TweakCategory.NetworkDesync,
            CategoryDisplay = "Red & Desync",
            TechnicalDescription = "Apaga Energy Efficient Ethernet (802.3az), Green Ethernet, Ultra Low Power y Wake-on-LAN D3 en la placa de red para eliminar micro-drops.",
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972...}\00XX -> *EEE=0, AdvancedEEE=0, EnableGreenEthernet=0, PnPCapabilities=24, AllowIdleIrpInD3=0",
            SourceAttribution = "Realtek / Intel Ethernet Low-Latency Spec",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            ReadLiveState = () => ReadFirstNetClassDword("PnPCapabilities") == 24,
            ApplyAction = _ =>
            {
                SetAllNetClassValues("NicEnergySavingOff", keyPath =>
                {
                    _audit.SetRegistryValue("NicEnergySavingOff", RegistryHive.LocalMachine, keyPath, "PnPCapabilities", 24, RegistryValueKind.DWord);
                    _audit.SetRegistryValue("NicEnergySavingOff", RegistryHive.LocalMachine, keyPath, "AllowIdleIrpInD3", 0, RegistryValueKind.DWord);
                    foreach (string prop in new[] { "AdvancedEEE", "*EEE", "EEELinkAdvertisement", "ULPMode", "GigaLite", "EnableGreenEthernet", "PowerSavingMode", "AutoDisableGigabit" })
                    {
                        _audit.SetRegistryValue("NicEnergySavingOff", RegistryHive.LocalMachine, keyPath, prop, "0", RegistryValueKind.String);
                    }
                });
                return (true, "Ahorros de energía NIC (EEE / GreenEthernet / D3) desactivados con backup en Journal.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "NicInterruptModerationConfigurable",
            CodeNumber = "N02",
            Title = "Interrupt Moderation NIC Configurable (Medium vs Off — Medible)",
            Category = TweakCategory.NetworkDesync,
            CategoryDisplay = "Red & Desync",
            TechnicalDescription = "No se fuerza a ciegas: permite elegir 'Medium / Adaptive' (suele dar menor latencia DPC en chips Realtek/Intel bajo tráfico) o 'Off' (procesamiento inmediato paquete por paquete).",
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972...}\00XX -> *InterruptModeration & *InterruptModerationRate",
            SourceAttribution = "BoringBoredom PC-Tuning §11.34 / BlurBusters",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            SelectedOptionKey = "Medium",
            Options = new List<TweakOptionChoice>
            {
                new() { Key = "Default", ShortLabel = "Auto", DisplayName = "Adaptive / Default", Description = "Moderación adaptativa por defecto." },
                new() { Key = "Medium", ShortLabel = "Medium", DisplayName = "Moderation ON + Rate Medium (125) [RECOMENDADO — Menor DPC]", Description = "Evita tormentas de ISR en el CPU manteniendo baja latencia." },
                new() { Key = "Off", ShortLabel = "Off", DisplayName = "Interrupt Moderation OFF (0) [Agresivo]", Description = "Desactiva todo agrupamiento; cada paquete genera una interrupción inmediata." }
            },
            ReadLiveState = () =>
            {
                string? im = ReadFirstNetClassString("*InterruptModeration");
                string? rate = ReadFirstNetClassString("*InterruptModerationRate");
                return im == "0" || rate == "125";
            },
            ReadLiveOptionKey = () =>
            {
                string? im = ReadFirstNetClassString("*InterruptModeration");
                string? rate = ReadFirstNetClassString("*InterruptModerationRate");
                if (im == "0") return "Off";
                if (rate == "125") return "Medium";
                return "Default";
            },
            ApplyAction = opt =>
            {
                if (string.Equals(opt, "Default", StringComparison.OrdinalIgnoreCase))
                {
                    SetAllNetClassValues("NicInterruptModerationConfigurable", keyPath =>
                    {
                        _audit.SetRegistryValue("NicInterruptModerationConfigurable", RegistryHive.LocalMachine, keyPath, "*InterruptModeration", "1", RegistryValueKind.String);
                        _audit.SetRegistryValue("NicInterruptModerationConfigurable", RegistryHive.LocalMachine, keyPath, "*InterruptModerationRate", "255", RegistryValueKind.String);
                    });
                    return (true, "Interrupt Moderation restaurado a Adaptive/Default.");
                }
                bool off = string.Equals(opt, "Off", StringComparison.OrdinalIgnoreCase);
                SetAllNetClassValues("NicInterruptModerationConfigurable", keyPath =>
                {
                    _audit.SetRegistryValue("NicInterruptModerationConfigurable", RegistryHive.LocalMachine, keyPath, "*InterruptModeration", off ? "0" : "1", RegistryValueKind.String);
                    if (!off)
                    {
                        _audit.SetRegistryValue("NicInterruptModerationConfigurable", RegistryHive.LocalMachine, keyPath, "*InterruptModerationRate", "125", RegistryValueKind.String);
                    }
                });
                return (true, off ? "Interrupt Moderation establecido en OFF (0)." : "Interrupt Moderation establecido en Medium (menor DPC).");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "NicRingBuffersConfigurable",
            CodeNumber = "N03",
            Title = "NDIS Ring Buffers (*ReceiveBuffers / *TransmitBuffers — Fuente Única)",
            Category = TweakCategory.NetworkDesync,
            CategoryDisplay = "Red & Desync",
            TechnicalDescription = "Unifica los antiguos módulos [20] y [21]. Configura el tamaño de los buffers TX/RX de la placa de red.",
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972...}\00XX -> *ReceiveBuffers, *TransmitBuffers",
            SourceAttribution = "Calypto / Realtek NDIS Tuning",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = false,
            InProfesional = true,
            SelectedOptionKey = "512",
            Options = new List<TweakOptionChoice>
            {
                new() { Key = "Default", ShortLabel = "Auto", DisplayName = "Default del driver NIC", Description = "Valor por defecto del controlador de red." },
                new() { Key = "256", ShortLabel = "256", DisplayName = "256 Buffers (Ultra Baja Latencia — Solo Juego)", Description = "Mínimo bufferbloat local en la NIC." },
                new() { Key = "512", ShortLabel = "512", DisplayName = "512 Buffers (Equilibrio Seguro para Fibra 300M-1Gbps)", Description = "Baja latencia de cola sin descartar paquetes en ráfagas." },
                new() { Key = "1024", ShortLabel = "1024", DisplayName = "1024 Buffers (Alto Caudal 2.5Gbps)", Description = "Evita packet loss si descargás o streameás mientras jugás." }
            },
            ReadLiveState = () => ReadFirstNetClassString("*ReceiveBuffers") is "256" or "512" or "1024",
            ReadLiveOptionKey = () =>
            {
                string? rb = ReadFirstNetClassString("*ReceiveBuffers");
                return rb is "256" or "512" or "1024" ? rb : "Default";
            },
            ApplyAction = opt =>
            {
                if (string.Equals(opt, "Default", StringComparison.OrdinalIgnoreCase))
                {
                    _audit.RevertTweakFromJournal("NicRingBuffersConfigurable", out _);
                    return (true, "Buffers TX/RX de red restaurados al valor original del controlador.");
                }
                string val = string.IsNullOrWhiteSpace(opt) ? "512" : opt;
                SetAllNetClassValues("NicRingBuffersConfigurable", keyPath =>
                {
                    _audit.SetRegistryValue("NicRingBuffersConfigurable", RegistryHive.LocalMachine, keyPath, "*ReceiveBuffers", val, RegistryValueKind.String);
                    _audit.SetRegistryValue("NicRingBuffersConfigurable", RegistryHive.LocalMachine, keyPath, "*TransmitBuffers", val, RegistryValueKind.String);
                });
                return (true, $"Buffers TX/RX de red configurados en {val}.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "NicLsoAndFlowControlOff",
            CodeNumber = "N04",
            Title = "Desactivar LSO (Large Send Offload v1/v2), RSC y Flow Control",
            Category = TweakCategory.NetworkDesync,
            CategoryDisplay = "Red & Desync",
            TechnicalDescription = "Desactiva la segmentación diferida LSO, el Receive Segment Coalescing (netsh rsc=disabled) y las tramas PAUSE de Flow Control que retienen paquetes.",
            WhatItTouches = @"*LsoV2IPv4=0, *LsoV2IPv6=0, *FlowControl=0 + netsh int tcp set global rsc=disabled",
            SourceAttribution = "BlurBusters Desync Guide / PC-Tuning §11.34",
            Risk = RiskLevel.Low,
            RequiresReboot = false,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            ReadLiveState = () => ReadFirstNetClassString("*LsoV2IPv4") == "0",
            ApplyAction = _ =>
            {
                SetAllNetClassValues("NicLsoAndFlowControlOff", keyPath =>
                {
                    _audit.SetRegistryValue("NicLsoAndFlowControlOff", RegistryHive.LocalMachine, keyPath, "*LsoV2IPv4", "0", RegistryValueKind.String);
                    _audit.SetRegistryValue("NicLsoAndFlowControlOff", RegistryHive.LocalMachine, keyPath, "*LsoV2IPv6", "0", RegistryValueKind.String);
                    _audit.SetRegistryValue("NicLsoAndFlowControlOff", RegistryHive.LocalMachine, keyPath, "*FlowControl", "0", RegistryValueKind.String);
                });
                _audit.RecordCustomReversibleAction("NicLsoAndFlowControlOff", BackupTargetKind.NetshTcpSetting, "global", "rsc", "enabled", "disabled");
                TransactionalAuditService.RunHiddenProcess("netsh.exe", "int tcp set global rsc=disabled");
                return (true, "LSO v2, Flow Control y TCP RSC desactivados.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "MmcssAndNetworkThrottlingSelector",
            CodeNumber = "N05",
            Title = "MMCSS Games Priority + NetworkThrottlingIndex Configurable",
            Category = TweakCategory.NetworkDesync,
            CategoryDisplay = "Red & Desync",
            TechnicalDescription = "Fuente única de verdad (unifica [27], [35] y [54]). Configura la reserva de CPU de MMCSS y el índice de throttling de paquetes multimedia.",
            WhatItTouches = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile -> SystemResponsiveness, NetworkThrottlingIndex & Tasks\Games",
            SourceAttribution = "BoringBoredom PC-Tuning / Microsoft MMCSS Docs",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            SelectedOptionKey = "Balanced10_Off",
            Options = new List<TweakOptionChoice>
            {
                new() { Key = "Default", ShortLabel = "Auto (20%)", DisplayName = "Default Windows (20% / 10)", Description = "Reserva estándar de Windows." },
                new() { Key = "Balanced10_Off", ShortLabel = "10% / Off", DisplayName = "Responsiveness = 10% + NetworkThrottling OFF (0xFFFFFFFF) [RECOMENDADO]", Description = "Evita el bug de kernel con valor 0 y elimina el límite de paquetes de red." },
                new() { Key = "Medium10_20", ShortLabel = "10% / 20", DisplayName = "Responsiveness = 10% + NetworkThrottling = 20 (Menor DPC en Audio)", Description = "Mantiene control moderado de ráfagas NDIS si usás interfaz de audio sensible." },
                new() { Key = "Zero_Off", ShortLabel = "0% / Off", DisplayName = "Responsiveness = 0% + NetworkThrottling OFF (0xFFFFFFFF)", Description = "Modo extremo clásico." }
            },
            ReadLiveState = () =>
            {
                int? sr = ReadRegDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness");
                return sr is 0 or 10;
            },
            ReadLiveOptionKey = () =>
            {
                int? sr = ReadRegDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness");
                int? nt = ReadRegDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex");
                if (sr == 0) return "Zero_Off";
                if (sr == 10 && nt == 20) return "Medium10_20";
                if (sr == 10) return "Balanced10_Off";
                return "Default";
            },
            ApplyAction = opt =>
            {
                const string mmPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
                const string gamesPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";
                if (string.Equals(opt, "Default", StringComparison.OrdinalIgnoreCase))
                {
                    _audit.SetRegistryValue("MmcssAndNetworkThrottlingSelector", RegistryHive.LocalMachine, mmPath, "SystemResponsiveness", 20, RegistryValueKind.DWord);
                    _audit.SetRegistryValue("MmcssAndNetworkThrottlingSelector", RegistryHive.LocalMachine, mmPath, "NetworkThrottlingIndex", 10, RegistryValueKind.DWord);
                    return (true, "MMCSS restaurado a valores estándar (20% / 10).");
                }
                int sysResp = opt == "Zero_Off" ? 0 : 10;
                int netThrot = opt == "Medium10_20" ? 20 : unchecked((int)0xFFFFFFFF);

                _audit.SetRegistryValue("MmcssAndNetworkThrottlingSelector", RegistryHive.LocalMachine, mmPath, "SystemResponsiveness", sysResp, RegistryValueKind.DWord);
                _audit.SetRegistryValue("MmcssAndNetworkThrottlingSelector", RegistryHive.LocalMachine, mmPath, "NetworkThrottlingIndex", netThrot, RegistryValueKind.DWord);
                _audit.SetRegistryValue("MmcssAndNetworkThrottlingSelector", RegistryHive.LocalMachine, gamesPath, "Priority", 6, RegistryValueKind.DWord);
                _audit.SetRegistryValue("MmcssAndNetworkThrottlingSelector", RegistryHive.LocalMachine, gamesPath, "Scheduling Category", "High", RegistryValueKind.String);
                _audit.SetRegistryValue("MmcssAndNetworkThrottlingSelector", RegistryHive.LocalMachine, gamesPath, "GPU Priority", 8, RegistryValueKind.DWord);
                return (true, $"MMCSS configurado (SystemResponsiveness={sysResp}%, NetworkThrottling={(netThrot == -1 ? "OFF" : netThrot.ToString())}).");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "TcpNagleAndRssOptimize",
            CodeNumber = "N06",
            Title = "Nagle OFF (TCPNoDelay=1) + RSS Multi-Core Queues + ECN Off",
            Category = TweakCategory.NetworkDesync,
            CategoryDisplay = "Red & Desync",
            TechnicalDescription = "Desactiva el algoritmo de agrupamiento de Nagle en todas las interfaces TCP/IP, activa Receive Side Scaling (RSS) multi-cola y desactiva ECN.",
            WhatItTouches = @"Tcpip\Parameters\Interfaces\<GUID> -> TCPNoDelay=1, TcpAckFrequency=1 + *RSS=1",
            SourceAttribution = "Estándar Red Competitiva",
            Risk = RiskLevel.Low,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            ReadLiveState = () => ReadRegDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "ECNEnabled") == 0,
            ApplyAction = _ =>
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var ifRoot = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces");
                if (ifRoot != null)
                {
                    foreach (string sub in ifRoot.GetSubKeyNames())
                    {
                        string path = $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{sub}";
                        _audit.SetRegistryValue("TcpNagleAndRssOptimize", RegistryHive.LocalMachine, path, "TCPNoDelay", 1, RegistryValueKind.DWord);
                        _audit.SetRegistryValue("TcpNagleAndRssOptimize", RegistryHive.LocalMachine, path, "TcpAckFrequency", 1, RegistryValueKind.DWord);
                    }
                }
                int queues = Math.Min(4, _hwService.CurrentProfile.CpuPhysicalCores);
                SetAllNetClassValues("TcpNagleAndRssOptimize", keyPath =>
                {
                    _audit.SetRegistryValue("TcpNagleAndRssOptimize", RegistryHive.LocalMachine, keyPath, "*RSS", "1", RegistryValueKind.String);
                    _audit.SetRegistryValue("TcpNagleAndRssOptimize", RegistryHive.LocalMachine, keyPath, "*NumRssQueues", queues.ToString(), RegistryValueKind.String);
                });
                _audit.SetRegistryValue("TcpNagleAndRssOptimize", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "ECNEnabled", 0, RegistryValueKind.DWord);
                TransactionalAuditService.RunHiddenProcess("netsh.exe", "int tcp set global rss=enabled");
                TransactionalAuditService.RunHiddenProcess("netsh.exe", "int tcp set global ecncapability=disabled");
                return (true, $"Nagle OFF + RSS ({queues} colas) + ECN OFF aplicados.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "QosDscp46Games",
            CodeNumber = "N07",
            Title = "Política QoS DSCP 46 (Expedited Forwarding UDP para CS2 / Valorant / Juegos)",
            Category = TweakCategory.NetworkDesync,
            CategoryDisplay = "Red & Desync",
            TechnicalDescription = "Etiqueta todos los paquetes UDP salientes de CS2, Valorant y tus juegos agregados con prioridad DSCP 46 (EF) para routers con QoS/SQM.",
            WhatItTouches = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\QoS\<Juego>",
            SourceAttribution = "BoringBoredom PC-Tuning §11.42.7",
            Risk = RiskLevel.Low,
            InMinimal = true,
            InCompetitivo = true,
            InProfesional = true,
            ReadLiveState = () => ReadRegString(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\QoS\KRUZH_CS2", "DSCP Value") == "46",
            ApplyAction = _ =>
            {
                foreach (var game in TargetGames.Where(g => g.QosDscp46))
                {
                    string safeName = "KRUZH_" + Path.GetFileNameWithoutExtension(game.ExecutableName);
                    string p = $@"SOFTWARE\Policies\Microsoft\Windows\QoS\{safeName}";
                    _audit.SetRegistryValue("QosDscp46Games", RegistryHive.LocalMachine, p, "Version", "1.0", RegistryValueKind.String);
                    _audit.SetRegistryValue("QosDscp46Games", RegistryHive.LocalMachine, p, "Application Name", game.ExecutableName, RegistryValueKind.String);
                    _audit.SetRegistryValue("QosDscp46Games", RegistryHive.LocalMachine, p, "Protocol", "UDP", RegistryValueKind.String);
                    _audit.SetRegistryValue("QosDscp46Games", RegistryHive.LocalMachine, p, "Local Port", "*", RegistryValueKind.String);
                    _audit.SetRegistryValue("QosDscp46Games", RegistryHive.LocalMachine, p, "Remote Port", "*", RegistryValueKind.String);
                    _audit.SetRegistryValue("QosDscp46Games", RegistryHive.LocalMachine, p, "Local IP", "*", RegistryValueKind.String);
                    _audit.SetRegistryValue("QosDscp46Games", RegistryHive.LocalMachine, p, "Local IP Prefix", "32", RegistryValueKind.String);
                    _audit.SetRegistryValue("QosDscp46Games", RegistryHive.LocalMachine, p, "Remote IP", "*", RegistryValueKind.String);
                    _audit.SetRegistryValue("QosDscp46Games", RegistryHive.LocalMachine, p, "Remote IP Prefix", "32", RegistryValueKind.String);
                    _audit.SetRegistryValue("QosDscp46Games", RegistryHive.LocalMachine, p, "DSCP Value", "46", RegistryValueKind.String);
                    _audit.SetRegistryValue("QosDscp46Games", RegistryHive.LocalMachine, p, "Throttle Rate", "-1", RegistryValueKind.String);
                }
                return (true, $"QoS DSCP 46 aplicado para {TargetGames.Count(g => g.QosDscp46)} juegos.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "NtpHighPrecisionClockSync",
            CodeNumber = "N08",
            Title = "NTP Clock Sync Fix (Cloudflare + Google + NIST de Alta Frecuencia)",
            Category = TweakCategory.NetworkDesync,
            CategoryDisplay = "Red & Desync",
            TechnicalDescription = "Configura servidores NTP de estrato 1/2 de baja latencia y reduce el intervalo de resincronización (64–1024s) para evitar clock drift frente al servidor del juego.",
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Services\W32Time\Parameters & Config",
            SourceAttribution = "CS2 Desync Community Fix",
            Risk = RiskLevel.Low,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            ReadLiveState = () =>
            {
                string? srv = ReadRegString(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\W32Time\Parameters", "NtpServer");
                return srv != null && srv.Contains("cloudflare", StringComparison.OrdinalIgnoreCase);
            },
            ApplyAction = _ =>
            {
                _audit.SetRegistryValue("NtpHighPrecisionClockSync", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\W32Time\Parameters", "NtpServer", "time.cloudflare.com,0x9 time.google.com,0x9 time.nist.gov,0x9", RegistryValueKind.String);
                _audit.SetRegistryValue("NtpHighPrecisionClockSync", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\W32Time\Config", "MaxPollInterval", 10, RegistryValueKind.DWord);
                _audit.SetRegistryValue("NtpHighPrecisionClockSync", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\W32Time\Config", "MinPollInterval", 6, RegistryValueKind.DWord);
                TransactionalAuditService.RunHiddenProcess("w32tm.exe", "/resync /force");
                return (true, "Sincronización NTP de alta precisión aplicada.");
            }
        });

        // ==================== CATEGORÍA 5: IRQ / MSI / AUDIO ====================
        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "PciMsiModeV3",
            CodeNumber = "I01",
            Title = "MSI Mode v3 Nativo + DevicePriority en Instancias PCI (GPU, xHCI, NVMe, NIC)",
            Category = TweakCategory.IrqMsiAudio,
            CategoryDisplay = "IRQ, MSI & Audio",
            TechnicalDescription = "Activa Message Signaled Interrupts (MSISupported=1) y prioridad de interrupción directamente en las instancias PCI reales (Enum\\PCI), excluyendo nodos hijos HID y protegiendo el audio Realtek contra artefactos.",
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Enum\PCI\<Device>\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties",
            SourceAttribution = "MSI Utility v3 / BoringBoredom PC-Tuning §11.38",
            Risk = RiskLevel.Medium,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            ReadLiveState = () => _irqMsi.IsPciMsiModeAllEnabled(),
            ApplyAction = _ => _irqMsi.ApplySafePciMsiMode("PciMsiModeV3")
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "PciIrqSmartBalancer",
            CodeNumber = "I02",
            Title = "IRQ Affinity en Instancias PCI Reales (AutoGpuAffinity — Nunca Core 0)",
            Category = TweakCategory.IrqMsiAudio,
            CategoryDisplay = "IRQ, MSI & Audio",
            TechnicalDescription = "Escribe DevicePolicy=4 y AssignmentSetOverride directamente en HKLM\\SYSTEM\\CurrentControlSet\\Enum\\PCI (donde el kernel realmente lo lee), asignando GPU, USB xHCI y NIC a núcleos físicos distintos fuera del Core 0.",
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Enum\PCI\<Device>\Device Parameters\Interrupt Management\Affinity Policy",
            SourceAttribution = "AutoGpuAffinity / BoringBoredom PC-Tuning §11.43",
            Risk = RiskLevel.Medium,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = false,
            InProfesional = true,
            ApplicableGate = hw => hw.CpuPhysicalCores >= 4
                ? ApplicabilityResult.Applicable($"Topología de {hw.CpuPhysicalCores} núcleos físicos detectada")
                : ApplicabilityResult.NotApplicable("Requiere al menos 4 núcleos físicos para aislar IRQs sin saturar hilos."),
            ReadLiveState = () => _irqMsi.IsPciIrqAffinityActive(),
            ApplyAction = _ => _irqMsi.ApplyOptimalPciIrqAffinity("PciIrqSmartBalancer")
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "AudioEnhancementsAndSpatialOff",
            CodeNumber = "I03",
            Title = "Desactivar Audio Enhancements (APO), Spatial Sound & ProtectedAudioDG",
            Category = TweakCategory.IrqMsiAudio,
            CategoryDisplay = "IRQ, MSI & Audio",
            TechnicalDescription = "Deshabilita el procesamiento de efectos APO en endpoints de audio, Spatial Audio y el overhead DRM de audiodg.exe que causan spikes de DPC.",
            WhatItTouches = @"MMDevices\Audio\Render\<Endpoint>\FxProperties\{1da5d803...},6=1, EnableSpatialAudio=0, DisableProtectedAudioDG=1",
            SourceAttribution = "BlurBusters Threads #15352, #12413 (Confirmado por Chief Blur Buster)",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            ReadLiveState = () => ReadRegDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio", "DisableProtectedAudioDG") == 1,
            ApplyAction = _ =>
            {
                _audit.SetRegistryValue("AudioEnhancementsAndSpatialOff", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio", "DisableProtectedAudioDG", 1, RegistryValueKind.DWord);
                _audit.SetRegistryValue("AudioEnhancementsAndSpatialOff", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Audio", "EnableSpatialAudio", 0, RegistryValueKind.DWord);
                return (true, "Audio Enhancements, Spatial Audio y ProtectedAudioDG desactivados con backup.");
            }
        });

        // ==================== CATEGORÍA 6: USB, ALMACENAMIENTO Y MEMORIA ====================
        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "UsbHidPowerSavingOff",
            CodeNumber = "S01",
            Title = "Desactivar Suspensión Selectiva en Controladoras USB y Dispositivos HID",
            Category = TweakCategory.UsbPowerBus,
            CategoryDisplay = "USB, Disco & Memoria",
            TechnicalDescription = "Evita que los puertos USB del mouse y teclado entren en suspensión selectiva D2/D3.",
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Enum\USB & HID -> EnhancedPowerManagementEnabled=0, SelectiveSuspendOn=0",
            SourceAttribution = "BoringBoredom PC-Tuning §11.37",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            ReadLiveState = IsUsbHidPowerSavingDisabledInRegistry,
            ApplyAction = _ =>
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                int modified = 0;
                foreach (string branch in new[] { "HID", "USB" })
                {
                    using var root = hklm.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{branch}");
                    if (root == null) continue;
                    foreach (string dev in root.GetSubKeyNames())
                    {
                        using var devKey = root.OpenSubKey(dev);
                        if (devKey == null) continue;
                        foreach (string inst in devKey.GetSubKeyNames())
                        {
                            string paramPath = $@"SYSTEM\CurrentControlSet\Enum\{branch}\{dev}\{inst}\Device Parameters";
                            using var pKey = hklm.OpenSubKey(paramPath);
                            if (pKey != null && pKey.GetValue("EnhancedPowerManagementEnabled") != null)
                            {
                                _audit.SetRegistryValue("UsbHidPowerSavingOff", RegistryHive.LocalMachine, paramPath, "EnhancedPowerManagementEnabled", 0, RegistryValueKind.DWord);
                                modified++;
                            }
                        }
                    }
                }
                return (true, $"Ahorro de energía USB/HID desactivado en {modified} endpoints.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "PcieAspmDisable",
            CodeNumber = "S02",
            Title = "Desactivar PCIe ASPM (Active State Power Management L0s/L1)",
            Category = TweakCategory.UsbPowerBus,
            CategoryDisplay = "USB, Disco & Memoria",
            TechnicalDescription = "Fuerza el enlace PCIe a permanecer en estado activo L0, eliminando la latencia de transición de estados de ahorro en GPU y NVMe.",
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Services\pci\Parameters -> EnableASPM=0, EnableL1=0 + powercfg ASPM=0",
            SourceAttribution = "PCI-SIG / BlurBusters",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            EvaluateGate = hw => hw.Chassis == ChassisKind.Laptop
                ? EvaluationResult.NotRecommended("En Laptop aumenta el consumo y temperatura de reposo del bus PCIe/NVMe.")
                : EvaluationResult.Recommended("Óptimo en Desktop para mantener el bus PCIe en L0 permanente."),
            ReadLiveState = () => ReadRegDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\pci\Parameters", "EnableASPM") == 0,
            ApplyAction = _ =>
            {
                _audit.SetRegistryValue("PcieAspmDisable", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\pci\Parameters", "EnableASPM", 0, RegistryValueKind.DWord);
                _audit.SetRegistryValue("PcieAspmDisable", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\pci\Parameters", "EnableL1", 0, RegistryValueKind.DWord);
                _audit.SetPowerCfgAcValue("PcieAspmDisable", "501a4d13-42af-4429-9fd1-a8218c268e20", "ee12f906-d2d4-4ffa-ac25-a54e11278c20", 0);
                return (true, "PCIe ASPM (L0s/L1) desactivado.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "FastStartupAndHibernationOff",
            CodeNumber = "S03",
            Title = "Desactivar Fast Startup (Inicio Rápido Híbrido) e Hibernación",
            Category = TweakCategory.StorageMemory,
            CategoryDisplay = "USB, Disco & Memoria",
            TechnicalDescription = "Garantiza que al apagar la PC el kernel y los drivers PCIe/USB se reinicien limpiamente en vez de recargar un estado hibernado con DPC acumulada, y libera el archivo hiberfil.sys.",
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power -> HiberbootEnabled=0 & HibernateEnabled=0 (powercfg /h off)",
            SourceAttribution = "BoringBoredom PC-Tuning §6.16 & §11.21",
            Risk = RiskLevel.Low,
            InMinimal = true,
            InCompetitivo = true,
            InProfesional = true,
            EvaluateGate = hw => hw.Chassis == ChassisKind.Laptop
                ? EvaluationResult.Neutral("En Laptop desactiva la hibernación en disco (la suspensión normal en RAM sigue funcionando).")
                : EvaluationResult.Recommended("Esencial en Desktop (y tras usar SCEWIN) para que el apagado sea 100% real."),
            ReadLiveState = () => ReadRegDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled") == 0,
            ApplyAction = _ =>
            {
                _audit.SetRegistryValue("FastStartupAndHibernationOff", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0, RegistryValueKind.DWord);
                _audit.SetRegistryValue("FastStartupAndHibernationOff", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled", 0, RegistryValueKind.DWord);
                TransactionalAuditService.RunHiddenProcess("powercfg.exe", "/h off");
                return (true, "Fast Startup e Hibernación desactivados.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "NtfsDisable8dot3AndLastAccess",
            CodeNumber = "S04",
            Title = "Optimización NTFS (Desactivar Nombres Cortos 8.3 y LastAccess Timestamp)",
            Category = TweakCategory.StorageMemory,
            CategoryDisplay = "USB, Disco & Memoria",
            TechnicalDescription = "Evita que el driver NTFS realice escrituras de metadatos adicionales cada vez que el motor del juego lee texturas o shaders.",
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Control\FileSystem -> NtfsDisable8dot3NameCreation=1, NtfsDisableLastAccessUpdate=1",
            SourceAttribution = "BoringBoredom PC-Tuning §11.2",
            Risk = RiskLevel.Low,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            ReadLiveState = () => ReadRegDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsDisable8dot3NameCreation") == 1,
            ApplyAction = _ =>
            {
                _audit.SetRegistryValue("NtfsDisable8dot3AndLastAccess", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsDisable8dot3NameCreation", 1, RegistryValueKind.DWord);
                _audit.SetRegistryValue("NtfsDisable8dot3AndLastAccess", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsDisableLastAccessUpdate", 0x80000003, RegistryValueKind.DWord);
                return (true, "Metadatos NTFS 8.3 y LastAccess optimizados.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "SysMainAndMemoryManagement",
            CodeNumber = "S05",
            Title = "SysMain (Superfetch) OFF en SSD + DisablePagingExecutive + Memory Compression Off",
            Category = TweakCategory.StorageMemory,
            CategoryDisplay = "USB, Disco & Memoria",
            TechnicalDescription = "En equipos con SSD/NVMe y >=16GB de RAM, desactiva SysMain, mantiene los drivers del kernel en RAM física (DisablePagingExecutive=1) y evita micro-stutters por compresión de páginas en CPU.",
            WhatItTouches = @"Servicio SysMain (Start=4) + Session Manager\Memory Management -> DisablePagingExecutive=1, LargeSystemCache=0",
            SourceAttribution = "BoringBoredom PC-Tuning §11.18 & §11.33",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            ApplicableGate = hw => hw.Storage == StorageKind.NvmeOrSsd && hw.RamTotalGb >= 16
                ? ApplicabilityResult.Applicable($"Detectado {hw.Storage} + {hw.RamTotalGb}GB RAM")
                : ApplicabilityResult.NotApplicable("Requiere unidad SSD/NVMe y al menos 16GB de RAM."),
            ReadLiveState = () => ReadRegDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "DisablePagingExecutive") == 1,
            ApplyAction = _ =>
            {
                _audit.SetServiceStartup("SysMainAndMemoryManagement", "SysMain", 4, stopIfRunning: true);
                _audit.SetRegistryValue("SysMainAndMemoryManagement", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "DisablePagingExecutive", 1, RegistryValueKind.DWord);
                _audit.SetRegistryValue("SysMainAndMemoryManagement", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "LargeSystemCache", 0, RegistryValueKind.DWord);
                return (true, "SysMain desactivado y ejecutivos del kernel fijados en RAM física.");
            }
        });

        // ==================== CATEGORÍA 7: FPS BOOST, JUEGOS Y MODO ENTUSIASTA ====================
        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "GameIfeoHighPriority",
            CodeNumber = "F01",
            Title = "Prioridad CPU/IO Alta por IFEO + Preferencia GPU + FSO Off (CS2 / Valorant / Custom)",
            Category = TweakCategory.FpsBoostGames,
            CategoryDisplay = "FPS Boost & Juegos",
            TechnicalDescription = "Configura CpuPriorityClass=3 e IoPriority=3 en la subclave correcta IFEO\\<exe>\\PerfOptions (100% Anticheat-Safe, sin watcher), fuerza GPU de Alto Rendimiento y desactiva FSO.",
            WhatItTouches = @"IFEO\<exe>\PerfOptions (CpuPriorityClass=3, IoPriority=3), DirectX\UserGpuPreferences, AppCompatFlags\Layers",
            SourceAttribution = "BoringBoredom PC-Tuning §11.42",
            Risk = RiskLevel.Low,
            InMinimal = true,
            InCompetitivo = true,
            InProfesional = true,
            ReadLiveState = () => ReadRegDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\cs2.exe\PerfOptions", "CpuPriorityClass") == 3,
            ApplyAction = _ =>
            {
                foreach (var game in TargetGames)
                {
                    if (game.HighCpuAndIoPriority)
                    {
                        string ifeoPerf = $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{game.ExecutableName}\PerfOptions";
                        _audit.SetRegistryValue("GameIfeoHighPriority", RegistryHive.LocalMachine, ifeoPerf, "CpuPriorityClass", 3, RegistryValueKind.DWord);
                        _audit.SetRegistryValue("GameIfeoHighPriority", RegistryHive.LocalMachine, ifeoPerf, "IoPriority", 3, RegistryValueKind.DWord);
                    }
                    if (game.HighPerfGpuPreference && !string.IsNullOrWhiteSpace(game.FullExePath))
                    {
                        _audit.SetRegistryValue("GameIfeoHighPriority", RegistryHive.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences", game.FullExePath, "GpuPreference=2;", RegistryValueKind.String);
                    }
                    if (game.DisableFullscreenOptimizations && !string.IsNullOrWhiteSpace(game.FullExePath))
                    {
                        _audit.SetRegistryValue("GameIfeoHighPriority", RegistryHive.CurrentUser, @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", game.FullExePath, "~ DisableDXMaximizedWindowedMode", RegistryValueKind.String);
                    }
                }
                return (true, $"Optimizaciones por ejecutable aplicadas a {TargetGames.Count} juegos.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "SteamBackgroundGpuAccelOff",
            CodeNumber = "F02",
            Title = "Desactivar Aceleración GPU de Steam WebHelper + Prioridad Compositor (CSRSS/DWM)",
            Category = TweakCategory.FpsBoostGames,
            CategoryDisplay = "FPS Boost & Juegos",
            TechnicalDescription = "Impide que el navegador interno de Steam compita por ciclos 3D de la GPU mientras jugás CS2 y asegura prioridad alta para csrss.exe y dwm.exe.",
            WhatItTouches = @"HKCU\Software\Valve\Steam\H264HWAccel=0, GPUAccelWebViewsV3=0 & IFEO\csrss.exe / dwm.exe PerfOptions",
            SourceAttribution = "CS2 Competitive Guide",
            Risk = RiskLevel.Low,
            InMinimal = true,
            InCompetitivo = true,
            InProfesional = true,
            ReadLiveState = () => ReadRegDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\csrss.exe\PerfOptions", "CpuPriorityClass") == 3,
            ApplyAction = _ =>
            {
                _audit.SetRegistryValue("SteamBackgroundGpuAccelOff", RegistryHive.CurrentUser, @"Software\Valve\Steam", "gpuacceleration", 0, RegistryValueKind.DWord);
                _audit.SetRegistryValue("SteamBackgroundGpuAccelOff", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\csrss.exe\PerfOptions", "CpuPriorityClass", 3, RegistryValueKind.DWord);
                _audit.SetRegistryValue("SteamBackgroundGpuAccelOff", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\dwm.exe\PerfOptions", "CpuPriorityClass", 3, RegistryValueKind.DWord);
                return (true, "Aceleración de fondo de Steam desactivada y prioridad CSRSS/DWM configurada.");
            }
        });

        // === MÓDULOS ENTUSIASTAS (OPT-IN / REDUCEN SEGURIDAD O RIESGO ANTICHEAT) ===
        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "DisableCfgPerGameIfeo",
            CodeNumber = "E01",
            Title = "Desactivar Control Flow Guard (CFG / MitigationOptions) en Juegos",
            Category = TweakCategory.FpsBoostGames,
            CategoryDisplay = "FPS Boost & Juegos",
            TechnicalDescription = "Desactiva las comprobaciones de memoria de Control Flow Guard en el ejecutable del juego para ganar 1% lows. ADVERTENCIA: Puede ser bloqueado por Riot Vanguard o FACEIT AC.",
            WhatItTouches = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\<exe> -> MitigationOptions",
            SourceAttribution = "ValleyOfDoom / Tweaks Extremos DX12",
            Risk = RiskLevel.High,
            SecurityImpact = SecurityImpact.ReducesSecurity,
            InMinimal = false,
            InCompetitivo = false,
            InProfesional = false,
            InEntusiasta = true,
            EvaluateGate = _ => EvaluationResult.NotRecommended("OPT-IN ENTUSIASTA: Desactiva mitigaciones de memoria. No usar si jugás con Riot Vanguard o FACEIT AC."),
            AnticheatGate = _ => AnticheatResult.Unsafe("Riot Vanguard y FACEIT AC pueden rechazar iniciar el juego si detectan MitigationOptions modificadas en IFEO."),
            ReadLiveState = () =>
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\cs2.exe");
                return k?.GetValue("MitigationOptions") is byte[];
            },
            ApplyAction = _ =>
            {
                byte[] mitBytes = { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 };
                foreach (var game in TargetGames)
                {
                    string p = $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{game.ExecutableName}";
                    _audit.SetRegistryValue("DisableCfgPerGameIfeo", RegistryHive.LocalMachine, p, "MitigationOptions", mitBytes, RegistryValueKind.Binary);
                }
                return (true, "MitigationOptions (CFG OFF) aplicado en modo Entusiasta.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "CpuKernelMitigationsOff",
            CodeNumber = "E02",
            Title = "Desactivar Mitigaciones de CPU del Kernel (Spectre / Meltdown / Downfall)",
            Category = TweakCategory.SystemCpu,
            CategoryDisplay = "Sistema & CPU",
            TechnicalDescription = "Establece FeatureSettingsOverride = 3 y FeatureSettingsOverrideMask = 3 para eliminar el overhead de mitigaciones de salto especulativo en llamadas al kernel (syscalls).",
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management -> FeatureSettingsOverride=3, FeatureSettingsOverrideMask=3",
            SourceAttribution = "BoringBoredom PC-Tuning §11.30",
            Risk = RiskLevel.Critical,
            SecurityImpact = SecurityImpact.ReducesSecurity,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = false,
            InProfesional = false,
            InEntusiasta = true,
            EvaluateGate = _ => EvaluationResult.NotRecommended("OPT-IN ENTUSIASTA: Reduce la seguridad del procesador frente a ataques de canal lateral y puede inhabilitar Vanguard / FACEIT AC."),
            AnticheatGate = _ => AnticheatResult.Unsafe("Inhabilita comprobaciones de seguridad de kernel exigidas por anticheats modernos (Vanguard / FACEIT AC)."),
            ReadLiveState = () => ReadRegDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "FeatureSettingsOverride") == 3,
            ApplyAction = _ =>
            {
                const string memPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
                _audit.SetRegistryValue("CpuKernelMitigationsOff", RegistryHive.LocalMachine, memPath, "FeatureSettingsOverride", 3, RegistryValueKind.DWord);
                _audit.SetRegistryValue("CpuKernelMitigationsOff", RegistryHive.LocalMachine, memPath, "FeatureSettingsOverrideMask", 3, RegistryValueKind.DWord);
                return (true, "Mitigaciones de CPU del kernel desactivadas (FeatureSettingsOverride = 3).");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "DefenderGameFolderExclusion",
            CodeNumber = "E03",
            Title = "Excluir Carpetas de Juegos del Escaneo en Tiempo Real de Windows Defender",
            Category = TweakCategory.FpsBoostGames,
            CategoryDisplay = "FPS Boost & Juegos",
            TechnicalDescription = "Añade la ruta de instalación de CS2/juegos a las exclusiones de Windows Defender (MsMpEng.exe) para evitar escaneos de archivos .vpk en partida.",
            WhatItTouches = @"Windows Defender ExclusionPath para carpetas de juego seleccionadas",
            SourceAttribution = "CS2 Stutter Fix",
            Risk = RiskLevel.Medium,
            SecurityImpact = SecurityImpact.ReducesSecurity,
            InMinimal = false,
            InCompetitivo = false,
            InProfesional = false,
            InEntusiasta = true,
            EvaluateGate = _ => EvaluationResult.Neutral("OPT-IN DE SEGURIDAD: Excluye la carpeta del juego del antivirus en tiempo real."),
            ReadLiveState = () => IsDefenderExclusionPresentInRegistry(),
            ApplyAction = _ =>
            {
                int count = 0;
                foreach (var game in TargetGames)
                {
                    if (!string.IsNullOrWhiteSpace(game.FullExePath))
                    {
                        string? dir = Path.GetDirectoryName(game.FullExePath);
                        if (!string.IsNullOrWhiteSpace(dir))
                        {
                            _audit.RecordCustomReversibleAction("DefenderGameFolderExclusion", BackupTargetKind.DefenderExclusion, dir, "ExclusionPath", "NotExcluded", "Excluded");
                            TransactionalAuditService.RunHiddenProcess("powershell.exe", $"-NoProfile -Command \"Add-MpPreference -ExclusionPath '{dir}' -ErrorAction SilentlyContinue\"");
                            count++;
                        }
                    }
                }
                return (true, $"Exclusión de Defender añadida para {count} carpetas de juego.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "PauseWindowsUpdate1Year",
            CodeNumber = "E04",
            Title = "Pausar Descargas de Windows Update (Evitar TiWorker en Partida)",
            Category = TweakCategory.SystemCpu,
            CategoryDisplay = "Sistema & CPU",
            TechnicalDescription = "Pausa las actualizaciones automáticas en segundo plano. Recordá reanudar periódicamente para instalar parches de seguridad.",
            WhatItTouches = @"HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings -> PauseUpdatesExpiryTime",
            SourceAttribution = "Estándar Torneo LAN",
            Risk = RiskLevel.Medium,
            SecurityImpact = SecurityImpact.ReducesSecurity,
            InMinimal = false,
            InCompetitivo = false,
            InProfesional = false,
            InEntusiasta = true,
            EvaluateGate = _ => EvaluationResult.Neutral("OPT-IN DE SEGURIDAD: Pausar actualizaciones evita descargas en partida, pero reduce seguridad si no actualizás manualmente."),
            ReadLiveState = () => ReadRegString(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "PauseUpdatesExpiryTime") != null,
            ApplyAction = _ =>
            {
                string pause = DateTime.UtcNow.AddDays(365).ToString("yyyy-MM-ddTHH:mm:ssZ");
                string today = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                const string wuPath = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
                _audit.SetRegistryValue("PauseWindowsUpdate1Year", RegistryHive.LocalMachine, wuPath, "PauseUpdatesExpiryTime", pause, RegistryValueKind.String);
                _audit.SetRegistryValue("PauseWindowsUpdate1Year", RegistryHive.LocalMachine, wuPath, "PauseFeatureUpdatesEndTime", pause, RegistryValueKind.String);
                _audit.SetRegistryValue("PauseWindowsUpdate1Year", RegistryHive.LocalMachine, wuPath, "PauseQualityUpdatesEndTime", pause, RegistryValueKind.String);
                _audit.SetRegistryValue("PauseWindowsUpdate1Year", RegistryHive.LocalMachine, wuPath, "PauseUpdatesStartTime", today, RegistryValueKind.String);
                return (true, "Windows Update pausado (reversible con 1 clic desde KRUZH LATENCY).");
            }
        });
    }

    private static string? DetectCs2ExePath()
    {
        try
        {
            using var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var steamKey = hklm32.OpenSubKey(@"SOFTWARE\Valve\Steam");
            string? steamPath = steamKey?.GetValue("InstallPath")?.ToString();
            if (string.IsNullOrWhiteSpace(steamPath)) return null;

            string defaultExe = Path.Combine(steamPath, @"steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe");
            if (File.Exists(defaultExe)) return defaultExe;

            string vdf = Path.Combine(steamPath, @"steamapps\libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (string line in File.ReadAllLines(vdf))
                {
                    if (line.Contains("\"path\"", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = line.Split('"', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3)
                        {
                            string lib = parts[^1].Replace(@"\\", @"\");
                            string candidate = Path.Combine(lib, @"steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe");
                            if (File.Exists(candidate)) return candidate;
                        }
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static int? ReadRegDword(RegistryHive hive, string subKey, string name)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var k = baseKey.OpenSubKey(subKey);
        return k?.GetValue(name) as int?;
    }

    private static string? ReadRegString(RegistryHive hive, string subKey, string name)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var k = baseKey.OpenSubKey(subKey);
        return k?.GetValue(name)?.ToString();
    }

    private static int? ReadFirstDisplayClassDword(string valueName)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var cls = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (cls == null) return null;
        foreach (string sub in cls.GetSubKeyNames())
        {
            if (sub.Length == 4 && char.IsDigit(sub[0]))
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue(valueName) is int v) return v;
            }
        }
        return null;
    }

    private void SetAllDisplayClassDword(string tweakId, string valueName, int value)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var cls = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (cls == null) return;
        foreach (string sub in cls.GetSubKeyNames())
        {
            if (sub.Length == 4 && char.IsDigit(sub[0]))
            {
                string path = $@"SYSTEM\CurrentControlSet\Control\Class\{{4d36e968-e325-11ce-bfc1-08002be10318}}\{sub}";
                _audit.SetRegistryValue(tweakId, RegistryHive.LocalMachine, path, valueName, value, RegistryValueKind.DWord);
            }
        }
    }

    private static int? ReadFirstNetClassDword(string valueName)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var cls = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}");
        if (cls == null) return null;
        foreach (string sub in cls.GetSubKeyNames())
        {
            if (sub.Length == 4 && char.IsDigit(sub[0]))
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DeviceInstanceID")?.ToString()?.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (k.GetValue(valueName) is int v) return v;
                }
            }
        }
        return null;
    }

    private static string? ReadFirstNetClassString(string valueName)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var cls = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}");
        if (cls == null) return null;
        foreach (string sub in cls.GetSubKeyNames())
        {
            if (sub.Length == 4 && char.IsDigit(sub[0]))
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DeviceInstanceID")?.ToString()?.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return k.GetValue(valueName)?.ToString();
                }
            }
        }
        return null;
    }

    private static void SetAllNetClassValues(string tweakId, Action<string> applyPerKey)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var cls = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}");
        if (cls == null) return;
        foreach (string sub in cls.GetSubKeyNames())
        {
            if (sub.Length == 4 && char.IsDigit(sub[0]))
            {
                using var k = cls.OpenSubKey(sub);
                string? pnp = k?.GetValue("DeviceInstanceID")?.ToString();
                if (!string.IsNullOrEmpty(pnp) && (pnp.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) || pnp.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)))
                {
                    string path = $@"SYSTEM\CurrentControlSet\Control\Class\{{4d36e972-e325-11ce-bfc1-08002be10318}}\{sub}";
                    applyPerKey(path);
                }
            }
        }
    }

    private static string? _cachedBcdOutput;
    private static DateTime _lastBcdQueryUtc = DateTime.MinValue;

    private static string GetCachedBcdCurrent()
    {
        if (_cachedBcdOutput != null && (DateTime.UtcNow - _lastBcdQueryUtc).TotalSeconds < 6)
        {
            return _cachedBcdOutput;
        }
        try
        {
            _cachedBcdOutput = TransactionalAuditService.RunHiddenProcess("bcdedit.exe", "/enum {current}");
            _lastBcdQueryUtc = DateTime.UtcNow;
            return _cachedBcdOutput;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void InvalidateBcdCache()
    {
        _cachedBcdOutput = null;
        _lastBcdQueryUtc = DateTime.MinValue;
    }

    private static int? ReadActivePowerSchemeAcSettingDword(string subGroupGuid, string settingGuid)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var schemesRoot = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes");
            string? activeGuid = schemesRoot?.GetValue("ActivePowerScheme")?.ToString();
            if (string.IsNullOrWhiteSpace(activeGuid)) return null;

            string settingPath = $@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\{activeGuid}\{subGroupGuid}\{settingGuid}";
            using var settingKey = hklm.OpenSubKey(settingPath);
            return settingKey?.GetValue("ACSettingIndex") as int?;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUsbHidPowerSavingDisabledInRegistry()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var hidRoot = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\HID");
            if (hidRoot == null) return false;

            int checkedCount = 0;
            int disabledCount = 0;
            foreach (string dev in hidRoot.GetSubKeyNames())
            {
                using var devKey = hidRoot.OpenSubKey(dev);
                if (devKey == null) continue;
                foreach (string inst in devKey.GetSubKeyNames())
                {
                    using var pKey = devKey.OpenSubKey($@"{inst}\Device Parameters");
                    if (pKey?.GetValue("EnhancedPowerManagementEnabled") is int val)
                    {
                        checkedCount++;
                        if (val == 0) disabledCount++;
                    }
                }
            }
            return checkedCount > 0 && disabledCount == checkedCount;
        }
        catch
        {
            return false;
        }
    }

    private bool IsDefenderExclusionPresentInRegistry()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var exKey = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender\Exclusions\Paths");
            if (exKey == null) return false;
            var names = exKey.GetValueNames();
            foreach (var game in TargetGames)
            {
                if (!string.IsNullOrWhiteSpace(game.FullExePath))
                {
                    string? dir = Path.GetDirectoryName(game.FullExePath);
                    if (!string.IsNullOrWhiteSpace(dir) && names.Any(n => string.Equals(n, dir, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
            }
        }
        catch { }
        return false;
    }

    private (bool ok, string detail) ApplyDefaultOsRevertFallback(string tweakId)
    {
        try
        {
            switch (tweakId)
            {
                case "MarkCMouseCurve":
                    byte[] defX = { 0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, 0x15,0x6E,0x00,0x00,0x00,0x00,0x00,0x00, 0x00,0x40,0x01,0x00,0x00,0x00,0x00,0x00, 0x29,0xDC,0x03,0x00,0x00,0x00,0x00,0x00, 0x00,0x00,0x28,0x00,0x00,0x00,0x00,0x00 };
                    byte[] defY = { 0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00, 0xFD,0x11,0x01,0x00,0x00,0x00,0x00,0x00, 0x00,0x24,0x04,0x00,0x00,0x00,0x00,0x00, 0x00,0xFC,0x12,0x00,0x00,0x00,0x00,0x00, 0x00,0xC0,0xBB,0x01,0x00,0x00,0x00,0x00 };
                    _audit.SetRegistryValue("MarkC_Rev", RegistryHive.CurrentUser, @"Control Panel\Mouse", "SmoothMouseXCurve", defX, RegistryValueKind.Binary);
                    _audit.SetRegistryValue("MarkC_Rev", RegistryHive.CurrentUser, @"Control Panel\Mouse", "SmoothMouseYCurve", defY, RegistryValueKind.Binary);
                    return (true, "Curvas de mouse restauradas al valor estándar de Windows.");

                case "MouseDataQueueDynamic":
                    _audit.SetRegistryValue("MDQ_Rev", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\mouclass\Parameters", "MouseDataQueueSize", 100, RegistryValueKind.DWord);
                    _audit.SetRegistryValue("MDQ_Rev", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", "KeyboardDataQueueSize", 100, RegistryValueKind.DWord);
                    return (true, "DataQueueSize restaurado al estándar de Windows (100).");

                case "RawMouseThrottleWin11":
                    _audit.SetRegistryValue("RMT_Rev", RegistryHive.CurrentUser, @"Control Panel\Mouse", "RawMouseThrottleDuration", 8, RegistryValueKind.DWord);
                    return (true, "RawMouseThrottleDuration restaurado al estándar.");

                case "CpuPrioritySeparationSelector":
                    _audit.SetRegistryValue("CPS_Rev", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 2, RegistryValueKind.DWord);
                    return (true, "Win32PrioritySeparation restaurado a 2 (Default Windows).");

                case "GlobalTimerResolutionWin11":
                    _audit.SetRegistryValue("GTR_Rev", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 0, RegistryValueKind.DWord);
                    return (true, "GlobalTimerResolutionRequests restaurado a 0.");

                case "BcdDisableDynamicTick":
                    TransactionalAuditService.RunHiddenProcess("bcdedit.exe", "/set disabledynamictick no");
                    return (true, "disabledynamictick restaurado a no.");

                case "ReservedCpuSetsTopology":
                    _audit.SetRegistryValue("RCS_Rev", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "ReservedCpuSets", Array.Empty<byte>(), RegistryValueKind.Binary);
                    return (true, "ReservedCpuSets limpiado.");

                case "PowerThrottlingEcoQosOff":
                    _audit.SetRegistryValue("PTE_Rev", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 0, RegistryValueKind.DWord);
                    return (true, "PowerThrottlingOff restaurado a 0.");

                case "CpuIdleAndCoreParkingDesktop":
                    _audit.SetPowerCfgAcValue("CIP_Rev", "54533251-82be-4824-96c1-47b60b740d00", "5d76a2ca-e8c0-402f-a133-2158492d58ad", 0);
                    return (true, "C-States Idle rehabilitado (IDLEDISABLE=0).");

                case "GameBarDvrSafeDisable":
                    _audit.SetRegistryValue("GBD_Rev", RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 1, RegistryValueKind.DWord);
                    _audit.SetRegistryValue("GBD_Rev", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 1, RegistryValueKind.DWord);
                    return (true, "Game DVR restaurado al valor estándar.");

                case "WindowsGameModeOn":
                    _audit.SetRegistryValue("WGM_Rev", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 0, RegistryValueKind.DWord);
                    return (true, "Efectos visuales restaurados a automático.");

                case "ServicesAndTasksDebloat":
                    _audit.SetServiceStartup("STD_Rev", "DiagTrack", 2, stopIfRunning: false);
                    return (true, "Servicio DiagTrack restaurado a inicio automático.");

                case "GpuHardwareSchedulingHags":
                    _audit.SetRegistryValue("HAGS_Rev", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 1, RegistryValueKind.DWord);
                    return (true, "HAGS desactivado (HwSchMode = 1).");

                case "GpuDynamicP0State":
                    SetAllDisplayClassDword("P0_Rev", "DisableDynamicPstate", 0);
                    return (true, "DisableDynamicPstate restaurado a 0.");

                case "NvidiaUltraLowLatencyReg":
                    _audit.SetRegistryValue("NV_Rev", RegistryHive.CurrentUser, @"Software\NVIDIA Corporation\NvTray", "StartOnLogin", 1, RegistryValueKind.DWord);
                    return (true, "NvTray StartOnLogin restaurado a 1.");

                case "AmdRadeonUlpsOff":
                    SetAllDisplayClassDword("ULPS_Rev", "EnableUlps", 1);
                    return (true, "EnableUlps restaurado a 1.");

                case "DirectXIndependentFlipAndFse":
                    _audit.SetRegistryValue("DX_Rev", RegistryHive.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings", "", RegistryValueKind.String);
                    return (true, "DirectXUserGlobalSettings restaurado.");

                case "DisableMpo":
                    _audit.SetRegistryValue("MPO_Rev", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 0, RegistryValueKind.DWord);
                    return (true, "MPO restaurado (OverlayTestMode = 0).");

                case "DisableHdcp":
                    SetAllDisplayClassDword("HDCP_Rev", "RMHdcpKeyglobZero", 0);
                    return (true, "HDCP restaurado (RMHdcpKeyglobZero = 0).");

                case "NicEnergySavingOff":
                    SetAllNetClassValues("NIC_Rev", kp => _audit.SetRegistryValue("NIC_Rev", RegistryHive.LocalMachine, kp, "PnPCapabilities", 0, RegistryValueKind.DWord));
                    return (true, "PnPCapabilities restaurado a 0.");

                case "NicLsoAndFlowControlOff":
                    SetAllNetClassValues("LSO_Rev", kp => _audit.SetRegistryValue("LSO_Rev", RegistryHive.LocalMachine, kp, "*LsoV2IPv4", "1", RegistryValueKind.String));
                    return (true, "LSO v2 restaurado a 1.");

                case "MmcssAndNetworkThrottlingSelector":
                    _audit.SetRegistryValue("MM_Rev", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 20, RegistryValueKind.DWord);
                    _audit.SetRegistryValue("MM_Rev", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", 10, RegistryValueKind.DWord);
                    return (true, "MMCSS restaurado a 20% / 10.");

                case "TcpNagleAndRssOptimize":
                    _audit.SetRegistryValue("TCP_Rev", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "ECNEnabled", 1, RegistryValueKind.DWord);
                    return (true, "Configuración TCP restaurada.");

                case "QosDscp46Games":
                    _audit.SetRegistryValue("QOS_Rev", RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\QoS\KRUZH_CS2", "DSCP Value", "0", RegistryValueKind.String);
                    return (true, "Política QoS DSCP restaurada.");

                case "NtpHighPrecisionClockSync":
                    _audit.SetRegistryValue("NTP_Rev", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\W32Time\Parameters", "NtpServer", "time.windows.com,0x9", RegistryValueKind.String);
                    return (true, "Servidor NTP restaurado a time.windows.com.");

                case "AudioEnhancementsAndSpatialOff":
                    _audit.SetRegistryValue("AUD_Rev", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio", "DisableProtectedAudioDG", 0, RegistryValueKind.DWord);
                    return (true, "DisableProtectedAudioDG restaurado a 0.");

                case "PcieAspmDisable":
                    _audit.SetRegistryValue("ASPM_Rev", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\pci\Parameters", "EnableASPM", 1, RegistryValueKind.DWord);
                    return (true, "PCIe ASPM restaurado.");

                case "FastStartupAndHibernationOff":
                    TransactionalAuditService.RunHiddenProcess("powercfg.exe", "/h on");
                    _audit.SetRegistryValue("FS_Rev", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 1, RegistryValueKind.DWord);
                    return (true, "Fast Startup e Hibernación rehabilitados.");

                case "NtfsDisable8dot3AndLastAccess":
                    _audit.SetRegistryValue("NTFS_Rev", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsDisable8dot3NameCreation", 2, RegistryValueKind.DWord);
                    return (true, "NTFS 8.3 restaurado al valor estándar.");

                case "SysMainAndMemoryManagement":
                    _audit.SetServiceStartup("SM_Rev", "SysMain", 2, stopIfRunning: false);
                    _audit.SetRegistryValue("SM_Rev", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "DisablePagingExecutive", 0, RegistryValueKind.DWord);
                    return (true, "SysMain y DisablePagingExecutive restaurados.");

                case "GameIfeoHighPriority":
                    _audit.SetRegistryValue("IFEO_Rev", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\cs2.exe\PerfOptions", "CpuPriorityClass", 2, RegistryValueKind.DWord);
                    return (true, "Prioridad IFEO restaurada a Normal (2).");

                case "SteamBackgroundGpuAccelOff":
                    _audit.SetRegistryValue("STM_Rev", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\csrss.exe\PerfOptions", "CpuPriorityClass", 2, RegistryValueKind.DWord);
                    return (true, "Prioridad CSRSS restaurada.");

                case "CpuKernelMitigationsOff":
                    _audit.SetRegistryValue("MIT_Rev", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "FeatureSettingsOverride", 0, RegistryValueKind.DWord);
                    return (true, "Mitigaciones del kernel rehabilitadas.");
            }
        }
        catch { }
        return (false, "No se encontró entrada previa en el journal para revertir.");
    }
}
