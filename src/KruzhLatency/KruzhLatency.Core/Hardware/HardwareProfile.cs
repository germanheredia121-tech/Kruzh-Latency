namespace KruzhLatency.Core.Hardware;

public enum ChassisKind { Unknown, Desktop, Laptop }
public enum CpuVendor { Unknown, Intel, Amd }
public enum GpuVendor { Unknown, Nvidia, Amd, Intel }
public enum StorageKind { Unknown, Nvme, Ssd, Hdd }

/// <summary>Un procesador lógico con su ubicación real en la topología.</summary>
public sealed record LogicalProcessor(
    int Index,
    int PhysicalCoreIndex,
    bool IsEfficiencyCore,
    bool IsSmtSibling,
    int L3GroupIndex,
    /// <summary>Tamaño del L3 de su grupo en KB (0 = desconocido). En X3D distingue el CCD con V-Cache.</summary>
    int L3SizeKb = 0);

/// <summary>
/// Perfil de hardware detectado (lo llena Infra en T24). Inmutable: los gates de los tweaks son funciones puras de este objeto.
/// Los valores "Unknown"/null significan "no se pudo detectar" — los gates deben tratarlos de forma conservadora.
/// </summary>
public sealed record HardwareProfile
{
    // Sistema operativo
    public int WindowsBuild { get; init; }
    public bool IsWindows11 => WindowsBuild >= 22000;
    public bool IsWindows11_24H2OrLater => WindowsBuild >= 26100;

    // Chasis
    public ChassisKind Chassis { get; init; } = ChassisKind.Unknown;
    public bool IsLaptop => Chassis == ChassisKind.Laptop;

    // CPU
    public string CpuName { get; init; } = string.Empty;
    public CpuVendor CpuVendor { get; init; } = CpuVendor.Unknown;
    public IReadOnlyList<LogicalProcessor> Topology { get; init; } = Array.Empty<LogicalProcessor>();
    public int PhysicalCores => Topology.Select(t => t.PhysicalCoreIndex).Distinct().Count();
    public int LogicalProcessors => Topology.Count;
    public bool IsHybrid => Topology.Any(t => t.IsEfficiencyCore);
    public int L3Groups => Topology.Select(t => t.L3GroupIndex).Distinct().Count();
    public bool IsMultiCcd => CpuVendor == CpuVendor.Amd && L3Groups >= 2;
    public bool IsX3D { get; init; }

    // GPU (la discreta si existe)
    public string GpuName { get; init; } = string.Empty;
    public GpuVendor GpuVendor { get; init; } = GpuVendor.Unknown;
    public string? GpuInstanceId { get; init; }

    // Memoria y almacenamiento
    public int RamTotalGb { get; init; }
    /// <summary>null = no se pudo determinar (SMBIOS no disponible).</summary>
    public int? RamChannels { get; init; }
    public int? RamConfiguredSpeedMts { get; init; }
    public int? RamRatedSpeedMts { get; init; }
    public StorageKind SystemDrive { get; init; } = StorageKind.Unknown;

    // Monitor
    public int? MonitorCurrentHz { get; init; }
    public int? MonitorMaxHz { get; init; }

    // Firmware / seguridad (relevantes para anticheat)
    public bool? SecureBootEnabled { get; init; }
    public bool? TpmReady { get; init; }
    public bool? VbsRunning { get; init; }
}
