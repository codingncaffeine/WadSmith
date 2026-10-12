using WadSmith.Editor.Model;

namespace WadSmith.Editor.Editing;

/// <summary>One change to the map that knows how to take itself back.</summary>
public interface IEdit
{
    /// <summary>What the change was, in the History panel's words: "Change floor height".</summary>
    string Label { get; }

    void Do(EditorMap map);

    void Undo(EditorMap map);

    /// <summary>
    /// Folds a following edit of the same gesture (a drag) into this one, so the drag undoes in one
    /// step. Returns false when the two cannot be merged.
    /// </summary>
    bool TryMerge(IEdit next) => false;
}

/// <summary>
/// The undo history. Every change to the map goes through <see cref="Run"/>; edits made during one
/// gesture (a drag, a held key) are merged into one step. A checkpoint names a point the user can go
/// back to (each Play adds one).
/// </summary>
public sealed class History(EditorMap map)
{
    private readonly List<Entry> _undo = [];
    private readonly List<Entry> _redo = [];

    private sealed record Entry(IEdit Edit, long Gesture, DateTimeOffset When)
    {
        public IEdit Edit { get; set; } = Edit;

        public string? Checkpoint { get; set; }
    }

    public EditorMap Map { get; } = map;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Edit.Label : null;

    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Edit.Label : null;

    /// <summary>The steps, oldest first, with their checkpoint names, for the History panel.</summary>
    public IReadOnlyList<(string Label, string? Checkpoint, DateTimeOffset When)> Steps => _undo.Select(e => (e.Edit.Label, e.Checkpoint, e.When)).ToList();

    /// <summary>Raised after every Run, Undo or Redo.</summary>
    public event Action? Moved;

    /// <summary>
    /// Applies an edit and records it. Pass the same non-zero <paramref name="gesture"/> for every
    /// edit of one drag so they become one step.
    /// </summary>
    public void Run(IEdit edit, long gesture = 0)
    {
        edit.Do(Map);
        _redo.Clear();
        if (gesture != 0 && _undo.Count > 0 && _undo[^1].Gesture == gesture && _undo[^1].Checkpoint is null && _undo[^1].Edit.TryMerge(edit))
        {
            // Merged into the previous step of the same gesture.
        }
        else
        {
            _undo.Add(new Entry(edit, gesture, DateTimeOffset.Now));
        }

        Map.Touch();
        Moved?.Invoke();
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var e = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        e.Edit.Undo(Map);
        _redo.Add(e);
        Map.Touch();
        Moved?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var e = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        e.Edit.Do(Map);
        _undo.Add(e);
        Map.Touch();
        Moved?.Invoke();
        return true;
    }

    /// <summary>Names the current point in the history (a version the user can return to).</summary>
    public void Checkpoint(string name)
    {
        if (_undo.Count > 0) _undo[^1].Checkpoint = name;
    }

    private static long _nextGesture;

    /// <summary>A fresh id for one drag or other continuous gesture.</summary>
    public static long NewGesture() => Interlocked.Increment(ref _nextGesture);
}

/// <summary>Sets one property of one element. Edits of the same property of the same element merge, keeping the first old value.</summary>
public sealed class SetProperty<TElement, TValue>(TElement element, string label, Func<TElement, TValue> get, Action<TElement, TValue> set, TValue value) : IEdit
    where TElement : Element
{
    private readonly TValue _old = get(element);
    private TValue _new = value;

    public string Label { get; } = label;

    public TElement Element { get; } = element;

    public void Do(EditorMap map) => set(Element, _new);

    public void Undo(EditorMap map) => set(Element, _old);

    public bool TryMerge(IEdit next)
    {
        if (next is not SetProperty<TElement, TValue> other || !ReferenceEquals(other.Element, Element) || other.Label != Label) return false;
        _new = other._new;
        return true;
    }
}

/// <summary>Several edits as one step ("Change floor height of 4 areas").</summary>
public sealed class Batch(string label, IReadOnlyList<IEdit> edits) : IEdit
{
    public string Label { get; } = label;

    public void Do(EditorMap map)
    {
        foreach (var e in edits) e.Do(map);
    }

    public void Undo(EditorMap map)
    {
        for (var i = edits.Count - 1; i >= 0; i--) edits[i].Undo(map);
    }
}
