namespace KruzhLatency.Core.Debloat;

/// <summary>
/// E4: tareas del Mantenimiento automático de Windows. Se ofrecen una por una (solo las que existen en la PC),
/// en lugar de desactivar a ciegas todo lo que tenga "Maintenance" en el nombre. Las de Windows Update
/// (UpdateOrchestrator) no están: las protege el sistema y apagarlas frena las actualizaciones.
/// </summary>
public static class MaintenanceTasks
{
    public const string RegistryKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance";
    public const string RegistryValue = "MaintenanceDisabled";

    public static readonly IReadOnlyList<(string Path, string Why)> All = new (string, string)[]
    {
        (@"\Microsoft\Windows\TaskScheduler\Regular Maintenance", "Dispara el mantenimiento diario cuando la PC está encendida."),
        (@"\Microsoft\Windows\TaskScheduler\Idle Maintenance", "Corre el mantenimiento cuando detecta la PC inactiva (puede arrancar con el juego en pausa)."),
        (@"\Microsoft\Windows\TaskScheduler\Maintenance Configurator", "Reprograma el mantenimiento y lo vuelve a activar."),
        (@"\Microsoft\Windows\TaskScheduler\Manual Maintenance", "Mantenimiento iniciado a mano desde Seguridad y mantenimiento."),
        (@"\Microsoft\Windows\Diagnosis\Scheduled", "Diagnósticos programados que corren dentro de la ventana de mantenimiento."),
    };

    public static string Name(string path) => path[(path.LastIndexOf('\\') + 1)..];
}
