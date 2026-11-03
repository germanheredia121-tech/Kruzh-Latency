using System.Diagnostics;
using Microsoft.Win32;

namespace KruzhLatency.Infra.Display;

/// <summary>
/// Caché de configuración de pantallas de Windows (spec 002 / C4): HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\
/// Configuration y \Connectivity. Guarda resolución, Hz, posición y combinaciones por cada monitor/cable que se conectó;
/// con monitores fantasma o EDIDs cambiados (CRU) quedan entradas viejas. Windows las vuelve a crear al reiniciar.
/// </summary>
public static class DisplayCache
{
    public const string GraphicsDrivers = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    public static readonly string[] CacheKeys = { "Configuration", "Connectivity" };

    /// <summary>Exporta la clave con reg.exe (formato .reg estándar, se restaura con reg import). Lanza si falla.</summary>
    public static void Export(string subKey, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        Run("reg.exe", $"export \"HKLM\\{GraphicsDrivers}\\{subKey}\" \"{file}\" /y");
        if (!File.Exists(file)) throw new IOException($"reg export no generó {file}.");
    }

    /// <summary>Restaura un .reg exportado con <see cref="Export"/>. Lanza si falla.</summary>
    public static void Import(string file) => Run("reg.exe", $"import \"{file}\"");

    /// <summary>Borra las subclaves (la clave padre queda: Windows la repuebla al reiniciar). Devuelve cuántas borró.</summary>
    public static int ClearSubKeys(string subKey)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{GraphicsDrivers}\{subKey}", writable: true);
        if (key is null) return 0;
        int n = 0;
        foreach (var child in key.GetSubKeyNames())
        {
            key.DeleteSubKeyTree(child, throwOnMissingSubKey: false);
            n++;
        }
        return n;
    }

    public static int CountSubKeys(string subKey)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{GraphicsDrivers}\{subKey}");
            return key?.SubKeyCount ?? 0;
        }
        catch (System.Security.SecurityException)
        {
            return -1; // sin permiso (no admin)
        }
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
            throw new TimeoutException($"{exe} no respondió.");
        }
        if (p.ExitCode != 0) throw new InvalidOperationException($"{exe} {args} terminó con código {p.ExitCode}.");
    }
}
