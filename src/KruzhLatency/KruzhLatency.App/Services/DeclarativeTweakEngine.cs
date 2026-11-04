using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using CommunityToolkit.Mvvm.ComponentModel;
using KruzhLatency.Core.Abstractions;
using KruzhLatency.Core.Devices;
using KruzhLatency.Infra.Devices;
using KruzhLatency.Infra.Network;
using KruzhLatency.Infra.Wmi;
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

    /// <summary>Desde qué perfil se aplica este ítem (se muestra chico en la fila).</summary>
    public string ProfileHintText =>
        InMinimal ? "Desde Normal"
        : InCompetitivo ? "Desde Competitivo"
        : InProfesional ? "Desde Profesional"
        : InEntusiasta ? "Solo Entusiasta"
        : "Manual";

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
        "AmdRadeonUlpsOff" => "\uE708",
        "DirectXIndependentFlipAndFse" => "\uE740",
        "DisableMpo" => "\uE81E",
        "DisableHdcp" => "\uE72E",
        "NicLsoAndFlowControlOff" => "\uE72A",
        "MmcssAndNetworkThrottlingSelector" => "\uE9F9",
        "TcpNagleAndRssOptimize" => "\uE968",
        "QosDscp46Games" => "\uE8CB",
        "NtpHighPrecisionClockSync" => "\uE774",
        "PciMsiModeV3" => "\uE964",
        "PciIrqSmartBalancer" => "\uE71D",
        "UsbHidPowerSavingOff" => "\uE88E",
        "PcieAspmDisable" => "\uE950",
        "FastStartupAndHibernationOff" => "\uE7E8",
        "NtfsDisable8dot3AndLastAccess" => "\uEDA2",
        "SysMainAndMemoryManagement" => "\uE958",
        "GameIfeoHighPriority" => "\uE74A",
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

    /// <summary>Resultado de leer el estado real de un ítem (gates + estado vivo). Se arma fuera del hilo de UI.</summary>
    public sealed record StateProbe(ApplicabilityResult Applicability, EvaluationResult Evaluation, AnticheatResult Anticheat,
        bool LiveApplied, string? LiveOptionKey, bool LiveReadFailed);

    /// <summary>
    /// Lee el estado real (registro, WMI, dispositivos…). No toca propiedades enlazadas a la UI: se puede llamar desde
    /// un hilo de fondo. Un ítem que no puede leer su estado queda "no aplicable" con el motivo, nunca tira abajo el resto.
    /// </summary>
    public StateProbe Probe(HardwareProfile hw)
    {
        ApplicabilityResult applicability;
        EvaluationResult evaluation = EvaluationResult.Recommended("");
        AnticheatResult anticheat = AnticheatResult.Safe();
        try
        {
            applicability = ApplicableGate(hw);
            evaluation = EvaluateGate(hw);
            anticheat = AnticheatGate(hw);
        }
        catch (Exception ex)
        {
            App.LogError($"Gates {Id}", ex);
            applicability = ApplicabilityResult.NotApplicable($"No se pudo leer el estado en esta PC ({ex.GetType().Name}).");
        }

        try
        {
            bool live = applicability.IsApplicable && ReadLiveState();
            string? liveKey = HasOptions ? (ReadLiveOptionKey?.Invoke() ?? Options.FirstOrDefault()?.Key ?? string.Empty) : null;
            return new StateProbe(applicability, evaluation, anticheat, live, liveKey, false);
        }
        catch (Exception ex)
        {
            App.LogError($"Estado {Id}", ex);
            return new StateProbe(applicability, evaluation, anticheat, false, null, true);
        }
    }

    /// <summary>Lectura + aplicación en el mismo hilo (para un ítem suelto después de aplicarlo/revertirlo).</summary>
    public void RefreshGatesAndLiveState(HardwareProfile hw) => ApplyProbe(Probe(hw));

    /// <summary>Vuelca una lectura en las propiedades enlazadas. Solo en el hilo de UI.</summary>
    public void ApplyProbe(StateProbe probe)
    {
        Applicability = probe.Applicability;
        Evaluation = probe.Evaluation;
        Anticheat = probe.Anticheat;
        try
        {
            if (probe.LiveReadFailed) throw new InvalidOperationException("lectura fallida");
            IsLiveApplied = probe.LiveApplied;
            if (HasOptions)
            {
                string liveKey = probe.LiveOptionKey ?? Options.FirstOrDefault()?.Key ?? string.Empty;
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

public sealed partial class DeclarativeTweakEngine
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
        BuildServiceAndDeviceCatalog();
        BuildDebloatItems(); // T41: pestaña Apps
        RefreshAll();
    }

    /// <summary>Spec 001: el producto es para CS2 y VALORANT. Otros .exe solo si el usuario los agrega a mano.</summary>
    private void InitializeDefaultGames()
    {
        string? cs2Path = DetectCs2ExePath();
        TargetGames.Add(new GameTargetItem
        {
            Name = "Counter-Strike 2",
            ShortName = "CS2",
            ExecutableName = "cs2.exe",
            FullExePath = cs2Path ?? string.Empty,
            CoverImagePath = FindSteamCover("730"),
            GradientStartHex = "#3A2E17",
            GradientEndHex = "#14120C"
        });

        TargetGames.Add(new GameTargetItem
        {
            Name = "VALORANT",
            ShortName = "VALORANT",
            ExecutableName = "VALORANT-Win64-Shipping.exe",
            FullExePath = DetectValorantExePath() ?? string.Empty,
            GradientStartHex = "#2A1A2E",
            GradientEndHex = "#120D14"
        });
    }

    /// <summary>
    /// Ubica VALORANT por la entrada de desinstalación que crea el Riot Client ("Riot Game valorant.live"),
    /// con fallback a la ruta por defecto. Solo lee el registro; no toca archivos del juego.
    /// </summary>
    private static string? DetectValorantExePath()
    {
        const string relativeExe = @"ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe";
        try
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var k = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Riot Game valorant.live");
                string? location = k?.GetValue("InstallLocation")?.ToString();
                if (!string.IsNullOrWhiteSpace(location))
                {
                    string candidate = Path.Combine(location, relativeExe);
                    if (File.Exists(candidate)) return candidate;
                }
            }

            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                string candidate = Path.Combine(drive.RootDirectory.FullName, @"Riot Games\VALORANT\live", relativeExe);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { }
        return null;
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

    /// <summary>Lectura completa en el hilo actual. Solo para arranque (splash, ya en segundo plano) y casos puntuales.</summary>
    public void RefreshAll()
    {
        var hw = _hwService.CurrentProfile;
        foreach (var tweak in _allTweaks)
        {
            tweak.RefreshGatesAndLiveState(hw);
        }
    }

    private Task? _refreshTask;
    private bool _refreshAgain;

    /// <summary>
    /// Relee el estado de todos los ítems en segundo plano y lo vuelca en la UI de una vez (la UI nunca se congela).
    /// Si ya hay una lectura en curso no se apilan: se hace una más al terminar y todos esperan esa.
    /// </summary>
    public Task RefreshAllAsync(Microsoft.UI.Dispatching.DispatcherQueue ui, Action<Action>? applyWrapper = null)
    {
        if (_refreshTask is { IsCompleted: false })
        {
            _refreshAgain = true;
            return _refreshTask;
        }
        _refreshTask = RunRefreshLoopAsync(ui, applyWrapper ?? (a => a()));
        return _refreshTask;
    }

    private async Task RunRefreshLoopAsync(Microsoft.UI.Dispatching.DispatcherQueue ui, Action<Action> applyWrapper)
    {
        do
        {
            _refreshAgain = false;
            var hw = _hwService.CurrentProfile;
            var items = _allTweaks.ToList();
            var probes = await Task.Run(() => items.Select(t => t.Probe(hw)).ToList());

            var applied = new TaskCompletionSource();
            if (!ui.TryEnqueue(() =>
                {
                    try
                    {
                        applyWrapper(() =>
                        {
                            for (int i = 0; i < items.Count; i++) items[i].ApplyProbe(probes[i]);
                        });
                    }
                    finally
                    {
                        applied.SetResult();
                    }
                }))
            {
                return; // la ventana se cerró
            }
            await applied.Task;
        }
        while (_refreshAgain);
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
                        "CpuPrioritySeparationSelector" => "38",
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
        finally
        {
            InvalidateReadCaches(); // la relectura de verificación tiene que ver el estado nuevo
        }
    }

    /// <summary>Paso 3 (UI). Re-lee el estado real y arma el reporte.</summary>
    public TweakExecutionReport CompleteApply(DeclarativeTweakItem item, bool ok, string detail)
    {
        bool verified = false;
        try
        {
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

    /// <summary>
    /// Paso 2 del revert (background). Solo desde el journal: se restaura el valor original exacto que se guardó.
    /// Sin backup no se escribe nada (T31: se quitaron los fallbacks que adivinaban "valores de Windows" con ids "*_Rev",
    /// bug B2). Un ítem puede explicar por qué no se revierte (ej. fantasmas borrados). Nunca lanza.
    /// </summary>
    public (bool Success, string Detail) RunRevertActions(DeclarativeTweakItem item)
    {
        try
        {
            bool reverted = _audit.RevertTweakFromJournal(item.Id, out string msg);
            if (!reverted && !_audit.HasBackupForTweak(item.Id))
            {
                msg = item.CustomRevertFallback is { } explain
                    ? explain().Detail
                    : "No hay valores originales guardados de este ajuste (no lo aplicó KRUZH o ya se revirtió): no se cambió nada.";
            }
            return (reverted, msg);
        }
        catch (Exception ex)
        {
            App.LogError($"Revert {item.Id}", ex);
            return (false, $"Error al revertir: {ex.Message}");
        }
        finally
        {
            InvalidateReadCaches();
        }
    }

    /// <summary>Paso 3 del revert (UI).</summary>
    public TweakExecutionReport CompleteRevert(DeclarativeTweakItem item, bool ok, string detail)
    {
        try
        {
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
            TechnicalDescription = "En Windows 11 un pedido de timer fino (ej. 0,5 ms) solo vale para la ventana del programa que lo pide; con este valor vuelve a aplicarse a todo el sistema, como en Windows 10. Lo necesita el modo residente (C16).",
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel -> GlobalTimerResolutionRequests = 1",
            SourceAttribution = "Microsoft Learn: timeBeginPeriod (cambios de Windows 11) / PC-Tuning §11.50",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = true,
            InCompetitivo = true,
            InProfesional = true,
            ApplicableGate = hw => hw.IsWindows11
                ? ApplicabilityResult.Applicable("Windows 11")
                : ApplicabilityResult.NotApplicable("En Windows 10 los pedidos de timer ya son globales."),
            ReadLiveState = () => ReadRegDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests") == 1,
            ApplyAction = _ =>
            {
                _audit.SetRegistryValue("GlobalTimerResolutionWin11", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 1, RegistryValueKind.DWord);
                return (true, "GlobalTimerResolutionRequests = 1 aplicado.");
            }
        });
        BuildTimerItems(); // E12 / ADR-010

        // ADR-008: en Windows 11 ningún perfil toca useplatformtick / disabledynamictick. C03 solo deja el reloj por defecto.
        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "BcdDisableDynamicTick", // Id histórico: conserva el journal de versiones anteriores
            CodeNumber = "C03",
            Title = "Reloj de Windows por defecto (sin HPET forzado)",
            Category = TweakCategory.SystemCpu,
            CategoryDisplay = "Sistema & CPU",
            TechnicalDescription = "Borra useplatformclock (fuerza HPET como contador de rendimiento: más lento que el TSC). " +
                                   "Si otra herramienta dejó useplatformtick o disabledynamictick, también los borra (en Windows 11 no hay evidencia de mejora y hay reportes de timer trabado), salvo que los hayas aplicado con el experimental de KRUZH.",
            WhatItTouches = @"BCD {current} (WMI BcdStore): borra useplatformclock; borra useplatformtick y disabledynamictick si no los puso C11",
            SourceAttribution = "Microsoft Learn: BcdOSLoaderElementTypes (UsePlatformClock: solo para depuración) · ADR-008",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = true,
            InCompetitivo = true,
            InProfesional = true,
            EvaluateGate = _ =>
            {
                var leftovers = new[] { BcdElement.UsePlatformClock, BcdElement.UsePlatformTick, BcdElement.DisableDynamicTick }
                    .Where(e => ReadBcd(e) is not null).Select(WmiBcdStore.BcdEditName).ToList();
                return leftovers.Count == 0
                    ? EvaluationResult.Recommended("Tu BCD ya usa el reloj por defecto.")
                    : EvaluationResult.Recommended($"Encontrado en tu BCD: {string.Join(", ", leftovers)} (lo dejó otra herramienta o un tweak anterior).");
            },
            ReadLiveState = () => ReadBcd(BcdElement.UsePlatformClock) is null &&
                                  (_audit.HasBackupForTweak("BcdExperimentalTick") ||
                                   (ReadBcd(BcdElement.UsePlatformTick) is null && ReadBcd(BcdElement.DisableDynamicTick) is null)),
            ApplyAction = _ =>
            {
                _audit.SetBcdValue("BcdDisableDynamicTick", "useplatformclock", null);
                if (!_audit.HasBackupForTweak("BcdExperimentalTick"))
                {
                    _audit.SetBcdValue("BcdDisableDynamicTick", "useplatformtick", null);
                    _audit.SetBcdValue("BcdDisableDynamicTick", "disabledynamictick", null);
                }
                return (true, "Reloj por defecto: useplatformclock borrado (y banderas de tick de otras herramientas).");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "BcdExperimentalTick",
            CodeNumber = "C11",
            Title = "Experimental: useplatformtick + disabledynamictick (Windows 11)",
            Category = TweakCategory.SystemCpu,
            CategoryDisplay = "Sistema & CPU",
            TechnicalDescription = "Tick del reloj desde la plataforma y sin tick dinámico. No hay documentación de Microsoft ni benchmarks controlados que muestren mejora en Windows 11, y hay reportes de timer trabado o desincronizado. " +
                                   "Medí DPC y frametime (Monitor) antes y después; si empeora, revertilo.",
            WhatItTouches = @"BCD {current} (WMI BcdStore): useplatformtick = yes, disabledynamictick = yes",
            SourceAttribution = "ADR-008 · Microsoft Learn: BCDEdit /set (useplatformtick, disabledynamictick: solo para depuración) · foros BlurBusters",
            Risk = RiskLevel.Medium,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = false,
            InProfesional = false,
            InEntusiasta = true,
            ApplicableGate = hw => hw.IsWindows11
                ? ApplicabilityResult.Applicable("Windows 11.")
                : ApplicabilityResult.NotApplicable("En Windows 10 usá \"Desactivar tick dinámico (Windows 10)\"."),
            EvaluateGate = _ => EvaluationResult.Neutral("Experimental: medí antes y después."),
            ReadLiveState = () => ReadBcd(BcdElement.UsePlatformTick)?.Boolean == true && ReadBcd(BcdElement.DisableDynamicTick)?.Boolean == true,
            ApplyAction = _ =>
            {
                _audit.SetBcdValue("BcdExperimentalTick", "useplatformtick", "yes");
                _audit.SetBcdValue("BcdExperimentalTick", "disabledynamictick", "yes");
                return (true, "useplatformtick y disabledynamictick activados. Reiniciá y medí.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "BcdDisableDynamicTickWin10",
            CodeNumber = "C12",
            Title = "Desactivar tick dinámico (Windows 10)",
            Category = TweakCategory.SystemCpu,
            CategoryDisplay = "Sistema & CPU",
            TechnicalDescription = "En Windows 10 el tick dinámico deja de interrumpir en reposo para ahorrar energía; desactivarlo mantiene el tick periódico constante. Fuera de perfiles: medí antes y después.",
            WhatItTouches = @"BCD {current} (WMI BcdStore): disabledynamictick = yes",
            SourceAttribution = "ADR-008 · Microsoft Learn: BCDEdit /set disabledynamictick",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = false,
            InProfesional = false,
            InEntusiasta = false,
            ApplicableGate = hw => hw.IsWindows11
                ? ApplicabilityResult.NotApplicable("En Windows 11 está en el experimental de Entusiasta (ADR-008).")
                : ApplicabilityResult.Applicable("Windows 10."),
            EvaluateGate = _ => EvaluationResult.Neutral("Opción manual: medí DPC antes y después."),
            ReadLiveState = () => ReadBcd(BcdElement.DisableDynamicTick)?.Boolean == true,
            ApplyAction = _ =>
            {
                _audit.SetBcdValue("BcdDisableDynamicTickWin10", "disabledynamictick", "yes");
                return (true, "disabledynamictick = yes. Reiniciá para aplicarlo.");
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
            WhatItTouches = @"powercfg scheme_current sub_processor: IDLEDISABLE=1, CPMINCORES=100",
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

        // Spec 002 / B7: estado mínimo del procesador al 100% (PROCTHROTTLEMIN) en el plan activo.
        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "ProcessorMinState100",
            CodeNumber = "C10",
            Title = "Estado mínimo del procesador al 100% (plan activo)",
            Category = TweakCategory.SystemCpu,
            CategoryDisplay = "Sistema & CPU",
            TechnicalDescription = "El CPU no baja de su frecuencia base mientras hay carga: evita la rampa de subida de frecuencia al empezar un frame o un tick. El boost (PBO / Turbo) sigue funcionando por encima de la base.",
            WhatItTouches = @"powercfg scheme_current sub_processor: PROCTHROTTLEMIN (893dee8e-2bef-41e0-89c6-b55d0929964c) = 100",
            SourceAttribution = "Microsoft Learn: Processor power management options (Minimum processor state) · spec 002",
            Risk = RiskLevel.Low,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            ApplicableGate = hw => hw.Chassis == ChassisKind.Laptop
                ? ApplicabilityResult.NotApplicable("En notebooks sube la temperatura y el consumo en reposo.")
                : ApplicabilityResult.Applicable("PC de escritorio."),
            EvaluateGate = _ => EvaluationResult.Recommended("Recomendado en PC de escritorio."),
            ReadLiveState = () => ReadActivePowerSchemeAcSettingDword("54533251-82be-4824-96c1-47b60b740d00", "893dee8e-2bef-41e0-89c6-b55d0929964c") == 100,
            ApplyAction = _ =>
            {
                _audit.SetPowerCfgAcValue("ProcessorMinState100", "54533251-82be-4824-96c1-47b60b740d00", "893dee8e-2bef-41e0-89c6-b55d0929964c", 100);
                return (true, "Estado mínimo del procesador = 100% en el plan activo.");
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
            Title = "Windows Game Mode ON + efectos visuales livianos",
            Category = TweakCategory.SystemCpu,
            CategoryDisplay = "Sistema & CPU",
            TechnicalDescription = "Activa AutoGameModeEnabled y configura los efectos visuales para priorizar rendimiento. (Copilot ahora está en la pestaña Apps.)",
            WhatItTouches = @"HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled=1, Explorer\VisualEffects\VisualFXSetting=2",
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
                _audit.SetRegistryValue("WindowsGameModeOn", RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 2, RegistryValueKind.DWord);
                return (true, "Game Mode ON y efectos visuales optimizados.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "ServicesAndTasksDebloat",
            CodeNumber = "C09",
            Title = "Tareas programadas de telemetría",
            Category = TweakCategory.Apps,
            CategoryDisplay = "Telemetría",
            TechnicalDescription = "Desactiva con backup las tareas programadas de telemetría y diagnóstico que se disparan en segundo plano (picos de CPU/disco en partida): " +
                                   string.Join("; ", KruzhLatency.Core.Debloat.TelemetryTasks.All.Select(t => $"{t.Path[(t.Path.LastIndexOf('\\') + 1)..]}: {t.Why}")) +
                                   ". Las que no existen en tu Windows se ignoran. Los servicios se manejan en la pestaña Servicios.",
            WhatItTouches = "Programador de tareas (API COM): " + string.Join(", ", KruzhLatency.Core.Debloat.TelemetryTasks.All.Select(t => t.Path)),
            SourceAttribution = "Microsoft Learn: Configure Windows diagnostic data · BoringBoredom PC-Tuning §11.14",
            Risk = RiskLevel.Low,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            ReadLiveState = () => KruzhLatency.Core.Debloat.TelemetryTasks.All
                .Select(t => KruzhLatency.Infra.Debloat.ScheduledTaskStore.IsEnabled(t.Path))
                .All(enabled => enabled != true),
            ApplyAction = _ =>
            {
                var results = KruzhLatency.Core.Debloat.TelemetryTasks.All
                    .Where(t => KruzhLatency.Infra.Debloat.ScheduledTaskStore.IsEnabled(t.Path) is not null)
                    .Select(t => _audit.SetScheduledTaskEnabled("ServicesAndTasksDebloat", t.Path, false))
                    .ToList();
                return results.All(ok => ok)
                    ? (true, $"{results.Count} tareas de telemetría desactivadas (reversibles).")
                    : (false, $"{results.Count(ok => ok)} de {results.Count} tareas desactivadas; Windows no dejó cambiar el resto.");
            }
        });

        BuildMaintenanceItems(); // E4

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
        BuildNvidiaRegistryItems(); // E10, E11

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

        // --- Independent Flip: el objetivo es que el juego presente por flip model y Windows lo promueva a
        //     Independent Flip (sin composición de DWM). Para eso hacen falta FSO activo y MPO activo:
        //     por eso este tweak NO desactiva FSO, y "Desactivar MPO" quedó fuera de los perfiles (G06).
        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "DirectXIndependentFlipAndFse",
            CodeNumber = "G05",
            Title = "Independent Flip: Optimizaciones para juegos en ventana (Flip Model) + FSO activo",
            Category = TweakCategory.GpuDisplay,
            CategoryDisplay = "GPU & Display",
            TechnicalDescription = "Activa 'Optimizaciones para juegos en ventana' (DX10/11 usan flip model en vez de blt) para que el juego pueda pasar a Independent Flip y saltear la composición de DWM. Mantiene las Fullscreen Optimizations activas: si un tweak anterior las había apagado (FSEBehavior=2 / HonorFSE=1), las vuelve a encender.",
            WhatItTouches = @"HKCU\Software\Microsoft\DirectX\UserGpuPreferences\DirectXUserGlobalSettings -> agrega SwapEffectUpgradeEnable=1 (respeta el resto) · HKCU\System\GameConfigStore -> GameDVR_FSEBehavior=0, GameDVR_DXGIHonorFSEWindowsCompatible=0",
            SourceAttribution = "Microsoft DirectX Dev Blog (Optimizations for windowed games / flip model) · PresentMon (Hardware: Independent Flip)",
            Risk = RiskLevel.Low,
            InMinimal = true,
            InCompetitivo = true,
            InProfesional = true,
            ApplicableGate = hw => hw.IsWindows11
                ? ApplicabilityResult.Applicable("Windows 11 detectado")
                : ApplicabilityResult.NotApplicable($"'Optimizaciones para juegos en ventana' es una función de Windows 11 (Build actual: {hw.WindowsBuild})."),
            EvaluateGate = _ => EvaluationResult.Recommended("Con FSO y MPO activos, CS2 y VALORANT en pantalla completa/ventana sin bordes pueden presentar en Independent Flip: misma latencia que fullscreen exclusivo, sin sus problemas de Alt-Tab."),
            ReadLiveState = () =>
            {
                string? s = ReadRegString(RegistryHive.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings");
                bool flipModel = s != null && s.Contains("SwapEffectUpgradeEnable=1", StringComparison.OrdinalIgnoreCase);
                bool fsoDisabled = ReadRegDword(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_FSEBehavior") == 2 ||
                                   ReadRegDword(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible") == 1;
                return flipModel && !fsoDisabled;
            },
            ApplyAction = _ =>
            {
                const string dxPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
                string current = ReadRegString(RegistryHive.CurrentUser, dxPath, "DirectXUserGlobalSettings") ?? string.Empty;
                _audit.SetRegistryValue("DirectXIndependentFlipAndFse", RegistryHive.CurrentUser, dxPath, "DirectXUserGlobalSettings",
                    MergeDirectXGlobalSetting(current, "SwapEffectUpgradeEnable", "1"), RegistryValueKind.String);

                // FSO activo (valores por defecto de Windows) — deshace lo que dejaba la versión anterior de este tweak.
                _audit.SetRegistryValue("DirectXIndependentFlipAndFse", RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_FSEBehavior", 0, RegistryValueKind.DWord);
                _audit.SetRegistryValue("DirectXIndependentFlipAndFse", RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible", 0, RegistryValueKind.DWord);
                return (true, "Flip model en ventana activado y Fullscreen Optimizations activas (camino a Independent Flip).");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "DisableMpo",
            CodeNumber = "G06",
            Title = "Desactivar MPO (solo para solucionar flickering / pantallas negras)",
            Category = TweakCategory.GpuDisplay,
            CategoryDisplay = "GPU & Display",
            TechnicalDescription = "Apaga los Multi-Plane Overlays de DWM. Úsalo SOLO si tenés flickering o pantallas negras con tu driver: sin MPO el juego no puede usar Independent Flip por hardware, así que en una PC sana empeora la latencia de presentación.",
            WhatItTouches = @"HKLM\SOFTWARE\Microsoft\Windows\Dwm -> OverlayTestMode = 5",
            SourceAttribution = "NVIDIA Support (MPO flicker workaround) · Microsoft Docs (Multiplane overlay support)",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = false,
            InProfesional = false,
            InEntusiasta = false, // ningún perfil lo activa: es una solución puntual de problemas, no una optimización
            ApplicableGate = hw => hw.WindowsBuild >= 26100
                ? ApplicabilityResult.NotApplicable("En Windows 11 24H2 o superior OverlayTestMode = 5 ya no desactiva MPO (ADR-008). Para problemas de MPO usá \"MPO estable\".")
                : ApplicabilityResult.Applicable($"Build {hw.WindowsBuild}: OverlayTestMode todavía funciona."),
            EvaluateGate = _ => EvaluationResult.NotRecommended("No recomendado: desactivar MPO impide Independent Flip por hardware. Solo para solucionar flickering o pantallas negras."),
            ReadLiveState = () => ReadRegDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode") == 5,
            ApplyAction = _ =>
            {
                _audit.SetRegistryValue("DisableMpo", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 5, RegistryValueKind.DWord);
                return (true, "MPO desactivado (OverlayTestMode = 5). Revertilo si no tenías flickering.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "DisableHdcp",
            CodeNumber = "G07",
            Title = "Desactivar Cifrado HDCP en Enlace de Video (Solo NVIDIA)",
            Category = TweakCategory.GpuDisplay,
            CategoryDisplay = "GPU & Display",
            TechnicalDescription = "Deshabilita el handshake de protección DRM HDCP en el enlace DisplayPort/HDMI del driver NVIDIA. Efecto reportado: leve y subjetivo (spec 002). Nota: impide reproducir Netflix/Prime Video/Disney+ con DRM en alta resolución.",
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\00XX (cada adaptador de video) -> RMHdcpKeyglobZero = 1",
            SourceAttribution = "Calypto Latency Guide · clave RMHdcpKeyglobZero de nvlddmkm (NVIDIA) · spec 002",
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

        // ADR-008: MPO estable en Windows 11 reciente sin apagarlo.
        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "MpoStableOverlayMinFps",
            CodeNumber = "G08",
            Title = "MPO estable (OverlayMinFPS = 0)",
            Category = TweakCategory.GpuDisplay,
            CategoryDisplay = "GPU & Display",
            TechnicalDescription = "Elimina el mínimo de FPS que DWM exige para darle un plano de overlay al juego: evita que alterne entre Composed: Flip e Independent Flip y los redibujados tras Alt-Tab. MPO sigue activo. Si \"Desactivar MPO\" (OverlayTestMode) estaba aplicado, lo borra.",
            WhatItTouches = @"HKLM\SOFTWARE\Microsoft\Windows\Dwm -> OverlayMinFPS = 0 (DWORD) · borra OverlayTestMode si existe",
            SourceAttribution = "ADR-008 · comunidad r/Windows11 y BlurBusters (PresentMon antes/después en 24H2) · Microsoft Learn: Multiplane overlay support",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            ApplicableGate = hw => hw.IsWindows11
                ? ApplicabilityResult.Applicable("Windows 11.")
                : ApplicabilityResult.NotApplicable("Es un ajuste de DWM de Windows 11."),
            EvaluateGate = _ => EvaluationResult.Recommended("Evidencia comunitaria: medí DPC y frametime antes y después (Monitor)."),
            ReadLiveState = () =>
                ReadRegDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayMinFPS") == 0 &&
                ReadRegDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode") is null,
            ApplyAction = _ =>
            {
                _audit.DeleteRegistryValue("MpoStableOverlayMinFps", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode");
                _audit.SetRegistryValue("MpoStableOverlayMinFps", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayMinFPS", 0, RegistryValueKind.DWord);
                return (true, "OverlayMinFPS = 0 aplicado (MPO activo). Reiniciá para que DWM lo tome.");
            }
        });

        // ==================== CATEGORÍA 4: RED / DESYNC / JITTER ====================
        // Placas de red: un ítem por placa con ajustes validados contra su driver (spec 002 / B4).
        BuildNicItems();
        BuildNetworkAdviceItems(); // filtros NDIS, optimizadores y driver NetAdapterCx (B5)
        BuildTcpStackItems();      // auto-tuning, Ndu y protocolos ligados (E3, E5, E6)

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "NicLsoAndFlowControlOff",
            CodeNumber = "N04",
            Title = "Desactivar RSC (Receive Segment Coalescing) de TCP",
            Category = TweakCategory.NetworkDesync,
            CategoryDisplay = "Red & Desync",
            TechnicalDescription = "RSC junta varios segmentos TCP recibidos en uno antes de entregarlos: menos interrupciones pero más espera. LSO y Flow Control los ajusta el ítem \"Placa de red\" según tu chip.",
            WhatItTouches = @"WMI root\StandardCimv2 MSFT_NetOffloadGlobalSetting -> ReceiveSegmentCoalescing = 0 (Disabled)",
            SourceAttribution = "Microsoft Learn: Receive Segment Coalescing (RSC) · Set-NetOffloadGlobalSetting · BlurBusters Desync Guide",
            Risk = RiskLevel.Low,
            RequiresReboot = false,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            ApplicableGate = _ => NetTcpGlobalStore.Read(NetTcpGlobalStore.OffloadClass, "ReceiveSegmentCoalescing") is null
                ? ApplicabilityResult.NotApplicable("Este Windows no expone el ajuste global de RSC.")
                : ApplicabilityResult.Applicable(),
            ReadLiveState = () => NetTcpGlobalStore.Read(NetTcpGlobalStore.OffloadClass, "ReceiveSegmentCoalescing") == 0,
            ApplyAction = _ =>
            {
                _audit.SetNetTcpGlobal("NicLsoAndFlowControlOff", NetTcpGlobalStore.OffloadClass, "ReceiveSegmentCoalescing", 0);
                return (true, "TCP RSC desactivado (verificado por WMI).");
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
            Title = "Nagle OFF (TCPNoDelay=1) + RSS de TCP + ECN Off",
            Category = TweakCategory.NetworkDesync,
            CategoryDisplay = "Red & Desync",
            TechnicalDescription = "Desactiva el algoritmo de agrupamiento de Nagle en todas las interfaces TCP/IP, activa RSS a nivel TCP y desactiva ECN (por WMI, sin netsh). Las colas RSS de cada placa las ajusta el ítem \"Placa de red\".",
            WhatItTouches = @"Tcpip\Parameters\Interfaces\<GUID> -> TCPNoDelay=1, TcpAckFrequency=1 · ECNEnabled=0 · WMI MSFT_NetOffloadGlobalSetting.ReceiveSideScaling=1 · MSFT_NetTCPSetting (Internet, InternetCustom).EcnCapability=0",
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
                // RSS y colas por placa: los valida el ítem "Placa de red" contra el driver (spec 002 / B4).
                _audit.SetRegistryValue("TcpNagleAndRssOptimize", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "ECNEnabled", 0, RegistryValueKind.DWord);
                if (NetTcpGlobalStore.Read(NetTcpGlobalStore.OffloadClass, "ReceiveSideScaling") is not null)
                {
                    _audit.SetNetTcpGlobal("TcpNagleAndRssOptimize", NetTcpGlobalStore.OffloadClass, "ReceiveSideScaling", 1);
                }
                foreach (string template in ClientTcpTemplates("EcnCapability"))
                {
                    _audit.SetNetTcpGlobal("TcpNagleAndRssOptimize", NetTcpGlobalStore.Target(NetTcpGlobalStore.TcpClass, template), "EcnCapability", 0);
                }
                return (true, "Nagle OFF + RSS de TCP + ECN OFF aplicados.");
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
            Title = "MSI en USB, audio HD, GPU, red y NVMe",
            Category = TweakCategory.IrqMsiAudio,
            CategoryDisplay = "IRQ, MSI & Audio",
            TechnicalDescription = "Activa Message Signaled Interrupts (MSISupported = 1) solo en los dispositivos que declaran soporte de MSI/MSI-X: " +
                                   "controladoras USB xHCI, audio HD, GPU, placa de red y NVMe. En los MSI-X se borra MessageNumberLimit para que no queden limitados. " +
                                   "El audio HD es el que más baja en ISR al pasar a MSI. Después de reiniciar, medí en el Monitor: si algo empeora (cortes de audio, más DPC), revertí." +
                                   MsiTargetsText(),
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Enum\PCI\<dispositivo>\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties -> MSISupported = 1 · MessageNumberLimit borrado (MSI-X)",
            SourceAttribution = "Microsoft Learn: Enabling Message-Signaled Interrupts in the Registry · MSI Utility v3 · BoringBoredom PC-Tuning §11.38",
            Risk = RiskLevel.Medium,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            ReadLiveState = () => _irqMsi.IsPciMsiModeAllEnabled(CachedDeviceTree()),
            ApplyAction = _ => _irqMsi.ApplySafePciMsiMode("PciMsiModeV3", CachedDeviceTree())
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "PciIrqSmartBalancer",
            CodeNumber = "I02",
            Title = "Interrupciones por núcleo físico (CPU 0 libre)",
            Category = TweakCategory.IrqMsiAudio,
            CategoryDisplay = "IRQ, MSI & Audio",
            TechnicalDescription = "Reparte las interrupciones en núcleos físicos distintos, nunca en el CPU 0 (queda para el kernel) ni en E-cores: " +
                                   "la red en el 2.º núcleo (por RSS, ítem \"Placa de red\"), cada controladora USB en su propio núcleo con prioridad alta " +
                                   "(primero la del mouse), la GPU en un núcleo o en dos (para el pico de video + juego) y el NVMe aparte. En X3D usa el CCD con V-Cache. " +
                                   "Si hay más dispositivos que núcleos, comparten con el menos importante.\n" + AffinityPlanText(),
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Enum\PCI\<dispositivo>\Device Parameters\Interrupt Management\Affinity Policy -> DevicePolicy = 4, AssignmentSetOverride (máscara), DevicePriority = 3 solo en USB (en el resto se borra); en la placa de red se borra el override",
            SourceAttribution = "Microsoft Learn: Interrupt Affinity and Priority (IRQ_DEVICE_POLICY) · AutoGpuAffinity · BoringBoredom PC-Tuning §11.43",
            Risk = RiskLevel.Medium,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = false,
            InProfesional = true,
            SelectedOptionKey = "Gpu2",
            Options = new List<TweakOptionChoice>
            {
                new() { Key = "Gpu2", ShortLabel = "GPU 2 núcleos", DisplayName = "GPU en dos núcleos [RECOMENDADO]", Description = "Reparte el pico de la GPU cuando hay video y juego a la vez." },
                new() { Key = "Gpu1", ShortLabel = "GPU 1 núcleo", DisplayName = "GPU en un núcleo", Description = "Deja un núcleo más libre (útil con 6 núcleos o menos y NVMe)." }
            },
            ApplicableGate = _ => _irqMsi.PlanAffinity(CachedDeviceTree()) is null
                ? ApplicabilityResult.NotApplicable("Hacen falta al menos 4 núcleos físicos para separar las interrupciones.")
                : ApplicabilityResult.Applicable(),
            ReadLiveState = () => _irqMsi.IsPciIrqAffinityActive(CachedDeviceTree(), gpuTwoCores: true) ||
                                  _irqMsi.IsPciIrqAffinityActive(CachedDeviceTree(), gpuTwoCores: false),
            ReadLiveOptionKey = () => _irqMsi.IsPciIrqAffinityActive(CachedDeviceTree(), gpuTwoCores: false) ? "Gpu1" : "Gpu2",
            ApplyAction = opt => _irqMsi.ApplyOptimalPciIrqAffinity("PciIrqSmartBalancer", CachedDeviceTree(), gpuTwoCores: opt != "Gpu1")
        });

        // ==================== CATEGORÍA 6: USB, ALMACENAMIENTO Y MEMORIA ====================
        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "UsbHidPowerSavingOff",
            CodeNumber = "S01",
            Title = "Ahorro de energía USB/HID apagado (hubs, mouse, teclado)",
            Category = TweakCategory.UsbPowerBus,
            CategoryDisplay = "USB, Disco & Memoria",
            TechnicalDescription = "En cada dispositivo y hub USB conectado: apaga la administración de energía mejorada del hub y la suspensión en reposo (D3), y desmarca \"Permitir que el equipo apague este dispositivo para ahorrar energía\" en USB y HID. Evita que el puerto del mouse o teclado entre en suspensión selectiva y tarde en despertar.",
            WhatItTouches = @"HKLM\SYSTEM\CurrentControlSet\Enum\USB\<dispositivo>\Device Parameters -> EnhancedPowerManagementEnabled=0, AllowIdleIrpInD3=0 · WMI MSPower_DeviceEnable.Enable=false (USB y HID que la exponen)",
            SourceAttribution = "Microsoft Learn: USB selective suspend / Device Parameters de usbhub · BoringBoredom PC-Tuning §11.37 · spec 002 (B6)",
            Risk = RiskLevel.Low,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            ReadLiveState = () =>
            {
                var targets = UsbPowerPlanner.PowerTargets(CachedDeviceTree());
                return targets.Count > 0 && targets.All(t =>
                    ReadRegDword(RegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Enum\{t.InstanceId}\Device Parameters", "EnhancedPowerManagementEnabled") == 0 &&
                    ReadRegDword(RegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Enum\{t.InstanceId}\Device Parameters", "AllowIdleIrpInD3") == 0);
            },
            ApplyAction = _ =>
            {
                var tree = new DeviceTree(new CfgMgrDeviceInventory().Snapshot());
                var targets = UsbPowerPlanner.PowerTargets(tree);
                foreach (var t in targets)
                {
                    string paramPath = $@"SYSTEM\CurrentControlSet\Enum\{t.InstanceId}\Device Parameters";
                    _audit.SetRegistryValue("UsbHidPowerSavingOff", RegistryHive.LocalMachine, paramPath, "EnhancedPowerManagementEnabled", 0, RegistryValueKind.DWord);
                    _audit.SetRegistryValue("UsbHidPowerSavingOff", RegistryHive.LocalMachine, paramPath, "AllowIdleIrpInD3", 0, RegistryValueKind.DWord);
                }

                int boxes = 0, failed = 0;
                foreach (var c in UsbPowerPlanner.PowerCheckboxCandidates(tree))
                {
                    switch (_audit.SetDeviceAllowPowerOff("UsbHidPowerSavingOff", c.InstanceId, allow: false))
                    {
                        case true: boxes++; break;
                        case false: failed++; break;
                    }
                }
                return failed == 0
                    ? (true, $"Ahorro de energía apagado en {targets.Count} dispositivos USB y en {boxes} casillas de energía.")
                    : (false, $"{targets.Count} USB ajustados, {boxes} casillas desmarcadas y {failed} que Windows no dejó cambiar.");
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
            Title = "Kernel siempre en RAM (DisablePagingExecutive)",
            Category = TweakCategory.StorageMemory,
            CategoryDisplay = "USB, Disco & Memoria",
            TechnicalDescription = "Con SSD/NVMe y 16 GB o más de RAM, mantiene los drivers y el código del kernel en RAM física (DisablePagingExecutive=1) en vez de mandarlos al archivo de paginación. SysMain se maneja en la pestaña Servicios.",
            WhatItTouches = @"Session Manager\Memory Management -> DisablePagingExecutive=1, LargeSystemCache=0",
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
                _audit.SetRegistryValue("SysMainAndMemoryManagement", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "DisablePagingExecutive", 1, RegistryValueKind.DWord);
                _audit.SetRegistryValue("SysMainAndMemoryManagement", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "LargeSystemCache", 0, RegistryValueKind.DWord);
                return (true, "Código del kernel fijado en RAM física.");
            }
        });

        // ==================== CATEGORÍA 7: FPS BOOST, JUEGOS Y MODO ENTUSIASTA ====================
        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "GameIfeoHighPriority",
            CodeNumber = "F01",
            Title = "Prioridad CPU/IO Alta por IFEO (CS2 / VALORANT)",
            Category = TweakCategory.FpsBoostGames,
            CategoryDisplay = "FPS Boost & Juegos",
            TechnicalDescription = "Configura CpuPriorityClass=3 (alta) e IoPriority=3 en IFEO\\<exe>\\PerfOptions: Windows prioriza el juego frente a procesos de fondo. No toca FSO (en Windows 11 conviene dejarlo activo para Independent Flip).",
            WhatItTouches = @"IFEO\<exe>\PerfOptions (CpuPriorityClass=3, IoPriority=3)",
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
                }
                return (true, $"Optimizaciones por ejecutable aplicadas a {TargetGames.Count} juegos.");
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

        // Spec 002 / B9: Hyper-V y VBS solo en Entusiasta, siempre con el modal de anticheat/seguridad.
        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "HypervisorAndVbsOff",
            CodeNumber = "C13",
            Title = "Desactivar Hyper-V y VBS / Integridad de memoria",
            Category = TweakCategory.SystemCpu,
            CategoryDisplay = "Sistema & CPU",
            TechnicalDescription = "Con Hyper-V cargado, Windows corre encima del hipervisor: cada interrupción y cambio de contexto pasa por una capa más, y VBS/HVCI verifica el código del kernel en el hipervisor. " +
                                   "Apagarlo baja el costo de interrupciones y DPC. " +
                                   "⚠ Deja de funcionar WSL2, Windows Sandbox, máquinas virtuales de Hyper-V y la Integridad de memoria. Algunos anticheats pueden exigir virtualización o VBS según la versión: si FACEIT AC o Vanguard no te dejan entrar, revertilo. " +
                                   "Si tu VBS está bloqueado por UEFI, el registro no alcanza: hay que desactivarlo desde la BIOS.",
            WhatItTouches = @"BCD {current} (WMI): hypervisorlaunchtype = Off · HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard -> EnableVirtualizationBasedSecurity=0 · DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity -> Enabled=0",
            SourceAttribution = "Microsoft Learn: BcdOSLoaderElementTypes (HypervisorLaunchType) · Enable virtualization-based protection of code integrity (claves DeviceGuard) · spec 002 (B9)",
            Risk = RiskLevel.High,
            SecurityImpact = SecurityImpact.ReducesSecurity,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = false,
            InProfesional = false,
            InEntusiasta = true,
            EvaluateGate = hw => hw.IsVbsEnabled || ReadBcd(BcdElement.HypervisorLaunchType)?.Integer != 0
                ? EvaluationResult.Neutral("Entusiasta: menos capas en cada interrupción a cambio de seguridad y compatibilidad.")
                : EvaluationResult.Recommended("Hyper-V y VBS ya están apagados."),
            AnticheatGate = _ => AnticheatResult.Unsafe("FACEIT AC / Vanguard pueden exigir virtualización o VBS según la versión de Windows y del anticheat."),
            ReadLiveState = () =>
                ReadBcd(BcdElement.HypervisorLaunchType)?.Integer == 0 &&
                ReadRegDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity") is null or 0 &&
                ReadRegDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled") is null or 0,
            ApplyAction = _ =>
            {
                _audit.SetBcdValue("HypervisorAndVbsOff", "hypervisorlaunchtype", "0");
                _audit.SetRegistryValue("HypervisorAndVbsOff", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", 0, RegistryValueKind.DWord);
                _audit.SetRegistryValue("HypervisorAndVbsOff", RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 0, RegistryValueKind.DWord);
                return (true, "Hyper-V (hypervisorlaunchtype Off) y VBS/HVCI desactivados. Reiniciá para aplicarlo.");
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

    /// <summary>
    /// Fija una clave dentro de DirectXUserGlobalSettings ("Clave=Valor;Clave=Valor;") respetando el resto
    /// (ej. VRROptimizeEnable, AutoHDREnable que el usuario haya configurado en Windows). Antes se sobrescribía todo.
    /// </summary>
    internal static string MergeDirectXGlobalSetting(string current, string key, string value)
    {
        var pairs = current
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !p.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        pairs.Add($"{key}={value}");
        return string.Join(";", pairs) + ";";
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
}
