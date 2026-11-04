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
using KruzhLatency.Infra.Etw;
using Microsoft.Win32;

namespace KruzhLatency.Services;

/// <summary>
/// Spec 003 / G7: afinidad de interrupciones medida para la xHCI del mouse y el NVMe. Cada candidato ("como viene Windows"
/// + núcleos) se mide con el p99 del driver; gana el de menor p99 que supere al default por más que el ruido
/// (<see cref="LatencyComparer"/>). Si ninguno gana, queda como venía. La placa de red no se toca: su afinidad la maneja RSS.
/// </summary>
public sealed class AutoAffinityService
{
    public const string Kind = "affinity";
    public const string TweakPrefix = "AutoAffinity:";
    public const string Default = "default";

    private readonly TransactionalAuditService _audit;
    private readonly AutoTuneSessionService _sessions;
    private readonly Func<IReadOnlyList<KruzhLatency.Core.Hardware.LogicalProcessor>> _topology;

    public AutoAffinityService(TransactionalAuditService audit, Func<IReadOnlyList<KruzhLatency.Core.Hardware.LogicalProcessor>> topology)
    {
        _audit = audit;
        _sessions = new AutoTuneSessionService(audit);
        _topology = topology;
    }

    private static string ToolsDir => Path.Combine(AppContext.BaseDirectory, "Assets", "Tools");

    private static string AffinityPath(string instance) =>
        $@"SYSTEM\CurrentControlSet\Enum\{instance}\Device Parameters\Interrupt Management\Affinity Policy";

    public AutoTuneSession BuildSession()
    {
        var tree = new DeviceTree(new CfgMgrDeviceInventory().Snapshot());
        var session = new AutoTuneSession { Kind = Kind };
        var topology = _topology();
        int kernelCore = topology.FirstOrDefault(t => t.Index == 0)?.PhysicalCoreIndex ?? 0;
        var firstThreads = topology.Where(t => !t.IsSmtSibling && t.PhysicalCoreIndex != kernelCore && t.Index < 64)
                                   .OrderBy(t => t.Index).Select(t => t.Index).ToList();

        // xHCI del mouse (el usuario genera la carga moviéndolo).
        var mouseController = UsbPowerPlanner.InputPlacement(tree)
            .FirstOrDefault(p => string.Equals(p.Input.ClassName, "Mouse", StringComparison.OrdinalIgnoreCase))?.Controller;
        if (mouseController is not null)
        {
            AddDevice(session, "d0", mouseController, "USB del mouse", LoadKind.UsbGuided, firstThreads.Select(c => (int?)c).Prepend(null), " · mové el mouse en círculos");
        }

        // NVMe: "como viene" vs la propuesta del planificador (cada cambio requiere reiniciar Windows).
        var plan = InterruptAffinityPlanner.Plan(topology, InterruptAffinityPlanner.Collect(tree), gpuTwoCores: false);
        foreach (var (a, i) in (plan?.Assignments.Where(x => x.Device.Kind == IrqDeviceKind.Nvme && x.Mask is not null) ?? Enumerable.Empty<IrqAssignment>()).Select((a, i) => (a, i)))
        {
            if (tree.Get(a.Device.InstanceId) is not { } node) continue;
            AddDevice(session, $"n{i}", node, "NVMe", LoadKind.Storage, new int?[] { null, a.Threads[0] }, "");
        }
        return session;
    }

    private static void AddDevice(AutoTuneSession s, string key, DeviceNode node, string label, LoadKind load, IEnumerable<int?> cpus, string hint)
    {
        string? driver = DriverFiles.ImageFileName(node.Service);
        if (string.IsNullOrEmpty(driver)) return;
        s.Data[$"{key}:instance"] = node.InstanceId;
        s.Data[$"{key}:name"] = $"{label} ({node.DisplayName})";
        s.Data[$"{key}:driver"] = driver;
        s.Data[$"{key}:load"] = load.ToString();
        s.TweakIds.Add(TweakPrefix + node.InstanceId);
        foreach (int? cpu in cpus)
        {
            string c = cpu is { } n ? n.ToString(CultureInfo.InvariantCulture) : Default;
            s.Steps.Add(new AutoTuneStep { Id = $"{key}:c:{c}", Title = $"{label}: {(cpu is null ? "como viene Windows" : $"CPU {cpu}")}{hint}" });
        }
        s.Steps.Add(new AutoTuneStep { Id = $"{key}:decide", Title = $"{label}: elegir" });
    }

    public void Begin(AutoTuneSession session) => _sessions.Begin(session);

    /// <summary>Ejecuta un paso. Reboot = true si quedó programado un reinicio para seguir.</summary>
    public (bool Ok, string Detail, bool Reboot) RunStep(AutoTuneSession session, string stepId, CancellationToken cancel)
    {
        string key = stepId[..stepId.IndexOf(':')];
        string instance = session.Data[$"{key}:instance"];
        string tweakId = TweakPrefix + instance;
        bool resumed = session.Step(stepId).Status == AutoTuneStepStatus.Running;
        session.Start(stepId);
        AutoTuneSessionService.Checkpoint(session);

        if (stepId.EndsWith(":decide", StringComparison.Ordinal)) return Decide(session, key, stepId, instance, tweakId);

        string cand = stepId[(stepId.LastIndexOf(':') + 1)..];
        if (!(resumed && session.Data.GetValueOrDefault($"{stepId}:applied") == "1"))
        {
            bool changed = ApplyCandidate(tweakId, instance, cand);
            session.Data[$"{stepId}:applied"] = "1";
            AutoTuneSessionService.Checkpoint(session);

            var restart = changed
                ? new CfgMgrDeviceInventory().Restart(instance, TimeSpan.FromSeconds(30))
                : new KruzhLatency.Core.Abstractions.DeviceRestartResult(true, 0, "Sin cambios.");
            if (!restart.Restarted && restart.ProblemCodeAfter == 0)
            {
                var (ok, detail) = _sessions.RebootToContinue(session);
                return ok ? (true, "Requiere reiniciar Windows: " + detail, true) : (false, detail, false);
            }
            if (!restart.IsHealthy)
            {
                _audit.RevertTweakFromJournal(tweakId, out _);
                new CfgMgrDeviceInventory().Restart(instance, TimeSpan.FromSeconds(30));
                session.Finish(stepId, false, $"No arrancó con esta afinidad ({restart.Detail}).");
                AutoTuneSessionService.Checkpoint(session);
                return (false, session.Step(stepId).Detail!, false);
            }
        }

        var load = Enum.Parse<LoadKind>(session.Data[$"{key}:load"]);
        var p99 = Measure(session.Data[$"{key}:driver"], load, cancel);
        session.Data[$"{stepId}:p99"] = string.Join(";", p99.Select(x => x.ToString("F1", CultureInfo.InvariantCulture)));
        session.Finish(stepId, true, $"p99 {p99.Average():F0} µs");
        AutoTuneSessionService.Checkpoint(session);
        return (true, session.Step(stepId).Detail!, false);
    }

    /// <summary>Aplica el candidato. Devuelve false si ya estaba así (no hace falta reiniciar el dispositivo).</summary>
    private bool ApplyCandidate(string tweakId, string instance, string cand)
    {
        string path = AffinityPath(instance);
        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var k = hklm.OpenSubKey(path))
        {
            object? policy = k?.GetValue("DevicePolicy");
            byte[]? mask = k?.GetValue("AssignmentSetOverride") as byte[];
            bool already = cand == Default
                ? policy is null && mask is null
                : policy is int p && p == 4 && mask is not null &&
                  InterruptAffinityPlanner.FromMaskBytes(mask) == 1UL << int.Parse(cand, CultureInfo.InvariantCulture);
            if (already) return false;
        }

        if (cand == Default)
        {
            _audit.DeleteRegistryValue(tweakId, RegistryHive.LocalMachine, path, "DevicePolicy");
            _audit.DeleteRegistryValue(tweakId, RegistryHive.LocalMachine, path, "AssignmentSetOverride");
            return true;
        }
        int cpu = int.Parse(cand, CultureInfo.InvariantCulture);
        _audit.SetRegistryValue(tweakId, RegistryHive.LocalMachine, path, "DevicePolicy", 4, RegistryValueKind.DWord);
        _audit.SetRegistryValue(tweakId, RegistryHive.LocalMachine, path, "AssignmentSetOverride", InterruptAffinityPlanner.ToMaskBytes(1UL << cpu), RegistryValueKind.Binary);
        return true;
    }

    private (bool, string, bool) Decide(AutoTuneSession s, string key, string stepId, string instance, string tweakId)
    {
        List<double> P99(string cand) => (s.Data.GetValueOrDefault($"{key}:c:{cand}:p99") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToList();

        var baseP99 = P99(Default);
        var best = s.Steps.Where(x => x.Id.StartsWith($"{key}:c:", StringComparison.Ordinal) && !x.Id.EndsWith(":" + Default, StringComparison.Ordinal))
            .Select(x => x.Id[(x.Id.LastIndexOf(':') + 1)..])
            .Select(c => (c, d: LatencyComparer.Decide(baseP99, P99(c))))
            .Where(x => x.d.KeepAlternative)
            .OrderBy(x => x.d.AltMeanUs)
            .FirstOrDefault();

        // Primero todo como estaba; si hay ganador, se aplica encima (queda en el journal para revertir).
        _audit.RevertTweakFromJournal(tweakId, out _);
        string result;
        if (best.c is not null)
        {
            ApplyCandidate(tweakId, instance, best.c);
            result = $"CPU {best.c}: {best.d.Reason}";
        }
        else
        {
            result = $"Ningún núcleo mejora a \"como viene Windows\" (p99 {baseP99.DefaultIfEmpty(0).Average():F0} µs): queda como estaba.";
        }
        var restart = new CfgMgrDeviceInventory().Restart(instance, TimeSpan.FromSeconds(30));
        if (!restart.Restarted) s.Data["pendingReboot"] = "1";
        s.Data[$"{key}:result"] = result;
        s.Finish(stepId, true, result);
        AutoTuneSessionService.Checkpoint(s);
        return (true, result, false);
    }

    private static List<double> Measure(string driver, LoadKind load, CancellationToken cancel)
    {
        var p99 = new List<double>();
        for (int i = 0; i < LatencyComparer.MinTrials && !cancel.IsCancellationRequested; i++)
        {
            using (InterruptLoad.Start(load, ToolsDir))
            {
                Thread.Sleep(AutoMsiService.Settle);
                var r = DpcLatencyAnalyzer.AnalyzeAsync(AutoMsiService.Window, cancel).GetAwaiter().GetResult();
                p99.Add(r.Drivers.FirstOrDefault(d => string.Equals(d.FileName, driver, StringComparison.OrdinalIgnoreCase))?.P99Us ?? 0);
            }
        }
        return p99;
    }

    public string Finish(AutoTuneSession session)
    {
        var sb = new StringBuilder();
        foreach (var key in session.Data.Keys.Where(k => k.EndsWith(":name", StringComparison.Ordinal)).Select(k => k[..k.IndexOf(':')]).OrderBy(k => k))
        {
            sb.Append("• ").Append(session.Data[$"{key}:name"]).Append(": ").AppendLine(session.Data.GetValueOrDefault($"{key}:result") ?? "sin resultado");
        }
        if (session.Data.ContainsKey("pendingReboot")) sb.AppendLine().AppendLine("Reiniciá la PC para que se aplique la afinidad elegida en los dispositivos que no se reinician en caliente.");
        sb.AppendLine().Append("Revertir: Interrupciones → Revertir.");
        _sessions.End();
        return sb.ToString();
    }
}
