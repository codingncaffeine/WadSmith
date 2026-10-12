using WadSmith.Core.Maps.Udmf;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Editing;

/// <summary>
/// A change to the map's structure (corners, walls, faces and areas added, split or taken off),
/// written down as it is made: each step changes the map at once, so later steps see the earlier
/// ones, and keeps its inverse for undo. Made only through <see cref="History.Record"/>, which
/// takes it back if building it fails.
/// </summary>
public sealed class StructureEdit : IEdit
{
    private readonly List<(Action Do, Action Undo)> _steps = [];
    private readonly HashSet<Element> _touched = new(ReferenceEqualityComparer.Instance);
    private readonly List<Element> _order = [];
    private bool _fresh = true;

    internal StructureEdit(EditorMap map, string label)
    {
        Map = map;
        Label = label;
    }

    public string Label { get; set; }

    public EditorMap Map { get; }

    /// <summary>Whether the edit changed anything.</summary>
    public bool IsEmpty => _steps.Count == 0;

    public IEnumerable<Element> Touched => _order;

    /// <summary>Puts a new element on the map.</summary>
    public T Add<T>(T element)
        where T : Element
    {
        Map.Put(element);
        _steps.Add((() => Map.Put(element), () => EditorMap.Take(element)));
        Note(element);
        return element;
    }

    /// <summary>Takes an element off the map (its place stays empty).</summary>
    public void Remove(Element element)
    {
        if (!element.OnMap) return;
        EditorMap.Take(element);
        _steps.Add((() => EditorMap.Take(element), () => Map.Put(element)));
        Note(element);
    }

    /// <summary>Changes one property of an element; nothing is written down when it already has that value.</summary>
    public void Set<TElement, TValue>(TElement element, Func<TElement, TValue> get, Action<TElement, TValue> set, TValue value)
        where TElement : Element
    {
        var old = get(element);
        if (EqualityComparer<TValue>.Default.Equals(old, value)) return;
        set(element, value);
        _steps.Add((() => set(element, value), () => set(element, old)));
        Note(element);
    }

    /// <summary>Turns a flag of an element on or off; undo restores the fields exactly, in their order.</summary>
    public void Flag(Element element, string name, bool on)
    {
        if (element.Flag(name) == on) return;
        var before = element.Extra.ToArray();
        element.SetFlag(name, on);
        var after = element.Extra.ToArray();
        _steps.Add((() => Restore(element, after), () => Restore(element, before)));
        Note(element);
    }

    private static void Restore(Element element, KeyValuePair<string, UdmfValue>[] fields)
    {
        foreach (var name in element.Extra.Select(f => f.Key).ToList()) element.Extra.Remove(name);
        foreach (var (k, v) in fields) element.Extra[k] = v;
    }

    /// <summary>Notes an element whose frame must be republished although no step changed it directly.</summary>
    public void Note(Element element)
    {
        if (_touched.Add(element)) _order.Add(element);
    }

    public void Do(EditorMap map)
    {
        // The steps already ran while the edit was being made; later calls are redos.
        if (_fresh)
        {
            _fresh = false;
            return;
        }

        foreach (var (step, _) in _steps) step();
    }

    public void Undo(EditorMap map)
    {
        for (var i = _steps.Count - 1; i >= 0; i--) _steps[i].Undo();
    }

    /// <summary>Takes back every step made so far (when building the edit failed).</summary>
    internal void Abandon()
    {
        Undo(Map);
        _steps.Clear();
    }
}
