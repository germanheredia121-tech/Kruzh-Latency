using System.Text.Json;
using System.Text.Json.Serialization;

namespace KruzhLatency.Core.Journal;

/// <summary>
/// Valor original de UNA cosa que KRUZH cambió. <see cref="Kind"/> se guarda por nombre (no por número de enum) para que
/// agregar o reordenar tipos nunca rompa un journal existente.
/// </summary>
public sealed record JournalEntry
{
    public required string TweakId { get; init; }
    /// <summary>Identidad de la operación dentro del tweak: tipo + destino + valor. Clave del baseline junto con TweakId.</summary>
    public required string OperationId { get; init; }
    public required string Kind { get; init; }
    public string TargetPath { get; init; } = "";
    public string ValueName { get; init; } = "";
    public bool PreviousValueExisted { get; init; }
    public string ValueKind { get; init; } = "";
    public string? PreviousValue { get; init; }
    public string? AppliedValue { get; init; }
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    public static string MakeOperationId(string kind, string targetPath, string valueName) =>
        $"{kind}|{targetPath}|{valueName}".ToLowerInvariant();
}

public sealed class JournalDocument
{
    public const int CurrentSchema = 2;

    public int SchemaVersion { get; set; } = CurrentSchema;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>tweakId → valores originales (uno por operación). Solo se escribe la primera vez.</summary>
    public Dictionary<string, List<JournalEntry>> Baselines { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Historial de cambios (para mostrar), acotado.</summary>
    public List<JournalEntry> History { get; set; } = new();
}

/// <summary>Cómo se cargó el journal (para avisar al usuario si hubo que recuperar).</summary>
public enum JournalLoadOutcome { New, Loaded, RecoveredFromBackup, CorruptStartedEmpty }

/// <summary>
/// Journal v2 (T11, PLAN §4, arregla B14): escritura atómica (journal.tmp → File.Replace con journal.bak), baseline único
/// por (tweakId, operationId) y recuperación de corrupción (si journal.json está roto se usa journal.bak; si los dos
/// están rotos se conserva una copia y se arranca vacío, avisando). Thread-safe.
/// </summary>
public sealed class JournalStore
{
    public const int MaxHistory = 1500;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _sync = new();
    private readonly string _path;
    private JournalDocument _doc;

    public JournalStore(string path)
    {
        _path = path;
        (_doc, LoadOutcome) = Load(path);
    }

    public string Path => _path;
    public JournalLoadOutcome LoadOutcome { get; }

    // ------------------------------------------------------------ lectura

    public IReadOnlyList<JournalEntry> Baselines(string tweakId)
    {
        lock (_sync) return _doc.Baselines.TryGetValue(tweakId, out var l) ? l.ToList() : Array.Empty<JournalEntry>();
    }

    public IReadOnlyDictionary<string, IReadOnlyList<JournalEntry>> AllBaselines()
    {
        lock (_sync) return _doc.Baselines.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => (IReadOnlyList<JournalEntry>)kv.Value.ToList());
    }

    public IReadOnlyList<JournalEntry> History()
    {
        lock (_sync) return _doc.History.ToList();
    }

    public bool HasBaseline(string tweakId)
    {
        lock (_sync) return _doc.Baselines.TryGetValue(tweakId, out var l) && l.Count > 0;
    }

    // ------------------------------------------------------------ escritura (cada cambio se persiste antes de volver)

    /// <summary>Guarda el valor original si es la primera vez para (tweakId, operationId). Siempre lo agrega al historial.</summary>
    /// <returns>true si se guardó como baseline nuevo; false si ya había uno (no se pisa el original).</returns>
    public bool RecordBaseline(JournalEntry entry)
    {
        lock (_sync)
        {
            if (!_doc.Baselines.TryGetValue(entry.TweakId, out var list))
            {
                _doc.Baselines[entry.TweakId] = list = new List<JournalEntry>();
            }
            bool isNew = !list.Any(e => e.OperationId == entry.OperationId);
            if (isNew) list.Add(entry);

            _doc.History.Add(entry);
            if (_doc.History.Count > MaxHistory) _doc.History.RemoveRange(0, _doc.History.Count - MaxHistory);
            SaveLocked();
            return isNew;
        }
    }

    /// <summary>Quita los baselines de un tweak (después de revertirlo).</summary>
    public void RemoveTweak(string tweakId)
    {
        lock (_sync)
        {
            if (_doc.Baselines.Remove(tweakId)) SaveLocked();
        }
    }

    /// <summary>Quita baselines puntuales (ej. Rescate: solo los de dispositivos).</summary>
    public int RemoveEntries(Func<JournalEntry, bool> predicate)
    {
        lock (_sync)
        {
            int removed = 0;
            foreach (var key in _doc.Baselines.Keys.ToList())
            {
                removed += _doc.Baselines[key].RemoveAll(e => predicate(e));
                if (_doc.Baselines[key].Count == 0) _doc.Baselines.Remove(key);
            }
            if (removed > 0) SaveLocked();
            return removed;
        }
    }

    /// <summary>Quita tweaks enteros por id (ej. entradas "*_Rev" de la v3).</summary>
    public int RemoveTweaks(Func<string, bool> tweakIdPredicate)
    {
        lock (_sync)
        {
            var keys = _doc.Baselines.Keys.Where(tweakIdPredicate).ToList();
            foreach (var k in keys) _doc.Baselines.Remove(k);
            if (keys.Count > 0) SaveLocked();
            return keys.Count;
        }
    }

    /// <summary>Importa entradas de otra versión (migración). Respeta la unicidad del baseline.</summary>
    public int Import(IEnumerable<JournalEntry> baselines, IEnumerable<JournalEntry> history)
    {
        lock (_sync)
        {
            int added = 0;
            foreach (var e in baselines)
            {
                if (!_doc.Baselines.TryGetValue(e.TweakId, out var list)) _doc.Baselines[e.TweakId] = list = new List<JournalEntry>();
                if (list.Any(x => x.OperationId == e.OperationId)) continue;
                list.Add(e);
                added++;
            }
            _doc.History.InsertRange(0, history);
            if (_doc.History.Count > MaxHistory) _doc.History.RemoveRange(0, _doc.History.Count - MaxHistory);
            SaveLocked();
            return added;
        }
    }

    // ------------------------------------------------------------ persistencia atómica

    private void SaveLocked()
    {
        _doc.UpdatedAtUtc = DateTime.UtcNow;
        _doc.SchemaVersion = JournalDocument.CurrentSchema;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        string tmp = _path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(fs, _doc, Json);
            fs.Flush(flushToDisk: true);
        }
        if (File.Exists(_path)) File.Replace(tmp, _path, _path + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, _path);
    }

    internal static (JournalDocument Doc, JournalLoadOutcome Outcome) Load(string path)
    {
        if (!File.Exists(path) && !File.Exists(path + ".bak")) return (new JournalDocument(), JournalLoadOutcome.New);

        if (TryRead(path) is { } doc) return (doc, JournalLoadOutcome.Loaded);
        if (TryRead(path + ".bak") is { } bak) return (bak, JournalLoadOutcome.RecoveredFromBackup);

        // Los dos rotos: se conserva la evidencia y se arranca vacío (la app lo avisa).
        try
        {
            if (File.Exists(path)) File.Copy(path, $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
        }
        catch (IOException) { }
        return (new JournalDocument(), JournalLoadOutcome.CorruptStartedEmpty);
    }

    private static JournalDocument? TryRead(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var doc = JsonSerializer.Deserialize<JournalDocument>(File.ReadAllText(file), Json);
            return doc is { SchemaVersion: >= 2 } ? doc : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }
}
