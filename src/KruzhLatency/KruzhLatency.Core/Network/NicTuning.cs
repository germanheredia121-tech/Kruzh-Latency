using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KruzhLatency.Core.Network;

/// <summary>Una opción avanzada de la placa de red tal como la declara su INF (Ndi\Params\&lt;keyword&gt;) + su valor actual.</summary>
public sealed record NdiParam(
    string Keyword,
    string? ParamDesc,
    string? Type,
    string? Default,
    string? Min,
    string? Max,
    string? Step,
    IReadOnlyDictionary<string, string>? Enum,
    string? Current);

/// <summary>Opciones avanzadas de una placa de red (lo que muestra la pestaña "Opciones avanzadas").</summary>
public sealed record NicAdvancedProperties(
    string DriverDesc,
    string? DriverVersion,
    string ComponentId,
    [property: JsonPropertyName("params")] IReadOnlyList<NdiParam> Params)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static NicAdvancedProperties FromJson(string json) =>
        JsonSerializer.Deserialize<NicAdvancedProperties>(json, JsonOptions)
        ?? throw new InvalidDataException("JSON de Ndi\\Params vacío.");

    public NdiParam? Get(string keyword) =>
        Params.FirstOrDefault(p => string.Equals(p.Keyword, keyword, StringComparison.OrdinalIgnoreCase));
}

public enum NicChipFamily { Realtek, Intel }

/// <summary>Chip identificado por su ID PCI (VEN/DEV), no por el nombre del driver.</summary>
public sealed record NicChip(string Name, NicChipFamily Family);

public sealed record NicSettingChange(string Keyword, string Label, string? Current, string Target, string Why)
{
    public bool IsAlreadySet => string.Equals(Current, Target, StringComparison.OrdinalIgnoreCase);
}

public sealed record NicTuningPlan(NicChip Chip, IReadOnlyList<NicSettingChange> Changes)
{
    public bool IsFullyApplied => Changes.All(c => c.IsAlreadySet);
}

/// <summary>
/// Spec 002 / B4: ajustes de baja latencia por chip. Solo se escriben keywords que el driver declara en Ndi\Params
/// y solo valores que esa definición acepta (enum o rango/paso); lo que no existe o no valida se omite.
/// Chips no reconocidos no reciben plan (nunca a ciegas).
/// </summary>
public static class NicTuningPlanner
{
    private static readonly Dictionary<string, NicChip> Chips = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"VEN_10EC&DEV_8125"] = new("Realtek RTL8125 (2.5GbE)", NicChipFamily.Realtek),
        [@"VEN_10EC&DEV_8126"] = new("Realtek RTL8126 (5GbE)", NicChipFamily.Realtek),
        [@"VEN_10EC&DEV_8168"] = new("Realtek RTL8111/8168 (1GbE)", NicChipFamily.Realtek),
        [@"VEN_10EC&DEV_8161"] = new("Realtek RTL8111 (1GbE)", NicChipFamily.Realtek),
        [@"VEN_10EC&DEV_2600"] = new("Killer E2600 (Realtek)", NicChipFamily.Realtek),
        [@"VEN_10EC&DEV_3000"] = new("Killer E3000/E3100 (Realtek)", NicChipFamily.Realtek),
        [@"VEN_8086&DEV_15F2"] = new("Intel I225-LM", NicChipFamily.Intel),
        [@"VEN_8086&DEV_15F3"] = new("Intel I225-V", NicChipFamily.Intel),
        [@"VEN_8086&DEV_125B"] = new("Intel I226-LM", NicChipFamily.Intel),
        [@"VEN_8086&DEV_125C"] = new("Intel I226-V", NicChipFamily.Intel),
        [@"VEN_8086&DEV_125D"] = new("Intel I226-IT", NicChipFamily.Intel),
        [@"VEN_8086&DEV_15B7"] = new("Intel I219-LM", NicChipFamily.Intel),
        [@"VEN_8086&DEV_15B8"] = new("Intel I219-V", NicChipFamily.Intel),
        [@"VEN_8086&DEV_15BB"] = new("Intel I219-LM", NicChipFamily.Intel),
        [@"VEN_8086&DEV_15BC"] = new("Intel I219-V", NicChipFamily.Intel),
        [@"VEN_8086&DEV_15BD"] = new("Intel I219-LM", NicChipFamily.Intel),
        [@"VEN_8086&DEV_15BE"] = new("Intel I219-V", NicChipFamily.Intel),
        [@"VEN_8086&DEV_15D7"] = new("Intel I219-LM", NicChipFamily.Intel),
        [@"VEN_8086&DEV_15D8"] = new("Intel I219-V", NicChipFamily.Intel),
        [@"VEN_8086&DEV_15E3"] = new("Intel I219-LM", NicChipFamily.Intel),
        [@"VEN_8086&DEV_15FA"] = new("Intel I219-V", NicChipFamily.Intel),
        [@"VEN_8086&DEV_15FB"] = new("Intel I219-LM", NicChipFamily.Intel),
        [@"VEN_8086&DEV_15FC"] = new("Intel I219-V", NicChipFamily.Intel),
        [@"VEN_8086&DEV_0D4C"] = new("Intel I219-LM", NicChipFamily.Intel),
        [@"VEN_8086&DEV_0D4D"] = new("Intel I219-V", NicChipFamily.Intel),
        [@"VEN_8086&DEV_0D4E"] = new("Intel I219-LM", NicChipFamily.Intel),
        [@"VEN_8086&DEV_0D4F"] = new("Intel I219-V", NicChipFamily.Intel),
        [@"VEN_8086&DEV_1A1C"] = new("Intel I219-LM", NicChipFamily.Intel),
        [@"VEN_8086&DEV_1A1D"] = new("Intel I219-V", NicChipFamily.Intel),
        [@"VEN_8086&DEV_1A1E"] = new("Intel I219-LM", NicChipFamily.Intel),
        [@"VEN_8086&DEV_1A1F"] = new("Intel I219-V", NicChipFamily.Intel),
    };

    public static NicChip? Identify(string componentOrHardwareId)
    {
        foreach (var (key, chip) in Chips)
        {
            if (componentOrHardwareId.Contains(key, StringComparison.OrdinalIgnoreCase)) return chip;
        }
        return null;
    }

    /// <summary>null si el chip no está en la tabla.</summary>
    public static NicTuningPlan? Plan(NicAdvancedProperties nic, int physicalCores)
    {
        if (Identify(nic.ComponentId) is not { } chip) return null;
        var changes = new List<NicSettingChange>();

        void Want(string keyword, string target, string why)
        {
            if (nic.Get(keyword) is not { } p) return;          // el driver no tiene esa opción
            if (Validate(p, target) is not { } valid) return;   // el valor no está permitido
            changes.Add(new NicSettingChange(p.Keyword, p.ParamDesc ?? p.Keyword, p.Current, valid, why));
        }

        // Estándar NDIS (todas las familias): Microsoft "Standardized INF keywords".
        Want("*InterruptModeration", "0", "Cada paquete se atiende al llegar, sin esperar a juntar varios.");
        Want("*FlowControl", "0", "Las tramas PAUSE pueden frenar el envío unos microsegundos.");
        Want("*EEE", "0", "Energy-Efficient Ethernet duerme el enlace entre paquetes y tarda en despertar.");
        Want("*LsoV2IPv4", "0", "LSO agrupa envíos grandes; no aporta a paquetes UDP chicos y suma ráfagas.");
        Want("*LsoV2IPv6", "0", "Ídem LSO en IPv6.");
        Want("*SpeedDuplex", "0", "Velocidad negociada con el router (evita desajustes de dúplex).");
        Want("*RSS", "1", "Reparte la recepción entre varios núcleos.");
        if (QueuesFor(nic.Get("*NumRssQueues"), physicalCores) is { } queues)
        {
            Want("*NumRssQueues", queues, "Hasta 4 colas RSS (sin pasar los núcleos físicos).");
        }

        if (chip.Family == NicChipFamily.Realtek)
        {
            Want("*ReceiveBuffers", "512", "Suficiente para no perder paquetes sin agrandar la cola.");
            Want("*TransmitBuffers", "128", "Anillo de envío corto: el paquete del juego sale sin esperar en cola.");
            Want("AdvancedEEE", "0", "Ahorro de energía propietario de Realtek.");
            Want("EnableGreenEthernet", "0", "Ahorro de energía propietario de Realtek.");
            Want("GigaLite", "0", "Baja la velocidad del enlace para ahorrar energía.");
            Want("PowerSavingMode", "0", "Ahorro de energía propietario de Realtek.");
        }
        else
        {
            Want("EEELinkAdvertisement", "0", "EEE en drivers Intel.");
            Want("ULPMode", "0", "Ultra Low Power de Intel: duerme el chip sin cable.");
            Want("ReduceSpeedOnPowerDown", "0", "Baja la velocidad del enlace en reposo.");
            Want("EnablePME", "0", "Wake on LAN / Power Management Event: el chip queda atendiendo energía.");
        }

        return new NicTuningPlan(chip, changes);
    }

    private static string? QueuesFor(NdiParam? p, int physicalCores)
    {
        if (p?.Enum is null) return null;
        int cap = Math.Max(1, Math.Min(4, physicalCores));
        return p.Enum.Keys
            .Select(k => int.TryParse(k, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1)
            .Where(v => v > 0 && v <= cap)
            .OrderByDescending(v => v)
            .Select(v => v.ToString(CultureInfo.InvariantCulture))
            .FirstOrDefault();
    }

    /// <summary>Devuelve el valor a escribir si la definición lo acepta (para enteros, el válido más cercano dentro del rango).</summary>
    internal static string? Validate(NdiParam p, string target)
    {
        string type = (p.Type ?? "").Trim().ToLowerInvariant();
        if (type == "enum")
        {
            return p.Enum is not null && p.Enum.ContainsKey(target) ? target : null;
        }
        if (type is "int" or "long" or "word" or "dword")
        {
            if (!long.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out long t)) return null;
            long min = ParseOr(p.Min, long.MinValue), max = ParseOr(p.Max, long.MaxValue), step = Math.Max(1, ParseOr(p.Step, 1));
            if (min > max) return null;
            long v = Math.Clamp(t, min, max);
            if (min != long.MinValue) v = min + (v - min) / step * step; // alinear al paso
            return v.ToString(CultureInfo.InvariantCulture);
        }
        return null; // "edit" y otros: nunca se escriben
    }

    private static long ParseOr(string? s, long fallback) =>
        long.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : fallback;
}
