using WadSmith.Core.Text;

namespace WadSmith.Core.Graphics;

/// <summary>What a TEXTURES definition is for.</summary>
public enum TextureUse
{
    Wall,
    Flat,
    Sprite,
    Graphic,
}

/// <summary>
/// GZDoom's TEXTURES lump: composite textures defined in text. Patches may be flipped and rotated;
/// colour effects (translation, blend, alpha, style) are read but not drawn in WadSmith's preview,
/// and each such use is listed in <see cref="Notes"/>.
/// </summary>
public sealed class TexturesLump
{
    public List<(TextureUse Use, CompositeTexture Texture)> Definitions { get; } = [];

    public List<string> Notes { get; } = [];

    public static TexturesLump Parse(byte[] data, string source)
    {
        var result = new TexturesLump();
        var sc = Scanner.FromBytes(data, source);
        while (!sc.AtEnd)
        {
            var kind = sc.ExpectIdentifier().ToLowerInvariant();
            TextureUse use;
            switch (kind)
            {
                case "texture":
                case "walltexture":
                    use = TextureUse.Wall;
                    break;
                case "flat":
                    use = TextureUse.Flat;
                    break;
                case "sprite":
                    use = TextureUse.Sprite;
                    break;
                case "graphic":
                    use = TextureUse.Graphic;
                    break;
                case "define":
                    // define "name" width height — a size override for an existing picture; nothing to compose.
                    sc.ExpectName();
                    sc.ExpectInteger();
                    sc.Accept(",");
                    sc.ExpectInteger();
                    continue;
                default:
                    throw sc.Error($"\"{kind}\" does not start a texture definition (expected Texture, WallTexture, Flat, Sprite or Graphic).");
            }

            sc.Accept("optional");
            var name = sc.ExpectName();
            sc.Expect(",");
            var width = (int)sc.ExpectInteger();
            sc.Expect(",");
            var height = (int)sc.ExpectInteger();

            double scaleX = 1, scaleY = 1;
            var worldPanning = false;
            var isNull = false;
            var patches = new List<PatchPlacement>();
            if (sc.Accept("{"))
            {
                while (!sc.Accept("}"))
                {
                    if (sc.AtEnd) throw sc.Error($"the definition of {name} is never closed with }}.");
                    var prop = sc.ExpectIdentifier().ToLowerInvariant();
                    switch (prop)
                    {
                        case "xscale":
                            scaleX = sc.ExpectNumber();
                            break;
                        case "yscale":
                            scaleY = sc.ExpectNumber();
                            break;
                        case "scale":
                            scaleX = scaleY = sc.ExpectNumber();
                            break;
                        case "offset":
                            sc.ExpectInteger();
                            sc.Expect(",");
                            sc.ExpectInteger();
                            break;
                        case "worldpanning":
                            worldPanning = true;
                            break;
                        case "nulltexture":
                            isNull = true;
                            break;
                        case "nodecals":
                        case "notrim":
                        case "nofullbright":
                            break;
                        case "patch":
                        case "graphic":
                        case "sprite":
                            patches.Add(ReadPatch(sc, name, result.Notes));
                            break;
                        default:
                            throw sc.Error($"\"{prop}\" is not something a texture definition can hold.");
                    }
                }
            }

            result.Definitions.Add((use, new CompositeTexture { Name = name, Width = width, Height = height, ScaleX = scaleX, ScaleY = scaleY, WorldPanning = worldPanning, Origin = source, IsNull = isNull }));
            result.Definitions[^1].Texture.Patches.AddRange(patches);
        }

        return result;
    }

    private static PatchPlacement ReadPatch(Scanner sc, string texture, List<string> notes)
    {
        var patch = sc.ExpectName();
        sc.Expect(",");
        var x = (int)sc.ExpectInteger();
        sc.Expect(",");
        var y = (int)sc.ExpectInteger();
        bool flipX = false, flipY = false;
        var rotate = 0;
        if (sc.Accept("{"))
        {
            while (!sc.Accept("}"))
            {
                if (sc.AtEnd) throw sc.Error($"a patch block in {texture} is never closed with }}.");
                var prop = sc.ExpectIdentifier().ToLowerInvariant();
                switch (prop)
                {
                    case "flipx":
                        flipX = true;
                        break;
                    case "flipy":
                        flipY = true;
                        break;
                    case "rotate":
                        rotate = (((int)sc.ExpectInteger() % 360) + 360) % 360;
                        break;
                    case "useoffsets":
                        break;
                    case "alpha":
                        sc.ExpectNumber();
                        notes.Add($"{texture}: patch {patch} is drawn without its alpha.");
                        break;
                    case "style":
                        sc.ExpectName();
                        notes.Add($"{texture}: patch {patch} is drawn without its render style.");
                        break;
                    case "translation":
                    case "blend":
                        SkipArguments(sc);
                        notes.Add($"{texture}: patch {patch} is drawn without its colour change ({prop}).");
                        break;
                    default:
                        throw sc.Error($"\"{prop}\" is not something a patch can hold.");
                }
            }
        }

        return new PatchPlacement(patch, x, y) { FlipX = flipX, FlipY = flipY, Rotate = rotate };
    }

    /// <summary>Skips a comma-separated argument list of strings and numbers.</summary>
    private static void SkipArguments(Scanner sc)
    {
        do
        {
            var t = sc.Peek();
            if (t.Kind is TokenKind.String or TokenKind.Integer or TokenKind.Float or TokenKind.Identifier)
            {
                sc.Next();
                continue;
            }

            if (t.Is("-") || t.Is(":") || t.Is("[") || t.Is("]") || t.Is("%") || t.Is("#") || t.Is("="))
            {
                sc.Next();
                continue;
            }

            break;
        }
        while (sc.Accept(",") || sc.Peek().Kind != TokenKind.End && !sc.Peek().Is("}") && sc.Peek().Kind != TokenKind.Identifier);
    }
}
