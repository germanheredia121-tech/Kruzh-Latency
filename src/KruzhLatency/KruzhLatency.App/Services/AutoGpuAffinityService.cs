using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using KruzhLatency.Core.Benchmark;
using KruzhLatency.Core.Devices;
using KruzhLatency.Core.Latency;
using KruzhLatency.Infra.Benchmark;
using KruzhLatency.Infra.Devices;
using Microsoft.Win32;

namespace KruzhLatency.Services;

/// <summary>
/// Spec 003 / G4: auto afinidad de GPU. Reimplementa el método de AutoGpuAffinity (GPL-3.0, sin usar su código):
/// para cada candidato ("como viene Windows" + primer hilo de cada núcleo físico) fija la afinidad de interrupciones de la GPU,
/// reinicia el driver, corre la carga sintética 3 veces y compara con <see cref="TrialComparer"/>. Aplica el ganador o nada.
/// Todo pasa por el journal con el id <see cref="TweakId"/>; cancelar o fallar deja la afinidad original.
/// </summary>
public sealed class AutoGpuAffinityService
{
    public const string TweakId = "AutoGpuAffinity";
    public static readonly TimeSpan Warmup = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan Capture = TimeSpan.FromSeconds(20);
    public const int TrialsPerCandidate = TrialComparer.MinTrials;

    public sealed record Candidate(string Name, ulong? Mask);

    private readonly TransactionalAuditService _audit;
    private readonly Func<IReadOnlyList<KruzhLatency.Core.Hardware.LogicalProcessor>> _topology;
    private readonly Dictionary<string, IReadOnlyList<FrameStats>> _results = new();

    public AutoGpuAffinityService(TransactionalAuditService audit, Func<IReadOnlyList<KruzhLatency.Core.Hardware.LogicalProcessor>> topology)
    {
        _audit = audit;
        _topology = topology;
    }

    private static string ToolsDir => Path.Combine(AppContext.BaseDirectory, "Assets", "Tools");

    /// <summary>GPU a ajustar (la primera de display presente) o el motivo por el que no se puede.</summary>
    public (IrqDevice? Gpu, string? Problem) FindGpu()
    {
        if (!SyntheticBenchmark.ToolsPresent(ToolsDir)) return (null, "Faltan las herramientas de medición junto a KRUZH.");
        if (_audit.HasBackupForTweak("PciIrqSmartBalancer"))
            return (null, "Primero revertí \"Interrupciones por núcleo físico\" (I02): las dos cosas tocan la misma afinidad.");
        var tree = new DeviceTree(new CfgMgrDeviceInventory().Snapshot());
        var gpu = InterruptAffinityPlanner.Collect(tree).FirstOrDefault(d => d.Kind == IrqDeviceKind.Gpu);
        return gpu is null ? (null, "No se encontró la placa de video.") : (gpu, null);
    }

    /// <summary>"Como viene Windows" + el primer hilo de cada núcleo físico.</summary>
    public IReadOnlyList<Candidate> Candidates()
    {
        var list = new List<Candidate> { new(TrialComparer.Default, null) };
        foreach (var t in _topology().Where(t => !t.IsSmtSibling && t.Index < 64).OrderBy(t => t.Index))
        {
            list.Add(new Candidate($"CPU {t.Index}", 1UL << t.Index));
        }
        return list;
    }

    private static string AffinityPath(IrqDevice gpu) =>
        $@"SYSTEM\CurrentControlSet\Enum\{gpu.InstanceId}\Device Parameters\Interrupt Management\Affinity Policy";

    /// <summary>Un paso del overlay: aplica el candidato, reinicia el driver y mide 3 veces.</summary>
    public (bool Ok, string Detail) Measure(IrqDevice gpu, Candidate c, CancellationToken cancel)
    {
        string path = AffinityPath(gpu);
        if (c.Mask is { } mask)
        {
            _audit.SetRegistryValue(TweakId, RegistryHive.LocalMachine, path, "DevicePolicy", 4, RegistryValueKind.DWord);
            _audit.SetRegistryValue(TweakId, RegistryHive.LocalMachine, path, "AssignmentSetOverride", InterruptAffinityPlanner.ToMaskBytes(mask), RegistryValueKind.Binary);
        }
        else
        {
            _audit.DeleteRegistryValue(TweakId, RegistryHive.LocalMachine, path, "DevicePolicy");
            _audit.DeleteRegistryValue(TweakId, RegistryHive.LocalMachine, path, "AssignmentSetOverride");
        }

        var restart = new CfgMgrDeviceInventory().Restart(gpu.InstanceId, TimeSpan.FromSeconds(30));
        if (!restart.IsHealthy) return (false, $"El driver de video no volvió bien ({restart.Detail}): se corta la medición.");

        var runs = new List<FrameStats>();
        for (int i = 0; i < TrialsPerCandidate; i++)
        {
            var (csv, error) = SyntheticBenchmark.RunGpuLoad(ToolsDir, Warmup, Capture, cancel);
            if (error is not null) return (false, error);
            if (FrameStats.From(PresentMonCsv.Parse(csv!, SyntheticBenchmark.LoadProcess + ".exe")) is { } stats) runs.Add(stats);
        }
        if (runs.Count < TrialsPerCandidate) return (false, "PresentMon no capturó suficientes frames.");
        _results[c.Name] = runs;
        return (true, $"{runs.Average(r => r.AvgFps):F0} FPS · 1% low {runs.Average(r => r.Low1Fps):F0}");
    }

    /// <summary>Último paso: decide, vuelve todo al original y aplica el ganador (si lo hay).</summary>
    public (bool Ok, string Detail, string Report) Finish(IrqDevice gpu, IReadOnlyList<Candidate> candidates)
    {
        var decision = TrialComparer.Decide(_results);
        _audit.RevertTweakFromJournal(TweakId, out _); // afinidad original
        if (decision.Winner is { } w && candidates.FirstOrDefault(c => c.Name == w)?.Mask is { } mask)
        {
            string path = AffinityPath(gpu);
            _audit.SetRegistryValue(TweakId, RegistryHive.LocalMachine, path, "DevicePolicy", 4, RegistryValueKind.DWord);
            _audit.SetRegistryValue(TweakId, RegistryHive.LocalMachine, path, "AssignmentSetOverride", InterruptAffinityPlanner.ToMaskBytes(mask), RegistryValueKind.Binary);
        }
        new CfgMgrDeviceInventory().Restart(gpu.InstanceId, TimeSpan.FromSeconds(30));
        return (true, decision.Reason, Report(decision));
    }

    /// <summary>Si algo falla o se cancela: afinidad original y driver reiniciado.</summary>
    public void Abort(IrqDevice gpu)
    {
        _audit.RevertTweakFromJournal(TweakId, out _);
        new CfgMgrDeviceInventory().Restart(gpu.InstanceId, TimeSpan.FromSeconds(30));
    }

    private static string Report(TrialDecision d)
    {
        var sb = new StringBuilder();
        sb.AppendLine(d.Reason).AppendLine();
        foreach (var c in d.Table)
        {
            sb.Append(c.Candidate == TrialComparer.Default ? "Como viene Windows" : c.Candidate)
              .Append(": ").Append(c.MeanAvgFps.ToString("F0", CultureInfo.InvariantCulture)).Append(" FPS · 1% low ")
              .Append(c.MeanLow1Fps.ToString("F0", CultureInfo.InvariantCulture))
              .Append(c.Candidate == TrialComparer.Default ? "" : $" ({c.DeltaLow1Pct:+0.0;-0.0} %)")
              .AppendLine(c.BeatsDefault ? "  ✓" : "");
        }
        sb.AppendLine().Append($"Ruido medido: {d.MarginPct:F1} %. Revertir: botón \"Revertir\" de Interrupciones.");
        return sb.ToString();
    }
}
