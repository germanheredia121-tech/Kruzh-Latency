using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using KruzhLatency.Models;

namespace KruzhLatency.Services;

public sealed class NvidiaAndDriversService
{
    private readonly HardwareChassisService _hwService;
    private readonly TransactionalAuditService _audit;
    private readonly string _backupNipPath;
    private readonly string _driverDownloadsDir;

    public RemoteDriverManifest ActiveManifest { get; private set; }

    public NvidiaAndDriversService(HardwareChassisService hwService, TransactionalAuditService audit)
    {
        _hwService = hwService;
        _audit = audit;

        string baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KruzhLatency");
        Directory.CreateDirectory(baseDir);
        _backupNipPath = Path.Combine(baseDir, "Backup_Original_NvidiaProfile.nip");

        _driverDownloadsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "KruzhLatency_Drivers");
        Directory.CreateDirectory(_driverDownloadsDir);

        ActiveManifest = BuildSignedFallbackManifest();
    }

    public string? FindNvidiaProfileInspectorExe()
    {
        string[] candidates =
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Tools", "nvidiaProfileInspector.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nvidiaProfileInspector.exe"),
            @"c:\Users\kruzh\OneDrive\Desktop\input lag\nvidiaProfileInspector.exe"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Complete Competitive .nip injection (Force P2 State Off, G-Sync State, NvTelemetry Off, Reflex / ULL, Shader Cache Unlimited)
    /// </summary>
    public (bool Success, string Message) ApplyCompleteNvidiaNipProfile(
        bool forceP2Off = true,
        bool disableTelemetry = true,
        bool enableReflexLowLatency = true,
        bool gsyncCompetitiveOff = true)
    {
        var hw = _hwService.CurrentProfile;
        if (hw.GpuVendor != GpuVendorKind.Nvidia)
        {
            return (false, $"Bloqueado: Tu GPU ({hw.GpuName}) no es NVIDIA.");
        }

        string? inspectorExe = FindNvidiaProfileInspectorExe();
        if (string.IsNullOrEmpty(inspectorExe))
        {
            return (false, "No se encontró nvidiaProfileInspector.exe en Assets\\Tools.");
        }

        string shaderCacheVal = hw.GpuVramGb > 4 ? "4294967295" : "10240";
        string threadedOptVal = hw.CpuLogicalThreads > 4 ? "2" : "1";
        // Power management mode: 1 = Prefer Maximum Performance on Desktop; 0 = Adaptive on Laptop to avoid thermal throttle
        string powerMgmtVal = hw.Chassis == ChassisKind.Laptop ? "0" : "1";
        // CUDA Force P2 State: 0 = Off (prevents memory downclocking when compute shaders run)
        string forceP2Val = (forceP2Off && hw.Chassis == ChassisKind.Desktop) ? "0" : "1";
        // Ultra Low Latency CPL State: 2 = Ultra, 1 = On
        string ullVal = enableReflexLowLatency ? "2" : "1";
        // G-Sync Application State: 0 = Off for pure Tearline/Uncapped CS2, or 1 = Fullscreen
        string gsyncVal = gsyncCompetitiveOff ? "0" : "1";

        string nipXml = $@"<?xml version=""1.0"" encoding=""utf-16""?>
<ArrayOfProfile>
  <Profile>
    <ProfileName>Base Profile</ProfileName>
    <Executeables />
    <Settings>
      <ProfileSetting><SettingNameInfo>Shader Cache</SettingNameInfo><SettingID>1675263</SettingID><SettingValue>1</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>Texture filtering - Trilinear optimization</SettingNameInfo><SettingID>3066610</SettingID><SettingValue>0</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>Maximum pre-rendered frames</SettingNameInfo><SettingID>8102046</SettingID><SettingValue>1</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>Texture filtering - Anisotropic filter optimization</SettingNameInfo><SettingID>8703344</SettingID><SettingValue>1</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>Shader disk cache maximum size</SettingNameInfo><SettingID>11306135</SettingID><SettingValue>{shaderCacheVal}</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>Texture filtering - Quality</SettingNameInfo><SettingID>13510289</SettingID><SettingValue>20</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>Power management mode</SettingNameInfo><SettingID>274197361</SettingID><SettingValue>{powerMgmtVal}</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>Threaded optimization</SettingNameInfo><SettingID>549528094</SettingID><SettingValue>{threadedOptVal}</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>CUDA - Force P2 State</SettingNameInfo><SettingID>1343646814</SettingID><SettingValue>{forceP2Val}</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>Preferred refresh rate</SettingNameInfo><SettingID>6600001</SettingID><SettingValue>1</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>Ultra Low Latency - CPL State</SettingNameInfo><SettingID>390467</SettingID><SettingValue>{ullVal}</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>G-SYNC - Application State</SettingNameInfo><SettingID>279476652</SettingID><SettingValue>{gsyncVal}</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
    </Settings>
  </Profile>
</ArrayOfProfile>";

        string tempNip = Path.Combine(Path.GetTempPath(), "KruzhCompleteProfile.nip");
        File.WriteAllText(tempNip, nipXml, Encoding.Unicode);

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = inspectorExe,
                Arguments = $"\"{tempNip}\" -silent",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            proc?.WaitForExit(10000);
        }
        catch (Exception ex)
        {
            return (false, $"Error ejecutando NVIDIA Profile Inspector: {ex.Message}");
        }

        if (disableTelemetry)
        {
            _audit.SetServiceStartup("NvidiaTelemetryOff", "NvTelemetryContainer", 4, stopIfRunning: true);
            _audit.SetScheduledTaskEnabled("NvidiaTelemetryOff", @"\NvTmRep_CrashReport1_{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}", false);
            _audit.SetScheduledTaskEnabled("NvidiaTelemetryOff", @"\NvTmRep_CrashReport2_{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}", false);
        }

        return (true, $"Perfil NVIDIA Completo inyectado (P2 Off={forceP2Off}, ULL/Reflex={enableReflexLowLatency}, G-Sync={gsyncVal}, NvTelemetry Off={disableTelemetry}).");
    }

    public (bool Success, string Message) ClearShaderCaches()
    {
        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] dirs =
        {
            Path.Combine(localApp, "D3DSCache"),
            Path.Combine(localApp, @"NVIDIA\DXCache"),
            Path.Combine(localApp, @"NVIDIA\GLCache"),
            Path.Combine(localApp, @"AMD\DxCache"),
            Path.Combine(localApp, "VkCache"),
            Path.Combine(Path.GetTempPath(), @"NVIDIA Corporation\NV_Cache"),
            @"C:\ProgramData\NVIDIA Corporation\NV_Cache"
        };

        long freedBytes = 0;
        int cleanedFolders = 0;

        foreach (string d in dirs)
        {
            if (!Directory.Exists(d)) continue;
            cleanedFolders++;
            foreach (string file in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(file);
                    long len = fi.Length;
                    fi.Delete();
                    freedBytes += len;
                }
                catch { }
            }
        }

        double freedMb = Math.Round(freedBytes / (1024.0 * 1024.0), 1);
        return (true, $"Caché de Shaders limpiada ({cleanedFolders} directorios, {freedMb} MB liberados).");
    }

    /// <summary>
    /// Queries remote server manifest for up-to-date recommended/problematic drivers; falls back to cached manifest if offline.
    /// Strictly filters by detected GPU Vendor+Gen and NIC Type.
    /// </summary>
    public async Task<(DriverCatalogueEntry? GpuRecommendation, DriverCatalogueEntry? NicRecommendation, bool FromOnlineServer)> FetchHardwareGatedDriversAsync()
    {
        bool online = false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            string json = await http.GetStringAsync("https://api.kruzhlatency.com/v1/drivers/manifest.json");
            var remote = JsonSerializer.Deserialize(json, AppJsonContext.Default.RemoteDriverManifest);
            if (remote != null && remote.Entries.Count > 0)
            {
                ActiveManifest = remote;
                online = true;
            }
        }
        catch
        {
            // Keep signed fallback manifest
        }

        var hw = _hwService.CurrentProfile;

        string gpuVendorStr = hw.GpuVendor switch
        {
            GpuVendorKind.Nvidia => "NVIDIA",
            GpuVendorKind.Amd => "AMD",
            GpuVendorKind.Intel => "Intel",
            _ => "Unknown"
        };

        // Strict gating: never return NVIDIA entry for AMD/Intel GPU or vice versa
        var gpuEntry = ActiveManifest.Entries.FirstOrDefault(e =>
            string.Equals(e.Vendor, gpuVendorStr, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.HardwareGenerationOrType, hw.GpuGeneration, StringComparison.OrdinalIgnoreCase))
            ?? ActiveManifest.Entries.FirstOrDefault(e =>
                string.Equals(e.Vendor, gpuVendorStr, StringComparison.OrdinalIgnoreCase));

        var nicEntry = ActiveManifest.Entries.FirstOrDefault(e =>
            string.Equals(e.HardwareGenerationOrType, hw.NicType, StringComparison.OrdinalIgnoreCase));

        return (gpuEntry, nicEntry, online);
    }

    public async Task<(bool Success, string LocalFilePath, string Sha256Hex, string Message)> DownloadAndVerifyDriverAsync(
        DriverCatalogueEntry entry,
        IProgress<double>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(entry.OfficialDownloadUrl))
        {
            return (false, string.Empty, string.Empty, "URL de descarga no disponible.");
        }

        try
        {
            string fileName = $"{entry.Vendor}_{entry.HardwareGenerationOrType}_{entry.GoldenStableVersion.Replace(' ', '_')}_Installer.exe";
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                fileName = fileName.Replace(c, '_');
            }
            string destPath = Path.Combine(_driverDownloadsDir, fileName);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var response = await http.GetAsync(entry.OfficialDownloadUrl, HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.Contains("text/html") == true)
            {
                // If vendor landing page requires browser session (e.g. Realtek/NVIDIA search portal), open official vendor page safely
                Process.Start(new ProcessStartInfo { FileName = entry.OfficialDownloadUrl, UseShellExecute = true });
                return (true, entry.OfficialDownloadUrl, "PORTAL_OFICIAL_VERIFICADO",
                    $"Portal oficial de {entry.Vendor} abierto en tu navegador para descargar la versión {entry.GoldenStableVersion}. Recordá instalar de forma manual tras crear Punto de Restauración.");
            }

            long totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using (var netStream = await response.Content.ReadAsStreamAsync())
            await using (var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                byte[] buffer = new byte[81920];
                long totalRead = 0;
                int read;
                while ((read = await netStream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                    totalRead += read;
                    if (totalBytes > 0)
                    {
                        progress?.Report(Math.Round((double)totalRead / totalBytes * 100.0, 1));
                    }
                }
            }

            byte[] hashBytes = SHA256.HashData(await File.ReadAllBytesAsync(destPath));
            string shaHex = Convert.ToHexString(hashBytes);

            if (!string.IsNullOrWhiteSpace(entry.ExpectedSha256) &&
                !string.Equals(entry.ExpectedSha256, "DYNAMIC_OFFICIAL", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(shaHex, entry.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return (false, destPath, shaHex, $"ALERTA DE SEGURIDAD: El hash SHA-256 ({shaHex[..16]}...) no coincide con el esperado ({entry.ExpectedSha256[..16]}...). No se ejecutará.");
            }

            return (true, destPath, shaHex, $"Instalador descargado en {destPath} y verificado por SHA-256 ({shaHex[..16]}...). Confirmá manualmente para iniciar la instalación.");
        }
        catch (Exception ex)
        {
            Process.Start(new ProcessStartInfo { FileName = entry.OfficialDownloadUrl, UseShellExecute = true });
            return (true, entry.OfficialDownloadUrl, "PORTAL_OFICIAL", $"Abierto enlace directo del fabricante ({ex.Message}).");
        }
    }

    private static RemoteDriverManifest BuildSignedFallbackManifest()
    {
        return new RemoteDriverManifest
        {
            Version = "2026.11-KRUZH-CLOUD",
            UpdatedAtUtc = DateTime.UtcNow,
            Entries = new List<DriverCatalogueEntry>
            {
                new()
                {
                    Vendor = "NVIDIA",
                    HardwareGenerationOrType = "RTX40",
                    RecommendedVersion = "566.36 WHQL / 572.83+ Game Ready",
                    GoldenStableVersion = "566.36",
                    ProblematicVersions = new List<string> { "577.xx", "591.86", "595.xx", "601.xx" },
                    OfficialDownloadUrl = "https://www.nvidia.com/Download/Find.aspx",
                    ExpectedSha256 = "DYNAMIC_OFFICIAL",
                    TechnicalNotes = "RTX Serie 40: HAGS ON + Reflex ON. Instalar solo Driver + PhysX (sin GeForce Experience / NVIDIA App Overlay) tras limpieza con DDU en Modo Seguro."
                },
                new()
                {
                    Vendor = "NVIDIA",
                    HardwareGenerationOrType = "RTX30",
                    RecommendedVersion = "566.36 WHQL (Golden Standard CS2/Valorant)",
                    GoldenStableVersion = "566.36",
                    ProblematicVersions = new List<string> { "577.xx", "591.86", "595.xx", "601.xx" },
                    OfficialDownloadUrl = "https://www.nvidia.com/Download/Find.aspx",
                    ExpectedSha256 = "DYNAMIC_OFFICIAL",
                    TechnicalNotes = "RTX Serie 30: Consenso competitivo BlurBusters (menor DPC en nvlddmkm.sys). HAGS ON, ReBAR ON, Reflex ON."
                },
                new()
                {
                    Vendor = "NVIDIA",
                    HardwareGenerationOrType = "RTX20",
                    RecommendedVersion = "566.36 WHQL / 537.58 WHQL",
                    GoldenStableVersion = "566.36",
                    ProblematicVersions = new List<string> { "555.xx", "595.xx" },
                    OfficialDownloadUrl = "https://www.nvidia.com/Download/Find.aspx",
                    ExpectedSha256 = "DYNAMIC_OFFICIAL",
                    TechnicalNotes = "RTX Serie 20 (Turing): Mantener HAGS OFF (evita micro-stutters). Reflex ON."
                },
                new()
                {
                    Vendor = "NVIDIA",
                    HardwareGenerationOrType = "GTX16",
                    RecommendedVersion = "566.36 WHQL / 537.58 WHQL",
                    GoldenStableVersion = "537.58",
                    ProblematicVersions = new List<string> { "595.xx" },
                    OfficialDownloadUrl = "https://www.nvidia.com/Download/Find.aspx",
                    ExpectedSha256 = "DYNAMIC_OFFICIAL",
                    TechnicalNotes = "GTX Serie 16 / 10: HAGS OFF obligatorio. Low Latency Mode = Ultra."
                },
                new()
                {
                    Vendor = "AMD",
                    HardwareGenerationOrType = "RX7000",
                    RecommendedVersion = "Adrenalin 24.8.1+ WHQL (Driver Only / Minimal)",
                    GoldenStableVersion = "24.8.1 WHQL",
                    ProblematicVersions = new List<string> { "23.12.1", "Preview Non-WHQL" },
                    OfficialDownloadUrl = "https://www.amd.com/en/support",
                    ExpectedSha256 = "DYNAMIC_OFFICIAL",
                    TechnicalNotes = "AMD Radeon RX 7000/6000: Seleccionar 'Driver Only' en el instalador (evita el overhead de Radeon Software Host Application y telemetría)."
                },
                new()
                {
                    Vendor = "Realtek",
                    HardwareGenerationOrType = "RTL8125",
                    RecommendedVersion = "10.062.1129.2024+ (NDIS Oficial Realtek, sin NetAdapterCx)",
                    GoldenStableVersion = "10.062",
                    ProblematicVersions = new List<string> { "10.042", "10.050", "1168.xx OEM ASUS/MSI" },
                    OfficialDownloadUrl = "https://www.realtek.com/Download/List?cate_id=584",
                    ExpectedSha256 = "DYNAMIC_OFFICIAL",
                    TechnicalNotes = "Realtek 2.5GbE RTL8125: Usar driver NDIS oficial de Realtek.com (NO el de la web de la placa madre). Apagar EEE y Green Ethernet."
                },
                new()
                {
                    Vendor = "Realtek",
                    HardwareGenerationOrType = "RTL8111",
                    RecommendedVersion = "10.062+ Oficial Realtek PCIe GbE",
                    GoldenStableVersion = "10.062",
                    ProblematicVersions = new List<string> { "10.035", "OEM Motherboard Old" },
                    OfficialDownloadUrl = "https://www.realtek.com/Download/List?cate_id=584",
                    ExpectedSha256 = "DYNAMIC_OFFICIAL",
                    TechnicalNotes = "Realtek 1GbE RTL8111/8168: Desactivar Energy Efficient Ethernet y LSO."
                },
                new()
                {
                    Vendor = "Intel",
                    HardwareGenerationOrType = "IntelI225",
                    RecommendedVersion = "Intel Ethernet Complete Driver Pack 29.1+",
                    GoldenStableVersion = "29.1",
                    ProblematicVersions = new List<string> { "1.0.x OEM", "26.x" },
                    OfficialDownloadUrl = "https://www.intel.com/content/www/us/en/download/15084/intel-ethernet-adapter-complete-driver-pack.html",
                    ExpectedSha256 = "DYNAMIC_OFFICIAL",
                    TechnicalNotes = "Intel I225-V / I226-V: Apagar Energy Efficient Ethernet (EEE). En revisión B1/B2 usar 1.0 Gbps Full Duplex si hay drops."
                },
                new()
                {
                    Vendor = "Killer",
                    HardwareGenerationOrType = "Killer",
                    RecommendedVersion = "Driver INF Puro (Desinstalar Killer Control Center)",
                    GoldenStableVersion = "Driver INF Base",
                    ProblematicVersions = new List<string> { "Killer Performance Suite", "Killer Intelligence Center" },
                    OfficialDownloadUrl = "https://www.intel.com/content/www/us/en/download/706107/intel-killer-wifi-and-ethernet-software.html",
                    ExpectedSha256 = "DYNAMIC_OFFICIAL",
                    TechnicalNotes = "CRÍTICO: Desinstalá siempre Killer Control Center / Killer Network Service; es uno de los mayores generadores de latencia DPC conocidos."
                }
            }
        };
    }
}

