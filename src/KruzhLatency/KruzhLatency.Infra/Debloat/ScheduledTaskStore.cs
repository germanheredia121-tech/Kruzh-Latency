using System.Runtime.InteropServices;

namespace KruzhLatency.Infra.Debloat;

/// <summary>
/// Tareas programadas por la API COM del Programador de tareas (Schedule.Service): estado real sin parsear la salida
/// traducida de schtasks ("Disabled" / "Deshabilitado"…), que era lo que hacía la versión anterior.
/// </summary>
public static class ScheduledTaskStore
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(3);
    private static readonly Dictionary<string, (bool? Enabled, DateTime At)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>null si la tarea no existe en esta PC. Caché de 3 s: el refresco de estados no abre COM en cada vuelta.</summary>
    public static bool? IsEnabled(string taskPath)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(taskPath, out var c) && DateTime.UtcNow - c.At < CacheTtl) return c.Enabled;
        }
        dynamic? task = Find(taskPath);
        bool? value;
        try { value = task is null ? null : (bool)task.Enabled; }
        finally { Release(task); }
        lock (Cache) Cache[taskPath] = (value, DateTime.UtcNow);
        return value;
    }

    /// <summary>Lanza si la tarea no existe o Windows no deja cambiarla.</summary>
    public static void SetEnabled(string taskPath, bool enabled)
    {
        lock (Cache) Cache.Remove(taskPath);
        dynamic? task = Find(taskPath) ?? throw new InvalidOperationException($"La tarea {taskPath} no existe.");
        try { task.Enabled = enabled; }
        finally { Release(task); }
    }

    private static dynamic? Find(string taskPath)
    {
        int slash = taskPath.LastIndexOf('\\');
        string folder = slash <= 0 ? "\\" : taskPath[..slash];
        string name = taskPath[(slash + 1)..];

        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Programador de tareas no disponible.");
        dynamic service = Activator.CreateInstance(type)!;
        try
        {
            service.Connect();
            dynamic f = service.GetFolder(folder);
            try { return f.GetTask(name); }
            finally { Release(f); }
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
            return null; // carpeta o tarea inexistente (el binder COM lo traduce a FileNotFoundException) o sin permiso
        }
        finally
        {
            Release(service);
        }
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }
}
