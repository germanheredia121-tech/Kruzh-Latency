using System.Diagnostics;

namespace KruzhLatency.Infra.Benchmark;

/// <summary>
/// Spec 003 / G2: carga sintética de GPU (lava-triangle de liblava, MIT) medida por fuera con PresentMon (Intel, MIT).
/// Ninguno de los dos toca juegos (ADR-004). PresentMon necesita administrador (sesión ETW), que KRUZH ya es.
/// </summary>
public static class SyntheticBenchmark
{
    public const string LoadProcess = "lava-triangle";

    public static string LavaTriangle(string toolsDir) => Path.Combine(toolsDir, "liblava", "lava-triangle.exe");
    public static string PresentMon(string toolsDir) => Path.Combine(toolsDir, "PresentMon", "PresentMon.exe");

    public static bool ToolsPresent(string toolsDir) => File.Exists(LavaTriangle(toolsDir)) && File.Exists(PresentMon(toolsDir));

    /// <summary>
    /// Abre la carga, espera el calentamiento, captura <paramref name="capture"/> con PresentMon y cierra todo.
    /// Devuelve el texto CSV o un error. Siempre cierra la carga aunque falle o se cancele.
    /// </summary>
    public static (string? Csv, string? Error) RunGpuLoad(string toolsDir, TimeSpan warmup, TimeSpan capture, CancellationToken cancel)
    {
        if (!ToolsPresent(toolsDir)) return (null, "Faltan PresentMon o lava-triangle junto a KRUZH (reinstalá la app).");

        string csvPath = Path.Combine(Path.GetTempPath(), $"kruzh-pm-{Guid.NewGuid():N}.csv");
        Process? load = null;
        try
        {
            load = Process.Start(new ProcessStartInfo(LavaTriangle(toolsDir))
            {
                WorkingDirectory = Path.GetDirectoryName(LavaTriangle(toolsDir))!,
                UseShellExecute = false,
            });
            if (load is null) return (null, "No se pudo abrir la carga de GPU.");
            if (cancel.WaitHandle.WaitOne(warmup)) return (null, "Cancelado.");
            if (load.HasExited) return (null, $"La carga de GPU se cerró sola (código {load.ExitCode}). ¿Driver de video sin Vulkan?");

            int seconds = Math.Max(5, (int)capture.TotalSeconds);
            using var pm = Process.Start(new ProcessStartInfo(PresentMon(toolsDir),
                $"--process_name {LoadProcess}.exe --output_file \"{csvPath}\" --timed {seconds} --terminate_after_timed " +
                "--stop_existing_session --session_name KruzhLatency-PresentMon --no_console_stats")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (pm is null) return (null, "No se pudo iniciar PresentMon.");
            if (!pm.WaitForExit((seconds + 30) * 1000))
            {
                try { pm.Kill(); } catch (InvalidOperationException) { }
                return (null, "PresentMon no terminó a tiempo.");
            }
            if (!File.Exists(csvPath)) return (null, $"PresentMon no generó datos (código {pm.ExitCode}).");
            return (File.ReadAllText(csvPath), null);
        }
        finally
        {
            if (load is { HasExited: false })
            {
                try { load.Kill(); load.WaitForExit(5000); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            }
            load?.Dispose();
            try { File.Delete(csvPath); } catch (IOException) { }
        }
    }
}
