using KruzhLatency.Core.Debloat;

namespace KruzhLatency.Core.Tests;

public class HardwareAccelerationTests
{
    [Theory]
    [InlineData("""{"hardware_acceleration_mode":{"enabled":false},"browser":{}}""", false)]
    [InlineData("""{"hardware_acceleration_mode":{"enabled":true}}""", true)]
    [InlineData("""{"browser":{"enabled_labs_experiments":[]}}""", true)]   // sin la clave: default activada
    [InlineData("no es json", true)]                                         // ilegible: no se acusa a la app
    public void Chromium(string json, bool expected) => Assert.Equal(expected, HardwareAcceleration.ChromiumEnabled(json));

    [Theory]
    [InlineData("""{"enableHardwareAcceleration":false,"BACKGROUND_COLOR":"#202225"}""", false)]
    [InlineData("""{"enableHardwareAcceleration":true}""", true)]
    [InlineData("""{"IS_MAXIMIZED":true}""", true)]
    public void Discord(string json, bool expected) => Assert.Equal(expected, HardwareAcceleration.DiscordEnabled(json));
}
