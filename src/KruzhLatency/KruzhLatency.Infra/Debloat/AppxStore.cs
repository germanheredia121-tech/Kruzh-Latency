using KruzhLatency.Core.Debloat;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace KruzhLatency.Infra.Debloat;

/// <summary>
/// Apps AppX del usuario vía Windows.Management.Deployment.PackageManager (la API que usa Configuración → Apps).
/// Sin PowerShell ni texto: listar, quitar para el usuario, volver a registrar (revertir) y desaprovisionar.
/// </summary>
public static class AppxStore
{
    private static readonly PackageManager Manager = new();

    /// <summary>Paquetes del usuario actual (el de la sesión que corre KRUZH).</summary>
    public static IReadOnlyList<InstalledAppx> Installed() =>
        Manager.FindPackagesForUser(string.Empty)
            .Select(p => new InstalledAppx(p.Id.Name, p.Id.FamilyName, p.Id.FullName, p.IsFramework, p.SignatureKind == PackageSignatureKind.System))
            .ToList();

    public static bool IsInstalled(string fullName) =>
        Manager.FindPackagesForUser(string.Empty).Any(p => string.Equals(p.Id.FullName, fullName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Quita el paquete para el usuario. Los archivos quedan si está aprovisionado (por eso se puede revertir).</summary>
    public static (bool Ok, string Detail) RemoveForUser(string fullName)
    {
        var result = Manager.RemovePackageAsync(fullName).AsTask().GetAwaiter().GetResult();
        return result.IsRegistered && IsInstalled(fullName)
            ? (false, result.ErrorText)
            : (true, "Quitada.");
    }

    /// <summary>Revertir: vuelve a registrar el paquete para el usuario (funciona si sigue aprovisionado o instalado para otro usuario).</summary>
    public static (bool Ok, string Detail) Register(string fullName)
    {
        try
        {
            var result = Manager.RegisterPackageByFullNameAsync(fullName, Array.Empty<string>(), DeploymentOptions.None).AsTask().GetAwaiter().GetResult();
            return IsInstalled(fullName) ? (true, "Restaurada.") : (false, result.ErrorText);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return (false, $"No se pudo volver a registrar (reinstalala desde la Microsoft Store): {ex.Message}");
        }
    }

    /// <summary>Entusiasta: que no vuelva para usuarios nuevos ni con actualizaciones de Windows. KRUZH no lo puede deshacer.</summary>
    public static (bool Ok, string Detail) Deprovision(string familyName)
    {
        try
        {
            Manager.DeprovisionPackageForAllUsersAsync(familyName).AsTask().GetAwaiter().GetResult();
            return (true, "Desaprovisionada.");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return (false, ex.Message);
        }
    }
}
