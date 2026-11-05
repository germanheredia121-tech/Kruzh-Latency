using System;
using System.Collections.Generic;
using System.Globalization;
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
/// Spec 003 / G6: auto MSI. Por cada dispositivo que declara MSI/MSI-X, bajo una carga que lo hace interrumpir:
/// <list type="number">
/// <item>Modo: p99 del estado actual contra MSI invertido (on↔off).</item>
/// <item>Prioridad (<c>DevicePriority</c>): el modo elegido contra prioridad Alta.</item>
/// <item>Límite de mensajes (<c>MessageNumberLimit</c>), solo MSI-X con varios mensajes: sin límite contra 1, 2, 4…
/// (<see cref="MsiTuningPlan"/>).</item>
/// </list>
/// Cada cosa se queda solo si baja el p99 más que el ruido (<see cref="LatencyComparer"/>) y tiene su propio registro en el
/// journal (revertir la prioridad no deshace el MSI). Si el dispositivo no arranca con un cambio, vuelve atrás y queda "no apto".
/// Los que no se reinician en caliente (a veces la GPU) usan el reinicio con retomado (G5), también para volver atrás.
/// El NVMe no se mide (ver BuildSession).
/// </summary>
public sealed class AutoMsiService
{
    public const string Kind = "msi";
    public const string TweakPrefix = "AutoMsi:";
    public const string PriorityTweakPrefix = "AutoMsiPrio:";
    public const string LimitTweakPrefix = "AutoMsiLimit:";
    /// <summary>Todo lo que puede dejar una sesión de auto MSI en el journal.</summary>
    public static readonly string[] AllTweakPrefixes = { TweakPrefix, PriorityTweakPrefix, LimitTweakPrefix };

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

    private static string AffinityPath(string instance) =>
        $@"SYSTEM\CurrentControlSet\Enum\{instance}\Device Parameters\Interrupt Management\Affinity Policy";

    private static int? ReadDword(string path, string name)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var k = hklm.OpenSubKey(path);
        return k?.GetValue(name) is int v ? v : null;
    }

    private static bool MsiOn(string instance) => ReadDword(MsiPath(instance), "MSISupported") == 1;

    private static LoadKind LoadFor(string msiKind) => msiKind switch
    {
        "USB xHCI" => LoadKind.UsbGuided,
        "Audio HD" => LoadKind.Audio,
        "GPU" => LoadKind.Gpu,
        "Red" => LoadKind.Network,
        "NVMe" => LoadKind.Storage,
        _ => LoadKind.UsbGuided,
    };

    private static string KeyOf(string stepId) => stepId[..stepId.IndexOf(':')];

    // ---------------------------------------------------------------- armar la sesión

    public AutoTuneSession BuildSession()
    {
        var tree = new DeviceTree(new CfgMgrDeviceInventory().Snapshot());
        var session = new AutoTuneSession { Kind = Kind };
        int i = 0;
        foreach (var t in MsiPlanner.Targets(tree))
        {
            // El NVMe no se mide: sin MSI el disco puede trabarse y colgar Windows entero (sin volcado, porque el disco
            // es donde se escribiría), y por ser el disco del sistema la vuelta atrás exige reiniciar. Queda como está.
            if (t.Kind == "NVMe") continue;

            string driver = DriverFiles.ImageFileName(t.Device.Service) ?? "";
            if (driver.Length == 0) continue;

            string key = $"d{i++}";
            string inst = t.Device.InstanceId;
            bool on = MsiOn(inst);
            string hint = LoadFor(t.Kind) == LoadKind.UsbGuided ? " · mové el mouse en círculos" : "";
            uint maxMessages = t.Device.Interrupts?.MaxMessages ?? 1;
            session.Data[$"{key}:instance"] = inst;
            session.Data[$"{key}:name"] = $"{t.Device.DisplayName} ({t.Kind})";
            session.Data[$"{key}:kind"] = t.Kind;
            session.Data[$"{key}:driver"] = driver;
            session.Data[$"{key}:msix"] = t.RemoveMessageLimit ? "1" : "0";
            session.Data[$"{key}:orig"] = on ? "1" : "0";
            session.TweakIds.Add(TweakPrefix + inst);
            session.TweakIds.Add(PriorityTweakPrefix + inst);
            session.TweakIds.Add(LimitTweakPrefix + inst);

            session.Steps.Add(new AutoTuneStep { Id = $"{key}:base", Title = $"{t.Kind}: {(on ? "con" : "sin")} MSI (actual){hint}" });
            session.Steps.Add(new AutoTuneStep { Id = $"{key}:alt", Title = $"{t.Kind}: {(on ? "sin" : "con")} MSI{hint}" });
            session.Steps.Add(new AutoTuneStep { Id = $"{key}:prio", Title = $"{t.Kind}: prioridad alta{hint}" });

            var limits = MsiTuningPlan.LimitCandidates(t.RemoveMessageLimit, maxMessages);
            foreach (int n in limits)
            {
                session.Steps.Add(new AutoTuneStep { Id = $"{key}:lim:{n}", Title = $"{t.Kind}: límite de {n} mensaje{(n == 1 ? "" : "s")}{hint}" });
            }
            if (limits.Count > 0) session.Steps.Add(new AutoTuneStep { Id = $"{key}:limdecide", Title = $"{t.Kind}: elegir límite" });
        }
        return session;
    }

    public void Begin(AutoTuneSession session) => _sessions.Begin(session);

    /// <summary>El paso mide el USB del mouse (el usuario tiene que moverlo): la UI muestra el panel con los reportes en vivo.</summary>
    public static bool NeedsMouse(AutoTuneSession session, string stepId)
    {
        int at = stepId.IndexOf(':');
        if (at < 0 || stepId.EndsWith(":limdecide", StringComparison.Ordinal)) return false;
        return LoadFor(session.Data.GetValueOrDefault($"{stepId[..at]}:kind") ?? "") == LoadKind.UsbGuided;
    }

    /// <summary>Dispositivos de la sesión (para avisar cuánto tarda).</summary>
    public static int DeviceCount(AutoTuneSession session) => session.Data.Keys.Count(k => k.EndsWith(":name", StringComparison.Ordinal));

    /// <summary>Pasos que miden (los de "elegir" no miden).</summary>
    public static int MeasuringSteps(AutoTuneSession session) => session.Steps.Count(s => !s.Id.EndsWith(":limdecide", StringComparison.Ordinal));

    // ---------------------------------------------------------------- un paso

    /// <summary>Ejecuta un paso. Reboot = true si hay que reiniciar Windows para seguir (ya quedó programado).</summary>
    public (bool Ok, string Detail, bool Reboot) RunStep(AutoTuneSession session, string stepId, CancellationToken cancel)
    {
        string key = KeyOf(stepId);
        string instance = session.Data[$"{key}:instance"];
        string kind = session.Data[$"{key}:kind"];
        var load = LoadFor(kind);
        List<double> Measure() => InterruptMeasure.Run(kind, session.Data[$"{key}:driver"], load, cancel);
        bool resumed = session.Step(stepId).Status == AutoTuneStepStatus.Running;
        session.Start(stepId);
        AutoTuneSessionService.Checkpoint(session);

        string part = stepId[(key.Length + 1)..];

        // Un programa no dejó reiniciar el dispositivo: el resto de sus pruebas se saltean (cada una pediría reiniciar Windows).
        if (session.Data.GetValueOrDefault($"{key}:blocked") is { } blocked) return Skip(session, stepId, blocked);

        // Dispositivo que solo cambia reiniciando Windows: el límite de mensajes no se mide (serían 3-4 reinicios más).
        if (session.Data.GetValueOrDefault($"{key}:cold") == "1" && (part.StartsWith("lim:", StringComparison.Ordinal) || part == "limdecide"))
        {
            if (part == "limdecide") session.Data[$"{key}:limResult"] = "Límite de mensajes: no se midió (este dispositivo pide reiniciar Windows en cada prueba).";
            return Skip(session, stepId, "No se mide: pediría reiniciar Windows en cada prueba.");
        }

        if (part == "base") return RunBase(session, stepId, key, Measure);
        if (part == "alt") return RunMode(session, stepId, key, instance, resumed, Measure);
        if (part == "prio") return RunPriority(session, stepId, key, instance, resumed, Measure);
        if (part == "limdecide") return DecideLimit(session, stepId, key, instance);
        if (part.StartsWith("lim:", StringComparison.Ordinal)) return RunLimit(session, stepId, key, instance, resumed, int.Parse(part[4..], CultureInfo.InvariantCulture), Measure);
        return Skip(session, stepId, "Paso desconocido.");
    }

    private (bool, string, bool) RunBase(AutoTuneSession s, string stepId, string key, Func<List<double>> measure)
    {
        var p99 = measure();
        s.Data[$"{key}:base"] = Join(p99);
        s.Data[$"{key}:final"] = s.Data[$"{key}:base"]; // referencia para prioridad y límite si el modo no cambia
        s.Finish(stepId, true, $"p99 {p99.DefaultIfEmpty(0).Average():F0} µs");
        AutoTuneSessionService.Checkpoint(s);
        return (true, s.Step(stepId).Detail!, false);
    }

    /// <summary>Modo MSI: actual contra invertido.</summary>
    private (bool, string, bool) RunMode(AutoTuneSession s, string stepId, string key, string instance, bool resumed, Func<List<double>> measure)
    {
        string tweakId = TweakPrefix + instance;
        bool turnOn = s.Data[$"{key}:orig"] == "0";
        var early = ApplyAndRestart(s, stepId, $"{key}:applied", resumed, tweakId, instance, $"{key}:result", () =>
        {
            _audit.SetRegistryValue(tweakId, RegistryHive.LocalMachine, MsiPath(instance), "MSISupported", turnOn ? 1 : 0, RegistryValueKind.DWord);
            if (turnOn && s.Data[$"{key}:msix"] == "1")
            {
                _audit.DeleteRegistryValue(tweakId, RegistryHive.LocalMachine, MsiPath(instance), "MessageNumberLimit");
            }
        });
        if (early is { } e) return e;

        var alt = measure();
        s.Data[$"{key}:alt"] = Join(alt);
        var decision = LatencyComparer.Decide(Split(s.Data.GetValueOrDefault($"{key}:base")), alt);
        if (decision.KeepAlternative) s.Data[$"{key}:final"] = s.Data[$"{key}:alt"];
        return Conclude(s, stepId, tweakId, instance, decision.KeepAlternative, "Modo MSI: " + decision.Reason, $"{key}:result");
    }

    /// <summary>Prioridad: el modo que quedó contra prioridad Alta.</summary>
    private (bool, string, bool) RunPriority(AutoTuneSession s, string stepId, string key, string instance, bool resumed, Func<List<double>> measure)
    {
        if (!resumed && ReadDword(AffinityPath(instance), "DevicePriority") == MsiTuningPlan.HighPriority)
        {
            s.Data[$"{key}:prioResult"] = "Prioridad: ya estaba en Alta, no se midió.";
            return Skip(s, stepId, s.Data[$"{key}:prioResult"]);
        }

        string tweakId = PriorityTweakPrefix + instance;
        var early = ApplyAndRestart(s, stepId, $"{stepId}:applied", resumed, tweakId, instance, $"{key}:prioResult", () =>
            _audit.SetRegistryValue(tweakId, RegistryHive.LocalMachine, AffinityPath(instance), "DevicePriority", MsiTuningPlan.HighPriority, RegistryValueKind.DWord));
        if (early is { } e) return e;

        var p99 = measure();
        var decision = LatencyComparer.Decide(Split(s.Data.GetValueOrDefault($"{key}:final")), p99);
        if (decision.KeepAlternative) s.Data[$"{key}:final"] = Join(p99);
        return Conclude(s, stepId, tweakId, instance, decision.KeepAlternative, "Prioridad alta: " + decision.Reason, $"{key}:prioResult");
    }

    /// <summary>Un candidato de límite: se aplica, se mide y se guarda (la elección es en "limdecide").</summary>
    private (bool, string, bool) RunLimit(AutoTuneSession s, string stepId, string key, string instance, bool resumed, int limit, Func<List<double>> measure)
    {
        if (!resumed && !MsiOn(instance)) return Skip(s, stepId, "Sin MSI no hay límite de mensajes: no aplica.");

        string tweakId = LimitTweakPrefix + instance;
        var early = ApplyAndRestart(s, stepId, $"{stepId}:applied", resumed, tweakId, instance, $"{key}:limResult", () =>
            _audit.SetRegistryValue(tweakId, RegistryHive.LocalMachine, MsiPath(instance), "MessageNumberLimit", limit, RegistryValueKind.DWord));
        if (early is { } e) return e;

        var p99 = measure();
        s.Data[$"{stepId}:p99"] = Join(p99);
        s.Finish(stepId, true, $"p99 {p99.DefaultIfEmpty(0).Average():F0} µs");
        AutoTuneSessionService.Checkpoint(s);
        return (true, s.Step(stepId).Detail!, false);
    }

    /// <summary>Elige el límite: el mejor candidato si le gana a "sin límite" por encima del ruido; si no, queda como estaba.</summary>
    private (bool, string, bool) DecideLimit(AutoTuneSession s, string stepId, string key, string instance)
    {
        var candidates = s.Steps
            .Where(x => x.Id.StartsWith($"{key}:lim:", StringComparison.Ordinal) && s.Data.ContainsKey($"{x.Id}:p99"))
            .ToDictionary(x => int.Parse(x.Id[(x.Id.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture),
                          x => (IReadOnlyList<double>)Split(s.Data[$"{x.Id}:p99"]));
        if (candidates.Count == 0)
        {
            s.Data[$"{key}:limResult"] = "Límite de mensajes: no aplica (sin MSI o sin candidatos medidos).";
            return Skip(s, stepId, s.Data[$"{key}:limResult"]);
        }

        string tweakId = LimitTweakPrefix + instance;
        var best = MsiTuningPlan.PickBest(Split(s.Data.GetValueOrDefault($"{key}:final")), candidates);
        _audit.RevertTweakFromJournal(tweakId, out _); // vuelve el límite original
        string result;
        if (best is { } b)
        {
            _audit.SetRegistryValue(tweakId, RegistryHive.LocalMachine, MsiPath(instance), "MessageNumberLimit", b.Value, RegistryValueKind.DWord);
            result = $"Límite de {b.Value} mensaje{(b.Value == 1 ? "" : "s")}: {b.Decision.Reason}";
        }
        else
        {
            result = "Límite de mensajes: ninguno mejora a \"sin límite\", queda como estaba.";
        }
        s.Data[$"{key}:limResult"] = result;
        s.Finish(stepId, true, result);
        AutoTuneSessionService.Checkpoint(s);
        bool healthy = new CfgMgrDeviceInventory().Restart(instance, TimeSpan.FromSeconds(30)).IsHealthy;
        return healthy ? (true, result, false) : RebootNow(s, result, true);
    }

    // ---------------------------------------------------------------- piezas comunes

    /// <summary>
    /// Aplica el cambio (journal) y reinicia el dispositivo. null = listo para medir. Si no se reinicia en caliente, programa
    /// el reinicio de Windows (el paso se retoma al volver). Si no arranca con el cambio, lo revierte y queda "no apto".
    /// </summary>
    private (bool, string, bool)? ApplyAndRestart(AutoTuneSession s, string stepId, string appliedKey, bool resumed, string tweakId,
                                                   string instance, string resultKey, Action apply)
    {
        if (resumed && s.Data.GetValueOrDefault(appliedKey) == "1")
        {
            return new CfgMgrDeviceInventory().GetDevice(instance) is { ProblemCode: 0 }
                ? null
                : Unfit(s, stepId, tweakId, instance, "no arrancó después del reinicio", resultKey);
        }

        apply();
        s.Data[appliedKey] = "1";
        if (s.Data[$"{KeyOf(stepId)}:kind"] == "GPU") s.Data["gpuRestarted"] = "1"; // la UI se reabre al terminar
        AutoTuneSessionService.Checkpoint(s);

        var restart = new CfgMgrDeviceInventory().Restart(instance, TimeSpan.FromSeconds(30));
        if (restart.Vetoed)
        {
            // El cambio no llegó a aplicarse (el dispositivo siguió andando): se deshace en el registro, sin reiniciar nada,
            // y el resto de las pruebas de este dispositivo se saltean.
            _audit.RevertTweakFromJournal(tweakId, out _);
            string key = KeyOf(stepId);
            string why = $"No se midió: {restart.Detail} Cerralo y volvé a medir.";
            s.Data[$"{key}:blocked"] = why;
            s.Data[resultKey] = why;
            s.Finish(stepId, false, why);
            AutoTuneSessionService.Checkpoint(s);
            return (false, why, false);
        }
        if (!restart.Restarted && restart.ProblemCodeAfter == 0)
        {
            s.Data[$"{KeyOf(stepId)}:cold"] = "1";
            var (ok, detail) = _sessions.RebootToContinue(s);
            return ok ? (true, "Requiere reiniciar Windows: " + detail, true) : (false, detail, false);
        }
        return restart.IsHealthy ? null : Unfit(s, stepId, tweakId, instance, $"no arrancó con el cambio ({restart.Detail})", resultKey);
    }

    /// <summary>Cierra un paso de decisión: si no se queda, revierte y reinicia el dispositivo (o Windows, ya).</summary>
    private (bool, string, bool) Conclude(AutoTuneSession s, string stepId, string tweakId, string instance, bool keep, string result, string resultKey)
    {
        bool backNeedsReboot = false;
        if (!keep)
        {
            _audit.RevertTweakFromJournal(tweakId, out _);
            backNeedsReboot = !new CfgMgrDeviceInventory().Restart(instance, TimeSpan.FromSeconds(30)).IsHealthy;
        }
        s.Data[resultKey] = result;
        s.Finish(stepId, true, result);
        AutoTuneSessionService.Checkpoint(s);
        return backNeedsReboot ? RebootNow(s, result, true) : (true, result, false);
    }

    private (bool, string, bool) Unfit(AutoTuneSession s, string stepId, string tweakId, string instance, string why, string resultKey)
    {
        _audit.RevertTweakFromJournal(tweakId, out _);
        var back = new CfgMgrDeviceInventory().Restart(instance, TimeSpan.FromSeconds(30));
        s.Data[resultKey] = $"No apto: {why}. Se dejó como estaba.";
        s.Finish(stepId, false, s.Data[resultKey]);
        AutoTuneSessionService.Checkpoint(s);
        return back.IsHealthy ? (false, s.Data[resultKey], false) : RebootNow(s, s.Data[resultKey], false);
    }

    private static (bool, string, bool) Skip(AutoTuneSession s, string stepId, string why)
    {
        s.Finish(stepId, true, why);
        AutoTuneSessionService.Checkpoint(s);
        return (true, why, false);
    }

    /// <summary>
    /// La vuelta atrás no se pudo aplicar en caliente: se reinicia YA (no "más tarde"), para que el dispositivo no siga
    /// funcionando con el valor de prueba. La sesión sigue sola al volver.
    /// </summary>
    private (bool, string, bool) RebootNow(AutoTuneSession session, string result, bool ok)
    {
        var (scheduled, detail) = _sessions.RebootToContinue(session);
        if (scheduled) return (ok, $"{result} Reiniciando para aplicar la vuelta atrás: {detail}", true);
        session.Data["pendingReboot"] = "1";
        AutoTuneSessionService.Checkpoint(session);
        return (ok, $"{result} ({detail})", false);
    }

    // ---------------------------------------------------------------- fin

    public string Finish(AutoTuneSession session)
    {
        var sb = new StringBuilder();
        foreach (var key in session.Data.Keys.Where(k => k.EndsWith(":name", StringComparison.Ordinal)).Select(k => k[..k.IndexOf(':')]).OrderBy(k => k))
        {
            sb.Append("• ").AppendLine(session.Data[$"{key}:name"]);
            sb.Append("   ").AppendLine(session.Data.GetValueOrDefault($"{key}:result") ?? "Modo MSI: sin resultado");
            if (session.Data.GetValueOrDefault($"{key}:prioResult") is { } prio) sb.Append("   ").AppendLine(prio);
            if (session.Data.GetValueOrDefault($"{key}:limResult") is { } lim) sb.Append("   ").AppendLine(lim);
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
