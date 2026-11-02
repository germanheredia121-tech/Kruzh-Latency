using KruzhLatency.Core.Abstractions;
using KruzhLatency.Core.Hardware;

namespace KruzhLatency.Core.Tweaks;

public enum TweakStatus
{
    /// <summary>El gate de aplicabilidad falló, o ninguna operación tiene sobre qué actuar en esta PC.</summary>
    NotApplicable,
    NotApplied,
    /// <summary>Algunas operaciones están en el valor objetivo y otras no. Aplicar completa las que faltan.</summary>
    Partial,
    Applied,
    /// <summary>No se pudo leer el estado (error de lectura).</summary>
    Unknown
}

/// <summary>Estado real de un tweak, calculado leyendo TODAS sus operaciones.</summary>
public sealed record TweakState(
    string TweakId,
    TweakStatus Status,
    /// <summary>Opción aplicada (Applied) o más avanzada (Partial). null si NotApplied / NotApplicable.</summary>
    string? ActiveOptionKey,
    Gate Applicability,
    Gate Recommendation,
    string? ErrorDetail = null);

public enum ApplyOutcome
{
    Applied,
    AlreadyApplied,
    Reverted,
    NotApplicable,
    Failed
}

public sealed record OperationFailure(string OperationId, string Message);

/// <summary>Resultado de aplicar o revertir un tweak. Nunca se lanza excepción por un tweak: se reporta acá.</summary>
public sealed record ApplyResult(
    string TweakId,
    ApplyOutcome Outcome,
    string Detail,
    bool RequiresReboot,
    IReadOnlyList<OperationFailure> Failures)
{
    public bool Succeeded => Outcome is ApplyOutcome.Applied or ApplyOutcome.AlreadyApplied or ApplyOutcome.Reverted;

    public static ApplyResult Fail(string tweakId, string detail, IReadOnlyList<OperationFailure>? failures = null) =>
        new(tweakId, ApplyOutcome.Failed, detail, false, failures ?? Array.Empty<OperationFailure>());
}

/// <summary>Calcula el <see cref="TweakState"/> a partir de las operaciones. Función pura sobre el sistema (solo lee).</summary>
public static class TweakStateEvaluator
{
    public static TweakState Evaluate(TweakDefinition tweak, HardwareProfile hw, ISystemContext sys)
    {
        var applicable = tweak.Applicable(hw);
        var recommended = tweak.Recommended(hw);

        if (!applicable.Passed)
        {
            return new TweakState(tweak.Id, TweakStatus.NotApplicable, null, applicable, recommended);
        }

        try
        {
            // Para cada opción: cuántas operaciones presentes están en el objetivo.
            var perOption = tweak.Options
                .Select(opt =>
                {
                    var states = opt.Operations.Select(op => op.Read(sys)).Where(s => s != OpState.NotPresent).ToList();
                    return (opt.Key, Present: states.Count, Target: states.Count(s => s == OpState.Target));
                })
                .ToList();

            if (perOption.All(o => o.Present == 0))
            {
                return new TweakState(tweak.Id, TweakStatus.NotApplicable, null,
                    Gate.Fail("No hay nada que modificar en esta PC (los componentes que toca este tweak no existen)."), recommended);
            }

            var fullyApplied = perOption.FirstOrDefault(o => o.Present > 0 && o.Target == o.Present);
            if (fullyApplied.Key is not null)
            {
                return new TweakState(tweak.Id, TweakStatus.Applied, fullyApplied.Key, applicable, recommended);
            }

            var best = perOption.OrderByDescending(o => o.Target).First();
            return best.Target > 0
                ? new TweakState(tweak.Id, TweakStatus.Partial, best.Key, applicable, recommended)
                : new TweakState(tweak.Id, TweakStatus.NotApplied, null, applicable, recommended);
        }
        catch (Exception ex)
        {
            return new TweakState(tweak.Id, TweakStatus.Unknown, null, applicable, recommended, ex.Message);
        }
    }
}
