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

    /// <summary>true si en algún momento se reinició el driver de video (la ventana de KRUZH conviene reabrirla).</summary>
    public bool GpuRestarted { get; private set; }

    /// <summary>Máscara actual de la GPU (null = sin afinidad fija, como viene Windows).</summary>
    private static ulong? CurrentMask(IrqDevice gpu)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var k = hklm.OpenSubKey(AffinityPath(gpu));
        return k?.GetValue("DevicePolicy") is int p && p == 4 && k.GetValue("AssignmentSetOverride") is byte[] b
            ? InterruptAffinityPlanner.FromMaskBytes(b)
            : null;
    }

    /// <summary>Escribe la afinidad del candidato y reinicia el driver SOLO si cambió algo.</summary>
    private (bool Ok, string? Error) ApplyAndRestart(IrqDevice gpu, ulong? mask)
    {
        if (CurrentMask(gpu) == mask) return (true, null);
        string path = AffinityPath(gpu);
        if (mask is { } m)
        {
            _audit.SetRegistryValue(TweakId, RegistryHive.LocalMachine, path, "DevicePolicy", 4, RegistryValueKind.DWord);
            _audit.SetRegistryValue(TweakId, RegistryHive.LocalMachine, path, "AssignmentSetOverride", InterruptAffinityPlanner.ToMaskBytes(m), RegistryValueKind.Binary);
        }
        else
        {
            _audit.DeleteRegistryValue(TweakId, RegistryHive.LocalMachine, path, "DevicePolicy");
            _audit.DeleteRegistryValue(TweakId, RegistryHive.LocalMachine, path, "AssignmentSetOverride");
        }
        GpuRestarted = true;
        var restart = new CfgMgrDeviceInventory().Restart(gpu.InstanceId, TimeSpan.FromSeconds(30));
        if (!restart.IsHealthy) return (false, $"El driver de video no volvió bien ({restart.Detail}).");
        Thread.Sleep(TimeSpan.FromSeconds(5)); // el driver termina de inicializar la salida de video
        return (true, null);
    }

    /// <summary>Chequeo previo, sin tocar la GPU: la carga 3D abre y se mantiene.</summary>
    public string? Preflight() => SyntheticBenchmark.Preflight(ToolsDir, TimeSpan.FromSeconds(4));

    /// <summary>Un paso del overlay: aplica el candidato (reinicia el driver si cambió) y mide 3 veces.</summary>
    public (bool Ok, string Detail) Measure(IrqDevice gpu, Candidate c, CancellationToken cancel)
    {
        var (ok, error) = ApplyAndRestart(gpu, c.Mask);
        if (!ok) return (false, error + " Se corta la medición.");

        var runs = new List<FrameStats>();
        for (int i = 0; i < TrialsPerCandidate; i++)
        {
            var (csv, err) = SyntheticBenchmark.RunGpuLoad(ToolsDir, Warmup, Capture, cancel);
            if (err is not null) return (false, err);
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
        var winnerMask = decision.Winner is { } w ? candidates.FirstOrDefault(c => c.Name == w)?.Mask : null;
        _audit.RevertTweakFromJournal(TweakId, out _); // afinidad original en el registro
        if (winnerMask is { } mask)
        {
            string path = AffinityPath(gpu);
            _audit.SetRegistryValue(TweakId, RegistryHive.LocalMachine, path, "DevicePolicy", 4, RegistryValueKind.DWord);
            _audit.SetRegistryValue(TweakId, RegistryHive.LocalMachine, path, "AssignmentSetOverride", InterruptAffinityPlanner.ToMaskBytes(mask), RegistryValueKind.Binary);
        }
        if (GpuRestarted)
        {
            // El driver tiene que releer la afinidad final.
            new CfgMgrDeviceInventory().Restart(gpu.InstanceId, TimeSpan.FromSeconds(30));
        }
        return (true, decision.Reason, Report(decision));
    }

    /// <summary>Si algo falla o se cancela: afinidad original y, si se había reiniciado, driver reiniciado de nuevo.</summary>
    public void Abort(IrqDevice gpu)
    {
        _audit.RevertTweakFromJournal(TweakId, out _);
        if (GpuRestarted) new CfgMgrDeviceInventory().Restart(gpu.InstanceId, TimeSpan.FromSeconds(30));
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
