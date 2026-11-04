using System.Diagnostics;
using KruzhLatency.Core.Benchmark;
using KruzhLatency.Infra.Security;

namespace KruzhLatency.Infra.Benchmark;

/// <summary>
/// Spec 003 / G5: guarda la sesión de auto-tune en ProgramData\KruzhLatency\autotune (ACL: SYSTEM/Administradores, usuarios solo lectura),
/// con escritura atómica (session.tmp → File.Replace con session.bak), igual que el journal.
/// </summary>
public static class AutoTuneSessionStore
{
    private static string Dir => SecureFolders.ProgramData("autotune");
    private static string PathJson => Path.Combine(Dir, "session.json");

    public static void Save(AutoTuneSession session)
    {
        string tmp = PathJson + ".tmp";
        File.WriteAllText(tmp, session.ToJson());
        if (File.Exists(PathJson)) File.Replace(tmp, PathJson, PathJson + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, PathJson);
    }

    /// <summary>La sesión guardada (o la copia de respaldo si la principal está dañada); null si no hay.</summary>
    public static AutoTuneSession? Load()
    {
        foreach (string p in new[] { PathJson, PathJson + ".bak" })
        {
            try
            {
                if (File.Exists(p) && AutoTuneSession.FromJson(File.ReadAllText(p)) is { } s) return s;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    private static string ReportPath => Path.Combine(Dir, "report.txt");

    /// <summary>Resultado a mostrar la próxima vez que abra KRUZH (después de relanzarse por un reinicio del driver de video).</summary>
    public static void SaveReport(string title, string text) => File.WriteAllText(ReportPath, title + "\n" + text);

    /// <summary>Devuelve y borra el resultado pendiente (null si no hay).</summary>
    public static (string Title, string Text)? TakeReport()
    {
        try
        {
            if (!File.Exists(ReportPath)) return null;
            string all = File.ReadAllText(ReportPath);
            File.Delete(ReportPath);
            int nl = all.IndexOf('\n');
            return nl < 0 ? ("Resultado", all) : (all[..nl], all[(nl + 1)..]);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static void Delete()
    {
        foreach (string p in new[] { PathJson, PathJson + ".bak", PathJson + ".tmp" })
        {
            try { File.Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Volver a abrir KRUZH (ventana nueva) después de reiniciar el driver de video.</summary>
public static class SelfRelaunch
{
    public static bool Start(string exePath)
    {
        using var p = Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
        return p is not null;
    }
}

/// <summary>Reinicio de Windows para retomar una medición (sin parsear salida de herramientas).</summary>
public static class SystemRestart
{
    /// <summary>Reinicia en <paramref name="delaySeconds"/> segundos con un aviso visible.</summary>
    public static bool Schedule(int delaySeconds, string reason)
    {
        string comment = reason.Replace("\"", "'");
        using var p = Process.Start(new ProcessStartInfo("shutdown.exe", $"/r /t {Math.Clamp(delaySeconds, 5, 600)} /c \"{comment}\" /d p:2:4")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        p?.WaitForExit(10_000);
        return p is { ExitCode: 0 };
    }

    /// <summary>Cancela un reinicio programado (si el usuario se arrepiente).</summary>
    public static void Cancel()
    {
        using var p = Process.Start(new ProcessStartInfo("shutdown.exe", "/a") { UseShellExecute = false, CreateNoWindow = true });
        p?.WaitForExit(10_000);
    }
}
