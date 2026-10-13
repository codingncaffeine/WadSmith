using WadSmith.Core;
using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Core.Models;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;

namespace WadSmith.Tests;

/// <summary>3D models brought in as props (Phase 11): read in GZDoom's axes, baked into sprites, and written as a project's MODELDEF, ZScript and catalogue entry.</summary>
public sealed class ModelPropTests
{
    /// <summary>
    /// A box 2 m deep along OBJ's X (the way it faces), 3 m tall (OBJ's Y), 4 m wide along OBJ's Z; its front (+X) face red,
    /// the rest blue, through an .mtl library whose picture names differ in letter case from the files.
    /// </summary>
    internal static string WriteBox(string folder, string name = "crate_box")
    {
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "red.png"), ImageCodec.EncodePng(Solid(255, 0, 0)));
        File.WriteAllBytes(Path.Combine(folder, "blue.png"), ImageCodec.EncodePng(Solid(0, 0, 255)));
        File.WriteAllText(Path.Combine(folder, name + ".mtl"), "newmtl front\nmap_Kd -s 1 1 1 RED.PNG\nnewmtl side\nmap_Kd blue.png\n");
        var text = $"mtllib {name}.mtl\n" +
            "v -1 0 -2\nv 1 0 -2\nv 1 0 2\nv -1 0 2\nv -1 3 -2\nv 1 3 -2\nv 1 3 2\nv -1 3 2\n" +
            "vt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\n" +
            "usemtl front\nf 2/1 3/2 7/3 6/4\n" +
            "usemtl side\nf 1/1 4/2 8/3 5/4\nf 1/1 2/2 6/3 5/4\nf 4/1 3/2 7/3 8/4\nf 1/1 2/2 3/3 4/4\nf 5/1 6/2 7/3 8/4\n";
        var path = Path.Combine(folder, name + ".obj");
        File.WriteAllText(path, text);
        return path;
    }

    private static RgbaImage Solid(byte r, byte g, byte b)
    {
        var image = new RgbaImage(4, 4);
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++) image.Set(x, y, r, g, b);
        }

        return image;
    }

    [Fact]
    public void AnObjIsReadInGZDoomsAxesWithItsMaterialsPictures()
    {
        using var temp = new TempFolder();
        var mesh = ObjModel.Read(WriteBox(temp.Path));
        // OBJ (x, y, z) → (x, -z, y): Y up becomes Z up, Z flipped becomes Y.
        Assert.Equal((-1f, -2f, 0f), mesh.Min);
        Assert.Equal((1f, 2f, 3f), mesh.Max);
        Assert.Equal(12, mesh.TriangleCount);
        Assert.Equal(["front", "side"], mesh.Surfaces.Select(s => s.Name));
        Assert.Equal((255, 0, 0, 255), mesh.Surfaces[0].Image!.Get(0, 0));
        Assert.Equal((0, 0, 255, 255), mesh.Surfaces[1].Image!.Get(0, 0));
    }

    [Fact]
    public void AMaterialOfAColourAloneIsAPictureOfThatColourAndWindowsPathsAreFound()
    {
        // As Blender writes them on Windows: a picture under textures\ for the body, black eyes with a colour and no picture.
        using var temp = new TempFolder();
        Directory.CreateDirectory(Path.Combine(temp.Path, "textures"));
        File.WriteAllBytes(Path.Combine(temp.Path, "textures", "Body.jpg"), ImageCodec.EncodePng(Solid(0, 255, 0)));
        File.WriteAllText(Path.Combine(temp.Path, "s.mtl"), "newmtl Body\nKd 0.8 0.8 0.8\nmap_Kd textures\\Body.jpg\nmap_Bump  textures\\\\nothing.jpg\n\nnewmtl Eye\nKd 0.000000 0.000000 0.000000\n");
        var path = Path.Combine(temp.Path, "s.obj");
        File.WriteAllText(path, "mtllib s.mtl\nv 0 0 0\nv 1 0 0\nv 0 1 0\nusemtl Eye\nf 1 2 3\nusemtl Body\nf 1 2 3\n");
        var mesh = ObjModel.Read(path);
        Assert.Equal((0, 0, 0, 255), mesh.Surfaces[0].Image!.Get(0, 0));
        Assert.Null(mesh.Surfaces[0].Picture);
        Assert.Equal((0, 255, 0, 255), mesh.Surfaces[1].Image!.Get(0, 0));
    }

    [Fact]
    public void ABrokenObjSaysWhereAndAMissingPictureIsANote()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "bad.obj");
        File.WriteAllText(path, "v 0 0 0\nv 1 0 0\nf 1 2 9\n");
        var error = Assert.Throws<CorruptDataException>(() => ObjModel.Read(path));
        Assert.Contains("line 3", error.Message, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(temp.Path, "m.mtl"), "newmtl a\nmap_Kd broken.png\n");
        File.WriteAllText(Path.Combine(temp.Path, "broken.png"), "not a picture");
        File.WriteAllText(path, "mtllib m.mtl\nv 0 0 0\nv 1 0 0\nv 0 1 0\nusemtl a\nf 1 2 3\n");
        var notes = new List<string>();
        var mesh = ObjModel.Read(path, notes);
        Assert.Null(mesh.Surfaces[0].Image);
        Assert.Contains(notes, n => n.Contains("broken.png", StringComparison.Ordinal));
    }

    [Fact]
    public void TheBakedRotationsSeeTheModelFromWhereDoomsRotationsLook()
    {
        using var temp = new TempFolder();
        var mesh = ObjModel.Read(WriteBox(temp.Path));
        var (scale, note) = ModelProps.SizeOf(mesh);
        Assert.Equal(32 * 1.2, scale, 6);
        Assert.Contains("metres", note, StringComparison.Ordinal);
        var origin = (0f, 0f, 0f);

        // Rotation 1 looks at its front: red, as wide as the box is wide (4 m), 3 m = 96 units tall, foot at the bottom.
        var front = MeshBaker.Bake(mesh, scale, origin, 1);
        Assert.InRange(front.Width, 153, 156);
        Assert.InRange(front.Height, 96, 98);
        Assert.InRange(front.OffsetY, 96, 98);
        Assert.InRange(front.OffsetX, 76, 78);
        var (r, _, b, a) = front.Get(front.Width / 2, front.Height / 2);
        Assert.True(a == 255 && r > 0 && b == 0, $"rotation 1 shows {r},{b}");

        // Rotation 3 looks at its left side (2 m deep), rotation 5 at its back: blue, not red.
        var left = MeshBaker.Bake(mesh, scale, origin, 3);
        Assert.InRange(left.Width, 76, 79);
        foreach (var rotation in new[] { 3, 5, 7 })
        {
            var image = MeshBaker.Bake(mesh, scale, origin, rotation);
            var (rr, _, bb, aa) = image.Get(image.Width / 2, image.Height / 2);
            Assert.True(aa == 255 && rr == 0 && bb > 0, $"rotation {rotation} shows {rr},{bb}");
        }

        // Corners left of the box's outline stay see-through at 45°: the box does not fill its whole picture.
        var angled = MeshBaker.Bake(mesh, scale, origin, 2);
        Assert.Equal(0, angled.Get(0, 0).A);
    }

    [Fact]
    public void SizesAreReadFromHowTallTheModelIs()
    {
        static Mesh Tall(float z) => new([new MeshSurface("", [new(0, 0, 0, 0, 0), new(1, 0, 0, 0, 0), new(0, 0, z, 0, 0)], null, null)]);
        Assert.Equal(32 * 1.2, ModelProps.SizeOf(Tall(1.8f)).Scale, 6);
        Assert.Equal(1, ModelProps.SizeOf(Tall(64)).Scale);
        Assert.Equal(32 * 1.2 / 100, ModelProps.SizeOf(Tall(180 * 10)).Scale, 6);
        Assert.Equal("Old barrel 2", ModelProps.Title("old_barrel-2"));
    }

    [Fact]
    public void AModelBecomesAPropTheProjectDrawsPlacesAndShows()
    {
        using var temp = new TempFolder();
        var path = WriteBox(Path.Combine(temp.Path, "source"));
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var wad = GameFiles.Require("DOOM2.WAD");
        var engine = GameFiles.Require("gzdoom.pk3");
        GameResources Load() => GameResources.Load("doom2", project.Folder, [new FoundGame(GameCatalog.Get("doom2")!, wad, new FileInfo(wad).Length)], engine)!;
        var doom = Load();

        var made = ModelProps.Import(project, doom, path);
        Assert.Empty(made.Notes);
        var thing = made.Actor.Thing;
        Assert.Equal(("ModelCrateBox", "Crate box", ActorBorrowing.FirstNumber), (thing.ClassName, thing.Name, made.Actor.Number));
        // 4 m wide, 2 m deep: its reach is the half-diagonal, √(2² + 1²) m; 3 m tall = 96 units.
        Assert.Equal((int)Math.Ceiling(Math.Sqrt(5) * 32 * 1.2), (int)thing.Radius);
        Assert.Equal(96, (int)thing.Height);
        Assert.True(doom.CanShow(made.Actor.Number));
        Assert.NotNull(doom.SpriteFor(made.Actor.Number));

        var folder = Path.Combine(project.Folder, "models", "ModelCrateBox");
        var obj = File.ReadAllText(Path.Combine(folder, "model.obj"));
        Assert.Contains("usemtl skin0.png", obj, StringComparison.Ordinal);
        Assert.Contains("usemtl skin1.png", obj, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(folder, "skin0.png")));
        var definition = File.ReadAllText(Path.Combine(folder, "modeldef.txt"));
        Assert.Contains("Path \"models/ModelCrateBox\"", definition, StringComparison.Ordinal);
        Assert.Contains($"FrameIndex {thing.Sprite} A 0 0", definition, StringComparison.Ordinal);
        Assert.Contains("Offset 0 0 0", definition, StringComparison.Ordinal);
        for (var rotation = 1; rotation <= 8; rotation++) Assert.True(File.Exists(Path.Combine(project.Folder, "sprites", $"{thing.Sprite}A{rotation}.png")));
        Assert.Contains("#include \"models/ModelCrateBox/modeldef.txt\"", File.ReadAllText(Path.Combine(project.Folder, ModelProps.DefinitionFile)), StringComparison.Ordinal);
        Assert.Contains("#include \"zscript/wadsmith/ModelCrateBox.zs\"", File.ReadAllText(Path.Combine(project.Folder, DukeBorrowing.ScriptFile)), StringComparison.Ordinal);
        var script = File.ReadAllText(Path.Combine(project.Folder, "zscript", "wadsmith", "ModelCrateBox.zs"));
        Assert.Contains("class ModelCrateBox : Actor", script, StringComparison.Ordinal);
        Assert.Contains($"{thing.Sprite} A -1;", script, StringComparison.Ordinal);

        // The map places it by its number; a second model gets the next number and its own names; reopened, both are there.
        ProjectMapInfo.Refresh(project, doom.Game, doom.Files, engine);
        Assert.Contains($"{ActorBorrowing.FirstNumber} = ModelCrateBox", File.ReadAllText(Path.Combine(project.Folder, ProjectMapInfo.FileName)), StringComparison.Ordinal);
        var second = ModelProps.Import(project, doom, path);
        Assert.Equal(("ModelCrateBox2", ActorBorrowing.FirstNumber + 1), (second.Actor.Thing.ClassName, second.Actor.Number));
        Assert.NotEqual(thing.Sprite, second.Actor.Thing.Sprite);
        var again = Load();
        Assert.Equal("ModelCrateBox", again.Things.Find(ActorBorrowing.FirstNumber)?.ClassName);
        Assert.Equal("ModelCrateBox2", again.Things.Find(ActorBorrowing.FirstNumber + 1)?.ClassName);
        Assert.True(again.CanShow(ActorBorrowing.FirstNumber + 1));
        Assert.Equal("ModelCrateBox", ModelProps.Of(Project.Open(project.Folder), ActorBorrowing.FirstNumber));
    }

    [Fact]
    public void TheThreeDViewStandsTheModelWhereGZDoomDrawsItAndPicksItAsTheObject()
    {
        using var temp = new TempFolder();
        var mesh = ObjModel.Read(WriteBox(temp.Path));
        var map = StarterRoom.Create("MAP01", "doom");
        var (cx, cy) = (map.Vertices.Average(v => v.X), map.Vertices.Average(v => v.Y));
        // Facing north (90°): the model's forward (+X) turns onto the map's +Y.
        map.Things.Add(new WadSmith.Core.Maps.MapThing { X = cx, Y = cy, Angle = 90, Type = 20000 });
        var geometry = WadSmith.Editor.Geometry.MapGeometry.Build(WadSmith.Editor.Model.EditorMap.FromModel(map).Frame);
        var floor = map.Sectors[0].FloorHeight;
        var prop = new WadSmith.Editor.Scene.PropShape(mesh, 38.4, (0, 0, 0), "Box", [null, null]);
        var scene = WadSmith.Editor.Scene.MapScene.Build(geometry, _ => (64, 64), _ => new WadSmith.Editor.Scene.SpriteShape(16, 16, 8, 16, 1, 1), model: t => t == 20000 ? prop : null);

        Assert.DoesNotContain(scene.Sprites, s => s.Type == 20000);
        var batches = scene.Batches.Where(b => b.Kind == WadSmith.Editor.Scene.SurfaceKind.Model).ToList();
        Assert.Equal(2, batches.Count);
        var v = batches.SelectMany(b => Enumerable.Range(0, b.VertexCount).Select(i => (X: b.Vertices[i * 8], Y: b.Vertices[i * 8 + 1], Z: b.Vertices[i * 8 + 2]))).ToList();
        // 4 m wide across its forward (now east–west), 2 m deep (now north–south), 3 m = 96 units tall on the floor.
        Assert.Equal(cx - 76.8, v.Min(p => p.X), 2);
        Assert.Equal(cx + 76.8, v.Max(p => p.X), 2);
        Assert.Equal(cy - 38.4, v.Min(p => p.Y), 2);
        Assert.Equal(cy + 38.4, v.Max(p => p.Y), 2);
        Assert.Equal(floor, v.Min(p => p.Z), 2);
        Assert.Equal(floor + 96, v.Max(p => p.Z), 2);
        // The front face (red, the first surface) is the north one.
        Assert.True(batches[0].Vertices.Where((_, i) => i % 8 == 1).All(y => Math.Abs(y - (cy + 38.4)) < 0.01));

        // A ray from the south at mid height meets the object, from outside or (both sides drawn) from inside.
        var hit = scene.Hit((cx, cy - 300, floor + 48), (0, 1, 0), 90);
        Assert.Equal(WadSmith.Editor.Scene.HitKind.Thing, hit?.Kind);
        Assert.Same(scene.Things.Single(t => t.Type == 20000).Element, hit!.Value.Element);
        Assert.NotEmpty(scene.Surface(hit.Value).Surfaces);
    }

    [Fact]
    public void ThePropsShapeIsReadBackFromTheProjectAsItsModelDefSaysIt()
    {
        using var temp = new TempFolder();
        var path = WriteBox(Path.Combine(temp.Path, "source"));
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        var wad = GameFiles.Require("freedoom2.wad");
        var game = GameResources.Load("freedoom2", project.Folder, [new FoundGame(GameCatalog.Get("freedoom2")!, wad, new FileInfo(wad).Length)], null)!;
        var made = ModelProps.Import(project, game, path);
        var shape = ModelProps.Shape(project.Folder, project.CopiedNow(), made.Actor.Number)!;
        Assert.Equal(38.4, shape.Scale, 6);
        Assert.Equal((0f, 0f, 0f), shape.Origin);
        Assert.Equal((255, 0, 0, 255), shape.Pictures[0]!.Get(0, 0));
        Assert.Null(ModelProps.Shape(project.Folder, project.CopiedNow(), 1));

        // An entry edited by hand: the view follows its scale and offset, as GZDoom would.
        var definition = Path.Combine(project.Folder, "models", made.Actor.Thing.ClassName, "modeldef.txt");
        File.WriteAllText(definition, File.ReadAllText(definition).Replace("Scale 38.4 38.4 38.4", "Scale 10 10 10", StringComparison.Ordinal).Replace("Offset 0 0 0", "Offset 5 0 -10", StringComparison.Ordinal));
        var edited = ModelProps.Shape(project.Folder, project.CopiedNow(), made.Actor.Number)!;
        Assert.Equal(10, edited.Scale);
        Assert.Equal((-0.5f, 0f, 1f), edited.Origin);
    }

    [Fact]
    public void AModelWithNoMaterialsTakesThePictureNamedLikeItAndSaysSo()
    {
        // As downloads often come: "spot_triangulated.obj" with "spot_texture.png" beside it and no .mtl.
        using var temp = new TempFolder();
        File.WriteAllBytes(Path.Combine(temp.Path, "other.png"), ImageCodec.EncodePng(Solid(0, 255, 0)));
        File.WriteAllBytes(Path.Combine(temp.Path, "spot_texture.png"), ImageCodec.EncodePng(Solid(255, 0, 0)));
        var path = Path.Combine(temp.Path, "spot_triangulated.obj");
        File.WriteAllText(path, "v 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nf 1/1 2/1 3/1\n");
        var notes = new List<string>();
        var mesh = ObjModel.Read(path, notes);
        Assert.Equal((255, 0, 0, 255), mesh.Surfaces[0].Image!.Get(0, 0));
        Assert.Equal(["No material names a picture, so spot_texture.png beside it was used."], notes);

        // Two pictures, neither named like it: none is guessed.
        File.Move(Path.Combine(temp.Path, "spot_texture.png"), Path.Combine(temp.Path, "texture.png"));
        Assert.Null(ObjModel.Read(path).Surfaces[0].Image);
    }

    [Fact]
    public void ATgaSkinIsRead()
    {
        // 2×1, 24-bit, bottom-up: the first pixel stored is the bottom-left.
        byte[] tga = [0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 1, 0, 24, 0, 255, 0, 0, 0, 255, 0];
        var image = Tga.Decode(tga);
        Assert.Equal((0, 0, 255, 255), image.Get(0, 0));
        Assert.Equal((0, 255, 0, 255), image.Get(1, 0));
        // Run-length: one packet repeating red twice.
        byte[] rle = [0, 0, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 1, 0, 24, 0x20, 0x81, 0, 0, 255];
        var packed = Tga.Decode(rle);
        Assert.Equal((255, 0, 0, 255), packed.Get(1, 0));
    }
}
