using KruzhLatency.Core.Abstractions;

namespace KruzhLatency.Core.Tests.Fakes;

/// <summary>Registro en memoria con la misma semántica que el real: claves case-insensitive, DeleteValue no borra la clave.</summary>
public sealed class FakeRegistry : IRegistry
{
    private readonly Dictionary<(RegHive, string), Dictionary<string, RegValue>> _keys = new(new KeyComparer());

    /// <summary>Cantidad de escrituras (Set + Delete) — útil para verificar idempotencia.</summary>
    public int WriteCount { get; private set; }

    public bool KeyExists(RegHive hive, string subKey) => _keys.ContainsKey((hive, Normalize(subKey)));

    public RegValue? GetValue(RegHive hive, string subKey, string valueName) =>
        _keys.TryGetValue((hive, Normalize(subKey)), out var values) && values.TryGetValue(valueName, out var v) ? v : null;

    public void SetValue(RegHive hive, string subKey, string valueName, RegValue value)
    {
        CreateKey(hive, subKey)[valueName] = value;
        WriteCount++;
    }

    public void DeleteValue(RegHive hive, string subKey, string valueName)
    {
        if (_keys.TryGetValue((hive, Normalize(subKey)), out var values))
        {
            values.Remove(valueName);
        }
        WriteCount++;
    }

    public IReadOnlyList<string> GetSubKeyNames(RegHive hive, string subKey)
    {
        string prefix = Normalize(subKey) + "\\";
        return _keys.Keys
            .Where(k => k.Item1 == hive && k.Item2.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Item2[prefix.Length..].Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> GetValueNames(RegHive hive, string subKey) =>
        _keys.TryGetValue((hive, Normalize(subKey)), out var values) ? values.Keys.ToList() : new List<string>();

    /// <summary>Crea una clave vacía (y sus padres), como haría un driver instalado.</summary>
    public Dictionary<string, RegValue> CreateKey(RegHive hive, string subKey)
    {
        string norm = Normalize(subKey);
        string[] parts = norm.Split('\\');
        for (int i = 1; i <= parts.Length; i++)
        {
            var path = string.Join('\\', parts.Take(i));
            if (!_keys.ContainsKey((hive, path)))
            {
                _keys[(hive, path)] = new Dictionary<string, RegValue>(StringComparer.OrdinalIgnoreCase);
            }
        }
        return _keys[(hive, norm)];
    }

    /// <summary>Copia profunda del estado completo, para comparar "antes" vs "después".</summary>
    public IReadOnlyDictionary<string, RegValue> Snapshot() =>
        _keys.SelectMany(k => k.Value.Select(v => ($"{k.Key.Item1}\\{k.Key.Item2}\\{v.Key}".ToUpperInvariant(), v.Value)))
             .ToDictionary(x => x.Item1, x => x.Value);

    private static string Normalize(string subKey) => subKey.Trim('\\');

    private sealed class KeyComparer : IEqualityComparer<(RegHive, string)>
    {
        public bool Equals((RegHive, string) x, (RegHive, string) y) =>
            x.Item1 == y.Item1 && string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((RegHive, string) obj) =>
            HashCode.Combine(obj.Item1, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2));
    }
}
