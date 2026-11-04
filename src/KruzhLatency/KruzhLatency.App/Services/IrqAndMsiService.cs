using System;
using System.Collections.Generic;
using System.Linq;
using KruzhLatency.Models;
using KruzhLatency.Core.Devices;
using KruzhLatency.Core.Latency;
using KruzhLatency.Infra.Devices;
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
                    IsSafeForMsi = true,
                    SafetyNote = isRealtekOrHdAudio
                        ? "Audio HD: MSI activable si el dispositivo lo declara. Si después escuchás cortes, revertí I01."
                        : "MSI activable si el dispositivo lo declara."
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


    /// <summary>Foto del árbol de dispositivos (si el llamador no tiene una).</summary>
    private static DeviceTree Tree(DeviceTree? tree) => tree ?? new DeviceTree(new CfgMgrDeviceInventory().Snapshot());

    private static string InterruptPath(string instanceId) =>
        $@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters\Interrupt Management";

    // ------------------------------------------------------------------ E8: MSI

    /// <summary>
    /// E8: MSISupported = 1 en xHCI, audio HD, GPU, red y NVMe que declaran soporte de MSI/MSI-X; en MSI-X además se borra
    /// MessageNumberLimit. La prioridad de interrupción ya no se toca acá: la decide el reparto por núcleo (E7).
    /// </summary>
    public (bool Success, string Summary) ApplySafePciMsiMode(string tweakId = "PciMsiModeV3", DeviceTree? tree = null)
    {
        var targets = MsiPlanner.Targets(Tree(tree));
        if (targets.Count == 0) return (false, "Ningún dispositivo declara soporte de MSI.");

        foreach (var t in targets)
        {
            string msiPath = $@"{InterruptPath(t.Device.InstanceId)}\MessageSignaledInterruptProperties";
            _audit.SetRegistryValue(tweakId, RegistryHive.LocalMachine, msiPath, "MSISupported", 1, RegistryValueKind.DWord);
            if (t.RemoveMessageLimit)
            {
                _audit.DeleteRegistryValue(tweakId, RegistryHive.LocalMachine, msiPath, "MessageNumberLimit");
            }
        }
        return (true, $"MSI activado en {targets.Count} dispositivos ({string.Join(", ", targets.GroupBy(t => t.Kind).Select(g => $"{g.Key} ×{g.Count()}"))}). Reiniciá y mirá el Monitor: si algo empeora, revertí.");
    }

    public bool IsPciMsiModeAllEnabled(DeviceTree? tree = null)
    {
        var targets = MsiPlanner.Targets(Tree(tree));
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        return targets.Count > 0 && targets.All(t =>
        {
            using var k = hklm.OpenSubKey($@"{InterruptPath(t.Device.InstanceId)}\MessageSignaledInterruptProperties");
            return k?.GetValue("MSISupported") is int v && v == 1 &&
                   (!t.RemoveMessageLimit || k.GetValue("MessageNumberLimit") is null);
        });
    }

    // ------------------------------------------------------------------ E7: afinidad por núcleo físico

    public AffinityPlan? PlanAffinity(DeviceTree? tree = null, bool gpuTwoCores = true) =>
        InterruptAffinityPlanner.Plan(_hwService.CurrentProfile.CoreTopology, InterruptAffinityPlanner.Collect(Tree(tree)), gpuTwoCores);

    /// <summary>
    /// E7: DevicePolicy = 4 + AssignmentSetOverride según el planificador (núcleo 0 libre, un núcleo por controladora USB,
    /// GPU en uno o dos núcleos, NVMe aparte). La red no lleva override: sus colas RSS las fija el ítem de la placa (E2).
    /// </summary>
    public (bool Success, string Summary) ApplyOptimalPciIrqAffinity(string tweakId = "PciIrqSmartBalancer", DeviceTree? tree = null, bool gpuTwoCores = true)
    {
        if (PlanAffinity(tree, gpuTwoCores) is not { } plan)
        {
            return (false, "Hacen falta al menos 4 núcleos físicos para separar las interrupciones.");
        }

        foreach (var a in plan.Assignments)
        {
            string affPath = $@"{InterruptPath(a.Device.InstanceId)}\Affinity Policy";
            if (a.MaskBytes is { } bytes)
            {
                _audit.SetRegistryValue(tweakId, RegistryHive.LocalMachine, affPath, "DevicePolicy", 4, RegistryValueKind.DWord);
                _audit.SetRegistryValue(tweakId, RegistryHive.LocalMachine, affPath, "AssignmentSetOverride", bytes, RegistryValueKind.Binary);
            }
            else
            {
                // Red: sin override (una afinidad vieja pisaría a RSS).
                _audit.DeleteRegistryValue(tweakId, RegistryHive.LocalMachine, affPath, "DevicePolicy");
                _audit.DeleteRegistryValue(tweakId, RegistryHive.LocalMachine, affPath, "AssignmentSetOverride");
            }

            if (a.DevicePriority is { } prio)
            {
                _audit.SetRegistryValue(tweakId, RegistryHive.LocalMachine, affPath, "DevicePriority", prio, RegistryValueKind.DWord);
            }
            else
            {
                _audit.DeleteRegistryValue(tweakId, RegistryHive.LocalMachine, affPath, "DevicePriority");
            }
        }

        return (true, Describe(plan) + " Reiniciá para que se aplique.");
    }

    /// <summary>Aplicado = cada dispositivo del plan tiene exactamente la máscara y la prioridad planeadas.</summary>
    public bool IsPciIrqAffinityActive(DeviceTree? tree = null, bool gpuTwoCores = true)
    {
        if (PlanAffinity(tree, gpuTwoCores) is not { } plan || plan.Assignments.Count == 0) return false;
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        return plan.Assignments.All(a =>
        {
            using var k = hklm.OpenSubKey($@"{InterruptPath(a.Device.InstanceId)}\Affinity Policy");
            bool maskOk = a.Mask is { } m
                ? k?.GetValue("DevicePolicy") is int p && p == 4 &&
                  k.GetValue("AssignmentSetOverride") is byte[] b && InterruptAffinityPlanner.FromMaskBytes(b) == m
                : k?.GetValue("AssignmentSetOverride") is null;
            bool prioOk = a.DevicePriority is { } prio ? k?.GetValue("DevicePriority") is int dp && dp == prio : k?.GetValue("DevicePriority") is null;
            return maskOk && prioOk;
        });
    }

    public static string Describe(AffinityPlan plan)
    {
        static string Cpus(IReadOnlyList<int> t) => t.Count == 1 ? $"CPU {t[0]}" : "CPU " + string.Join(" + ", t);
        var parts = new List<string>();
        if (plan.Rss is { } r) parts.Add($"Red: RSS en CPU {r.BaseProcessor}{(r.MaxProcessor != r.BaseProcessor ? $"–{r.MaxProcessor}" : "")}");
        foreach (var a in plan.Assignments.Where(a => a.Mask is not null))
        {
            parts.Add($"{a.Device.Name}: {Cpus(a.Threads)}{(a.DevicePriority == InterruptAffinityPlanner.UsbPriorityHigh ? " (prioridad alta)" : "")}{(a.SharesCore ? " (compartido)" : "")}");
        }
        return "CPU 0 libre. " + string.Join(" · ", parts) + "." + (plan.Notes.Count > 0 ? " " + string.Join(" ", plan.Notes) : "");
    }
}
