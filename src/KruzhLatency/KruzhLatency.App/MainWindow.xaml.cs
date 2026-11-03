using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using KruzhLatency.Controls;
using KruzhLatency.Models;
using KruzhLatency.Services;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using WinRT.Interop;

namespace KruzhLatency;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _telemetryTimer;
    private readonly List<double> _cpuSamples = new();
    private readonly List<double> _gpuSamples = new();
    private readonly List<double> _ramSamples = new();
    private readonly List<double> _dpcSamples = new();
    private readonly List<double> _timerSamples = new();
    private readonly List<double> _scoreSamples = new();
    private List<NvramCheckItem> _currentNvramItems = new();
    private List<DriverCatalogueEntry> _currentDrivers = new();
    private PresetProfileKind _activePreset = PresetProfileKind.Competitivo;
    private bool _isInitializingUi = true;
    private bool _isHandlingToggleChange;
    private int _telemetryTickCounter;
    private Storyboard? _activeTooltipStoryboard;
    private string _currentGamesTab = "All";
    private string _gamesSearchQuery = string.Empty;
    private GameTargetItem? _selectedGameForModal = null;

    public MainWindow()
    {
        this.InitializeComponent();
        ConfigureMicaAndCustomTitleBar();

        _telemetryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _telemetryTimer.Tick += TelemetryTimer_Tick;

        this.Activated += MainWindow_Activated;
        RootGrid.Loaded += RootGrid_Loaded;
    }

    private IntPtr GetWindowHandle() => WindowNative.GetWindowHandle(this);

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_isInitializingUi) return;
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            RefreshLiveTweaksStateInPlace();
        }
    }

    private void MainNavView_PaneOpening(NavigationView sender, object args)
    {
        if (PaneBrandHeaderPanel != null) PaneBrandHeaderPanel.Visibility = Visibility.Visible;
        if (HeaderLicenseText != null) HeaderLicenseText.Visibility = Visibility.Visible;
    }

    private void MainNavView_PaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args)
    {
        if (PaneBrandHeaderPanel != null) PaneBrandHeaderPanel.Visibility = Visibility.Collapsed;
        if (HeaderLicenseText != null) HeaderLicenseText.Visibility = Visibility.Collapsed;
    }

    private void ConfigureMicaAndCustomTitleBar()
    {
        try
        {
            // Opaque solid background (#0E0F11) - Mica and acrylic disabled
            this.SystemBackdrop = null;

            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(AppTitleBar);

            IntPtr hwnd = GetWindowHandle();
            WindowId wndId = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(wndId);
            appWindow.Resize(new SizeInt32(1380, 880));
            appWindow.Title = "KRUZH LATENCY";

            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                var tb = appWindow.TitleBar;
                tb.ExtendsContentIntoTitleBar = true;
                tb.ButtonBackgroundColor = Colors.Transparent;
                tb.ButtonInactiveBackgroundColor = Colors.Transparent;
                tb.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(18, 255, 255, 255);
                tb.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(28, 255, 255, 255);
                tb.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 244, 246, 248);
                tb.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, 244, 246, 248);
                tb.ButtonPressedForegroundColor = Windows.UI.Color.FromArgb(255, 244, 246, 248);
                tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 97, 102, 112);
            }
        }
        catch { }
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs args)
    {
        if (!_isInitializingUi) return;

        try
        {
            App.TweakEngine.RefreshAll();
            PopulateHardwareAndDashboardUi();
            RefreshTweaksListUi();
            RefreshPciAndMsiUi();
            RefreshPowerPlansUi();
            RefreshGamesAndNvidiaUi();
            InitializeBiosNvramUi();
            RefreshJournalAndLicenseUi();

            _isInitializingUi = false;

            MainNavView.SelectedItem = NavDashboardItem;
            SwitchToView("Dashboard");

            // Set Hand cursor for the 3 visual resource cards
            SetHandCursor(HomeResourceCard1);
            SetHandCursor(HomeResourceCard2);
            SetHandCursor(HomeResourceCard3);

            // Sample live sensors immediately so zero "-" or mock values ever remain on screen
            TelemetryTimer_Tick(null, EventArgs.Empty);
            _telemetryTimer.Start();

            // Start native RegNotifyChangeKeyValue watchers for immediate external registry change detection
            App.MonitorScoreService.StartRegistryWatchers(() =>
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_isInitializingUi && !_isApplyingBatch)
                    {
                        RefreshLiveTweaksStateInPlace();
                    }
                });
            });

            _ = RefreshDriversUiAsync();

            if (!App.LicenseService.EulaState.Accepted && RootGrid.XamlRoot != null)
            {
                await ShowEulaDialogAsync(requireAcceptance: true);
            }
        }
        catch (Exception ex)
        {
            App.LogAndDisplayFatalError("RootGrid_Loaded Error", ex);
        }
    }

    // ========================================================================================
    // NAVIGATION BETWEEN THE 8 MAIN VIEWS (EXPANDABLE HAMBURGER RAIL)
    // ========================================================================================
    private void MainNavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem selectedItem || selectedItem.Tag is not string targetTag)
            return;

        SwitchToView(targetTag);
    }

    private void SwitchToView(string targetTag)
    {
        HideInstantTooltip();
        ViewDashboard.Visibility = targetTag == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        ViewTweaks.Visibility = targetTag == "Tweaks" ? Visibility.Visible : Visibility.Collapsed;
        ViewIrqMsi.Visibility = targetTag == "IrqMsi" ? Visibility.Visible : Visibility.Collapsed;
        ViewPowerDrivers.Visibility = targetTag == "PowerDrivers" ? Visibility.Visible : Visibility.Collapsed;
        ViewGamesNvidia.Visibility = targetTag == "GamesNvidia" ? Visibility.Visible : Visibility.Collapsed;
        ViewBiosNvram.Visibility = targetTag == "BiosNvram" ? Visibility.Visible : Visibility.Collapsed;
        ViewMonitorAudit.Visibility = targetTag == "MonitorAudit" ? Visibility.Visible : Visibility.Collapsed;
        ViewConfig.Visibility = targetTag == "Config" ? Visibility.Visible : Visibility.Collapsed;

        TitleBarPageNameText.Text = targetTag switch
        {
            "Dashboard" => "Inicio",
            "Tweaks" => "Optimizaciones",
            "IrqMsi" => "GPU",
            "PowerDrivers" => "Instaladores",
            "GamesNvidia" => "Juegos",
            "BiosNvram" => "BIOS",
            "MonitorAudit" => "Monitor",
            "Config" => "Configuración",
            _ => "Inicio"
        };

        if (targetTag == "MonitorAudit" || targetTag == "Config")
        {
            RefreshJournalAndLicenseUi();
        }
        else if (targetTag == "IrqMsi")
        {
            RefreshPciAndMsiUi();
        }
        else if (targetTag == "GamesNvidia")
        {
            RefreshGamesAndNvidiaUi();
        }
        else if (targetTag == "BiosNvram")
        {
            InitializeBiosNvramUi();
        }
    }

    private void OpenTweaksFromHomeBtn_Click(object sender, RoutedEventArgs e)
    {
        MainNavView.SelectedItem = NavTweaksItem;
        SwitchToView("Tweaks");
    }

    private void OpenConfigFromHomeBtn_Click(object sender, RoutedEventArgs e)
    {
        MainNavView.SelectedItem = NavConfigItem;
        SwitchToView("Config");
    }

    private void HomeResourceCardGpu_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        MainNavView.SelectedItem = NavIrqMsiItem;
        SwitchToView("IrqMsi");
    }

    private void HomeResourceCardAi_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        MainNavView.SelectedItem = NavTweaksItem;
        SwitchToView("Tweaks");
    }

    private void HomeResourceCardGame_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        MainNavView.SelectedItem = NavConfigItem;
        SwitchToView("Config");
    }

    private void ResourceCard_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            AnimateResourceCard(border, targetScale: 1.015, hoverHighlight: true);
        }
    }

    private void ResourceCard_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            AnimateResourceCard(border, targetScale: 1.0, hoverHighlight: false);
        }
    }

    private void AnimateResourceCard(Border card, double targetScale, bool hoverHighlight)
    {
        if (card.RenderTransform is not ScaleTransform st) return;

        var sb = new Storyboard();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(180));

        var scaleXAnim = new DoubleAnimation
        {
            To = targetScale,
            Duration = duration,
            EasingFunction = ease
        };
        Storyboard.SetTarget(scaleXAnim, st);
        Storyboard.SetTargetProperty(scaleXAnim, "ScaleX");

        var scaleYAnim = new DoubleAnimation
        {
            To = targetScale,
            Duration = duration,
            EasingFunction = ease
        };
        Storyboard.SetTarget(scaleYAnim, st);
        Storyboard.SetTargetProperty(scaleYAnim, "ScaleY");

        sb.Children.Add(scaleXAnim);
        sb.Children.Add(scaleYAnim);

        Border? highlight = card.Name switch
        {
            "HomeResourceCard1" => ResourceHoverHighlight1,
            "HomeResourceCard2" => ResourceHoverHighlight2,
            "HomeResourceCard3" => ResourceHoverHighlight3,
            _ => null
        };

        if (highlight != null)
        {
            var opAnim = new DoubleAnimation
            {
                To = hoverHighlight ? 0.04 : 0.0,
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(opAnim, highlight);
            Storyboard.SetTargetProperty(opAnim, "Opacity");
            sb.Children.Add(opAnim);
        }

        sb.Begin();
    }

    private static void SetHandCursor(UIElement element)
    {
        try
        {
            var cursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand);
            var prop = typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            prop?.SetValue(element, cursor);
        }
        catch { }
    }

    // ========================================================================================
    // DASHBOARD, HARDWARE TELEMETRY & DYNAMIC SCORE PRO
    // ========================================================================================
    private void PopulateHardwareAndDashboardUi()
    {
        var hw = App.HardwareService.CurrentProfile;

        SidebarChassisBadge.Text = $"AMI Aptio: {(hw.IsAmiAptioBios ? "Compatible" : "Bloqueado")} · Placa: {hw.BaseBoardModel}";
        SidebarCpuSummary.Text = $"{hw.CpuName} ({hw.CpuPhysicalCores}C/{hw.CpuLogicalThreads}T)";
        SidebarGpuSummary.Text = $"{hw.GpuName} ({hw.GpuGeneration})";

        HwCpuNameText.Text = hw.CpuName;
        HwCpuArchText.Text = $"{hw.CpuVendor} · {hw.ClockingModeDescription}";
        DashboardCpuSubtitleText.Text = $"{hw.CpuPhysicalCores} núcleos físicos · {hw.CpuLogicalThreads} hilos lógicos";

        int eCores = hw.Topology.Count(t => t.IsEfficiencyCore);
        int pCores = hw.CpuPhysicalCores;
        HwTopologyText.Text = $"{pCores} Núcleos Físicos / {hw.CpuLogicalThreads} Hilos Lógicos";
        HwCcdL3Text.Text = hw.IsDualCcdRyzen
            ? "Dual-CCD Ryzen (ReservedCpuSets Activo)"
            : (hw.IsIntelHybrid ? $"Intel Hybrid ({eCores} E-Cores detectados)" : "Single-CCD uniforme (sin E-Cores)");

        HwGpuNameText.Text = hw.GpuName;
        HwGpuGenText.Text = $"Gen: {hw.GpuGeneration} · VRAM: {hw.GpuVramGb} GB · HAGS Rec: {(hw.BenefitsFromHags ? "Activo" : "Inactivo")}";
        DashboardGpuSubtitleText.Text = $"{hw.GpuName} ({hw.GpuVramGb} GB VRAM)";

        HwMotherboardText.Text = string.IsNullOrWhiteSpace(hw.BaseBoardModel) || hw.BaseBoardModel == "Unknown"
            ? "Placa base del sistema"
            : hw.BaseBoardModel;
        HwBiosVersionText.Text = $"BIOS: {hw.BiosVendor} {hw.BiosVersion}";

        HwChassisText.Text = hw.Chassis == ChassisKind.Laptop ? "Laptop / Portátil (Thermal-Safe)" : "Desktop / Escritorio";
        HwBatteryText.Text = hw.ChassisDetectionReason;

        HwNicNameText.Text = $"{hw.NicName} ({hw.NicType})";
        HwRamMonitorText.Text = $"RAM: {hw.RamTotalGb} GB ({(hw.IsDualChannel ? "Dual Channel" : "Single")}) · Monitor: {hw.MonitorRefreshHz} Hz";

        HwBiosText.Text = $"{hw.BiosVendor} ({hw.BiosVersion})";
        HwSecurityStateText.Text = $"TPM: {(hw.IsTpmEnabled ? "Activo" : "Inactivo")} · Secure Boot: {(hw.IsSecureBootEnabled ? "Activo" : "Inactivo")} · VBS: {(hw.IsVbsEnabled ? "Activo" : "Inactivo")}";

        CpuClockingModeCombo.SelectedIndex = hw.ClockingMode == CpuClockingMode.StaticAllCoreOverclock ? 0 : 1;

        MousePollingHzCombo.SelectedIndex = hw.MousePollingRateHz switch
        {
            >= 8000 => 3,
            >= 4000 => 2,
            >= 2000 => 1,
            _ => 0
        };

        CpuClockingModeExplainText.Text = hw.ClockingMode == CpuClockingMode.StaticAllCoreOverclock
            ? "Modo actual: Multiplicador fijo All-Core Overclock. El motor prioriza el plan Desktop Agresivo (100% constante) y descarta planes Ryzen Balanced."
            : "Modo actual: Boost dinámico (PBO / Curve Optimizer / Turbo). El motor habilita gestión dinámica de relojes.";

        UpdateScoreRingAndCounters();
    }

    private void UpdateScoreRingAndCounters()
    {
        var hw = App.HardwareService.CurrentProfile;
        var (score, grade, positives, _, issues) = App.MonitorScoreService.CalculateDynamicScore(hw, App.TweakEngine);

        DashboardScoreValueText.Text = $"{score} %";
        int appliedTotal = App.TweakEngine.AllTweaks.Count(t => t.IsApplicableBool && t.IsLiveApplied);
        int applicableTotal = App.TweakEngine.AllTweaks.Count(t => t.IsApplicableBool);
        DashboardAnticheatText.Text = $"{appliedTotal} de {applicableTotal} optimizaciones activas en Windows";

        if (BottomStatusBarText != null && (string.IsNullOrEmpty(BottomStatusBarText.Text) || BottomStatusBarText.Text == "-"))
        {
            BottomStatusBarText.Text = $"{appliedTotal} optimizaciones detectadas en vivo · {grade}";
        }
    }

    private void RefreshLiveTweaksStateInPlace()
    {
        _isHandlingToggleChange = true;
        try
        {
            App.TweakEngine.RefreshAll();
            UpdateScoreRingAndCounters();
            UpdateTweaksSummarySubtitle();
            UpdatePendingChangesBar();
        }
        finally
        {
            _isHandlingToggleChange = false;
        }
    }

    private void UpdateTweaksSummarySubtitle()
    {
        if (TweaksSummaryCounterText == null) return;
        int totalApplicable = App.TweakEngine.AllTweaks.Count(t => t.IsApplicableBool);
        int liveApplied = App.TweakEngine.AllTweaks.Count(t => t.IsApplicableBool && t.IsLiveApplied);
        int hiddenByHw = App.TweakEngine.AllTweaks.Count(t => !t.IsApplicableBool);
        TweaksSummaryCounterText.Text =
            $"{liveApplied} de {totalApplicable} optimizaciones compatibles aplicadas en el sistema ({hiddenByHw} ocultas por hardware).";
    }

    private void RefreshSystemStateBtn_Click(object sender, RoutedEventArgs e)
    {
        App.HardwareService.DetectHardwareProfile();
        App.TweakEngine.RefreshAll();
        PopulateHardwareAndDashboardUi();
        RefreshTweaksListUi();
        RefreshPowerPlansUi();
        SetStatus("Estado real del sistema (ReadState + Verify) actualizado.");
    }

    private void CpuClockingModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingUi) return;
        if (CpuClockingModeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            var newMode = tag == "StaticAllCore"
                ? CpuClockingMode.StaticAllCoreOverclock
                : CpuClockingMode.DynamicBoostPbo;

            App.HardwareService.SetClockingModeOverride(newMode);
            App.TweakEngine.RefreshAll();

            PopulateHardwareAndDashboardUi();
            RefreshTweaksListUi();
            RefreshPowerPlansUi();
            InitializeBiosNvramUi();
            SetStatus($"Régimen de CPU actualizado: {App.HardwareService.CurrentProfile.ClockingModeDescription}");
        }
    }

    private void MousePollingHzCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingUi) return;
        if (MousePollingHzCombo.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag &&
            int.TryParse(tag, out int hz))
        {
            App.HardwareService.SetMousePollingRate(hz);

            var mouseQueueTweak = App.TweakEngine.AllTweaks.FirstOrDefault(t => t.Id == "MouseDataQueueDynamic");
            if (mouseQueueTweak != null)
            {
                mouseQueueTweak.SelectedOptionKey = hz switch
                {
                    >= 8000 => "128",
                    >= 4000 => "64",
                    >= 2000 => "32",
                    _ => "16"
                };
            }

            App.TweakEngine.RefreshAll();
            RefreshTweaksListUi();
            SetStatus($"Polling Rate ajustado a {hz} Hz (MouseDataQueueSize = {mouseQueueTweak?.SelectedOptionKey ?? "16"}).");
        }
    }

    // ========================================================================================
    // PRESET PROFILES (MINIMAL, COMPETITIVO, PROFESIONAL, ENTUSIASTA) & JSON IMPORT/EXPORT
    // ========================================================================================
    private void SelectPresetProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;

        _activePreset = tag switch
        {
            "Minimal" => PresetProfileKind.Minimal,
            "Profesional" => PresetProfileKind.Profesional,
            "Entusiasta" => PresetProfileKind.Entusiasta,
            _ => PresetProfileKind.Competitivo
        };

        _isHandlingToggleChange = true;
        try
        {
            App.TweakEngine.SelectPresetProfile(_activePreset);
            MainNavView.SelectedItem = NavTweaksItem;
            SwitchToView("Tweaks");
            UpdatePendingChangesBar();
        }
        finally
        {
            _isHandlingToggleChange = false;
        }

        int pendingCount = App.TweakEngine.AllTweaks.Count(t => t.IsPending);
        SetStatus(pendingCount > 0
            ? $"Perfil {_activePreset} preseleccionado: {pendingCount} cambios pendientes listos para aplicar."
            : $"Todos los módulos compatibles del Perfil {_activePreset} ya están aplicados en el sistema.");
    }

    private void ExportProfileJsonBtn_Click(object sender, RoutedEventArgs e)
    {
        string? savePath = Win32DialogService.ShowSaveFileDialog(
            GetWindowHandle(),
            "Exportar Perfil KRUZH LATENCY (.json)",
            "Perfil KRUZH JSON (*.json)\0*.json\0Todos los archivos (*.*)\0*.*\0",
            $"KruzhProfile_{_activePreset}_{DateTime.Now:yyyyMMdd}.json",
            "json");

        if (string.IsNullOrWhiteSpace(savePath)) return;

        var hw = App.HardwareService.CurrentProfile;
        var doc = new ExportedProfileDocument
        {
            ProfileName = $"KRUZH {_activePreset} ({hw.CpuName})",
            BasePreset = _activePreset,
            CreatedAtUtc = DateTime.UtcNow,
            PreferredClockingMode = hw.ClockingMode,
            MousePollingRateHz = hw.MousePollingRateHz,
            EnabledTweaks = App.TweakEngine.AllTweaks.ToDictionary(t => t.Id, t => t.IsLiveApplied),
            SelectedTweakOptions = App.TweakEngine.AllTweaks
                .Where(t => t.HasOptions && !string.IsNullOrEmpty(t.SelectedOptionKey))
                .ToDictionary(t => t.Id, t => t.SelectedOptionKey),
            CustomGames = App.TweakEngine.TargetGames.ToList()
        };

        string json = JsonSerializer.Serialize(doc, AppJsonContext.Default.ExportedProfileDocument);
        File.WriteAllText(savePath, json, Encoding.UTF8);
        SetStatus($"Perfil exportado correctamente en: {savePath}");
    }

    private async void ImportProfileJsonBtn_Click(object sender, RoutedEventArgs e)
    {
        string? openPath = Win32DialogService.ShowOpenFileDialog(
            GetWindowHandle(),
            "Importar Perfil KRUZH LATENCY (.json)",
            "Perfil KRUZH JSON (*.json)\0*.json\0Todos los archivos (*.*)\0*.*\0");

        if (string.IsNullOrWhiteSpace(openPath) || !File.Exists(openPath)) return;

        try
        {
            string json = File.ReadAllText(openPath, Encoding.UTF8);
            var doc = JsonSerializer.Deserialize(json, AppJsonContext.Default.ExportedProfileDocument);
            if (doc == null)
            {
                SetStatus("El archivo .json no tiene un formato de perfil KRUZH válido.");
                return;
            }

            App.HardwareService.SetClockingModeOverride(doc.PreferredClockingMode);
            App.HardwareService.SetMousePollingRate(doc.MousePollingRateHz);
            App.TweakEngine.RefreshAll();

            foreach (var tweak in App.TweakEngine.AllTweaks)
            {
                if (doc.SelectedTweakOptions.TryGetValue(tweak.Id, out string? opt) && !string.IsNullOrEmpty(opt))
                {
                    tweak.SelectedOptionKey = opt;
                }

                if (doc.EnabledTweaks.TryGetValue(tweak.Id, out bool enabled))
                {
                    tweak.IsSelectedForBatch = enabled && tweak.IsApplicableBool;
                }
            }

            PopulateHardwareAndDashboardUi();
            RefreshTweaksListUi();
            await ApplySelectedTweaksFlowAsync(doc.ProfileName);
        }
        catch (Exception ex)
        {
            SetStatus($"Error importando perfil JSON: {ex.Message}");
        }
    }

    // ========================================================================================
    // TWEAKS VIEW (GROUPED CATEGORY CONTAINERS + PENDING MODEL + INSTANT ANIMATED TOOLTIP)
    // ========================================================================================
    private void RefreshTweaksListUi()
    {
        if (TweaksGroupsItemsControl == null) return;

        _isHandlingToggleChange = true;
        try
        {
            App.TweakEngine.RefreshAll();

            string catTag = (TweakCategoryFilterCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ALL";
            string gateTag = (TweakGateFilterCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "APPLICABLE";
            string search = TweakSearchBox?.Text?.Trim() ?? string.Empty;

            IEnumerable<DeclarativeTweakItem> query = App.TweakEngine.AllTweaks;

            if (catTag != "ALL")
            {
                query = catTag switch
                {
                    "MouseAndInput" => query.Where(t => t.Category == TweakCategory.MouseInput),
                    "UsbAndPower" => query.Where(t => t.Category == TweakCategory.UsbPowerBus || t.Category == TweakCategory.StorageMemory),
                    "GpuAndDisplay" => query.Where(t => t.Category == TweakCategory.GpuDisplay),
                    "NetworkAndTcp" => query.Where(t => t.Category == TweakCategory.NetworkDesync),
                    "SystemAndCpu" => query.Where(t => t.Category == TweakCategory.SystemCpu),
                    "IrqAndAudio" => query.Where(t => t.Category == TweakCategory.IrqMsiAudio),
                    "FpsBoostAndGames" => query.Where(t => t.Category == TweakCategory.FpsBoostGames),
                    _ => query
                };
            }

            query = gateTag switch
            {
                "APPLICABLE" => query.Where(t => t.IsApplicableBool),
                "RECOMMENDED" => query.Where(t => t.IsRecommendedBool),
                "ANTICHEAT_SAFE" => query.Where(t => t.IsApplicableBool && !t.IsAnticheatUnsafeBool && !t.ReducesSecurityBool),
                _ => query
            };

            if (!string.IsNullOrEmpty(search))
            {
                query = query.Where(t =>
                    t.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    t.CodeNumber.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    t.TechnicalDescription.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    t.WhatItTouches.Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            var filtered = query.ToList();

            var categoryOrder = new (string Title, Func<DeclarativeTweakItem, bool> Predicate)[]
            {
                ("MOUSE E INPUT",         t => t.Category == TweakCategory.MouseInput),
                ("CPU Y SCHEDULER",       t => t.Category == TweakCategory.SystemCpu),
                ("GPU Y DISPLAY",         t => t.Category == TweakCategory.GpuDisplay),
                ("RED Y TCP/IP",          t => t.Category == TweakCategory.NetworkDesync),
                ("IRQ, MSI Y AUDIO",      t => t.Category == TweakCategory.IrqMsiAudio),
                ("ENERGÍA, USB Y MEMORIA",t => t.Category == TweakCategory.UsbPowerBus || t.Category == TweakCategory.StorageMemory),
                ("JUEGOS Y FPS",          t => t.Category == TweakCategory.FpsBoostGames)
            };

            var groups = new List<TweakCategoryGroup>();
            foreach (var (secTitle, pred) in categoryOrder)
            {
                var sectionItems = filtered.Where(pred).ToList();
                if (sectionItems.Count > 0)
                {
                    for (int i = 0; i < sectionItems.Count; i++)
                    {
                        sectionItems[i].IsFirstInGroup = (i == 0);
                    }
                    groups.Add(new TweakCategoryGroup
                    {
                        SectionTitle = secTitle,
                        Items = sectionItems
                    });
                }
            }

            TweaksGroupsItemsControl.ItemsSource = groups;
            UpdateTweaksSummarySubtitle();
            UpdatePendingChangesBar();
        }
        finally
        {
            _isHandlingToggleChange = false;
        }
    }

    private void TweakFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingUi) return;
        RefreshTweaksListUi();
    }

    private void TweakSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializingUi) return;
        RefreshTweaksListUi();
    }

    private void TweakCardToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializingUi || _isHandlingToggleChange) return;
        if (sender is not ToggleSwitch toggle || toggle.Tag is not string tweakId) return;

        var item = App.TweakEngine.AllTweaks.FirstOrDefault(t => t.Id == tweakId);
        if (item == null || !item.IsApplicableBool) return;

        if (toggle.IsOn == item.DesiredApplied) return;

        item.SetDesiredToggle(toggle.IsOn);
        UpdatePendingChangesBar();
    }

    private void SegmentedOptionBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string compositeTag) return;
        string[] parts = compositeTag.Split('|');
        if (parts.Length != 2) return;

        string tweakId = parts[0];
        string optionKey = parts[1];

        var item = App.TweakEngine.AllTweaks.FirstOrDefault(t => t.Id == tweakId);
        if (item == null || !item.IsApplicableBool) return;

        item.SetDesiredOption(optionKey);
        UpdatePendingChangesBar();
    }

    private void UpdatePendingChangesBar()
    {
        if (PendingChangesStickyBar == null || PendingChangesStatusText == null) return;

        int pendingCount = App.TweakEngine.AllTweaks.Count(t => t.IsPending);
        if (pendingCount > 0)
        {
            PendingChangesStatusText.Text = pendingCount == 1
                ? "1 cambio pendiente · se creará un punto de restauración antes de aplicar"
                : $"{pendingCount} cambios pendientes · se creará un punto de restauración antes de aplicar";
            PendingChangesStickyBar.Visibility = Visibility.Visible;
        }
        else
        {
            PendingChangesStickyBar.Visibility = Visibility.Collapsed;
        }
    }

    private void DiscardPendingChangesBtn_Click(object sender, RoutedEventArgs e)
    {
        _isHandlingToggleChange = true;
        try
        {
            App.TweakEngine.DiscardAllPending();
            UpdatePendingChangesBar();
        }
        finally
        {
            _isHandlingToggleChange = false;
        }
        SetStatus("Cambios pendientes descartados. Controles sincronizados con el estado real del sistema.");
    }

    private async void ApplyPendingChangesBtn_Click(object sender, RoutedEventArgs e)
    {
        await ApplyPendingChangesFlowAsync();
    }

    private bool _isApplyingBatch;

    private async Task ApplyPendingChangesFlowAsync()
    {
        if (_isApplyingBatch) return;
        var pending = App.TweakEngine.AllTweaks.Where(t => t.IsPending).ToList();
        if (pending.Count == 0) return;

        if (!App.LicenseService.PerformIntegrityAndAntiDebugCheck(out string integrityMsg))
        {
            await ShowSimpleInfoModalAsync("Verificación de integridad", integrityMsg);
            return;
        }

        var toApply = pending.Where(t => !t.IsPendingRevert).ToList();
        var toRevert = pending.Where(t => t.IsPendingRevert).ToList();
        bool needsReboot = pending.Any(t => t.RequiresReboot);

        var riskyItems = toApply
            .Where(t => t.IsAnticheatUnsafeBool || t.ReducesSecurityBool)
            .ToList();

        if (riskyItems.Count > 0)
        {
            bool redConfirmed = await ShowRedSecurityWarningModalAsync(riskyItems);
            if (!redConfirmed)
            {
                SetStatus("Operación cancelada en la advertencia de seguridad/anticheat.");
                return;
            }
        }

        _isApplyingBatch = true;
        _telemetryTimer.Stop(); // no refrescar estado de tweaks mientras se escriben (evita lecturas a medio aplicar)

        int total = pending.Count;
        int completed = 0;
        int appliedOk = 0;
        int revertedOk = 0;
        var failures = new List<TweakExecutionReport>();

        try
        {
            // 1. Overlay: velo con blur + box-loader + carrusel de 3 líneas
            ApplyingChangesOverlay.Visibility = Visibility.Visible;
            StartOverlaySpinnerAnimation();

            OverlayRestorePointText.Text = "Creando punto de restauración del sistema...";
            OverlayStepDescText.Text = "Preparando cambios...";
            OverlayProgressBar.Value = 0;
            OverlayCountText.Text = $"0 / {total} aplicados";
            SetCarouselRows(prevTitle: "(Inicio del lote)", prevStatus: string.Empty, prevFailed: false,
                activeTitle: "Creando punto de restauración…", nextTitle: CarouselTitleAt(pending, 0), next2Title: CarouselTitleAt(pending, 1));

            // 2. Punto de restauración previo
            // TODO(T55): el servicio todavía reporta éxito aunque falle; se corrige con el adaptador WMI.
            var (rpOk, rpMsg) = await App.AuditService.CreateSystemRestorePointAsync($"KRUZH LATENCY ({total} cambios)");
            OverlayRestorePointText.Text = rpOk ? "Punto de restauración solicitado" : "No se pudo crear el punto de restauración";
            SetStatus(rpMsg);

            // 3. Un tweak a la vez: chequeo en UI -> operaciones de sistema en background -> verificación en UI.
            //    Al pasar al siguiente, la lista sube una posición: el que estaba en el medio pasa arriba ("listo"),
            //    el de abajo entra al medio ("aplicando") y entra uno nuevo por abajo ("en espera").
            string lastTitle = "Creando punto de restauración…";
            string lastStatus = rpOk ? "listo ✓" : "omitido";
            bool lastFailed = !rpOk;

            for (int i = 0; i < total; i++)
            {
                var item = pending[i];
                var stepWatch = Stopwatch.StartNew();
                int step = i; // copia local: la lambda de abajo no debe depender de la variable del for

                string prevTitleForRow = lastTitle;
                string prevStatusForRow = lastStatus;
                bool prevFailedForRow = lastFailed;
                await SlideCarouselUpAsync(
                    enteringTitle: CarouselTitleAt(pending, i + 1),
                    afterSlide: () => SetCarouselRows(prevTitleForRow, prevStatusForRow, prevFailedForRow,
                        CarouselTitleAt(pending, step), CarouselTitleAt(pending, step + 1), CarouselTitleAt(pending, step + 2)));

                OverlayStepDescText.Text = item.IsPendingRevert ? "Revirtiendo al valor original..." : "Aplicando optimización...";
                OverlayProgressBar.Value = (double)completed / total * 100.0;
                OverlayCountText.Text = $"{completed} / {total} aplicados";

                TweakExecutionReport report;
                bool isRevert = item.IsPendingRevert;
                try
                {
                    if (isRevert)
                    {
                        var (ok, detail) = await Task.Run(() => App.TweakEngine.RunRevertActions(item));
                        report = App.TweakEngine.CompleteRevert(item, ok, detail);
                    }
                    else
                    {
                        if (item.HasOptions)
                        {
                            item.SelectedOptionKey = item.DesiredOptionKey;
                        }
                        var early = App.TweakEngine.PreCheckApply(item);
                        if (early != null)
                        {
                            report = early;
                        }
                        else
                        {
                            string optionKey = item.SelectedOptionKey;
                            var (ok, detail) = await Task.Run(() => DeclarativeTweakEngine.RunApplyAction(item, optionKey));
                            report = App.TweakEngine.CompleteApply(item, ok, detail);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Red de seguridad: un tweak nunca corta el lote ni crashea la app.
                    App.LogError($"Batch item {item.Id}", ex);
                    report = new TweakExecutionReport
                    {
                        TweakId = item.Id,
                        TweakTitle = item.Title,
                        Success = false,
                        Message = $"Error inesperado: {ex.Message}"
                    };
                }

                if (report.Success)
                {
                    if (isRevert) revertedOk++; else appliedOk++;
                }
                else
                {
                    failures.Add(report);
                }

                completed++;
                OverlayProgressBar.Value = (double)completed / total * 100.0;
                OverlayCountText.Text = $"{completed} / {total} aplicados";
                lastTitle = CarouselTitleAt(pending, i);
                lastStatus = report.Success ? (isRevert ? "revertido ✓" : "listo ✓") : "falló ✗";
                lastFailed = !report.Success;

                // Tiempo mínimo por tweak para que el movimiento de la lista se pueda seguir con la vista.
                int remaining = MinCarouselStepMs - (int)stepWatch.ElapsedMilliseconds;
                if (remaining > 0)
                {
                    await Task.Delay(remaining);
                }
            }

            // Último deslizamiento: el último tweak sube a "listo" y el centro muestra el resultado del lote.
            string finalPrevTitle = lastTitle;
            string finalPrevStatus = lastStatus;
            bool finalPrevFailed = lastFailed;
            string finalMessage = failures.Count == 0 ? "¡Optimizaciones completadas!" : $"Terminado con {failures.Count} error(es)";
            await SlideCarouselUpAsync(
                enteringTitle: string.Empty,
                afterSlide: () =>
                {
                    SetCarouselRows(finalPrevTitle, finalPrevStatus, finalPrevFailed, finalMessage, string.Empty, string.Empty);
                    CarouselActiveStatusText.Text = string.Empty;
                    CarouselNextStatusText.Text = string.Empty;
                });

            OverlayProgressBar.Value = 100;
            OverlayCountText.Text = $"{total} / {total} procesados";
            OverlayStepDescText.Text = "Verificando estado real en el registro...";
            await Task.Delay(500);
        }
        finally
        {
            // El overlay se cierra SIEMPRE, aunque algo haya fallado.
            ApplyingChangesOverlay.Visibility = Visibility.Collapsed;
            StopOverlaySpinnerAnimation();

            _isHandlingToggleChange = true;
            try
            {
                App.TweakEngine.DiscardAllPending();
                RefreshLiveTweaksStateInPlace();
                RefreshJournalAndLicenseUi();
            }
            catch (Exception ex)
            {
                App.LogError("Refresh after batch", ex);
            }
            finally
            {
                _isHandlingToggleChange = false;
                _isApplyingBatch = false;
                _telemetryTimer.Start();
            }
        }

        SetStatus(failures.Count == 0
            ? $"Cambios completados: {appliedOk} aplicados, {revertedOk} revertidos."
            : $"Cambios completados con errores: {appliedOk} aplicados, {revertedOk} revertidos, {failures.Count} fallaron.");

        if (failures.Count > 0)
        {
            string detailText = string.Join("\n", failures.Select(f => $"• {f.TweakTitle}: {f.Message}"));
            await ShowSimpleInfoModalAsync(
                $"{failures.Count} cambio(s) no se aplicaron",
                $"{detailText}\n\nEl resto del lote se aplicó normalmente. Detalle técnico en %LOCALAPPDATA%\\KruzhLatency\\errors.log");
        }

        if (needsReboot)
        {
            await ShowSimpleInfoModalAsync(
                "Reinicio recomendado",
                "Algunos cambios a nivel de kernel/BCD requieren reiniciar la PC para surtir efecto total.");
        }
    }

    private Storyboard? _overlaySpinnerStoryboard;
    private Task<bool>? _overlayBoxLoaderTask;

    /// <summary>Paso de la pista del carrusel: alto de fila (44) + separación (4).</summary>
    private const double CarouselRowPitch = 48;
    private const int CarouselSlideMs = 260;
    /// <summary>Tiempo mínimo que cada tweak queda en el centro, para que el movimiento sea legible.</summary>
    private const int MinCarouselStepMs = 450;

    private void StartOverlaySpinnerAnimation()
    {
        // El box-loader se carga una sola vez (con el overlay ya visible, así el WebView2 está en el árbol visual).
        _overlayBoxLoaderTask ??= BoxLoader.LoadAsync(OverlayBoxLoaderWebView, OverlayFallbackSpinner);
        if (_overlayBoxLoaderTask.IsCompletedSuccessfully && _overlayBoxLoaderTask.Result)
        {
            return; // las cajas ya están andando; no hace falta el spinner de respaldo
        }

        try
        {
            var anim = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = new Duration(TimeSpan.FromSeconds(0.85)),
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(anim, OverlaySpinnerRotate);
            Storyboard.SetTargetProperty(anim, "Angle");

            _overlaySpinnerStoryboard = new Storyboard();
            _overlaySpinnerStoryboard.Children.Add(anim);
            _overlaySpinnerStoryboard.Begin();
        }
        catch (Exception ex)
        {
            App.LogError("Overlay spinner", ex);
        }
    }

    private static string CarouselTitleAt(IReadOnlyList<DeclarativeTweakItem> items, int index) =>
        index >= 0 && index < items.Count
            ? $"[{items[index].CodeNumber}] {items[index].Title}"
            : (index == items.Count ? "(Fin del lote)" : string.Empty);

    /// <summary>Asigna las 3 filas visibles + la fila de abajo que entra al deslizar.</summary>
    private void SetCarouselRows(string prevTitle, string prevStatus, bool prevFailed, string activeTitle, string nextTitle, string next2Title)
    {
        var positive = (Brush)Application.Current.Resources["KruzhPositiveBrush"];
        var danger = (Brush)Application.Current.Resources["KruzhDangerMutedBrush"];

        CarouselPrevTitleText.Text = prevTitle;
        CarouselPrevStatusText.Text = prevStatus;
        CarouselPrevStatusText.Foreground = prevFailed ? danger : positive;
        CarouselPrevIcon.Glyph = prevFailed ? "" : ""; // ✕ / ✓
        CarouselPrevIcon.Foreground = prevFailed ? danger : positive;
        CarouselPrevIcon.Visibility = string.IsNullOrEmpty(prevStatus) ? Visibility.Collapsed : Visibility.Visible;

        CarouselActiveTitleText.Text = activeTitle;
        CarouselActiveStatusText.Text = "aplicando…";

        CarouselNextTitleText.Text = nextTitle;
        CarouselNextStatusText.Text = string.IsNullOrEmpty(nextTitle) || nextTitle == "(Fin del lote)" ? string.Empty : "en espera";

        CarouselNext2TitleText.Text = next2Title;
        CarouselNext2StatusText.Text = string.IsNullOrEmpty(next2Title) || next2Title == "(Fin del lote)" ? string.Empty : "en espera";
    }

    /// <summary>
    /// Sube la lista una posición (animación de 260 ms). Al terminar llama a <paramref name="afterSlide"/> para
    /// reasignar los textos y vuelve la pista a su lugar en el mismo frame, así el salto no se ve.
    /// </summary>
    private Task SlideCarouselUpAsync(string enteringTitle, Action afterSlide)
    {
        CarouselNext2TitleText.Text = enteringTitle;
        CarouselNext2StatusText.Text = string.IsNullOrEmpty(enteringTitle) || enteringTitle == "(Fin del lote)" ? string.Empty : "en espera";

        var done = new TaskCompletionSource();
        try
        {
            var anim = new DoubleAnimation
            {
                From = 0,
                To = -CarouselRowPitch,
                Duration = new Duration(TimeSpan.FromMilliseconds(CarouselSlideMs)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(anim, CarouselTrackTransform);
            Storyboard.SetTargetProperty(anim, "Y");

            var sb = new Storyboard();
            sb.Children.Add(anim);
            sb.Completed += (_, _) =>
            {
                try
                {
                    afterSlide();
                }
                finally
                {
                    sb.Stop(); // vuelve Y a 0 con los textos ya corridos
                    CarouselTrackTransform.Y = 0;
                    done.TrySetResult();
                }
            };
            sb.Begin();
        }
        catch (Exception ex)
        {
            App.LogError("Carousel slide", ex);
            afterSlide();
            CarouselTrackTransform.Y = 0;
            done.TrySetResult();
        }
        return done.Task;
    }

    private void StopOverlaySpinnerAnimation()
    {
        try
        {
            _overlaySpinnerStoryboard?.Stop();
        }
        catch { }
    }

    // ========================================================================================
    // INSTANT ANIMATED INFO TOOLTIP ("ⓘ" HOVER & CLICK, 0ms DELAY, 150ms CUBICEASE)
    // ========================================================================================
    private void TweakInfoIcon_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border iconBorder || iconBorder.Tag is not DeclarativeTweakItem item) return;
        if (iconBorder.Child is FontIcon fi)
        {
            fi.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 240, 242, 244));
        }
        ShowInstantTooltip(iconBorder, item);
    }

    private void TweakInfoIcon_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border iconBorder && iconBorder.Child is FontIcon fi)
        {
            fi.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 88, 94, 102));
        }
        HideInstantTooltip();
    }

    private void TweakInfoIcon_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Border iconBorder || iconBorder.Tag is not DeclarativeTweakItem item) return;
        e.Handled = true;
        ShowInstantTooltip(iconBorder, item);
    }

    private void TweaksScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (InstantTooltipCard != null && InstantTooltipCard.Visibility == Visibility.Visible)
        {
            HideInstantTooltip();
        }
    }

    private void ShowInstantTooltip(FrameworkElement anchor, DeclarativeTweakItem item)
    {
        if (InstantTooltipCard == null || InstantTooltipTransform == null) return;

        _activeTooltipStoryboard?.Stop();

        InstantTooltipTitleText.Text = item.Title;
        InstantTooltipDescText.Text = item.TechnicalDescription;
        InstantTooltipKeyText.Text = item.WhatItTouches;

        InstantTooltipCard.Visibility = Visibility.Visible;
        InstantTooltipCard.Measure(new Size(300, 1000));

        double cardW = Math.Min(300, Math.Max(220, InstantTooltipCard.DesiredSize.Width));
        double cardH = Math.Max(68, InstantTooltipCard.DesiredSize.Height);

        InstantTooltipTransform.CenterX = cardW;
        InstantTooltipTransform.CenterY = cardH * 0.5;

        Point anchorPos = anchor.TransformToVisual(RootGrid).TransformPoint(new Point(0, 0));
        double left = anchorPos.X - cardW - 12;
        if (left < 16)
        {
            left = anchorPos.X + anchor.ActualWidth + 12;
        }

        double top = anchorPos.Y + (anchor.ActualHeight * 0.5) - (cardH * 0.5);
        double maxTop = Math.Max(16, RootGrid.ActualHeight - cardH - 16);
        top = Math.Clamp(top, 16, maxTop);

        Canvas.SetLeft(InstantTooltipCard, left);
        Canvas.SetTop(InstantTooltipCard, top);

        var sb = new Storyboard();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(140));

        var opacityAnim = new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = duration,
            EasingFunction = ease
        };
        Storyboard.SetTarget(opacityAnim, InstantTooltipCard);
        Storyboard.SetTargetProperty(opacityAnim, "Opacity");

        var translateYAnim = new DoubleAnimation
        {
            From = 6.0,
            To = 0.0,
            Duration = duration,
            EasingFunction = ease
        };
        Storyboard.SetTarget(translateYAnim, InstantTooltipTransform);
        Storyboard.SetTargetProperty(translateYAnim, "TranslateY");

        var scaleXAnim = new DoubleAnimation
        {
            From = 0.95,
            To = 1.0,
            Duration = duration,
            EasingFunction = ease
        };
        Storyboard.SetTarget(scaleXAnim, InstantTooltipTransform);
        Storyboard.SetTargetProperty(scaleXAnim, "ScaleX");

        var scaleYAnim = new DoubleAnimation
        {
            From = 0.95,
            To = 1.0,
            Duration = duration,
            EasingFunction = ease
        };
        Storyboard.SetTarget(scaleYAnim, InstantTooltipTransform);
        Storyboard.SetTargetProperty(scaleYAnim, "ScaleY");

        sb.Children.Add(opacityAnim);
        sb.Children.Add(translateYAnim);
        sb.Children.Add(scaleXAnim);
        sb.Children.Add(scaleYAnim);

        _activeTooltipStoryboard = sb;
        sb.Begin();
    }

    private void HideInstantTooltip()
    {
        if (InstantTooltipCard == null || InstantTooltipCard.Visibility == Visibility.Collapsed) return;

        _activeTooltipStoryboard?.Stop();

        var sb = new Storyboard();
        var opacityAnim = new DoubleAnimation
        {
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(85))
        };
        Storyboard.SetTarget(opacityAnim, InstantTooltipCard);
        Storyboard.SetTargetProperty(opacityAnim, "Opacity");
        sb.Children.Add(opacityAnim);

        sb.Completed += (_, _) =>
        {
            InstantTooltipCard.Visibility = Visibility.Collapsed;
        };

        _activeTooltipStoryboard = sb;
        sb.Begin();
    }

    private async Task ApplySelectedTweaksFlowAsync(string profileTitle)
    {
        var selected = App.TweakEngine.AllTweaks
            .Where(t => t.IsSelectedForBatch && t.IsApplicableBool)
            .ToList();

        if (selected.Count == 0)
        {
            await ShowSimpleInfoModalAsync("Sin módulos seleccionados", "No hay optimizaciones aplicables seleccionadas en este perfil.");
            return;
        }

        if (!App.LicenseService.PerformIntegrityAndAntiDebugCheck(out string integrityMsg))
        {
            await ShowSimpleInfoModalAsync("Verificación de integridad", integrityMsg);
            return;
        }

        bool needsReboot = selected.Any(t => t.RequiresReboot);
        var createRestorePointCheck = new CheckBox
        {
            Content = "Crear punto de restauración de Windows antes de aplicar cambios",
            IsChecked = true,
            Margin = new Thickness(0, 8, 0, 8)
        };

        var summaryBuilder = new StringBuilder();
        summaryBuilder.AppendLine($"Se aplicarán {selected.Count} optimizaciones de '{profileTitle}' validadas para este hardware:");
        summaryBuilder.AppendLine();
        foreach (var t in selected.Take(16))
        {
            string optInfo = t.HasOptions ? $" ({t.SelectedOptionKey})" : "";
            summaryBuilder.AppendLine($"· [{t.CodeNumber}] {t.Title}{optInfo}");
        }
        if (selected.Count > 16)
        {
            summaryBuilder.AppendLine($"... y {selected.Count - 16} módulos adicionales.");
        }
        summaryBuilder.AppendLine();
        summaryBuilder.AppendLine(needsReboot
            ? "Nota: Algunos cambios de kernel o BCD requerirán reiniciar Windows al finalizar."
            : "Todos los cambios seleccionados toman efecto sin necesidad de reinicio inmediato.");

        var modal1Panel = new StackPanel { Spacing = 8 };
        modal1Panel.Children.Add(new TextBlock
        {
            Text = summaryBuilder.ToString(),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 240, 242, 244))
        });
        modal1Panel.Children.Add(createRestorePointCheck);

        var modal1 = new ContentDialog
        {
            Title = $"Aplicar {profileTitle}",
            Content = modal1Panel,
            PrimaryButtonText = "Continuar y aplicar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };

        if (await modal1.ShowAsync() != ContentDialogResult.Primary)
        {
            SetStatus("Aplicación de perfil cancelada.");
            return;
        }

        var riskyItems = selected
            .Where(t => t.IsAnticheatUnsafeBool || t.ReducesSecurityBool)
            .ToList();

        if (riskyItems.Count > 0)
        {
            bool redConfirmed = await ShowRedSecurityWarningModalAsync(riskyItems);
            if (!redConfirmed)
            {
                SetStatus("Operación cancelada en la advertencia de seguridad/anticheat.");
                return;
            }
        }

        if (createRestorePointCheck.IsChecked == true)
        {
            SetStatus("Creando punto de restauración del sistema...");
            var (_, rpMsg) = await App.AuditService.CreateSystemRestorePointAsync($"KRUZH LATENCY — {profileTitle}");
            SetStatus(rpMsg);
        }

        int okCount = 0;
        int verifiedCount = 0;
        foreach (var item in selected)
        {
            var rep = App.TweakEngine.ExecuteApply(item);
            if (rep.Success) okCount++;
            if (rep.Verified) verifiedCount++;
        }

        RefreshLiveTweaksStateInPlace();
        RefreshJournalAndLicenseUi();

        string finalSummary = $"{profileTitle} aplicado: {okCount}/{selected.Count} módulos ejecutados ({verifiedCount} verificados en vivo).";
        SetStatus(finalSummary);
    }

    private async Task<bool> ShowRedSecurityWarningModalAsync(List<DeclarativeTweakItem> riskyItems)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ADVERTENCIA DE SEGURIDAD Y COMPATIBILIDAD ANTICHEAT");
        sb.AppendLine("Has seleccionado módulos que desactivan mitigaciones de kernel, aislamiento VBS/HVCI o protecciones del sistema:");
        sb.AppendLine();

        foreach (var r in riskyItems)
        {
            sb.AppendLine($"· [{r.CodeNumber}] {r.Title}");
            sb.AppendLine($"  {r.GateExplanationText}");
        }

        sb.AppendLine();
        sb.AppendLine("Riot Vanguard (Valorant) y FACEIT AC requieren TPM 2.0, Secure Boot, VBS y mitigaciones de kernel activas en Windows 11. Desactivarlas puede impedir que dichos juegos inicien hasta revertir desde el Journal.");

        var confirmCheck = new CheckBox
        {
            Content = "Comprendo la advertencia y deseo aplicar estos cambios bajo mi responsabilidad.",
            Margin = new Thickness(0, 10, 0, 0)
        };

        // Desaturated red (#B85F5F) ONLY on border and warning title, transparent background
        var mutedDangerBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 184, 95, 95));
        var warningBorder = new Border
        {
            Background = new SolidColorBrush(Colors.Transparent),
            BorderBrush = mutedDangerBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = sb.ToString(),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = mutedDangerBrush,
                        FontSize = 12
                    },
                    confirmCheck
                }
            }
        };

        await _dialogLock.WaitAsync();
        try
        {
            if (_activeContentDialog != null)
            {
                try { _activeContentDialog.Hide(); } catch { }
                await Task.Delay(100);
            }

            var warningDialog = new ContentDialog
            {
                Title = "Advertencia de seguridad y anticheat",
                Content = warningBorder,
                PrimaryButtonText = "Aplicar cambios",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = RootGrid.XamlRoot
            };
            _activeContentDialog = warningDialog;

            try
            {
                var res = await warningDialog.ShowAsync();
                return res == ContentDialogResult.Primary && confirmCheck.IsChecked == true;
            }
            finally
            {
                if (_activeContentDialog == warningDialog) _activeContentDialog = null;
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
        finally
        {
            _dialogLock.Release();
        }
    }

    private void RevertSelectedTweaksBtn_Click(object sender, RoutedEventArgs e)
    {
        var selected = App.TweakEngine.AllTweaks.Where(t => t.IsSelectedForBatch).ToList();
        int count = 0;
        foreach (var item in selected)
        {
            var rep = App.TweakEngine.ExecuteRevert(item);
            if (rep.Success) count++;
        }

        RefreshTweaksListUi();
        UpdateScoreRingAndCounters();
        RefreshJournalAndLicenseUi();
        SetStatus($"Se revirtieron {count} módulos seleccionados desde el Journal.");
    }

    // ========================================================================================
    // VIEW 3: IRQ, MSI MODE v3 & RESERVED CPU SETS
    // ========================================================================================
    private void RefreshPciAndMsiUi()
    {
        var devices = App.IrqMsiService.EnumeratePciDevices();
        PciDevicesItemsControl.ItemsSource = devices;

        var hw = App.HardwareService.CurrentProfile;
        if (hw.IsIntelHybrid || hw.IsDualCcdRyzen)
        {
            ReservedCpuSetsStatusText.Text =
                $"Compatible con {hw.CpuName}: máscara de núcleos reservados para tareas de fondo = 0x{hw.ReservedBackgroundCpuMask:X} (E-Cores / CCD secundario).";
        }
        else
        {
            ReservedCpuSetsStatusText.Text =
                $"No aplicable en {hw.CpuName}: procesador Single-CCD uniforme sin E-Cores. El motor omite el aislamiento para no restar hilos físicos al juego.";
        }
    }

    private void ScanPciDevicesBtn_Click(object sender, RoutedEventArgs e)
    {
        RefreshPciAndMsiUi();
        SetStatus("Bus PCI (HKLM\\SYSTEM\\CurrentControlSet\\Enum\\PCI) re-escaneado.");
    }

    private void ApplySafeMsiAllBtn_Click(object sender, RoutedEventArgs e)
    {
        var (_, summary) = App.IrqMsiService.ApplySafePciMsiMode();
        RefreshPciAndMsiUi();
        RefreshTweaksListUi();
        UpdateScoreRingAndCounters();
        SetStatus(summary);
    }

    private void ApplyOptimalIrqPinningBtn_Click(object sender, RoutedEventArgs e)
    {
        var (_, summary) = App.IrqMsiService.ApplyOptimalPciIrqAffinity();
        RefreshPciAndMsiUi();
        RefreshTweaksListUi();
        UpdateScoreRingAndCounters();
        SetStatus(summary);
    }

    private void RevertAllPciIrqMsiBtn_Click(object sender, RoutedEventArgs e)
    {
        App.AuditService.RevertTweakFromJournal("PciMsiModeV3", out string msiMsg);
        App.AuditService.RevertTweakFromJournal("PciIrqSmartBalancer", out string irqMsg);
        RefreshPciAndMsiUi();
        RefreshTweaksListUi();
        SetStatus($"PCI MSI/IRQ revertido: {msiMsg} · {irqMsg}");
    }

    private void EnableSinglePciMsiBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string pnpId) return;
        string msiPath = $@"SYSTEM\CurrentControlSet\Enum\{pnpId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
        string affPath = $@"SYSTEM\CurrentControlSet\Enum\{pnpId}\Device Parameters\Interrupt Management\Affinity Policy";

        App.AuditService.SetRegistryValue("PciMsiModeV3", Microsoft.Win32.RegistryHive.LocalMachine, msiPath, "MSISupported", 1, Microsoft.Win32.RegistryValueKind.DWord);
        App.AuditService.SetRegistryValue("PciMsiModeV3", Microsoft.Win32.RegistryHive.LocalMachine, affPath, "DevicePriority", 3, Microsoft.Win32.RegistryValueKind.DWord);

        RefreshPciAndMsiUi();
        SetStatus($"MSI Mode = 1 y DevicePriority = High aplicados en {pnpId}.");
    }

    // ========================================================================================
    // VIEW 4: RUNTIME POWER PLANS & REMOTE DRIVERS CATALOG
    // ========================================================================================
    private void RefreshPowerPlansUi()
    {
        PowerPlansItemsControl.ItemsSource = App.PowerService.GetDynamicPlanEvaluations();
    }

    private void ActivateDynamicPowerPlanBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not DynamicPowerPlanKind kind) return;
        var (_, msg) = App.PowerService.ApplyDynamicPowerPlan(kind);
        RefreshPowerPlansUi();
        UpdateScoreRingAndCounters();
        SetStatus(msg);
    }

    private void ImportCustomPowBtn_Click(object sender, RoutedEventArgs e)
    {
        string? powPath = Win32DialogService.ShowOpenFileDialog(
            GetWindowHandle(),
            "Seleccionar Plan de Energía (.pow)",
            "Archivos Power Plan (*.pow)\0*.pow\0Todos los archivos (*.*)\0*.*\0");

        if (string.IsNullOrWhiteSpace(powPath)) return;
        var (_, msg) = App.PowerService.ImportCustomPowFile(powPath);
        SetStatus(msg);
    }

    private void RestorePreviousPowerPlanBtn_Click(object sender, RoutedEventArgs e)
    {
        TransactionalAuditService.RunHiddenProcess("powercfg.exe", "/setactive 381b4222-f694-41f0-9685-ff5bb260df2e");
        SetStatus("Plan de energía Balanced estándar de Windows restaurado.");
    }

    private async Task RefreshDriversUiAsync()
    {
        var (gpuRec, nicRec, _) = await App.NvidiaDriversService.FetchHardwareGatedDriversAsync();
        _currentDrivers = new List<DriverCatalogueEntry>();
        if (gpuRec != null) _currentDrivers.Add(gpuRec);
        if (nicRec != null) _currentDrivers.Add(nicRec);
        DriversItemsControl.ItemsSource = _currentDrivers;
    }

    private async void RefreshDriversManifestBtn_Click(object sender, RoutedEventArgs e)
    {
        SetStatus("Consultando manifiesto de drivers...");
        await RefreshDriversUiAsync();
        SetStatus($"Catálogo de drivers actualizado ({_currentDrivers.Count} recomendaciones para tu hardware).");
    }

    private async void DownloadDriverBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string recVersion) return;
        var entry = _currentDrivers.FirstOrDefault(d => d.RecommendedVersion == recVersion);
        if (entry == null) return;

        SetStatus($"Iniciando descarga asistida de {entry.Vendor} {entry.RecommendedVersion}...");
        var (ok, localPath, sha256, msg) = await App.NvidiaDriversService.DownloadAndVerifyDriverAsync(entry);
        SetStatus(msg);

        if (ok && File.Exists(localPath))
        {
            var confirm = new ContentDialog
            {
                Title = "Driver descargado y verificado (SHA-256)",
                Content = $"Archivo: {localPath}\nSHA-256: {sha256}\n\n¿Deseás iniciar el instalador oficial ahora?",
                PrimaryButtonText = "Ejecutar instalador",
                CloseButtonText = "Cerrar",
                XamlRoot = RootGrid.XamlRoot
            };
            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
            {
                Process.Start(new ProcessStartInfo { FileName = localPath, UseShellExecute = true });
            }
        }
        else if (!string.IsNullOrWhiteSpace(entry.OfficialDownloadUrl))
        {
            Process.Start(new ProcessStartInfo { FileName = entry.OfficialDownloadUrl, UseShellExecute = true });
        }
    }

    // ========================================================================================
    // VIEW 5: FPS BOOST, CUSTOM GAMES & NVIDIA .NIP COMPLETE INJECTOR (HONEST PROGRAM 1:1)
    // ========================================================================================
    private void RefreshGamesAndNvidiaUi()
    {
        var all = App.TweakEngine.TargetGames;
        foreach (var g in all)
        {
            // Badge "OPT" y pestaña "Optimizados" según el estado REAL del sistema.
            try { g.IsLiveOptimized = App.GameOptimizer.IsAnyApplied(g); }
            catch (Exception ex) { App.LogError($"Game state {g.ExecutableName}", ex); g.IsLiveOptimized = false; }
        }
        IEnumerable<GameTargetItem> filtered = all;

        if (_currentGamesTab == "Installed")
        {
            filtered = filtered.Where(g => g.IsInstalled);
        }
        else if (_currentGamesTab == "Optimized")
        {
            filtered = filtered.Where(g => g.IsOptimized);
        }

        if (!string.IsNullOrWhiteSpace(_gamesSearchQuery))
        {
            filtered = filtered.Where(g =>
                (g.Name != null && g.Name.Contains(_gamesSearchQuery, StringComparison.OrdinalIgnoreCase)) ||
                (g.ShortName != null && g.ShortName.Contains(_gamesSearchQuery, StringComparison.OrdinalIgnoreCase)) ||
                (g.ExecutableName != null && g.ExecutableName.Contains(_gamesSearchQuery, StringComparison.OrdinalIgnoreCase)));
        }

        var list = filtered.ToList();
        GamesGridView.ItemsSource = null;
        GamesGridView.ItemsSource = list;
        GamesLibraryCountText.Text = $"TU BIBLIOTECA · {all.Count} JUEGOS";

        var hw = App.HardwareService.CurrentProfile;
        if (hw.GpuVendor == GpuVendorKind.Nvidia)
        {
            NvidiaSectionEligibilityText.Text = $"GPU NVIDIA detectada: {hw.GpuName} ({hw.GpuGeneration}) · Inyección de perfil .nip habilitada.";
            ApplyCustomNipBtn.IsEnabled = true;
        }
        else
        {
            NvidiaSectionEligibilityText.Text = $"No aplicable: Tu GPU es {hw.GpuName} ({hw.GpuVendor}). Los perfiles .nip exclusivos de NVIDIA permanecen deshabilitados.";
            ApplyCustomNipBtn.IsEnabled = false;
        }
    }

    private void TabGames_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        _currentGamesTab = tag;

        TabGamesAll.Foreground = (SolidColorBrush)Application.Current.Resources["KruzhTextSecondaryBrush"];
        TabGamesAll.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        TabGamesAll.FontWeight = Microsoft.UI.Text.FontWeights.Normal;

        TabGamesInstalled.Foreground = (SolidColorBrush)Application.Current.Resources["KruzhTextSecondaryBrush"];
        TabGamesInstalled.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        TabGamesInstalled.FontWeight = Microsoft.UI.Text.FontWeights.Normal;

        TabGamesOptimized.Foreground = (SolidColorBrush)Application.Current.Resources["KruzhTextSecondaryBrush"];
        TabGamesOptimized.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        TabGamesOptimized.FontWeight = Microsoft.UI.Text.FontWeights.Normal;

        btn.Foreground = (SolidColorBrush)Application.Current.Resources["KruzhTextPrimaryBrush"];
        btn.BorderBrush = (SolidColorBrush)Application.Current.Resources["KruzhTextPrimaryBrush"];
        btn.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;

        RefreshGamesAndNvidiaUi();
    }

    private void GamesSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _gamesSearchQuery = GamesSearchTextBox.Text.Trim();
        RefreshGamesAndNvidiaUi();
    }

    // Hover de las cards de juego (y de "Agregar juego"): borde más claro + se eleva 3px. Sin el fondo gris del GridView.
    private static readonly SolidColorBrush GameCardHoverBorderBrush = new(Windows.UI.Color.FromArgb(255, 90, 90, 90));
    private static readonly SolidColorBrush AddCardIdleBorderBrush = new(Windows.UI.Color.FromArgb(255, 51, 51, 51));
    private static readonly SolidColorBrush AddCardHoverBackgroundBrush = new(Windows.UI.Color.FromArgb(255, 20, 20, 20));
    private static readonly SolidColorBrush TransparentBrush = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    private void GameCard_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement el) SetHandCursor(el);
    }

    private void GameCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border card) return;
        card.BorderBrush = GameCardHoverBorderBrush;
        card.Translation = new System.Numerics.Vector3(0, -3, 0);
        if (ReferenceEquals(card, AddGameCard))
        {
            card.Background = AddCardHoverBackgroundBrush;
            AddGameIcon.Foreground = (Brush)Application.Current.Resources["KruzhTextPrimaryBrush"];
            AddGameText.Foreground = (Brush)Application.Current.Resources["KruzhTextPrimaryBrush"];
        }
    }

    private void GameCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border card) return;
        card.Translation = System.Numerics.Vector3.Zero;
        if (ReferenceEquals(card, AddGameCard))
        {
            card.BorderBrush = AddCardIdleBorderBrush;
            card.Background = TransparentBrush;
            AddGameIcon.Foreground = (Brush)Application.Current.Resources["KruzhTextTertiaryBrush"];
            AddGameText.Foreground = (Brush)Application.Current.Resources["KruzhTextTertiaryBrush"];
        }
        else
        {
            card.BorderBrush = (Brush)Application.Current.Resources["KruzhHairlineBorderBrush"];
        }
    }

    private void GamesGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not GameTargetItem game) return;
        _selectedGameForModal = game;

        ModalGameTitleText.Text = game.Name;
        try
        {
            if (uint.TryParse(game.GradientStartHex.TrimStart('#'), System.Globalization.NumberStyles.HexNumber, null, out uint hexVal))
            {
                byte a = 255;
                byte r = (byte)((hexVal >> 16) & 0xFF);
                byte g = (byte)((hexVal >> 8) & 0xFF);
                byte b = (byte)(hexVal & 0xFF);
                ModalHeroBackground.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(a, r, g, b));
            }
        }
        catch { }

        if (!string.IsNullOrEmpty(game.CoverImagePath) && File.Exists(game.CoverImagePath))
        {
            ModalMiniCoverImage.Source = new BitmapImage(new Uri(game.CoverImagePath));
        }
        else
        {
            ModalMiniCoverImage.Source = null;
        }

        // Los toggles muestran lo que REALMENTE está aplicado; si no hay nada aplicado, la recomendación (todo ON).
        bool anyApplied = App.GameOptimizer.IsAnyApplied(game);
        ModalIfeoPriorityToggle.IsOn = !anyApplied || App.GameOptimizer.IsHighPriorityApplied(game);
        ModalQosToggle.IsOn = !anyApplied || App.GameOptimizer.IsQosApplied(game);

        // Preferencia de GPU: solo con 2+ GPUs físicas (iGPU + dedicada). Con una sola GPU no hace nada.
        bool multiGpu = GameOptimizationService.HasMultiplePhysicalGpus();
        ModalGpuPrefRow.Visibility = multiGpu ? Visibility.Visible : Visibility.Collapsed;
        ModalGpuPrefToggle.IsOn = multiGpu && (!anyApplied || App.GameOptimizer.IsGpuPreferenceApplied(game));

        ModalExePathText.Text = string.IsNullOrEmpty(game.FullExePath) ? game.ExecutableName : game.FullExePath;
        ModalAnticheatNoteText.Text = game.IsInstalled
            ? "Compatible con FACEIT AC y Vanguard: solo prioridad por registro (IFEO) y QoS. No toca archivos ni memoria del juego."
            : "No encontramos el ejecutable. Elegilo con «Seleccionar» para poder aplicar.";
        ModalApplyBtn.IsEnabled = game.IsInstalled;
        GameOptimizationModalOverlay.Visibility = Visibility.Visible;
    }

    private void CloseGameModal_Click(object sender, RoutedEventArgs e)
    {
        GameOptimizationModalOverlay.Visibility = Visibility.Collapsed;
        _selectedGameForModal = null;
    }

    private void CloseGameModal_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        GameOptimizationModalOverlay.Visibility = Visibility.Collapsed;
        _selectedGameForModal = null;
    }

    private void ModalChangeCoverBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGameForModal == null) return;

        string? imgPath = Win32DialogService.ShowOpenFileDialog(
            GetWindowHandle(),
            "Seleccionar portada del juego (JPG/PNG)",
            "Imágenes (*.jpg;*.jpeg;*.png;*.webp)\0*.jpg;*.jpeg;*.png;*.webp\0Todos los archivos (*.*)\0*.*\0");

        if (string.IsNullOrWhiteSpace(imgPath) || !File.Exists(imgPath)) return;

        _selectedGameForModal.CoverImagePath = imgPath;
        ModalMiniCoverImage.Source = new BitmapImage(new Uri(imgPath));
        RefreshGamesAndNvidiaUi();
        SetStatus($"Portada actualizada para '{_selectedGameForModal.Name}'.");
    }

    private void ModalSelectExeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGameForModal == null) return;

        string? exePath = Win32DialogService.ShowOpenFileDialog(
            GetWindowHandle(),
            $"Seleccionar ejecutable para {_selectedGameForModal.Name} (.exe)",
            "Ejecutable (*.exe)\0*.exe\0");

        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return;

        _selectedGameForModal.FullExePath = exePath;
        _selectedGameForModal.ExecutableName = Path.GetFileName(exePath);
        ModalExePathText.Text = exePath;
        RefreshGamesAndNvidiaUi();
        SetStatus($"Ruta de ejecutable actualizada: '{exePath}'.");
    }

    private async void ModalApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGameForModal == null) return;
        var game = _selectedGameForModal;
        bool priority = ModalIfeoPriorityToggle.IsOn;
        bool qos = ModalQosToggle.IsOn;
        bool gpuPref = ModalGpuPrefRow.Visibility == Visibility.Visible && ModalGpuPrefToggle.IsOn;

        ModalApplyBtn.IsEnabled = false;
        ModalRevertBtn.IsEnabled = false;
        try
        {
            var (ok, detail) = await Task.Run(() => App.GameOptimizer.Apply(game, priority, qos, gpuPref));
            SetStatus(detail);
            if (!ok)
            {
                await ShowSimpleInfoModalAsync("No se pudo aplicar", detail);
            }
        }
        finally
        {
            ModalApplyBtn.IsEnabled = true;
            ModalRevertBtn.IsEnabled = true;
            GameOptimizationModalOverlay.Visibility = Visibility.Collapsed;
            _selectedGameForModal = null;
            RefreshGamesAndNvidiaUi();
            RefreshJournalAndLicenseUi();
        }
    }

    private async void ModalRevertBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGameForModal == null) return;
        var game = _selectedGameForModal;

        ModalApplyBtn.IsEnabled = false;
        ModalRevertBtn.IsEnabled = false;
        try
        {
            var (ok, detail) = await Task.Run(() => App.GameOptimizer.Revert(game));
            SetStatus(detail);
            if (!ok)
            {
                await ShowSimpleInfoModalAsync("No se pudo revertir", detail);
            }
        }
        finally
        {
            ModalApplyBtn.IsEnabled = true;
            ModalRevertBtn.IsEnabled = true;
            GameOptimizationModalOverlay.Visibility = Visibility.Collapsed;
            _selectedGameForModal = null;
            RefreshGamesAndNvidiaUi();
            RefreshJournalAndLicenseUi();
        }
    }

    private void AddCustomGameExeBtn_Click(object sender, RoutedEventArgs e)
    {
        string? exePath = Win32DialogService.ShowOpenFileDialog(
            GetWindowHandle(),
            "Seleccionar ejecutable del juego (.exe)",
            "Ejecutable (*.exe)\0*.exe\0");

        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return;

        string exeName = Path.GetFileName(exePath);
        if (App.TweakEngine.TargetGames.Any(g => string.Equals(g.ExecutableName, exeName, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus($"El ejecutable '{exeName}' ya figura en la lista.");
            return;
        }

        App.TweakEngine.TargetGames.Add(new GameTargetItem
        {
            Name = Path.GetFileNameWithoutExtension(exePath),
            ShortName = Path.GetFileNameWithoutExtension(exePath).ToUpperInvariant(),
            ExecutableName = exeName,
            FullExePath = exePath
        });

        RefreshGamesAndNvidiaUi();
        SetStatus($"Ejecutable '{exeName}' agregado.");
    }

    private void RemoveCustomGameBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string exeName) return;
        var existing = App.TweakEngine.TargetGames.FirstOrDefault(g => string.Equals(g.ExecutableName, exeName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            App.TweakEngine.TargetGames.Remove(existing);
            RefreshGamesAndNvidiaUi();
            SetStatus($"Ejecutable '{exeName}' quitado.");
        }
    }

    private void ApplyAllGamesBoostBtn_Click(object sender, RoutedEventArgs e)
    {
        string[] gameTweakIds = { "GameIfeoHighPriority", "QosDscp46Games", "SteamBackgroundGpuAccelOff" };
        int applied = 0;
        foreach (string id in gameTweakIds)
        {
            var t = App.TweakEngine.AllTweaks.FirstOrDefault(x => x.Id == id);
            if (t != null)
            {
                var rep = App.TweakEngine.ExecuteApply(t);
                if (rep.Success) applied++;
            }
        }
        RefreshLiveTweaksStateInPlace();
        SetStatus($"Optimización por juego aplicada a {App.TweakEngine.TargetGames.Count} ejecutables ({applied} módulos activos).");
    }

    private void ApplyCustomNipBtn_Click(object sender, RoutedEventArgs e)
    {
        var (_, msg) = App.NvidiaDriversService.ApplyCompleteNvidiaNipProfile(
            forceP2Off: NipForceP2OffCheck.IsChecked == true,
            disableTelemetry: NipDisableTelemetryCheck.IsChecked == true,
            enableReflexLowLatency: NipUltraLowLatencyCheck.IsChecked == true,
            gsyncCompetitiveOff: NipDisableGsyncCheck.IsChecked == true);

        SetStatus(msg);
    }

    private void RestoreBaseNipBtn_Click(object sender, RoutedEventArgs e)
    {
        string? inspector = App.NvidiaDriversService.FindNvidiaProfileInspectorExe();
        string baseNip = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Data", "Base Profile.nip");
        if (!string.IsNullOrEmpty(inspector) && File.Exists(baseNip))
        {
            TransactionalAuditService.RunHiddenProcess(inspector, $"\"{baseNip}\" -silent");
            SetStatus("Base Profile.nip restaurado en el driver NVIDIA.");
        }
        else
        {
            SetStatus("No se encontró nvidiaProfileInspector.exe o Base Profile.nip en Assets.");
        }
    }

    private void ClearShaderCacheBtn_Click(object sender, RoutedEventArgs e)
    {
        var (_, msg) = App.NvidiaDriversService.ClearShaderCaches();
        SetStatus(msg);
    }

    // ========================================================================================
    // VIEW 6: BIOS / NVRAM ADVISORY (SCEWIN MANUAL + SHA-256 VERIFIER — HONEST PROGRAM 1:1)
    // ========================================================================================
    private void InitializeBiosNvramUi()
    {
        var hw = App.HardwareService.CurrentProfile;
        BiosMotherboardText.Text = string.IsNullOrEmpty(hw.Motherboard) ? "Placa base del sistema" : hw.Motherboard;
        BiosVersionText.Text = string.IsNullOrEmpty(hw.BiosVersion) ? "Desconocida" : hw.BiosVersion;

        var (isAmi, msg) = App.NvramService.CheckAmiAptioCompatibility();
        BiosScewinCompatibilityText.Text = isAmi ? "Compatible ✓" : "Incompatible ✕";
        BiosScewinCompatibilityText.Foreground = isAmi
            ? (SolidColorBrush)Application.Current.Resources["KruzhPositiveBrush"]
            : (SolidColorBrush)Application.Current.Resources["KruzhDangerMutedBrush"];

        // Spec 001 F9: no se carga nada automáticamente ni se muestra ningún setting hasta que el usuario importa su nvram.txt.
        ShowNvramEmptyState(
            "Todavía no importaste tu nvram.txt",
            "Exportalo con SCEWIN y arrastralo acá o usá «Importar nvram.txt». Solo vas a ver los settings que existen en tu BIOS.");
    }

    private void ShowNvramEmptyState(string title, string detail)
    {
        _currentNvramItems = new List<NvramCheckItem>();
        NvramFindingsItemsControl.ItemsSource = null;
        NvramResultsPanel.Visibility = Visibility.Collapsed;
        NvramEmptyStatePanel.Visibility = Visibility.Visible;
        NvramEmptyStateTitle.Text = title;
        NvramEmptyStateDetail.Text = detail;
        BiosParamsFoundText.Text = "—";
        BiosOptimosSugeridosText.Text = "Importá tu nvram.txt";
    }

    private void LoadAndDisplayNvramFile(string filePath)
    {
        List<NvramCheckItem> items;
        try
        {
            items = App.NvramService.AnalyzeNvramFile(filePath);
        }
        catch (Exception ex)
        {
            App.LogError("NVRAM import", ex);
            ShowNvramEmptyState("No se pudo leer el archivo", $"{Path.GetFileName(filePath)}: {ex.Message}");
            return;
        }

        if (items.Count == 0)
        {
            ShowNvramEmptyState(
                "No encontramos settings conocidos en este archivo",
                $"{Path.GetFileName(filePath)} no contiene ninguna «Setup Question» de nuestra base de datos. Verificá que sea el nvram.txt exportado por SCEWIN.");
            return;
        }

        _currentNvramItems = items;
        NvramFindingsItemsControl.ItemsSource = null;
        NvramFindingsItemsControl.ItemsSource = _currentNvramItems;
        NvramEmptyStatePanel.Visibility = Visibility.Collapsed;
        NvramResultsPanel.Visibility = Visibility.Visible;

        int needsChange = _currentNvramItems.Count(x => x.NeedsChange);
        int optimal = _currentNvramItems.Count - needsChange;
        BiosParamsFoundText.Text = _currentNvramItems.Count.ToString();
        BiosOptimosSugeridosText.Text = $"{optimal} bien · {needsChange} a revisar";
    }

    private async void ViewCurrentBiosConfigBtn_Click(object sender, RoutedEventArgs e)
    {
        var hw = App.HardwareService.CurrentProfile;
        string detail = $"Información de Firmware detectada:\n\n" +
                        $"• Placa base: {hw.Motherboard}\n" +
                        $"• Versión BIOS: {hw.BiosVersion}\n" +
                        $"• Procesador: {hw.CpuName} ({hw.Architecture})\n" +
                        $"• Modo de reloj: {hw.ClockingMode}\n" +
                        $"• fTPM / TPM 2.0: {(hw.SecureBootAndTpmPresent ? "Activo (Requerido anticheats)" : "Inactivo")}\n\n" +
                        $"Para analizar los parámetros binarios exactos de tu placa, exportá nvram.txt con SCEWIN_64.exe e importalo aquí.";
        await ShowSimpleInfoModalAsync("Configuración actual de BIOS", detail);
    }

    private void OpenScewinFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        TransactionalAuditService.RunHiddenProcess("explorer.exe", $"\"{baseDir}\"");
        SetStatus($"Carpeta de aplicación abierta: {baseDir}");
    }

    private async void DownloadScewinBtn_Click(object sender, RoutedEventArgs e)
    {
        string instructions = "AMI SCEWIN (Aptio V / IV) es una utilidad de American Megatrends que exporta las variables de setup de la BIOS a un archivo de texto.\n\n" +
                              "Cómo exportar tu nvram.txt (solo lectura):\n" +
                              "1. Abrí Terminal / CMD como Administrador en la carpeta de SCEWIN.\n" +
                              "2. Ejecutá:  SCEWIN_64.exe /o /s nvram.txt\n" +
                              "3. Guardá una copia en un pendrive como backup.\n" +
                              "4. Importá ese nvram.txt en KRUZH LATENCY para ver qué settings conviene cambiar.\n\n" +
                              "KRUZH LATENCY nunca escribe en la BIOS: los cambios los aplicás vos desde el setup de la BIOS.";
        await ShowSimpleInfoModalAsync("AMI SCEWIN — Cómo exportar nvram.txt", instructions);
    }

    private void ImportNvramFileBtn_Click(object sender, RoutedEventArgs e)
    {
        string? filePath = Win32DialogService.ShowOpenFileDialog(
            GetWindowHandle(),
            "Importar archivo nvram.txt exportado por AMI SCEWIN",
            "Archivos NVRAM SCEWIN (*.txt)\0*.txt\0Todos los archivos (*.*)\0*.*\0");

        if (string.IsNullOrWhiteSpace(filePath)) return;
        LoadAndDisplayNvramFile(filePath);
        SetStatus($"Archivo NVRAM '{filePath}' analizado.");
    }

    private void NvramPanel_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Soltar nvram.txt para analizar";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void NvramPanel_Drop(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.FirstOrDefault() is StorageFile file && file.Path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                LoadAndDisplayNvramFile(file.Path);
                SetStatus($"Archivo '{file.Name}' importado.");
            }
        }
    }

    private async void VerifyScewinHashBtn_Click(object sender, RoutedEventArgs e)
    {
        string? exePath = Win32DialogService.ShowOpenFileDialog(
            GetWindowHandle(),
            "Seleccionar SCEWIN_64.exe para verificar SHA-256",
            "Ejecutable SCEWIN (*.exe)\0*.exe\0");

        if (string.IsNullOrWhiteSpace(exePath)) return;
        var (_, detail) = App.NvramService.VerifyScewinBinarySha256(exePath);
        SetStatus(detail);
        await ShowSimpleInfoModalAsync("Verificación SHA-256 (SCEWIN)", detail);
    }

    // ========================================================================================
    // VIEW 7: REALTIME MONITOR (100% LIVE OS SENSORS), AUDIT JOURNAL & COMMERCIAL LICENSE
    // ========================================================================================
    private void TelemetryTimer_Tick(object? sender, object e)
    {
        _telemetryTickCounter++;

        var (curTimerMs, _, _) = App.MonitorScoreService.QueryTimerResolutionMs();
        var (dpcPct, cpuPct) = App.MonitorScoreService.SampleCpuAndKernelLoad();
        double gpuPct = App.MonitorScoreService.SampleGpuUsagePercent();
        var (ramUsedGb, ramTotalGb, ramPct) = App.MonitorScoreService.SampleRamUsage();

        DashboardCpuUsageCardText.Text = $"{cpuPct:F1} %";
        DashboardGpuUsageCardText.Text = $"{gpuPct:F1} %";
        DashboardRamUsageCardText.Text = $"{ramUsedGb:F1} / {ramTotalGb:F0} GB";
        DashboardRamSubtitleText.Text = $"{ramPct:F0}% en uso ({(App.HardwareService.CurrentProfile.IsDualChannel ? "Dual Channel" : "Single Channel")})";
        DashboardTimerCardText.Text = $"{curTimerMs:F3} ms";
        DashboardDpcCardText.Text = $"{dpcPct:F2} %";

        bool dpcIsHealthy = dpcPct <= 3.5;
        DpcStatusPillText.Text = dpcIsHealthy ? "Estable" : "Alerta";
        DpcStatusPillText.Foreground = new SolidColorBrush(dpcIsHealthy
            ? Windows.UI.Color.FromArgb(255, 99, 192, 136)
            : Windows.UI.Color.FromArgb(255, 184, 95, 95));
        DpcStatusPillBorder.Background = new SolidColorBrush(dpcIsHealthy
            ? Windows.UI.Color.FromArgb(20, 99, 192, 136)
            : Windows.UI.Color.FromArgb(20, 184, 95, 95));
        DpcStatusPillBorder.BorderBrush = new SolidColorBrush(dpcIsHealthy
            ? Windows.UI.Color.FromArgb(51, 99, 192, 136)
            : Windows.UI.Color.FromArgb(51, 184, 95, 95));

        // Every 3 seconds, poll live ReadState() of tweaks to catch any non-watched external OS changes
        if (_telemetryTickCounter % 3 == 0 && !_isInitializingUi)
        {
            RefreshLiveTweaksStateInPlace();
        }

        double currentScore = 0;
        if (double.TryParse(DashboardScoreValueText.Text.Replace("%", "").Trim(), out double parsedScore))
        {
            currentScore = parsedScore;
        }

        _cpuSamples.Add(cpuPct);
        _gpuSamples.Add(gpuPct);
        _ramSamples.Add(ramPct);
        _timerSamples.Add(curTimerMs);
        _dpcSamples.Add(dpcPct);
        _scoreSamples.Add(currentScore);

        if (_cpuSamples.Count > 60) _cpuSamples.RemoveAt(0);
        if (_gpuSamples.Count > 60) _gpuSamples.RemoveAt(0);
        if (_ramSamples.Count > 60) _ramSamples.RemoveAt(0);
        if (_timerSamples.Count > 60) _timerSamples.RemoveAt(0);
        if (_dpcSamples.Count > 60) _dpcSamples.RemoveAt(0);
        if (_scoreSamples.Count > 60) _scoreSamples.RemoveAt(0);

        if (ViewDashboard.Visibility == Visibility.Visible)
        {
            RenderHomeSparklines();
        }
        else if (ViewMonitorAudit.Visibility == Visibility.Visible)
        {
            double p99Dpc = _dpcSamples.OrderByDescending(x => x).FirstOrDefault();
            double avgDpc = _dpcSamples.Average();
            LiveMonitorStatsText.Text = $"Timer: {curTimerMs:F3} ms · CPU: {cpuPct:F1}% · GPU: {gpuPct:F1}% · DPC/ISR actual: {dpcPct:F2}% · Promedio: {avgDpc:F2}% · Pico P99: {p99Dpc:F2}%";
            RenderTelemetryChartPolylines();
        }
    }

    private void RenderHomeSparklines()
    {
        int count = _dpcSamples.Count;
        if (count == 0) return;

        double w = 310;
        double h = 20;

        if (count == 1)
        {
            // Duplicate single initial sample so the sparkline renders immediately at startup
            HomeCpuSparkline.Points = BuildSparklinePoints(new List<double> { _cpuSamples[0], _cpuSamples[0] }, 100.0, w, h);
            HomeGpuSparkline.Points = BuildSparklinePoints(new List<double> { _gpuSamples[0], _gpuSamples[0] }, 100.0, w, h);
            HomeRamSparkline.Points = BuildSparklinePoints(new List<double> { _ramSamples[0], _ramSamples[0] }, 100.0, w, h);
            HomeTimerSparkline.Points = BuildSparklinePoints(new List<double> { _timerSamples[0], _timerSamples[0] }, 2.0, w, h);
            HomeDpcSparkline.Points = BuildSparklinePoints(new List<double> { _dpcSamples[0], _dpcSamples[0] }, 10.0, w, h);
            HomeScoreSparkline.Points = BuildSparklinePoints(new List<double> { _scoreSamples[0], _scoreSamples[0] }, 100.0, w, h);
            return;
        }

        HomeCpuSparkline.Points = BuildSparklinePoints(_cpuSamples, 100.0, w, h);
        HomeGpuSparkline.Points = BuildSparklinePoints(_gpuSamples, 100.0, w, h);
        HomeRamSparkline.Points = BuildSparklinePoints(_ramSamples, 100.0, w, h);
        HomeTimerSparkline.Points = BuildSparklinePoints(_timerSamples, 2.0, w, h);
        HomeDpcSparkline.Points = BuildSparklinePoints(_dpcSamples, 10.0, w, h);
        HomeScoreSparkline.Points = BuildSparklinePoints(_scoreSamples, 100.0, w, h);
    }

    private static PointCollection BuildSparklinePoints(List<double> samples, double maxScale, double w, double h)
    {
        var pts = new PointCollection();
        int count = samples.Count;
        double stepX = w / Math.Max(1, count - 1);
        for (int i = 0; i < count; i++)
        {
            double x = i * stepX;
            double norm = Math.Clamp(samples[i] / maxScale, 0.0, 1.0);
            double y = h - (norm * (h - 6)) - 3;
            pts.Add(new Point(x, y));
        }
        return pts;
    }

    private void RenderTelemetryChartPolylines()
    {
        double width = LiveTelemetryCanvas.ActualWidth > 100 ? LiveTelemetryCanvas.ActualWidth : 900;
        double height = 120;

        var dpcPoints = new PointCollection();
        var timerPoints = new PointCollection();

        int count = _dpcSamples.Count;
        if (count < 2) return;

        double stepX = width / Math.Max(1, count - 1);
        for (int i = 0; i < count; i++)
        {
            double x = i * stepX;
            double dpcNorm = Math.Clamp(_dpcSamples[i] / 20.0, 0.0, 1.0);
            double yDpc = height - (dpcNorm * (height - 10)) - 5;
            dpcPoints.Add(new Point(x, yDpc));

            double timerNorm = Math.Clamp(_timerSamples[i] / 2.0, 0.0, 1.0);
            double yTimer = height - (timerNorm * (height - 10)) - 5;
            timerPoints.Add(new Point(x, yTimer));
        }

        DpcChartPolyline.Points = dpcPoints;
        TimerChartPolyline.Points = timerPoints;
    }

    private void QuickTestInterruptModMediumBtn_Click(object sender, RoutedEventArgs e)
    {
        var item = App.TweakEngine.AllTweaks.FirstOrDefault(t => t.Id == "NicInterruptModerationConfigurable");
        if (item != null)
        {
            item.SelectedOptionKey = "Medium";
            var rep = App.TweakEngine.ExecuteApply(item);
            RefreshLiveTweaksStateInPlace();
            SetStatus($"Prueba en vivo: {rep.Message}");
        }
    }

    private void QuickTestInterruptModOffBtn_Click(object sender, RoutedEventArgs e)
    {
        var item = App.TweakEngine.AllTweaks.FirstOrDefault(t => t.Id == "NicInterruptModerationConfigurable");
        if (item != null)
        {
            item.SelectedOptionKey = "Off";
            var rep = App.TweakEngine.ExecuteApply(item);
            RefreshLiveTweaksStateInPlace();
            SetStatus($"Prueba en vivo: {rep.Message}");
        }
    }

    private void RefreshJournalAndLicenseUi()
    {
        var history = App.AuditService.GetAuditHistory();
        JournalEntriesItemsControl.ItemsSource = history;

        HwidReadOnlyBox.Text = App.LicenseService.CurrentHwid;
        bool licensed = App.LicenseService.IsLicensedAndValid;
        HeaderLicenseText.Text = licensed ? "Licencia activa" : "Sin activar";
        HeaderLicenseDot.Background = new SolidColorBrush(licensed
            ? Windows.UI.Color.FromArgb(255, 99, 192, 136)
            : Windows.UI.Color.FromArgb(255, 88, 94, 102));

        LicenseDetailsStatusText.Text = licensed && App.LicenseService.ActiveToken != null
            ? $"Licencia activa · Titular: {App.LicenseService.ActiveToken.CustomerName} · Edición: {App.LicenseService.ActiveToken.Edition} · Vence: {App.LicenseService.ActiveToken.ExpiresAtUtc:yyyy-MM-dd}"
            : "Ingresá tu clave comercial KRUZH-XXXX-XXXX-XXXX (o KRUZH-AUTHOR-MASTER-2026) para vincular este HWID.";
    }

    private void OpenJournalFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KruzhLatency",
            "Journal");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    private async void RevertAllFromJournalBtn_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            Title = "Revertir todos los cambios desde el Journal",
            Content = "¿Deseás restaurar todos los valores originales registrados en el Journal de Auditoría? No se eliminará ninguna rama del registro.",
            PrimaryButtonText = "Revertir todo",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        App.AuditService.RevertAllFromJournal(out string summary);
        App.TweakEngine.RefreshAll();
        RefreshTweaksListUi();
        UpdateScoreRingAndCounters();
        RefreshJournalAndLicenseUi();
        SetStatus(summary);
    }

    private async void ActivateLicenseBtn_Click(object sender, RoutedEventArgs e)
    {
        string key = LicenseKeyInputBox.Text;
        var (ok, msg) = await App.LicenseService.ActivateLicenseKeyAsync(key);
        RefreshJournalAndLicenseUi();
        SetStatus(msg);
        await ShowSimpleInfoModalAsync(ok ? "Licencia activada" : "Error de activación", msg);
    }

    private async void ShowEulaModalBtn_Click(object sender, RoutedEventArgs e)
    {
        await ShowEulaDialogAsync(requireAcceptance: false);
    }

    private async Task ShowEulaDialogAsync(bool requireAcceptance)
    {
        string eulaText =
            "ACUERDO DE LICENCIA DE USUARIO FINAL (EULA) — KRUZH LATENCY\n" +
            "Autor: KRUZH — author\n\n" +
            "1. ALCANCE TÉCNICO: KRUZH LATENCY aplica ajustes declarativos de bajo nivel sobre el registro de Windows, políticas de interrupción PCI (MSI/IRQ), planes de energía y análisis asistido de NVRAM.\n" +
            "2. REVERSIBILIDAD: El motor de Triple Puerta y el Journal Transaccional registran el valor previo exacto de cada parámetro antes de modificarlo.\n" +
            "3. MÓDULO BIOS/SCEWIN: La exportación de nvram_optimizado.txt es estrictamente asesora y manual. El usuario se compromete a exportar previamente su backup original.\n" +
            "4. LICENCIA VINCULADA A HARDWARE (HWID): Licencia comercial personal e intransferible.";

        var dialog = new ContentDialog
        {
            Title = "Acuerdo de licencia y responsabilidad (EULA)",
            Content = new ScrollViewer
            {
                MaxHeight = 320,
                Content = new TextBlock
                {
                    Text = eulaText,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12
                }
            },
            PrimaryButtonText = "Aceptar términos",
            CloseButtonText = requireAcceptance ? null : "Cerrar",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };

        await dialog.ShowAsync();
        App.LicenseService.AcceptEula();
    }

    private async void QuickRestorePointBtn_Click(object sender, RoutedEventArgs e)
    {
        SetStatus("Creando punto de restauración del sistema...");
        var (_, msg) = await App.AuditService.CreateSystemRestorePointAsync("KRUZH LATENCY — Manual Checkpoint");
        SetStatus(msg);
        await ShowSimpleInfoModalAsync("Punto de restauración de Windows", msg);
    }

    private static readonly System.Threading.SemaphoreSlim _dialogLock = new(1, 1);
    private static ContentDialog? _activeContentDialog;

    private async Task ShowSimpleInfoModalAsync(string title, string message)
    {
        await _dialogLock.WaitAsync();
        try
        {
            if (_activeContentDialog != null)
            {
                try { _activeContentDialog.Hide(); } catch { }
                await Task.Delay(100);
            }

            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                CloseButtonText = "Aceptar",
                XamlRoot = RootGrid.XamlRoot
            };
            _activeContentDialog = dialog;

            try
            {
                await dialog.ShowAsync();
            }
            finally
            {
                if (_activeContentDialog == dialog) _activeContentDialog = null;
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Ignore single ContentDialog COMException safely
        }
        finally
        {
            _dialogLock.Release();
        }
    }

    private void SetStatus(string text)
    {
        if (BottomStatusBarText != null)
        {
            BottomStatusBarText.Text = $"{DateTime.Now:HH:mm:ss} · {text}";
        }
    }
}
