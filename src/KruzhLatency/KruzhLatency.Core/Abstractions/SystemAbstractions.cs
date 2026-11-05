namespace KruzhLatency.Core.Abstractions;

// ---------------------------------------------------------------------------------------------
// Procesos externos (bcdedit/netsh/schtasks como último recurso). Nunca se ignora el exit code.
// ---------------------------------------------------------------------------------------------
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

public interface IProcessRunner
{
    /// <summary>Lee stdout y stderr completos, respeta el timeout (mata el proceso al vencer) y no lanza por exit code ≠ 0.</summary>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------------------------
// Servicios
// ---------------------------------------------------------------------------------------------
/// <summary>Valores de HKLM\SYSTEM\CurrentControlSet\Services\X\Start.</summary>
public enum ServiceStartMode
{
    Boot = 0,
    System = 1,
    Automatic = 2,
    Manual = 3,
    Disabled = 4
}

public enum ServiceRunState
{
    Unknown,
    Stopped,
    Running,
    Pending
}

public sealed record ServiceSnapshot(string Name, ServiceStartMode StartMode, bool DelayedAutoStart, ServiceRunState RunState);

public interface IServiceManager
{
    /// <summary>null si el servicio no existe en esta PC.</summary>
    ServiceSnapshot? Get(string serviceName);

    void SetStartMode(string serviceName, ServiceStartMode mode, bool delayedAutoStart);

    /// <summary>Detiene el servicio si está corriendo. Devuelve false si no se pudo (ej. no admite stop).</summary>
    bool Stop(string serviceName, TimeSpan timeout);

    bool Start(string serviceName, TimeSpan timeout);
}

// ---------------------------------------------------------------------------------------------
// Energía (powrprof: PowerReadACValueIndex / PowerWriteACValueIndex). Siempre con GUID de esquema explícito.
// ---------------------------------------------------------------------------------------------
public interface IPowerSettings
{
    Guid GetActiveScheme();

    void SetActiveScheme(Guid scheme);

    bool SchemeExists(Guid scheme);

    /// <summary>null si el setting no existe en ese esquema.</summary>
    uint? ReadAcValue(Guid scheme, Guid subGroup, Guid setting);

    void WriteAcValue(Guid scheme, Guid subGroup, Guid setting, uint value);

    /// <summary>Duplica un esquema y devuelve el GUID nuevo.</summary>
    Guid DuplicateScheme(Guid source, string friendlyName);
}

// ---------------------------------------------------------------------------------------------
// BCD ({current}). Implementación vía WMI BcdStore: independiente del idioma de Windows.
// ---------------------------------------------------------------------------------------------
/// <summary>Elementos BCD que usa el catálogo. Se agregan acá a medida que hagan falta.</summary>
public enum BcdElement
{
    DisableDynamicTick,
    UsePlatformClock,
    UsePlatformTick,
    TscSyncPolicy,
    HypervisorLaunchType
}

/// <summary>Valor de un elemento BCD: booleano o entero (según el elemento).</summary>
public sealed record BcdValue(bool? Boolean, ulong? Integer)
{
    public static BcdValue Of(bool value) => new(value, null);
    public static BcdValue Of(ulong value) => new(null, value);
}

public interface IBcdStore
{
    /// <summary>null si el elemento no está definido en {current}.</summary>
    BcdValue? Read(BcdElement element);

    void Write(BcdElement element, BcdValue value);

    /// <summary>Borra el elemento (equivale a "no definido" = default de Windows).</summary>
    void Delete(BcdElement element);
}

// ---------------------------------------------------------------------------------------------
// Punto de restauración (WMI SystemRestore). Reporta el resultado REAL.
// ---------------------------------------------------------------------------------------------
public enum RestorePointOutcome
{
    Created,
    /// <summary>Windows limita a 1 punto cada 24 h por defecto; no es un error del usuario.</summary>
    SkippedFrequencyLimit,
    ProtectionDisabled,
    Failed
}

public sealed record RestorePointResult(RestorePointOutcome Outcome, string Detail);

public interface IRestorePoint
{
    Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------------------------
// Dispositivos (SetupAPI / CfgMgr32): auditor de drivers + MSI auto-tune.
// ---------------------------------------------------------------------------------------------
/// <summary>Capacidades de interrupción leídas del HARDWARE (DEVPKEY_PciDevice_InterruptSupport / InterruptMessageMaximum).</summary>
public sealed record InterruptCapabilities(bool SupportsLineBased, bool SupportsMsi, bool SupportsMsiX, uint MaxMessages);

public sealed record DeviceDriverInfo(string Provider, string Version, DateOnly? Date, string InfName, bool IsMicrosoftGeneric);

public sealed record DeviceInfo(
    string InstanceId,
    string FriendlyName,
    string ClassName,
    Guid ClassGuid,
    bool IsPresent,
    /// <summary>Código de problema de Device Manager (0 = OK; 10 = no arranca; 28 = sin driver; 43 = reportó falla; etc.).</summary>
    uint ProblemCode,
    DeviceDriverInfo? Driver,
    /// <summary>null si no es un dispositivo PCI o no se pudo leer.</summary>
    InterruptCapabilities? Interrupts);

public interface IDeviceManager
{
    IReadOnlyList<DeviceInfo> EnumerateDevices();

    DeviceInfo? GetDevice(string instanceId);

    /// <summary>Deshabilita y vuelve a habilitar el devnode para que tome la nueva config de interrupciones.
    /// Devuelve el estado del dispositivo después del reinicio. false en <c>Restarted</c> = requiere reiniciar la PC.</summary>
    DeviceRestartResult Restart(string instanceId, TimeSpan timeout);
}

/// <param name="Vetoed">Un programa con el dispositivo abierto no dejó reiniciarlo (CR_REMOVE_VETOED): no es que el driver no lo soporte.</param>
public sealed record DeviceRestartResult(bool Restarted, uint ProblemCodeAfter, string Detail, bool Vetoed = false)
{
    public bool IsHealthy => Restarted && ProblemCodeAfter == 0;
}

/// <summary>
/// Casilla "Permitir que el equipo apague este dispositivo para ahorrar energía" (WMI root\wmi MSPower_DeviceEnable).
/// Es la misma que muestra la pestaña Administración de energía del Administrador de dispositivos.
/// </summary>
public interface IDevicePowerManagement
{
    /// <summary>null si el dispositivo no expone la casilla.</summary>
    bool? GetAllowPowerOff(string instanceId);

    /// <summary>Lanza si el dispositivo no expone la casilla o WMI rechaza el cambio.</summary>
    void SetAllowPowerOff(string instanceId, bool allow);
}

// ---------------------------------------------------------------------------------------------
// Reloj (para timestamps testeables en el journal)
// ---------------------------------------------------------------------------------------------
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// Punto único de acceso al sistema que reciben las operaciones. En producción lo arma Infra;
/// en tests se arma con fakes.
/// </summary>
public interface ISystemContext
{
    IRegistry Registry { get; }
    IServiceManager Services { get; }
    IPowerSettings Power { get; }
    IBcdStore Bcd { get; }
    IProcessRunner Processes { get; }
    IDeviceManager Devices { get; }
    IDevicePowerManagement DevicePower { get; }
    IRestorePoint RestorePoints { get; }
    IClock Clock { get; }
}
