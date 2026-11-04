using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KruzhLatency.Core.Benchmark;
using KruzhLatency.Infra.Benchmark;

namespace KruzhLatency.Services;

/// <summary>
/// Spec 003 / G5: ciclo de vida de una sesión de auto-tune con reinicios.
/// Empezar → (pasos; si un paso necesita reiniciar: guardar + tarea de retomado + reinicio) → al iniciar sesión KRUZH abre solo
/// y retoma → terminar (borrar sesión y tarea). Cancelar en cualquier momento revierte todo lo que la sesión cambió.
/// </summary>
public sealed class AutoTuneSessionService
{
    private const string ResumeTaskTweakId = "AutoTune.Resume";

    private readonly TransactionalAuditService _audit;

    /// <summary>Quién retoma cada tipo de sesión (lo registran G6/G7). Sin runner, solo se puede cancelar.</summary>
    public static Dictionary<string, Func<AutoTuneSession, Task>> Runners { get; } = new(StringComparer.OrdinalIgnoreCase);

    public AutoTuneSessionService(TransactionalAuditService audit) => _audit = audit;

    public static AutoTuneSession? Pending()
    {
        var s = AutoTuneSessionStore.Load();
        return s is { IsFinished: false } ? s : null;
    }

    public void Begin(AutoTuneSession session)
    {
        AutoTuneSessionStore.Save(session);
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("No se pudo ubicar KruzhLatency.exe.");
        string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        _audit.CreateScheduledTask(ResumeTaskTweakId, AutoTuneResume.TaskPath, AutoTuneResume.TaskXml(exe, user));
    }

    public static void Checkpoint(AutoTuneSession session) => AutoTuneSessionStore.Save(session);

    /// <summary>Guarda el estado y reinicia en 15 s. Falla (sin reiniciar) si se pasó el límite de reinicios.</summary>
    public (bool Ok, string Detail) RebootToContinue(AutoTuneSession session)
    {
        if (session.RebootBudgetExceeded)
        {
            return (false, $"La medición ya reinició {AutoTuneSession.MaxReboots} veces: se corta por seguridad.");
        }
        session.Reboots++;
        AutoTuneSessionStore.Save(session);
        return SystemRestart.Schedule(15, "KRUZH LATENCY: reinicio para seguir midiendo. Se retoma sola al iniciar sesión.")
            ? (true, "Reiniciando en 15 segundos…")
            : (false, "Windows no aceptó programar el reinicio.");
    }

    /// <summary>Fin normal: se borran la sesión y la tarea de retomado. Lo aplicado por la sesión queda.</summary>
    public void End()
    {
        AutoTuneSessionStore.Delete();
        _audit.RevertTweakFromJournal(ResumeTaskTweakId, out _);
        // Respaldo: si la tarea ya existía de una sesión anterior, el journal no la registró como creada.
        KruzhLatency.Infra.Debloat.ScheduledTaskStore.Delete(AutoTuneResume.TaskPath);
    }

    /// <summary>Cancelar: revierte todo lo que la sesión tocó y termina.</summary>
    public string Cancel(AutoTuneSession session)
    {
        var notes = new List<string>();
        foreach (string id in session.TweakIds)
        {
            if (_audit.RevertTweakFromJournal(id, out string msg)) notes.Add(msg);
        }
        session.Abort("Cancelado: se revirtieron los cambios de la sesión.");
        End();
        return notes.Count == 0 ? "No había cambios para revertir." : string.Join(" ", notes);
    }
}
