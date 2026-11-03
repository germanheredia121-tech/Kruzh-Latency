using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using KruzhLatency.Models;
using Microsoft.Win32;

namespace KruzhLatency.Services;

public sealed class NvramAdvisoryService
{
    private readonly HardwareChassisService _hwService;
    public string LastLoadedFilePath { get; private set; } = string.Empty;
    public string LastRawContent { get; private set; } = string.Empty;

    public NvramAdvisoryService(HardwareChassisService hwService)
    {
        _hwService = hwService;
    }

    public (bool IsSupported, string CompatibilityMessage) CheckAmiAptioCompatibility()
    {
        var hw = _hwService.CurrentProfile;
        if (hw.Chassis == ChassisKind.Laptop)
        {
            return (false, $"BLOQUEO PREVENTIVO DE SEGURIDAD: Tu equipo es una Laptop ({hw.BaseBoardModel}). Las portátiles suelen tener bloqueo de escritura NVRAM OEM y modificar parámetros ocultos por SCEWIN puede brickear el equipo sin jumper Clear CMOS accesible.");
        }
        if (!hw.IsAmiAptioBios)
        {
            return (false, $"BIOS NO COMPATIBLE CON SCEWIN: El proveedor detectado es '{hw.BiosVendor}' (se requiere American Megatrends / AMI Aptio V). No utilices SCEWIN en este equipo.");
        }
        return (true, $"BIOS AMI Aptio Compatible detectada ({hw.BiosVendor} — Versión {hw.BiosVersion} en placa {hw.BaseBoardModel}). Modo solo lectura: importá tu nvram.txt para ver recomendaciones.");
    }

    public List<NvramCheckItem> AnalyzeNvramFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new List<NvramCheckItem>();
        }

        LastLoadedFilePath = filePath;
        LastRawContent = File.ReadAllText(filePath, Encoding.UTF8);
        var hw = _hwService.CurrentProfile;

        var definitions = GetCuratedBiosDefinitions(hw);
        var results = new List<NvramCheckItem>();

        foreach (var def in definitions)
        {
            var match = FindSetupQuestionBlock(LastRawContent, def.QuestionName);
            if (!match.Found)
            {
                continue;
            }

            bool isBad = def.AvoidValues.Any(bad =>
                match.CurrentOption.Contains(bad, StringComparison.OrdinalIgnoreCase));


            results.Add(new NvramCheckItem
            {
                QuestionName = def.QuestionName,
                Impact = def.Impact,
                CurrentValue = match.CurrentOption,
                RecommendedValue = def.RecommendedValue,
                AvailableOptions = match.AllOptions,
                AvoidValues = def.AvoidValues,
                TechnicalReason = def.TechnicalReason,
                FoundInFile = true,
                NeedsChange = isBad,
                IsAnticheatSensitive = def.IsAnticheatSensitive
            });
        }

        return results
            .OrderByDescending(x => x.NeedsChange)
            .ThenBy(x => x.Impact switch
            {
                "EXTREMO" => 0,
                "CRÍTICO" => 1,
                "ALTO" => 2,
                "MEDIO" => 3,
                _ => 4
            })
            .ToList();
    }

    public (string Sha256Hex, string VerificationDetail) VerifyScewinBinarySha256(string exePath)
    {
        if (!File.Exists(exePath))
        {
            return (string.Empty, "Archivo no encontrado.");
        }
        byte[] bytes = File.ReadAllBytes(exePath);
        string sha = Convert.ToHexString(SHA256.HashData(bytes));
        long kb = bytes.Length / 1024;
        return (sha, $"Archivo: {Path.GetFileName(exePath)} ({kb} KB) | SHA-256: {sha}");
    }

    private static (bool Found, string CurrentOption, List<string> AllOptions) FindSetupQuestionBlock(string raw, string questionName)
    {
        string pattern = $@"Setup Question\s*=\s*[^\r\n]*{Regex.Escape(questionName)}[^\r\n]*[\r\n]+((?:.|\n)*?)(?=\r?\n\r?\n|\Z)";
        var m = Regex.Match(raw, pattern, RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return (false, string.Empty, new List<string>());
        }

        string block = m.Value;
        var starMatch = Regex.Match(block, @"^[ \t]*\*\[[\w]+\]\s*(.+?)(?:\s*//|\r|\n|\Z)", RegexOptions.Multiline);
        string current = starMatch.Success ? starMatch.Groups[1].Value.Trim() : "Valor numérico / No listado";

        var allMatches = Regex.Matches(block, @"^[ \t]*\*?\[[\w]+\]\s*(.+?)(?:\s*//|\r|\n|\Z)", RegexOptions.Multiline);
        var opts = allMatches.Select(x => x.Groups[1].Value.Trim()).Distinct().ToList();

        return (true, current, opts);
    }

    private static List<NvramCheckItem> GetCuratedBiosDefinitions(HardwareProfile hw)
    {
        bool isStaticOc = hw.ClockingMode == CpuClockingMode.StaticAllCoreOverclock;

        return new List<NvramCheckItem>
        {
            new() { QuestionName = "Global C-state Control", RecommendedValue = "Disabled", AvoidValues = new() { "Auto", "Enabled" }, Impact = "CRÍTICO", TechnicalReason = "Impide que los núcleos entren en C6/C7 deep sleep (ahorra 100-300 µs de latencia de despertar por frame en Desktop)." },
            new() { QuestionName = "AMD KVM Mouse Protocol", RecommendedValue = "Absolute", AvoidValues = new() { "Auto", "Simple" }, Impact = "CRÍTICO", TechnicalReason = "Simple/Auto interpolan coordenadas del mouse. Absolute entrega coordenadas 1:1 directas." },
            new() { QuestionName = "PM L1 SS", RecommendedValue = "Disabled", AvoidValues = new() { "L1.1", "L1.2", "L1.1_L1.2" }, Impact = "CRÍTICO", TechnicalReason = "Desactiva sub-estados de sueño profundo PCIe L1 para mantener latencia mínima de bus." },
            new() { QuestionName = "Aggressive Link PM Capability", RecommendedValue = "Disabled", AvoidValues = new() { "Enabled" }, Impact = "CRÍTICO", TechnicalReason = "Evita que el controlador SATA entre en Partial/Slumber (elimina spikes de DPC de almacenamiento)." },
            new() { QuestionName = "XHCI D3 Support", RecommendedValue = "Disabled", AvoidValues = new() { "Auto", "Enabled" }, Impact = "ALTO", TechnicalReason = "Impide que la controladora USB 3.0 (xHCI) entre en ahorro D3. Crítico para estabilidad de polling rate en el mouse." },
            new() { QuestionName = "Legacy USB Support", RecommendedValue = "Disabled", AvoidValues = new() { "Enabled", "Auto" }, Impact = "ALTO", TechnicalReason = "El soporte Legacy USB emula PS/2 vía interrupciones SMM (System Management Mode), causando micro-stutters." },
            new() { QuestionName = "XHCI Hand-off", RecommendedValue = "Enabled", AvoidValues = new() { "Disabled" }, Impact = "MEDIO", TechnicalReason = "Entrega el control directo del controlador xHCI al driver de Windows sin intervención del BIOS." },
            new() { QuestionName = "Clock Power Management", RecommendedValue = "Disabled", AvoidValues = new() { "Enabled" }, Impact = "ALTO", TechnicalReason = "Apagar CLKREQ# mantiene el reloj de referencia PCIe activo permanentemente." },
            new() { QuestionName = "Power Supply Idle Control", RecommendedValue = "Typical Current Idle", AvoidValues = new() { "Low Current Idle", "Auto" }, Impact = "ALTO", TechnicalReason = "Mantiene corriente base estable en el VRM/fuente de plataformas AMD Ryzen." },
            new() { QuestionName = "PSS Support", RecommendedValue = "Disabled", AvoidValues = new() { "Enabled", "Auto" }, Impact = "ALTO", TechnicalReason = isStaticOc ? "Esencial con tu All-Core Overclock: desactiva estados P de Cool'n'Quiet para mantener tu frecuencia fija." : "Desactiva estados P de ahorro Cool'n'Quiet en reposo." },
            new() { QuestionName = "CPPC", RecommendedValue = isStaticOc ? "Disabled" : "Enabled", AvoidValues = new() { isStaticOc ? "Enabled" : "Disabled" }, Impact = "MEDIO", TechnicalReason = isStaticOc ? "Con All-Core Overclock fijo conviene Disabled para que Windows no intente escalar relojes." : "Con PBO + Curve Optimizer conviene Enabled para que el SMU gestione el boost." },
            new() { QuestionName = "CPPC Preferred Cores", RecommendedValue = "Disabled", AvoidValues = new() { "Enabled", "Auto" }, Impact = "MEDIO", TechnicalReason = "Evita que el scheduler rote hilos constantemente entre dos núcleos preferidos, reduciendo misses de caché L1/L2." },
            new() { QuestionName = "Int. Clk Differential Spread", RecommendedValue = "Disabled", AvoidValues = new() { "Auto", "Enabled" }, Impact = "ALTO", TechnicalReason = "Desactiva la modulación Spread Spectrum del reloj interno del chipset (menos jitter en PCIe/USB)." },
            new() { QuestionName = "SB Clock Spread Spectrum", RecommendedValue = "Disabled", AvoidValues = new() { "Auto", "Enabled" }, Impact = "ALTO", TechnicalReason = "Elimina el jitter inducido por Spread Spectrum en el South Bridge." },
            new() { QuestionName = "Chipset Power Saving Features", RecommendedValue = "Disabled", AvoidValues = new() { "Enabled", "Auto" }, Impact = "ALTO", TechnicalReason = "Mantiene los buses del chipset siempre activos sin latencia de wakeup." },
            new() { QuestionName = "PSPP Policy", RecommendedValue = "Disabled", AvoidValues = new() { "Performance", "Balanced", "Power Saving", "Auto" }, Impact = "ALTO", TechnicalReason = "Evita que PCIe Speed Power Policy reduzca la generación del enlace PCIe en reposo." },
            new() { QuestionName = "IOMMU", RecommendedValue = "Disabled", AvoidValues = new() { "Auto", "Enabled" }, Impact = "ALTO", IsAnticheatSensitive = true, TechnicalReason = "Reduce overhead de traducción DMA en USB/Red. ATENCIÓN: Vanguard/FACEIT con VBS pueden requerir IOMMU Enabled; desmarcado por defecto para proteger Anticheat." },
            new() { QuestionName = "SVM Mode", RecommendedValue = "Disabled", AvoidValues = new() { "Enabled" }, Impact = "MEDIO", IsAnticheatSensitive = true, TechnicalReason = "Desactiva virtualización AMD-V (elimina hipervisor). Si jugás con VBS exigido por anticheat, mantenelo Enabled." },
            new() { QuestionName = "Security Device Support", RecommendedValue = "Enabled", AvoidValues = new() { "Disabled" }, Impact = "ALTO", IsAnticheatSensitive = true, TechnicalReason = "Regla KRUZH: Con BIOS actualizado, mantener fTPM ON por defecto (obligatorio para Valorant y FACEIT AC en Win11). Solo apagar si no usás esos anticheats y sufrís spikes SPI." },
            new() { QuestionName = "HD Audio Controller", RecommendedValue = "Disabled", AvoidValues = new() { "Enabled", "Auto" }, Impact = "ALTO", IsAnticheatSensitive = true, TechnicalReason = "Apaga el chip Realtek integrado en BIOS. SOLO marcar si usás auriculares/DAC por USB (desmarcado por defecto para no dejarte sin sonido Jack 3.5mm)." },
            new() { QuestionName = "SMT Control", RecommendedValue = "Disabled", AvoidValues = new() { "Enabled", "Auto" }, Impact = "EXTREMO", IsAnticheatSensitive = true, TechnicalReason = "Opcional avanzado: apagar SMT dedica el 100% de la caché al hilo físico en CPUs de >=8 núcleos (desmarcado por defecto)." },
            new() { QuestionName = "AB Clock Gating", RecommendedValue = "Disabled", AvoidValues = new() { "Auto", "Enabled" }, Impact = "MEDIO", TechnicalReason = "Desactiva el clock gating del bus AMD (AB)." },
            new() { QuestionName = "EHCI D3 Support", RecommendedValue = "Disabled", AvoidValues = new() { "Auto", "Enabled" }, Impact = "MEDIO", TechnicalReason = "Desactiva el estado de ahorro D3 en controladoras USB EHCI." },
            new() { QuestionName = "SB C1E Support", RecommendedValue = "Disabled", AvoidValues = new() { "Auto", "Enabled" }, Impact = "MEDIO", TechnicalReason = "Evita paradas de reloj C1E coordinadas por el South Bridge." },
            new() { QuestionName = "USB MSI Option", RecommendedValue = "Enabled", AvoidValues = new() { "Disabled", "Auto" }, Impact = "MEDIO", TechnicalReason = "Activa soporte MSI desde el firmware para los controladores USB." },
            new() { QuestionName = "MsiDis in HPET", RecommendedValue = "Enabled", AvoidValues = new() { "Disabled", "Auto" }, Impact = "MEDIO", TechnicalReason = "Mejora el manejo de interrupciones del bloque temporizador." },
            new() { QuestionName = "Fast Boot", RecommendedValue = "Disabled", AvoidValues = new() { "Enabled", "Ultra Fast" }, Impact = "BAJO", TechnicalReason = "Realiza entrenamiento completo de memoria y enlaces PCIe en cada arranque." },
            new() { QuestionName = "Data Link Feature Exchange", RecommendedValue = "Disabled", AvoidValues = new() { "Enabled" }, Impact = "BAJO", TechnicalReason = "Reduce negociación de protocolo extra en el enlace PCIe." },
            new() { QuestionName = "Fusion Message C State", RecommendedValue = "Disabled", AvoidValues = new() { "Enabled" }, Impact = "BAJO", TechnicalReason = "Evita tráfico de mensajes C-state entre CPU y chipset FCH." },
            new() { QuestionName = "EHCI Traffic Handling", RecommendedValue = "Disabled", AvoidValues = new() { "Enabled" }, Impact = "BAJO", TechnicalReason = "Desactiva interrupciones legacy de tráfico EHCI." },
            new() { QuestionName = "USB Phy Power Down", RecommendedValue = "Disabled", AvoidValues = new() { "Auto", "Enabled" }, Impact = "BAJO", TechnicalReason = "Mantiene energizada la capa física PHY de los puertos USB." },
            new() { QuestionName = "TimerTick Tracking", RecommendedValue = "Disabled", AvoidValues = new() { "Enabled" }, Impact = "BAJO", TechnicalReason = "Elimina mensajes periódicos de seguimiento de timer tick." },
            new() { QuestionName = "Clock Interrupt Tag", RecommendedValue = "Disabled", AvoidValues = new() { "Enabled" }, Impact = "BAJO", TechnicalReason = "Quita etiquetado extra en interrupciones de reloj." },
            new() { QuestionName = "Fusion Message C Multi-Core", RecommendedValue = "Disabled", AvoidValues = new() { "Enabled" }, Impact = "BAJO", TechnicalReason = "Desactiva rastreo de C-state por núcleo en el chipset." },
            new() { QuestionName = "Network Stack Driver Support", RecommendedValue = "Disabled", AvoidValues = new() { "Enabled" }, Impact = "BAJO", TechnicalReason = "Desactiva la pila de arranque por red PXE en UEFI." }
        };
    }
}

public sealed partial class RealtimeMonitorAndScoreService
{
    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryTimerResolution(out uint minimumResolution, out uint maximumResolution, out uint currentResolution);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out ulong lpIdleTime, out ulong lpKernelTime, out ulong lpUserTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegOpenKeyExW(IntPtr hKey, string lpSubKey, uint ulOptions, uint samDesired, out IntPtr phkResult);

    [LibraryImport("advapi32.dll")]
    private static partial int RegNotifyChangeKeyValue(IntPtr hKey, [MarshalAs(UnmanagedType.Bool)] bool bWatchSubtree, uint dwNotifyFilter, IntPtr hEvent, [MarshalAs(UnmanagedType.Bool)] bool fAsynchronous);

    [LibraryImport("advapi32.dll")]
    private static partial int RegCloseKey(IntPtr hKey);

    private static readonly IntPtr HKEY_CURRENT_USER = new(unchecked((int)0x80000001));
    private static readonly IntPtr HKEY_LOCAL_MACHINE = new(unchecked((int)0x80000002));
    private const uint KEY_NOTIFY = 0x0010;
    private const uint REG_NOTIFY_CHANGE_NAME = 0x00000001;
    private const uint REG_NOTIFY_CHANGE_LAST_SET = 0x00000004;

    private PerformanceCounter? _dpcTimeCounter;
    private PerformanceCounter? _interruptTimeCounter;
    private PerformanceCounter? _privilegedTimeCounter;
    private ulong _prevIdleTime;
    private ulong _prevKernelTime;
    private ulong _prevUserTime;

    private List<PerformanceCounter> _gpu3DCounters = new();
    private DateTime _lastGpuCounterRefreshUtc = DateTime.MinValue;
    private double _lastGpuPercent;
    private bool _gpuSampleInFlight;
    private bool _watchersStarted;

    public RealtimeMonitorAndScoreService()
    {
        try
        {
            GetSystemTimes(out _prevIdleTime, out _prevKernelTime, out _prevUserTime);
            _dpcTimeCounter = new PerformanceCounter("Processor", "% DPC Time", "_Total", true);
            _interruptTimeCounter = new PerformanceCounter("Processor", "% Interrupt Time", "_Total", true);
            _privilegedTimeCounter = new PerformanceCounter("Processor", "% Privileged Time", "_Total", true);
            _dpcTimeCounter.NextValue();
            _interruptTimeCounter.NextValue();
            _privilegedTimeCounter.NextValue();
        }
        catch { }
    }

    public void StartRegistryWatchers(Action onExternalRegistryChanged)
    {
        if (_watchersStarted) return;
        _watchersStarted = true;

        var watchedKeys = new (IntPtr Root, string SubKey)[]
        {
            (HKEY_LOCAL_MACHINE, @"SYSTEM\CurrentControlSet\Control\PriorityControl"),
            (HKEY_LOCAL_MACHINE, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile"),
            (HKEY_CURRENT_USER,  @"Control Panel\Mouse"),
            (HKEY_LOCAL_MACHINE, @"SYSTEM\CurrentControlSet\Services\mouclass\Parameters"),
            (HKEY_LOCAL_MACHINE, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers"),
            (HKEY_LOCAL_MACHINE, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling"),
            (HKEY_LOCAL_MACHINE, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management"),
            (HKEY_LOCAL_MACHINE, @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel"),
            (HKEY_CURRENT_USER,  @"System\GameConfigStore"),
            (HKEY_CURRENT_USER,  @"Software\Microsoft\GameBar")
        };

        foreach (var (root, subKey) in watchedKeys)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                if (RegOpenKeyExW(root, subKey, 0, KEY_NOTIFY, out IntPtr hKey) != 0 || hKey == IntPtr.Zero)
                {
                    return;
                }
                try
                {
                    while (true)
                    {
                        int rc = RegNotifyChangeKeyValue(
                            hKey,
                            true,
                            REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET,
                            IntPtr.Zero,
                            false);
                        if (rc != 0) break;
                        try
                        {
                            onExternalRegistryChanged();
                        }
                        catch { }
                    }
                }
                finally
                {
                    RegCloseKey(hKey);
                }
            });
        }
    }

    public (double CurrentMs, double MinMs, double MaxMs) QueryTimerResolutionMs()
    {
        if (NtQueryTimerResolution(out uint min, out uint max, out uint cur) == 0)
        {
            return (Math.Round(cur / 10000.0, 3), Math.Round(min / 10000.0, 3), Math.Round(max / 10000.0, 3));
        }
        return (0.0, 0.0, 0.0);
    }

    public (double KernelDpcPercent, double TotalCpuPercent) SampleCpuAndKernelLoad()
    {
        double cpuPct = 0.0;
        if (GetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
        {
            ulong idleDiff = idle - _prevIdleTime;
            ulong kernelDiff = kernel - _prevKernelTime;
            ulong userDiff = user - _prevUserTime;
            _prevIdleTime = idle;
            _prevKernelTime = kernel;
            _prevUserTime = user;

            ulong totalSys = kernelDiff + userDiff;
            if (totalSys > 0)
            {
                cpuPct = (double)(totalSys - idleDiff) * 100.0 / totalSys;
            }
        }

        double dpcAndIsrPct = 0.0;
        try
        {
            if (_dpcTimeCounter != null && _interruptTimeCounter != null)
            {
                dpcAndIsrPct = _dpcTimeCounter.NextValue() + _interruptTimeCounter.NextValue();
            }
            else if (_privilegedTimeCounter != null)
            {
                dpcAndIsrPct = _privilegedTimeCounter.NextValue();
            }
        }
        catch { }

        return (Math.Round(Math.Clamp(dpcAndIsrPct, 0, 100), 2), Math.Round(Math.Clamp(cpuPct, 0, 100), 1));
    }

    public (double UsedGb, double TotalGb, double UsedPercent) SampleRamUsage()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem) && mem.ullTotalPhys > 0)
        {
            double totalGb = mem.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
            double availGb = mem.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
            double usedGb = Math.Max(0, totalGb - availGb);
            double pct = (usedGb / totalGb) * 100.0;
            return (Math.Round(usedGb, 1), Math.Round(totalGb, 1), Math.Round(pct, 0));
        }
        return (0.0, 0.0, 0.0);
    }

    public double SampleGpuUsagePercent()
    {
        if (!_gpuSampleInFlight)
        {
            _gpuSampleInFlight = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (_gpu3DCounters.Count == 0 || (DateTime.UtcNow - _lastGpuCounterRefreshUtc).TotalSeconds > 15)
                    {
                        foreach (var old in _gpu3DCounters)
                        {
                            try { old.Dispose(); } catch { }
                        }
                        _gpu3DCounters.Clear();

                        var cat = new PerformanceCounterCategory("GPU Engine");
                        var names = cat.GetInstanceNames();
                        foreach (string inst in names)
                        {
                            if (inst.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase))
                            {
                                var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true);
                                _ = pc.NextValue();
                                _gpu3DCounters.Add(pc);
                            }
                        }
                        _lastGpuCounterRefreshUtc = DateTime.UtcNow;
                    }

                    double sum = 0.0;
                    var valid = new List<PerformanceCounter>();
                    foreach (var c in _gpu3DCounters)
                    {
                        try
                        {
                            sum += c.NextValue();
                            valid.Add(c);
                        }
                        catch
                        {
                            try { c.Dispose(); } catch { }
                        }
                    }
                    _gpu3DCounters = valid;
                    _lastGpuPercent = Math.Round(Math.Clamp(sum, 0.0, 100.0), 1);
                }
                catch
                {
                    _lastGpuPercent = 0.0;
                }
                finally
                {
                    _gpuSampleInFlight = false;
                }
            });
        }
        return _lastGpuPercent;
    }

    public (int Score, string Grade, List<string> Positives, List<string> Warnings, List<string> Issues) CalculateDynamicScore(
        HardwareProfile hw,
        DeclarativeTweakEngine engine)
    {
        // Usa el estado ya leído (el refresco corre en segundo plano): releer acá congelaba la UI y duplicaba el trabajo (B19).
        var applicableTweaks = engine.AllTweaks
            .Where(t => t.IsApplicableBool &&
                        t.Evaluation.Level == RecommendationLevel.Recommended &&
                        !t.IsAnticheatUnsafeBool &&
                        !t.ReducesSecurityBool)
            .ToList();

        var positives = new List<string>();
        var warnings = new List<string>();
        var issues = new List<string>();

        int appliedRecommended = applicableTweaks.Count(t => t.IsLiveApplied);
        int totalRecommended = Math.Max(1, applicableTweaks.Count);

        // Base 75 points from recommended applicable tweaks for this exact build
        int tweakPoints = (int)Math.Round((double)appliedRecommended / totalRecommended * 75.0);

        // 15 points from Timer Resolution & DPC Kernel latency
        var (curTimerMs, _, _) = QueryTimerResolutionMs();
        int timerPoints = curTimerMs <= 0.55 ? 8 : (curTimerMs <= 1.05 ? 5 : 2);
        if (curTimerMs <= 0.55)
            positives.Add($"Timer Resolution: {curTimerMs:F3} ms (Precisión óptima 0.5ms)");
        else
            warnings.Add($"Timer Resolution actual: {curTimerMs:F3} ms (Activá GlobalTimerResolutionRequests + Timer 0.5ms)");

        var (dpcPct, _) = SampleCpuAndKernelLoad();
        int dpcPoints = dpcPct <= 3.5 ? 7 : (dpcPct <= 8.0 ? 5 : 2);
        if (dpcPct <= 3.5)
            positives.Add($"Kernel / DPC Time en vivo: {dpcPct:F1}% (Excelente, sin tormenta de interrupciones)");
        else
            warnings.Add($"Kernel / DPC Time elevado: {dpcPct:F1}% (Revisá procesos en segundo plano o Interrupt Moderation)");

        // 10 points from Hardware Configuration (Dual Channel + Clocking Mode coherence)
        int hwPoints = 0;
        if (hw.IsDualChannel)
        {
            hwPoints += 5;
            positives.Add($"Memoria RAM: {hw.RamTotalGb} GB en Dual Channel activo");
        }
        else
        {
            issues.Add("Memoria RAM: Posible Single Channel detectado (cuello de botella en 1% lows)");
        }

        hwPoints += 5;
        positives.Add($"Modo CPU Dinámico: {hw.ClockingModeDescription}");

        foreach (var t in applicableTweaks)
        {
            if (t.IsLiveApplied)
                positives.Add($"[OK] {t.CodeNumber} — {t.Title}");
            else
                issues.Add($"[Pendiente] {t.CodeNumber} — {t.Title}");
        }

        int finalScore = Math.Clamp(tweakPoints + timerPoints + dpcPoints + hwPoints, 0, 100);
        string grade = finalScore switch
        {
            >= 95 => "S+ NIVEL PRO ESL / LAN READY",
            >= 85 => "A+ FACEIT LEVEL 10 / RADIANT",
            >= 70 => "B COMPETITIVO OPTIMIZADO",
            >= 50 => "C BASE ESTÁNDAR (OPTIMIZABLE)",
            _ => "D REQUIERE OPTIMIZACIÓN KRUZH"
        };

        return (finalScore, grade, positives, warnings, issues);
    }
}

public static class Win32DialogService
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAMEW
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAMEW lpofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetSaveFileNameW(ref OPENFILENAMEW lpofn);

    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_OVERWRITEPROMPT = 0x00000002;
    private const int OFN_NOCHANGEDIR = 0x00000008;

    public static string? ShowOpenFileDialog(IntPtr ownerHwnd, string title, string filter, string? initialDir = null)
    {
        const int maxChars = 1024;
        IntPtr fileBuffer = Marshal.AllocHGlobal(maxChars * 2);
        try
        {
            Marshal.WriteInt16(fileBuffer, 0);
            var ofn = new OPENFILENAMEW
            {
                lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
                hwndOwner = ownerHwnd,
                lpstrFilter = filter,
                nFilterIndex = 1,
                lpstrFile = fileBuffer,
                nMaxFile = maxChars,
                lpstrInitialDir = initialDir ?? AppDomain.CurrentDomain.BaseDirectory,
                lpstrTitle = title,
                Flags = OFN_PATHMUSTEXIST | OFN_FILEMUSTEXIST | OFN_NOCHANGEDIR
            };

            if (GetOpenFileNameW(ref ofn))
            {
                return Marshal.PtrToStringUni(fileBuffer);
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(fileBuffer);
        }
    }

    public static string? ShowSaveFileDialog(IntPtr ownerHwnd, string title, string filter, string defaultFileName, string defaultExt = "txt")
    {
        const int maxChars = 1024;
        IntPtr fileBuffer = Marshal.AllocHGlobal(maxChars * 2);
        try
        {
            byte[] initBytes = Encoding.Unicode.GetBytes(defaultFileName + "\0");
            Marshal.Copy(initBytes, 0, fileBuffer, Math.Min(initBytes.Length, maxChars * 2));

            var ofn = new OPENFILENAMEW
            {
                lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
                hwndOwner = ownerHwnd,
                lpstrFilter = filter,
                nFilterIndex = 1,
                lpstrFile = fileBuffer,
                nMaxFile = maxChars,
                lpstrInitialDir = AppDomain.CurrentDomain.BaseDirectory,
                lpstrTitle = title,
                lpstrDefExt = defaultExt,
                Flags = OFN_PATHMUSTEXIST | OFN_OVERWRITEPROMPT | OFN_NOCHANGEDIR
            };

            if (GetSaveFileNameW(ref ofn))
            {
                return Marshal.PtrToStringUni(fileBuffer);
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(fileBuffer);
        }
    }
}

