using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KruzhLatency.Core.Benchmark;
using KruzhLatency.Infra.Benchmark;
using KruzhLatency.Models;
using KruzhLatency.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace KruzhLatency;

/// <summary>
/// Asistente de optimización completa (tipo instalador): perfil → una pantalla por sección con casillas → mediciones →
/// resumen → "Optimizar". Aplica los tweaks marcados con el flujo normal (punto de restauración + journal) y después corre
/// las mediciones elegidas una tras otra, aunque haya reinicios o KRUZH se reabra (cola en <see cref="OnboardingStore"/>).
/// Solo suma: lo que ya está aplicado se muestra bloqueado; revertir se hace desde Optimizaciones o Monitor.
/// </summary>
public sealed partial class MainWindow
{
    private const string OnboardingRebootKind = "onboarding-reboot";
    private const string LabSectionPrefix = "Laboratorio";

    private readonly List<OnboardingPage> _onboardingPages = new();
    private int _onboardingIndex;
    private PresetProfileKind _onboardingPreset = PresetProfileKind.Competitivo;

    /// <summary>Secciones del catálogo, en el orden en que se recorren (Laboratorio va aparte, al final).</summary>
    private static readonly (string Name, string Subtitle, Func<DeclarativeTweakItem, bool> Match)[] OnboardingSections =
    {
        ("Mouse e input", "Cómo llega el mouse y el teclado a Windows.", t => t.Category == TweakCategory.MouseInput),
        ("Procesador y sistema", "Planificador, timer, energía del procesador y ajustes del sistema.", t => t.Category == TweakCategory.SystemCpu),
        ("GPU y pantalla", "Driver de video, modo de presentación y pantalla.", t => t.Category == TweakCategory.GpuDisplay),
        ("Red", "Placa de red y pila TCP/IP.", t => t.Category == TweakCategory.NetworkDesync),
        ("Interrupciones y audio", "MSI, prioridad de interrupciones y audio.", t => t.Category == TweakCategory.IrqMsiAudio),
        ("Energía, USB y memoria", "Ahorro de energía de USB/PCIe, disco y memoria.", t => t.Category is TweakCategory.UsbPowerBus or TweakCategory.StorageMemory),
        ("Juegos", "Ajustes por juego (sin tocar archivos ni memoria del juego).", t => t.Category == TweakCategory.FpsBoostGames),
        ("Servicios", "Servicios de Windows que no hacen falta para jugar. Los del anticheat, la red y el audio no se tocan.", t => t.Category == TweakCategory.Services),
        ("Dispositivos", "Dispositivos que no se usan para jugar. Se reactivan desde Optimizaciones → Device Manager.", t => t.Category == TweakCategory.Devices),
        ("Apps y publicidad", "Apps que vienen con Windows, publicidad y telemetría. Solo para tu usuario y revertibles.", t => t.Category == TweakCategory.Apps),
    };

    private static bool IsLab(DeclarativeTweakItem t) => t.CategoryDisplay.StartsWith(LabSectionPrefix, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- abrir / cerrar

    private async void OpenOnboardingBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingBatch) return;
        if (InOnboarding || AutoTuneSessionService.Pending() is not null)
        {
            await ShowSimpleInfoModalAsync("Optimización completa",
                "Hay una optimización o una medición en curso. Reiniciá KRUZH para retomarla o cancelarla.");
            return;
        }
        _onboardingPreset = _activePreset;
        BuildOnboardingPages();
        _onboardingIndex = 0;
        ShowOnboardingPage();
        OnboardingOverlay.Visibility = Visibility.Visible;
    }

    private void OnboardingCancel_Click(object sender, RoutedEventArgs e)
    {
        OnboardingOverlay.Visibility = Visibility.Collapsed;
        WithToggleGuard(() => App.TweakEngine.DiscardAllPending());
        UpdatePendingChangesBar();
        SetStatus("Optimización completa cancelada: no se cambió nada.");
    }

    private void WithToggleGuard(Action action)
    {
        bool previous = _isHandlingToggleChange;
        _isHandlingToggleChange = true; // los toggles de Optimizaciones cambian por binding: que no lo tomen como clic
        try { action(); }
        finally { _isHandlingToggleChange = previous; }
    }

    // ---------------------------------------------------------------- armado de pantallas

    private void BuildOnboardingPages()
    {
        WithToggleGuard(() => App.TweakEngine.SelectPresetProfile(_onboardingPreset));
        _onboardingPages.Clear();

        _onboardingPages.Add(new OnboardingPage
        {
            Kind = OnboardingPageKind.Profile,
            StepName = "Perfil",
            Title = "Elegí un perfil",
            Subtitle = "Marca de entrada lo que corresponde a cada perfil. En las pantallas siguientes podés sumar o sacar cualquier cosa.",
        });

        var applicable = App.TweakEngine.AllTweaks.Where(t => t.IsApplicableBool).ToList();
        foreach (var (name, subtitle, match) in OnboardingSections)
        {
            var items = applicable.Where(t => match(t) && !IsLab(t)).ToList();
            if (items.Count == 0) continue;
            var page = new OnboardingPage { Kind = OnboardingPageKind.Items, StepName = name, Title = name, Subtitle = subtitle };
            page.Rows.AddRange(items.Select(TweakRow));
            _onboardingPages.Add(page);
        }

        var lab = applicable.Where(IsLab).ToList();
        if (lab.Count > 0)
        {
            var page = new OnboardingPage
            {
                Kind = OnboardingPageKind.Items,
                StepName = "Laboratorio",
                Title = "Laboratorio (sin evidencia)",
                Subtitle = "Ajustes populares sin evidencia de que mejoren algo. No están en ningún perfil: marcá solo los que quieras probar y medí antes y después.",
            };
            page.Rows.AddRange(lab.Select(TweakRow));
            _onboardingPages.Add(page);
        }

        bool measureByDefault = _onboardingPreset != PresetProfileKind.Minimal;
        var measure = new OnboardingPage
        {
            Kind = OnboardingPageKind.Measurements,
            StepName = "Mediciones",
            Title = "Mediciones en tu PC",
            Subtitle = "KRUZH prueba cada opción con carga real y deja solo la que mide mejor; si ninguna gana, queda como viene Windows. " +
                       "Mientras mide no uses la PC: puede reiniciarse sola y sigue al volver.",
        };
        measure.Rows.Add(MeasurementRow(OnboardingPlan.Msi, "Modo MSI",
            "Mide en la GPU, el USB, la red y el audio: MSI sí o no, prioridad alta, y el límite de mensajes donde aplica. Deja lo de menor latencia de interrupción. El disco no se prueba.", measureByDefault));
        measure.Rows.Add(MeasurementRow(OnboardingPlan.GpuAffinity, "Afinidad de la GPU",
            "Prueba en qué núcleo la GPU entrega más FPS con una carga 3D propia medida por PresentMon. La pantalla parpadea en cada prueba.", measureByDefault));
        measure.Rows.Add(MeasurementRow(OnboardingPlan.DeviceAffinity, "Afinidad del USB del mouse y del NVMe",
            "Prueba en qué núcleo atienden sus interrupciones. En los pasos de USB hay que mover el mouse en círculos.", measureByDefault));
        _onboardingPages.Add(measure);

        _onboardingPages.Add(new OnboardingPage
        {
            Kind = OnboardingPageKind.Summary,
            StepName = "Resumen",
            Title = "Listo para optimizar",
            Subtitle = "Revisá el resumen. Antes de tocar nada se crea un punto de restauración, y cada cambio queda registrado para revertirlo.",
        });
    }

    private OnboardingRow TweakRow(DeclarativeTweakItem t)
    {
        bool locked = t.IsLiveApplied;
        var badges = new List<string>();
        if (locked) badges.Add("Ya aplicado");
        if (IsLab(t)) badges.Add("Sin evidencia");
        if (t.RequiresReboot) badges.Add("Pide reinicio");
        if (t.ReducesSecurityBool) badges.Add("Baja la seguridad");
        if (t.IsAnticheatUnsafeBool) badges.Add("Riesgo anticheat");
        if (t.Risk >= RiskLevel.High) badges.Add("Riesgo alto");

        var row = new OnboardingRow
        {
            Tweak = t,
            Title = string.IsNullOrWhiteSpace(t.CodeNumber) ? t.Title : $"{t.Title}  ·  {t.CodeNumber}",
            Description = FirstSentence(t.TechnicalDescription),
            Tooltip = t.TechnicalDescription,
            Badges = badges,
            IsLocked = locked,
            OptionKey = t.HasOptions
                ? (t.IsPending ? t.DesiredOptionKey : DeclarativeTweakEngine.RecommendedOptionKey(t))
                : null,
            IsChecked = locked || t.IsPending,
        };
        row.Changed = OnOnboardingRowChanged;
        return row;
    }

    private OnboardingRow MeasurementRow(string key, string title, string description, bool on)
    {
        var row = new OnboardingRow { MeasurementKey = key, Title = title, Description = description, IsChecked = on };
        row.Changed = OnOnboardingRowChanged;
        return row;
    }

    private static string FirstSentence(string text)
    {
        string s = (text ?? string.Empty).Replace('\n', ' ').Trim();
        int dot = s.IndexOf(". ", StringComparison.Ordinal);
        if (dot > 0) s = s[..(dot + 1)];
        return s.Length > 220 ? s[..217] + "…" : s;
    }

    private void OnOnboardingRowChanged(OnboardingRow row)
    {
        if (row.Tweak is { } t && !row.IsLocked)
        {
            WithToggleGuard(() =>
            {
                if (!row.IsChecked) t.SyncDesiredWithLive();
                else if (t.HasOptions) t.SetDesiredOption(row.OptionKey ?? DeclarativeTweakEngine.RecommendedOptionKey(t));
                else t.SetDesiredToggle(true);
            });
        }
        UpdateOnboardingCount();
    }

    // ---------------------------------------------------------------- navegación

    private OnboardingPage CurrentOnboardingPage => _onboardingPages[_onboardingIndex];

    private void ShowOnboardingPage()
    {
        var page = CurrentOnboardingPage;
        OnboardingTitleText.Text = page.Title;
        OnboardingSubtitleText.Text = page.Subtitle;

        OnboardingProfilePanel.Visibility = page.Kind == OnboardingPageKind.Profile ? Visibility.Visible : Visibility.Collapsed;
        OnboardingRowsPanel.Visibility = page.Kind is OnboardingPageKind.Items or OnboardingPageKind.Measurements ? Visibility.Visible : Visibility.Collapsed;
        OnboardingSummaryPanel.Visibility = page.Kind == OnboardingPageKind.Summary ? Visibility.Visible : Visibility.Collapsed;

        if (page.Kind == OnboardingPageKind.Profile) HighlightOnboardingProfile();
        if (page.Kind is OnboardingPageKind.Items or OnboardingPageKind.Measurements)
        {
            OnboardingRowsList.ItemsSource = page.Rows;
            OnboardingRowsScroll.ChangeView(null, 0, null, disableAnimation: true);
            UpdateOnboardingCount();
        }
        if (page.Kind == OnboardingPageKind.Summary) _ = ShowOnboardingSummaryAsync();

        OnboardingBackBtn.IsEnabled = _onboardingIndex > 0;
        OnboardingNextBtn.Content = page.Kind == OnboardingPageKind.Summary ? "Optimizar" : "Siguiente";
        RenderOnboardingSteps();
    }

    private void RenderOnboardingSteps()
    {
        OnboardingStepsPanel.Children.Clear();
        for (int i = 0; i < _onboardingPages.Count; i++)
        {
            bool current = i == _onboardingIndex;
            bool done = i < _onboardingIndex;
            OnboardingStepsPanel.Children.Add(new TextBlock
            {
                Text = (done ? "✓  " : current ? "›  " : "    ") + _onboardingPages[i].StepName,
                FontSize = 13,
                FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = (Brush)Application.Current.Resources[current ? "KruzhTextPrimaryBrush" : done ? "KruzhTextSecondaryBrush" : "KruzhTextTertiaryBrush"],
            });
        }
    }

    private void UpdateOnboardingCount()
    {
        var page = CurrentOnboardingPage;
        if (page.Kind is not (OnboardingPageKind.Items or OnboardingPageKind.Measurements)) return;
        int locked = page.Rows.Count(r => r.IsLocked);
        int picked = page.Rows.Count(r => r.IsChecked && !r.IsLocked);
        OnboardingCountText.Text = locked > 0
            ? $"{picked} para aplicar · {locked} ya aplicados · {page.Rows.Count} en total"
            : $"{picked} de {page.Rows.Count} marcados";
    }

    private void OnboardingNext_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentOnboardingPage.Kind == OnboardingPageKind.Summary)
        {
            _ = RunOnboardingAsync();
            return;
        }
        _onboardingIndex++;
        ShowOnboardingPage();
    }

    private void OnboardingBack_Click(object sender, RoutedEventArgs e)
    {
        if (_onboardingIndex == 0) return;
        _onboardingIndex--;
        ShowOnboardingPage();
    }

    private void OnboardingSelectAll_Click(object sender, RoutedEventArgs e) => SetAllOnboardingRows(true);

    private void OnboardingSelectNone_Click(object sender, RoutedEventArgs e) => SetAllOnboardingRows(false);

    private void SetAllOnboardingRows(bool value)
    {
        foreach (var r in CurrentOnboardingPage.Rows.Where(r => !r.IsLocked)) r.IsChecked = value;
    }

    private void OnboardingProfile_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        _onboardingPreset = tag switch
        {
            "Minimal" => PresetProfileKind.Minimal,
            "Profesional" => PresetProfileKind.Profesional,
            "Entusiasta" => PresetProfileKind.Entusiasta,
            _ => PresetProfileKind.Competitivo,
        };
        BuildOnboardingPages(); // el perfil rearma lo marcado en todas las secciones
        _onboardingIndex = 0;
        ShowOnboardingPage();
    }

    private void HighlightOnboardingProfile()
    {
        var res = Application.Current.Resources;
        foreach (var (card, kind) in new[]
                 {
                     (OnbProfileMinimal, PresetProfileKind.Minimal), (OnbProfileCompetitivo, PresetProfileKind.Competitivo),
                     (OnbProfileProfesional, PresetProfileKind.Profesional), (OnbProfileEntusiasta, PresetProfileKind.Entusiasta),
                 })
        {
            bool active = kind == _onboardingPreset;
            card.BorderBrush = (Brush)res[active ? "KruzhActiveStrokeBrush" : "KruzhHairlineBorderBrush"];
            card.Background = (Brush)res[active ? "KruzhCardHoverBrush" : "KruzhCardSurfaceBrush"];
        }
        int pending = App.TweakEngine.AllTweaks.Count(t => t.IsPending);
        OnboardingSubtitleText.Text = $"Con este perfil quedan marcados {pending} cambios para tu PC. En las pantallas siguientes podés sumar o sacar cualquier cosa.";
    }

    // ---------------------------------------------------------------- resumen

    private (bool Msi, bool Gpu, bool Devices) ChosenMeasurements()
    {
        var rows = _onboardingPages.FirstOrDefault(p => p.Kind == OnboardingPageKind.Measurements)?.Rows ?? new List<OnboardingRow>();
        bool On(string key) => rows.Any(r => r.MeasurementKey == key && r.IsChecked);
        return (On(OnboardingPlan.Msi), On(OnboardingPlan.GpuAffinity), On(OnboardingPlan.DeviceAffinity));
    }

    private string BuildOnboardingSummary()
    {
        var pending = App.TweakEngine.AllTweaks.Where(t => t.IsPending && !t.IsPendingRevert).ToList();
        var (msi, gpu, dev) = ChosenMeasurements();
        var conflicts = OnboardingPlan.ConflictingTweaks(msi, gpu, dev);
        var sb = new StringBuilder();

        sb.AppendLine($"Perfil: {PresetName(_onboardingPreset)}");
        sb.AppendLine();
        sb.AppendLine($"Cambios a aplicar: {pending.Count}");
        foreach (var (name, _, match) in OnboardingSections)
        {
            int n = pending.Count(t => match(t) && !IsLab(t));
            if (n > 0) sb.AppendLine($"   • {name}: {n}");
        }
        int lab = pending.Count(IsLab);
        if (lab > 0) sb.AppendLine($"   • Laboratorio: {lab}");

        int reboot = pending.Count(t => t.RequiresReboot);
        int risky = pending.Count(t => t.IsAnticheatUnsafeBool || t.ReducesSecurityBool);
        if (reboot > 0) sb.AppendLine($"   {reboot} piden reiniciar la PC.");
        if (risky > 0) sb.AppendLine($"   {risky} bajan la seguridad o tienen riesgo con el anticheat: te los vuelve a mostrar antes de aplicar.");
        foreach (string id in conflicts)
        {
            if (App.TweakEngine.AllTweaks.FirstOrDefault(t => t.Id == id) is { } c)
            {
                sb.AppendLine($"   \"{c.Title}\" no se aplica: lo decide la medición{(c.IsLiveApplied && id == OnboardingPlan.ManualAffinityTweak ? " (se revierte para poder medir)" : "")}.");
            }
        }

        sb.AppendLine();
        if (!msi && !gpu && !dev)
        {
            sb.AppendLine("Mediciones: ninguna.");
        }
        else
        {
            sb.AppendLine("Mediciones, en este orden:");
            if (reboot > 0) sb.AppendLine("   • Reinicio, para medir con todos los cambios ya aplicados");
            if (msi) sb.AppendLine("   • Modo MSI");
            if (gpu) sb.AppendLine("   • Afinidad de la GPU");
            if (dev) sb.AppendLine("   • Afinidad del USB del mouse y del NVMe");
            int cores = App.HardwareService.CurrentProfile.CoreTopology.Count(c => !c.IsSmtSibling);
            int minutes = OnboardingPlan.EstimatedMinutes(msi, gpu, dev, msiDevices: 5, cores: Math.Max(1, cores));
            sb.AppendLine($"   Tiempo aproximado: {minutes} minutos, más los reinicios.");
            sb.AppendLine();
            sb.AppendLine("Cerrá los juegos y no uses la PC mientras mide (salvo mover el mouse cuando lo pida).");
            sb.AppendLine("Si Windows se reinicia, KRUZH se abre solo y sigue donde quedó.");
        }
        sb.AppendLine();
        sb.Append("Al terminar vas a ver un informe con todo lo que quedó.");
        return sb.ToString();
    }

    /// <summary>Resumen + avisos de conexión (puerto del mouse, EDID) que se leen en segundo plano.</summary>
    private async Task ShowOnboardingSummaryAsync()
    {
        string summary = BuildOnboardingSummary();
        OnboardingSummaryText.Text = summary + "\n\nRevisando cómo están conectados el mouse, el teclado y el monitor…";
        string warnings = FormatSetupWarnings(await CollectSetupWarningsAsync());
        if (CurrentOnboardingPage.Kind == OnboardingPageKind.Summary)
        {
            OnboardingSummaryText.Text = summary + (warnings.Length > 0 ? "\n\n" + warnings.TrimEnd() : "\n\nConexiones: mouse, teclado y monitor bien conectados.");
        }
    }

    /// <summary>Lo que KRUZH no puede arreglar solo: puerto del mouse/teclado y EDID del monitor.</summary>
    private static async Task<IReadOnlyList<KruzhLatency.Core.Devices.SetupWarning>> CollectSetupWarningsAsync()
    {
        try
        {
            return await Task.Run(() =>
            {
                var tree = new KruzhLatency.Core.Devices.DeviceTree(new KruzhLatency.Infra.Devices.CfgMgrDeviceInventory().Snapshot());
                var monitors = KruzhLatency.Infra.Display.MonitorProbe.Detect(tree).Select(KruzhLatency.Core.Display.MonitorAnalyzer.Analyze).ToList();
                return (IReadOnlyList<KruzhLatency.Core.Devices.SetupWarning>)KruzhLatency.Core.Devices.SetupAdvisor.Inputs(tree)
                    .Concat(KruzhLatency.Core.Devices.SetupAdvisor.Displays(monitors)).ToList();
            });
        }
        catch (Exception ex)
        {
            App.LogError("Setup warnings", ex);
            return Array.Empty<KruzhLatency.Core.Devices.SetupWarning>();
        }
    }

    private static string FormatSetupWarnings(IReadOnlyList<KruzhLatency.Core.Devices.SetupWarning> warnings)
    {
        if (warnings.Count == 0) return string.Empty;
        var sb = new StringBuilder("⚠ Para revisar (KRUZH no lo puede cambiar solo):\n");
        foreach (var w in warnings)
        {
            sb.AppendLine($"   • {w.Title}: {w.Detail}");
            sb.AppendLine($"     → {w.Action}");
        }
        return sb.AppendLine().ToString();
    }

    private static string PresetName(PresetProfileKind p) => p switch
    {
        PresetProfileKind.Minimal => "Normal",
        PresetProfileKind.Profesional => "Profesional",
        PresetProfileKind.Entusiasta => "Entusiasta",
        _ => "Competitivo",
    };

    // ---------------------------------------------------------------- ejecución

    private async Task RunOnboardingAsync()
    {
        var (msi, gpu, dev) = ChosenMeasurements();

        // Lo manual que toca lo mismo que una medición sale del lote (el reparto fijo de afinidad se revierte si estaba).
        WithToggleGuard(() =>
        {
            foreach (string id in OnboardingPlan.ConflictingTweaks(msi, gpu, dev))
            {
                if (App.TweakEngine.AllTweaks.FirstOrDefault(t => t.Id == id) is not { } t) continue;
                if (t.IsLiveApplied && id == OnboardingPlan.ManualAffinityTweak) t.SetDesiredToggle(false);
                else if (!t.IsLiveApplied) t.SyncDesiredWithLive();
            }
        });

        var pending = App.TweakEngine.AllTweaks.Where(t => t.IsPending).ToList();
        bool needsReboot = pending.Any(t => t.RequiresReboot);
        int toApply = pending.Count(t => !t.IsPendingRevert);

        OnboardingOverlay.Visibility = Visibility.Collapsed;
        if (!await ApplyPendingChangesFlowAsync(rebootNotice: false))
        {
            SetStatus("Optimización completa cancelada antes de aplicar.");
            return;
        }

        var queue = OnboardingPlan.Queue(needsReboot, msi, gpu, dev);
        OnboardingStore.Start(queue);
        OnboardingStore.AppendReport($"Perfil {PresetName(_onboardingPreset)}",
            toApply == 0 ? "No había cambios nuevos para aplicar." : $"{toApply} cambios aplicados (si alguno falló, se mostró al aplicar).");

        if (queue.Count > 0 && pending.Count == 0)
        {
            // Sin lote no hubo punto de restauración: se crea antes de medir.
            await App.AuditService.CreateSystemRestorePointAsync("KRUZH LATENCY — antes de la optimización completa");
        }
        await ContinueOnboardingAsync(rebootRecommended: needsReboot);
    }

    /// <summary>Corre la próxima medición de la cola; si no queda ninguna, cierra el asistente con el informe.</summary>
    private async Task ContinueOnboardingAsync(bool rebootRecommended = true)
    {
        string? next;
        try { next = await Task.Run(OnboardingStore.Pop); }
        catch (Exception ex)
        {
            App.LogError("Onboarding queue", ex);
            next = null;
        }

        switch (next)
        {
            case null:
                await FinishOnboardingAsync(rebootRecommended);
                break;
            case OnboardingPlan.Reboot:
                await RebootForOnboardingAsync();
                break;
            case OnboardingPlan.Msi:
                await StartAutoMsiAsync(interactive: false);
                break;
            case OnboardingPlan.GpuAffinity:
                await StartAutoGpuAffinityAsync(interactive: false);
                break;
            case OnboardingPlan.DeviceAffinity:
                await StartAutoAffinityAsync(interactive: false);
                break;
            default:
                await ContinueOnboardingAsync(rebootRecommended);
                break;
        }
    }

    /// <summary>Reinicio antes de medir: una sesión de un paso, retomada por la tarea programada de G5.</summary>
    private async Task RebootForOnboardingAsync()
    {
        var svc = new AutoTuneSessionService(App.AuditService);
        var session = new AutoTuneSession { Kind = OnboardingRebootKind };
        session.Steps.Add(new AutoTuneStep { Id = "reboot", Title = "seguir con las mediciones después del reinicio" });
        session.Start("reboot");

        var (ok, detail) = await Task.Run(() =>
        {
            svc.Begin(session);
            return svc.RebootToContinue(session);
        });
        if (ok)
        {
            SetStatus("Reiniciando para medir con todos los cambios aplicados…");
            return;
        }
        await Task.Run(svc.End);
        OnboardingStore.AppendReport("Reinicio", $"No se pudo reiniciar ({detail}). Se midió sin reiniciar.");
        await ContinueOnboardingAsync();
    }

    private async Task ResumeOnboardingAfterRebootAsync(AutoTuneSession session)
    {
        session.Finish("reboot", true, "Reiniciado.");
        await Task.Run(() => new AutoTuneSessionService(App.AuditService).End());
        await ContinueOnboardingAsync();
    }

    private async Task FinishOnboardingAsync(bool rebootRecommended)
    {
        string report = await Task.Run(OnboardingStore.Finish);
        RefreshLiveTweaksStateInPlace();
        AfterPciChange();

        string warnings = FormatSetupWarnings(await CollectSetupWarningsAsync());
        string text = (report.Length > 0 ? report + "\n\n" : string.Empty) + warnings +
                      "Todo lo que cambió se puede revertir desde Monitor → Cambios aplicados (y lo medido, desde Interrupciones → Revertir todo).";
        if (!rebootRecommended)
        {
            await ShowSimpleInfoModalAsync("Optimización terminada", text);
            return;
        }
        bool reboot = await ShowConfirmModalAsync("Optimización terminada",
            text + "\n\nReiniciá la PC para que todo quede aplicado.", "Reiniciar ahora", "Más tarde");
        if (reboot) await Task.Run(() => SystemRestart.Schedule(10, "KRUZH LATENCY: optimización completa terminada."));
    }

    /// <summary>
    /// Al abrir KRUZH: retomar una medición cortada por un reinicio, mostrar el resultado guardado antes de reabrirse,
    /// y seguir el asistente si quedó a mitad.
    /// </summary>
    private async Task ResumeAutoTuneAtStartupAsync()
    {
        bool hadSession = await Task.Run(() => AutoTuneSessionService.Pending() is not null);
        await ResumePendingAutoTuneAsync();

        if (!InOnboarding && AutoTuneSessionStore.TakeReport() is { } pendingReport)
        {
            await ShowSimpleInfoModalAsync(pendingReport.Title, pendingReport.Text);
        }

        if (!hadSession && InOnboarding)
        {
            bool go = await ShowConfirmModalAsync("Optimización completa en curso",
                "La optimización completa quedó a mitad (KRUZH se reabrió después de reiniciar el driver de video, o se cerró). " +
                "¿Seguir con las mediciones que faltan?", "Seguir", "Terminar acá");
            if (go) await ContinueOnboardingAsync();
            else await FinishOnboardingAsync(rebootRecommended: true);
        }
    }
}
