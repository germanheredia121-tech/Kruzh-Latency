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

        if (ghosts.Count > 0)
        {
            var g = new StackPanel { Spacing = 6 };
            g.Children.Add(MicroLabel($"MONITORES NO CONECTADOS ({ghosts.Count})"));
            foreach (var a in ghosts)
            {
                g.Children.Add(Muted($"{a.Monitor.Name} · {a.Monitor.InstanceId}"));
            }
            g.Children.Add(Muted("Quedan de monitores, cables o drivers anteriores. La limpieza con backup llega en la próxima versión de esta sección."));
            DisplayMonitorsPanel.Children.Add(g);
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
            root.Children.Add(Section("Timings detallados", TimingsPanel(edid)));
            root.Children.Add(Section("Hz exactos por tipo de bloque", ExactHzPanel(edid)));
            root.Children.Add(Section("Bloque base", BasePanel(edid.Base)));
            foreach (var cta in edid.Cta) root.Children.Add(Section($"Extensión CTA-861 (revisión {cta.Revision})", CtaPanel(cta)));
            foreach (var did in edid.DisplayId) root.Children.Add(Section($"Extensión DisplayID {did.Version}", DisplayIdPanel(did)));
        }
        return card;
    }

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
