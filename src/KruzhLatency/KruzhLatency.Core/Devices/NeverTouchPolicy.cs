namespace KruzhLatency.Core.Devices;

/// <summary>Hechos del sistema que no salen del árbol PnP (los detecta Infra en tiempo real).</summary>
public sealed record DeviceSafetyContext(
    /// <summary>GPU que tiene el escritorio (instancia PCI).</summary>
    string? ActiveGpuInstanceId = null,
    /// <summary>Disco de arranque (instancia del DiskDrive).</summary>
    string? BootDiskInstanceId = null,
    /// <summary>Placas de red con enlace activo.</summary>
    IReadOnlyCollection<string>? LinkedNicInstanceIds = null,
    /// <summary>Endpoint de audio de la salida predeterminada (SWD\MMDEVAPI\…).</summary>
    string? DefaultAudioEndpointInstanceId = null);

public enum ProtectionReason
{
    TreeRoot,
    PciBridge,
    PlatformDevice,
    Processor,
    SecurityDevice,
    ActiveGpu,
    StorageChain,
    InputDeviceChain,
    LinkedNic,
    DefaultAudioChain,
    FallbackDisplay
}

public sealed record DeviceProtection(string InstanceId, ProtectionReason Reason, string Explanation);

/// <summary>
/// ADR-007: dispositivos que KRUZH nunca deshabilita ni elimina. Se decide por topología, clase PCI y enumerador,
/// nunca por nombre visible (que está traducido y varía por fabricante).
/// Cualquier regla que proponga deshabilitar algo debe pasar primero por <see cref="Evaluate"/>.
/// </summary>
public static class NeverTouchPolicy
{
    /// <summary>
    /// Dispositivos ACPI que no son críticos para arrancar y que spec 002 (B1) sí puede deshabilitar.
    /// Todo lo demás enumerado por ACPI queda protegido.
    /// </summary>
    private static readonly string[] NonCriticalAcpiHardwareIds =
    {
        @"ACPI\VEN_PNP&DEV_0103", @"ACPI\PNP0103",   // HPET
        @"ACPI\VEN_PNP&DEV_0800", @"ACPI\PNP0800",   // Altavoz del sistema (beep)
        @"ACPI\VEN_NVDA&DEV_0820", @"ACPI\NVDA0820", // NVIDIA Platform Controllers and Framework (NVPCF)
    };

    /// <summary>Buses raíz virtuales que no hacen falta para arrancar ni para jugar (los deshabilitan las reglas de B1).</summary>
    private static readonly string[] NonCriticalRootHardwareIds =
    {
        @"ROOT\CompositeBus", @"ROOT\NdisVirtualBus", @"ROOT\RDPBUS", @"ROOT\umbus", @"ROOT\vdrvroot",
    };

    /// <summary>Servicios de display que nunca se detienen ni deshabilitan (panel de NVIDIA/AMD, ver ADR-007).</summary>
    public static readonly IReadOnlySet<string> ProtectedServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "NVDisplay.ContainerLocalSystem",
        "AMD External Events Utility",
        "amdfendr",
        "amdfendrmgr",
    };

    public static IReadOnlyDictionary<string, DeviceProtection> Evaluate(DeviceTree tree, DeviceSafetyContext context)
    {
        var result = new Dictionary<string, DeviceProtection>(StringComparer.OrdinalIgnoreCase);

        void Protect(DeviceNode node, ProtectionReason reason, string why)
        {
            // La primera razón gana: las de clase (más específicas) se evalúan antes que las de cadena.
            result.TryAdd(node.InstanceId, new DeviceProtection(node.InstanceId, reason, why));
        }

        void ProtectWithAncestors(string? instanceId, ProtectionReason reason, string why)
        {
            if (instanceId is null || tree.Get(instanceId) is not { } node) return;
            Protect(node, reason, why);
            foreach (var a in tree.Ancestors(node)) Protect(a, reason, why);
        }

        // 1. Por clase / enumerador (independiente del contexto).
        foreach (var n in tree.All)
        {
            if (ClassReason(n) is { } r) Protect(n, r.Reason, r.Why);
        }

        // 2. Cadenas de padres: entrada (mouse/teclado), almacenamiento, GPU, red, audio.
        foreach (var input in tree.All.Where(n => n.IsPresent && (Is(n, "Mouse") || Is(n, "Keyboard"))))
        {
            ProtectWithAncestors(input.InstanceId, ProtectionReason.InputDeviceChain,
                "Está en el camino del mouse o teclado (controladora USB, hub o el propio dispositivo).");
        }

        foreach (var disk in tree.All.Where(n => n.IsPresent && Is(n, "DiskDrive")))
        {
            ProtectWithAncestors(disk.InstanceId, ProtectionReason.StorageChain, "Controladora o camino de un disco conectado.");
        }
        ProtectWithAncestors(context.BootDiskInstanceId, ProtectionReason.StorageChain, "Disco de arranque.");

        if (context.ActiveGpuInstanceId is { } gpuId)
        {
            ProtectWithAncestors(gpuId, ProtectionReason.ActiveGpu, "GPU con el escritorio (deshabilitarla deja la pantalla en el driver básico).");
        }
        else
        {
            // Sin dato de Infra: se protegen todas las GPU presentes.
            foreach (var g in tree.All.Where(n => n.IsPresent && Is(n, "Display")))
            {
                ProtectWithAncestors(g.InstanceId, ProtectionReason.ActiveGpu, "GPU presente (no se pudo saber cuál tiene el escritorio).");
            }
        }

        foreach (var nic in context.LinkedNicInstanceIds ?? Array.Empty<string>())
        {
            ProtectWithAncestors(nic, ProtectionReason.LinkedNic, "Placa de red con enlace activo.");
        }

        ProtectWithAncestors(context.DefaultAudioEndpointInstanceId, ProtectionReason.DefaultAudioChain,
            "Salida de audio predeterminada y su controladora.");

        return result;
    }

    public static bool IsProtected(IReadOnlyDictionary<string, DeviceProtection> evaluation, string instanceId) =>
        evaluation.ContainsKey(instanceId);

    private static (ProtectionReason Reason, string Why)? ClassReason(DeviceNode n)
    {
        if (n.ParentInstanceId is null || n.Enumerator.Equals("HTREE", StringComparison.OrdinalIgnoreCase))
            return (ProtectionReason.TreeRoot, "Raíz del árbol de dispositivos.");

        if (n.IsPci && PciClass(n) is { } cc)
        {
            if (cc.StartsWith("06")) return (ProtectionReason.PciBridge, "Bridge PCI / root port / host bridge: deshabilitarlo apaga todo lo que cuelga de él.");
            if (cc.StartsWith("08") && !cc.StartsWith("0805")) return (ProtectionReason.PlatformDevice, "Periférico de sistema (IOMMU, PIC, timer, RTC).");
            if (cc.StartsWith("0C05")) return (ProtectionReason.PlatformDevice, "Controladora SMBus (sensores, SPD de la RAM).");
            if (cc.StartsWith("10")) return (ProtectionReason.SecurityDevice, "Procesador de seguridad (AMD PSP / cifrado).");
            if (cc.StartsWith("13")) return (ProtectionReason.PlatformDevice, "Función de instrumentación de la plataforma.");
            if (cc.StartsWith("0780") && n.HasHardwareIdPrefix(@"PCI\VEN_8086")) return (ProtectionReason.SecurityDevice, "Intel Management Engine.");
        }

        if (Is(n, "Processor")) return (ProtectionReason.Processor, "Procesador.");
        if (Is(n, "SecurityDevices")) return (ProtectionReason.SecurityDevice, "TPM (lo exigen Vanguard y Windows 11).");
        if (Is(n, "Computer")) return (ProtectionReason.PlatformDevice, "Nodo de la PC (HAL).");

        if (n.Enumerator.Equals("ACPI_HAL", StringComparison.OrdinalIgnoreCase))
            return (ProtectionReason.PlatformDevice, "ACPI del sistema.");
        if (n.Enumerator.Equals("ACPI", StringComparison.OrdinalIgnoreCase) &&
            !NonCriticalAcpiHardwareIds.Any(n.HasHardwareIdPrefix))
            return (ProtectionReason.PlatformDevice, "Dispositivo ACPI de la placa madre.");

        if (n.Enumerator.Equals("ROOT", StringComparison.OrdinalIgnoreCase))
        {
            if (n.HasHardwareIdPrefix(@"ROOT\BasicDisplay") || n.HasHardwareIdPrefix(@"ROOT\BasicRender"))
                return (ProtectionReason.FallbackDisplay, "Driver de pantalla de respaldo (si la GPU falla, es lo único que da imagen).");
            if ((Is(n, "System") || Is(n, "SCSIAdapter") || Is(n, "Volume")) &&
                !NonCriticalRootHardwareIds.Any(n.HasHardwareIdPrefix))
                return (ProtectionReason.PlatformDevice, "Bus o enumerador raíz de Windows.");
        }

        return null;
    }

    /// <summary>Código de clase PCI (6 hex, ej. 060400) desde el compatible ID PCI\CC_xxxxxx.</summary>
    private static string? PciClass(DeviceNode n)
    {
        const string prefix = @"PCI\CC_";
        var cc = n.CompatibleIds.FirstOrDefault(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return cc?[prefix.Length..].ToUpperInvariant();
    }

    private static bool Is(DeviceNode n, string className) =>
        string.Equals(n.ClassName, className, StringComparison.OrdinalIgnoreCase);
}
