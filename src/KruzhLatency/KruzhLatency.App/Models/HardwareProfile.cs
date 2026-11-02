using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KruzhLatency.Models;

public enum ChassisKind
{
    Desktop,
    Laptop
}

public enum CpuVendorKind
{
    Intel,
    AMD,
    Unknown
}

public enum CpuClockingMode
{
    DynamicBoostPbo,       // PBO + Curve Optimizer / Intel Turbo Boost dinámico
    StaticAllCoreOverclock // Multiplicador fijo / All-Core OC (ej. 4.70 GHz fijo sin PBO)
}

public enum GpuVendorKind
{
    Nvidia,
    Amd,
    Intel,
    Unknown
}

public enum StorageKind
{
    NvmeOrSsd,
    MechanicalHdd
}

public sealed class LogicalProcessorNode
{
    public int LogicalIndex { get; set; }
    public int PhysicalCoreIndex { get; set; }
    public bool IsEfficiencyCore { get; set; }
    public bool IsSmtSecondaryThread { get; set; }
    public int L3CacheGroupIndex { get; set; }
}

public sealed class HardwareProfile
{
    // CPU
    public string CpuName { get; set; } = "Desconocido";
    public CpuVendorKind CpuVendor { get; set; } = CpuVendorKind.Unknown;
    public int CpuPhysicalCores { get; set; } = 4;
    public int CpuLogicalThreads { get; set; } = 8;
    public int CpuMaxClockMHz { get; set; } = 3600;
    public int CpuCurrentClockMHz { get; set; } = 3600;
    public bool IsIntelHybrid { get; set; }
    public bool IsRyzenZen2OrZen3OrZen4 { get; set; }
    public bool IsDualCcdRyzen { get; set; }
    public bool IsRyzenX3D { get; set; }
    public CpuClockingMode ClockingMode { get; set; } = CpuClockingMode.DynamicBoostPbo;
    public string ClockingModeDescription { get; set; } = string.Empty;
    public List<LogicalProcessorNode> Topology { get; set; } = new();
    public ulong ReservedBackgroundCpuMask { get; set; }

    // Chassis
    public ChassisKind Chassis { get; set; } = ChassisKind.Desktop;
    public string ChassisDetectionReason { get; set; } = "Desktop (PC de Escritorio)";
    public bool HasBattery { get; set; }

    // GPU
    public string GpuName { get; set; } = "Desconocido";
    public GpuVendorKind GpuVendor { get; set; } = GpuVendorKind.Unknown;
    public string GpuGeneration { get; set; } = "Unknown";
    public int GpuVramGb { get; set; } = 8;
    public string GpuDriverVersion { get; set; } = "N/A";
    public string GpuDriverDate { get; set; } = "N/A";
    public string GpuPnpInstanceId { get; set; } = string.Empty;
    public bool SupportsReBar => GpuGeneration is "RTX30" or "RTX40" or "RTX50" or "RX6000" or "RX7000";
    public bool BenefitsFromHags => GpuGeneration is "RTX30" or "RTX40" or "RTX50";

    // RAM & Storage
    public int RamTotalGb { get; set; } = 16;
    public int RamSpeedMHz { get; set; } = 3200;
    public int RamModulesCount { get; set; } = 2;
    public bool IsDualChannel => RamModulesCount >= 2;
    public StorageKind Storage { get; set; } = StorageKind.NvmeOrSsd;

    // Network (NIC)
    public string NicName { get; set; } = "Desconocido";
    public string NicType { get; set; } = "Generic";
    public string NicDriverVersion { get; set; } = "N/A";
    public string NicDriverDate { get; set; } = "N/A";
    public string NicDriverProvider { get; set; } = "N/A";
    public string NicPnpInstanceId { get; set; } = string.Empty;

    // OS, BIOS & Peripherals
    public string OsCaption { get; set; } = "Microsoft Windows";
    public int WindowsBuild { get; set; } = 22631;
    public bool IsWindows11 => WindowsBuild >= 22000;
    public bool IsWindows11_22H2Plus => WindowsBuild >= 22621;
    public bool IsWindows11_24H2Plus => WindowsBuild >= 26100;
    public int MonitorRefreshHz { get; set; } = 144;
    public int MousePollingRateHz { get; set; } = 1000;

    // BIOS & Security State
    public string BiosVendor { get; set; } = "Unknown";
    public string BiosVersion { get; set; } = "Unknown";
    public string BaseBoardModel { get; set; } = "Unknown";
    public bool IsAmiAptioBios { get; set; }
    public bool IsSecureBootEnabled { get; set; } = true;
    public bool IsTpmEnabled { get; set; } = true;
    public bool IsVbsEnabled { get; set; }

    [JsonIgnore]
    public string Motherboard => BaseBoardModel;
    [JsonIgnore]
    public string Architecture => "x64";
    [JsonIgnore]
    public bool SecureBootAndTpmPresent => IsSecureBootEnabled && IsTpmEnabled;
}

