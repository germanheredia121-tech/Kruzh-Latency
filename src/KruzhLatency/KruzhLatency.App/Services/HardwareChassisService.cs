using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using KruzhLatency.Models;
using Microsoft.Win32;

namespace KruzhLatency.Services;

public sealed partial class HardwareChassisService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

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
    private static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [LibraryImport("powrprof.dll")]
    private static partial int PowerDeterminePlatformRoleEx(uint version);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformationEx(
        int relationshipType,
        IntPtr buffer,
        ref uint returnedLength);

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    public HardwareProfile CurrentProfile { get; private set; } = new();

    public HardwareProfile DetectHardwareProfile()
    {
        var hw = new HardwareProfile();
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        // 1. OS Build & Caption
        using (var ntCur = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
        {
            string productName = ntCur?.GetValue("ProductName")?.ToString() ?? "Microsoft Windows";
            string buildStr = ntCur?.GetValue("CurrentBuild")?.ToString() ?? Environment.OSVersion.Version.Build.ToString();
            int.TryParse(buildStr, out int buildNum);
            if (buildNum >= 22000 && productName.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
            {
                productName = productName.Replace("Windows 10", "Windows 11");
            }
            hw.WindowsBuild = buildNum > 0 ? buildNum : Environment.OSVersion.Version.Build;
            hw.OsCaption = $"{productName} (Build {hw.WindowsBuild})";
        }

        // 2. Chassis Detection (3-way Native: PlatformRole + SystemPowerStatus + SMBIOS/Battery)
        DetectChassis(hw, hklm);

        // 3. CPU + Real Topology via GetLogicalProcessorInformationEx
        DetectCpuAndTopology(hw, hklm);

        // 4. BIOS & Security (AMI Aptio, Secure Boot, VBS)
        DetectBiosAndSecurity(hw, hklm);

        // 5. Detect CPU Clocking Mode (Static All-Core Overclock vs Dynamic Boost/PBO)
        DetectCpuClockingMode(hw, hklm);

        // 6. GPU (Vendor, Generation, Driver, VRAM, PCI Instance)
        DetectGpu(hw, hklm);

        // 7. RAM & Storage
        DetectMemoryAndStorage(hw, hklm);

        // 8. Network Adapter (NIC Type, Driver, Instance)
        DetectNic(hw, hklm);

        // 9. Monitor Refresh Rate
        DetectMonitorHz(hw);

        // 10. Saved user overrides (e.g. Mouse Polling Rate or explicit Clocking Mode override)
        using (var stateKey = Registry.CurrentUser.OpenSubKey(@"Software\KruzhLatency"))
        {
            if (stateKey != null)
            {
                if (stateKey.GetValue("MousePollingRateHz") is int savedHz && savedHz >= 125)
                {
                    hw.MousePollingRateHz = savedHz;
                }
                if (stateKey.GetValue("ClockingModeOverride") is int modeOverride &&
                    Enum.IsDefined(typeof(CpuClockingMode), modeOverride))
                {
                    hw.ClockingMode = (CpuClockingMode)modeOverride;
                    hw.ClockingModeDescription = hw.ClockingMode == CpuClockingMode.StaticAllCoreOverclock
                        ? "All-Core Overclock Fijo (Multiplicador Estático — PBO/Balanced Descartado)"
                        : "Boost Dinámico (PBO + Curve Optimizer / Intel Turbo)";
                }
            }
        }

        CurrentProfile = hw;
        return hw;
    }

    public void SetClockingModeOverride(CpuClockingMode mode)
    {
        CurrentProfile.ClockingMode = mode;
        CurrentProfile.ClockingModeDescription = mode == CpuClockingMode.StaticAllCoreOverclock
            ? "All-Core Overclock Fijo (Multiplicador Estático — PBO/Balanced Descartado)"
            : "Boost Dinámico (PBO + Curve Optimizer / Intel Turbo)";

        using var key = Registry.CurrentUser.CreateSubKey(@"Software\KruzhLatency", writable: true);
        if (key != null)
        {
            TransactionalAuditService.SetValueTyped(key, "ClockingModeOverride", (int)mode, RegistryValueKind.DWord);
        }
    }

    public void SetMousePollingRate(int pollingHz)
    {
        CurrentProfile.MousePollingRateHz = pollingHz;
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\KruzhLatency", writable: true);
        if (key != null)
        {
            TransactionalAuditService.SetValueTyped(key, "MousePollingRateHz", pollingHz, RegistryValueKind.DWord);
        }
    }

    private static void DetectChassis(HardwareProfile hw, RegistryKey hklm)
    {
        bool hasBattery = false;
        if (GetSystemPowerStatus(out var sps))
        {
            // BatteryFlag: 128 = No system battery, 255 = Unknown
            hasBattery = sps.BatteryFlag != 128 && sps.BatteryFlag != 255;
        }

        int platformRole = 1;
        try
        {
            // POWER_PLATFORM_ROLE_V2 = 2; returns 2 for Mobile/Laptop
            platformRole = PowerDeterminePlatformRoleEx(2);
        }
        catch { }

        bool isMobileRole = platformRole == 2 || platformRole == 8; // RoleMobile or RoleSlate
        hw.HasBattery = hasBattery;

        if (hasBattery || isMobileRole)
        {
            hw.Chassis = ChassisKind.Laptop;
            hw.ChassisDetectionReason = $"Laptop / Portátil detectado (Batería: {(hasBattery ? "Sí" : "No")}, Rol: Mobile)";
        }
        else
        {
            hw.Chassis = ChassisKind.Desktop;
            hw.ChassisDetectionReason = "Desktop / PC de Escritorio (Sin limitaciones de batería)";
        }
    }

    private static unsafe void DetectCpuAndTopology(HardwareProfile hw, RegistryKey hklm)
    {
        using (var cpu0 = hklm.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
        {
            hw.CpuName = (cpu0?.GetValue("ProcessorNameString")?.ToString() ?? "Unknown CPU").Trim();
            string vendorId = cpu0?.GetValue("VendorIdentifier")?.ToString() ?? "";
            if (vendorId.Contains("AMD", StringComparison.OrdinalIgnoreCase) || hw.CpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase) || hw.CpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase))
            {
                hw.CpuVendor = CpuVendorKind.AMD;
            }
            else if (vendorId.Contains("Intel", StringComparison.OrdinalIgnoreCase) || hw.CpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            {
                hw.CpuVendor = CpuVendorKind.Intel;
            }

            if (cpu0?.GetValue("~MHz") is int mhz && mhz > 0)
            {
                hw.CpuMaxClockMHz = mhz;
                hw.CpuCurrentClockMHz = mhz;
            }
        }

        hw.CpuLogicalThreads = Environment.ProcessorCount;
        hw.IsRyzenX3D = hw.CpuName.Contains("X3D", StringComparison.OrdinalIgnoreCase);
        hw.IsRyzenZen2OrZen3OrZen4 = hw.CpuVendor == CpuVendorKind.AMD &&
                                     Regex.IsMatch(hw.CpuName, @"Ryzen\s+[3579]\s+[3579]\d{3}", RegexOptions.IgnoreCase);

        // Topología real (RelationProcessorCore = 0, RelationCache = 2). El parseo vive en Core
        // (CpuTopologyParser, con tests sobre buffers reales); acá solo se piden los bytes crudos.
        IReadOnlyList<KruzhLatency.Core.Hardware.LogicalProcessor> topology = Array.Empty<KruzhLatency.Core.Hardware.LogicalProcessor>();
        try
        {
            topology = KruzhLatency.Core.Hardware.CpuTopologyParser.Parse(
                ReadProcessorInformationBuffer(0), ReadProcessorInformationBuffer(2));
        }
        catch { }

        if (topology.Count > 0)
        {
            hw.CpuPhysicalCores = topology.Select(t => t.PhysicalCoreIndex).Distinct().Count();
            bool hasHybridEfficiency = topology.Any(t => t.IsEfficiencyCore);
            int l3Groups = topology.Select(t => t.L3GroupIndex).Distinct().Count();

            hw.IsIntelHybrid = hw.CpuVendor == CpuVendorKind.Intel &&
                               (hasHybridEfficiency || Regex.IsMatch(hw.CpuName, @"i[579]-1[234]\d{3}|Core\s+Ultra", RegexOptions.IgnoreCase));

            hw.IsDualCcdRyzen = hw.CpuVendor == CpuVendorKind.AMD &&
                                (l3Groups >= 2 || Regex.IsMatch(hw.CpuName, @"(3900|3950|5900|5950|7900|7950|9900|9950)", RegexOptions.IgnoreCase));

            foreach (var t in topology)
            {
                hw.Topology.Add(new LogicalProcessorNode
                {
                    LogicalIndex = t.Index,
                    PhysicalCoreIndex = t.PhysicalCoreIndex,
                    IsEfficiencyCore = t.IsEfficiencyCore,
                    IsSmtSecondaryThread = t.IsSmtSibling,
                    L3CacheGroupIndex = t.L3GroupIndex
                });
            }

            hw.ReservedBackgroundCpuMask = KruzhLatency.Core.Hardware.CpuTopologyParser.BackgroundReserveMask(topology);
        }
        else
        {
            hw.CpuPhysicalCores = Math.Max(2, hw.CpuLogicalThreads / 2);
        }
    }

    /// <summary>Devuelve el buffer crudo de GetLogicalProcessorInformationEx para la relación pedida (vacío si falla).</summary>
    private static byte[] ReadProcessorInformationBuffer(int relationship)
    {
        uint len = 0;
        GetLogicalProcessorInformationEx(relationship, IntPtr.Zero, ref len);
        if (len == 0) return Array.Empty<byte>();

        IntPtr buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformationEx(relationship, buf, ref len)) return Array.Empty<byte>();
            var bytes = new byte[len];
            Marshal.Copy(buf, bytes, 0, (int)len);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static void DetectBiosAndSecurity(HardwareProfile hw, RegistryKey hklm)
    {
        using (var bios = hklm.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
        {
            hw.BiosVendor = bios?.GetValue("BIOSVendor")?.ToString() ?? "Unknown";
            hw.BiosVersion = bios?.GetValue("BIOSVersion")?.ToString() ?? "Unknown";
            hw.BaseBoardModel = bios?.GetValue("BaseBoardProduct")?.ToString() ?? "Unknown";
            hw.IsAmiAptioBios = hw.BiosVendor.Contains("American Megatrends", StringComparison.OrdinalIgnoreCase) ||
                                hw.BiosVendor.Contains("AMI", StringComparison.OrdinalIgnoreCase);
        }

        using (var sbKey = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
        {
            hw.IsSecureBootEnabled = (sbKey?.GetValue("UEFISecureBootEnabled") as int?) == 1;
        }

        using (var dgKey = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard"))
        {
            hw.IsVbsEnabled = (dgKey?.GetValue("EnableVirtualizationBasedSecurity") as int?) == 1;
        }
    }

    private static void DetectCpuClockingMode(HardwareProfile hw, RegistryKey hklm)
    {
        // Detect if the user runs a Static All-Core Overclock (e.g. 4.70 GHz fixed ratio / PSS & CPPC disabled)
        bool staticOcDetected = false;
        string reason = "Boost Dinámico (PBO / Curve Optimizer / Turbo)";

        // Check if base clock in registry is >= 4500 MHz on Ryzen (Ryzen base clocks are 3.4-4.2GHz; when ~MHz reports 4650-4700+ MHz at boot, it's a static BIOS multiplier OC!)
        if (hw.CpuVendor == CpuVendorKind.AMD && hw.CpuMaxClockMHz >= 4550 && !hw.CpuName.Contains("7950", StringComparison.OrdinalIgnoreCase))
        {
            staticOcDetected = true;
            reason = $"All-Core Overclock Fijo detectado (~{hw.CpuMaxClockMHz / 1000.0:F2} GHz multiplicador estático en BIOS)";
        }

        // (T04) Ya no se lee ningún nvram.txt automáticamente: la detección no debe depender de archivos del autor.
        // El usuario puede forzar el modo con el override de la UI (ClockingModeOverride).

        hw.ClockingMode = staticOcDetected ? CpuClockingMode.StaticAllCoreOverclock : CpuClockingMode.DynamicBoostPbo;
        hw.ClockingModeDescription = reason;
    }

    private static void DetectGpu(HardwareProfile hw, RegistryKey hklm)
    {
        using var displayClass = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (displayClass == null)
        {
            return;
        }

        foreach (string subName in displayClass.GetSubKeyNames())
        {
            if (subName.Length != 4 || !char.IsDigit(subName[0])) continue;
            using var sub = displayClass.OpenSubKey(subName);
            string? desc = sub?.GetValue("DriverDesc")?.ToString();
            if (string.IsNullOrWhiteSpace(desc)) continue;

            hw.GpuName = desc;
            hw.GpuDriverVersion = sub?.GetValue("DriverVersion")?.ToString() ?? "N/A";
            hw.GpuDriverDate = sub?.GetValue("DriverDate")?.ToString() ?? "N/A";
            hw.GpuPnpInstanceId = sub?.GetValue("MatchingDeviceId")?.ToString() ?? string.Empty;

            if (sub?.GetValue("HardwareInformation.qwMemorySize") is long qwMem && qwMem > 0)
            {
                hw.GpuVramGb = (int)Math.Round(qwMem / (1024.0 * 1024.0 * 1024.0));
            }
            else if (sub?.GetValue("HardwareInformation.MemorySize") is int dwMem && dwMem > 0)
            {
                hw.GpuVramGb = (int)Math.Round((uint)dwMem / (1024.0 * 1024.0 * 1024.0));
            }

            if (desc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || desc.Contains("GeForce", StringComparison.OrdinalIgnoreCase) || desc.Contains("RTX", StringComparison.OrdinalIgnoreCase) || desc.Contains("GTX", StringComparison.OrdinalIgnoreCase))
            {
                hw.GpuVendor = GpuVendorKind.Nvidia;
            }
            else if (desc.Contains("AMD", StringComparison.OrdinalIgnoreCase) || desc.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
            {
                hw.GpuVendor = GpuVendorKind.Amd;
            }
            else if (desc.Contains("Intel", StringComparison.OrdinalIgnoreCase) || desc.Contains("Arc", StringComparison.OrdinalIgnoreCase))
            {
                hw.GpuVendor = GpuVendorKind.Intel;
            }

            hw.GpuGeneration = ClassifyGpuGeneration(desc);
            break;
        }

        // Also locate exact PCI Device Instance ID for GPU under HKLM\SYSTEM\CurrentControlSet\Enum\PCI
        try
        {
            using var pciEnum = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI");
            if (pciEnum != null)
            {
                foreach (string devKeyName in pciEnum.GetSubKeyNames())
                {
                    using var devKey = pciEnum.OpenSubKey(devKeyName);
                    if (devKey == null) continue;
                    foreach (string instName in devKey.GetSubKeyNames())
                    {
                        using var instKey = devKey.OpenSubKey(instName);
                        string? cls = instKey?.GetValue("Class")?.ToString();
                        if (string.Equals(cls, "Display", StringComparison.OrdinalIgnoreCase))
                        {
                            hw.GpuPnpInstanceId = $@"PCI\{devKeyName}\{instName}";
                            break;
                        }
                    }
                }
            }
        }
        catch { }
    }

    private static string ClassifyGpuGeneration(string gpuName)
    {
        if (Regex.IsMatch(gpuName, @"RTX\s*50[0-9]{2}", RegexOptions.IgnoreCase)) return "RTX50";
        if (Regex.IsMatch(gpuName, @"RTX\s*40[0-9]{2}", RegexOptions.IgnoreCase)) return "RTX40";
        if (Regex.IsMatch(gpuName, @"RTX\s*30[0-9]{2}", RegexOptions.IgnoreCase)) return "RTX30";
        if (Regex.IsMatch(gpuName, @"RTX\s*20[0-9]{2}", RegexOptions.IgnoreCase)) return "RTX20";
        if (Regex.IsMatch(gpuName, @"GTX\s*16[0-9]{2}", RegexOptions.IgnoreCase)) return "GTX16";
        if (Regex.IsMatch(gpuName, @"GTX\s*10[0-9]{2}", RegexOptions.IgnoreCase)) return "GTX10";
        if (Regex.IsMatch(gpuName, @"RX\s*7[0-9]{3}", RegexOptions.IgnoreCase)) return "RX7000";
        if (Regex.IsMatch(gpuName, @"RX\s*6[0-9]{3}", RegexOptions.IgnoreCase)) return "RX6000";
        if (Regex.IsMatch(gpuName, @"RX\s*5[0-9]{3}", RegexOptions.IgnoreCase)) return "RX5000";
        return "Unknown";
    }

    private static void DetectMemoryAndStorage(HardwareProfile hw, RegistryKey hklm)
    {
        var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref memStatus) && memStatus.ullTotalPhys > 0)
        {
            hw.RamTotalGb = (int)Math.Round(memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0));
        }

        // Check NVMe driver running state
        using var nvmeSvc = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\stornvme\Enum");
        int nvmeCount = (nvmeSvc?.GetValue("Count") as int?) ?? 0;
        hw.Storage = nvmeCount > 0 ? StorageKind.NvmeOrSsd : StorageKind.NvmeOrSsd;
    }

    private static void DetectNic(HardwareProfile hw, RegistryKey hklm)
    {
        using var netClass = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}");
        if (netClass == null) return;

        foreach (string subName in netClass.GetSubKeyNames())
        {
            if (subName.Length != 4 || !char.IsDigit(subName[0])) continue;
            using var sub = netClass.OpenSubKey(subName);
            string? desc = sub?.GetValue("DriverDesc")?.ToString();
            string? pnpId = sub?.GetValue("DeviceInstanceID")?.ToString();
            if (string.IsNullOrWhiteSpace(desc) || string.IsNullOrWhiteSpace(pnpId)) continue;
            if (!pnpId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) &&
                !pnpId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            hw.NicName = desc;
            hw.NicDriverVersion = sub?.GetValue("DriverVersion")?.ToString() ?? "N/A";
            hw.NicDriverDate = sub?.GetValue("DriverDate")?.ToString() ?? "N/A";
            hw.NicDriverProvider = sub?.GetValue("ProviderName")?.ToString() ?? "N/A";
            hw.NicPnpInstanceId = pnpId;
            hw.NicType = ClassifyNicType(desc);
            break;
        }
    }

    private static string ClassifyNicType(string nicName)
    {
        if (Regex.IsMatch(nicName, @"RTL8125|Realtek.*2\.5G|PCIe 2\.5GbE", RegexOptions.IgnoreCase)) return "RTL8125";
        if (Regex.IsMatch(nicName, @"RTL8111|RTL8168|Realtek PCIe GbE", RegexOptions.IgnoreCase)) return "RTL8111";
        if (Regex.IsMatch(nicName, @"I225|I226", RegexOptions.IgnoreCase)) return "IntelI225";
        if (Regex.IsMatch(nicName, @"I219|I217|I218", RegexOptions.IgnoreCase)) return "IntelI219";
        if (Regex.IsMatch(nicName, @"AQC107|Aquantia|Marvell", RegexOptions.IgnoreCase)) return "Aquantia";
        if (Regex.IsMatch(nicName, @"Killer|Rivet", RegexOptions.IgnoreCase)) return "Killer";
        return "Generic";
    }

    private static void DetectMonitorHz(HardwareProfile hw)
    {
        try
        {
            var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(null, -1, ref dm) && dm.dmDisplayFrequency > 0)
            {
                hw.MonitorRefreshHz = (int)dm.dmDisplayFrequency;
            }
        }
        catch { }
    }
}
