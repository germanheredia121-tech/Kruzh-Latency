using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using KruzhLatency.Core.Devices;
using KruzhLatency.Core.Display;
using KruzhLatency.Infra.Devices;
using KruzhLatency.Infra.Display;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace KruzhLatency;

/// <summary>
/// Sección "Pantalla" (spec 002 / F16, C3): análisis de solo lectura. Por monitor: señal actual, máximo, ancho de banda,
/// notas y el EDID completo decodificado en secciones plegables (timings, bloque base, CTA-861, DisplayID, Hz exactos).
/// </summary>
public sealed partial class MainWindow
{
    private bool _displayLoading;

    private void DisplayRefreshBtn_Click(object sender, RoutedEventArgs e) => _ = RefreshDisplayUiAsync();

    private async Task RefreshDisplayUiAsync()
    {
        if (_displayLoading) return;
        _displayLoading = true;
        DisplayStatusText.Text = "Leyendo monitores…";
        DisplayStatusText.Visibility = Visibility.Visible;
        try
        {
            var analyses = await Task.Run(() =>
            {
                var tree = new DeviceTree(new CfgMgrDeviceInventory().Snapshot());
                return MonitorProbe.Detect(tree).Select(MonitorAnalyzer.Analyze).ToList();
            });
            RenderDisplay(analyses);
            DisplayStatusText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            App.LogError("Pantalla", ex);
            DisplayStatusText.Text = $"No se pudieron leer los monitores: {ex.Message}";
        }
        finally
        {
            _displayLoading = false;
        }
    }

    private void RenderDisplay(IReadOnlyList<MonitorAnalysis> analyses)
    {
        DisplayMonitorsPanel.Children.Clear();
        var present = analyses.Where(a => a.Monitor.IsPresent).ToList();
        var ghosts = analyses.Where(a => !a.Monitor.IsPresent).ToList();

        if (present.Count == 0)
        {
            DisplayMonitorsPanel.Children.Add(Muted("Windows no reporta ningún monitor conectado."));
        }
        foreach (var a in present)
        {
            DisplayMonitorsPanel.Children.Add(MonitorCard(a));
        }

        _displayGhostIds = ghosts.Select(a => a.Monitor.InstanceId).ToList();
        DisplayMonitorsPanel.Children.Add(CleanupCard(ghosts));
    }

    // ---------------------------------------------------------------- C4: limpieza

    private List<string> _displayGhostIds = new();

    private UIElement CleanupCard(IReadOnlyList<MonitorAnalysis> ghosts)
    {
        var s = new StackPanel { Spacing = 8 };
        s.Children.Add(MicroLabel("LIMPIEZA"));
        if (ghosts.Count > 0)
        {
            s.Children.Add(Muted($"Monitores no conectados ({ghosts.Count}): " + string.Join(", ", ghosts.Select(g => g.Monitor.Name))));
        }
        s.Children.Add(Muted("Borra los monitores fantasma y la configuración de pantallas que guarda Windows por cada monitor y cable " +
                             "(GraphicsDrivers\\Configuration y \\Connectivity). Sirve cuando quedan modos viejos después de cambiar de monitor o de usar CRU. " +
                             "Se exporta antes y se puede revertir. Al reiniciar, Windows la vuelve a armar: revisá que vuelvas a estar en tus Hz."));
        var btn = new Button
        {
            Content = ghosts.Count > 0 ? "Limpiar fantasmas y caché de pantallas" : "Limpiar caché de pantallas",
            Style = (Style)Application.Current.Resources["KruzhGhostButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Left
        };
        btn.Click += DisplayCleanupBtn_Click;
        s.Children.Add(btn);
        return s;
    }

    private async void DisplayCleanupBtn_Click(object sender, RoutedEventArgs e)
    {
        var ghosts = _displayGhostIds.ToList();
        bool go = await ShowConfirmModalAsync("Limpiar pantallas",
            (ghosts.Count > 0 ? $"Se borran {ghosts.Count} monitores no conectados (no se puede deshacer, pero si los volvés a conectar Windows los reinstala). " : "") +
            "Se exporta y se borra la configuración guardada de pantallas; se puede revertir desde Monitor → Cambios aplicados. " +
            "Antes se crea un punto de restauración. Después hay que reiniciar (nunca se reinicia el driver en caliente).",
            "Limpiar");
        if (!go) return;

        var steps = new List<OverlayStep>
        {
            new("Punto de restauración", () =>
            {
                var r = new KruzhLatency.Infra.Wmi.WmiRestorePoint().CreateAsync("KRUZH LATENCY - limpieza de pantallas").GetAwaiter().GetResult();
                return (true, r.Detail); // si Windows lo saltea (límite de 24 h) igual se sigue: hay export propio
            }),
            new("Exportar y limpiar la caché de pantallas", () =>
            {
                int n = App.AuditService.ResetDisplayCache("DisplayCacheReset");
                return (true, $"{n} entradas borradas (export en la carpeta de backups de KRUZH).");
            }, RequiresReboot: true)
        };
        foreach (var id in ghosts)
        {
            steps.Add(new OverlayStep($"Borrar monitor fantasma {id}", () =>
            {
                var (removed, detail) = new CfgMgrDeviceInventory().RemovePhantom(id);
                return (removed, detail);
            }));
        }

        await RunStepsWithOverlayAsync("Limpiando pantallas", "Caché de Windows y monitores fantasma", steps);
        await RefreshDisplayUiAsync();

        if (await ShowConfirmModalAsync("Reiniciar", "Para que Windows vuelva a detectar los monitores hay que reiniciar. ¿Reiniciar ahora?", "Reiniciar"))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "shutdown.exe", Arguments = "/r /t 5", CreateNoWindow = true, UseShellExecute = false
            });
        }
    }

    private UIElement MonitorCard(MonitorAnalysis a)
    {
        var m = a.Monitor;
        var card = new Border { Style = (Style)Application.Current.Resources["KruzhCardStyle"], Padding = new Thickness(22, 18, 22, 18) };
        var root = new StackPanel { Spacing = 14 };
        card.Child = root;

        // Encabezado
        var head = new StackPanel { Spacing = 2 };
        head.Children.Add(new TextBlock { Text = m.Name, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brush("KruzhTextPrimaryBrush") });
        string conn = m.Current?.Connection ?? "sin señal activa";
        string mfg = a.Parsed is { } p ? $"{p.Base.ManufacturerId} · EDID {p.Base.Version}" : "";
        head.Children.Add(Muted(string.Join(" · ", new[] { conn, mfg, m.HasOverride ? "EDID modificado (override)" : "" }.Where(s => s.Length > 0))));
        root.Children.Add(head);

        // Métricas
        var grid = new Grid { ColumnSpacing = 28, RowSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddMetric(grid, 0, "SEÑAL ACTUAL", m.Current is { } c ? $"{c.SignalWidth}x{c.SignalHeight} @ {Hz(c.RefreshHz)}" : "—",
            m.Current is { } c2 ? $"Escritorio {c2.DesktopWidth}x{c2.DesktopHeight} · total {c2.HTotal}x{c2.VTotal} · {Mhz(c2.PixelRateHz)} · escalado {c2.Scaling}" : null);
        AddMetric(grid, 1, "MÁXIMO DEL MONITOR", a.NativeMax is { } n ? $"{n.HActive}x{n.VActive} @ {Hz(n.RefreshHz)}" : "—",
            a.NativeMax is { } n2 ? $"Total {n2.HTotal}x{n2.VTotal} · {Mhz(n2.PixelClockHz)}" : a.ParseError);
        AddMetric(grid, 2, "ANCHO DE BANDA", a.NativeMax is null ? "—" : $"{a.UncompressedGbps:0.00} Gbps",
            a.NativeMax is null ? null : a.NeedsDscOnHbr2 ? "En DisplayPort HBR2 necesita DSC" : "Entra en DisplayPort HBR2 sin DSC");
        root.Children.Add(grid);

        foreach (var note in a.Notes.Where(n => !n.StartsWith("El modo máximo pide", StringComparison.Ordinal)))
        {
            root.Children.Add(Muted("• " + note));
        }

        if (a.Parsed is { } edid)
        {
            if (a.NativeMax is not null && m.Current is not null)
            {
                root.Children.Add(Section("Resolución 4:3 al refresco nativo (CRU)", GuidePanel(a)));
            }
            root.Children.Add(Section("Timings detallados", TimingsPanel(edid)));
            root.Children.Add(Section("Hz exactos por tipo de bloque", ExactHzPanel(edid)));
            root.Children.Add(Section("Bloque base", BasePanel(edid.Base)));
            foreach (var cta in edid.Cta) root.Children.Add(Section($"Extensión CTA-861 (revisión {cta.Revision})", CtaPanel(cta)));
            foreach (var did in edid.DisplayId) root.Children.Add(Section($"Extensión DisplayID {did.Version}", DisplayIdPanel(did)));
        }
        return card;
    }

    // ---------------------------------------------------------------- C5: guía de CRU (modo manual)

    private static readonly (int W, int H)[] StretchedOptions = { (1280, 960), (1440, 1080), (1024, 768) };

    private UIElement GuidePanel(MonitorAnalysis a)
    {
        var root = new StackPanel { Spacing = 14 };
        root.Children.Add(Muted("Crea una resolución 4:3 que el monitor escala por hardware (sin pasar por la GPU), al refresco nativo exacto. " +
                                "Podés hacerlo a mano con CRU siguiendo esta guía, armada con los datos de tu monitor."));

        var options = StretchedOptions.Where(o => o.H <= a.NativeMax!.VActive).ToList();
        var combo = new ComboBox { MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (w, h) in options) combo.Items.Add($"{w}x{h}");
        combo.SelectedIndex = 0;

        var stepsHost = new StackPanel { Spacing = 14 };
        var verifyHost = new StackPanel { Spacing = 6 };
        CruGuide? current = null;

        void Render()
        {
            var (w, h) = options[Math.Max(0, combo.SelectedIndex)];
            current = CruGuideBuilder.Build(new CruGuideRequest(a, w, h, CurrentGpuVendor()));
            stepsHost.Children.Clear();
            verifyHost.Children.Clear();
            stepsHost.Children.Add(Muted($"Resultado: {w}x{h} @ {Hz(current.StretchedChoice.Chosen.RefreshHz)} en DisplayID {current.DisplayIdVersion}. {current.StretchedChoice.Reason}"));
            foreach (var step in current.Steps)
            {
                var s = new StackPanel { Spacing = 4 };
                s.Children.Add(new TextBlock { Text = step.Title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Brush("KruzhTextPrimaryBrush") });
                foreach (var line in step.Lines) s.Children.Add(Muted(line));
                if (step.Link is { } link)
                {
                    s.Children.Add(new HyperlinkButton { Content = "Abrir la página oficial de CRU", NavigateUri = new Uri(link), Padding = new Thickness(0) });
                }
                stepsHost.Children.Add(s);
            }
        }

        combo.SelectionChanged += (_, _) => Render();
        Render();

        var verify = new Button { Content = "Verificar", Style = (Style)Application.Current.Resources["KruzhGhostButtonStyle"], HorizontalAlignment = HorizontalAlignment.Left };
        verify.Click += async (_, _) =>
        {
            if (current is null) return;
            verifyHost.Children.Clear();
            verifyHost.Children.Add(Muted("Releyendo el EDID y la señal…"));
            string id = a.Monitor.InstanceId;
            var now = await Task.Run(() =>
            {
                var tree = new DeviceTree(new CfgMgrDeviceInventory().Snapshot());
                var m = MonitorProbe.Detect(tree).FirstOrDefault(x => string.Equals(x.InstanceId, id, StringComparison.OrdinalIgnoreCase));
                return m is null ? null : MonitorAnalyzer.Analyze(m);
            });
            verifyHost.Children.Clear();
            if (now is null)
            {
                verifyHost.Children.Add(Muted("El monitor ya no aparece conectado."));
                return;
            }
            foreach (var c in CruGuideBuilder.Verify(current, now))
            {
                verifyHost.Children.Add(new TextBlock
                {
                    Text = $"{(c.Ok ? "✓" : "✗")}  {c.Name} — {c.Detail}",
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush(c.Ok ? "KruzhPositiveBrush" : "KruzhAmberBrush")
                });
            }
        };

        var auto = new Button { Content = "Aplicar automáticamente", Style = (Style)Application.Current.Resources["KruzhGhostButtonStyle"], HorizontalAlignment = HorizontalAlignment.Left };
        auto.Click += async (_, _) =>
        {
            if (current is not null) await ApplyEdidAutomaticallyAsync(a, current);
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        header.Children.Add(new TextBlock { Text = "Resolución 4:3", VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, Foreground = Brush("KruzhTextSecondaryBrush") });
        header.Children.Add(combo);
        root.Children.Add(header);
        root.Children.Add(MicroLabel("MODO AUTOMÁTICO"));
        root.Children.Add(Muted("KRUZH arma el mismo EDID que la guía (bloque base + DisplayID con el nativo y la 4:3, sin CTA-861) y lo escribe como override. " +
                                "Después del reinicio te pregunta si se ve bien: si no confirmás en 60 segundos, vuelve solo al EDID anterior y reinicia."));
        root.Children.Add(auto);
        root.Children.Add(MicroLabel("MODO MANUAL (CRU)"));
        root.Children.Add(stepsHost);
        root.Children.Add(verify);
        root.Children.Add(verifyHost);
        return root;
    }

    // ---------------------------------------------------------------- C6: modo automático (ADR-009)

    private async Task ApplyEdidAutomaticallyAsync(MonitorAnalysis a, CruGuide guide)
    {
        string instance = a.Monitor.InstanceId;
        byte[]? original = a.Monitor.Edid;
        if (original is null)
        {
            SetStatus("Windows no tiene el EDID del monitor: no se puede armar el override.");
            return;
        }

        byte[] edid = EdidBuilder.BuildDisplayIdOverride(original, guide.DisplayIdVersion == "2.0", new[] { guide.Native, guide.Stretched });
        var check = EdidParser.Parse(edid); // nunca se escribe algo que el propio parser no lee bien
        if (!check.Base.ChecksumValid || check.DisplayId.Count != 1 || !check.DisplayId[0].ChecksumValid)
        {
            SetStatus("El EDID generado no pasó la validación: no se escribió nada.");
            return;
        }

        bool go = await ShowConfirmModalAsync("Aplicar EDID automáticamente",
            $"Se reemplaza CTA-861 por DisplayID {guide.DisplayIdVersion} con {guide.Native.HActive}x{guide.Native.VActive} y " +
            $"{guide.Stretched.HActive}x{guide.Stretched.VActive} a {Hz(guide.StretchedChoice.Chosen.RefreshHz)}. " +
            (a.Parsed?.Cta.Count > 0 ? "Se pierden el audio por el cable, HDR y FreeSync del monitor. " : "") +
            "Se crea un punto de restauración y, al reiniciar, KRUZH te pregunta si se ve bien: si no contestás en 60 segundos vuelve solo al EDID anterior.",
            "Aplicar y reiniciar");
        if (!go) return;

        EdidRollbackPaths? paths = null;
        var results = await RunStepsWithOverlayAsync("Aplicando EDID", "Con vuelta atrás automática", new[]
        {
            new OverlayStep("Punto de restauración", () =>
            {
                var r = new KruzhLatency.Infra.Wmi.WmiRestorePoint().CreateAsync("KRUZH LATENCY - EDID").GetAwaiter().GetResult();
                return (true, r.Detail);
            }),
            new OverlayStep("Preparar la vuelta atrás", () =>
            {
                string folder = EdidAutoApply.SecureFolder();
                string? previous = EdidAutoApply.ExportCurrentOverride(instance, folder);
                paths = new EdidRollbackPaths(instance, folder, previous);
                EdidAutoApply.InstallRollback(paths, Environment.ProcessPath!);
                return (true, "Tareas de confirmación y vuelta atrás programadas.");
            }),
            new OverlayStep("Escribir EDID_OVERRIDE", () =>
            {
                App.AuditService.WriteEdidOverride("EdidAutoOverride", instance, edid);
                var (_, over) = MonitorProbe.ReadEdids(instance);
                return over is not null && over.AsSpan().SequenceEqual(edid)
                    ? (true, "Override escrito y verificado.")
                    : (false, "El override leído no coincide con el generado.");
            }, RequiresReboot: true)
        });

        if (results.Any(r => !r.Success))
        {
            // Algo falló: no se deja nada a medias (sin override o sin vuelta atrás).
            if (paths is not null) EdidAutoApply.Confirm(paths);
            App.AuditService.RevertTweakFromJournal("EdidAutoOverride", out _);
            await ShowSimpleInfoModalAsync("No se aplicó", "Un paso falló y se deshizo todo. No hace falta reiniciar.");
            return;
        }

        if (await ShowConfirmModalAsync("Reiniciar", "El EDID nuevo se lee al arrancar. ¿Reiniciar ahora?", "Reiniciar"))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "shutdown.exe", Arguments = "/r /t 5", CreateNoWindow = true, UseShellExecute = false
            });
        }
    }

    /// <summary>Al iniciar después de aplicar: "¿se ve bien?". Sí → se confirma; No → vuelve atrás y reinicia. Si no contesta, lo hace la tarea de SYSTEM.</summary>
    private async Task ConfirmPendingEdidAsync()
    {
        string? instance = EdidAutoApply.PendingConfirmation();
        if (instance is null) return;

        string folder = EdidAutoApply.SecureFolder();
        string previous = System.IO.Path.Combine(folder, "edid-previo.reg");
        var paths = new EdidRollbackPaths(instance, folder, System.IO.File.Exists(previous) ? previous : null);

        bool ok = await ShowConfirmModalAsync("¿Se ve bien la pantalla?",
            "KRUZH aplicó un EDID nuevo. Si ves esto bien, confirmá. Si no confirmás en 60 segundos desde que iniciaste sesión, " +
            "Windows vuelve solo al EDID anterior y reinicia.",
            "Se ve bien", "Volver atrás");
        try
        {
            if (ok)
            {
                EdidAutoApply.Confirm(paths);
                SetStatus("EDID confirmado. Revisá en Pantalla → Verificar.");
            }
            else
            {
                App.AuditService.RevertTweakFromJournal("EdidAutoOverride", out _);
                EdidAutoApply.RollbackNow(paths); // borra el override, limpia las tareas y reinicia
            }
        }
        catch (Exception ex)
        {
            App.LogError("Confirmación EDID", ex);
        }
    }

    private static GpuVendor CurrentGpuVendor() => App.HardwareService.CurrentProfile.GpuVendor switch
    {
        KruzhLatency.Models.GpuVendorKind.Nvidia => GpuVendor.Nvidia,
        KruzhLatency.Models.GpuVendorKind.Amd => GpuVendor.Amd,
        KruzhLatency.Models.GpuVendorKind.Intel => GpuVendor.Intel,
        _ => GpuVendor.Unknown
    };

    // ---------------------------------------------------------------- secciones del EDID

    private UIElement TimingsPanel(Edid edid)
    {
        var p = new StackPanel { Spacing = 10 };
        foreach (var t in edid.AllDetailedTimings)
        {
            p.Children.Add(Line($"{t.HActive}x{t.VActive}{(t.Interlaced ? "i" : "")} @ {Hz(t.RefreshHz)}{(t.IsPreferred ? "  (preferido)" : "")}",
                $"{SourceName(t.Source)} · {Mhz(t.PixelClockHz)} · total {t.HTotal}x{t.VTotal} · " +
                $"H: front {t.HFrontPorch}, sync {t.HSyncWidth}, back {t.HBackPorch} · V: front {t.VFrontPorch}, sync {t.VSyncWidth}, back {t.VBackPorch} · " +
                $"{t.HorizontalKhz:0.000} kHz"));
        }
        return p;
    }

    private UIElement ExactHzPanel(Edid edid)
    {
        var p = new StackPanel { Spacing = 10 };
        if (edid.NativeMax is not { } native)
        {
            p.Children.Add(Muted("Sin timings detallados."));
            return p;
        }
        double target = Math.Round(native.RefreshHz);
        p.Children.Add(Muted($"El pixel clock se guarda en pasos de 10 kHz (Detailed Timing y DisplayID 1.3) o 1 kHz (DisplayID 2.0). Según el total de cada modo, solo algunos refrescos son exactos. Objetivo: {target:0} Hz."));

        void Add(string title, int hTotal, int vTotal)
        {
            var r = RefreshCalculator.RecommendDisplayId(hTotal, vTotal, target);
            p.Children.Add(Line(title,
                $"DisplayID 1.3: {Hz(r.Type1.RefreshHz)} · DisplayID 2.0: {Hz(r.Type7.RefreshHz)} → {r.Reason}"));
        }

        Add($"{native.HActive}x{native.VActive} con los timings de fábrica (total {native.HTotal}x{native.VTotal})", native.HTotal, native.VTotal);
        foreach (var (w, h) in new[] { (native.VActive * 4 / 3, native.VActive), (1280, 960) }.Distinct().Where(r => r.Item1 < native.HActive))
        {
            var cvt = RefreshCalculator.CvtReducedBlanking(w, h, target);
            Add($"{w}x{h} 4:3 con CVT reducido (\"Automatic PC\" de CRU, total {cvt.HTotal}x{cvt.VTotal})", cvt.HTotal, cvt.VTotal);
        }
        return p;
    }

    private UIElement BasePanel(EdidBaseBlock b)
    {
        var p = new StackPanel { Spacing = 8 };
        p.Children.Add(KeyValue("Fabricante / producto", $"{b.ManufacturerId} · código {b.ProductCode:X4} · fabricado semana {b.WeekOfManufacture} de {b.YearOfManufacture}"));
        p.Children.Add(KeyValue("Entrada", b.IsDigital ? $"Digital{(b.Interface is null ? "" : " · " + b.Interface)}{(b.BitsPerColor is null ? "" : $" · {b.BitsPerColor} bits por color")}" : "Analógica"));
        p.Children.Add(KeyValue("Tamaño", $"{b.ScreenWidthCm} x {b.ScreenHeightCm} cm{(b.Gamma is null ? "" : $" · gamma {b.Gamma:0.00}")}"));
        if (b.Range is { } r)
            p.Children.Add(KeyValue("Rango", $"{r.MinVHz}–{r.MaxVHz} Hz vertical · {r.MinHKhz}–{r.MaxHKhz} kHz horizontal · pixel clock máx. {r.MaxPixelClockMHz} MHz"));
        if (b.EstablishedTimings.Count > 0) p.Children.Add(KeyValue("Established", string.Join(", ", b.EstablishedTimings)));
        if (b.StandardTimings.Count > 0) p.Children.Add(KeyValue("Standard", string.Join(", ", b.StandardTimings.Select(s => $"{s.Width}x{s.Height} @ {s.RefreshHz}"))));
        p.Children.Add(KeyValue("Extensiones", $"{b.ExtensionCount} · checksum {(b.ChecksumValid ? "correcto" : "INVÁLIDO")}"));
        return p;
    }

    private UIElement CtaPanel(CtaExtension cta)
    {
        var p = new StackPanel { Spacing = 8 };
        var flags = new List<string>();
        if (cta.BasicAudio) flags.Add("audio básico");
        if (cta.YCbCr444) flags.Add("YCbCr 4:4:4");
        if (cta.YCbCr422) flags.Add("YCbCr 4:2:2");
        if (cta.Underscan) flags.Add("underscan");
        p.Children.Add(KeyValue("Soporta", flags.Count == 0 ? "—" : string.Join(", ", flags)));
        foreach (var d in cta.DataBlocks)
        {
            p.Children.Add(KeyValue(d.Name, d.Summary.Length > 0 ? d.Summary : $"{d.Payload.Length} bytes"));
        }
        p.Children.Add(KeyValue("Timings detallados", $"{cta.DetailedTimings.Count} · checksum {(cta.ChecksumValid ? "correcto" : "INVÁLIDO")}"));
        return p;
    }

    private UIElement DisplayIdPanel(DisplayIdExtension did)
    {
        var p = new StackPanel { Spacing = 8 };
        foreach (var b in did.DataBlocks)
        {
            p.Children.Add(KeyValue(b.Name, $"revisión {b.Revision} · {b.Payload.Length} bytes"));
        }
        p.Children.Add(KeyValue("Checksum", did.ChecksumValid ? "correcto" : "INVÁLIDO"));
        return p;
    }

    // ---------------------------------------------------------------- helpers de UI

    private static string Hz(double hz) => hz.ToString("0.000", CultureInfo.InvariantCulture) + " Hz";
    private static string Mhz(long hz) => (hz / 1e6).ToString("0.000", CultureInfo.InvariantCulture) + " MHz";

    private static string SourceName(TimingSource s) => s switch
    {
        TimingSource.DisplayIdType7 => "DisplayID 2.0 Type VII",
        TimingSource.DisplayIdType1 => "DisplayID 1.3 Type I",
        _ => "Detailed Timing"
    };

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private static TextBlock MicroLabel(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["KruzhMicroLabelStyle"] };

    private static TextBlock Muted(string text) =>
        new() { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = Brush("KruzhTextTertiaryBrush") };

    private static UIElement Line(string title, string detail)
    {
        var s = new StackPanel { Spacing = 2 };
        s.Children.Add(new TextBlock { Text = title, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Foreground = Brush("KruzhTextPrimaryBrush") });
        s.Children.Add(Muted(detail));
        return s;
    }

    private static UIElement KeyValue(string key, string value)
    {
        var g = new Grid { ColumnSpacing = 16 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var k = new TextBlock { Text = key, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = Brush("KruzhTextSecondaryBrush") };
        var v = new TextBlock { Text = value, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = Brush("KruzhTextPrimaryBrush"), IsTextSelectionEnabled = true };
        Grid.SetColumn(v, 1);
        g.Children.Add(k);
        g.Children.Add(v);
        return g;
    }

    private static void AddMetric(Grid grid, int column, string label, string value, string? detail)
    {
        var s = new StackPanel { Spacing = 3 };
        s.Children.Add(MicroLabel(label));
        s.Children.Add(new TextBlock { Text = value, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brush("KruzhTextPrimaryBrush") });
        if (!string.IsNullOrEmpty(detail)) s.Children.Add(Muted(detail));
        Grid.SetColumn(s, column);
        grid.Children.Add(s);
    }

    private static Expander Section(string title, UIElement content) => new()
    {
        Header = title,
        Content = content,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        IsExpanded = false
    };
}
