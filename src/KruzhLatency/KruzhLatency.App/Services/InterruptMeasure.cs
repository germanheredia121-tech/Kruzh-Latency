using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using KruzhLatency.Core.Benchmark;
using KruzhLatency.Core.Latency;
using KruzhLatency.Infra.Benchmark;
using KruzhLatency.Infra.Etw;

namespace KruzhLatency.Services;

/// <summary>
/// Spec 003: una medición de auto-tune = <see cref="LatencyComparer.MinTrials"/> ventanas ETW con la carga del dispositivo,
/// y en cada una el p99 combinado de todos los drivers donde Windows anota sus interrupciones (<see cref="InterruptDrivers"/>).
/// En las del USB del mouse la carga la genera el usuario: si en una ventana casi no movió el mouse (<see cref="MouseActivity"/>),
/// esa ventana se repite (hasta <see cref="MaxMouseRetries"/> veces) en vez de medir un USB sin actividad.
/// </summary>
internal static class InterruptMeasure
{
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(12);
    public const int MaxMouseRetries = 2;

    /// <summary>Fracción de la ventana en la que el mouse tiene que estar moviéndose para que la medición valga.</summary>
    private const double MinMovingFraction = 0.4;

    /// <summary>Mensaje para el panel del mouse (lo lee la UI): vacío si no hay nada que avisar.</summary>
    public static volatile string Notice = string.Empty;

    /// <summary>Duración estimada de una medición completa (para la cuenta regresiva de la UI).</summary>
    public static TimeSpan Expected => TimeSpan.FromTicks((Settle + Window).Ticks * LatencyComparer.MinTrials);

    private static string ToolsDir => Path.Combine(AppContext.BaseDirectory, "Assets", "Tools");

    public static List<double> Run(string msiKind, string? driverImage, LoadKind load, CancellationToken cancel)
    {
        var drivers = InterruptDrivers.For(msiKind, driverImage);
        var p99 = new List<double>();
        bool mouse = load == LoadKind.UsbGuided && MouseActivity.Available;
        int retries = 0;
        Notice = string.Empty;
        for (int i = 0; i < LatencyComparer.MinTrials && !cancel.IsCancellationRequested; i++)
        {
            using (InterruptLoad.Start(load, ToolsDir))
            {
                Thread.Sleep(Settle);
                long before = MouseActivity.Total;
                var r = DpcLatencyAnalyzer.AnalyzeAsync(Window, cancel).GetAwaiter().GetResult();
                long reports = MouseActivity.Total - before;

                if (mouse && reports < MinReports() && retries < MaxMouseRetries)
                {
                    retries++;
                    i--; // se repite esta ventana
                    Notice = $"No detecté el mouse moviéndose ({reports} reportes): repito esta medición ({retries}/{MaxMouseRetries}).";
                    continue;
                }
                Notice = string.Empty;
                p99.Add(r.CombinedP99(drivers).P99Us);
            }
        }
        return p99;
    }

    /// <summary>Reportes mínimos en una ventana: 40 % del tiempo moviéndose al polling real visto (mínimo 125 Hz).</summary>
    private static long MinReports() =>
        (long)(MinMovingFraction * Window.TotalSeconds * Math.Max(125, MouseActivity.PeakRate));
}
