using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace KruzhLatency.Infra.Etw;

/// <summary>Latencia acumulada de un driver durante la captura (tiempos en microsegundos).</summary>
public sealed record DriverLatencyStat(
    string FileName,
    string Description,
    long DpcCount,
    double DpcAvgUs,
    double DpcMaxUs,
    long IsrCount,
    double IsrAvgUs,
    double IsrMaxUs,
    double TotalMs)
{
    public double MaxUs => Math.Max(DpcMaxUs, IsrMaxUs);
    public long Count => DpcCount + IsrCount;
}

public sealed record DpcAnalysisResult(
    TimeSpan Duration,
    IReadOnlyList<DriverLatencyStat> Drivers,
    long TotalDpc,
    long TotalIsr,
    string? Error)
{
    public static DpcAnalysisResult Failed(string error) => new(TimeSpan.Zero, Array.Empty<DriverLatencyStat>(), 0, 0, error);
}

/// <summary>
/// Mide, por driver, cuánto tardan sus DPC e ISR usando una sesión ETW del kernel (mismo método que LatencyMon / xperf).
/// Solo lectura: no modifica nada del sistema. Requiere administrador. Se ejecuta a pedido durante unos segundos.
/// Base de la medición del auto-tune MSI (ADR-005).
/// </summary>
public static class DpcLatencyAnalyzer
{
    private const string SessionName = "KruzhLatency-DpcIsr";

    private sealed class Accumulator
    {
        public long DpcCount;
        public double DpcTotalUs;
        public double DpcMaxUs;
        public long IsrCount;
        public double IsrTotalUs;
        public double IsrMaxUs;
    }

    private readonly record struct ModuleRange(ulong Start, ulong End, string FileName);

    public static async Task<DpcAnalysisResult> AnalyzeAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (!(TraceEventSession.IsElevated() ?? false))
        {
            return DpcAnalysisResult.Failed("Se necesita ejecutar KRUZH LATENCY como administrador para medir DPC/ISR.");
        }

        // Dirección de rutina -> estadísticas. Se resuelve a driver al final (los eventos de carga de imágenes
        // del rundown pueden llegar después de los primeros DPC).
        var byRoutine = new Dictionary<ulong, Accumulator>();
        var modules = new List<ModuleRange>();
        var gate = new object();
        long totalDpc = 0, totalIsr = 0;

        try
        {
            // Si quedó una sesión con el mismo nombre de una ejecución anterior que crasheó, se reemplaza.
            using var session = new TraceEventSession(SessionName) { StopOnDispose = true };
            session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.DeferedProcedureCalls |
                KernelTraceEventParser.Keywords.Interrupt |
                KernelTraceEventParser.Keywords.ImageLoad);

            var kernel = session.Source.Kernel;

            void OnImage(ImageLoadTraceData d)
            {
                if (d.ImageSize <= 0 || string.IsNullOrEmpty(d.FileName)) return;
                lock (gate)
                {
                    modules.Add(new ModuleRange(d.ImageBase, d.ImageBase + (ulong)d.ImageSize, d.FileName));
                }
            }

            void OnDpc(DPCTraceData d)
            {
                double us = d.ElapsedTimeMSec * 1000.0;
                if (us < 0) return;
                lock (gate)
                {
                    totalDpc++;
                    if (!byRoutine.TryGetValue(d.Routine, out var acc)) byRoutine[d.Routine] = acc = new Accumulator();
                    acc.DpcCount++;
                    acc.DpcTotalUs += us;
                    if (us > acc.DpcMaxUs) acc.DpcMaxUs = us;
                }
            }

            void OnIsr(ISRTraceData d)
            {
                double us = d.ElapsedTimeMSec * 1000.0;
                if (us < 0) return;
                lock (gate)
                {
                    totalIsr++;
                    if (!byRoutine.TryGetValue(d.Routine, out var acc)) byRoutine[d.Routine] = acc = new Accumulator();
                    acc.IsrCount++;
                    acc.IsrTotalUs += us;
                    if (us > acc.IsrMaxUs) acc.IsrMaxUs = us;
                }
            }

            kernel.ImageGroup += OnImage;
            kernel.PerfInfoDPC += OnDpc;
            kernel.PerfInfoThreadedDPC += OnDpc;
            kernel.PerfInfoTimerDPC += OnDpc;
            kernel.PerfInfoISR += OnIsr;

            var processing = Task.Run(() => session.Source.Process(), CancellationToken.None);
            var watch = Stopwatch.StartNew();
            try
            {
                await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // se cortó antes: se reporta lo medido hasta ahora
            }
            session.Stop();
            await processing.ConfigureAwait(false);
            watch.Stop();

            List<DriverLatencyStat> drivers;
            lock (gate)
            {
                drivers = Aggregate(byRoutine, modules);
            }
            return new DpcAnalysisResult(watch.Elapsed, drivers, totalDpc, totalIsr, null);
        }
        catch (UnauthorizedAccessException)
        {
            return DpcAnalysisResult.Failed("Windows no permitió iniciar la sesión de medición del kernel (se requiere administrador).");
        }
        catch (Exception ex)
        {
            return DpcAnalysisResult.Failed($"No se pudo medir DPC/ISR: {ex.Message}");
        }
    }

    private static List<DriverLatencyStat> Aggregate(Dictionary<ulong, Accumulator> byRoutine, List<ModuleRange> modules)
    {
        var sorted = modules.OrderBy(m => m.Start).ToArray();
        var perDriver = new Dictionary<string, Accumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var (routine, acc) in byRoutine)
        {
            string file = ResolveModule(sorted, routine) ?? "Desconocido";
            if (!perDriver.TryGetValue(file, out var total)) perDriver[file] = total = new Accumulator();
            total.DpcCount += acc.DpcCount;
            total.DpcTotalUs += acc.DpcTotalUs;
            total.DpcMaxUs = Math.Max(total.DpcMaxUs, acc.DpcMaxUs);
            total.IsrCount += acc.IsrCount;
            total.IsrTotalUs += acc.IsrTotalUs;
            total.IsrMaxUs = Math.Max(total.IsrMaxUs, acc.IsrMaxUs);
        }

        return perDriver
            .Select(kv => new DriverLatencyStat(
                Path.GetFileName(kv.Key),
                DriverDescriptions.Describe(kv.Key),
                kv.Value.DpcCount,
                kv.Value.DpcCount > 0 ? kv.Value.DpcTotalUs / kv.Value.DpcCount : 0,
                kv.Value.DpcMaxUs,
                kv.Value.IsrCount,
                kv.Value.IsrCount > 0 ? kv.Value.IsrTotalUs / kv.Value.IsrCount : 0,
                kv.Value.IsrMaxUs,
                (kv.Value.DpcTotalUs + kv.Value.IsrTotalUs) / 1000.0))
            .OrderByDescending(d => d.MaxUs)
            .ToList();
    }

    /// <summary>Búsqueda binaria del módulo que contiene la dirección.</summary>
    private static string? ResolveModule(ModuleRange[] sorted, ulong address)
    {
        int lo = 0, hi = sorted.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var m = sorted[mid];
            if (address < m.Start) hi = mid - 1;
            else if (address >= m.End) lo = mid + 1;
            else return m.FileName;
        }
        return null;
    }
}

/// <summary>Nombre entendible de un driver: tabla de los más comunes + descripción del archivo como respaldo.</summary>
public static class DriverDescriptions
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nvlddmkm.sys"] = "Driver de video NVIDIA",
        ["amdkmdag.sys"] = "Driver de video AMD",
        ["igdkmd64.sys"] = "Driver de video Intel",
        ["igdkmdn64.sys"] = "Driver de video Intel",
        ["dxgkrnl.sys"] = "DirectX (kernel de gráficos)",
        ["ndis.sys"] = "Red (NDIS)",
        ["tcpip.sys"] = "Red (TCP/IP)",
        ["rt640x64.sys"] = "Placa de red Realtek",
        ["rt68cx21x64.sys"] = "Placa de red Realtek",
        ["rtcx21x64.sys"] = "Placa de red Realtek",
        ["e1d68x64.sys"] = "Placa de red Intel",
        ["e2f68.sys"] = "Placa de red Intel 2.5G",
        ["e2fn.sys"] = "Placa de red Intel 2.5G",
        ["usbxhci.sys"] = "Controladora USB 3 (xHCI)",
        ["ucx01000.sys"] = "Controladora USB (framework)",
        ["usbhub3.sys"] = "Hub USB 3",
        ["hidclass.sys"] = "Dispositivos HID (mouse/teclado)",
        ["mouhid.sys"] = "Mouse HID",
        ["kbdhid.sys"] = "Teclado HID",
        ["hdaudbus.sys"] = "Bus de audio HD",
        ["portcls.sys"] = "Audio (Port Class)",
        ["storport.sys"] = "Almacenamiento (Storport)",
        ["stornvme.sys"] = "SSD NVMe",
        ["storahci.sys"] = "Discos SATA (AHCI)",
        ["acpi.sys"] = "ACPI (energía y placa madre)",
        ["wdf01000.sys"] = "Framework de drivers de Windows",
        ["ntoskrnl.exe"] = "Kernel de Windows",
        ["hal.dll"] = "Capa de hardware de Windows (HAL)",
        ["netwtw10.sys"] = "WiFi Intel",
        ["netwtw14.sys"] = "WiFi Intel",
        ["netwtw16.sys"] = "WiFi Intel",
    };

    public static string Describe(string kernelPath)
    {
        string file = Path.GetFileName(kernelPath);
        if (Known.TryGetValue(file, out var known)) return known;

        try
        {
            string? local = ToLocalPath(kernelPath);
            if (local != null && File.Exists(local))
            {
                string? desc = FileVersionInfo.GetVersionInfo(local).FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();
            }
        }
        catch
        {
            // solo es una descripción: si falla se muestra el nombre del archivo
        }
        return file;
    }

    /// <summary>"\SystemRoot\System32\drivers\x.sys" o "\Device\HarddiskVolumeN\Windows\..." → ruta local.</summary>
    private static string? ToLocalPath(string kernelPath)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (kernelPath.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(windows, kernelPath[@"\SystemRoot\".Length..]);
        }
        int idx = kernelPath.IndexOf(@"\Windows\", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            return Path.Combine(windows, kernelPath[(idx + @"\Windows\".Length)..]);
        }
        return Path.IsPathRooted(kernelPath) && kernelPath.Length > 2 && kernelPath[1] == ':' ? kernelPath : null;
    }
}
