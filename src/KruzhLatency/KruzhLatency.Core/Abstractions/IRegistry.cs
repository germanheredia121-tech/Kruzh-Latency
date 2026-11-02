namespace KruzhLatency.Core.Abstractions;

/// <summary>Hive de registro. <see cref="CurrentUser"/> = hive del usuario INTERACTIVO (el jugador), no el de la cuenta que elevó.</summary>
public enum RegHive
{
    LocalMachine,
    CurrentUser,
    Users
}

/// <summary>Tipos de valor de registro soportados (espejo de REG_* sin depender de Microsoft.Win32).</summary>
public enum RegKind
{
    String,
    ExpandString,
    DWord,
    QWord,
    Binary,
    MultiString
}

/// <summary>
/// Valor de registro tipado e inmutable. <see cref="Data"/> es: string (String/ExpandString), uint (DWord),
/// ulong (QWord), byte[] (Binary) o string[] (MultiString). Usar las fábricas para garantizar el tipo correcto.
/// </summary>
public sealed record RegValue
{
    public RegKind Kind { get; }
    public object Data { get; }

    private RegValue(RegKind kind, object data)
    {
        Kind = kind;
        Data = data;
    }

    public static RegValue String(string value) => new(RegKind.String, value ?? throw new ArgumentNullException(nameof(value)));
    public static RegValue ExpandString(string value) => new(RegKind.ExpandString, value ?? throw new ArgumentNullException(nameof(value)));
    public static RegValue DWord(uint value) => new(RegKind.DWord, value);
    public static RegValue QWord(ulong value) => new(RegKind.QWord, value);
    public static RegValue Binary(byte[] value) => new(RegKind.Binary, (byte[])(value ?? throw new ArgumentNullException(nameof(value))).Clone());
    public static RegValue MultiString(string[] value) => new(RegKind.MultiString, (string[])(value ?? throw new ArgumentNullException(nameof(value))).Clone());

    public string AsString() => (string)Data;
    public uint AsDWord() => (uint)Data;
    public ulong AsQWord() => (ulong)Data;
    public byte[] AsBinary() => (byte[])((byte[])Data).Clone();
    public string[] AsMultiString() => (string[])((string[])Data).Clone();

    /// <summary>Igualdad por contenido (byte[] y string[] se comparan elemento a elemento).</summary>
    public bool Equals(RegValue? other)
    {
        if (other is null || other.Kind != Kind) return false;
        return (Data, other.Data) switch
        {
            (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),
            (string[] a, string[] b) => a.SequenceEqual(b, StringComparer.Ordinal),
            _ => Data.Equals(other.Data)
        };
    }

    public override int GetHashCode() => HashCode.Combine(Kind, Data switch
    {
        byte[] b => b.Length,
        string[] s => s.Length,
        _ => Data.GetHashCode()
    });

    public override string ToString() => Data switch
    {
        byte[] b => $"{Kind}:{Convert.ToHexString(b)}",
        string[] s => $"{Kind}:[{string.Join("|", s)}]",
        _ => $"{Kind}:{Data}"
    };
}

/// <summary>Acceso al registro. Las implementaciones usan siempre la vista de 64 bits.</summary>
public interface IRegistry
{
    bool KeyExists(RegHive hive, string subKey);

    /// <summary>Devuelve null si la clave o el valor no existen.</summary>
    RegValue? GetValue(RegHive hive, string subKey, string valueName);

    /// <summary>Crea la clave si no existe.</summary>
    void SetValue(RegHive hive, string subKey, string valueName, RegValue value);

    /// <summary>Borra SOLO el valor, nunca la clave. No falla si no existe.</summary>
    void DeleteValue(RegHive hive, string subKey, string valueName);

    IReadOnlyList<string> GetSubKeyNames(RegHive hive, string subKey);

    IReadOnlyList<string> GetValueNames(RegHive hive, string subKey);
}
