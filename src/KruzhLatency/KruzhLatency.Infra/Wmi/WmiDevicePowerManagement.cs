using System.Management;
using KruzhLatency.Core.Abstractions;

namespace KruzhLatency.Infra.Wmi;

/// <summary>
/// root\wmi MSPower_DeviceEnable: la casilla "Permitir que el equipo apague este dispositivo para ahorrar energía".
/// La instancia WMI se llama como el devnode + "_0" (ej. USB\VID_046D&amp;PID_C54D\5&amp;...&amp;0&amp;1_0).
/// </summary>
public sealed class WmiDevicePowerManagement : IDevicePowerManagement
{
    private static readonly ManagementScope Scope = new(@"\\.\root\wmi");

    public bool? GetAllowPowerOff(string instanceId)
    {
        using var obj = Find(instanceId);
        return obj?["Enable"] is bool enabled ? enabled : null;
    }

    public void SetAllowPowerOff(string instanceId, bool allow)
    {
        using var obj = Find(instanceId)
            ?? throw new InvalidOperationException($"{instanceId} no expone la casilla de ahorro de energía (MSPower_DeviceEnable).");
        obj["Enable"] = allow;
        obj.Put();
    }

    private static ManagementObject? Find(string instanceId)
    {
        // WQL: la barra invertida y la comilla simple se escapan con barra invertida; la comparación no distingue mayúsculas.
        string name = (instanceId + "_0").Replace(@"\", @"\\").Replace("'", @"\'");
        using var searcher = new ManagementObjectSearcher(Scope,
            new ObjectQuery($"SELECT * FROM MSPower_DeviceEnable WHERE InstanceName = '{name}'"));
        try
        {
            return searcher.Get().Cast<ManagementObject>().FirstOrDefault();
        }
        catch (ManagementException)
        {
            return null; // la clase no existe si ningún driver la registra
        }
    }
}
