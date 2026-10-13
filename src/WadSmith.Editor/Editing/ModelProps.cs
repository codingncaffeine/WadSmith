using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WadSmith.Core;
using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Core.Models;
using WadSmith.Core.Projects;

namespace WadSmith.Editor.Editing;

/// <summary>A model brought in as a prop: the object it became, how big it was read as, and what was said about it.</summary>
public sealed record ImportedModel(CarriedActor Actor, double Scale, string SizeNote, IReadOnlyList<string> Notes);

/// <summary>
/// 3D models as props in a GZDoom map (D16, D42, Phase 11): the model and its pictures are copied into the project under
/// <c>models/&lt;Class&gt;/</c>, a MODELDEF entry draws it, a ZScript decoration class places it by a number of the project's
/// own, and eight sprites baked from it (<see cref="MeshBaker"/>) show it where models are not drawn — the plan, the shelf,
/// renderers without models and older ports. The model is stood on its base and centred on its middle by MODELDEF's offsets,
/// and its size is read from its height: a model a few units tall was made in metres.
/// </summary>
public static class ModelProps
{
    /// <summary>The project's MODELDEF WadSmith writes: it includes each prop's own entry.</summary>
    public const string DefinitionFile = "modeldef.txt";

    private const string DefinitionMark = "// Written by WadSmith: the 3D models brought into this project. Edits here are replaced.";

    /// <summary>The model files a prop can be made from.</summary>
    public static readonly IReadOnlyList<string> Extensions = [".obj", ".md3", ".iqm", ".glb", ".gltf", ".md2", ".mdl", ".vox", ".kvx"];

    /// <summary>Whether <paramref name="path"/> names a model file WadSmith brings in as a prop, or a picture of a block set (<see cref="BlockSet"/>).</summary>
    public static bool IsModel(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()) || BlockSet.Of(path) is not null;

    /// <summary>Map units per metre: a 1.75 m figure stands as tall as Doom's marine (56).</summary>
    public const double UnitsPerMetre = 32;

    /// <summary>GZDoom draws a model's height divided by the game's pixel stretch (FSpriteModelFrame, non-voxels).</summary>
    public const double Stretch = 1.2;

    /// <summary>
    /// How many map units one of the model's units is, from its height: up to 8 units tall it was made in metres; over 1024
    /// in centimetres; otherwise its units are map units. The note says what was read.
    /// </summary>
    public static (double Scale, string Note) SizeOf(Mesh mesh)
    {
        var tall = mesh.Max.Z - mesh.Min.Z;
        if (tall <= 8)
        {
            var scale = UnitsPerMetre * Stretch;
            return (scale, $"read as metres: {tall.ToString("0.##", CultureInfo.InvariantCulture)} m tall, {Math.Max(4, Math.Ceiling(tall * UnitsPerMetre))} units in the map");
        }

        if (tall > 1024)
        {
            var scale = UnitsPerMetre * Stretch / 100;
            return (scale, $"read as centimetres: {Math.Round(tall)} cm tall, {Math.Max(4, Math.Ceiling(tall * UnitsPerMetre / 100))} units in the map");
        }

        return (1, $"read as map units: {Math.Max(4, Math.Ceiling(tall / Stretch))} units tall in the map");
    }

    /// <summary>The size of a model in metres (glTF says so): 32 map units a metre, said like <see cref="SizeOf"/>.</summary>
    public static (double Scale, string Note) Metres(Mesh mesh)
    {
        var tall = mesh.Max.Z - mesh.Min.Z;
        return (UnitsPerMetre * Stretch, $"in metres: {tall.ToString("0.##", CultureInfo.InvariantCulture)} m tall, {Math.Max(4, Math.Ceiling(tall * UnitsPerMetre))} units in the map");
    }

    /// <summary>A Quake model's size: Quake's units are Doom's, so the model is scaled by the pixel stretch to stand as tall in the map as in Quake.</summary>
    public static (double Scale, string Note) QuakeUnits(Mesh mesh)
    {
        var tall = mesh.Max.Z - mesh.Min.Z;
        return (Stretch, $"in Quake's units, which are Doom's: {Math.Max(4, Math.Ceiling(tall))} units tall in the map");
    }

    /// <summary>
    /// Quake's palette for a model at <paramref name="path"/>: a <c>palette.lmp</c> beside it or in a <c>gfx</c> folder beside it
    /// or above it (Quake's files taken out of their PAK), else <paramref name="fromQuake"/> (the user's own Quake).
    /// </summary>
    private static byte[]? PaletteFor(string path, Func<byte[]?>? fromQuake)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var parent = Path.GetDirectoryName(folder);
        foreach (var candidate in new[] { Path.Combine(folder, "palette.lmp"), Path.Combine(folder, "gfx", "palette.lmp"), parent is null ? null : Path.Combine(parent, "gfx", "palette.lmp") })
        {
            if (candidate is not null && ObjModel.Find(Path.GetDirectoryName(candidate)!, Path.GetFileName(candidate)) is { } file && new FileInfo(file).Length >= 768) return File.ReadAllBytes(file);
        }

        return fromQuake?.Invoke() is { Length: >= 768 } palette ? palette : null;
    }

    /// <summary>The widest a model's picture is copied (D15's cap for media): bigger ones are shrunk, keeping their shape.</summary>
    public const int MaxSkin = 1024;

    /// <summary>A picture shrunk to at most <paramref name="most"/> pixels across, by averaging the pixels each new one covers.</summary>
    public static RgbaImage Shrink(RgbaImage image, int most)
    {
        var factor = (double)Math.Max(image.Width, image.Height) / most;
        var (w, h) = (Math.Max(1, (int)Math.Round(image.Width / factor)), Math.Max(1, (int)Math.Round(image.Height / factor)));
        var small = new RgbaImage(w, h);
        for (var y = 0; y < h; y++)
        {
            int y0 = y * image.Height / h, y1 = Math.Max(y0 + 1, (y + 1) * image.Height / h);
            for (var x = 0; x < w; x++)
            {
                int x0 = x * image.Width / w, x1 = Math.Max(x0 + 1, (x + 1) * image.Width / w);
                long r = 0, g = 0, b = 0, a = 0, n = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var (pr, pg, pb, pa) = image.Get(sx, sy);
                        (r, g, b, a, n) = (r + pr, g + pg, b + pb, a + pa, n + 1);
                    }
                }

                small.Set(x, y, (byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
            }
        }

        return small;
    }

    /// <summary>The model a placed object's number shows in <paramref name="project"/>, by its class; null for any other object.</summary>
    public static string? Of(Project project, int number)
    {
        var actor = project.CopiedNow().FirstOrDefault(c => c.Kind == "actor" && c.Game == "model" && c.Name == number.ToString(CultureInfo.InvariantCulture));
        return actor?.Original;
    }

    /// <summary>
    /// Brings the model at <paramref name="path"/> into <paramref name="project"/> as a prop of <paramref name="into"/>'s game.
    /// Reads, bakes and writes files: call it on a worker.
    /// </summary>
    public static ImportedModel Import(Project project, GameResources into, string path, Func<byte[]?>? quakePalette = null)
    {
        if (!IsModel(path)) throw new CorruptDataException($"{Path.GetFileName(path)} is not a model WadSmith brings in yet (it takes {string.Join(", ", Extensions)}).");
        var notes = new List<string>();
        var kind = Path.GetExtension(path).ToLowerInvariant();
        if (kind is ".vox" or ".kvx") return ImportVoxels(project, into, path, kind, notes);

        // Quake's models: an MD2 comes as it is; an MDL becomes an MD2 (GZDoom reads no MDL), its colours through Quake's palette.
        var quake = kind switch
        {
            ".md2" => Md2.Read(path, notes),
            ".mdl" => Mdl.Read(File.ReadAllBytes(path), PaletteFor(path, quakePalette) ?? throw new CorruptDataException($"{Path.GetFileName(path)} is a Quake model whose colours are in Quake's palette, and no Quake was found: add your Quake folder to the games, or put its gfx/palette.lmp beside the model."), Path.GetFileName(path)),
            _ => null,
        };
        var mesh = kind switch
        {
            ".md3" => Md3Model.Read(path, notes),
            ".iqm" => IqmModel.Read(path, notes),
            ".glb" or ".gltf" => GltfModel.Read(path, notes),
            ".md2" or ".mdl" => quake!.ToMesh(),
            _ when BlockSet.Of(path) is not null => BlockSet.Read(path, notes),
            _ => ObjModel.Read(path, notes),
        };
        // glTF is in metres by its own rule and a block is a metre cube; Quake's units are Doom's (both players stand 56 tall,
        // so the model is drawn 1.2 times its own height to stand as tall in the map); other formats say nothing, so their
        // height is read.
        var block = BlockSet.Of(path) is not null;
        var (scale, sizeNote) = kind is ".glb" or ".gltf" || block ? Metres(mesh) : quake is not null ? QuakeUnits(mesh) : SizeOf(mesh);
        var stem = block ? BlockSet.NameOf(path) : Path.GetFileNameWithoutExtension(path);

        var className = ClassName(project, into, stem);
        var sprite = SpriteName(project, into);
        var folder = $"models/{className}";
        var files = new List<(string, byte[])>();

        // The pictures beside the model, as PNG: an OBJ written again names them in its usemtl lines (GZDoom reads each as a
        // picture in the model's folder); an MD3 or IQM is copied as it is and MODELDEF skins its surfaces by number.
        var pictures = new string?[mesh.Surfaces.Count];
        for (var s = 0; s < mesh.Surfaces.Count; s++)
        {
            if (mesh.Surfaces[s].Image is not { } image) continue;
            pictures[s] = $"skin{s}.png";
            if (Math.Max(image.Width, image.Height) > MaxSkin)
            {
                notes.Add($"Its {image.Width}×{image.Height} picture was made {MaxSkin} pixels across at most, to keep the project small.");
                image = Shrink(image, MaxSkin);
            }

            files.Add(($"{folder}/{pictures[s]}", ImageCodec.EncodePng(image)));
        }

        var modelFile = "model" + (kind is ".md3" or ".iqm" or ".md2" ? kind : kind == ".mdl" ? ".md2" : ".obj");
        files.Add(($"{folder}/{modelFile}", kind switch
        {
            ".md3" or ".iqm" or ".md2" => File.ReadAllBytes(path),
            ".mdl" => Md2.Write(quake!, pictures[0] ?? ""),
            _ => Encoding.UTF8.GetBytes(ObjModel.Write(mesh, s => pictures[s])),
        }));
        var skins = kind switch
        {
            ".md3" or ".iqm" => string.Concat(pictures.Select((p, s) => p is null ? "" : $"\tSurfaceSkin 0 {s} \"{p}\"\n")),
            ".md2" or ".mdl" when pictures[0] is { } skin => $"\tSkin 0 \"{skin}\"\n",
            _ => "",
        };

        // Stood on its base, centred on its middle: MODELDEF's offsets are in map units.
        var origin = ((mesh.Min.X + mesh.Max.X) / 2, (mesh.Min.Y + mesh.Max.Y) / 2, mesh.Min.Z);
        var c = CultureInfo.InvariantCulture;
        var s3 = scale.ToString("R", c);
        files.Add(($"{folder}/modeldef.txt", Encoding.UTF8.GetBytes(
            $"Model {className}\n{{\n\tPath \"{folder}\"\n\tModel 0 \"{modelFile}\"\n{skins}\tScale {s3} {s3} {s3}\n" +
            string.Create(c, $"\tOffset {Offset(origin.Item1)} {Offset(origin.Item2)} {Offset(origin.Item3)}\n") +
            $"\tFrameIndex {sprite} A 0 0\n}}\n")));

        string Offset(float at) => (Math.Round(-at * scale, 3) + 0.0).ToString("0.###", c);

        // Eight sprites, one per rotation.
        for (var rotation = 1; rotation <= 8; rotation++)
        {
            files.Add(($"sprites/{sprite}A{rotation}.png", ImageCodec.EncodePng(MeshBaker.Bake(mesh, scale, origin, rotation, Stretch))));
        }

        var radius = Math.Max(4, (int)Math.Ceiling(mesh.Reach * scale));
        var height = Math.Max(4, (int)Math.Ceiling((mesh.Max.Z - mesh.Min.Z) * scale / Stretch));
        var title = Title(stem);
        var script = $"zscript/wadsmith/{className}.zs";
        files.Add((script, Encoding.UTF8.GetBytes(Declare(className, title, sprite, radius, height))));
        project.WriteFiles(files);

        var number = FreeNumber(project, into);
        project.Record(
        [
            new CopiedAsset { Kind = "zscript", Game = "model", Original = className, Name = className, File = script },
            new CopiedAsset { Kind = "modeldef", Game = "model", Original = className, Name = className, File = $"{folder}/modeldef.txt" },
            new CopiedAsset { Kind = "actor", Game = "model", Original = className, Name = number.ToString(c), File = script },
            new CopiedAsset { Kind = "modelprop", Game = "model", Original = className, Name = string.Join('|', sprite, radius.ToString(c), height.ToString(c), title, Path.GetFileName(path)), File = folder },
        ]);
        DukeBorrowing.WriteScript(project);
        WriteDefinitions(project);

        var thing = ThingOf(number, className, sprite, radius, height, title);
        into.Carry([thing], project.Folder);
        return new ImportedModel(new CarriedActor(number, thing, 8, 0, true), scale, sizeNote, notes);
    }

    /// <summary>The project's VOXELDEF WadSmith writes: a line for each voxel prop.</summary>
    public const string VoxelFile = "voxeldef.txt";

    private const string VoxelMark = "// Written by WadSmith: the voxel models brought into this project. Edits here are replaced.";

    /// <summary>
    /// A voxel model as a prop (D16): a MagicaVoxel file becomes a KVX (a KVX comes as it is) under <c>voxels/</c>, named like
    /// the prop's sprite, and VOXELDEF draws it in place of that sprite's frame A wherever voxels are drawn (both of GZDoom's
    /// renderers); one voxel is one map unit. The sprites, class and number follow as for any model.
    /// </summary>
    private static ImportedModel ImportVoxels(Project project, GameResources into, string path, string kind, List<string> notes)
    {
        var bytes = kind == ".kvx" ? File.ReadAllBytes(path) : null;
        var voxels = bytes is null ? MagicaVoxel.Read(path, notes) : Kvx.Read(bytes, Path.GetFileName(path));
        var mesh = voxels.ToMesh();
        var className = ClassName(project, into, Path.GetFileNameWithoutExtension(path));
        var sprite = SpriteName(project, into);
        var file = $"voxels/{sprite}.kvx";
        var files = new List<(string, byte[])> { (file, bytes ?? Kvx.Write(voxels)) };
        for (var rotation = 1; rotation <= 8; rotation++) files.Add(($"sprites/{sprite}A{rotation}.png", ImageCodec.EncodePng(MeshBaker.Bake(mesh, 1, (0, 0, 0), rotation, 1))));
        var radius = Math.Max(4, (int)Math.Ceiling(mesh.Reach));
        var height = Math.Max(4, voxels.Up);
        var title = Title(Path.GetFileNameWithoutExtension(path));
        var script = $"zscript/wadsmith/{className}.zs";
        files.Add((script, Encoding.UTF8.GetBytes(Declare(className, title, sprite, radius, height))));
        project.WriteFiles(files);

        var c = CultureInfo.InvariantCulture;
        var number = FreeNumber(project, into);
        project.Record(
        [
            new CopiedAsset { Kind = "zscript", Game = "model", Original = className, Name = className, File = script },
            new CopiedAsset { Kind = "voxeldef", Game = "model", Original = className, Name = VoxelLine(sprite, 0), File = VoxelFile },
            new CopiedAsset { Kind = "actor", Game = "model", Original = className, Name = number.ToString(c), File = script },
            new CopiedAsset { Kind = "modelprop", Game = "model", Original = className, Name = string.Join('|', sprite, radius.ToString(c), height.ToString(c), title, Path.GetFileName(path), "0"), File = file },
        ]);
        DukeBorrowing.WriteScript(project);
        WriteVoxelDefinitions(project);

        var thing = ThingOf(number, className, sprite, radius, height, title);
        into.Carry([thing], project.Folder);
        return new ImportedModel(new CarriedActor(number, thing, 8, 0, true), 1, $"{voxels.Count} voxels, one map unit each: {height} units tall in the map", notes);
    }

    /// <summary>A voxel prop's VOXELDEF line: frame A of its sprite drawn as the voxel of the same name, turned by <paramref name="turn"/> degrees.</summary>
    private static string VoxelLine(string sprite, double turn) =>
        turn % 360 == 0 ? $"{sprite}A = \"{sprite}\" {{ Scale = 1.0 }}" : string.Create(CultureInfo.InvariantCulture, $"{sprite}A = \"{sprite}\" {{ Scale = 1.0 AngleOffset = {turn % 360} }}");

    /// <summary>Writes the project's VOXELDEF from its voxel props; a VOXELDEF of the user's own is left alone.</summary>
    public static void WriteVoxelDefinitions(Project project)
    {
        var path = Path.Combine(project.Folder, VoxelFile);
        if (File.Exists(path))
        {
            using var reader = new StreamReader(path);
            if (reader.ReadLine() != VoxelMark) return;
        }

        var lines = project.CopiedNow().Where(c => c.Kind == "voxeldef").Select(c => c.Name).ToList();
        if (lines.Count == 0) return;
        project.WriteFile(VoxelFile, Encoding.UTF8.GetBytes(VoxelMark + "\n\n" + string.Join("\n", lines) + "\n"));
    }

    /// <summary>
    /// The model a prop of a project (<paramref name="copied"/>, its record) draws in the 3D view, read from the project's own copy in
    /// <paramref name="folder"/>: its shape, each surface's picture (<c>skinN.png</c>), and the scale and offset its MODELDEF
    /// entry gives it (so an entry edited by hand shows as GZDoom will draw it). Null when it is not a prop or cannot be read.
    /// Reads files: call it on a worker.
    /// </summary>
    public static Scene.PropShape? Shape(string folder, IReadOnlyList<CopiedAsset> copied, int number)
    {
        var className = copied.FirstOrDefault(c => c.Kind == "actor" && c.Game == "model" && c.Name == number.ToString(CultureInfo.InvariantCulture))?.Original;
        if (className is null || copied.FirstOrDefault(c => c.Kind == "modelprop" && c.Original == className) is not { } prop) return null;
        var dir = Path.Combine(folder, prop.File);
        try
        {
            // A voxel prop: its KVX, one map unit a voxel, not squashed (GZDoom draws voxels at the game's pixel shape).
            if (IsVoxels(prop))
            {
                var voxels = Kvx.Read(File.ReadAllBytes(dir), prop.File).ToMesh();
                var turned = TurnOf(copied, className);
                return new Scene.PropShape(voxels.Turned(turned), 1, (0, 0, 0), $"{className}@{turned.ToString(CultureInfo.InvariantCulture)}", [voxels.Surfaces[0].Image], 1);
            }

            var (mesh, scale, turn, origin) = Definition(dir);
            var pictures = new RgbaImage?[mesh.Surfaces.Count];
            for (var i = 0; i < pictures.Length; i++)
            {
                var skin = Path.Combine(dir, $"skin{i}.png");
                pictures[i] = File.Exists(skin) ? ImageCodec.Decode(File.ReadAllBytes(skin), skin) : mesh.Surfaces[i].Image;
            }

            // Keyed by the turn too, so a turned model is a new picture set in the view.
            return new Scene.PropShape(mesh.Turned(turn), scale, origin, $"{className}@{turn.ToString(CultureInfo.InvariantCulture)}", pictures, Stretch);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException or FormatException)
        {
            App.Log.Warn($"The model of {className} could not be read for the 3D view: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// A prop's MODELDEF entry in <paramref name="dir"/> as GZDoom reads it: the model (unturned), its scale, its AngleOffset
    /// (degrees anticlockwise, applied to the model before the offset) and the point of the turned model its Offset stands on
    /// the object's spot.
    /// </summary>
    private static (Mesh Mesh, double Scale, double Turn, (float X, float Y, float Z) Origin) Definition(string dir)
    {
        var definition = File.ReadAllText(Path.Combine(dir, "modeldef.txt"));
        var file = Regex.Match(definition, "(?im)^\\s*Model\\s+0\\s+\"([^\"]+)\"") is { Success: true } m ? m.Groups[1].Value : "model.obj";
        var path = Path.Combine(dir, file);
        var mesh = Path.GetExtension(path).ToLowerInvariant() switch { ".md3" => Md3Model.Read(path), ".iqm" => IqmModel.Read(path), ".md2" => Md2.Read(path).ToMesh(), _ => ObjModel.Read(path) };
        var scale = Regex.Match(definition, @"(?im)^\s*Scale\s+([-0-9.eE]+)") is { Success: true } s ? double.Parse(s.Groups[1].Value, CultureInfo.InvariantCulture) : 1;
        var turn = Regex.Match(definition, @"(?im)^\s*AngleOffset\s+([-0-9.eE]+)") is { Success: true } a ? double.Parse(a.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var offset = Regex.Match(definition, @"(?im)^\s*Offset\s+([-0-9.eE]+)\s+([-0-9.eE]+)\s+([-0-9.eE]+)");
        float Back(int group) => offset.Success && scale != 0 ? (float)(-double.Parse(offset.Groups[group].Value, CultureInfo.InvariantCulture) / scale) : 0;
        return (mesh, scale, turn, (Back(1), Back(2), Back(3)));
    }

    private static bool IsVoxels(CopiedAsset prop) => prop.File.EndsWith(".kvx", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Turns a voxel prop: its VOXELDEF line's AngleOffset (GZDoom turns a voxel about its pivot, the middle of its base, so it
    /// needs no offset), the sprites baked again, the turn in the record.
    /// </summary>
    private static void TurnVoxels(Project project, CopiedAsset prop, double degrees)
    {
        var sprite = prop.Name.Split('|')[0];
        var mesh = Kvx.Read(File.ReadAllBytes(Path.Combine(project.Folder, prop.File)), prop.File).ToMesh().Turned(degrees);
        var files = new List<(string, byte[])>();
        for (var rotation = 1; rotation <= 8; rotation++) files.Add(($"sprites/{sprite}A{rotation}.png", ImageCodec.EncodePng(MeshBaker.Bake(mesh, 1, (0, 0, 0), rotation, 1))));
        project.WriteFiles(files);
        if (project.CopiedNow().FirstOrDefault(c => c.Kind == "voxeldef" && c.Original == prop.Original) is { } line) project.Replace(line, line with { Name = VoxelLine(sprite, degrees) });
        WriteVoxelDefinitions(project);
        var parts = prop.Name.Split('|').ToList();
        while (parts.Count < 6) parts.Add("");
        parts[5] = (degrees % 360).ToString(CultureInfo.InvariantCulture);
        project.Replace(prop, prop with { Name = string.Join('|', parts) });
    }

    /// <summary>How far a prop's model is turned from the way it was made, in degrees anticlockwise, as the project's record keeps it (no file is read: any thread).</summary>
    public static double TurnOf(IReadOnlyList<CopiedAsset> copied, string className) =>
        copied.FirstOrDefault(c => c.Kind == "modelprop" && c.Original == className)?.Name.Split('|') is { Length: >= 6 } parts
        && double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var turn) ? turn : 0;

    /// <summary>
    /// Turns a prop's model to face <paramref name="degrees"/> anticlockwise of the way it was made (a model made facing another
    /// way than Doom's forward): MODELDEF's AngleOffset, the offset that stands the turned model on its base and middle, and
    /// the eight sprites baked again. Its size and class stay. Reads and writes files: call it on a worker.
    /// </summary>
    public static void Turn(Project project, string className, double degrees)
    {
        var prop = project.CopiedNow().FirstOrDefault(c => c.Kind == "modelprop" && c.Original == className) ?? throw new CorruptDataException($"{className} is not a model of this project.");
        var dir = Path.Combine(project.Folder, prop.File);
        if (IsVoxels(prop))
        {
            TurnVoxels(project, prop, degrees);
            return;
        }

        var (mesh, scale, _, _) = Definition(dir);
        var pictures = Enumerable.Range(0, mesh.Surfaces.Count).Select(i => Path.Combine(dir, $"skin{i}.png")).Select(p => File.Exists(p) ? ImageCodec.Decode(File.ReadAllBytes(p), p) : null).ToList();
        var turned = new Mesh([.. mesh.Turned(degrees).Surfaces.Select((s, i) => new MeshSurface(s.Name, s.Triangles, s.Picture, pictures[i] ?? s.Image))]);
        var origin = ((turned.Min.X + turned.Max.X) / 2, (turned.Min.Y + turned.Max.Y) / 2, turned.Min.Z);
        var c = CultureInfo.InvariantCulture;
        string Offset(float at) => (Math.Round(-at * scale, 3) + 0.0).ToString("0.###", c);

        // AngleOffset just before the Offset line (written once), the Offset for the turned model.
        var definition = File.ReadAllText(Path.Combine(dir, "modeldef.txt"));
        definition = Regex.Replace(definition, @"(?im)^\s*AngleOffset\s+[-0-9.eE]+\r?\n", "");
        var turnLine = degrees % 360 == 0 ? "" : $"\tAngleOffset {(degrees % 360).ToString(c)}\n";
        definition = Regex.Replace(definition, @"(?im)^\s*Offset\s+[-0-9.eE]+\s+[-0-9.eE]+\s+[-0-9.eE]+\r?\n", _ => $"{turnLine}\tOffset {Offset(origin.Item1)} {Offset(origin.Item2)} {Offset(origin.Item3)}\n");
        var sprite = prop.Name.Split('|')[0];
        var files = new List<(string, byte[])> { ($"{prop.File}/modeldef.txt", Encoding.UTF8.GetBytes(definition)) };
        for (var rotation = 1; rotation <= 8; rotation++) files.Add(($"sprites/{sprite}A{rotation}.png", ImageCodec.EncodePng(MeshBaker.Bake(turned, scale, origin, rotation, Stretch))));
        project.WriteFiles(files);

        // The record keeps the turn too, for the Inspector to read without the file.
        var parts = prop.Name.Split('|').ToList();
        while (parts.Count < 6) parts.Add("");
        parts[5] = (degrees % 360).ToString(c);
        project.Replace(prop, prop with { Name = string.Join('|', parts) });
    }

    /// <summary>The catalogue entry of a prop: a solid decoration showing its baked sprites.</summary>
    public static ThingType ThingOf(int number, string className, string sprite, int radius, int height, string title) =>
        new(number, className, title, sprite, 'A', radius, height, 1, 1, false, ThingCategory.Decoration);

    /// <summary>The props a project's record lists, as catalogue entries (for <see cref="GameResources.Load"/>).</summary>
    public static IEnumerable<ThingType> Recorded(ProjectInfo info)
    {
        foreach (var prop in info.Copied.Where(c => c.Kind == "modelprop"))
        {
            var parts = prop.Name.Split('|');
            var number = info.Copied.FirstOrDefault(c => c.Kind == "actor" && c.Game == "model" && c.Original == prop.Original)?.Name;
            if (parts.Length < 4 || number is null) continue;
            yield return ThingOf(int.Parse(number, CultureInfo.InvariantCulture), prop.Original, parts[0], int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture), parts[3]);
        }
    }

    /// <summary>The class: a solid decoration of the model's size that stands still on its first baked frame.</summary>
    public static string Declare(string className, string title, string sprite, int radius, int height) =>
        $"// {title}, a 3D model brought in with WadSmith; its MODELDEF draws the model, the sprites stand in where models are not drawn.\n" +
        $"class {className} : Actor\n{{\n\tDefault\n\t{{\n\t\tTag \"{title.Replace("\"", "'", StringComparison.Ordinal)}\";\n\t\tRadius {radius};\n\t\tHeight {height};\n\t\t+SOLID;\n\t}}\n\n" +
        $"\tStates\n\t{{\n\tSpawn:\n\t\t{sprite} A -1;\n\t\tStop;\n\t}}\n}}\n";

    /// <summary>Writes the project's MODELDEF: an include for every prop's entry; a MODELDEF of the user's own is left alone.</summary>
    public static void WriteDefinitions(Project project)
    {
        var path = Path.Combine(project.Folder, DefinitionFile);
        if (File.Exists(path))
        {
            using var reader = new StreamReader(path);
            if (reader.ReadLine() != DefinitionMark) return;
        }

        var includes = project.Info.Copied.Where(c => c.Kind == "modeldef").Select(c => $"#include \"{c.File}\"").Distinct(StringComparer.Ordinal).ToList();
        if (includes.Count == 0) return;
        project.WriteFile(DefinitionFile, Encoding.UTF8.GetBytes(DefinitionMark + "\n\n" + string.Join("\n", includes) + "\n"));
    }

    /// <summary>"old_barrel-2" → "Old barrel 2".</summary>
    public static string Title(string fileName)
    {
        var words = new string([.. fileName.Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "Model";
        var text = string.Join(' ', words).ToLowerInvariant();
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>A class name from the file's name ("Model" + its letters) that neither the game nor the project has.</summary>
    private static string ClassName(Project project, GameResources into, string fileName)
    {
        var letters = string.Concat(Title(fileName).Split(' ').Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        var stem = "Model" + new string([.. letters.Where(char.IsAsciiLetterOrDigit)]);
        if (stem.Length > 40) stem = stem[..40];
        var taken = new HashSet<string>(project.Info.Copied.Select(c => c.Original), StringComparer.OrdinalIgnoreCase);
        var name = stem;
        for (var i = 2; taken.Contains(name) || into.Things.FindClass(name) is not null; i++) name = stem + i.ToString(CultureInfo.InvariantCulture);
        return name;
    }

    /// <summary>A four-letter sprite name, "M" and three more, that neither the game's files nor the project's props use.</summary>
    private static string SpriteName(Project project, GameResources into)
    {
        var used = new HashSet<string>(project.Info.Copied.Where(c => c.Kind == "modelprop").Select(c => c.Name.Split('|')[0]), StringComparer.OrdinalIgnoreCase);
        const string symbols = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        for (var i = 0; i < symbols.Length * symbols.Length * symbols.Length; i++)
        {
            var name = $"M{symbols[i / (36 * 36)]}{symbols[i / 36 % 36]}{symbols[i % 36]}";
            if (!used.Contains(name) && into.Sprites.Frames(name).Count == 0) return name;
        }

        throw new CorruptDataException("Every sprite name WadSmith gives models is taken in this project.");
    }

    /// <summary>A number this game and the project leave free, from the first carried-object number up.</summary>
    private static int FreeNumber(Project project, GameResources into)
    {
        var taken = new HashSet<int>(into.Things.All.Select(t => t.Number));
        foreach (var c in project.Info.Copied.Where(c => c.Kind == "actor")) taken.Add(int.Parse(c.Name, CultureInfo.InvariantCulture));
        var number = ActorBorrowing.FirstNumber;
        while (taken.Contains(number)) number++;
        return number;
    }
}
