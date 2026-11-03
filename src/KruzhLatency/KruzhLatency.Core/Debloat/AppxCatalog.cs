using KruzhLatency.Core.Tweaks;

namespace KruzhLatency.Core.Debloat;

/// <summary>Un paquete AppX instalado para el usuario (lo que devuelve PackageManager).</summary>
public sealed record InstalledAppx(
    string Name,
    string FamilyName,
    string FullName,
    bool IsFramework,
    /// <summary>Firmado como parte del sistema (no se puede quitar).</summary>
    bool IsSystem);

/// <summary>Regla del catálogo: qué paquetes (por prefijo del nombre del paquete, nunca por nombre visible) y desde qué perfil.</summary>
public sealed record AppxRule(
    string Id,
    string Title,
    string Section,
    string Why,
    Profile From,
    IReadOnlyList<string> NamePrefixes,
    string? Warning = null);

public sealed record AppxRecommendation(AppxRule Rule, IReadOnlyList<InstalledAppx> Packages);

/// <summary>
/// T41 (U13): apps que no hacen falta para jugar. Quitar por usuario es reversible (el paquete sigue aprovisionado y se
/// vuelve a registrar); desaprovisionar (que no vuelvan con usuarios nuevos o actualizaciones) es solo Entusiasta.
/// Nunca se tocan la Store, el instalador, runtimes, códecs, seguridad ni paneles de drivers (<see cref="IsProtected"/>).
/// </summary>
public static class AppxCatalog
{
    public const string MicrosoftApps = "Apps de Microsoft que no hacen falta para jugar";
    public const string Assistant = "Asistentes y widgets";
    public const string Xbox = "Xbox";
    public const string ThirdParty = "Apps preinstaladas de terceros";

    public static readonly IReadOnlyList<AppxRule> Rules = new AppxRule[]
    {
        new("News", "Noticias", MicrosoftApps, "Feed de noticias de Bing con actualización en segundo plano.", Profile.Normal, new[] { "Microsoft.BingNews" }),
        new("Weather", "El tiempo", MicrosoftApps, "App del clima con actualización en segundo plano.", Profile.Normal, new[] { "Microsoft.BingWeather" }),
        new("BingSearch", "Búsqueda de Bing", MicrosoftApps, "Integra resultados web de Bing en la búsqueda de Windows.", Profile.Competitivo, new[] { "Microsoft.BingSearch" }),
        new("GetHelp", "Obtener ayuda", MicrosoftApps, "Asistente de soporte de Microsoft.", Profile.Competitivo, new[] { "Microsoft.GetHelp" }),
        new("Tips", "Sugerencias (Tips)", MicrosoftApps, "Muestra consejos y notificaciones de Windows.", Profile.Normal, new[] { "Microsoft.Getstarted" }),
        new("Solitaire", "Microsoft Solitaire Collection", MicrosoftApps, "Juego preinstalado con publicidad.", Profile.Normal, new[] { "Microsoft.MicrosoftSolitaireCollection" }),
        new("OfficeHub", "Microsoft 365 (Office Hub)", MicrosoftApps, "Lanzador de Office que promociona la suscripción.", Profile.Normal, new[] { "Microsoft.MicrosoftOfficeHub" }),
        new("People", "Contactos", MicrosoftApps, "App de contactos que casi no se usa.", Profile.Normal, new[] { "Microsoft.People" }),
        new("FeedbackHub", "Centro de opiniones", MicrosoftApps, "Envía comentarios y diagnósticos a Microsoft.", Profile.Normal, new[] { "Microsoft.WindowsFeedbackHub" }),
        new("Maps", "Mapas", MicrosoftApps, "Mapas sin conexión con descargas en segundo plano.", Profile.Normal, new[] { "Microsoft.WindowsMaps" }),
        new("MoviesTv", "Películas y TV", MicrosoftApps, "Reproductor de la tienda de películas.", Profile.Competitivo, new[] { "Microsoft.ZuneVideo" }),
        new("Clipchamp", "Clipchamp", MicrosoftApps, "Editor de video preinstalado.", Profile.Normal, new[] { "Clipchamp.Clipchamp" }),
        new("Todo", "Microsoft To Do", MicrosoftApps, "Lista de tareas con sincronización.", Profile.Competitivo, new[] { "Microsoft.Todos" }),
        new("PowerAutomate", "Power Automate", MicrosoftApps, "Automatización de escritorio.", Profile.Normal, new[] { "Microsoft.PowerAutomateDesktop" }),
        new("MixedReality", "Portal de realidad mixta / Visor 3D", MicrosoftApps, "Sin visor de realidad mixta no se usan.", Profile.Normal, new[] { "Microsoft.MixedReality.Portal", "Microsoft.Microsoft3DViewer", "Microsoft.Print3D" }),
        new("Skype", "Skype", MicrosoftApps, "Discontinuado.", Profile.Normal, new[] { "Microsoft.SkypeApp" }),
        new("DevHome", "Dev Home", MicrosoftApps, "Panel para desarrolladores.", Profile.Competitivo, new[] { "Microsoft.Windows.DevHome" }),
        new("QuickAssist", "Asistencia rápida", MicrosoftApps, "Control remoto de soporte.", Profile.Profesional, new[] { "MicrosoftCorporationII.QuickAssist" },
            "Si alguien te da soporte remoto con esta app, no la quites."),
        new("Outlook", "Outlook (nuevo)", MicrosoftApps, "Cliente de correo con sincronización en segundo plano.", Profile.Profesional, new[] { "Microsoft.OutlookForWindows" },
            "Si usás Outlook para el correo, no lo quites."),
        new("PhoneLink", "Vincular teléfono", MicrosoftApps, "Sincroniza el celular en segundo plano.", Profile.Profesional, new[] { "Microsoft.YourPhone", "MicrosoftWindows.CrossDevice" },
            "Si usás tu celular desde la PC, no lo quites."),
        new("Recorders", "Alarmas y Grabadora de sonido", MicrosoftApps, "Apps accesorias.", Profile.Profesional, new[] { "Microsoft.WindowsAlarms", "Microsoft.WindowsSoundRecorder" }),
        new("Teams", "Microsoft Teams (personal)", MicrosoftApps, "Se inicia con Windows y queda en segundo plano.", Profile.Competitivo, new[] { "MSTeams", "MicrosoftTeams" },
            "Si usás Teams, no lo quites."),

        new("Cortana", "Cortana", Assistant, "Asistente discontinuado.", Profile.Normal, new[] { "Microsoft.549981C3F5F10" }),
        new("Copilot", "Copilot (app)", Assistant, "Asistente de IA que puede quedar en segundo plano.", Profile.Competitivo, new[] { "Microsoft.Copilot", "Microsoft.Windows.Ai.Copilot.Provider" }),
        new("Widgets", "Widgets (Web Experience Pack)", Assistant, "El panel de widgets y noticias de la barra de tareas; corre en segundo plano.", Profile.Competitivo, new[] { "MicrosoftWindows.Client.WebExperience" },
            "Desaparecen los widgets de la barra de tareas."),

        new("XboxApps", "Apps de Xbox y Game Bar", Xbox, "Game Bar y la app de Xbox: capturas, overlay y Game Pass. CS2 y VALORANT no los usan.", Profile.Profesional,
            new[] { "Microsoft.GamingApp", "Microsoft.XboxGamingOverlay", "Microsoft.XboxApp", "Microsoft.XboxGameOverlay", "Microsoft.XboxSpeechToTextOverlay", "Microsoft.Xbox.TCUI" },
            "Si jugás con Game Pass o grabás con Game Bar (Win + G), no lo quites."),

        new("ThirdParty", "Apps de terceros preinstaladas", ThirdParty, "Apps que vienen con Windows u OEM por acuerdos comerciales (TikTok, Spotify preinstalado, Candy Crush, Disney+, Prime Video, Facebook…).", Profile.Normal,
            new[] { "BytedancePte.Ltd.TikTok", "SpotifyAB.SpotifyMusic", "king.com.", "Disney.", "AmazonVideo.PrimeVideo", "Facebook.", "FACEBOOK.", "Instagram.", "Twitter.", "Netflix" },
            "Si usás alguna de estas apps instaladas desde la Store, desmarcalas: se quitan todas las del grupo."),
    };

    /// <summary>Nunca se quitan, aunque alguna regla los nombrara por error.</summary>
    private static readonly string[] ProtectedPrefixes =
    {
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.VCLibs", "Microsoft.UI.Xaml",
        "Microsoft.NET.", "Microsoft.WindowsAppRuntime", "Microsoft.SecHealthUI", "Microsoft.WindowsTerminal", "Microsoft.WindowsNotepad",
        "Microsoft.Windows.Photos", "Microsoft.WindowsCalculator", "Microsoft.Paint", "Microsoft.ScreenSketch", "Microsoft.HEIFImageExtension",
        "Microsoft.HEVCVideoExtension", "Microsoft.VP9VideoExtensions", "Microsoft.WebMediaExtensions", "Microsoft.AV1VideoExtension",
        "Microsoft.RawImageExtension", "Microsoft.WebpImageExtension", "Microsoft.MPEG2VideoExtension", "Microsoft.XboxIdentityProvider",
        "NVIDIACorp.", "AdvancedMicroDevicesInc", "RealtekSemiconductorCorp", "AppUp.Intel", "Microsoft.Windows.ShellExperienceHost",
        "Microsoft.Windows.StartMenuExperienceHost", "MicrosoftWindows.Client.CBS", "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl",
        "Microsoft.LockApp", "Microsoft.Windows.CloudExperienceHost", "Microsoft.Windows.SecHealthUI", "Microsoft.MicrosoftEdge",
    };

    public static bool IsProtected(string packageName) =>
        ProtectedPrefixes.Any(p => packageName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<AppxRecommendation> Plan(IEnumerable<InstalledAppx> installed)
    {
        var removable = installed.Where(p => !p.IsFramework && !p.IsSystem && !IsProtected(p.Name)).ToList();
        return Rules
            .Select(r => new AppxRecommendation(r, removable
                .Where(p => r.NamePrefixes.Any(prefix => p.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                .DistinctBy(p => p.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList()))
            .Where(rec => rec.Packages.Count > 0)
            .ToList();
    }
}

/// <summary>Tareas programadas de telemetría y diagnóstico (T41). Las que no existen en la PC se ignoran.</summary>
public static class TelemetryTasks
{
    public static readonly IReadOnlyList<(string Path, string Why)> All = new (string, string)[]
    {
        (@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser", "Analiza compatibilidad de apps y envía datos de telemetría (picos de CPU y disco)."),
        (@"\Microsoft\Windows\Application Experience\ProgramDataUpdater", "Recolecta datos de programas para el Programa de Mejora."),
        (@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator", "Consolida y envía datos del Programa de Mejora de la Experiencia del Cliente."),
        (@"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip", "Recolecta estadísticas de uso de USB."),
        (@"\Microsoft\Windows\Autochk\Proxy", "Recolecta datos de chkdsk para el Programa de Mejora."),
        (@"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector", "Envía información de diagnóstico de discos."),
        (@"\Microsoft\Windows\Feedback\Siuf\DmClient", "Encuestas y comentarios de Windows."),
        (@"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload", "Encuestas y comentarios de Windows."),
        (@"\Microsoft\Windows\Windows Error Reporting\QueueReporting", "Envía en segundo plano los informes de errores en cola."),
    };
}
