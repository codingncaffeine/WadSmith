using System.Globalization;
using System.Text;

namespace WadSmith.Core.Maps.Udmf;

public enum UdmfValueKind
{
    Integer,
    Float,
    Boolean,
    String,
}

/// <summary>One UDMF value. Integers and floats stay distinct so a map writes back as it was read.</summary>
public readonly struct UdmfValue : IEquatable<UdmfValue>
{
    private readonly long _integer;
    private readonly double _float;
    private readonly string? _string;

    private UdmfValue(UdmfValueKind kind, long integer, double number, string? text)
    {
        Kind = kind;
        _integer = integer;
        _float = number;
        _string = text;
    }

    public UdmfValueKind Kind { get; }

    public static UdmfValue Int(long value) => new(UdmfValueKind.Integer, value, 0, null);

    public static UdmfValue Float(double value) => new(UdmfValueKind.Float, 0, value, null);

    public static UdmfValue Bool(bool value) => new(UdmfValueKind.Boolean, value ? 1 : 0, 0, null);

    public static UdmfValue Str(string value) => new(UdmfValueKind.String, 0, 0, value);

    public static implicit operator UdmfValue(int value) => Int(value);

    public static implicit operator UdmfValue(double value) => Float(value);

    public static implicit operator UdmfValue(bool value) => Bool(value);

    public static implicit operator UdmfValue(string value) => Str(value);

    /// <summary>The value as an integer; a float is truncated, as the engines read a float where they expect an integer.</summary>
    public long AsInt() => Kind switch
    {
        UdmfValueKind.Integer or UdmfValueKind.Boolean => _integer,
        UdmfValueKind.Float => (long)_float,
        _ => throw new FormatException($"\"{_string}\" is text, not a number."),
    };

    public double AsFloat() => Kind switch
    {
        UdmfValueKind.Float => _float,
        UdmfValueKind.Integer or UdmfValueKind.Boolean => _integer,
        _ => throw new FormatException($"\"{_string}\" is text, not a number."),
    };

    public bool AsBool() => Kind switch
    {
        UdmfValueKind.Boolean or UdmfValueKind.Integer => _integer != 0,
        UdmfValueKind.Float => _float != 0,
        _ => throw new FormatException($"\"{_string}\" is text, not true or false."),
    };

    public string AsString() => Kind == UdmfValueKind.String ? _string! : ToUdmf();

    /// <summary>The value as it is written in a TEXTMAP, always with the invariant culture.</summary>
    public string ToUdmf() => Kind switch
    {
        UdmfValueKind.Integer => _integer.ToString(CultureInfo.InvariantCulture),
        UdmfValueKind.Float => FormatFloat(_float),
        UdmfValueKind.Boolean => _integer != 0 ? "true" : "false",
        _ => Quote(_string!),
    };

    /// <summary>Shortest round-trip text, always with a decimal point so it reads back as a float.</summary>
    public static string FormatFloat(double value)
    {
        var s = value.ToString("R", CultureInfo.InvariantCulture);
        if (s.Contains('E', StringComparison.Ordinal))
        {
            s = value.ToString("0.0###############", CultureInfo.InvariantCulture);
        }

        return s.Contains('.', StringComparison.Ordinal) || s.Contains("NaN", StringComparison.Ordinal) || s.Contains("Infinity", StringComparison.Ordinal) ? s : s + ".0";
    }

    public static string Quote(string text)
    {
        var sb = new StringBuilder(text.Length + 2);
        sb.Append('"');
        foreach (var c in text)
        {
            if (c is '"' or '\\') sb.Append('\\');
            sb.Append(c);
        }

        return sb.Append('"').ToString();
    }

    public bool Equals(UdmfValue other) => Kind == other.Kind && Kind switch
    {
        UdmfValueKind.Float => _float.Equals(other._float),
        UdmfValueKind.String => string.Equals(_string, other._string, StringComparison.Ordinal),
        _ => _integer == other._integer,
    };

    public override bool Equals(object? obj) => obj is UdmfValue v && Equals(v);

    public override int GetHashCode() => HashCode.Combine(Kind, _integer, _float, _string);

    public static bool operator ==(UdmfValue a, UdmfValue b) => a.Equals(b);

    public static bool operator !=(UdmfValue a, UdmfValue b) => !a.Equals(b);

    public override string ToString() => ToUdmf();
}
