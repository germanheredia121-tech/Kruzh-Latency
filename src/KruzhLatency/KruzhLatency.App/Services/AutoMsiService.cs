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
/// Spec 003 / G6: auto MSI. Por cada dispositivo que declara MSI/MSI-X se mide el p99 de su driver en el estado actual
/// y con MSI invertido (on↔off), bajo una carga que lo hace interrumpir. Se queda la alternativa solo si baja el p99 más que
/// el ruido (<see cref="LatencyComparer"/>); si el dispositivo no arranca con el cambio, vuelve atrás y queda "no apto".
/// Los dispositivos que no se reinician en caliente (NVMe de arranque, a veces la GPU) usan el reinicio con retomado (G5).
/// </summary>
public sealed class AutoMsiService
{
    public const string Kind = "msi";
    public const string TweakPrefix = "AutoMsi:";
    public const int Trials = LatencyComparer.MinTrials;
    public static readonly TimeSpan Settle = InterruptMeasure.Settle;
    public static readonly TimeSpan Window = InterruptMeasure.Window;

    private readonly TransactionalAuditService _audit;
    private readonly AutoTuneSessionService _sessions;

    public AutoMsiService(TransactionalAuditService audit)
    {
        _audit = audit;
        _sessions = new AutoTuneSessionService(audit);
    }


    private static string MsiPath(string instance) =>
        $@"SYSTEM\CurrentControlSet\Enum\{instance}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";

    private static bool MsiOn(string instance)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var k = hklm.OpenSubKey(MsiPath(instance));
        return k?.GetValue("MSISupported") is int v && v == 1;
    }

    private static LoadKind LoadFor(string msiKind) => msiKind switch
    {
        "USB xHCI" => LoadKind.UsbGuided,
        "Audio HD" => LoadKind.Audio,
        "GPU" => LoadKind.Gpu,
        "Red" => LoadKind.Network,
        "NVMe" => LoadKind.Storage,
        _ => LoadKind.UsbGuided,
    };

    // ---------------------------------------------------------------- armar la sesión

    public AutoTuneSession BuildSession()
    {
        var tree = new DeviceTree(new CfgMgrDeviceInventory().Snapshot());
        var session = new AutoTuneSession { Kind = Kind };
        int i = 0;
        foreach (var t in MsiPlanner.Targets(tree))
        {
            string driver = DriverFiles.ImageFileName(t.Device.Service) ?? "";
            if (driver.Length == 0) continue;

            string key = $"d{i++}";
            bool on = MsiOn(t.Device.InstanceId);
            string hint = LoadFor(t.Kind) == LoadKind.UsbGuided ? " · mové el mouse en círculos" : "";
            session.Data[$"{key}:instance"] = t.Device.InstanceId;
            session.Data[$"{key}:name"] = $"{t.Device.DisplayName} ({t.Kind})";
            session.Data[$"{key}:kind"] = t.Kind;
            session.Data[$"{key}:driver"] = driver;
            session.Data[$"{key}:msix"] = t.RemoveMessageLimit ? "1" : "0";
            session.Data[$"{key}:orig"] = on ? "1" : "0";
            session.TweakIds.Add(TweakPrefix + t.Device.InstanceId);
            session.Steps.Add(new AutoTuneStep { Id = $"{key}:base", Title = $"{t.Kind}: {(on ? "con" : "sin")} MSI (actual){hint}" });
            session.Steps.Add(new AutoTuneStep { Id = $"{key}:alt", Title = $"{t.Kind}: {(on ? "sin" : "con")} MSI{hint}" });
        }
        return session;
    }

    public void Begin(AutoTuneSession session) => _sessions.Begin(session);

    // ---------------------------------------------------------------- un paso

    /// <summary>
    /// Ejecuta el paso actual. Devuelve Reboot = true si hay que reiniciar Windows para seguir (ya quedó programado).
    /// </summary>
    public (bool Ok, string Detail, bool Reboot) RunStep(AutoTuneSession session, string stepId, CancellationToken cancel)
    {
        string key = stepId[..stepId.IndexOf(':')];
        string instance = session.Data[$"{key}:instance"];
        string driver = session.Data[$"{key}:driver"];
        string kind = session.Data[$"{key}:kind"];
        var load = LoadFor(kind);
        List<double> Measure() => InterruptMeasure.Run(kind, driver, load, cancel);
        bool resumed = session.Step(stepId).Status == AutoTuneStepStatus.Running;
        session.Start(stepId);
        AutoTuneSessionService.Checkpoint(session);

        if (stepId.EndsWith(":base", StringComparison.Ordinal))
        {
            var p99 = Measure();
            session.Data[$"{key}:base"] = Join(p99);
            session.Finish(stepId, true, $"p99 {p99.Average():F0} µs");
            AutoTuneSessionService.Checkpoint(session);
            return (true, session.Step(stepId).Detail!, false);
        }

        string tweakId = TweakPrefix + instance;
        if (!(resumed && session.Data.GetValueOrDefault($"{key}:applied") == "1"))
        {
            // Aplicar la alternativa (journal) y reiniciar el dispositivo.
            bool turnOn = session.Data[$"{key}:orig"] == "0";
            _audit.SetRegistryValue(tweakId, RegistryHive.LocalMachine, MsiPath(instance), "MSISupported", turnOn ? 1 : 0, RegistryValueKind.DWord);
            if (turnOn && session.Data[$"{key}:msix"] == "1")
            {
                _audit.DeleteRegistryValue(tweakId, RegistryHive.LocalMachine, MsiPath(instance), "MessageNumberLimit");
            }
            session.Data[$"{key}:applied"] = "1";
            if (session.Data[$"{key}:kind"] == "GPU") session.Data["gpuRestarted"] = "1"; // la UI se reabre al terminar
            AutoTuneSessionService.Checkpoint(session);

            var restart = new CfgMgrDeviceInventory().Restart(instance, TimeSpan.FromSeconds(30));
            if (!restart.Restarted && restart.ProblemCodeAfter == 0)
            {
                // No se reinicia en caliente: el cambio se aplica reiniciando Windows y el paso se retoma al volver.
                var (ok, detail) = _sessions.RebootToContinue(session);
                return ok ? (true, "Requiere reiniciar Windows: " + detail, true) : (false, detail, false);
            }
            if (!restart.IsHealthy) return Unfit(session, stepId, tweakId, instance, $"no arrancó con el cambio ({restart.Detail})");
        }
        else if (new CfgMgrDeviceInventory().GetDevice(instance) is not { ProblemCode: 0 })
        {
            return Unfit(session, stepId, tweakId, instance, "no arrancó después del reinicio");
        }

        var alt = Measure();
        session.Data[$"{key}:alt"] = Join(alt);
        var baseP99 = Split(session.Data.GetValueOrDefault($"{key}:base"));
        var decision = LatencyComparer.Decide(baseP99, alt);
        if (!decision.KeepAlternative)
        {
            _audit.RevertTweakFromJournal(tweakId, out _);
            var back = new CfgMgrDeviceInventory().Restart(instance, TimeSpan.FromSeconds(30));
            if (!back.Restarted) session.Data["pendingReboot"] = "1"; // la vuelta atrás se aplica en el próximo reinicio
        }
        session.Data[$"{key}:result"] = decision.Reason;
        session.Finish(stepId, true, decision.Reason);
        AutoTuneSessionService.Checkpoint(session);
        return (true, decision.Reason, false);
    }

    private (bool, string, bool) Unfit(AutoTuneSession session, string stepId, string tweakId, string instance, string why)
    {
        _audit.RevertTweakFromJournal(tweakId, out _);
        var back = new CfgMgrDeviceInventory().Restart(instance, TimeSpan.FromSeconds(30));
        if (!back.IsHealthy) session.Data["pendingReboot"] = "1";
        string key = stepId[..stepId.IndexOf(':')];
        session.Data[$"{key}:result"] = $"No apto: {why}. Se dejó como estaba.";
        session.Finish(stepId, false, session.Data[$"{key}:result"]);
        AutoTuneSessionService.Checkpoint(session);
        return (false, session.Data[$"{key}:result"], false);
    }


    // ---------------------------------------------------------------- fin

    public string Finish(AutoTuneSession session)
    {
        var sb = new StringBuilder();
        foreach (var key in session.Data.Keys.Where(k => k.EndsWith(":name", StringComparison.Ordinal)).Select(k => k[..k.IndexOf(':')]).OrderBy(k => k))
        {
            sb.Append("• ").Append(session.Data[$"{key}:name"]).Append(": ")
              .AppendLine(session.Data.GetValueOrDefault($"{key}:result") ?? "sin resultado");
        }
        if (session.Data.ContainsKey("pendingReboot")) sb.AppendLine().AppendLine("Reiniciá la PC para que se aplique la vuelta atrás de los dispositivos que no se reinician en caliente.");
        sb.AppendLine().Append("Cada cambio que quedó se puede deshacer en Interrupciones → Revertir todo.");
        _sessions.End();
        return sb.ToString();
    }

    public string Cancel(AutoTuneSession session) => _sessions.Cancel(session);

    private static string Join(IEnumerable<double> v) => string.Join(";", v.Select(x => x.ToString("F1", CultureInfo.InvariantCulture)));

    private static List<double> Split(string? s) =>
        string.IsNullOrEmpty(s) ? new List<double>() : s.Split(';').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToList();
}
