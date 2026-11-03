using System.Security.AccessControl;
using System.Security.Principal;

namespace KruzhLatency.Infra.Security;

/// <summary>
/// Carpetas de máquina (C:\ProgramData\KruzhLatency\…) que solo SYSTEM y Administradores pueden escribir.
/// Lo que vive acá lo usa la app elevada para escribir en el sistema (journal, scripts de vuelta atrás): si un usuario
/// sin privilegios pudiera editarlo, podría hacer que KRUZH escriba lo que él quiera (escalada de privilegios).
/// </summary>
public static class SecureFolders
{
    public static string ProgramData(params string[] parts)
    {
        string dir = Path.Combine(new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KruzhLatency" }.Concat(parts).ToArray());
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));

        var info = new DirectoryInfo(dir);
        if (!info.Exists) info.Create(sec);
        else info.SetAccessControl(sec);
        return dir;
    }
}
