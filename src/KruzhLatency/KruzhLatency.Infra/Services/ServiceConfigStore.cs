using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KruzhLatency.Infra.Services;

/// <summary>
/// Tipo de inicio de un servicio por el Administrador de servicios (ChangeServiceConfigW), no por el registro.
/// Varias claves de Services (DPS, WdiServiceHost, WdiSystemHost, TrkWks…) solo dan lectura a Administradores: la escritura
/// directa de "Start" falla, pero el SCM (que corre como SYSTEM) sí la hace si el servicio da SERVICE_CHANGE_CONFIG
/// a Administradores. Valores de inicio: 0 boot, 1 system, 2 automático, 3 manual, 4 deshabilitado.
/// </summary>
public static partial class ServiceConfigStore
{
    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_CONFIG = 0x0001;
    private const uint SERVICE_CHANGE_CONFIG = 0x0002;
    private const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
    private const int ERROR_ACCESS_DENIED = 5;

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenService(IntPtr scm, string name, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig(IntPtr service, uint type, uint start, uint error, string? binary, string? group,
        IntPtr tagId, string? dependencies, string? account, string? password, string? displayName);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);

    /// <summary>
    /// Cambia el tipo de inicio. Lanza <see cref="ServiceProtectedException"/> si Windows no le da permiso de configuración
    /// a Administradores sobre ese servicio (servicio protegido), o <see cref="Win32Exception"/> por otro error.
    /// </summary>
    public static void SetStartType(string serviceName, int start)
    {
        if (start is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(start));

        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            IntPtr svc = OpenService(scm, serviceName, SERVICE_CHANGE_CONFIG | SERVICE_QUERY_CONFIG);
            if (svc == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_ACCESS_DENIED) throw new ServiceProtectedException(serviceName);
                throw new Win32Exception(err);
            }
            try
            {
                if (!ChangeServiceConfig(svc, SERVICE_NO_CHANGE, (uint)start, SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, null))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_ACCESS_DENIED) throw new ServiceProtectedException(serviceName);
                    throw new Win32Exception(err);
                }
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }
}

/// <summary>El servicio no deja que Administradores cambien su configuración (lo protege Windows).</summary>
public sealed class ServiceProtectedException(string service)
    : InvalidOperationException($"Windows protege el servicio {service}: no deja cambiar su inicio ni siquiera como administrador.");
