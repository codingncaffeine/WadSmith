namespace WadSmith.Core.Containers;

/// <summary>
/// The base game, then add-ons, then the project: containers in load order. A lookup returns the
/// last match, so later containers replace earlier ones exactly as they do in the engine.
/// </summary>
public sealed class ResourceStack
{
    private readonly List<ResourceContainer> _containers = [];

    public ResourceStack()
    {
    }

    public ResourceStack(IEnumerable<ResourceContainer> containers) => _containers.AddRange(containers);

    public IReadOnlyList<ResourceContainer> Containers => _containers;

    public void Add(ResourceContainer container) => _containers.Add(container);

    public ResourceEntry? Find(string name, ResourceNamespace ns = ResourceNamespace.Global)
    {
        for (var i = _containers.Count - 1; i >= 0; i--)
        {
            var hit = _containers[i].Find(name, ns);
            if (hit is not null) return hit;
        }

        return null;
    }

    /// <summary>Every lump with this name in this namespace, in load order (needed for TEXTURE1, PNAMES, ANIMDEFS and other lumps that add up rather than replace).</summary>
    public IEnumerable<ResourceEntry> FindAll(string name, ResourceNamespace ns = ResourceNamespace.Global)
    {
        foreach (var c in _containers)
        {
            foreach (var e in c.Entries)
            {
                if (!e.IsMarker && e.Namespace == ns && LumpName.Comparer.Equals(e.Name, name)) yield return e;
            }
        }
    }

    /// <summary>The visible entries of a namespace: one per name, the last loaded one, in first-seen order.</summary>
    public IReadOnlyList<ResourceEntry> All(ResourceNamespace ns)
    {
        var order = new List<string>();
        var winner = new Dictionary<string, ResourceEntry>(LumpName.Comparer);
        foreach (var c in _containers)
        {
            foreach (var e in c.Entries)
            {
                if (e.IsMarker || e.Namespace != ns) continue;
                if (!winner.ContainsKey(e.Name)) order.Add(e.Name);
                winner[e.Name] = e;
            }
        }

        return order.Select(n => winner[n]).ToList();
    }
}
