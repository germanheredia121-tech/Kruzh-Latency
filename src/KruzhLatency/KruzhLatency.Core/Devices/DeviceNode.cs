using System.Text.Json;
using System.Text.Json.Serialization;
using KruzhLatency.Core.Abstractions;

namespace KruzhLatency.Core.Devices;

/// <summary>
/// Un devnode del árbol de PnP (presente o fantasma), con lo necesario para las reglas de spec 002:
/// relación padre/hijo, IDs de hardware, clase, ubicación en el bus, problema, driver y capacidades de interrupción.
/// </summary>
public sealed record DeviceNode(
    string InstanceId,
    /// <summary>null solo para la raíz (HTREE\ROOT\0).</summary>
    string? ParentInstanceId,
    string Description,
    string? FriendlyName,
    string ClassName,
    Guid ClassGuid,
    /// <summary>Enumerador/bus: PCI, USB, HID, ACPI, ROOT, SWD, DISPLAY…</summary>
    string Enumerator,
    IReadOnlyList<string> HardwareIds,
    IReadOnlyList<string> CompatibleIds,
    bool IsPresent,
    /// <summary>Deshabilitado en el Administrador de dispositivos (CONFIGFLAG_DISABLED o CM_PROB_DISABLED).</summary>
    bool IsDisabled,
    /// <summary>Código de problema (0 = OK; 22 = deshabilitado; 28 = sin driver; 43 = reportó falla; 45 = no conectado).</summary>
    uint ProblemCode,
    string? Service,
    string? Manufacturer,
    string? LocationInfo,
    uint? BusNumber,
    /// <summary>En PCI: (device &lt;&lt; 16) | function.</summary>
    uint? Address,
    DeviceDriverInfo? Driver,
    InterruptCapabilities? Interrupts)
{
    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(FriendlyName) ? Description : FriendlyName!;

    /// <summary>Número de dispositivo PCI (solo si <see cref="Enumerator"/> es PCI).</summary>
    [JsonIgnore] public uint? PciDevice => IsPci && Address is { } a ? a >> 16 : null;

    /// <summary>Número de función PCI (solo si <see cref="Enumerator"/> es PCI).</summary>
    [JsonIgnore] public uint? PciFunction => IsPci && Address is { } a ? a & 0xFFFF : null;

    [JsonIgnore] public bool IsPci => string.Equals(Enumerator, "PCI", StringComparison.OrdinalIgnoreCase);

    public bool HasHardwareIdPrefix(string prefix) =>
        HardwareIds.Any(h => h.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public DeviceInfo ToDeviceInfo() =>
        new(InstanceId, DisplayName, ClassName, ClassGuid, IsPresent, ProblemCode, Driver, Interrupts);
}

/// <summary>Inventario completo de dispositivos (presentes + fantasmas). Solo lectura.</summary>
public interface IDeviceInventory
{
    IReadOnlyList<DeviceNode> Snapshot();
}

/// <summary>Árbol navegable de devnodes (padres, hijos, ancestros) armado desde una foto del inventario.</summary>
public sealed class DeviceTree
{
    private readonly Dictionary<string, DeviceNode> _byId;
    private readonly Dictionary<string, List<DeviceNode>> _children;

    public DeviceTree(IEnumerable<DeviceNode> nodes)
    {
        _byId = new Dictionary<string, DeviceNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes) _byId[n.InstanceId] = n;

        _children = new Dictionary<string, List<DeviceNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in _byId.Values)
        {
            if (n.ParentInstanceId is null) continue;
            if (!_children.TryGetValue(n.ParentInstanceId, out var list))
            {
                _children[n.ParentInstanceId] = list = new List<DeviceNode>();
            }
            list.Add(n);
        }
    }

    public IReadOnlyCollection<DeviceNode> All => _byId.Values;

    public DeviceNode? Get(string instanceId) => _byId.TryGetValue(instanceId, out var n) ? n : null;

    public DeviceNode? Parent(DeviceNode node) => node.ParentInstanceId is { } p ? Get(p) : null;

    public IReadOnlyList<DeviceNode> Children(DeviceNode node) =>
        _children.TryGetValue(node.InstanceId, out var list) ? list : Array.Empty<DeviceNode>();

    /// <summary>Padre, abuelo… hasta la raíz. Corta ante ciclos (datos corruptos).</summary>
    public IEnumerable<DeviceNode> Ancestors(DeviceNode node)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { node.InstanceId };
        for (var p = Parent(node); p is not null && seen.Add(p.InstanceId); p = Parent(p))
        {
            yield return p;
        }
    }

    public IEnumerable<DeviceNode> Descendants(DeviceNode node)
    {
        var stack = new Stack<DeviceNode>(Children(node));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!seen.Add(n.InstanceId)) continue;
            yield return n;
            foreach (var c in Children(n)) stack.Push(c);
        }
    }

    /// <summary>Funciones PCI hermanas: mismo bus y mismo número de dispositivo, distinta función (ej. GPU fn0 + audio HDMI fn1).</summary>
    public IEnumerable<DeviceNode> PciSiblings(DeviceNode node) =>
        node.IsPci && node.BusNumber is { } bus && node.PciDevice is { } dev
            ? _byId.Values.Where(n => n.IsPci && n.IsPresent && n.BusNumber == bus && n.PciDevice == dev &&
                                      !string.Equals(n.InstanceId, node.InstanceId, StringComparison.OrdinalIgnoreCase))
            : Enumerable.Empty<DeviceNode>();

    // ------------------------------------------------------------ JSON (fixtures)

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string ToJson(IEnumerable<DeviceNode> nodes) =>
        JsonSerializer.Serialize(nodes.OrderBy(n => n.InstanceId, StringComparer.OrdinalIgnoreCase).ToList(), JsonOptions);

    public static DeviceTree FromJson(string json) =>
        new(JsonSerializer.Deserialize<List<DeviceNode>>(json, JsonOptions) ?? new List<DeviceNode>());
}
