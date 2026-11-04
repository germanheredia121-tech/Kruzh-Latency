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
/// </summary>
internal static class InterruptMeasure
{
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(12);

    private static string ToolsDir => Path.Combine(AppContext.BaseDirectory, "Assets", "Tools");

    public static List<double> Run(string msiKind, string? driverImage, LoadKind load, CancellationToken cancel)
    {
        var drivers = InterruptDrivers.For(msiKind, driverImage);
        var p99 = new List<double>();
        for (int i = 0; i < LatencyComparer.MinTrials && !cancel.IsCancellationRequested; i++)
        {
            using (InterruptLoad.Start(load, ToolsDir))
            {
                Thread.Sleep(Settle);
                var r = DpcLatencyAnalyzer.AnalyzeAsync(Window, cancel).GetAwaiter().GetResult();
                p99.Add(r.CombinedP99(drivers).P99Us);
            }
        }
        return p99;
    }
}
