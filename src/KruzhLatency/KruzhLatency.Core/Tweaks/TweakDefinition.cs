using KruzhLatency.Core.Hardware;

namespace KruzhLatency.Core.Tweaks;

public enum TweakCategory
{
    Input,
    SystemCpu,
    GpuDisplay,
    Network,
    InterruptsMsi,
    UsbPower,
    StorageMemory,
    Games,
    Services,
    Debloat
}

public enum RiskLevel { Low, Medium, High, Critical }

/// <summary>Perfiles de la app, de menos a más agresivo. Profesional = máximo compatible con FACEIT AC / Vanguard (ADR-004).</summary>
public enum Profile
{
    Normal,
    Competitivo,
    Profesional,
    Entusiasta
}

public enum AnticheatImpact
{
    Safe,
    /// <summary>Puede bloquear el juego o generar problemas con FACEIT AC / Vanguard. Solo perfil Entusiasta.</summary>
    MayBreakAnticheat
}

public enum SecurityImpact { None, ReducesSecurity }

/// <summary>Resultado de un gate (aplicable / recomendado) con el motivo que ve el usuario.</summary>
public sealed record Gate(bool Passed, string Reason)
{
    public static Gate Pass(string reason = "") => new(true, reason);
    public static Gate Fail(string reason) => new(false, reason);
}

/// <summary>
/// Una variante del tweak (ej. "Medium" / "Off"). Un tweak on/off tiene UNA sola opción.
/// El estado "default de Windows" NO es una opción: se vuelve a él revirtiendo desde el journal.
/// </summary>
public sealed record TweakOption(string Key, string Label, string Description, IReadOnlyList<Operation> Operations);

/// <summary>Definición declarativa de un tweak (ADR-001/002). No contiene lógica de aplicación: solo datos + operaciones.</summary>
public sealed record TweakDefinition
{
    public required string Id { get; init; }
    public required string Code { get; init; }
    public required string Title { get; init; }
    public required TweakCategory Category { get; init; }

    /// <summary>Por qué baja la latencia / mejora frametime. Obligatorio (ADR-002).</summary>
    public required string Why { get; init; }

    /// <summary>Fuente técnica verificable. Obligatoria (ADR-002).</summary>
    public required string Source { get; init; }

    public RiskLevel Risk { get; init; } = RiskLevel.Low;
    public AnticheatImpact Anticheat { get; init; } = AnticheatImpact.Safe;
    public SecurityImpact Security { get; init; } = SecurityImpact.None;

    /// <summary>Perfiles que incluyen este tweak por defecto.</summary>
    public IReadOnlySet<Profile> Profiles { get; init; } = new HashSet<Profile>();

    public Func<HardwareProfile, Gate> Applicable { get; init; } = _ => Gate.Pass();
    public Func<HardwareProfile, Gate> Recommended { get; init; } = _ => Gate.Pass();

    public required IReadOnlyList<TweakOption> Options { get; init; }

    /// <summary>Opción que eligen los perfiles. null = la primera.</summary>
    public string? DefaultOptionKey { get; init; }

    public bool IsToggle => Options.Count == 1;

    public bool RequiresReboot => Options.Any(o => o.Operations.Any(op => op.RequiresReboot));

    public TweakOption DefaultOption =>
        DefaultOptionKey is null ? Options[0] : GetOption(DefaultOptionKey);

    public TweakOption GetOption(string key) =>
        Options.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"El tweak {Id} no tiene la opción '{key}'.", nameof(key));

    /// <summary>Todas las operaciones de todas las opciones, sin repetir Id (lo que puede tocar este tweak).</summary>
    public IEnumerable<Operation> AllOperations =>
        Options.SelectMany(o => o.Operations).GroupBy(op => op.Id, StringComparer.Ordinal).Select(g => g.First());

    /// <summary>Reglas de catálogo. Devuelve la lista de errores (vacía = válida).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Id)) errors.Add("Id vacío.");
        if (string.IsNullOrWhiteSpace(Why)) errors.Add($"{Id}: falta Why (ADR-002).");
        if (string.IsNullOrWhiteSpace(Source)) errors.Add($"{Id}: falta Source (ADR-002).");
        if (Options.Count == 0) errors.Add($"{Id}: no tiene opciones.");

        foreach (var opt in Options)
        {
            if (opt.Operations.Count == 0) errors.Add($"{Id}/{opt.Key}: la opción no tiene operaciones.");
        }

        var dupKeys = Options.GroupBy(o => o.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key);
        foreach (var k in dupKeys) errors.Add($"{Id}: opción duplicada '{k}'.");

        if (DefaultOptionKey is not null && !Options.Any(o => string.Equals(o.Key, DefaultOptionKey, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"{Id}: DefaultOptionKey '{DefaultOptionKey}' no existe.");
        }

        // ADR-004: solo Entusiasta puede incluir tweaks que rompen anticheat o bajan seguridad.
        if (Anticheat == AnticheatImpact.MayBreakAnticheat && Profiles.Any(p => p != Profile.Entusiasta))
        {
            errors.Add($"{Id}: puede romper anticheat y está en un perfil distinto de Entusiasta (ADR-004).");
        }
        if (Security == SecurityImpact.ReducesSecurity && Profiles.Any(p => p != Profile.Entusiasta))
        {
            errors.Add($"{Id}: reduce seguridad y está en un perfil distinto de Entusiasta (ADR-004).");
        }

        return errors;
    }
}
