using KruzhLatency.Core.Debloat;

namespace KruzhLatency.Infra.Debloat;

/// <summary>Lee (sin modificar) la configuración de aceleración por hardware de las apps instaladas.</summary>
public static class HardwareAccelerationProbe
{
    public static IReadOnlyList<AccelApp> Detect()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        const string chromiumHow = "Configuración → Sistema → \"Usar aceleración de gráficos cuando esté disponible\" → activar y reiniciar el navegador.";

        var list = new List<AccelApp>();
        foreach (var (name, path) in new[]
                 {
                     ("Google Chrome", Path.Combine(local, @"Google\Chrome\User Data\Local State")),
                     ("Microsoft Edge", Path.Combine(local, @"Microsoft\Edge\User Data\Local State")),
                     ("Brave", Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data\Local State")),
                     ("Opera GX", Path.Combine(roaming, @"Opera Software\Opera GX Stable\Local State")),
                 })
        {
            if (Read(path) is { } json) list.Add(new AccelApp(name, HardwareAcceleration.ChromiumEnabled(json), chromiumHow));
        }

        if (Read(Path.Combine(roaming, @"discord\settings.json")) is { } discord)
        {
            list.Add(new AccelApp("Discord", HardwareAcceleration.DiscordEnabled(discord),
                "Configuración de usuario → Avanzado → \"Aceleración por hardware\" → activar (Discord se reinicia)."));
        }
        return list;
    }

    private static string? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            // FileShare.ReadWrite: la app puede estar abierta escribiéndolo.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
