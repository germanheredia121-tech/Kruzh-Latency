using KruzhLatency.Core.Abstractions;

namespace KruzhLatency.Core.Tests.Fakes;

public sealed class FakeServiceManager : IServiceManager
{
    private readonly Dictionary<string, ServiceSnapshot> _services = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Servicios que no admiten Stop (como algunos críticos de Windows).</summary>
    public HashSet<string> Unstoppable { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Install(string name, ServiceStartMode mode, ServiceRunState state = ServiceRunState.Running, bool delayed = false) =>
        _services[name] = new ServiceSnapshot(name, mode, delayed, state);

    public ServiceSnapshot? Get(string serviceName) => _services.TryGetValue(serviceName, out var s) ? s : null;

    public void SetStartMode(string serviceName, ServiceStartMode mode, bool delayedAutoStart)
    {
        var s = Get(serviceName) ?? throw new InvalidOperationException($"El servicio {serviceName} no existe.");
        _services[serviceName] = s with { StartMode = mode, DelayedAutoStart = delayedAutoStart };
    }

    public bool Stop(string serviceName, TimeSpan timeout)
    {
        var s = Get(serviceName);
        if (s is null || Unstoppable.Contains(serviceName)) return false;
        _services[serviceName] = s with { RunState = ServiceRunState.Stopped };
        return true;
    }

    public bool Start(string serviceName, TimeSpan timeout)
    {
        var s = Get(serviceName);
        if (s is null || s.StartMode == ServiceStartMode.Disabled) return false;
        _services[serviceName] = s with { RunState = ServiceRunState.Running };
        return true;
    }
}

public sealed class FakePowerSettings : IPowerSettings
{
    private readonly Dictionary<(Guid Scheme, Guid Sub, Guid Setting), uint> _values = new();
    private readonly HashSet<Guid> _schemes = new();
    private Guid _active;

    public FakePowerSettings(Guid activeScheme)
    {
        _schemes.Add(activeScheme);
        _active = activeScheme;
    }

    public Guid GetActiveScheme() => _active;

    public void SetActiveScheme(Guid scheme)
    {
        if (!_schemes.Contains(scheme)) throw new InvalidOperationException($"El esquema {scheme} no existe.");
        _active = scheme;
    }

    public bool SchemeExists(Guid scheme) => _schemes.Contains(scheme);

    public uint? ReadAcValue(Guid scheme, Guid subGroup, Guid setting) =>
        _values.TryGetValue((scheme, subGroup, setting), out var v) ? v : null;

    public void WriteAcValue(Guid scheme, Guid subGroup, Guid setting, uint value)
    {
        if (!_schemes.Contains(scheme)) throw new InvalidOperationException($"El esquema {scheme} no existe.");
        _values[(scheme, subGroup, setting)] = value;
    }

    public Guid DuplicateScheme(Guid source, string friendlyName)
    {
        if (!_schemes.Contains(source)) throw new InvalidOperationException($"El esquema {source} no existe.");
        var copy = Guid.NewGuid();
        _schemes.Add(copy);
        foreach (var kv in _values.Where(kv => kv.Key.Scheme == source).ToList())
        {
            _values[(copy, kv.Key.Sub, kv.Key.Setting)] = kv.Value;
        }
        return copy;
    }
}

public sealed class FakeBcdStore : IBcdStore
{
    private readonly Dictionary<BcdElement, BcdValue> _elements = new();

    public BcdValue? Read(BcdElement element) => _elements.TryGetValue(element, out var v) ? v : null;
    public void Write(BcdElement element, BcdValue value) => _elements[element] = value;
    public void Delete(BcdElement element) => _elements.Remove(element);
}

/// <summary>Runner con respuestas programadas por "archivo + argumentos". Registra todas las llamadas.</summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Dictionary<string, ProcessResult> _responses = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Calls { get; } = new();

    public void Respond(string commandLine, ProcessResult result) => _responses[commandLine] = result;

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        string commandLine = $"{fileName} {string.Join(' ', arguments)}".Trim();
        Calls.Add(commandLine);
        return Task.FromResult(_responses.TryGetValue(commandLine, out var r)
            ? r
            : new ProcessResult(1, string.Empty, $"FakeProcessRunner: sin respuesta programada para '{commandLine}'", false));
    }
}

public sealed class FakeDeviceManager : IDeviceManager
{
    private readonly Dictionary<string, DeviceInfo> _devices = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Si está definido, decide el código de problema tras cada reinicio (para simular combinaciones MSI que rompen el dispositivo).</summary>
    public Func<DeviceInfo, uint>? ProblemCodeAfterRestart { get; set; }

    /// <summary>Dispositivos que no se pueden reiniciar en caliente (ej. disco de booteo).</summary>
    public HashSet<string> NotHotRestartable { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Restarts { get; } = new();

    public void Add(DeviceInfo device) => _devices[device.InstanceId] = device;

    public IReadOnlyList<DeviceInfo> EnumerateDevices() => _devices.Values.ToList();

    public DeviceInfo? GetDevice(string instanceId) => _devices.TryGetValue(instanceId, out var d) ? d : null;

    public DeviceRestartResult Restart(string instanceId, TimeSpan timeout)
    {
        Restarts.Add(instanceId);
        var d = GetDevice(instanceId);
        if (d is null) return new DeviceRestartResult(false, 0, "No existe.");
        if (NotHotRestartable.Contains(instanceId)) return new DeviceRestartResult(false, d.ProblemCode, "Requiere reiniciar la PC.");

        uint code = ProblemCodeAfterRestart?.Invoke(d) ?? 0;
        _devices[instanceId] = d with { ProblemCode = code };
        return new DeviceRestartResult(true, code, code == 0 ? "OK" : $"Code {code}");
    }
}

public sealed class FakeRestorePoint : IRestorePoint
{
    public RestorePointResult NextResult { get; set; } = new(RestorePointOutcome.Created, "OK");
    public List<string> Requests { get; } = new();

    public Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken = default)
    {
        Requests.Add(description);
        return Task.FromResult(NextResult);
    }
}

public sealed class FakeDevicePower : IDevicePowerManagement
{
    private readonly Dictionary<string, bool> _allow = new(StringComparer.OrdinalIgnoreCase);

    public void Expose(string instanceId, bool allowPowerOff) => _allow[instanceId] = allowPowerOff;

    public bool? GetAllowPowerOff(string instanceId) => _allow.TryGetValue(instanceId, out var v) ? v : null;

    public void SetAllowPowerOff(string instanceId, bool allow)
    {
        if (!_allow.ContainsKey(instanceId)) throw new InvalidOperationException($"{instanceId} no expone la casilla de energía.");
        _allow[instanceId] = allow;
    }
}

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Windows en memoria. Cada test arma el estado inicial que necesita.</summary>
public sealed class FakeSystemContext : ISystemContext
{
    public static readonly Guid DefaultScheme = new("381b4222-f694-41f0-9685-ff5bb260df2e"); // Balanced

    public FakeRegistry FakeRegistry { get; } = new();
    public FakeServiceManager FakeServices { get; } = new();
    public FakePowerSettings FakePower { get; } = new(DefaultScheme);
    public FakeBcdStore FakeBcd { get; } = new();
    public FakeProcessRunner FakeProcesses { get; } = new();
    public FakeDeviceManager FakeDevices { get; } = new();
    public FakeDevicePower FakeDevicePower { get; } = new();
    public FakeRestorePoint FakeRestorePoints { get; } = new();
    public FakeClock FakeClock { get; } = new();

    public IRegistry Registry => FakeRegistry;
    public IServiceManager Services => FakeServices;
    public IPowerSettings Power => FakePower;
    public IBcdStore Bcd => FakeBcd;
    public IProcessRunner Processes => FakeProcesses;
    public IDeviceManager Devices => FakeDevices;
    public IDevicePowerManagement DevicePower => FakeDevicePower;
    public IRestorePoint RestorePoints => FakeRestorePoints;
    public IClock Clock => FakeClock;
}
