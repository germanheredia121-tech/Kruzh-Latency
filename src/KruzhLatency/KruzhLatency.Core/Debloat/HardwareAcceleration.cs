using System.Text.Json;

namespace KruzhLatency.Core.Debloat;

/// <summary>Una app de fondo que usa la GPU para dibujar y decodificar video si tiene la aceleración por hardware activada.</summary>
public sealed record AccelApp(string Name, bool Enabled, string HowToEnable);

/// <summary>
/// Aceleración por hardware de navegadores Chromium y Discord. Con la CPU como límite (CS2/VALORANT a cientos de FPS),
/// apagarla es contraproducente: la interfaz y el video pasan a la CPU y compiten con el juego, mientras la GPU tiene margen
/// y el video se decodifica en su motor dedicado (NVDEC/VCN). Solo lectura: KRUZH no edita archivos de otras apps.
/// </summary>
public static class HardwareAcceleration
{
    /// <summary>Chromium ("Local State"): hardware_acceleration_mode.enabled; si falta, está activada (default).</summary>
    public static bool ChromiumEnabled(string localStateJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(localStateJson);
            return !(doc.RootElement.TryGetProperty("hardware_acceleration_mode", out var mode) &&
                     mode.ValueKind == JsonValueKind.Object &&
                     mode.TryGetProperty("enabled", out var enabled) &&
                     enabled.ValueKind == JsonValueKind.False);
        }
        catch (JsonException)
        {
            return true; // archivo ilegible: no se acusa a la app
        }
    }

    /// <summary>Discord (settings.json): enableHardwareAcceleration; si falta, está activada (default).</summary>
    public static bool DiscordEnabled(string settingsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            return !(doc.RootElement.TryGetProperty("enableHardwareAcceleration", out var v) && v.ValueKind == JsonValueKind.False);
        }
        catch (JsonException)
        {
            return true;
        }
    }
}
