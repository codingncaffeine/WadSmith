namespace WadSmith.Core.Maps.Udmf;

/// <summary>
/// Field names in the order they were given, compared without regard to case (UDMF identifiers are
/// case-insensitive). Setting an existing field keeps its place.
/// </summary>
public sealed class UdmfFields : IEnumerable<KeyValuePair<string, UdmfValue>>
{
    private readonly List<string> _order = [];
    private readonly Dictionary<string, UdmfValue> _values = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _order.Count;

    public UdmfValue this[string name]
    {
        get => _values[name];
        set
        {
            if (!_values.ContainsKey(name)) _order.Add(name);
            _values[name] = value;
        }
    }

    public bool TryGet(string name, out UdmfValue value) => _values.TryGetValue(name, out value);

    public bool Contains(string name) => _values.ContainsKey(name);

    public bool Remove(string name)
    {
        if (!_values.Remove(name)) return false;
        _order.RemoveAt(_order.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)));
        return true;
    }

    public IEnumerator<KeyValuePair<string, UdmfValue>> GetEnumerator()
    {
        foreach (var n in _order) yield return new KeyValuePair<string, UdmfValue>(n, _values[n]);
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A block such as <c>linedef { … }</c>.</summary>
public sealed class UdmfBlock(string type)
{
    public string Type { get; } = type;

    public UdmfFields Fields { get; } = new();
}

/// <summary>
/// A parsed TEXTMAP: the top-level assignments (<c>namespace</c> among them) and the blocks, in
/// order. Blocks of any type are kept, so a writer never drops what it does not understand.
/// </summary>
public sealed class UdmfDocument
{
    public UdmfFields Globals { get; } = new();

    public List<UdmfBlock> Blocks { get; } = [];

    public string? Namespace
    {
        get => Globals.TryGet("namespace", out var v) && v.Kind == UdmfValueKind.String ? v.AsString() : null;
        set
        {
            if (value is null) Globals.Remove("namespace");
            else Globals["namespace"] = value;
        }
    }
}
