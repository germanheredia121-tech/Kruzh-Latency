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

    /// <summary>Crea o reemplaza una tarea desde su XML (crea la carpeta si falta). Corre con el token del usuario conectado.</summary>
    public static void Register(string taskPath, string xml)
    {
        lock (Cache) Cache.Remove(taskPath);
        int slash = taskPath.LastIndexOf('\\');
        string folder = slash <= 0 ? "\\" : taskPath[..slash];
        string name = taskPath[(slash + 1)..];

        dynamic service = Connect();
        dynamic? root = null, f = null, registered = null;
        try
        {
            root = service.GetFolder("\\");
            try { f = service.GetFolder(folder); }
            catch (Exception ex) when (ex is COMException or IOException) { f = root.CreateFolder(folder, Type.Missing); }
            const int TASK_CREATE_OR_UPDATE = 6, TASK_LOGON_INTERACTIVE_TOKEN = 3;
            registered = f.RegisterTask(name, xml, TASK_CREATE_OR_UPDATE, null, null, TASK_LOGON_INTERACTIVE_TOKEN, Type.Missing);
        }
        finally
        {
            Release(registered); Release(f); Release(root); Release(service);
        }
    }

    /// <summary>Arranca la tarea ahora (no espera). Lanza si no existe.</summary>
    public static void Run(string taskPath)
    {
        dynamic? task = Find(taskPath) ?? throw new InvalidOperationException($"La tarea {taskPath} no existe.");
        dynamic? running = null;
        try { running = task.Run(null); }
        finally { Release(running); Release(task); }
    }

    /// <summary>Detiene las instancias en curso y borra la tarea. No hace nada si no existe.</summary>
    public static void Delete(string taskPath)
    {
        lock (Cache) Cache.Remove(taskPath);
        dynamic? task = Find(taskPath);
        if (task is null) return;
        try { task.Stop(0); }
        catch (COMException) { /* no estaba corriendo */ }
        finally { Release(task); }

        int slash = taskPath.LastIndexOf('\\');
        dynamic service = Connect();
        dynamic? f = null;
        try
        {
            f = service.GetFolder(slash <= 0 ? "\\" : taskPath[..slash]);
            f.DeleteTask(taskPath[(slash + 1)..], 0);
        }
        finally
        {
            Release(f); Release(service);
        }
    }

    /// <summary>Rutas completas de las tareas de una carpeta (incluidas las ocultas); vacío si la carpeta no existe.</summary>
    public static IReadOnlyList<string> List(string folderPath, bool recursive)
    {
        var result = new List<string>();
        dynamic? service = null;
        try
        {
            service = Connect();
            Collect(service.GetFolder(folderPath), recursive, result);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
            // carpeta inexistente o sin permiso
        }
        finally
        {
            Release(service);
        }
        return result;
    }

    private static void Collect(dynamic folder, bool recursive, List<string> result)
    {
        dynamic? tasks = null, folders = null;
        try
        {
            const int TASK_ENUM_HIDDEN = 1;
            tasks = folder.GetTasks(TASK_ENUM_HIDDEN);
            foreach (dynamic t in tasks)
            {
                try { result.Add((string)t.Path); }
                finally { Release(t); }
            }
            if (!recursive) return;
            folders = folder.GetFolders(0);
            foreach (dynamic sub in folders)
            {
                try { Collect(sub, true, result); }
                finally { Release(sub); }
            }
        }
        finally
        {
            Release(tasks); Release(folders); Release(folder);
        }
    }

    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Programador de tareas no disponible.");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }
}
