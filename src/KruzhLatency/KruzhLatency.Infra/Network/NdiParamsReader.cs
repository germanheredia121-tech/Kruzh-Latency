using KruzhLatency.Core.Network;
using Microsoft.Win32;

namespace KruzhLatency.Infra.Network;

/// <summary>Lee las opciones avanzadas de una placa de red: Ndi\Params (definición del INF) + valor actual en su clave de clase.</summary>
public static class NdiParamsReader
{
    /// <param name="classKeyPath">Ruta relativa a HKLM: SYSTEM\CurrentControlSet\Control\Class\{4d36e972-…}\00XX.</param>
    public static NicAdvancedProperties? Read(string classKeyPath)
    {
        using var root = Registry.LocalMachine.OpenSubKey(classKeyPath);
        using var paramsKey = root?.OpenSubKey(@"Ndi\Params");
        if (root is null || paramsKey is null) return null;

        var list = new List<NdiParam>();
        foreach (var keyword in paramsKey.GetSubKeyNames())
        {
            using var p = paramsKey.OpenSubKey(keyword);
            if (p is null) continue;
            Dictionary<string, string>? values = null;
            using (var e = p.OpenSubKey("Enum"))
            {
                if (e is not null)
                {
                    values = e.GetValueNames().ToDictionary(v => v, v => e.GetValue(v)?.ToString() ?? "", StringComparer.Ordinal);
                }
            }
            list.Add(new NdiParam(keyword, S(p, "ParamDesc"), S(p, "type"), S(p, "default"), S(p, "min"), S(p, "max"), S(p, "step"),
                values, S(root, keyword)));
        }

        return new NicAdvancedProperties(S(root, "DriverDesc") ?? "", S(root, "DriverVersion"), S(root, "ComponentId") ?? "", list);
    }

    private static string? S(RegistryKey k, string name) => k.GetValue(name)?.ToString();
}
