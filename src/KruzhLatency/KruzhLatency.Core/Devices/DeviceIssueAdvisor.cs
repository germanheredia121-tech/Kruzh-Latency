using KruzhLatency.Core.Tweaks;

namespace KruzhLatency.Core.Devices;

public enum DeviceIssueAction
{
    /// <summary>Se puede deshabilitar sin perder nada (ej. controladora RAID sin discos, USB que falla).</summary>
    Disable,
    /// <summary>Falta el driver: se abre la descarga oficial.</summary>
    InstallDriver,
    /// <summary>Solo aviso (revisar hardware / reinstalar driver).</summary>
    Advice
}

public sealed record DeviceIssue(
    string Id,
    DeviceNode Device,
    string Title,
    string Detail,
    DeviceIssueAction Action,
    /// <summary>Descarga oficial o página de Windows Update (InstallDriver).</summary>
    string? Link = null,
    Profile DisableFrom = Profile.Competitivo);

/// <summary>
/// Spec 002 / B3: dispositivos presentes con código de error del Administrador de dispositivos.
/// Un dispositivo en error puede reintentar en bucle y generar interrupciones/DPC; acá se decide qué hacer con cada uno.
/// Nunca propone deshabilitar algo protegido (ADR-007).
/// </summary>
public static class DeviceIssueAdvisor
{
    public const uint CodeNotConfigured = 1;
    public const uint CodeCannotStart = 10;
    public const uint CodeDisabled = 22;
    public const uint CodeNoDriver = 28;
    public const uint CodeReportedFailure = 43;
    public const uint CodeNotConnected = 45;

    public static IReadOnlyList<DeviceIssue> Evaluate(DeviceTree tree, DeviceSafetyContext safety)
    {
        var protectedSet = NeverTouchPolicy.Evaluate(tree, safety);
        var issues = new List<DeviceIssue>();

        foreach (var n in tree.All.Where(n => n.IsPresent && n.ProblemCode is not 0 and not CodeDisabled and not CodeNotConnected))
        {
            bool canDisable = !protectedSet.ContainsKey(n.InstanceId);
            string id = $"Issue:{n.InstanceId}";

            if (n.ProblemCode == CodeReportedFailure && IsUsb(n) && canDisable)
            {
                issues.Add(new DeviceIssue(id, n, $"Dispositivo USB que falla: {n.DisplayName} (Code 43)",
                    "Windows lo detecta pero el dispositivo no responde (cable, puerto, hub o el aparato). Mientras esté así el controlador USB reintenta y genera interrupciones. " +
                    "Se deshabilita; revisá físicamente qué está enchufado (probá otro cable o puerto) y desenchufalo si no lo usás.",
                    DeviceIssueAction.Disable));
                continue;
            }

            if (n.ProblemCode == CodeNoDriver && DeviceRuleEngine.PciClassStarts(n, "0104") && canDisable &&
                !tree.Descendants(n).Any(d => d.IsPresent && d.ClassName.Equals("DiskDrive", StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new DeviceIssue(id, n, $"Controladora RAID sin driver ni discos: {n.DisplayName} (Code 28)",
                    "No hay ningún disco en modo RAID: tus discos usan otra controladora (NVMe/AHCI). Se deshabilita para que no quede un dispositivo PCI sin driver.",
                    DeviceIssueAction.Disable));
                continue;
            }

            if (n.ProblemCode is CodeNoDriver or CodeNotConfigured)
            {
                issues.Add(new DeviceIssue(id, n, $"Sin driver: {n.DisplayName} (Code {n.ProblemCode})",
                    $"Windows no tiene driver para este dispositivo. {DriverHint(n)}",
                    DeviceIssueAction.InstallDriver, DriverLink(n)));
                continue;
            }

            issues.Add(new DeviceIssue(id, n, $"Dispositivo con error: {n.DisplayName} (Code {n.ProblemCode})",
                n.ProblemCode == CodeCannotStart
                    ? "El driver no pudo iniciar el dispositivo. Reinstalá el driver del fabricante o probá otro puerto/ranura."
                    : "El Administrador de dispositivos reporta un problema. Reinstalá el driver o revisá la conexión.",
                DeviceIssueAction.Advice, DriverLink(n)));
        }

        return issues;
    }

    private static bool IsUsb(DeviceNode n) =>
        n.Enumerator.Equals("USB", StringComparison.OrdinalIgnoreCase) || n.HasHardwareIdPrefix(@"USB\");

    private static string DriverHint(DeviceNode n) =>
        n.HasHardwareIdPrefix(@"PCI\VEN_1022") ? "Es un dispositivo de AMD: instalá el driver de chipset de AMD para tu placa."
        : n.HasHardwareIdPrefix(@"PCI\VEN_8086") ? "Es un dispositivo de Intel: instalá el driver de chipset con Intel Driver & Support Assistant."
        : n.HasHardwareIdPrefix(@"PCI\VEN_10DE") ? "Es de NVIDIA: reinstalá el driver de la GPU."
        : n.HasHardwareIdPrefix(@"PCI\VEN_10EC") ? "Es de Realtek: instalá el driver desde la página de Realtek."
        : "Buscalo en las actualizaciones opcionales de Windows Update o en la página del fabricante de tu placa madre.";

    internal static string DriverLink(DeviceNode n) =>
        n.HasHardwareIdPrefix(@"PCI\VEN_1022") ? "https://www.amd.com/en/support/download/drivers.html"
        : n.HasHardwareIdPrefix(@"PCI\VEN_8086") ? "https://www.intel.com/content/www/us/en/support/detect.html"
        : n.HasHardwareIdPrefix(@"PCI\VEN_10DE") ? "https://www.nvidia.com/Download/Find.aspx"
        : n.HasHardwareIdPrefix(@"PCI\VEN_10EC") ? "https://www.realtek.com/Download/List?cate_id=584"
        : "ms-settings:windowsupdate-optionalupdates";
}
