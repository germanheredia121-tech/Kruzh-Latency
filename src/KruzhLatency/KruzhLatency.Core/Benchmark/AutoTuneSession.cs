using System.Text.Json;
using System.Text.Json.Serialization;

namespace KruzhLatency.Core.Benchmark;

public enum AutoTuneStepStatus { Pending, Running, Done, Failed, Skipped }

/// <summary>Un paso de una sesión de auto-tune (ej. "MSI de la xHCI del CPU: probar con MSI").</summary>
public sealed class AutoTuneStep
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary>El cambio de este paso solo se aplica reiniciando Windows.</summary>
    public bool NeedsReboot { get; init; }
    public AutoTuneStepStatus Status { get; set; } = AutoTuneStepStatus.Pending;
    public string? Detail { get; set; }
}

/// <summary>
/// Spec 003 / G5: sesión de medición que sobrevive reinicios. Se guarda en disco antes de cada reinicio; al volver,
/// el paso que quedó en <see cref="AutoTuneStepStatus.Running"/> es el que hay que verificar y medir.
/// Datos libres por paso en <see cref="Data"/> (resultados serializados por el runner).
/// </summary>
public sealed class AutoTuneSession
{
    public const int MaxReboots = 25;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>Tipo de runner que la retoma ("msi", "affinity"…).</summary>
    public string Kind { get; init; } = "";
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    public int Reboots { get; set; }
    /// <summary>Ids del journal que toca la sesión: cancelar = revertir todos.</summary>
    public List<string> TweakIds { get; init; } = new();
    public List<AutoTuneStep> Steps { get; init; } = new();
    public Dictionary<string, string> Data { get; init; } = new();

    [JsonIgnore] public AutoTuneStep? Current => Steps.FirstOrDefault(s => s.Status == AutoTuneStepStatus.Running)
                                                 ?? Steps.FirstOrDefault(s => s.Status == AutoTuneStepStatus.Pending);
    [JsonIgnore] public bool IsFinished => Current is null;
    [JsonIgnore] public bool RebootBudgetExceeded => Reboots >= MaxReboots;

    public AutoTuneStep Step(string id) => Steps.First(s => s.Id == id);

    public void Start(string id) => Step(id).Status = AutoTuneStepStatus.Running;

    public void Finish(string id, bool ok, string detail)
    {
        var s = Step(id);
        s.Status = ok ? AutoTuneStepStatus.Done : AutoTuneStepStatus.Failed;
        s.Detail = detail;
    }

    /// <summary>Corta la sesión: los pasos que faltan quedan como saltados.</summary>
    public void Abort(string reason)
    {
        foreach (var s in Steps.Where(s => s.Status is AutoTuneStepStatus.Pending or AutoTuneStepStatus.Running))
        {
            s.Status = AutoTuneStepStatus.Skipped;
            s.Detail = reason;
        }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static AutoTuneSession? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<AutoTuneSession>(json, Json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>Tarea programada de un uso que reabre KRUZH (elevado) al iniciar sesión para retomar la medición.</summary>
public static class AutoTuneResume
{
    public const string TaskPath = @"\KRUZH\Retomar medicion";
    public const string Argument = "--resume-autotune";

    public static string TaskXml(string exePath, string userId)
    {
        static string X(string s) => System.Security.SecurityElement.Escape(s) ?? "";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>KRUZH LATENCY</Author>
                <Description>Retoma una medición de auto-tune después de reiniciar (spec 003). Se borra al terminar.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{X(userId)}</UserId>
                  <Delay>PT20S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{X(userId)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Enabled>true</Enabled>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>"{X(exePath)}"</Command>
                  <Arguments>{Argument}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}
