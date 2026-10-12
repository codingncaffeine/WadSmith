namespace WadSmith.Core.Containers;

/// <summary>How PK3s and folders map directories and file names onto namespaces and lump names.</summary>
internal static class PathNamespaces
{
    private static readonly Dictionary<string, ResourceNamespace> TopLevel = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sprites"] = ResourceNamespace.Sprites,
        ["flats"] = ResourceNamespace.Flats,
        ["patches"] = ResourceNamespace.Patches,
        ["textures"] = ResourceNamespace.Textures,
        ["hires"] = ResourceNamespace.HiRes,
        ["colormaps"] = ResourceNamespace.Colormaps,
        ["acs"] = ResourceNamespace.Acs,
        ["voices"] = ResourceNamespace.Voices,
        ["voxels"] = ResourceNamespace.Voxels,
        ["graphics"] = ResourceNamespace.Graphics,
        ["sounds"] = ResourceNamespace.Sounds,
        ["music"] = ResourceNamespace.Music,
        ["maps"] = ResourceNamespace.Maps,
        ["filter"] = ResourceNamespace.Filter,
    };

    /// <summary>
    /// The namespace and lookup name of a file at <paramref name="path"/> (forward slashes, relative to
    /// the archive root). Files at the root are global; files in a known top-level directory take its
    /// namespace (sub-folders included); anything else is <see cref="ResourceNamespace.Other"/>.
    /// </summary>
    public static (ResourceNamespace Ns, string Name) Classify(string path)
    {
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        var ns = ResourceNamespace.Global;
        if (slash >= 0)
        {
            ns = TopLevel.TryGetValue(path[..slash], out var known) ? known : ResourceNamespace.Other;
        }

        var file = path[(path.LastIndexOf('/') + 1)..];
        var dot = file.IndexOf('.', StringComparison.Ordinal);
        var name = dot > 0 ? file[..dot] : file;
        return (ns, name);
    }
}
