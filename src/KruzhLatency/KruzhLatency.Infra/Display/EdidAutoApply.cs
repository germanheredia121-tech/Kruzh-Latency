using System.Diagnostics;
using KruzhLatency.Core.Display;
using Microsoft.Win32;

namespace KruzhLatency.Infra.Display;

/// <summary>
/// ADR-009, modo automático: preparación de la vuelta atrás (script + tareas programadas), confirmación y rollback.
/// La escritura del EDID_OVERRIDE la hace la app con el journal; acá solo lo que rodea esa escritura.
/// </summary>
public static class EdidAutoApply
{
    /// <summary>C:\ProgramData\KruzhLatency\edid: lo lee SYSTEM, solo lo pueden escribir SYSTEM y Administradores.</summary>
    public static string SecureFolder() => KruzhLatency.Infra.Security.SecureFolders.ProgramData("edid");

    /// <summary>Exporta el EDID_OVERRIDE actual a .reg si existe. Devuelve la ruta o null si no había override.</summary>
    public static string? ExportCurrentOverride(string instanceId, string folder)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters\EDID_OVERRIDE");
        if (key is null || key.ValueCount == 0) return null;
        string file = Path.Combine(folder, "edid-previo.reg");
        Run("reg.exe", $"export \"HKLM\\SYSTEM\\CurrentControlSet\\Enum\\{instanceId}\\Device Parameters\\EDID_OVERRIDE\" \"{file}\" /y");
        return file;
    }

    /// <summary>Escribe el script y crea las dos tareas (confirmación del usuario + rollback de SYSTEM a los 60 s).</summary>
    public static void InstallRollback(EdidRollbackPaths paths, string exePath)
    {
        File.WriteAllText(paths.Script, EdidRollbackPlan.BuildScript(paths), System.Text.Encoding.ASCII);
        File.WriteAllText(paths.PendingFile, paths.MonitorInstanceId);
        if (File.Exists(paths.ConfirmedFlag)) File.Delete(paths.ConfirmedFlag);
        Run("schtasks.exe", EdidRollbackPlan.RollbackTaskArgs(paths));
        Run("schtasks.exe", EdidRollbackPlan.ConfirmTaskArgs(exePath));
    }

    /// <summary>¿Hay un cambio de EDID esperando confirmación? Devuelve la instancia del monitor o null.</summary>
    public static string? PendingConfirmation()
    {
        string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KruzhLatency", "edid", "edid-pending.txt");
        try
        {
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>El usuario confirmó que se ve bien: marca, borra las tareas y limpia.</summary>
    public static void Confirm(EdidRollbackPaths paths)
    {
        File.WriteAllText(paths.ConfirmedFlag, DateTime.Now.ToString("O"));
        TryRun("schtasks.exe", $"/Delete /TN \"{EdidRollbackPaths.RollbackTask}\" /F");
        TryRun("schtasks.exe", $"/Delete /TN \"{EdidRollbackPaths.ConfirmTask}\" /F");
        File.Delete(paths.PendingFile);
        File.Delete(paths.ConfirmedFlag);
    }

    /// <summary>El usuario pidió volver atrás: corre el mismo script que la tarea (borra el override y reinicia).</summary>
    public static void RollbackNow(EdidRollbackPaths paths) => Run("cmd.exe", $"/c \"{paths.Script}\"");

    private static void TryRun(string exe, string args)
    {
        try { Run(exe, args); }
        catch (InvalidOperationException) { }
    }

    private static void Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        if (!p.WaitForExit(30_000))
        {
            try { p.Kill(); } catch (InvalidOperationException) { }
            throw new InvalidOperationException($"{exe} no respondió.");
        }
        if (p.ExitCode != 0) throw new InvalidOperationException($"{exe} terminó con código {p.ExitCode}.");
    }
}
