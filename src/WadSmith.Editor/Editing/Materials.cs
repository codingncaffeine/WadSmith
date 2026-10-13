using WadSmith.Core;
using WadSmith.Core.Containers;
using WadSmith.Core.Graphics;
using WadSmith.Core.Projects;

namespace WadSmith.Editor.Editing;

/// <summary>A material brought in: the texture name walls, doors and floors use, the maps that came with it, and what was said.</summary>
public sealed record ImportedMaterial(string Texture, IReadOnlyList<PbrLayer> Layers, IReadOnlyList<string> Notes);

/// <summary>
/// PBR material sets as textures (D42, Phase 11): the colour picture becomes a texture of the project (<c>textures/NAME.png</c>),
/// usable on walls, doors and floors, and its maps go where GZDoom finds a texture's layers by name
/// (<c>materials/normalmaps/</c>, <c>roughness/</c>, <c>metallic/</c>, <c>ao/</c>, <c>specular/</c>), so walls look modern
/// under dynamic lights with no GLDEFS to write. Every picture is kept to 1024 pixels across.
/// </summary>
public static class Materials
{
    private static readonly Dictionary<PbrLayer, string> Folders = new()
    {
        [PbrLayer.Colour] = "textures",
        [PbrLayer.Normal] = "materials/normalmaps",
        [PbrLayer.Roughness] = "materials/roughness",
        [PbrLayer.Metallic] = "materials/metallic",
        [PbrLayer.Occlusion] = "materials/ao",
        [PbrLayer.Specular] = "materials/specular",
    };

    /// <summary>Whether <paramref name="path"/> is a picture of a PBR set WadSmith brings in as a material.</summary>
    public static bool IsMaterial(string path) => PbrSet.Of(path) is not null;

    /// <summary>Brings the set <paramref name="path"/> belongs to into <paramref name="project"/> and <paramref name="into"/>'s textures. Reads and writes files: call it on a worker.</summary>
    public static ImportedMaterial Import(Project project, GameResources into, string path)
    {
        var set = PbrSet.Of(path) ?? throw new CorruptDataException($"{Path.GetFileName(path)} is not part of a material set (a colour picture with a normal, roughness or metal map beside it, named alike).");
        var notes = new List<string>();
        var pictures = PbrSet.Read(set);
        var letters = new string([.. set.Name.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit)]);
        var name = Borrowing.FreeName(into.Textures, letters.Length == 0 ? "MATERIAL" : letters[..Math.Min(8, letters.Length)], into.Game);
        var files = new List<(string, byte[])>();
        foreach (var (layer, image) in pictures)
        {
            var picture = image;
            if (Math.Max(picture.Width, picture.Height) > ModelProps.MaxSkin)
            {
                if (layer == PbrLayer.Colour) notes.Add($"Its {picture.Width}×{picture.Height} pictures were made {ModelProps.MaxSkin} pixels across at most, to keep the project small.");
                picture = ModelProps.Shrink(picture, ModelProps.MaxSkin);
            }

            files.Add(($"{Folders[layer]}/{name}.png", ImageCodec.EncodePng(picture)));
        }

        project.WriteFiles(files);
        var colour = $"textures/{name}.png";
        var entry = ResourceContainer.Open(project.Folder).FindPath(colour) ?? throw new CorruptDataException($"{colour} was written but could not be read back.");
        into.Textures.Add(entry);
        project.Record([.. pictures.Keys.Select(layer => new CopiedAsset { Kind = layer == PbrLayer.Colour ? "wall" : "material", Game = "pbr", Original = set.Name, Name = name, File = $"{Folders[layer]}/{name}.png" })]);
        return new ImportedMaterial(name, [.. pictures.Keys.Where(l => l != PbrLayer.Colour).Order()], notes);
    }

    /// <summary>The maps in words: "a normal map, roughness and occlusion".</summary>
    public static string Words(IReadOnlyList<PbrLayer> layers)
    {
        var words = layers.Select(l => l switch
        {
            PbrLayer.Normal => "a normal map",
            PbrLayer.Roughness => "roughness",
            PbrLayer.Metallic => "metalness",
            PbrLayer.Occlusion => "occlusion",
            _ => "specular",
        }).ToList();
        return words.Count switch { 0 => "no maps", 1 => words[0], _ => string.Join(", ", words[..^1]) + " and " + words[^1] };
    }
}
