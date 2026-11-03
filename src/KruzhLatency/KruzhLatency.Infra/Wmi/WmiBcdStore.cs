using System.Management;
using KruzhLatency.Core.Abstractions;

namespace KruzhLatency.Infra.Wmi;

/// <summary>
/// {current} del BCD vía WMI (root\WMI BcdObject): independiente del idioma de Windows, a diferencia de bcdedit /enum.
/// Requiere administrador. Tipos de elemento según BcdOSLoaderElementTypes (Microsoft Learn); useplatformtick y
/// disabledynamictick no están en esa página y salen de la tabla de ReactOS / Geoff Chappell (0x260000A4 / 0x260000A5).
/// </summary>
public sealed class WmiBcdStore : IBcdStore
{
    private const string CurrentId = "{fa926493-6f1c-4193-a414-58f0b2456d1e}";

    private static readonly ManagementScope Scope = new(@"\\.\root\WMI") { Options = { EnablePrivileges = true } };

    public static uint TypeOf(BcdElement e) => e switch
    {
        BcdElement.UsePlatformClock => 0x260000A2,
        BcdElement.UsePlatformTick => 0x260000A4,
        BcdElement.DisableDynamicTick => 0x260000A5,
        BcdElement.TscSyncPolicy => 0x250000A6,
        BcdElement.HypervisorLaunchType => 0x250000F0,
        _ => throw new ArgumentOutOfRangeException(nameof(e))
    };

    /// <summary>Nombre de la opción de bcdedit (para mostrar y para el journal).</summary>
    public static string BcdEditName(BcdElement e) => e switch
    {
        BcdElement.UsePlatformClock => "useplatformclock",
        BcdElement.UsePlatformTick => "useplatformtick",
        BcdElement.DisableDynamicTick => "disabledynamictick",
        BcdElement.TscSyncPolicy => "tscsyncpolicy",
        BcdElement.HypervisorLaunchType => "hypervisorlaunchtype",
        _ => throw new ArgumentOutOfRangeException(nameof(e))
    };

    public static BcdElement? FromBcdEditName(string name) =>
        Enum.GetValues<BcdElement>().Cast<BcdElement?>().FirstOrDefault(e => string.Equals(BcdEditName(e!.Value), name, StringComparison.OrdinalIgnoreCase));

    private static bool IsBoolean(BcdElement e) => (TypeOf(e) & 0x0F000000) == 0x06000000;

    public BcdValue? Read(BcdElement element)
    {
        using var current = Current();
        using var input = current.GetMethodParameters("GetElement");
        input["Type"] = TypeOf(element);
        using var output = current.InvokeMethod("GetElement", input, null);
        if (output?["ReturnValue"] is not true || output["Element"] is not ManagementBaseObject el) return null; // no definido
        using (el)
        {
            return IsBoolean(element)
                ? BcdValue.Of(el["Boolean"] is true)
                : BcdValue.Of(Convert.ToUInt64(el["Integer"]));
        }
    }

    public void Write(BcdElement element, BcdValue value)
    {
        using var current = Current();
        bool boolean = IsBoolean(element);
        using var input = current.GetMethodParameters(boolean ? "SetBooleanElement" : "SetIntegerElement");
        input["Type"] = TypeOf(element);
        if (boolean) input["Boolean"] = value.Boolean ?? throw new ArgumentException("Se esperaba un booleano.", nameof(value));
        else input["Integer"] = value.Integer ?? throw new ArgumentException("Se esperaba un entero.", nameof(value));
        using var output = current.InvokeMethod(boolean ? "SetBooleanElement" : "SetIntegerElement", input, null);
        if (output?["ReturnValue"] is not true)
        {
            throw new InvalidOperationException($"BCD rechazó {BcdEditName(element)}.");
        }
    }

    public void Delete(BcdElement element)
    {
        if (Read(element) is null) return;
        using var current = Current();
        using var input = current.GetMethodParameters("DeleteElement");
        input["Type"] = TypeOf(element);
        using var output = current.InvokeMethod("DeleteElement", input, null);
        if (output?["ReturnValue"] is not true)
        {
            throw new InvalidOperationException($"BCD no pudo borrar {BcdEditName(element)}.");
        }
    }

    /// <summary>Valor en el formato que acepta bcdedit en cualquier idioma: yes/no o número.</summary>
    public static string Serialize(BcdValue v) => v.Boolean is { } b ? (b ? "yes" : "no") : v.Integer!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static BcdValue? Parse(BcdElement element, string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "<DELETED>") return null;
        // Journals de versiones anteriores guardaban el texto traducido de bcdedit ("Sí", "Oui"…).
        string[] yes = { "yes", "true", "1", "sí", "si", "oui", "ja", "sim", "да" };
        if (IsBoolean(element)) return BcdValue.Of(yes.Contains(text.Trim().ToLowerInvariant()));
        return ulong.TryParse(text.Trim(), out var n) ? BcdValue.Of(n) : null;
    }

    private static ManagementObject Current() =>
        new(Scope, new ManagementPath($"BcdObject.Id=\"{CurrentId}\",StoreFilePath=\"\""), null);
}
