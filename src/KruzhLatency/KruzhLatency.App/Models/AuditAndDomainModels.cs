using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KruzhLatency.Models;

public enum BackupTargetKind
{
    RegistryValue,
    ServiceStartMode,
    BcdEntry,
    PowerCfgValue,
    ScheduledTaskState,
    NetshTcpSetting,
    FsutilSetting,
    MsiInterruptSetting,
    IrqAffinitySetting,
    DefenderExclusion
}

public sealed class AtomicBackupRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TweakId { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public BackupTargetKind TargetKind { get; set; }
    public string TargetPath { get; set; } = string.Empty;
    public string ValueName { get; set; } = string.Empty;
    public bool PreviousValueExisted { get; set; }
    public string RegistryValueKindName { get; set; } = "DWord";
    public string? PreviousSerializedValue { get; set; }
    public string? AppliedSerializedValue { get; set; }
}

public sealed class BackupJournalDocument
{
    public string Product { get; set; } = "KRUZH LATENCY";
    public string Author { get; set; } = "KRUZH — author";
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Dictionary<string, List<AtomicBackupRecord>> TweakBackups { get; set; } = new();
    public List<AtomicBackupRecord> AuditHistory { get; set; } = new();
}

public sealed class ExportedProfileDocument
{
    public string Product { get; set; } = "KRUZH LATENCY";
    public string Author { get; set; } = "KRUZH — author";
    public string ProfileName { get; set; } = "Custom Competitive Profile";
    public PresetProfileKind BasePreset { get; set; } = PresetProfileKind.Competitivo;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public CpuClockingMode PreferredClockingMode { get; set; } = CpuClockingMode.DynamicBoostPbo;
    public int MousePollingRateHz { get; set; } = 1000;
    public Dictionary<string, bool> EnabledTweaks { get; set; } = new();
    public Dictionary<string, string> SelectedTweakOptions { get; set; } = new();
    public List<GameTargetItem> CustomGames { get; set; } = new();
}

public sealed class GameTargetItem
{
    public string Name { get; set; } = string.Empty;
    public string ShortName { get; set; } = string.Empty;
    public string ExecutableName { get; set; } = string.Empty;
    public string FullExePath { get; set; } = string.Empty;
    public string? CoverImagePath { get; set; }
    public string GradientStartHex { get; set; } = "#3A2E17";
    public string GradientEndHex { get; set; } = "#14120C";

    public bool HighCpuAndIoPriority { get; set; } = true;
    public bool QosDscp46 { get; set; } = true;
    public bool HighPerfGpuPreference { get; set; } = true;
    public bool DefenderFolderExclusion { get; set; }
    public bool DisableCfgExploitProtection { get; set; }

    [JsonIgnore]
    public bool IsInstalled => !string.IsNullOrEmpty(FullExePath) && System.IO.File.Exists(FullExePath);

    /// <summary>Estado REAL leído del sistema (IFEO / QoS / GPU pref) por GameOptimizationService, no lo que dicen los toggles.</summary>
    [JsonIgnore]
    public bool IsLiveOptimized { get; set; }

    [JsonIgnore]
    public bool IsOptimized => IsLiveOptimized;

    [JsonIgnore]
    public string StatusText => IsInstalled ? "Instalado" : "No instalado";

    [JsonIgnore]
    public bool HasCoverImage => !string.IsNullOrEmpty(CoverImagePath) && System.IO.File.Exists(CoverImagePath);

    [JsonIgnore]
    public Microsoft.UI.Xaml.Media.SolidColorBrush StatusDotBrush =>
        new(IsInstalled ? Windows.UI.Color.FromArgb(255, 99, 192, 136) : Windows.UI.Color.FromArgb(255, 74, 74, 74));

    [JsonIgnore]
    public Microsoft.UI.Xaml.Visibility OptimizedBadgeVisibility =>
        IsOptimized ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
}

public sealed class PciMsiDeviceItem
{
    public string FriendlyName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty; // GPU, USB xHCI, NVMe, NIC, Audio
    public string PnpInstanceId { get; set; } = string.Empty;
    public bool MsiSupported { get; set; }
    public int MaxMsiMessages { get; set; } = 1;
    public int CurrentMessageLimit { get; set; } = 1;
    public int DevicePriority { get; set; } = 2; // 1=Low, 2=Normal, 3=High
    public int AssignedCoreIndex { get; set; } = -1;
    public string AssignedCoreLabel { get; set; } = "OS Auto";
    public bool IsSafeForMsi { get; set; } = true;
    public string SafetyNote { get; set; } = string.Empty;
    [JsonIgnore]
    public string MsiStatusText => MsiSupported ? "MSI: ACTIVO" : "MSI: LÍNEA";
    [JsonIgnore]
    public string PriorityText => DevicePriority == 3 ? "Prioridad: Alta (3)" : (DevicePriority == 2 ? "Prioridad: Normal (2)" : $"Prioridad: {DevicePriority}");
}

public sealed class NvramCheckItem
{
    public string QuestionName { get; set; } = string.Empty;
    public string Impact { get; set; } = "ALTO"; // EXTREMO, CRITICO, ALTO, MEDIO, BAJO
    public string CurrentValue { get; set; } = "No encontrado";
    public string RecommendedValue { get; set; } = string.Empty;
    public List<string> AvailableOptions { get; set; } = new();
    public List<string> AvoidValues { get; set; } = new();
    public string TechnicalReason { get; set; } = string.Empty;
    public bool FoundInFile { get; set; }
    public bool NeedsChange { get; set; }
    public bool IsAnticheatSensitive { get; set; }
    [JsonIgnore]
    public string StatusBadge => NeedsChange ? "CAMBIAR" : "ÓPTIMO";

    // El catálogo usa "CRÍTICO" (con tilde); se aceptan ambas grafías.
    private bool IsSevereImpact => Impact is "EXTREMO" or "CRITICO" or "CRÍTICO";

    [JsonIgnore]
    public Microsoft.UI.Xaml.Media.SolidColorBrush ImpactBadgeBorderBrush =>
        IsSevereImpact
            ? new(Windows.UI.Color.FromArgb(90, 184, 95, 95))
            : (Impact is "ALTO" or "MEDIO"
                ? new(Windows.UI.Color.FromArgb(90, 184, 151, 95))
                : new(Windows.UI.Color.FromArgb(255, 36, 36, 36)));

    [JsonIgnore]
    public Microsoft.UI.Xaml.Media.SolidColorBrush ImpactBadgeForeground =>
        IsSevereImpact
            ? new(Windows.UI.Color.FromArgb(255, 184, 95, 95))
            : (Impact is "ALTO" or "MEDIO"
                ? new(Windows.UI.Color.FromArgb(255, 184, 151, 95))
                : new(Windows.UI.Color.FromArgb(255, 154, 154, 154)));

    /// <summary>Resultado de solo lectura: la app recomienda, nunca modifica la BIOS.</summary>
    [JsonIgnore]
    public string NvramStatusText => !NeedsChange
        ? "Bien ✓"
        : (IsAnticheatSensitive ? "Revisar (anticheat)" : "Cambiar");
}

public sealed class DriverCatalogueEntry
{
    public string Vendor { get; set; } = string.Empty; // NVIDIA, AMD, Realtek, Intel, Killer, Aquantia
    public string HardwareGenerationOrType { get; set; } = string.Empty; // RTX40, RTX30, RTL8125, etc.
    public string RecommendedVersion { get; set; } = string.Empty;
    public string GoldenStableVersion { get; set; } = string.Empty;
    public List<string> ProblematicVersions { get; set; } = new();
    public string OfficialDownloadUrl { get; set; } = string.Empty;
    public string ExpectedSha256 { get; set; } = string.Empty;
    public string TechnicalNotes { get; set; } = string.Empty;
}

public sealed class RemoteDriverManifest
{
    public string Version { get; set; } = "2026.11.1";
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<DriverCatalogueEntry> Entries { get; set; } = new();
}

public sealed class SignedLicenseToken
{
    public string LicenseId { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string Edition { get; set; } = "PRO COMMERCIAL";
    public string HwidHash { get; set; } = string.Empty;
    public DateTime IssuedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public string FeatureSalt { get; set; } = string.Empty;
    public string SignatureBase64 { get; set; } = string.Empty;
}

public sealed class EulaConsentState
{
    public bool Accepted { get; set; }
    public DateTime AcceptedAtUtc { get; set; }
    public string EulaVersion { get; set; } = "3.0-COMMERCIAL";
    public string MachineHwid { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BackupJournalDocument))]
[JsonSerializable(typeof(AtomicBackupRecord))]
[JsonSerializable(typeof(ExportedProfileDocument))]
[JsonSerializable(typeof(GameTargetItem))]
[JsonSerializable(typeof(RemoteDriverManifest))]
[JsonSerializable(typeof(DriverCatalogueEntry))]
[JsonSerializable(typeof(SignedLicenseToken))]
[JsonSerializable(typeof(EulaConsentState))]
[JsonSerializable(typeof(Dictionary<string, bool>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class AppJsonContext : JsonSerializerContext
{
}
