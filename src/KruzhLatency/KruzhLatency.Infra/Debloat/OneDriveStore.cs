using System.Diagnostics;
using KruzhLatency.Core.Debloat;

namespace KruzhLatency.Infra.Debloat;

/// <summary>Instalación de OneDrive encontrada: ejecutable y su desinstalador (OneDriveSetup.exe de la misma versión).</summary>
public sealed record OneDriveInstall(string Exe, string? Setup, bool PerMachine);

/// <summary>
/// OneDrive (Win32). Por usuario en %LOCALAPPDATA%\Microsoft\OneDrive o por equipo en Program Files.
/// Se desinstala con su propio OneDriveSetup.exe /uninstall (el método documentado); los archivos del usuario quedan
/// en la carpeta de OneDrive, sin sincronizar.
/// </summary>
public static class OneDriveStore
{
    public static OneDriveInstall? Find()
    {
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "OneDrive");
        string machine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft OneDrive");
        foreach (var (root, perMachine) in new[] { (local, false), (machine, true) })
        {
            string exe = Path.Combine(root, "OneDrive.exe");
            if (!File.Exists(exe)) continue;
            string? setup = Directory.Exists(root)
                ? Directory.GetFiles(root, "OneDriveSetup.exe", SearchOption.AllDirectories)
                           .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
            setup ??= SystemSetup();
            return new OneDriveInstall(exe, setup, perMachine);
        }
        return null;
    }

    /// <summary>El instalador que trae Windows (sirve para reinstalar o desinstalar si la carpeta de versión no lo tiene).</summary>
    public static string? SystemSetup()
    {
        foreach (string dir in new[] { Environment.SystemDirectory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64") })
        {
            string p = Path.Combine(dir, "OneDriveSetup.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Archivos solo-nube dentro de la carpeta de OneDrive (hasta <paramref name="max"/>).</summary>
    public static IReadOnlyList<string> CloudOnlyFiles(int max)
    {
        var found = new List<string>();
        string? root = Environment.GetEnvironmentVariable("OneDrive");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return found;

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (string f in Directory.EnumerateFiles(root, "*", options))
        {
            try
            {
                if (CloudFiles.IsCloudOnly(File.GetAttributes(f))) found.Add(f);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (found.Count >= max) break;
        }
        return found;
    }

    /// <summary>
    /// Descarga los archivos solo-nube leyéndolos completos (OneDrive los baja al abrirlos; tiene que estar corriendo).
    /// Devuelve los que siguen solo en la nube.
    /// </summary>
    public static IReadOnlyList<string> Hydrate(IReadOnlyList<string> files, TimeSpan budget)
    {
        if (Find() is { } install && Process.GetProcessesByName("OneDrive").Length == 0)
        {
            Process.Start(new ProcessStartInfo(install.Exe, "/background") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
            Thread.Sleep(TimeSpan.FromSeconds(8)); // que el cliente de sincronización arranque antes de pedirle archivos
        }

        var deadline = DateTime.UtcNow + budget;
        var buffer = new byte[1 << 20];
        foreach (string f in files)
        {
            if (DateTime.UtcNow > deadline) break;
            try
            {
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                while (fs.Read(buffer, 0, buffer.Length) > 0 && DateTime.UtcNow <= deadline) { }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return files.Where(f => File.Exists(f) && CloudFiles.IsCloudOnly(File.GetAttributes(f))).ToList();
    }

    /// <summary>Cierra OneDrive y ejecuta su desinstalador. Devuelve si después ya no queda el ejecutable.</summary>
    public static (bool Ok, string Detail) Uninstall()
    {
        if (Find() is not { } install) return (true, "OneDrive no está instalado.");
        if (install.Setup is null) return (false, "No se encontró OneDriveSetup.exe para desinstalar.");

        Run(install.Exe, "/shutdown", 15);
        foreach (var p in Process.GetProcessesByName("OneDrive"))
        {
            using (p)
            {
                try { p.Kill(); p.WaitForExit(5000); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            }
        }
        int code = Run(install.Setup, install.PerMachine ? "/uninstall /allusers" : "/uninstall", 180);
        bool gone = !File.Exists(install.Exe);
        return gone
            ? (true, "OneDrive desinstalado. Tus archivos siguen en la carpeta OneDrive, sin sincronizar.")
            : (false, $"El desinstalador terminó (código {code}) pero OneDrive sigue instalado.");
    }

    private static int Run(string exe, string args, int timeoutSeconds)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });
        if (p is null) return -1;
        if (!p.WaitForExit(timeoutSeconds * 1000)) return -2;
        return p.ExitCode;
    }
}
