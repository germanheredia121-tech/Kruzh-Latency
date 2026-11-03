using System.Text;

namespace KruzhLatency.Core.Display;

/// <summary>Rutas y nombres que usa la vuelta atrás automática del EDID (ADR-009).</summary>
public sealed record EdidRollbackPaths(
    string MonitorInstanceId,
    /// <summary>Carpeta compartida usuario/SYSTEM, ej. C:\ProgramData\KruzhLatency.</summary>
    string Folder,
    /// <summary>.reg con el EDID_OVERRIDE que había antes (null si no había).</summary>
    string? PreviousOverrideReg)
{
    public const string ConfirmTask = @"KRUZH\EdidConfirm";
    public const string RollbackTask = @"KRUZH\EdidRollback";

    public string ConfirmedFlag => Path.Combine(Folder, "edid-confirmed.flag");
    public string PendingFile => Path.Combine(Folder, "edid-pending.txt");
    public string Script => Path.Combine(Folder, "edid-rollback.cmd");
    public string OverrideKey => $@"HKLM\SYSTEM\CurrentControlSet\Enum\{MonitorInstanceId}\Device Parameters\EDID_OVERRIDE";
}

/// <summary>
/// ADR-009, modo automático: después de escribir EDID_OVERRIDE se programan dos tareas para el próximo inicio de sesión.
/// KRUZH pregunta "¿se ve bien?" y, si en 60 s nadie confirma (pantalla negra, Code 45, lo que sea), una tarea de SYSTEM
/// borra el override, restaura el anterior y reinicia. El script es texto plano de cmd para no depender de la app ni de la GPU.
/// </summary>
public static class EdidRollbackPlan
{
    public static string BuildScript(EdidRollbackPaths p)
    {
        var s = new StringBuilder();
        s.AppendLine("@echo off");
        s.AppendLine("rem KRUZH LATENCY - vuelta atras del EDID (ADR-009). Lo ejecuta una tarea de SYSTEM 60 s despues de iniciar sesion.");
        s.AppendLine($"if exist \"{p.ConfirmedFlag}\" goto confirmed");
        s.AppendLine($"reg delete \"{p.OverrideKey}\" /f");
        if (p.PreviousOverrideReg is not null)
        {
            s.AppendLine($"reg import \"{p.PreviousOverrideReg}\"");
        }
        s.AppendLine("call :cleanup");
        s.AppendLine("shutdown /r /t 15 /c \"KRUZH LATENCY: no se confirmo la pantalla. Se volvio al EDID anterior y se reinicia.\"");
        s.AppendLine("goto :eof");
        s.AppendLine(":confirmed");
        s.AppendLine("call :cleanup");
        s.AppendLine($"del \"{p.ConfirmedFlag}\"");
        s.AppendLine("goto :eof");
        s.AppendLine(":cleanup");
        s.AppendLine($"schtasks /Delete /TN \"{EdidRollbackPaths.ConfirmTask}\" /F");
        s.AppendLine($"schtasks /Delete /TN \"{EdidRollbackPaths.RollbackTask}\" /F");
        s.AppendLine($"del \"{p.PendingFile}\"");
        s.AppendLine("exit /b 0");
        return s.ToString();
    }

    /// <summary>Argumentos de schtasks para la tarea de SYSTEM: al iniciar sesión cualquier usuario, con 60 s de espera.</summary>
    public static string RollbackTaskArgs(EdidRollbackPaths p) =>
        $"/Create /TN \"{EdidRollbackPaths.RollbackTask}\" /SC ONLOGON /DELAY 0001:00 /RU SYSTEM /RL HIGHEST /TR \"cmd.exe /c \\\"{p.Script}\\\"\" /F";

    /// <summary>Argumentos de schtasks para abrir KRUZH en modo confirmación al iniciar sesión (usuario actual, elevado).</summary>
    public static string ConfirmTaskArgs(string exePath) =>
        $"/Create /TN \"{EdidRollbackPaths.ConfirmTask}\" /SC ONLOGON /RL HIGHEST /TR \"\\\"{exePath}\\\" --edid-confirm\" /F";
}
