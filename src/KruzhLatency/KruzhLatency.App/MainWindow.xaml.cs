using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using KruzhLatency.Controls;
using KruzhLatency.Core.Latency;
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
        FitAllViewsToViewport();

        _telemetryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _telemetryTimer.Tick += TelemetryTimer_Tick;

        this.Activated += MainWindow_Activated;
        // La sesión ETW del kernel sobrevive al proceso si no se detiene: se libera al cerrar.
        this.Closed += (_, _) => { try { _dpcSession?.Dispose(); } catch { } };
        RootGrid.Loaded += RootGrid_Loaded;

        // Spec 003 / G5: quién retoma cada tipo de medición después de un reinicio.
        AutoTuneSessionService.Runners[AutoMsiService.Kind] = RunAutoMsiAsync;
        AutoTuneSessionService.Runners[AutoAffinityService.Kind] = RunAutoAffinityAsync;
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

    // ========================================================================================
    // ANCHO DEL CONTENIDO = ANCHO VISIBLE
    // Si algún hijo pedía más ancho que la ventana, el ScrollViewer armaba un área más ancha y centraba el panel
    // (MaxWidth + Stretch) ahí: la vista quedaba corrida a la derecha y cortada (BIOS, Biblioteca, Monitor), y al
    // abrir/cerrar el sidebar quedaba con el ancho viejo. Fijando Width = min(MaxWidth, ancho visible) en cada
    // cambio de tamaño, el área nunca supera la ventana.
    // ========================================================================================
    private void FitAllViewsToViewport()
    {
        foreach (var sv in new[] { ViewDashboard, TweaksScrollViewer, ViewIrqMsi, ViewPowerDrivers, GamesScrollViewer, ViewBiosNvram, ViewMonitorAudit, ViewDisplay, ViewConfig })
        {
            FitContentToViewport(sv);
        }
    }

    private static void FitContentToViewport(ScrollViewer? sv)
    {
        if (sv == null) return;
        sv.HorizontalScrollMode = ScrollMode.Disabled;
        sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;

        void Fit()
        {
            if (sv.Content is not FrameworkElement content) return;
            double viewport = sv.ViewportWidth > 0 ? sv.ViewportWidth : sv.ActualWidth;
            if (viewport <= 0) return;
            double max = double.IsInfinity(content.MaxWidth) || content.MaxWidth <= 0 ? double.PositiveInfinity : content.MaxWidth;
            double target = Math.Min(max, viewport);
            if (Math.Abs(content.Width - target) > 0.5 || double.IsNaN(content.Width))
            {
                content.Width = target;
            }
        }

        sv.SizeChanged += (_, _) => Fit();
        sv.Loaded += (_, _) => Fit();
        sv.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => Fit());
        Fit();
    }

    // Sidebar en modo "Left" (empuja el contenido). WinUI lo cierra sin animación: se intercepta el cierre,
    // se anima el ancho del pane hasta el ancho compacto (el contenido acompaña) y recién ahí se cierra.
    private bool _paneCloseAnimationDone;
    private const double PaneOpenLength = 240;
    private const int PaneCloseMs = 200;

    private void AnimatePaneClose()
    {
        try
        {
            var anim = new DoubleAnimation
            {
                From = PaneOpenLength,
                To = MainNavView.CompactPaneLength,
                Duration = new Duration(TimeSpan.FromMilliseconds(PaneCloseMs)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(anim, MainNavView);
            Storyboard.SetTargetProperty(anim, "OpenPaneLength");
            var sb = new Storyboard();
            sb.Children.Add(anim);
            sb.Completed += (_, _) =>
            {
                _paneCloseAnimationDone = true;
                MainNavView.IsPaneOpen = false;
                sb.Stop();
                MainNavView.OpenPaneLength = PaneOpenLength;
                _paneCloseAnimationDone = false;
            };
            sb.Begin();
        }
        catch (Exception ex)
        {
            App.LogError("Pane close animation", ex);
            _paneCloseAnimationDone = true;
            MainNavView.IsPaneOpen = false;
            _paneCloseAnimationDone = false;
        }
    }

    // Sidebar: el pane se abre con la animación de recorte de WinUI, pero la marca y el texto de licencia pasaban
    // de Collapsed a Visible de golpe (y como el pane tiene el mismo color que el contenido, eso era lo único que se
    // veía: un "pop"). Ahora acompañan la apertura con un fundido y se van con un fundido corto al cerrar.
    private const int PaneFadeInMs = 220;
    private const int PaneFadeInDelayMs = 90;
    private const int PaneFadeOutMs = 90;

    private void MainNavView_PaneOpening(NavigationView sender, object args)
    {
        FadePaneElement(PaneBrandHeaderPanel, show: true);
        FadePaneElement(HeaderLicenseText, show: true);
    }

    private void MainNavView_PaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args)
    {
        if (!_paneCloseAnimationDone && sender.PaneDisplayMode == NavigationViewPaneDisplayMode.Left
            && sender.DisplayMode == NavigationViewDisplayMode.Expanded)
        {
            // Primero el cierre animado; el cierre real lo dispara AnimatePaneClose al terminar.
            args.Cancel = true;
            FadePaneElement(PaneBrandHeaderPanel, show: false);
            FadePaneElement(HeaderLicenseText, show: false);
            AnimatePaneClose();
            return;
        }
        if (!_paneCloseAnimationDone)
        {
            FadePaneElement(PaneBrandHeaderPanel, show: false);
            FadePaneElement(HeaderLicenseText, show: false);
        }
    }

    private static void FadePaneElement(UIElement? element, bool show)
    {
        if (element == null) return;
        try
        {
            if (show)
            {
                element.Opacity = 0;
                element.Visibility = Visibility.Visible;
            }

            var fade = new DoubleAnimation
            {
                To = show ? 1 : 0,
                BeginTime = show ? TimeSpan.FromMilliseconds(PaneFadeInDelayMs) : TimeSpan.Zero,
                Duration = new Duration(TimeSpan.FromMilliseconds(show ? PaneFadeInMs : PaneFadeOutMs)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fade, element);
            Storyboard.SetTargetProperty(fade, "Opacity");

            var sb = new Storyboard();
            sb.Children.Add(fade);
            if (!show)
            {
                // Al terminar el fundido se colapsa: en modo compacto (48px) no debe quedar texto recortado.
                sb.Completed += (_, _) => element.Visibility = Visibility.Collapsed;
            }
            sb.Begin();
        }
        catch
        {
            element.Opacity = 1;
            element.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
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

            // ADR-009: si hay un EDID esperando confirmación (se aplicó y se reinició), preguntar ya.
            _ = ConfirmPendingEdidAsync();

            // Spec 003 / G5: si una medición quedó a mitad (reinicio), ofrecer retomarla o cancelarla.
            _ = ResumePendingAutoTuneAsync();

            // Spec 003: resultado guardado antes de reabrir KRUZH (tras reiniciar el driver de video).
            if (KruzhLatency.Infra.Benchmark.AutoTuneSessionStore.TakeReport() is { } pendingReport)
            {
                _ = ShowSimpleInfoModalAsync(pendingReport.Title, pendingReport.Text);
            }

            // T11: si el journal estaba dañado, avisar (nunca en silencio).
            switch (App.AuditService.JournalLoadOutcome)
            {
                case KruzhLatency.Core.Journal.JournalLoadOutcome.RecoveredFromBackup:
                    SetStatus("El registro de cambios estaba dañado y se recuperó de su copia de seguridad.");
                    break;
                case KruzhLatency.Core.Journal.JournalLoadOutcome.CorruptStartedEmpty:
                    _ = ShowSimpleInfoModalAsync("Registro de cambios dañado",
                        "El registro de cambios de KRUZH y su copia estaban dañados. Se guardó una copia del archivo y se empezó uno nuevo: " +
                        "los cambios aplicados antes no se pueden revertir desde KRUZH (usá un punto de restauración de Windows si hace falta).");
                    break;
            }

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
        ViewDisplay.Visibility = targetTag == "Display" ? Visibility.Visible : Visibility.Collapsed;
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
            "Display" => "Pantalla",
            "Config" => "Configuración",
            _ => "Inicio"
        };

        if (targetTag == "MonitorAudit" || targetTag == "Config")
        {
            RefreshJournalAndLicenseUi();
        }
        else if (targetTag == "IrqMsi")
        {
            RefreshGpuPageUi();
        }
        else if (targetTag == "Tweaks")
        {
            RefreshPciAndMsiUi(); // "Interrupciones (avanzado)" vive al final de Optimizaciones
        }
        else if (targetTag == "GamesNvidia")
        {
            RefreshGamesAndNvidiaUi();
        }
        else if (targetTag == "Display")
        {
            _ = RefreshDisplayUiAsync();
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

    /// <summary>
    /// Relee el estado real de todos los ítems EN SEGUNDO PLANO y lo vuelca en la UI de una vez (antes se leía todo
    /// —inventario de dispositivos, WMI, AppX, BCD— en el hilo de UI y la ventana se tildaba al navegar).
    /// </summary>
    private async Task RefreshStateAsync(bool rerenderList)
    {
        try
        {
            await App.TweakEngine.RefreshAllAsync(DispatcherQueue, apply =>
            {
                bool previous = _isHandlingToggleChange;
                _isHandlingToggleChange = true; // las propiedades enlazadas cambian: que los toggles no lo tomen como clic
                try { apply(); }
                finally { _isHandlingToggleChange = previous; }
            });
            if (rerenderList) RefreshTweaksListUi();
            UpdateScoreRingAndCounters();
            UpdateTweaksSummarySubtitle();
            UpdatePendingChangesBar();
        }
        catch (Exception ex)
        {
            App.LogError("Refresco de estado", ex);
        }
    }

    /// <summary>Refresco en el lugar (sin rearmar la lista): las filas se actualizan por binding. Nunca bloquea la UI.</summary>
    private void RefreshLiveTweaksStateInPlace() => _ = RefreshStateAsync(rerenderList: false);

    // ---------------------------------------------------------------- pestañas de Optimizaciones
    private string _currentOptTab = "Tweaks";

    private void OptTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        _currentOptTab = tag;
        foreach (var b in new[] { OptTabTweaksBtn, OptTabServicesBtn, OptTabDevicesBtn, OptTabAppsBtn })
        {
            bool active = ReferenceEquals(b, btn);
            b.Foreground = (Brush)Application.Current.Resources[active ? "KruzhTextPrimaryBrush" : "KruzhTextSecondaryBrush"];
            b.BorderBrush = active ? (Brush)Application.Current.Resources["KruzhTextPrimaryBrush"] : TransparentBrush;
            b.FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
        DeviceRescueBtn.Visibility = tag == "Devices" ? Visibility.Visible : Visibility.Collapsed;
        RefreshTweaksListUi();
    }

    /// <summary>B10 / ADR-007: reactiva todo lo que KRUZH deshabilitó (no lo que deshabilitó el usuario) y ofrece reiniciar.</summary>
    private async void DeviceRescueBtn_Click(object sender, RoutedEventArgs e)
    {
        bool go = await ShowConfirmModalAsync("Rescate de dispositivos",
            "Se vuelven a habilitar todos los dispositivos que deshabilitó KRUZH (los que deshabilitaste vos antes no se tocan). " +
            "Usalo si algo dejó de funcionar después de aplicar el Device Manager. Después conviene reiniciar.",
            "Rescatar");
        if (!go) return;

        (int Enabled, IReadOnlyList<string> Failed) result = (0, Array.Empty<string>());
        await RunStepsWithOverlayAsync("Rescate", "Habilitando dispositivos", new[]
        {
            new OverlayStep("Habilitar lo que deshabilitó KRUZH", () =>
            {
                result = App.AuditService.RescueDisabledDevices();
                return result.Failed.Count == 0
                    ? (true, $"{result.Enabled} dispositivos habilitados.")
                    : (false, $"{result.Enabled} habilitados; fallaron {result.Failed.Count}: {string.Join(", ", result.Failed)}");
            })
        });

        RefreshLiveTweaksStateInPlace();
        if (result.Enabled > 0 &&
            await ShowConfirmModalAsync("Reiniciar", $"Se habilitaron {result.Enabled} dispositivos. ¿Reiniciar ahora para que todo arranque limpio?", "Reiniciar"))
        {
            Process.Start(new ProcessStartInfo { FileName = "shutdown.exe", Arguments = "/r /t 5", CreateNoWindow = true, UseShellExecute = false });
        }
        else if (result.Enabled == 0 && result.Failed.Count == 0)
        {
            SetStatus("No hay dispositivos deshabilitados por KRUZH.");
        }
    }

    private bool IsInCurrentOptTab(DeclarativeTweakItem t) => _currentOptTab switch
    {
        "Services" => t.Category == TweakCategory.Services,
        "Devices" => t.Category == TweakCategory.Devices,
        "Apps" => t.Category == TweakCategory.Apps,
        _ => t.Category != TweakCategory.Services && t.Category != TweakCategory.Devices && t.Category != TweakCategory.Apps
    };

    private void UpdateOptTabNote(TweakCategory? tab, int shown)
    {
        if (tab == null)
        {
            OptTabNoteText.Visibility = Visibility.Collapsed;
            return;
        }

        OptTabNoteText.Visibility = Visibility.Visible;
        OptTabNoteText.Text = tab == TweakCategory.Services
            ? (shown == 0
                ? "No encontramos servicios para optimizar en esta PC."
                : $"Analizamos los servicios de tu PC: estos {shown} se pueden desactivar sin afectar el juego. " +
                  $"Los que usan FACEIT AC, Vanguard, la red, el audio y Windows ({DeclarativeTweakEngine.ProtectedServices.Count} protegidos) no se tocan nunca.")
            : tab == TweakCategory.Apps
                ? (shown == 0
                    ? "No encontramos apps ni publicidad para quitar en esta PC."
                    : $"Apps instaladas que no hacen falta para jugar, publicidad y telemetría ({shown}). Las apps se quitan solo para tu usuario " +
                      "y se pueden revertir; la Store, los códecs, la seguridad y los paneles de drivers no se tocan nunca.")
                : (shown == 0
                    ? "No encontramos dispositivos para desactivar en esta PC."
                    : $"Analizamos el Administrador de dispositivos: estos {shown} no se usan para jugar y se pueden deshabilitar. " +
                      "Se aplica al reiniciar y se revierte desde Monitor.");
    }

    private void UpdateTweaksSummarySubtitle()
    {
        if (TweaksSummaryCounterText == null) return;
        // El contador refleja la pestaña actual (Tweaks / Servicios / Device Manager).
        var inTab = App.TweakEngine.AllTweaks.Where(IsInCurrentOptTab).ToList();
        int totalApplicable = inTab.Count(t => t.IsApplicableBool);
        int liveApplied = inTab.Count(t => t.IsApplicableBool && t.IsLiveApplied);
        int hiddenByHw = inTab.Count(t => !t.IsApplicableBool);
        TweaksSummaryCounterText.Text =
            hiddenByHw > 0
                ? $"{liveApplied} de {totalApplicable} activas · {hiddenByHw} no aplican a tu PC y no se muestran."
                : $"{liveApplied} de {totalApplicable} activas.";
    }

    private async void RefreshSystemStateBtn_Click(object sender, RoutedEventArgs e)
    {
        App.HardwareService.DetectHardwareProfile();
        await RefreshStateAsync(rerenderList: false);
        PopulateHardwareAndDashboardUi();
        RefreshTweaksListUi();
        RefreshPowerPlansUi();
        SetStatus("Estado real del sistema (ReadState + Verify) actualizado.");
    }

    private async void CpuClockingModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingUi) return;
        if (CpuClockingModeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            var newMode = tag == "StaticAllCore"
                ? CpuClockingMode.StaticAllCoreOverclock
                : CpuClockingMode.DynamicBoostPbo;

            App.HardwareService.SetClockingModeOverride(newMode);
            await RefreshStateAsync(rerenderList: false);

            PopulateHardwareAndDashboardUi();
            RefreshTweaksListUi();
            RefreshPowerPlansUi();
            InitializeBiosNvramUi();
            SetStatus($"Régimen de CPU actualizado: {App.HardwareService.CurrentProfile.ClockingModeDescription}");
        }
    }

    private async void MousePollingHzCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingUi) return;
        if (MousePollingHzCombo.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag &&
            int.TryParse(tag, out int hz))
        {
            App.HardwareService.SetMousePollingRate(hz);

            await RefreshStateAsync(rerenderList: false);
            RefreshTweaksListUi();
            SetStatus($"Polling rate ajustado a {hz} Hz.");
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
            await RefreshStateAsync(rerenderList: false);

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
                ("Mouse e input",         t => t.Category == TweakCategory.MouseInput),
                ("Procesador y sistema",  t => t.Category == TweakCategory.SystemCpu),
                ("GPU y pantalla",        t => t.Category == TweakCategory.GpuDisplay),
                ("Red",                   t => t.Category == TweakCategory.NetworkDesync),
                ("Interrupciones y audio", t => t.Category == TweakCategory.IrqMsiAudio),
                ("Energía, USB y memoria", t => t.Category == TweakCategory.UsbPowerBus || t.Category == TweakCategory.StorageMemory),
                ("Juegos",                t => t.Category == TweakCategory.FpsBoostGames)
            };

            // Pestaña actual: Tweaks (categorías clásicas) · Servicios · Device Manager (secciones por CategoryDisplay).
            var tabCategory = _currentOptTab switch
            {
                "Services" => (TweakCategory?)TweakCategory.Services,
                "Devices" => TweakCategory.Devices,
                "Apps" => TweakCategory.Apps,
                _ => null
            };
            if (tabCategory != null)
            {
                var tabItems = filtered.Where(t => t.Category == tabCategory).ToList();
                categoryOrder = tabItems
                    .Select(t => t.CategoryDisplay)
                    .Distinct()
                    .Select(section => (section, (Func<DeclarativeTweakItem, bool>)(t => t.Category == tabCategory && t.CategoryDisplay == section)))
                    .ToArray();
            }

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
            InterruptsExpander.Visibility = tabCategory == null ? Visibility.Visible : Visibility.Collapsed;
            UpdateOptTabNote(tabCategory, groups.Sum(g => g.Items.Count));
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
                ? "1 cambio pendiente · se crea un punto de restauración antes de aplicar"
                : $"{pendingCount} cambios pendientes · se crea un punto de restauración antes de aplicar";
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

        if (!await ConfirmRiskyAsync(toApply)) return;

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
            OverlayTitleText.Text = "Aplicando cambios";
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

    // ========================================================================================
    // RUNNER COMÚN CON OVERLAY: toda acción que toca el sistema muestra el box-loader + carrusel y su resultado.
    // ========================================================================================

    /// <summary>Un paso visible en el carrusel. <see cref="Run"/> se ejecuta en background y nunca debe tocar la UI.</summary>
    private sealed record OverlayStep(string Title, Func<(bool Success, string Detail)> Run, bool RequiresReboot = false);

    private sealed record OverlayStepResult(OverlayStep Step, bool Success, string Detail);

    /// <summary>
    /// Ejecuta los pasos uno por uno mostrando el overlay (cajas + carrusel + barra). Cada paso corre en background,
    /// un fallo no corta el resto, y al terminar el carrusel muestra el resultado. Devuelve el resultado de cada paso.
    /// </summary>
    private async Task<IReadOnlyList<OverlayStepResult>> RunStepsWithOverlayAsync(string heading, string subtitle, IReadOnlyList<OverlayStep> steps, bool showFailures = true)
    {
        var results = new List<OverlayStepResult>();
        if (steps.Count == 0 || _isApplyingBatch) return results;

        _isApplyingBatch = true;
        _telemetryTimer.Stop();
        try
        {
            ApplyingChangesOverlay.Visibility = Visibility.Visible;
            StartOverlaySpinnerAnimation();
            OverlayTitleText.Text = heading;
            OverlayRestorePointText.Text = subtitle;
            OverlayProgressBar.Value = 0;
            OverlayCountText.Text = $"0 / {steps.Count}";
            OverlayStepDescText.Text = heading + "…";

            string Title(int i) => i >= 0 && i < steps.Count ? steps[i].Title : (i == steps.Count ? "(Fin)" : string.Empty);
            SetCarouselRows("(Inicio)", string.Empty, false, Title(0), Title(1), Title(2));

            string lastTitle = "(Inicio)";
            string lastStatus = string.Empty;
            bool lastFailed = false;

            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                var watch = Stopwatch.StartNew();
                if (i > 0)
                {
                    int idx = i;
                    string pt = lastTitle, ps = lastStatus;
                    bool pf = lastFailed;
                    await SlideCarouselUpAsync(Title(idx + 1), () => SetCarouselRows(pt, ps, pf, Title(idx), Title(idx + 1), Title(idx + 2)));
                }

                (bool ok, string detail) outcome;
                try
                {
                    outcome = await Task.Run(step.Run);
                }
                catch (Exception ex)
                {
                    App.LogError($"Overlay step {step.Title}", ex);
                    outcome = (false, $"Error inesperado: {ex.Message}");
                }

                results.Add(new OverlayStepResult(step, outcome.ok, outcome.detail));
                lastTitle = step.Title;
                lastStatus = outcome.ok ? "listo ✓" : "falló ✗";
                lastFailed = !outcome.ok;
                OverlayProgressBar.Value = (double)(i + 1) / steps.Count * 100.0;
                OverlayCountText.Text = $"{i + 1} / {steps.Count}";

                int remaining = MinCarouselStepMs - (int)watch.ElapsedMilliseconds;
                if (remaining > 0) await Task.Delay(remaining);
            }

            int failed = results.Count(r => !r.Success);
            bool needsReboot = results.Any(r => r.Success && r.Step.RequiresReboot);
            string final = failed == 0
                ? (needsReboot ? "Listo · reiniciá la PC para que tome efecto" : "¡Listo!")
                : $"Terminado con {failed} error(es)";
            string ft = lastTitle, fs = lastStatus;
            bool ff = lastFailed;
            await SlideCarouselUpAsync(string.Empty, () =>
            {
                SetCarouselRows(ft, fs, ff, final, string.Empty, string.Empty);
                CarouselActiveStatusText.Text = string.Empty;
                CarouselNextStatusText.Text = string.Empty;
            });
            OverlayStepDescText.Text = failed == 0 ? "Cambios verificados." : "Revisá el detalle de los errores.";
            await Task.Delay(900);
        }
        finally
        {
            ApplyingChangesOverlay.Visibility = Visibility.Collapsed;
            StopOverlaySpinnerAnimation();
            _isApplyingBatch = false;
            _telemetryTimer.Start();
        }

        var failures = results.Where(r => !r.Success).ToList();
        if (showFailures && failures.Count > 0)
        {
            await ShowSimpleInfoModalAsync(
                $"{failures.Count} acción(es) no se completaron",
                string.Join("\n", failures.Select(f => $"• {f.Step.Title}: {f.Detail}")) +
                "\n\nDetalle técnico en %LOCALAPPDATA%\\KruzhLatency\\errors.log");
        }
        SetStatus(failures.Count == 0
            ? $"{heading}: {results.Count} acción(es) completadas."
            : $"{heading}: {results.Count - failures.Count} completadas, {failures.Count} con error.");
        return results;
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

        if (!await ConfirmRiskyAsync(selected)) return;

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

    /// <summary>
    /// B9: TODO ítem que puede romper un anticheat o baja la seguridad pide confirmación antes de aplicarse,
    /// venga del lote, de un perfil, de un toggle suelto o de un botón de acción. true = seguir.
    /// </summary>
    private async Task<bool> ConfirmRiskyAsync(IEnumerable<DeclarativeTweakItem> items)
    {
        var risky = items.Where(t => t.IsAnticheatUnsafeBool || t.ReducesSecurityBool).ToList();
        if (risky.Count == 0) return true;
        bool ok = await ShowRedSecurityWarningModalAsync(risky);
        if (!ok) SetStatus("Operación cancelada en la advertencia de seguridad/anticheat.");
        return ok;
    }

    private Task<bool> ShowRedSecurityWarningModalAsync(List<DeclarativeTweakItem> riskyItems)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Estos cambios bajan la seguridad del sistema o pueden hacer que FACEIT AC o Vanguard no dejen jugar:");
        sb.AppendLine();
        foreach (var r in riskyItems)
        {
            string why = r.IsAnticheatUnsafeBool && !string.IsNullOrWhiteSpace(r.Anticheat.WarningMessage) ? $" — {r.Anticheat.WarningMessage}" : "";
            sb.AppendLine($"• {r.Title}{why}");
        }
        sb.AppendLine();
        sb.Append("Si un anticheat no te deja entrar, revertilos desde Monitor → Cambios aplicados. ¿Aplicarlos igual?");
        return ShowConfirmModalAsync("Atención: seguridad y anticheat", sb.ToString(), "Aplicar igual");
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
    // VIEW 3: GPU — ajustes de driver de la GPU detectada + perfil del driver (.nip) + interrupciones (avanzado)
    // ========================================================================================
    /// <summary>Recorre Enum\PCI del registro: en segundo plano para no trabar la navegación.</summary>
    private async void RefreshPciAndMsiUi()
    {
        try
        {
            var devices = await Task.Run(() => App.IrqMsiService.EnumeratePciDevices());
            PciDevicesItemsControl.ItemsSource = devices;
        }
        catch (Exception ex)
        {
            App.LogError("PCI/MSI", ex);
        }
    }

    /// <summary>Tweaks de driver que se muestran en la página GPU (solo los aplicables a la GPU detectada).</summary>
    private static readonly string[] GpuDriverTweakIds = { "GpuHardwareSchedulingHags", "GpuDynamicP0State", "AmdRadeonUlpsOff" };

    private const string GpuProfileRegistryValue = "NvidiaDriverProfile";
    private bool _isApplyingGpuSetting;

    private void RefreshGpuPageUi()
    {
        var hw = App.HardwareService.CurrentProfile;
        bool isNvidia = hw.GpuVendor == GpuVendorKind.Nvidia;
        bool isAmd = hw.GpuVendor == GpuVendorKind.Amd;

        GpuVendorBadgeText.Text = hw.GpuVendor switch
        {
            GpuVendorKind.Nvidia => "NVIDIA",
            GpuVendorKind.Amd => "AMD",
            GpuVendorKind.Intel => "INTEL",
            _ => "GPU"
        };
        GpuDetectedNameText.Text = string.IsNullOrWhiteSpace(hw.GpuName) ? "GPU no detectada" : hw.GpuName;
        GpuDetectedDetailText.Text = string.IsNullOrWhiteSpace(hw.GpuDriverVersion) || hw.GpuDriverVersion == "N/A"
            ? "GPU detectada"
            : $"GPU detectada · driver {hw.GpuDriverVersion}";

        SetGpuVendorTab(GpuTabNvidia, GpuTabNvidiaText, GpuTabNvidiaLock, GpuTabNvidiaUnderline, isNvidia);
        SetGpuVendorTab(GpuTabAmd, GpuTabAmdText, GpuTabAmdLock, GpuTabAmdUnderline, isAmd);

        // Ajustes del driver: solo los aplicables (HAGS para todas; P0 solo NVIDIA; ULPS solo AMD — lo deciden los gates).
        _isHandlingToggleChange = true;
        try
        {
            var settings = App.TweakEngine.AllTweaks
                .Where(t => GpuDriverTweakIds.Contains(t.Id) && t.IsApplicableBool)
                .ToList();
            GpuDriverSettingsItemsControl.ItemsSource = null;
            GpuDriverSettingsItemsControl.ItemsSource = settings;
            GpuDriverSettingsEmptyText.Visibility = settings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _isHandlingToggleChange = false;
        }

        // Perfil del driver: solo NVIDIA (Profile Inspector).
        GpuProfileCardsGrid.Opacity = isNvidia ? 1 : 0.35;
        GpuProfileCardsGrid.IsHitTestVisible = isNvidia;
        GpuProfileSubtitleText.Text = isNvidia ? "Seleccioná un perfil por vez" : "Los perfiles de driver son exclusivos de NVIDIA.";
        UpdateGpuProfileSelectionUi(isNvidia ? ReadSavedGpuProfile() : null);
    }

    private static void SetGpuVendorTab(Border tab, TextBlock text, UIElement lockPanel, UIElement underline, bool isActive)
    {
        text.Foreground = (Brush)Application.Current.Resources[isActive ? "KruzhTextPrimaryBrush" : "KruzhTextTertiaryBrush"];
        lockPanel.Visibility = isActive ? Visibility.Collapsed : Visibility.Visible;
        underline.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        tab.Background = isActive ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 14, 14, 14)) : TransparentBrush;
    }

    /// <summary>Paso de overlay que APLICA un tweak del catálogo y verifica releyendo el estado real.</summary>
    private static OverlayStep ApplyTweakStep(DeclarativeTweakItem item)
    {
        string optionKey = item.SelectedOptionKey; // se lee en UI; el paso corre en background
        return new OverlayStep(item.Title, () =>
        {
            var (ok, detail) = DeclarativeTweakEngine.RunApplyAction(item, optionKey);
            if (!ok) return (false, detail);
            bool verified;
            try { verified = item.ReadLiveState(); } catch { verified = false; }
            return (true, verified
                ? "Aplicado y verificado."
                : (item.RequiresReboot ? "Aplicado: toma efecto al reiniciar." : "Aplicado, pero no se pudo verificar el estado."));
        }, item.RequiresReboot);
    }

    /// <summary>Paso de overlay que REVIERTE un tweak del catálogo a sus valores originales (journal).</summary>
    private static OverlayStep RevertTweakStep(DeclarativeTweakItem item) =>
        new(item.Title, () => App.TweakEngine.RunRevertActions(item), item.RequiresReboot);

    private async void GpuDriverToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isHandlingToggleChange || _isApplyingGpuSetting) return;
        if (sender is not ToggleSwitch ts || ts.Tag is not string id) return;
        var item = App.TweakEngine.AllTweaks.FirstOrDefault(t => t.Id == id);
        if (item == null || ts.IsOn == item.IsLiveApplied) return;

        _isApplyingGpuSetting = true;
        try
        {
            if (ts.IsOn)
            {
                var early = App.TweakEngine.PreCheckApply(item);
                if (early != null)
                {
                    SetStatus(early.Message);
                    return;
                }
                if (!await ConfirmRiskyAsync(new[] { item }))
                {
                    _isHandlingToggleChange = true;
                    ts.IsOn = false;
                    _isHandlingToggleChange = false;
                    return;
                }
                await RunStepsWithOverlayAsync("Aplicando cambios", "Ajuste del driver", new[] { ApplyTweakStep(item) });
            }
            else
            {
                await RunStepsWithOverlayAsync("Revirtiendo cambios", "Volviendo al valor original", new[] { RevertTweakStep(item) });
            }
        }
        catch (Exception ex)
        {
            App.LogError($"GPU toggle {id}", ex);
        }
        finally
        {
            _isApplyingGpuSetting = false;
            RefreshLiveTweaksStateInPlace();
            RefreshGpuPageUi();
            RefreshJournalAndLicenseUi();
        }
    }

    private static readonly SolidColorBrush GpuProfileIdleBorder = new(Windows.UI.Color.FromArgb(255, 42, 42, 42));
    private static readonly SolidColorBrush GpuProfileHoverBorder = new(Windows.UI.Color.FromArgb(255, 90, 90, 90));
    private static readonly SolidColorBrush GpuProfileSelectedBorder = new(Windows.UI.Color.FromArgb(255, 242, 242, 242));
    private string? _selectedGpuProfile;

    private void GpuProfileCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b && !IsSelectedGpuCard(b)) b.BorderBrush = GpuProfileHoverBorder;
    }

    private void GpuProfileCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b && !IsSelectedGpuCard(b)) b.BorderBrush = GpuProfileIdleBorder;
    }

    private bool IsSelectedGpuCard(Border card) =>
        string.Equals(card.Tag as string,
            _selectedGpuProfile != null && _selectedGpuProfile.StartsWith("Custom:", StringComparison.Ordinal) ? "Custom" : _selectedGpuProfile,
            StringComparison.Ordinal);

    private void UpdateGpuProfileSelectionUi(string? selected)
    {
        _selectedGpuProfile = selected;
        foreach (var (card, check) in new[]
        {
            (GpuProfileCompetitive, GpuProfileCompetitiveCheck),
            (GpuProfileCompetitiveGsync, GpuProfileCompetitiveGsyncCheck),
            (GpuProfileDefault, GpuProfileDefaultCheck),
            (GpuProfileCustom, GpuProfileCustomCheck)
        })
        {
            string? normalized = selected != null && selected.StartsWith("Custom:", StringComparison.Ordinal) ? "Custom" : selected;
            bool isSel = string.Equals(card.Tag as string, normalized, StringComparison.Ordinal);
            card.BorderBrush = isSel ? GpuProfileSelectedBorder : GpuProfileIdleBorder;
            check.Background = isSel ? GpuProfileSelectedBorder : TransparentBrush;
            if (check.Child is UIElement tick) tick.Visibility = isSel ? Visibility.Visible : Visibility.Collapsed;
        }

        GpuProfileStatusText.Text = selected switch
        {
            "Competitive" => "Competitivo aplicado.",
            "CompetitiveGsync" => "Competitivo + G-SYNC aplicado.",
            "Default" => "Perfil base de NVIDIA (predeterminado).",
            { } s when s.StartsWith("Custom:", StringComparison.Ordinal) => $"Perfil personalizado aplicado: {s[7..]}.",
            _ => "Ningún perfil seleccionado."
        };
    }

    private async void GpuProfileCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Border card || card.Tag is not string profile) return;
        if (App.HardwareService.CurrentProfile.GpuVendor != GpuVendorKind.Nvidia) return;
        if (_isApplyingGpuSetting) return;

        string title;
        Func<(bool Success, string Detail)> run;
        string profileToSave = profile;

        if (profile == "Custom")
        {
            // Importar un .nip propio (exportado desde NVIDIA Profile Inspector).
            string? nipPath = Win32DialogService.ShowOpenFileDialog(
                GetWindowHandle(),
                "Importar perfil de NVIDIA (.nip)",
                "Perfil NVIDIA Profile Inspector (*.nip)\0*.nip\0");
            if (string.IsNullOrWhiteSpace(nipPath) || !File.Exists(nipPath)) return;
            title = $"Perfil personalizado · {Path.GetFileNameWithoutExtension(nipPath)}";
            run = () => ImportNvidiaProfileFile(nipPath);
            profileToSave = $"Custom:{Path.GetFileNameWithoutExtension(nipPath)}";
        }
        else
        {
            if (profile == _selectedGpuProfile) return;
            (title, run) = profile switch
            {
                "Competitive" => ("Perfil Competitivo", (Func<(bool, string)>)(() => App.NvidiaDriversService.ApplyCompleteNvidiaNipProfile(
                    forceP2Off: true, disableTelemetry: true, enableReflexLowLatency: true, gsyncCompetitiveOff: true))),
                "CompetitiveGsync" => ("Perfil Competitivo + G-SYNC", () => App.NvidiaDriversService.ApplyCompleteNvidiaNipProfile(
                    forceP2Off: true, disableTelemetry: true, enableReflexLowLatency: true, gsyncCompetitiveOff: false)),
                _ => ("Perfil predeterminado de NVIDIA", () => ImportNvidiaProfileFile(Path.Combine(AppContext.BaseDirectory, "Assets", "Tools", "Base Profile.nip")))
            };
        }

        _isApplyingGpuSetting = true;
        try
        {
            var results = await RunStepsWithOverlayAsync("Aplicando perfil del driver", "NVIDIA Profile Inspector", new[] { new OverlayStep(title, run) });
            if (results.Count == 1 && results[0].Success)
            {
                SaveGpuProfile(profileToSave);
                UpdateGpuProfileSelectionUi(profileToSave);
            }
        }
        catch (Exception ex)
        {
            App.LogError($"GPU profile {profile}", ex);
        }
        finally
        {
            _isApplyingGpuSetting = false;
        }
    }

    /// <summary>
    /// Importa un .nip en el driver con NVIDIA Profile Inspector y valida exit code + timeout.
    /// (El "Predeterminado" buscaba Base Profile.nip en Assets\Data; vive en Assets\Tools.)
    /// </summary>
    private static (bool Success, string Message) ImportNvidiaProfileFile(string nipPath)
    {
        string? inspector = App.NvidiaDriversService.FindNvidiaProfileInspectorExe();
        if (string.IsNullOrEmpty(inspector)) return (false, "No se encontró nvidiaProfileInspector.exe en Assets\\Tools.");
        if (!File.Exists(nipPath)) return (false, $"No se encontró el archivo {Path.GetFileName(nipPath)}.");

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = inspector,
                Arguments = $"\"{nipPath}\" -silent",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            if (proc == null) return (false, "No se pudo iniciar NVIDIA Profile Inspector.");
            if (!proc.WaitForExit(15000))
            {
                try { proc.Kill(); } catch { }
                return (false, "NVIDIA Profile Inspector no respondió a tiempo.");
            }
            return proc.ExitCode == 0
                ? (true, $"Perfil {Path.GetFileName(nipPath)} importado en el driver.")
                : (false, $"NVIDIA Profile Inspector terminó con código {proc.ExitCode}.");
        }
        catch (Exception ex)
        {
            return (false, $"Error ejecutando NVIDIA Profile Inspector: {ex.Message}");
        }
    }

    private static string? ReadSavedGpuProfile()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\KruzhLatency");
        return key?.GetValue(GpuProfileRegistryValue) as string;
    }

    private static void SaveGpuProfile(string profile)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\KruzhLatency", writable: true);
        key?.SetValue(GpuProfileRegistryValue, profile, Microsoft.Win32.RegistryValueKind.String);
    }

    private void ScanPciDevicesBtn_Click(object sender, RoutedEventArgs e)
    {
        RefreshPciAndMsiUi();
        SetStatus("Bus PCI (HKLM\\SYSTEM\\CurrentControlSet\\Enum\\PCI) re-escaneado.");
    }

    private async void ApplySafeMsiAllBtn_Click(object sender, RoutedEventArgs e)
    {
        await RunStepsWithOverlayAsync("Aplicando cambios", "Modo MSI en dispositivos compatibles",
            new[] { new OverlayStep("Activar MSI seguro", () => App.IrqMsiService.ApplySafePciMsiMode(), RequiresReboot: true) });
        AfterPciChange();
    }

    private async void ApplyOptimalIrqPinningBtn_Click(object sender, RoutedEventArgs e)
    {
        await RunStepsWithOverlayAsync("Aplicando cambios", "Afinidad de interrupciones",
            new[] { new OverlayStep("Balancear interrupciones fuera del Core 0", () => App.IrqMsiService.ApplyOptimalPciIrqAffinity(), RequiresReboot: true) });
        AfterPciChange();
    }

    /// <summary>Spec 003 / G5: retoma (o cancela y revierte) una sesión de auto-tune que quedó a mitad por un reinicio.</summary>
    private async Task ResumePendingAutoTuneAsync()
    {
        var session = await Task.Run(AutoTuneSessionService.Pending);
        if (session is null) return;

        var svc = new AutoTuneSessionService(App.AuditService);
        string what = session.Current?.Title ?? "medición";
        if (AutoTuneSessionService.Runners.TryGetValue(session.Kind, out var runner))
        {
            bool resume = await ShowConfirmModalAsync("Medición en curso",
                $"Hay una medición de KRUZH a mitad (reinicio {session.Reboots}). Sigue con: {what}.\n\n" +
                "Si la cancelás, se revierte todo lo que cambió.", "Continuar", "Cancelar y revertir");
            if (resume)
            {
                await runner(session);
                return;
            }
        }
        else
        {
            await ShowSimpleInfoModalAsync("Medición incompleta",
                $"Quedó una medición a mitad ({what}) que esta versión de KRUZH no puede retomar. Se van a revertir sus cambios.");
        }
        string result = await Task.Run(() => svc.Cancel(session));
        SetStatus($"Medición cancelada. {result}");
    }

    /// <summary>Spec 003 / G6: prepara y arranca una sesión de auto MSI.</summary>
    private async void AutoMsiBtn_Click(object sender, RoutedEventArgs e)
    {
        if (AutoTuneSessionService.Pending() is not null)
        {
            await ShowSimpleInfoModalAsync("Medir MSI", "Ya hay una medición en curso. Reiniciá KRUZH para retomarla o cancelarla.");
            return;
        }
        var svc = new AutoMsiService(App.AuditService);
        var session = await Task.Run(svc.BuildSession);
        if (session.Steps.Count == 0)
        {
            await ShowSimpleInfoModalAsync("Medir MSI", "Ningún dispositivo de tu PC declara soporte de MSI.");
            return;
        }

        int devices = session.Steps.Count / 2;
        int minutes = (int)Math.Ceiling(session.Steps.Count * AutoMsiService.Trials * (AutoMsiService.Settle + AutoMsiService.Window).TotalSeconds / 60) + devices;
        bool go = await ShowConfirmModalAsync("Medir MSI (auto)",
            $"KRUZH va a medir {devices} dispositivos con MSI y sin MSI, con una carga que los hace trabajar. Tarda unos {minutes} minutos.\n\n" +
            "• Antes se crea un punto de restauración.\n" +
            "• Cada dispositivo se reinicia (la pantalla parpadea con la GPU; el mouse o la red se cortan 1-2 s).\n" +
            "• Los que no se reinician en caliente (el disco de arranque, a veces la GPU) piden reiniciar Windows: KRUZH se abre solo al volver y sigue.\n" +
            "• En los pasos de USB, mové el mouse en círculos mientras mide.\n" +
            "• Solo queda lo que baja la latencia por encima del ruido; si un dispositivo falla con el cambio, vuelve atrás.",
            "Empezar");
        if (!go) return;

        await App.AuditService.CreateSystemRestorePointAsync("KRUZH LATENCY — antes de medir MSI");
        await Task.Run(() => svc.Begin(session));
        await RunAutoMsiAsync(session);
    }

    /// <summary>Ejecuta (o retoma tras un reinicio) los pasos pendientes de una sesión de auto MSI.</summary>
    private async Task RunAutoMsiAsync(KruzhLatency.Core.Benchmark.AutoTuneSession session)
    {
        var svc = new AutoMsiService(App.AuditService);
        using var cts = new CancellationTokenSource();
        bool stop = false;
        bool rebooting = false;
        var pending = session.Steps.Where(s => s.Status is KruzhLatency.Core.Benchmark.AutoTuneStepStatus.Pending
                                                         or KruzhLatency.Core.Benchmark.AutoTuneStepStatus.Running).ToList();
        var steps = pending.Select(s => new OverlayStep(s.Title, () =>
        {
            if (stop) return (true, rebooting ? "Sigue después del reinicio." : "Saltado.");
            var (ok, detail, reboot) = svc.RunStep(session, s.Id, cts.Token);
            if (reboot) { stop = true; rebooting = true; }
            return (ok, detail); // un "no apto" se muestra como falla pero no corta la sesión: el overlay sigue con el resto
        })).ToList();

        await RunStepsWithOverlayAsync("Midiendo MSI", "No uses la PC (salvo mover el mouse en los pasos de USB)", steps);
        if (rebooting)
        {
            SetStatus("Reiniciando para seguir midiendo…");
            return;
        }
        bool gpuRestarted = session.Data.ContainsKey("gpuRestarted");
        string report = await Task.Run(() => svc.Finish(session));
        if (gpuRestarted)
        {
            KruzhLatency.Infra.Benchmark.AutoTuneSessionStore.SaveReport("Resultado: MSI", report);
            App.RelaunchAndExit();
            return;
        }
        await ShowSimpleInfoModalAsync("Resultado: MSI", report);
        AfterPciChange();
    }

    /// <summary>Spec 003 / G7: afinidad medida de la xHCI del mouse y del NVMe.</summary>
    private async void AutoAffinityBtn_Click(object sender, RoutedEventArgs e)
    {
        if (AutoTuneSessionService.Pending() is not null)
        {
            await ShowSimpleInfoModalAsync("Medir afinidad", "Ya hay una medición en curso. Reiniciá KRUZH para retomarla o cancelarla.");
            return;
        }
        if (App.AuditService.HasBackupForTweak("PciIrqSmartBalancer"))
        {
            await ShowSimpleInfoModalAsync("Medir afinidad", "Primero revertí \"Interrupciones por núcleo físico\" (I02): toca la misma afinidad.");
            return;
        }
        var svc = new AutoAffinityService(App.AuditService, () => App.HardwareService.CurrentProfile.CoreTopology);
        var session = await Task.Run(svc.BuildSession);
        if (session.Steps.Count == 0)
        {
            await ShowSimpleInfoModalAsync("Medir afinidad", "No se encontró el USB del mouse ni un NVMe para medir.");
            return;
        }

        int runs = session.Steps.Count(s => !s.Id.EndsWith(":decide", StringComparison.Ordinal));
        int minutes = (int)Math.Ceiling(runs * AutoMsiService.Trials * (AutoMsiService.Settle + AutoMsiService.Window).TotalSeconds / 60) + 1;
        bool go = await ShowConfirmModalAsync("Medir afinidad USB/NVMe (auto)",
            $"KRUZH va a probar {runs} configuraciones (como viene Windows + núcleos) para el USB del mouse y el disco NVMe. Tarda unos {minutes} minutos.\n\n" +
            "• En los pasos de USB, mové el mouse en círculos todo el tiempo que mide.\n" +
            "• El NVMe de arranque necesita reiniciar Windows entre pruebas: KRUZH se abre solo al volver y sigue.\n" +
            "• Solo queda lo que baja la latencia por encima del ruido; si nada gana, todo queda como estaba.",
            "Empezar");
        if (!go) return;

        await App.AuditService.CreateSystemRestorePointAsync("KRUZH LATENCY — antes de medir afinidad");
        await Task.Run(() => svc.Begin(session));
        await RunAutoAffinityAsync(session);
    }

    private async Task RunAutoAffinityAsync(KruzhLatency.Core.Benchmark.AutoTuneSession session)
    {
        var svc = new AutoAffinityService(App.AuditService, () => App.HardwareService.CurrentProfile.CoreTopology);
        using var cts = new CancellationTokenSource();
        bool stop = false;
        var pending = session.Steps.Where(s => s.Status is KruzhLatency.Core.Benchmark.AutoTuneStepStatus.Pending
                                                         or KruzhLatency.Core.Benchmark.AutoTuneStepStatus.Running).ToList();
        var steps = pending.Select(s => new OverlayStep(s.Title, () =>
        {
            if (stop) return (true, "Sigue después del reinicio.");
            var (ok, detail, reboot) = svc.RunStep(session, s.Id, cts.Token);
            stop |= reboot;
            return (ok, detail);
        })).ToList();

        await RunStepsWithOverlayAsync("Midiendo afinidad", "No uses la PC (salvo mover el mouse en los pasos de USB)", steps);
        if (stop)
        {
            SetStatus("Reiniciando para seguir midiendo…");
            return;
        }
        string report = await Task.Run(() => svc.Finish(session));
        await ShowSimpleInfoModalAsync("Resultado: afinidad USB/NVMe", report);
        AfterPciChange();
    }

    /// <summary>Spec 003 / G4: mide cada núcleo para la GPU con carga sintética y aplica el ganador o nada.</summary>
    private async void AutoGpuAffinityBtn_Click(object sender, RoutedEventArgs e)
    {
        var svc = new AutoGpuAffinityService(App.AuditService, () => App.HardwareService.CurrentProfile.CoreTopology);
        var (gpu, problem) = await Task.Run(svc.FindGpu);
        if (gpu is null)
        {
            await ShowSimpleInfoModalAsync("Medir afinidad de GPU", problem ?? "No se puede medir en esta PC.");
            return;
        }

        // Chequeo previo sin tocar la GPU: si la carga 3D no abre, no se reinicia nada.
        SetStatus("Probando la carga 3D…");
        if (await Task.Run(svc.Preflight) is { } preflightError)
        {
            await ShowSimpleInfoModalAsync("Medir afinidad de GPU", preflightError);
            return;
        }

        var candidates = svc.Candidates();
        double perRunSec = (AutoGpuAffinityService.Warmup + AutoGpuAffinityService.Capture).TotalSeconds + 4;
        int minutes = (int)Math.Ceiling(candidates.Count * (AutoGpuAffinityService.TrialsPerCandidate * perRunSec + 10) / 60);
        bool go = await ShowConfirmModalAsync("Medir afinidad de GPU",
            $"KRUZH va a probar {candidates.Count} opciones (como viene Windows + un núcleo por vez) para las interrupciones de tu {gpu.Name}, " +
            $"con una carga 3D propia medida por PresentMon. Tarda unos {minutes} minutos.\n\n" +
            "• La pantalla va a parpadear cada vez que se reinicia el driver de video.\n" +
            "• Cerrá juegos y no uses la PC mientras mide.\n" +
            "• Al final aplica solo lo que gane por encima del ruido; si nada gana, deja todo como estaba.",
            "Empezar");
        if (!go) return;

        using var cts = new CancellationTokenSource();
        bool failed = false;
        string report = string.Empty;
        var steps = candidates
            .Select(c => new OverlayStep(c.Name == KruzhLatency.Core.Benchmark.TrialComparer.Default ? "Como viene Windows" : $"GPU en {c.Name}", () =>
            {
                if (failed) return (false, "Saltado: falló un paso anterior.");
                var r = svc.Measure(gpu, c, cts.Token);
                failed |= !r.Ok;
                return r;
            }))
            .Append(new OverlayStep("Elegir y aplicar", () =>
            {
                if (failed)
                {
                    svc.Abort(gpu);
                    return (false, "Medición cortada: se dejó la afinidad original.");
                }
                var (ok, detail, rep) = svc.Finish(gpu, candidates);
                report = rep;
                return (ok, detail);
            }))
            .ToList();

        var results = await RunStepsWithOverlayAsync("Midiendo afinidad de GPU", "Carga 3D + PresentMon · no uses la PC", steps, showFailures: false);
        if (report.Length == 0)
        {
            report = "La medición no terminó:\n" + string.Join("\n", results.Where(r => !r.Success).Select(r => $"• {r.Step.Title}: {r.Detail}"));
        }

        if (svc.GpuRestarted)
        {
            // Después de reiniciar el driver de video, WinUI puede quedar sin dibujar texto: se reabre KRUZH con ventana nueva
            // y el resultado se muestra al volver.
            KruzhLatency.Infra.Benchmark.AutoTuneSessionStore.SaveReport("Resultado: afinidad de GPU", report);
            App.RelaunchAndExit();
            return;
        }
        await ShowSimpleInfoModalAsync("Resultado: afinidad de GPU", report);
        AfterPciChange();
    }

    private async void RevertAllPciIrqMsiBtn_Click(object sender, RoutedEventArgs e)
    {
        await RunStepsWithOverlayAsync("Revirtiendo cambios", "Interrupciones y MSI", new[]
        {
            new OverlayStep("Afinidad de GPU medida", () =>
            {
                bool ok = App.AuditService.RevertTweakFromJournal(AutoGpuAffinityService.TweakId, out string m);
                return (true, ok ? m : "No había afinidad de GPU medida para revertir.");
            }, RequiresReboot: true),
            new OverlayStep("MSI medido", () =>
            {
                var ids = App.AuditService.TweakIdsStartingWith(AutoMsiService.TweakPrefix);
                int n = ids.Count(id => App.AuditService.RevertTweakFromJournal(id, out _));
                return (true, n == 0 ? "No había MSI medido para revertir." : $"{n} dispositivos vuelven a su MSI original.");
            }, RequiresReboot: true),
            new OverlayStep("Afinidad USB/NVMe medida", () =>
            {
                var ids = App.AuditService.TweakIdsStartingWith(AutoAffinityService.TweakPrefix);
                int n = ids.Count(id => App.AuditService.RevertTweakFromJournal(id, out _));
                return (true, n == 0 ? "No había afinidad medida para revertir." : $"{n} dispositivos vuelven a su afinidad original.");
            }, RequiresReboot: true),
            new OverlayStep("Modo MSI", () =>
            {
                bool ok = App.AuditService.RevertTweakFromJournal("PciMsiModeV3", out string m);
                return (true, ok ? m : "No había cambios de MSI para revertir.");
            }, RequiresReboot: true),
            new OverlayStep("Afinidad de interrupciones", () =>
            {
                bool ok = App.AuditService.RevertTweakFromJournal("PciIrqSmartBalancer", out string m);
                return (true, ok ? m : "No había cambios de afinidad para revertir.");
            }, RequiresReboot: true)
        });
        AfterPciChange();
    }

    private async void EnableSinglePciMsiBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string pnpId) return;
        string msiPath = $@"SYSTEM\CurrentControlSet\Enum\{pnpId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
        string affPath = $@"SYSTEM\CurrentControlSet\Enum\{pnpId}\Device Parameters\Interrupt Management\Affinity Policy";

        await RunStepsWithOverlayAsync("Aplicando cambios", "Modo MSI", new[]
        {
            new OverlayStep("MSI + prioridad alta", () =>
            {
                App.AuditService.SetRegistryValue("PciMsiModeV3", Microsoft.Win32.RegistryHive.LocalMachine, msiPath, "MSISupported", 1, Microsoft.Win32.RegistryValueKind.DWord);
                App.AuditService.SetRegistryValue("PciMsiModeV3", Microsoft.Win32.RegistryHive.LocalMachine, affPath, "DevicePriority", 3, Microsoft.Win32.RegistryValueKind.DWord);
                return (true, "MSI activado y prioridad alta: toma efecto al reiniciar.");
            }, RequiresReboot: true)
        });
        AfterPciChange();
    }

    private void AfterPciChange()
    {
        RefreshPciAndMsiUi();
        RefreshLiveTweaksStateInPlace();
        RefreshJournalAndLicenseUi();
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
        // "Agregar juego" va como último ítem, al lado del último juego.
        GamesGridView.ItemsSource = list.Cast<object>().Append(AddGameTile.Instance).ToList();
        GamesLibraryCountText.Text = $"TU BIBLIOTECA · {all.Count} JUEGOS";
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
        if (IsAddGameCard(card))
        {
            card.Background = AddCardHoverBackgroundBrush;
            SetAddCardForeground(card, (Brush)Application.Current.Resources["KruzhTextPrimaryBrush"]);
        }
    }

    private void GameCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border card) return;
        card.Translation = System.Numerics.Vector3.Zero;
        if (IsAddGameCard(card))
        {
            card.BorderBrush = AddCardIdleBorderBrush;
            card.Background = TransparentBrush;
            SetAddCardForeground(card, (Brush)Application.Current.Resources["KruzhTextTertiaryBrush"]);
        }
        else
        {
            card.BorderBrush = (Brush)Application.Current.Resources["KruzhHairlineBorderBrush"];
        }
    }

    private static bool IsAddGameCard(Border card) => string.Equals(card.Tag as string, "AddGame", StringComparison.Ordinal);

    private static void SetAddCardForeground(Border card, Brush brush)
    {
        if (card.Child is not Panel panel) return;
        foreach (var child in panel.Children)
        {
            if (child is FontIcon icon) icon.Foreground = brush;
            else if (child is TextBlock text) text.Foreground = brush;
        }
    }

    private void GamesGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AddGameTile)
        {
            AddCustomGameExeBtn_Click(sender, new RoutedEventArgs());
            return;
        }
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

        GameOptimizationModalOverlay.Visibility = Visibility.Collapsed;
        _selectedGameForModal = null;
        await RunStepsWithOverlayAsync("Aplicando cambios", game.Name,
            new[] { new OverlayStep($"Optimizaciones de {game.Name}", () => App.GameOptimizer.Apply(game, priority, qos, gpuPref)) });
        RefreshGamesAndNvidiaUi();
        RefreshJournalAndLicenseUi();
    }

    private async void ModalRevertBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGameForModal == null) return;
        var game = _selectedGameForModal;

        GameOptimizationModalOverlay.Visibility = Visibility.Collapsed;
        _selectedGameForModal = null;
        await RunStepsWithOverlayAsync("Revirtiendo cambios", game.Name,
            new[] { new OverlayStep($"Optimizaciones de {game.Name}", () => App.GameOptimizer.Revert(game)) });
        RefreshGamesAndNvidiaUi();
        RefreshJournalAndLicenseUi();
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

    private async void ApplyAllGamesBoostBtn_Click(object sender, RoutedEventArgs e)
    {
        string[] gameTweakIds = { "GameIfeoHighPriority", "QosDscp46Games" };
        var items = gameTweakIds
            .Select(id => App.TweakEngine.AllTweaks.FirstOrDefault(x => x.Id == id))
            .Where(t => t != null)
            .Cast<DeclarativeTweakItem>()
            .ToList();
        if (!await ConfirmRiskyAsync(items)) return;

        int applied = 0;
        foreach (var t in items)
        {
            var rep = App.TweakEngine.ExecuteApply(t);
            if (rep.Success) applied++;
        }
        RefreshLiveTweaksStateInPlace();
        SetStatus($"Optimización por juego aplicada a {App.TweakEngine.TargetGames.Count} ejecutables ({applied} módulos activos).");
    }

    private async void ClearShaderCacheBtn_Click(object sender, RoutedEventArgs e)
    {
        // Borra miles de archivos: corre en background dentro del overlay.
        await RunStepsWithOverlayAsync("Limpiando caché", "Caché de shaders de DirectX / NVIDIA / AMD",
            new[] { new OverlayStep("Limpiar caché de shaders", () => App.NvidiaDriversService.ClearShaderCaches()) });
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

        // Cada 10 s (en segundo plano) relee el estado real para detectar cambios hechos fuera de KRUZH, y solo si se ve
        // Inicio u Optimizaciones: el resto del tiempo KRUZH no consulta el sistema (B19: la app no debe sumar latencia).
        if (_telemetryTickCounter % 10 == 0 && !_isInitializingUi && !_isApplyingBatch &&
            (ViewTweaks.Visibility == Visibility.Visible || ViewDashboard.Visibility == Visibility.Visible))
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
            UpdateMonitorTiles(curTimerMs, dpcPct, cpuPct, gpuPct);
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

    /// <summary>Gráfico de Monitor: solo interrupciones + DPC, con escala que se adapta (mínimo 5 %) y su etiqueta.</summary>
    private void RenderTelemetryChartPolylines()
    {
        int count = _dpcSamples.Count;
        if (count < 2) return;

        double width = LiveTelemetryCanvas.ActualWidth > 100 ? LiveTelemetryCanvas.ActualWidth : 900;
        double height = LiveTelemetryCanvas.ActualHeight > 40 ? LiveTelemetryCanvas.ActualHeight : 128;
        double scaleMax = Math.Max(5.0, Math.Ceiling(_dpcSamples.Max()));
        MonChartMaxLabel.Text = $"{scaleMax:0} %";

        var points = new PointCollection();
        double stepX = width / Math.Max(1, count - 1);
        for (int i = 0; i < count; i++)
        {
            double norm = Math.Clamp(_dpcSamples[i] / scaleMax, 0.0, 1.0);
            points.Add(new Point(i * stepX, height - (norm * (height - 6)) - 3));
        }
        DpcChartPolyline.Points = points;

        double avg = _dpcSamples.Average();
        double peak = _dpcSamples.Max();
        LiveMonitorStatsText.Text = $"promedio {avg:F2} % · pico {peak:F2} %";
    }

    private static readonly SolidColorBrush MonitorGoodBrush = new(Windows.UI.Color.FromArgb(255, 99, 192, 136));
    private static readonly SolidColorBrush MonitorWarnBrush = new(Windows.UI.Color.FromArgb(255, 214, 170, 92));
    private static readonly SolidColorBrush MonitorBadBrush = new(Windows.UI.Color.FromArgb(255, 205, 102, 102));

    /// <summary>Los 4 indicadores de Monitor: valor + una frase que explica si está bien, en lenguaje simple.</summary>
    private void UpdateMonitorTiles(double timerMs, double dpcPct, double cpuPct, double gpuPct)
    {
        var neutral = (Brush)Application.Current.Resources["KruzhTextTertiaryBrush"];

        MonTimerValue.Text = timerMs > 0 ? $"{timerMs:0.###} ms" : "-";
        if (timerMs > 0 && timerMs <= 1.05)
        {
            MonTimerStatus.Text = "Alta precisión activa.";
            MonTimerStatus.Foreground = MonitorGoodBrush;
        }
        else
        {
            // En reposo 15,6 ms es lo normal: CS2 / VALORANT piden 1 ms o menos al abrirse.
            MonTimerStatus.Text = "Normal en reposo. Los juegos la suben solos.";
            MonTimerStatus.Foreground = neutral;
        }

        MonDpcValue.Text = $"{dpcPct:0.00} %";
        if (dpcPct <= 2.0)
        {
            MonDpcStatus.Text = "Bien: los drivers responden rápido.";
            MonDpcStatus.Foreground = MonitorGoodBrush;
        }
        else if (dpcPct <= 5.0)
        {
            MonDpcStatus.Text = "Moderado: puede haber picos de latencia.";
            MonDpcStatus.Foreground = MonitorWarnBrush;
        }
        else
        {
            MonDpcStatus.Text = "Alto: un driver está generando latencia.";
            MonDpcStatus.Foreground = MonitorBadBrush;
        }

        MonCpuValue.Text = $"{cpuPct:0} %";
        MonCpuStatus.Text = cpuPct >= 90 ? "Al límite." : "Uso actual del procesador.";
        MonCpuStatus.Foreground = cpuPct >= 90 ? MonitorWarnBrush : neutral;

        MonGpuValue.Text = $"{gpuPct:0} %";
        MonGpuStatus.Text = "Uso actual de la placa de video.";
        MonGpuStatus.Foreground = neutral;
    }

    // ---------------------------------------------------------------- Drivers con más latencia (ETW DPC/ISR)
    private const double DpcWarnUs = 500;   // a partir de acá conviene mirar el driver
    private const double DpcBadUs = 1000;   // arriba de 1 ms se nota como micro-stutter

    private KruzhLatency.Infra.Etw.DpcLatencySession? _dpcSession;
    private DispatcherTimer? _dpcLiveTimer;
    private int _dpcLiveTicks;

    /// <summary>Iniciar / Detener la medición continua (sirve para dejarla corriendo mientras se juega).</summary>
    private async void AnalyzeDpcDriversBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_dpcSession == null)
        {
            StartDpcMeasurement();
        }
        else
        {
            await StopDpcMeasurementAsync();
        }
    }

    private void StartDpcMeasurement()
    {
        var session = KruzhLatency.Infra.Etw.DpcLatencySession.TryStart(out string? error);
        if (session == null)
        {
            ShowDpcAnalysis(KruzhLatency.Infra.Etw.DpcAnalysisResult.Failed(error ?? "No se pudo iniciar la medición."));
            return;
        }

        _dpcSession = session;
        _dpcLiveTicks = 0;
        // D2: mientras mide, KRUZH no redibuja gráficos ni relee tweaks (eso también genera DPC en dxgkrnl y suma a la medición).
        _telemetryTimer.Stop();
        DpcSummaryPanel.Visibility = Visibility.Collapsed;
        DpcDriversHeader.Visibility = Visibility.Collapsed;
        DpcDriversItemsControl.ItemsSource = null;
        AnalyzeDpcDriversBtn.Content = "Detener";
        DpcLiveDot.Visibility = Visibility.Visible;
        DpcStatusText.Text = "Midiendo · 00:00 · minimizá KRUZH y jugá";

        _dpcLiveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _dpcLiveTimer.Tick -= DpcLiveTimer_Tick;
        _dpcLiveTimer.Tick += DpcLiveTimer_Tick;
        _dpcLiveTimer.Start();
    }

    private void DpcLiveTimer_Tick(object? sender, object e)
    {
        if (_dpcSession == null) return;
        // D2: solo un texto chico cada 5 s; los resultados se muestran al detener.
        if (++_dpcLiveTicks % 5 != 0) return;
        var elapsed = _dpcSession.Elapsed;
        DpcStatusText.Text = $"Midiendo · {(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00} · minimizá KRUZH y jugá; los resultados aparecen al detener";
    }

    private async Task StopDpcMeasurementAsync()
    {
        var session = _dpcSession;
        if (session == null) return;
        _dpcSession = null;
        _dpcLiveTimer?.Stop();
        AnalyzeDpcDriversBtn.IsEnabled = false;
        DpcStatusText.Text = "Deteniendo…";
        try
        {
            var result = await session.StopAsync();
            ShowDpcAnalysis(result);
            var t = result.Duration;
            DpcStatusText.Text = $"Medición terminada · duró {(int)t.TotalMinutes:00}:{t.Seconds:00}";
        }
        catch (Exception ex)
        {
            App.LogError("DPC stop", ex);
            DpcStatusText.Text = "La medición se detuvo con un error.";
        }
        finally
        {
            session.Dispose();
            DpcLiveDot.Visibility = Visibility.Collapsed;
            AnalyzeDpcDriversBtn.Content = "Iniciar medición";
            AnalyzeDpcDriversBtn.IsEnabled = true;
            if (!_isApplyingBatch) _telemetryTimer.Start();
        }
    }

    private void ShowDpcAnalysis(KruzhLatency.Infra.Etw.DpcAnalysisResult result)
    {
        DpcSummaryPanel.Visibility = Visibility.Visible;

        if (result.Error != null)
        {
            DpcVerdictText.Text = "No se pudo medir";
            DpcVerdictText.Foreground = MonitorBadBrush;
            DpcSummaryText.Text = result.Error;
            DpcDriversHeader.Visibility = Visibility.Collapsed;
            DpcDriversItemsControl.ItemsSource = null;
            return;
        }

        var top = result.Drivers.Where(d => d.Count > 0).Take(8).ToList();
        var diagnoses = top.Select(d => DpcDiagnosis.Diagnose(d.FileName, d.MaxUs, AverageUs(d))).ToList();
        _lastDpcDiagnoses = diagnoses;
        var worst = top.FirstOrDefault();
        int offTarget = diagnoses.Count(x => x.NeedsAttention);
        if (worst == null)
        {
            DpcVerdictText.Text = "No se registraron DPC ni interrupciones en la medición.";
            DpcVerdictText.Foreground = (Brush)Application.Current.Resources["KruzhTextSecondaryBrush"];
        }
        else if (worst.MaxUs >= DpcBadUs)
        {
            DpcVerdictText.Text = $"{worst.Description} tuvo picos de {worst.MaxUs:0} µs: puede causar micro-stutters. Mirá abajo la causa y \"Arreglar\".";
            DpcVerdictText.Foreground = MonitorBadBrush;
        }
        else if (offTarget > 0)
        {
            DpcVerdictText.Text = $"Jugable, pero {offTarget} driver(s) fuera del objetivo (pico ≤ {DpcDiagnosis.PeakTargetUs:0} µs, promedio < {DpcDiagnosis.AverageTargetUs:0} µs).";
            DpcVerdictText.Foreground = MonitorWarnBrush;
        }
        else
        {
            DpcVerdictText.Text = $"En objetivo: ningún driver pasa {DpcDiagnosis.PeakTargetUs:0} µs de pico ni {DpcDiagnosis.AverageTargetUs:0} µs de promedio (peor: {worst.Description}, {worst.MaxUs:0} µs).";
            DpcVerdictText.Foreground = MonitorGoodBrush;
        }

        DpcSummaryText.Text =
            $"Medido durante {result.Duration.TotalSeconds:0} s · {result.TotalDpc:N0} DPC · {result.TotalIsr:N0} interrupciones · " +
            $"{result.Drivers.Count} drivers con actividad.";

        var neutral = (Brush)Application.Current.Resources["KruzhTextSecondaryBrush"];
        DpcDriversItemsControl.ItemsSource = top.Select((d, i) =>
        {
            var dx = diagnoses[i];
            return new DriverLatencyRow
            {
                Title = d.Description,
                FileName = d.FileName,
                MaxText = $"{d.MaxUs:0} µs",
                AvgText = $"{AverageUs(d):0.0} µs",
                CountText = $"{d.Count:N0}",
                MaxBrush = d.MaxUs >= DpcBadUs ? MonitorBadBrush : (dx.NeedsAttention ? MonitorWarnBrush : neutral),
                Cause = dx.NeedsAttention ? dx.Cause : string.Empty,
                CanFix = dx.NeedsAttention && FixableItems(dx).Any()
            };
        }).ToList();
        DpcDriversHeader.Visibility = top.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private List<DriverDiagnosis> _lastDpcDiagnoses = new();

    private static double AverageUs(KruzhLatency.Infra.Etw.DriverLatencyStat d) =>
        d.DpcCount + d.IsrCount > 0 ? (d.DpcAvgUs * d.DpcCount + d.IsrAvgUs * d.IsrCount) / (d.DpcCount + d.IsrCount) : 0;

    /// <summary>Ítems del catálogo que atacan el driver, aplicables en esta PC y todavía no aplicados.</summary>
    private static IEnumerable<DeclarativeTweakItem> FixableItems(DriverDiagnosis dx) =>
        App.TweakEngine.AllTweaks.Where(t => DpcDiagnosis.Matches(dx.FixItemIds, t.Id) && t.IsApplicableBool && !t.IsLiveApplied);

    /// <summary>D1: "Arreglar" marca como pendientes los ajustes que atacan ese driver y lleva a Optimizaciones para aplicarlos.</summary>
    private void DpcFixDriverBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string file }) return;
        var dx = _lastDpcDiagnoses.FirstOrDefault(d => string.Equals(d.FileName, file, StringComparison.OrdinalIgnoreCase));
        if (dx is null) return;

        var items = FixableItems(dx).ToList();
        foreach (var t in items)
        {
            if (t.HasOptions) t.SetDesiredOption(t.Options.LastOrDefault()?.Key ?? t.SelectedOptionKey);
            else t.SetDesiredToggle(true);
        }

        string tab = items.All(t => t.Category == TweakCategory.Devices) ? "Devices" : "Tweaks";
        MainNavView.SelectedItem = NavTweaksItem;
        SwitchToView("Tweaks");
        OptTab_Click(tab == "Devices" ? OptTabDevicesBtn : OptTabTweaksBtn, new RoutedEventArgs());
        SetStatus($"{items.Count} ajuste(s) para {file} quedaron pendientes: revisalos y tocá Aplicar.");
    }

    /// <summary>Nombre legible de un id del journal (tweak del catálogo, juego, o entradas internas viejas).</summary>
    private static string JournalTitleFor(string tweakId)
    {
        var item = App.TweakEngine.AllTweaks.FirstOrDefault(t => t.Id == tweakId);
        if (item != null) return item.Title;
        if (tweakId.StartsWith("Game:", StringComparison.OrdinalIgnoreCase)) return $"Optimizaciones del juego · {tweakId[5..]}";
        if (tweakId == "NvidiaTelemetryOff") return "Telemetría de NVIDIA desactivada";
        if (TransactionalAuditService.IsLegacyFallbackEntry(tweakId)) return "Restauración automática (versión anterior)";
        return tweakId;
    }

    private static string RelativeTime(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 1) return "recién";
        if (span.TotalMinutes < 60) return $"hace {(int)span.TotalMinutes} min";
        if (span.TotalHours < 24) return $"hace {(int)span.TotalHours} h";
        if (span.TotalDays < 30) return $"hace {(int)span.TotalDays} días";
        return utc.ToLocalTime().ToString("dd/MM/yyyy");
    }

    /// <summary>Paso de overlay que revierte una entrada del journal (tweak del catálogo, juego, o id suelto).</summary>
    private static OverlayStep BuildJournalRevertStep(string tweakId)
    {
        var item = App.TweakEngine.AllTweaks.FirstOrDefault(t => t.Id == tweakId);
        if (item != null)
        {
            return RevertTweakStep(item);
        }
        return new OverlayStep(JournalTitleFor(tweakId), () =>
        {
            bool reverted = App.AuditService.RevertTweakFromJournal(tweakId, out string msg);
            return (reverted, msg);
        });
    }

    private async void RevertSingleJournalEntryBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tweakId) return;
        await RunStepsWithOverlayAsync("Revirtiendo cambios", "Restaurando los valores originales", new[] { BuildJournalRevertStep(tweakId) });
        RefreshLiveTweaksStateInPlace();
        RefreshGamesAndNvidiaUi();
        RefreshJournalAndLicenseUi();
    }

    private void RefreshJournalAndLicenseUi()
    {
        // "Cambios aplicados": un renglón por tweak con cambios activos, en lenguaje humano (no IDs ni rutas de registro).
        var summary = App.AuditService.GetActiveBackupSummary()
            .Select(s => new JournalSummaryItem
            {
                TweakId = s.TweakId,
                Title = JournalTitleFor(s.TweakId),
                Detail = $"{s.ValueCount} {(s.ValueCount == 1 ? "valor modificado" : "valores modificados")} · {RelativeTime(s.LastChangeUtc)}"
            })
            .ToList();
        JournalEntriesItemsControl.ItemsSource = summary;
        JournalEmptyText.Visibility = summary.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RevertAllFromJournalBtn.IsEnabled = summary.Count > 0;

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
        string dir = Path.GetDirectoryName(App.AuditService.JournalPath)!; // C:\ProgramData\KruzhLatency\journal (T11)
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    private async void RevertAllFromJournalBtn_Click(object sender, RoutedEventArgs e)
    {
        var active = App.AuditService.GetActiveBackupSummary();
        if (active.Count == 0)
        {
            await ShowSimpleInfoModalAsync("Nada para revertir", "No hay cambios de KRUZH LATENCY aplicados en esta PC.");
            return;
        }

        bool confirmed = await ShowConfirmModalAsync(
            "Revertir todos los cambios",
            $"Se van a restaurar los valores originales de {active.Count} optimización(es). Algunos cambios necesitan reiniciar la PC para volver del todo.",
            "Revertir todo");
        if (!confirmed) return;

        var steps = active.Select(a => BuildJournalRevertStep(a.TweakId)).ToList();
        await RunStepsWithOverlayAsync("Revirtiendo cambios", "Restaurando los valores originales", steps);

        RefreshLiveTweaksStateInPlace();
        RefreshTweaksListUi();
        RefreshGamesAndNvidiaUi();
        RefreshJournalAndLicenseUi();
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

    /// <summary>Aviso con un solo botón, con el modal de la app.</summary>
    private Task ShowSimpleInfoModalAsync(string title, string message) =>
        ShowAppModalAsync(title, message, "Aceptar", secondaryText: null);

    /// <summary>Confirmación con el modal de la app. Devuelve true si se eligió la acción principal.</summary>
    private Task<bool> ShowConfirmModalAsync(string title, string message, string primaryText, string secondaryText = "Cancelar") =>
        ShowAppModalAsync(title, message, primaryText, secondaryText);

    private TaskCompletionSource<bool>? _appModalTcs;

    /// <summary>
    /// Modal propio (velo con blur + tarjeta negra) en lugar de ContentDialog de Windows. Si se pide otro modal mientras
    /// hay uno abierto, el anterior se resuelve como "cancelar" y se muestra el nuevo.
    /// </summary>
    private async Task<bool> ShowAppModalAsync(string title, string message, string primaryText, string? secondaryText)
    {
        await _dialogLock.WaitAsync();
        TaskCompletionSource<bool> tcs;
        try
        {
            _appModalTcs?.TrySetResult(false);
            tcs = new TaskCompletionSource<bool>();
            _appModalTcs = tcs;

            AppModalTitleText.Text = title;
            AppModalMessageText.Text = message;
            AppModalPrimaryBtn.Content = primaryText;
            AppModalSecondaryBtn.Content = secondaryText ?? string.Empty;
            AppModalSecondaryBtn.Visibility = secondaryText == null ? Visibility.Collapsed : Visibility.Visible;
            AppModalOverlay.Visibility = Visibility.Visible;
            AppModalPrimaryBtn.Focus(FocusState.Programmatic);
        }
        finally
        {
            _dialogLock.Release();
        }

        bool result = await tcs.Task;
        if (ReferenceEquals(_appModalTcs, tcs))
        {
            AppModalOverlay.Visibility = Visibility.Collapsed;
            _appModalTcs = null;
        }
        return result;
    }

    private void AppModalPrimaryBtn_Click(object sender, RoutedEventArgs e) => _appModalTcs?.TrySetResult(true);

    private void AppModalSecondaryBtn_Click(object sender, RoutedEventArgs e) => _appModalTcs?.TrySetResult(false);

    private void AppModalScrim_Tapped(object sender, TappedRoutedEventArgs e) => _appModalTcs?.TrySetResult(false);

    private void SetStatus(string text)
    {
        if (BottomStatusBarText != null)
        {
            BottomStatusBarText.Text = $"{DateTime.Now:HH:mm:ss} · {text}";
        }
    }
}
