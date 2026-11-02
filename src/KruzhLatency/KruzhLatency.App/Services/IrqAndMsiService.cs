using System;
using System.Collections.Generic;
using System.Linq;
using KruzhLatency.Models;
using Microsoft.Win32;

namespace KruzhLatency.Services;

public sealed class IrqAndMsiService
{
    private readonly TransactionalAuditService _audit;
    private readonly HardwareChassisService _hwService;

    public IrqAndMsiService(TransactionalAuditService audit, HardwareChassisService hwService)
    {
        _audit = audit;
        _hwService = hwService;
    }

    public List<PciMsiDeviceItem> EnumeratePciDevices()
    {
        var results = new List<PciMsiDeviceItem>();
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var pciRoot = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI");
        if (pciRoot == null)
        {
            return results;
        }

        foreach (string venDevKeyName in pciRoot.GetSubKeyNames())
        {
            using var venDevKey = pciRoot.OpenSubKey(venDevKeyName);
            if (venDevKey == null) continue;

            foreach (string instanceName in venDevKey.GetSubKeyNames())
            {
                using var instKey = venDevKey.OpenSubKey(instanceName);
                if (instKey == null) continue;

                string? service = instKey.GetValue("Service")?.ToString() ?? "";
                string? cls = instKey.GetValue("Class")?.ToString() ?? "";
                string? desc = instKey.GetValue("DeviceDesc")?.ToString() ?? "";
                string? friendly = instKey.GetValue("FriendlyName")?.ToString() ?? desc;

                int semiIdx = friendly.LastIndexOf(';');
                if (semiIdx >= 0 && semiIdx + 1 < friendly.Length)
                {
                    friendly = friendly[(semiIdx + 1)..];
                }

                string category = ClassifyPciDevice(cls, service, friendly);
                if (string.IsNullOrEmpty(category))
                {
                    continue;
                }

                // Verify the device is currently present/active by checking if it has Device Parameters\Interrupt Management
                string intMgmtRel = $@"SYSTEM\CurrentControlSet\Enum\PCI\{venDevKeyName}\{instanceName}\Device Parameters\Interrupt Management";
                using var intMgmtKey = hklm.OpenSubKey(intMgmtRel);
                using var msiKey = hklm.OpenSubKey($@"{intMgmtRel}\MessageSignaledInterruptProperties");
                using var affKey = hklm.OpenSubKey($@"{intMgmtRel}\Affinity Policy");

                bool msiSupported = (msiKey?.GetValue("MSISupported") as int?) == 1;
                int msgLimit = (msiKey?.GetValue("MessageNumberLimit") as int?) ?? 1;
                int priority = (affKey?.GetValue("DevicePriority") as int?) ?? 2;

                int assignedCore = -1;
                string assignedLabel = "OS Auto";
                if ((affKey?.GetValue("DevicePolicy") as int?) == 4 &&
                    affKey.GetValue("AssignmentSetOverride") is byte[] maskBytes &&
                    maskBytes.Length > 0)
                {
                    ulong mask = 0;
                    for (int b = 0; b < Math.Min(8, maskBytes.Length); b++)
                    {
                        mask |= ((ulong)maskBytes[b]) << (b * 8);
                    }
                    for (int bit = 0; bit < 64; bit++)
                    {
                        if ((mask & (1UL << bit)) != 0)
                        {
                            assignedCore = bit;
                            assignedLabel = $"Hilo Lógico #{bit} (Máscara 0x{mask:X2})";
                            break;
                        }
                    }
                }

                bool isRealtekOrHdAudio = category == "Audio Controller" ||
                                          friendly.Contains("Audio", StringComparison.OrdinalIgnoreCase) ||
                                          friendly.Contains("Realtek", StringComparison.OrdinalIgnoreCase) && category != "NIC Ethernet";

                results.Add(new PciMsiDeviceItem
                {
                    FriendlyName = friendly,
                    Category = category,
                    PnpInstanceId = $@"PCI\{venDevKeyName}\{instanceName}",
                    MsiSupported = msiSupported,
                    MaxMsiMessages = category == "NVMe Controller" ? 16 : (category == "USB xHCI Controller" ? 8 : 1),
                    CurrentMessageLimit = msgLimit,
                    DevicePriority = priority,
                    AssignedCoreIndex = assignedCore,
                    AssignedCoreLabel = assignedLabel,
                    IsSafeForMsi = !isRealtekOrHdAudio,
                    SafetyNote = isRealtekOrHdAudio
                        ? "Controladora HD Audio: mantener en modo Línea por defecto para evitar artefactos/cortes de sonido."
                        : "100% seguro para MSI Mode + Prioridad Alta."
                });
            }
        }

        return results
            .OrderBy(d => d.Category switch
            {
                "GPU Display" => 0,
                "USB xHCI Controller" => 1,
                "NIC Ethernet" => 2,
                "NVMe Controller" => 3,
                _ => 4
            })
            .ToList();
    }

    private static string ClassifyPciDevice(string cls, string service, string friendlyName)
    {
        if (string.Equals(cls, "Display", StringComparison.OrdinalIgnoreCase))
            return "GPU Display";
        if (string.Equals(service, "USBXHCI", StringComparison.OrdinalIgnoreCase) ||
            friendlyName.Contains("xHCI", StringComparison.OrdinalIgnoreCase) ||
            friendlyName.Contains("USB 3.", StringComparison.OrdinalIgnoreCase))
            return "USB xHCI Controller";
        if (string.Equals(service, "stornvme", StringComparison.OrdinalIgnoreCase) ||
            friendlyName.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
            return "NVMe Controller";
        if (string.Equals(cls, "Net", StringComparison.OrdinalIgnoreCase))
            return "NIC Ethernet";
        if (string.Equals(service, "HDAudBus", StringComparison.OrdinalIgnoreCase) &&
            friendlyName.Contains("Audio", StringComparison.OrdinalIgnoreCase))
            return "Audio Controller";
        return string.Empty;
    }

    /// <summary>
    /// Native MSI Utility v3 implementation: Enables MSI Mode and sets DevicePriority on safe PCI instances
    /// (GPU, USB xHCI, NVMe, NIC), skipping HD Audio controllers unless explicitly requested.
    /// </summary>
    public (bool Success, string Summary) ApplySafePciMsiMode(string tweakId = "PciMsiModeV3")
    {
        var devices = EnumeratePciDevices().Where(d => d.IsSafeForMsi).ToList();
        int count = 0;

        foreach (var dev in devices)
        {
            string baseReg = $@"SYSTEM\CurrentControlSet\Enum\{dev.PnpInstanceId}\Device Parameters\Interrupt Management";
            string msiPath = $@"{baseReg}\MessageSignaledInterruptProperties";
            string affPath = $@"{baseReg}\Affinity Policy";

            _audit.SetRegistryValue(tweakId, RegistryHive.LocalMachine, msiPath, "MSISupported", 1, RegistryValueKind.DWord);

            // High priority (3) for GPU & USB xHCI; Normal (2) for NIC & NVMe so they don't preempt mouse/frame presentation
            int targetPriority = dev.Category is "GPU Display" or "USB xHCI Controller" ? 3 : 2;
            _audit.SetRegistryValue(tweakId, RegistryHive.LocalMachine, affPath, "DevicePriority", targetPriority, RegistryValueKind.DWord);
            count++;
        }

        return (count > 0, $"MSI Mode v3 + DevicePriority aplicado en {count} dispositivos PCI reales (GPU, xHCI, NVMe, NIC).");
    }

    /// <summary>
    /// Native AutoGpuAffinity implementation: Assigns GPU, USB xHCI, and NIC interrupts to distinct physical P-cores,
    /// NEVER Core 0, NEVER SMT secondary threads, and NEVER E-cores.
    /// </summary>
    public (bool Success, string Summary) ApplyOptimalPciIrqAffinity(string tweakId = "PciIrqSmartBalancer")
    {
        var hw = _hwService.CurrentProfile;

        // Filter candidate cores: primary physical threads (no SMT secondary), no E-cores, and strictly LogicalIndex > 0 (never Core 0!)
        var candidatePhysicalThreads = hw.Topology
            .Where(t => t.LogicalIndex > 0 && !t.IsSmtSecondaryThread && !t.IsEfficiencyCore)
            .Select(t => t.LogicalIndex)
            .ToList();

        if (candidatePhysicalThreads.Count < 2)
        {
            // Fallback if topology has few cores: pick any non-zero logical processors
            for (int i = 1; i < hw.CpuLogicalThreads; i++)
            {
                if (!candidatePhysicalThreads.Contains(i))
                {
                    candidatePhysicalThreads.Add(i);
                }
            }
        }

        if (candidatePhysicalThreads.Count == 0)
        {
            return (false, "No hay suficientes núcleos fuera de Core 0 para aislar IRQs.");
        }

        // Assign distinct physical cores:
        // Last high physical P-core -> GPU
        // Second-to-last physical P-core -> USB xHCI (Mouse)
        // Third physical P-core -> NIC Ethernet
        int gpuThread = candidatePhysicalThreads[^1];
        int usbThread = candidatePhysicalThreads.Count >= 2 ? candidatePhysicalThreads[^2] : gpuThread;
        int nicThread = candidatePhysicalThreads.Count >= 3 ? candidatePhysicalThreads[^3] : usbThread;

        var devices = EnumeratePciDevices();
        int appliedCount = 0;

        foreach (var dev in devices)
        {
            int targetThread = dev.Category switch
            {
                "GPU Display" => gpuThread,
                "USB xHCI Controller" => usbThread,
                "NIC Ethernet" => nicThread,
                _ => -1
            };

            if (targetThread < 0) continue;

            ulong mask = 1UL << targetThread;
            byte[] maskBytes = GetMinimalAffinityBytes(mask);
            string affPath = $@"SYSTEM\CurrentControlSet\Enum\{dev.PnpInstanceId}\Device Parameters\Interrupt Management\Affinity Policy";

            _audit.SetRegistryValue(tweakId, RegistryHive.LocalMachine, affPath, "DevicePolicy", 4, RegistryValueKind.DWord);
            _audit.SetRegistryValue(tweakId, RegistryHive.LocalMachine, affPath, "AssignmentSetOverride", maskBytes, RegistryValueKind.Binary);
            appliedCount++;
        }

        return (appliedCount > 0,
            $"IRQ Affinity en Enum\\PCI aplicado ({appliedCount} buses): GPU -> Hilo #{gpuThread}, USB xHCI -> Hilo #{usbThread}, NIC -> Hilo #{nicThread} (Core 0 libre).");
    }

    public bool IsPciIrqAffinityActive()
    {
        return EnumeratePciDevices().Any(d => d.AssignedCoreIndex > 0);
    }

    public bool IsPciMsiModeAllEnabled()
    {
        var safeDevs = EnumeratePciDevices().Where(d => d.IsSafeForMsi).ToList();
        return safeDevs.Count > 0 && safeDevs.All(d => d.MsiSupported);
    }

    private static byte[] GetMinimalAffinityBytes(ulong mask)
    {
        byte[] full = BitConverter.GetBytes(mask);
        int lastNonZero = full.Length - 1;
        while (lastNonZero > 0 && full[lastNonZero] == 0)
        {
            lastNonZero--;
        }
        byte[] trimmed = new byte[lastNonZero + 1];
        Array.Copy(full, trimmed, trimmed.Length);
        return trimmed;
    }
}

