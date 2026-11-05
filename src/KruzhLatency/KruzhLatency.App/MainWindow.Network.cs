using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KruzhLatency.Core.Network;
using KruzhLatency.Infra.Etw;
using KruzhLatency.Infra.Network;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace KruzhLatency;

/// <summary>Página Red: prueba de ruta hasta el servidor (pérdida real por salto) y monitor del flujo de la partida.</summary>
public sealed partial class MainWindow
{
    private const string GameProcess = "cs2";
    private static readonly TimeSpan FlowDuration = TimeSpan.FromMinutes(3);
    private const int PathRounds = 120;
    private bool _netDiagBusy;

    /// <summary>Una fila de la tabla de saltos.</summary>
    public sealed class HopRow
    {
        public string Ttl { get; init; } = "";
        public string Address { get; init; } = "";
        public string Loss { get; init; } = "";
        public string Avg { get; init; } = "";
        public string Jitter { get; init; } = "";
        public string Verdict { get; init; } = "";
        public Brush LossBrush { get; init; } = new SolidColorBrush(Microsoft.UI.Colors.White);
    }

    /// <summary>Servidor de la partida = la dirección remota de la que CS2 recibe más paquetes en unos segundos.</summary>
    private async void NetDiagDetect_Click(object sender, RoutedEventArgs e)
    {
        if (_netDiagBusy) return;
        var pids = UdpFlowCapture.ProcessIds(GameProcess);
        if (pids.Count == 0)
        {
            await ShowSimpleInfoModalAsync("Detectar servidor", "Abrí CS2 y entrá a una partida (deathmatch alcanza), después volvé a tocar \"Detectar\".");
            return;
        }
        _netDiagBusy = true;
        NetDiagDetectBtn.IsEnabled = false;
        NetDiagDetectBtn.Content = "Escuchando 8 s…";
        try
        {
            var (flows, error) = await Task.Run(() => UdpFlowCapture.Capture(pids, TimeSpan.FromSeconds(8), CancellationToken.None));
            if (error is not null) { await ShowSimpleInfoModalAsync("Detectar servidor", error); return; }
            var best = flows!.OrderByDescending(kv => kv.Value.Count).FirstOrDefault();
            if (best.Value is not { Count: > 20 })
            {
                await ShowSimpleInfoModalAsync("Detectar servidor", "CS2 casi no recibió paquetes: ¿estabas en el menú? Entrá a una partida y probá de nuevo.");
                return;
            }
            NetDiagTargetBox.Text = HostOf(best.Key);
            SetStatus($"Servidor de la partida: {best.Key} ({best.Value.Count / 8} paquetes/s).");
        }
        finally
        {
            NetDiagDetectBtn.Content = "Detectar servidor de CS2";
            NetDiagDetectBtn.IsEnabled = true;
            _netDiagBusy = false;
        }
    }

    private static string HostOf(string endpoint)
    {
        if (endpoint.StartsWith('[')) return endpoint[1..endpoint.IndexOf(']')];
        int colon = endpoint.LastIndexOf(':');
        return colon > 0 ? endpoint[..colon] : endpoint;
    }

    private async void NetDiagPath_Click(object sender, RoutedEventArgs e)
    {
        if (_netDiagBusy) return;
        string target = NetDiagTargetBox.Text.Trim();
        if (target.Length == 0)
        {
            await ShowSimpleInfoModalAsync("Probar ruta", "Escribí la IP o el dominio del servidor, o abrí CS2 en una partida y tocá \"Detectar servidor de CS2\".");
            return;
        }
        var ip = await Task.Run(() => PathProbe.Resolve(target));
        if (ip is null)
        {
            await ShowSimpleInfoModalAsync("Probar ruta", $"No pude resolver \"{target}\".");
            return;
        }

        _netDiagBusy = true;
        NetDiagPathBtn.IsEnabled = false;
        NetDiagPathProgress.Visibility = Visibility.Visible;
        NetDiagPathProgress.Value = 0;
        NetDiagPathSummary.Visibility = Visibility.Visible;
        NetDiagPathSummary.Text = $"Midiendo la ruta hasta {ip} durante {PathRounds / 60} minutos…";
        NetDiagHopsList.ItemsSource = null;
        try
        {
            var progress = new Progress<(int Round, int Total)>(p =>
            {
                NetDiagPathProgress.Value = 100.0 * p.Round / p.Total;
                NetDiagPathSummary.Text = $"Midiendo la ruta hasta {ip}: {p.Round} de {p.Total} s…";
            });
            var hops = await PathProbe.SampleAsync(ip, PathRounds, progress, CancellationToken.None);
            var report = PathAnalysis.Analyze(hops, ip.ToString());
            NetDiagPathSummary.Text = report.Summary;
            NetDiagHopsList.ItemsSource = report.Hops.Select(ToRow).Prepend(new HopRow
            {
                Ttl = "#", Address = "Router", Loss = "Pérdida", Avg = "Promedio", Jitter = "Jitter", Verdict = "Lectura",
                LossBrush = (Brush)Application.Current.Resources["KruzhTextTertiaryBrush"],
            }).ToList();
        }
        catch (Exception ex)
        {
            App.LogError("Prueba de ruta", ex);
            NetDiagPathSummary.Text = $"La prueba de ruta falló: {ex.Message}";
        }
        finally
        {
            NetDiagPathProgress.Visibility = Visibility.Collapsed;
            NetDiagPathBtn.IsEnabled = true;
            _netDiagBusy = false;
        }
    }

    private static HopRow ToRow(HopReport r)
    {
        var res = Application.Current.Resources;
        string verdict = r.Verdict switch
        {
            HopVerdict.Silent => "No responde pings (normal)",
            HopVerdict.RateLimited => "Pierde pings pero los siguientes no: no es pérdida real",
            HopVerdict.LossStartsHere => "⚠ La pérdida real empieza acá",
            HopVerdict.LossInherited => "Pérdida que viene de antes",
            _ => "OK",
        };
        string zone = r.Zone switch { HopZone.Home => " · tu red", HopZone.IspAccess => " · proveedor", _ => "" };
        string loss = r.Hop.Received == 0 ? "—" : $"{r.Hop.LossPct:0.#} %";
        Brush brush = r.Verdict switch
        {
            HopVerdict.LossStartsHere or HopVerdict.LossInherited => (Brush)res["KruzhAmberBrush"],
            HopVerdict.Ok => (Brush)res["KruzhPositiveBrush"],
            _ => (Brush)res["KruzhTextSecondaryBrush"],
        };
        return new HopRow
        {
            Ttl = r.Hop.Ttl.ToString(),
            Address = (r.Hop.Address ?? "*") + zone,
            Loss = loss,
            Avg = r.Hop.Received == 0 ? "—" : $"{r.Hop.AvgMs:0} ms",
            Jitter = r.Hop.Received < 2 ? "—" : $"{r.Hop.JitterMs:0.0} ms",
            Verdict = verdict,
            LossBrush = brush,
        };
    }

    private async void NetDiagFlow_Click(object sender, RoutedEventArgs e)
    {
        if (_netDiagBusy) return;
        var pids = UdpFlowCapture.ProcessIds(GameProcess);
        if (pids.Count == 0)
        {
            await ShowSimpleInfoModalAsync("Grabar la partida", "Abrí CS2 y entrá a una partida; después tocá \"Grabar 3 minutos\" y volvé al juego.");
            return;
        }

        _netDiagBusy = true;
        NetDiagFlowBtn.IsEnabled = false;
        NetDiagFlowProgress.Visibility = Visibility.Visible;
        NetDiagFlowSummary.Visibility = Visibility.Visible;
        NetDiagFlowDetail.Visibility = Visibility.Collapsed;
        NetDiagFlowSummary.Text = "Grabando: volvé al juego y jugá normal 3 minutos.";
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            double s = (DateTime.UtcNow - started).TotalSeconds;
            NetDiagFlowProgress.Value = Math.Min(100, 100 * s / FlowDuration.TotalSeconds);
            NetDiagFlowSummary.Text = $"Grabando: {Math.Max(0, FlowDuration.TotalSeconds - s):0} s restantes. Volvé al juego y jugá normal.";
        };
        timer.Start();
        try
        {
            var (flows, error) = await Task.Run(() => UdpFlowCapture.Capture(pids, FlowDuration, CancellationToken.None));
            if (error is not null) { NetDiagFlowSummary.Text = error; return; }
            var best = flows!.OrderByDescending(kv => kv.Value.Count).FirstOrDefault();
            var report = FlowAnalysis.Analyze(best.Key ?? "-", best.Value ?? new List<double>());
            NetDiagFlowSummary.Text = report.Summary;

            var sb = new StringBuilder();
            sb.AppendLine($"Servidor: {report.Remote} · {report.Packets} paquetes en {report.DurationSeconds:0} s · intervalo normal {report.MedianIntervalMs:0.0} ms · p99 {report.P99IntervalMs:0.0} ms");
            if (report.Gaps.Count > 0)
            {
                sb.AppendLine("Huecos (segundo de la grabación → duración):");
                foreach (var g in report.Gaps.Take(40)) sb.AppendLine($"   {g.AtSeconds,6:0.0} s → {g.GapMs:0} ms");
                if (report.Gaps.Count > 40) sb.AppendLine($"   … y {report.Gaps.Count - 40} más");
            }
            if (string.IsNullOrEmpty(NetDiagTargetBox.Text) && report.Remote != "-") NetDiagTargetBox.Text = HostOf(report.Remote); // para probar la ruta después
            NetDiagFlowDetail.Text = sb.ToString().TrimEnd();
            NetDiagFlowDetail.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            App.LogError("Monitor de red", ex);
            NetDiagFlowSummary.Text = $"La grabación falló: {ex.Message}";
        }
        finally
        {
            timer.Stop();
            NetDiagFlowProgress.Visibility = Visibility.Collapsed;
            NetDiagFlowBtn.IsEnabled = true;
            _netDiagBusy = false;
        }
    }
}
