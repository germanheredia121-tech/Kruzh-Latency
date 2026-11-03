using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using KruzhLatency.Models;

namespace KruzhLatency.Services;

public enum DynamicPowerPlanKind
{
    DesktopAggressiveAllCore,
    RyzenBalancedPboTuned,
    LaptopThermalSafe
}

public sealed class PowerPlanDescriptor
{
    public DynamicPowerPlanKind Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string TechnicalDetails { get; set; } = string.Empty;
    public EvaluationResult Evaluation { get; set; } = EvaluationResult.Neutral("");
    public string EvaluationReason => Evaluation.Reason;
    public bool IsRecommended => Evaluation.Level == RecommendationLevel.Recommended;
    public bool IsNotRecommended => Evaluation.Level == RecommendationLevel.NotRecommended;
}

public sealed class PowerCatalogService
{
    private readonly HardwareChassisService _hwService;

    public PowerCatalogService(HardwareChassisService hwService)
    {
        _hwService = hwService;
    }

    public List<PowerPlanDescriptor> GetDynamicPlanEvaluations()
    {
        var hw = _hwService.CurrentProfile;
        var list = new List<PowerPlanDescriptor>();

        // 1. Desktop-Agresivo (All-Core OC / Max Fixed)
        EvaluationResult evalDesktopAggr;
        if (hw.Chassis == ChassisKind.Laptop)
        {
            evalDesktopAggr = EvaluationResult.NotRecommended(
                "CONTRAPRODUCENTE EN LAPTOP: Apaga C-States y fija 100% de núcleos activos sin descanso, causando Thermal Throttling severo en portátiles.");
        }
        else if (hw.CpuVendor == CpuVendorKind.AMD && hw.ClockingMode == CpuClockingMode.StaticAllCoreOverclock)
        {
            evalDesktopAggr = EvaluationResult.Recommended(
                $"ÓPTIMO PARA TU ALL-CORE OVERCLOCK ({hw.ClockingModeDescription}): Al no usar PBO ni Curve Optimizer y tener frecuencia fija, este plan elimina toda latencia de estados Idle/C-States y mantiene el 100% de respuesta inmediata.");
        }
        else if (hw.CpuVendor == CpuVendorKind.Intel)
        {
            evalDesktopAggr = EvaluationResult.Recommended(
                "ÓPTIMO PARA INTEL DESKTOP: Mantiene máxima frecuencia constante, Core Parking OFF, ASPM OFF y latencia de despertar cero.");
        }
        else
        {
            evalDesktopAggr = EvaluationResult.Neutral(
                "Alternativa de latencia cero para Desktop. Si usás PBO + Curve Optimizer dinámico, compará temperaturas y boost frente a Ryzen Balanced Tuned.");
        }

        list.Add(new PowerPlanDescriptor
        {
            Kind = DynamicPowerPlanKind.DesktopAggressiveAllCore,
            Title = "KRUZH Desktop — Agresivo (All-Core OC / Zero-Idle)",
            Subtitle = "Ideal para All-Core Overclock fijo (ej. 4.70 GHz sin PBO) e Intel Desktop",
            TechnicalDetails = "Min/Max CPU: 100% | C-States (IDLEDISABLE): OFF | Core Parking: 100% | PCIe ASPM: OFF | USB Selective Suspend: OFF | PerfCheck: 10ms",
            Evaluation = evalDesktopAggr
        });

        // 2. Ryzen-Balanced-Tuned (Solo para PBO + Curve Optimizer)
        EvaluationResult evalRyzenPbo;
        if (hw.Chassis == ChassisKind.Laptop)
        {
            evalRyzenPbo = EvaluationResult.NotRecommended("Diseñado para procesadores Ryzen de escritorio con PBO; en Laptop usá KRUZH Laptop — Seguro.");
        }
        else if (hw.CpuVendor != CpuVendorKind.AMD)
        {
            evalRyzenPbo = EvaluationResult.NotRecommended("Tu procesador es Intel; este perfil es exclusivo para el algoritmo PBO/CPPC de AMD Ryzen.");
        }
        else if (hw.ClockingMode == CpuClockingMode.StaticAllCoreOverclock)
        {
            evalRyzenPbo = EvaluationResult.NotRecommended(
                "CONTRAPRODUCENTE EN TU PC: Tenés un All-Core Overclock fijo (sin PBO ni Curve Optimizer). Un plan Balanced intenta escalar estados de voltaje/frecuencia que no aplican a un multiplicador fijo y añade jitter. Usá 'KRUZH Desktop — Agresivo'.");
        }
        else
        {
            evalRyzenPbo = EvaluationResult.Recommended(
                "ÓPTIMO PARA RYZEN CON PBO + CURVE OPTIMIZER: Permite que el SMU de AMD impulse los núcleos preferidos al boost máximo de 1-2 hilos sin ahogar el margen térmico, desactivando ASPM y suspensión USB.");
        }

        list.Add(new PowerPlanDescriptor
        {
            Kind = DynamicPowerPlanKind.RyzenBalancedPboTuned,
            Title = "KRUZH Ryzen — Balanced Tuned (Solo PBO + Curve Optimizer)",
            Subtitle = "Específico para Ryzen Zen2/Zen3/Zen4 con PBO + Curve Optimizer activos",
            TechnicalDetails = "Min CPU: 85% / Max: 100% | C-States: C1/C6 SMU Managed | Boost Policy: 100% | Autonomous Mode: Enabled | PCIe ASPM: OFF | USB Suspend: OFF",
            Evaluation = evalRyzenPbo
        });

        // 3. Laptop-Seguro (Thermal-Safe)
        EvaluationResult evalLaptop;
        if (hw.Chassis == ChassisKind.Laptop)
        {
            evalLaptop = EvaluationResult.Recommended(
                "ÓPTIMO PARA TU LAPTOP: Elimina ahorro de energía en USB y placa de red pero conserva la gestión térmica de C-States y Dynamic Boost para evitar caídas de FPS por temperatura.");
        }
        else
        {
            evalLaptop = EvaluationResult.NotRecommended(
                "Tu equipo es una PC de Escritorio (Desktop); este plan conserva límites térmicos de portátil que no necesitás.");
        }

        list.Add(new PowerPlanDescriptor
        {
            Kind = DynamicPowerPlanKind.LaptopThermalSafe,
            Title = "KRUZH Laptop — Pro Mobile (Thermal-Safe)",
            Subtitle = "Optimizado para portátiles gaming (evita Thermal Throttling en CPU/GPU)",
            TechnicalDetails = "C-States: ON (Evita sobrecalentamiento) | Boost Mode: Efficient Aggressive | Core Parking: 50% | USB Selective Suspend AC: OFF | Wireless Power Saving: OFF",
            Evaluation = evalLaptop
        });

        return list;
    }

    public (bool Success, string Message) ApplyDynamicPowerPlan(DynamicPowerPlanKind kind)
    {
        var hw = _hwService.CurrentProfile;
        string planName = kind switch
        {
            DynamicPowerPlanKind.DesktopAggressiveAllCore => "KRUZH LATENCY — Desktop Agresivo (All-Core / Max)",
            DynamicPowerPlanKind.RyzenBalancedPboTuned => "KRUZH LATENCY — Ryzen PBO/CO Tuned",
            DynamicPowerPlanKind.LaptopThermalSafe => "KRUZH LATENCY — Laptop Thermal-Safe",
            _ => "KRUZH LATENCY — Dynamic Plan"
        };

        string baseGuid = kind == DynamicPowerPlanKind.DesktopAggressiveAllCore
            ? "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" // High Performance base
            : "381b4222-f694-41f0-9685-ff5bb260df2e"; // Balanced base

        // Delete previous KRUZH dynamic plans to avoid clutter
        string listOut = TransactionalAuditService.RunHiddenProcess("powercfg.exe", "/list");
        foreach (string line in listOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains("KRUZH LATENCY", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Input Lag Eliminator", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(line, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
                if (m.Success)
                {
                    TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"/setactive {baseGuid}");
                    TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"/delete {m.Groups[1].Value}");
                }
            }
        }

        string dupOut = TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"/duplicatescheme {baseGuid}");
        var guidMatch = Regex.Match(dupOut, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
        string targetGuid = guidMatch.Success ? guidMatch.Groups[1].Value : "scheme_current";

        TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"/changename {targetGuid} \"{planName}\" \"Generado dinamicamente por KRUZH LATENCY (KRUZH - author)\"");

        // Common competitive peripheral settings (USB Selective Suspend OFF, Disk Idle 0, PCIe ASPM OFF)
        // SUB_DISK = 0012ee47-9041-4b5d-9b77-535fba8b1442, DISKIDLE = 6738e2c4-e8a5-4a42-b16a-e040e769756e
        TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"/setacvalueindex {targetGuid} 0012ee47-9041-4b5d-9b77-535fba8b1442 6738e2c4-e8a5-4a42-b16a-e040e769756e 0");
        // SUB_USB = 2a737441-1930-4402-8d77-b2bebba308a3, USBSELECTIVESUSPEND = 48e6b7a6-50f5-4782-a5d4-53bb8f07e226
        TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"/setacvalueindex {targetGuid} 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0");
        // SUB_PCIEXPRESS = 501a4d13-42af-4429-9fd1-a8218c268e20, ASPM = ee12f906-d2d4-4ffa-ac25-a54e11278c20
        TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"/setacvalueindex {targetGuid} 501a4d13-42af-4429-9fd1-a8218c268e20 ee12f906-d2d4-4ffa-ac25-a54e11278c20 0");

        // SUB_PROCESSOR = 54533251-82be-4824-96c1-47b60b740d00
        const string subProc = "54533251-82be-4824-96c1-47b60b740d00";
        const string idleDisable = "5d76a2ca-e8c0-402f-a133-2158492d58ad";
        const string cpMinCores = "0cc5b647-c1df-4637-891a-dec35c318583";
        const string procMin = "893dee8e-2bef-41e0-89c6-b55d0929964c";
        const string procMax = "bc5038f7-23e0-4960-96da-33abaf5935ec";
        const string boostMode = "be337238-0d82-4146-a960-4f3749d470c7";

        switch (kind)
        {
            case DynamicPowerPlanKind.DesktopAggressiveAllCore:
                if (!hw.IsIntelHybrid)
                {
                    TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"/setacvalueindex {targetGuid} {subProc} {idleDisable} 1");
                    TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"/setacvalueindex {targetGuid} {subProc} {cpMinCores} 100");
                }
                TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"/setacvalueindex {targetGuid} {subProc} {procMin} 100");
                TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"/setacvalueindex {targetGuid} {subProc} {procMax} 100");
                TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"/setacvalueindex {targetGuid} {subProc} {boostMode} 2");
                break;

            case DynamicPowerPlanKind.RyzenBalancedPboTuned:
                // C-States activos (0) para que PBO + Curve Optimizer lleguen al pico en un núcleo; mínimo 100% (spec 002 / B7): el boost sigue por encima de la base
                TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"/setacvalueindex {targetGuid} {subProc} {idleDisable} 0");
                TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"/setacvalueindex {targetGuid} {subProc} {cpMinCores} 100");
                TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"/setacvalueindex {targetGuid} {subProc} {procMin} 100");
                TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"/setacvalueindex {targetGuid} {subProc} {procMax} 100");
                TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"/setacvalueindex {targetGuid} {subProc} {boostMode} 2");
                break;

            case DynamicPowerPlanKind.LaptopThermalSafe:
                // Keep C-States enabled (0), Efficient Aggressive boost (4), and safe thermal floor
                TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"/setacvalueindex {targetGuid} {subProc} {idleDisable} 0");
                TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"/setacvalueindex {targetGuid} {subProc} {cpMinCores} 50");
                TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"/setacvalueindex {targetGuid} {subProc} {procMin} 20");
                TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"/setacvalueindex {targetGuid} {subProc} {procMax} 100");
                TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"/setacvalueindex {targetGuid} {subProc} {boostMode} 4");
                break;
        }

        TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"/setactive {targetGuid}");
        return (true, $"Plan dinámico '{planName}' generado y activado con éxito.");
    }

    public (bool Success, string Message) ImportCustomPowFile(string powFilePath)
    {
        if (string.IsNullOrWhiteSpace(powFilePath) || !File.Exists(powFilePath))
        {
            return (false, "El archivo .pow seleccionado no existe.");
        }

        string newGuid = Guid.NewGuid().ToString();
        string outText = TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"-import \"{powFilePath}\" {newGuid}");
        TransactionalAuditService.RunHiddenProcess("powercfg.exe", $"/setactive {newGuid}");
        return (true, $"Plan personalizado '{Path.GetFileName(powFilePath)}' importado y activado (GUID: {newGuid}).");
    }

    public string GetActivePowerSchemeSummary()
    {
        string outText = TransactionalAuditService.RunHiddenProcess("powercfg.exe", "/getactivescheme");
        return string.IsNullOrWhiteSpace(outText) ? "Balanced / Desconocido" : outText.Trim();
    }
}
