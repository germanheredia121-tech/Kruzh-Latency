using KruzhLatency.Core.Abstractions;
using KruzhLatency.Core.Devices;
using KruzhLatency.Infra.Devices;
using KruzhLatency.Infra.Wmi;

namespace KruzhLatency.Infra.Tests;

/// <summary>
/// Solo corre con KRUZH_INTEGRATION=1 (y como administrador). Así `dotnet test` en la PC de desarrollo
/// no toca el sistema real sin que el usuario lo pida (CLAUDE.md).
/// </summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("KRUZH_INTEGRATION") != "1")
        {
            Skip = "Integración: definir KRUZH_INTEGRATION=1 para correr contra el Windows real.";
        }
    }
}

[Trait("Category", "Integration")]
public class InfraIntegrationTests
{
    // ------------------------------------------------------------ solo lectura

    [IntegrationFact]
    public void Inventario_ArbolRealCompleto()
    {
        var tree = new DeviceTree(new CfgMgrDeviceInventory().Snapshot());

        Assert.Single(tree.All, n => n.ParentInstanceId is null);
        Assert.Contains(tree.All, n => n.IsPresent && n.IsPci && n.ClassName == "Display");
        Assert.All(tree.All.Where(n => n.IsPresent && n.ParentInstanceId is not null), n => Assert.NotNull(tree.Parent(n)));
    }

    [IntegrationFact]
    public void CasillaDeEnergia_SeLeeEnAlgunDispositivoUsb()
    {
        var power = new WmiDevicePowerManagement();
        var usb = new CfgMgrDeviceInventory().Snapshot().Where(n => n.IsPresent && n.ClassName == "USB");

        Assert.Contains(usb, n => power.GetAllowPowerOff(n.InstanceId) is not null);
    }

    // ------------------------------------------------------------ escriben en el sistema

    [IntegrationFact]
    public async Task PuntoDeRestauracion_ResultadoReal()
    {
        var result = await new WmiRestorePoint().CreateAsync("KRUZH LATENCY - test de integracion");

        Assert.NotEqual(RestorePointOutcome.Failed, result.Outcome);
    }

    [IntegrationFact]
    public void CasillaDeEnergia_IdaYVuelta()
    {
        var power = new WmiDevicePowerManagement();
        var device = new CfgMgrDeviceInventory().Snapshot()
            .First(n => n.IsPresent && n.ClassName == "USB" && power.GetAllowPowerOff(n.InstanceId) is not null);
        bool original = power.GetAllowPowerOff(device.InstanceId)!.Value;
        try
        {
            power.SetAllowPowerOff(device.InstanceId, !original);
            Assert.Equal(!original, power.GetAllowPowerOff(device.InstanceId));
        }
        finally
        {
            power.SetAllowPowerOff(device.InstanceId, original);
        }
        Assert.Equal(original, power.GetAllowPowerOff(device.InstanceId));
    }
}

[Trait("Category", "Integration")]
public class DisplayCacheIntegrationTests
{
    [IntegrationFact]
    public void Export_GeneraUnRegValido_SinBorrarNada()
    {
        string file = Path.Combine(Path.GetTempPath(), $"kruzh-test-{Guid.NewGuid():N}.reg");
        try
        {
            KruzhLatency.Infra.Display.DisplayCache.Export("Configuration", file);
            Assert.StartsWith("Windows Registry Editor", File.ReadAllText(file).TrimStart('﻿'));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
