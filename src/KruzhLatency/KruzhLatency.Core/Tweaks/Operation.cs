using KruzhLatency.Core.Abstractions;

namespace KruzhLatency.Core.Tweaks;

/// <summary>Estado de UNA operación respecto de su valor objetivo.</summary>
public enum OpState
{
    /// <summary>El sistema ya tiene el valor objetivo.</summary>
    Target,
    /// <summary>El sistema tiene otro valor (o no tiene ninguno) y la operación puede aplicarse.</summary>
    NotTarget,
    /// <summary>El objeto que toca la operación no existe en esta PC (servicio no instalado, NIC sin ese keyword, etc.).
    /// No cuenta ni a favor ni en contra del estado del tweak, y Apply no hace nada.</summary>
    NotPresent
}

/// <summary>
/// Valor original capturado ANTES de aplicar. Es lo que guarda el journal y lo único que necesita Restore.
/// <see cref="Payload"/> es texto serializable (lo define cada tipo de operación); null + Existed=false = "no existía".
/// </summary>
public sealed record OperationSnapshot(string OperationId, string OperationKind, bool Existed, string? Payload);

/// <summary>
/// Una escritura atómica y reversible sobre el sistema. Es la ÚNICA forma en que un tweak modifica Windows
/// (ADR-001). Ciclo obligatorio del engine: Capture → (journal) → Apply → Read (verificación).
/// Reglas para implementaciones:
///  - Id estable y único globalmente (se usa como clave del journal). Ej: "reg:HKLM\...\Valor".
///  - Read/Capture no modifican nada.
///  - Apply es idempotente.
///  - Restore deja exactamente el estado de la captura (incluido "no existía" → borrar).
///  - Describe() es el texto que ve el usuario: tiene que decir exactamente lo que hace Apply.
/// </summary>
public abstract class Operation
{
    public abstract string Id { get; }

    /// <summary>Tipo de operación (para deserializar snapshots y para reportes). Ej: "registry", "service".</summary>
    public abstract string Kind { get; }

    public virtual bool RequiresReboot => false;

    public abstract string Describe();

    public abstract OpState Read(ISystemContext sys);

    public abstract OperationSnapshot Capture(ISystemContext sys);

    public abstract void Apply(ISystemContext sys);

    public abstract void Restore(ISystemContext sys, OperationSnapshot snapshot);

    protected void EnsureSnapshotIsMine(OperationSnapshot snapshot)
    {
        if (!string.Equals(snapshot.OperationId, Id, StringComparison.Ordinal) ||
            !string.Equals(snapshot.OperationKind, Kind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"El snapshot {snapshot.OperationKind}/{snapshot.OperationId} no corresponde a la operación {Kind}/{Id}.");
        }
    }

    public override string ToString() => $"{Kind}:{Id}";
}
