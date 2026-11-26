using System.Globalization;

namespace Madelene.Model;

/// <summary>BSON-like type brackets that matter for equality and range comparison.</summary>
public enum FieldKind : byte
{
    String = (byte)'s',
    Number = (byte)'n',
    Boolean = (byte)'b',
    ObjectId = (byte)'o',
    Date = (byte)'d',
    Uuid = (byte)'u',
}

/// <summary>
/// A single scalar filter value. Numbers of any BSON width compare equal when their values are equal
/// (MongoDB semantics), so all numbers are stored as <see cref="double"/>.
/// Dates are stored as Unix milliseconds (BSON's own precision), which a double holds exactly.
/// </summary>
public readonly record struct FieldValue
{
    public FieldKind Kind { get; }
    public string? Text { get; }
    public double Number { get; }

    private FieldValue(FieldKind kind, string? text, double number)
    {
        Kind = kind;
        Text = text;
        Number = number;
    }

    public static FieldValue String(string value) => new(FieldKind.String, value ?? throw new ArgumentNullException(nameof(value)), 0);
    public static FieldValue Num(double value) => new(FieldKind.Number, null, value == 0 ? 0 : value); // fold -0 into 0
    public static FieldValue Bool(bool value) => new(FieldKind.Boolean, null, value ? 1 : 0);
    public static FieldValue Date(DateTime value) => new(FieldKind.Date, null, ToUnixMs(value));
    public static FieldValue DateMs(long unixMs) => new(FieldKind.Date, null, unixMs);
    public static FieldValue Uuid(Guid value) => new(FieldKind.Uuid, value.ToString("D"), 0);

    public static FieldValue ObjectId(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if (hex.Length != 24 || !hex.All(Uri.IsHexDigit))
            throw new ArgumentException($"'{hex}' is not a 24 character hex ObjectId.", nameof(hex));
        return new FieldValue(FieldKind.ObjectId, hex.ToLowerInvariant(), 0);
    }

    /// <summary>True for kinds that support range comparison.</summary>
    public bool IsOrdered => Kind is FieldKind.Number or FieldKind.Date;

    /// <summary>
    /// Canonical, type-tagged encoding used as the posting-list key. Two values are equal under
    /// MongoDB equality semantics exactly when their term encodings are equal.
    /// </summary>
    public string ToTerm() => Kind switch
    {
        FieldKind.String => "s:" + Text,
        FieldKind.Number => "n:" + Number.ToString("R", CultureInfo.InvariantCulture),
        FieldKind.Boolean => Number != 0 ? "b:1" : "b:0",
        FieldKind.ObjectId => "o:" + Text,
        FieldKind.Date => "d:" + ((long)Number).ToString(CultureInfo.InvariantCulture),
        FieldKind.Uuid => "u:" + Text,
        _ => throw new InvalidOperationException($"Unknown field kind {Kind}."),
    };

    /// <summary>Parses a term produced by <see cref="ToTerm"/>.</summary>
    public static FieldValue FromTerm(string term)
    {
        if (term.Length < 2 || term[1] != ':')
            throw new FormatException($"Malformed term '{term}'.");
        var body = term[2..];
        return (FieldKind)term[0] switch
        {
            FieldKind.String => String(body),
            FieldKind.Number => Num(double.Parse(body, NumberStyles.Float, CultureInfo.InvariantCulture)),
            FieldKind.Boolean => Bool(body == "1"),
            FieldKind.ObjectId => ObjectId(body),
            FieldKind.Date => DateMs(long.Parse(body, CultureInfo.InvariantCulture)),
            FieldKind.Uuid => new FieldValue(FieldKind.Uuid, body, 0),
            _ => throw new FormatException($"Unknown term kind in '{term}'."),
        };
    }

    /// <summary>Milliseconds since the Unix epoch (floored), MongoDB's date resolution.</summary>
    public static long ToUnixMs(DateTime value)
    {
        var ticks = ToUtcTicks(value) - DateTime.UnixEpoch.Ticks; // integer math: ticks exceed 2^53
        return ticks >= 0 ? ticks / TimeSpan.TicksPerMillisecond : -((-ticks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond);
    }

    public static DateTime FromUnixMs(long unixMs) => DateTime.UnixEpoch.AddMilliseconds(unixMs);

    public static long ToUtcTicks(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value.Ticks,
        DateTimeKind.Local => value.ToUniversalTime().Ticks,
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc).Ticks,
    };

    public override string ToString() => ToTerm();
}
