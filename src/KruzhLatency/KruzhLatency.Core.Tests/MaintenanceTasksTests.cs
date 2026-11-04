using KruzhLatency.Core.Debloat;

namespace KruzhLatency.Core.Tests;

public class MaintenanceTasksTests
{
    [Fact]
    public void SoloTareasDeWindows_SinWindowsUpdate()
    {
        Assert.All(MaintenanceTasks.All, t => Assert.StartsWith(@"\Microsoft\Windows\", t.Path));
        Assert.DoesNotContain(MaintenanceTasks.All, t => t.Path.Contains("UpdateOrchestrator", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(MaintenanceTasks.All.Count, MaintenanceTasks.All.Select(t => MaintenanceTasks.Name(t.Path)).Distinct().Count());
    }

    [Fact]
    public void Nombre_EsLaUltimaParteDeLaRuta() =>
        Assert.Equal("Regular Maintenance", MaintenanceTasks.Name(@"\Microsoft\Windows\TaskScheduler\Regular Maintenance"));
}
