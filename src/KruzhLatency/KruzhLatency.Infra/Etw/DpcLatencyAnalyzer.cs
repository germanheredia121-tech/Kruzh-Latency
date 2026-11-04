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
    double TotalMs,
    /// <summary>Percentil 99 de ISR + DPC del driver (µs, resolución 1 µs). Criterio del auto-tune (spec 003).</summary>
    double P99Us = 0)
{
    public double MaxUs => Math.Max(DpcMaxUs, IsrMaxUs);
    public long Count => DpcCount + IsrCount;
}

public sealed record DpcAnalysisResult(
    TimeSpan Duration,
    IReadOnlyList<DriverLatencyStat> Drivers,
    long TotalDpc,
    long TotalIsr,
    string? Error,
    /// <summary>Totales por CPU lógico (spec 003 / G3). Vacío si la medición falló.</summary>
    IReadOnlyList<CpuLatencyStat>? Cpus = null)
{
    public static DpcAnalysisResult Failed(string error) => new(TimeSpan.Zero, Array.Empty<DriverLatencyStat>(), 0, 0, error);

    /// <summary>Histogramas por archivo de driver (para combinar varios en un p99, spec 003).</summary>
    public IReadOnlyDictionary<string, KruzhLatency.Core.Latency.LatencyHistogram>? Histograms { get; init; }

    /// <summary>p99 y cantidad de ISR+DPC de un conjunto de drivers juntos (ej. USBXHCI + Wdf01000).</summary>
    public (double P99Us, long Count) CombinedP99(IEnumerable<string> driverFiles)
    {
        var merged = new KruzhLatency.Core.Latency.LatencyHistogram();
        if (Histograms is not null)
        {
            foreach (string f in driverFiles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Histograms.TryGetValue(f, out var h)) merged.Merge(h);
            }
        }
        return (merged.Percentile(99), merged.Count);
    }
}

/// <summary>Carga de interrupciones de un CPU lógico durante la captura.</summary>
public sealed record CpuLatencyStat(int Cpu, long DpcCount, long IsrCount, double TotalMs, double P99Us)
{
    public long Count => DpcCount + IsrCount;
}

/// <summary>
/// Mide, por driver, cuánto tardan sus DPC e ISR usando una sesión ETW del kernel (mismo método que LatencyMon / xperf).
/// Solo lectura: no modifica nada del sistema. Requiere administrador.
/// La sesión corre hasta que se la detiene (sirve para medir jugando) y se puede consultar en vivo con <see cref="Snapshot"/>.
/// Base de la medición del auto-tune MSI (ADR-005).
/// </summary>
public sealed class DpcLatencySession : IDisposable
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
        public readonly KruzhLatency.Core.Latency.LatencyHistogram All = new();
    }

    private sealed class CpuAccumulator
    {
        public long DpcCount;
        public long IsrCount;
        public readonly KruzhLatency.Core.Latency.LatencyHistogram All = new();
    }

    private readonly Dictionary<int, CpuAccumulator> _byCpu = new();

    private CpuAccumulator Cpu(int cpu)
    {
        if (!_byCpu.TryGetValue(cpu, out var c)) _byCpu[cpu] = c = new CpuAccumulator();
        return c;
    }

    private readonly record struct ModuleRange(ulong Start, ulong End, string FileName);

    private readonly TraceEventSession _session;
    private readonly Task _processing;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly Dictionary<ulong, Accumulator> _byRoutine = new();
    private readonly List<ModuleRange> _modules = new();
    private long _totalDpc;
    private long _totalIsr;
    private bool _stopped;

    public TimeSpan Elapsed => _watch.Elapsed;

    private DpcLatencySession(TraceEventSession session)
    {
        _session = session;
        var kernel = session.Source.Kernel;
        kernel.ImageGroup += OnImage;
        kernel.PerfInfoDPC += OnDpc;
        kernel.PerfInfoThreadedDPC += OnDpc;
        kernel.PerfInfoTimerDPC += OnDpc;
        kernel.PerfInfoISR += OnIsr;
        _processing = Task.Run(() => session.Source.Process());
    }

    /// <summary>Inicia la medición. Devuelve null y un error legible si no se pudo (sin admin, etc.).</summary>
    public static DpcLatencySession? TryStart(out string? error)
    {
        error = null;
        if (!(TraceEventSession.IsElevated() ?? false))
        {
            error = "Se necesita ejecutar KRUZH LATENCY como administrador para medir DPC/ISR.";
            return null;
        }

        try
        {
            // Si quedó una sesión con el mismo nombre de una ejecución anterior que crasheó, se reemplaza.
            var session = new TraceEventSession(SessionName) { StopOnDispose = true };
            session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.DeferedProcedureCalls |
                KernelTraceEventParser.Keywords.Interrupt |
                KernelTraceEventParser.Keywords.ImageLoad);
            return new DpcLatencySession(session);
        }
        catch (UnauthorizedAccessException)
        {
            error = "Windows no permitió iniciar la sesión de medición del kernel (se requiere administrador).";
            return null;
        }
        catch (Exception ex)
        {
            error = $"No se pudo iniciar la medición de DPC/ISR: {ex.Message}";
            return null;
        }
    }

    /// <summary>Resultado parcial hasta este momento (thread-safe, la medición sigue).</summary>
    public DpcAnalysisResult Snapshot()
    {
        lock (_gate)
        {
            var cpus = _byCpu.OrderBy(kv => kv.Key)
                .Select(kv => new CpuLatencyStat(kv.Key, kv.Value.DpcCount, kv.Value.IsrCount, kv.Value.All.TotalUs / 1000.0, kv.Value.All.Percentile(99)))
                .ToList();
            var (drivers, histograms) = Aggregate(_byRoutine, _modules);
            return new DpcAnalysisResult(_watch.Elapsed, drivers, _totalDpc, _totalIsr, null, cpus) { Histograms = histograms };
        }
    }

    /// <summary>Detiene la sesión ETW y devuelve el resultado final.</summary>
    public async Task<DpcAnalysisResult> StopAsync()
    {
        if (!_stopped)
        {
            _stopped = true;
            _watch.Stop();
            _session.Stop();
            try { await _processing.ConfigureAwait(false); } catch { }
        }
        return Snapshot();
    }

    public void Dispose()
    {
        if (!_stopped)
        {
            _stopped = true;
            try { _session.Stop(); } catch { }
        }
        _session.Dispose();
    }

    private void OnImage(ImageLoadTraceData d)
    {
        if (d.ImageSize <= 0 || string.IsNullOrEmpty(d.FileName)) return;
        lock (_gate)
        {
            _modules.Add(new ModuleRange(d.ImageBase, d.ImageBase + (ulong)d.ImageSize, d.FileName));
        }
    }

    private void OnDpc(DPCTraceData d)
    {
        double us = d.ElapsedTimeMSec * 1000.0;
        if (us < 0) return;
        lock (_gate)
        {
            _totalDpc++;
            if (!_byRoutine.TryGetValue(d.Routine, out var acc)) _byRoutine[d.Routine] = acc = new Accumulator();
            acc.DpcCount++;
            acc.DpcTotalUs += us;
            if (us > acc.DpcMaxUs) acc.DpcMaxUs = us;
            acc.All.Add(us);
            var cpu = Cpu(d.ProcessorNumber);
            cpu.DpcCount++;
            cpu.All.Add(us);
        }
    }

    private void OnIsr(ISRTraceData d)
    {
        double us = d.ElapsedTimeMSec * 1000.0;
        if (us < 0) return;
        lock (_gate)
        {
            _totalIsr++;
            if (!_byRoutine.TryGetValue(d.Routine, out var acc)) _byRoutine[d.Routine] = acc = new Accumulator();
            acc.IsrCount++;
            acc.IsrTotalUs += us;
            if (us > acc.IsrMaxUs) acc.IsrMaxUs = us;
            acc.All.Add(us);
            var cpu = Cpu(d.ProcessorNumber);
            cpu.IsrCount++;
            cpu.All.Add(us);
        }
    }

    private static (List<DriverLatencyStat> Drivers, Dictionary<string, KruzhLatency.Core.Latency.LatencyHistogram> Histograms) Aggregate(
        Dictionary<ulong, Accumulator> byRoutine, List<ModuleRange> modules)
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
            total.All.Merge(acc.All);
        }

        var histograms = new Dictionary<string, KruzhLatency.Core.Latency.LatencyHistogram>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, acc) in perDriver)
        {
            string name = Path.GetFileName(file);
            if (!histograms.TryGetValue(name, out var h)) histograms[name] = h = new KruzhLatency.Core.Latency.LatencyHistogram();
            h.Merge(acc.All);
        }

        var drivers = perDriver
            .Select(kv => new DriverLatencyStat(
                Path.GetFileName(kv.Key),
                DriverDescriptions.Describe(kv.Key),
                kv.Value.DpcCount,
                kv.Value.DpcCount > 0 ? kv.Value.DpcTotalUs / kv.Value.DpcCount : 0,
                kv.Value.DpcMaxUs,
                kv.Value.IsrCount,
                kv.Value.IsrCount > 0 ? kv.Value.IsrTotalUs / kv.Value.IsrCount : 0,
                kv.Value.IsrMaxUs,
                (kv.Value.DpcTotalUs + kv.Value.IsrTotalUs) / 1000.0,
                kv.Value.All.Percentile(99)))
            .OrderByDescending(d => d.MaxUs)
            .ToList();
        return (drivers, histograms);
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

/// <summary>Medición de duración fija (la usará el auto-tune MSI para comparar combinaciones).</summary>
public static class DpcLatencyAnalyzer
{
    public static async Task<DpcAnalysisResult> AnalyzeAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        using var session = DpcLatencySession.TryStart(out string? error);
        if (session == null) return DpcAnalysisResult.Failed(error ?? "No se pudo iniciar la medición.");
        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // se reporta lo medido hasta ahora
        }
        return await session.StopAsync().ConfigureAwait(false);
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
