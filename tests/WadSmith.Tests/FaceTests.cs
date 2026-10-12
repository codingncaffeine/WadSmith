using WadSmith.App.ViewModels;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Scene;

namespace WadSmith.Tests;

/// <summary>Wall faces: picked one side at a time in 3D, each wall owning its own, and shown in the Inspector.</summary>
public sealed class FaceTests
{
    /// <summary>Two rooms sharing the wall at x = 128 (front faces the west room), a grate on both of its faces.</summary>
    private static (EditorMap Map, Line Shared) GratedRooms()
    {
        var m = SceneTests.TwoRooms(0, 128);
        m.Sides[^1].Middle = "GRATE";
        m.Sides[^2].Middle = "GRATE";
        var map = EditorMap.FromModel(m);
        return (map, map.Lines[^1]);
    }

    private static MapScene Scene(EditorMap map) => MapScene.Build(MapGeometry.Build(map.Frame), _ => (64, 128));

    [Fact]
    public void EachSideOfAWallIsPickedAsItsOwnFace()
    {
        var (map, shared) = GratedRooms();
        var scene = Scene(map);

        // From the west room looking east, the shared wall's front face; from the east room looking west, its back face.
        Assert.Same(shared.Front, scene.Pick((64, 64, 64), (1, 0, 0), 0));
        Assert.Same(shared.Back, scene.Pick((192, 64, 64), (-1, 0, 0), 180));
    }

    [Fact]
    public void AFaceIsTintedAloneAndAWallWithBothFaces()
    {
        var (map, shared) = GratedRooms();
        var scene = Scene(map);
        Assert.Equal(6 * SceneBatch.Stride, scene.Highlight(new HashSet<Element> { shared.Front }).Surfaces.Length);
        Assert.Equal(6 * SceneBatch.Stride, scene.Highlight(new HashSet<Element> { shared.Back! }).Surfaces.Length);
        Assert.Equal(12 * SceneBatch.Stride, scene.Highlight(new HashSet<Element> { shared }).Surfaces.Length);
    }

    [Fact]
    public void OnThePlanAFaceRunsWithItsRoomOnTheRight()
    {
        var (map, shared) = GratedRooms();
        var geometry = MapGeometry.Build(map.Frame);
        var look = new App.Canvas.MapLook(null);

        // The shared wall runs from (128, 128) to (128, 0): the front face looks west, into the first room.
        var front = App.Canvas.SelectionOverlay.Take([shared.Front], geometry, look);
        Assert.Empty(front.Lines);
        Assert.Equal((128f, 128f, 128f, 0f), Assert.Single(front.Faces));
        var back = App.Canvas.SelectionOverlay.Take([shared.Back!], geometry, look);
        Assert.Equal((128f, 0f, 128f, 128f), Assert.Single(back.Faces));

        // The whole wall is drawn as a wall, without a face's tick.
        var wall = App.Canvas.SelectionOverlay.Take([shared], geometry, look);
        Assert.Single(wall.Lines);
        Assert.Empty(wall.Faces);
    }

    [Fact]
    public void EveryFaceKnowsItsWall()
    {
        var (map, shared) = GratedRooms();
        foreach (var l in map.Lines)
        {
            Assert.Same(l, l.Front.Line);
            if (l.Back is { } b) Assert.Same(l, b.Line);
        }

        Assert.Same(shared.Back, shared.Opposite(shared.Front));
        Assert.Same(shared.Front, shared.Opposite(shared.Back!));

        // Moving a face to another wall moves its back-pointer; the face it replaced belongs to no wall.
        var old = shared.Back!;
        var fresh = new Side(old.Sector);
        shared.Back = fresh;
        Assert.Same(shared, fresh.Line);
        Assert.Null(old.Line);
    }

    /// <summary>A packed classic map: two walls of a triangle room share one face.</summary>
    private static MapModel Packed()
    {
        var m = new MapModel { Name = "MAP01", Namespace = "zdoom" };
        m.Vertices.AddRange([new MapVertex { X = 0, Y = 0 }, new MapVertex { X = 0, Y = 128 }, new MapVertex { X = 128, Y = 0 }]);
        m.Sectors.Add(new MapSector { CeilingHeight = 128, FloorTexture = "F", CeilingTexture = "C" });
        m.Sides.Add(new MapSide { Sector = 0, Middle = "STONE" });
        m.Sides.Add(new MapSide { Sector = 0, Middle = "BRICK" });
        m.Lines.Add(new MapLine { V1 = 0, V2 = 1, Front = 0 });
        m.Lines.Add(new MapLine { V1 = 1, V2 = 2, Front = 0 });
        m.Lines.Add(new MapLine { V1 = 2, V2 = 0, Front = 1 });
        return m;
    }

    private static string Udmf(MapModel m) => UdmfText.WriteString(MapConversion.ToUdmf(m));

    [Fact]
    public void ASharedFaceBecomesOneFacePerWallAndIsWrittenBackAsOne()
    {
        var model = Packed();
        var map = EditorMap.FromModel(model);
        Assert.NotSame(map.Lines[0].Front, map.Lines[1].Front);
        Assert.Equal("STONE", map.Lines[1].Front.Middle);
        Assert.Same(map.Lines[1], map.Lines[1].Front.Line);

        var back = map.ToModel();
        Assert.Equal(2, back.Sides.Count);
        Assert.Equal(0, back.Lines[1].Front);
        Assert.Equal(Udmf(model), Udmf(back));
    }

    [Fact]
    public void ChangingOneWallsFaceLeavesTheWallItWasSharedWithAlone()
    {
        var map = EditorMap.FromModel(Packed());
        var history = new History(map);
        var face = map.Lines[1].Front;
        history.Run(new SetProperty<Side, string>(face, "Change wall texture", s => s.Middle, (s, v) => s.Middle = v, "METAL"));
        Assert.Equal("STONE", map.Lines[0].Front.Middle);

        var changed = map.ToModel();
        Assert.Equal(3, changed.Sides.Count);
        Assert.Equal("STONE", changed.Sides[changed.Lines[0].Front].Middle);
        Assert.Equal("METAL", changed.Sides[changed.Lines[1].Front].Middle);
        Assert.Equal("BRICK", changed.Sides[changed.Lines[2].Front].Middle);

        // Undone, the two match again and the file is the packed one it was.
        history.Undo();
        Assert.Equal(Udmf(Packed()), Udmf(map.ToModel()));
    }

    public static TheoryData<string, string> GameMaps() => UdmfTests.GameMaps();

    [Theory]
    [MemberData(nameof(GameMaps))]
    public void EveryWallOfEveryMapHasFacesOfItsOwn(string fileName, string game)
    {
        var wad = Core.Containers.WadContainer.Open(GameFiles.Require(fileName));
        foreach (var lumps in MapLocator.Find(wad))
        {
            var map = EditorMap.FromModel(MapConversion.Read(lumps, game));
            var seen = new HashSet<Side>(ReferenceEqualityComparer.Instance);
            foreach (var l in map.Lines)
            {
                Assert.True(seen.Add(l.Front) && ReferenceEquals(l, l.Front.Line), $"{fileName} {lumps.Name}: a front face is shared or points at another wall");
                if (l.Back is { } b) Assert.True(seen.Add(b) && ReferenceEquals(l, b.Line), $"{fileName} {lumps.Name}: a back face is shared or points at another wall");
            }
        }
    }

    private static (EditorSession Session, TempFolder Temp) Open(MapModel m)
    {
        var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(m);
        return (EditorSession.Open(project, "MAP01", _ => { }), temp);
    }

    [Fact]
    public void TheInspectorShowsAFacesPartsWithWhetherEachIsSeen()
    {
        // The east room's floor is 32 up and its ceiling 32 down: from the west both the upper and the lower part show.
        var (session, temp) = Open(SceneTests.TwoRooms(32, 96));
        using var _ = temp;
        using var __ = session;
        var shared = session.Map.Lines[^1];
        var inspector = new InspectorViewModel(session);

        session.Selection.Set(shared.Front);
        Assert.Equal("Wall face", inspector.Heading);
        var upper = inspector.Rows.OfType<TextRow>().Single(r => r.Label == "Upper texture");
        Assert.Equal("Seen above the opening, 32 tall.", upper.HintText);
        Assert.Equal("Seen below the opening, a step of 32.", inspector.Rows.OfType<TextRow>().Single(r => r.Label == "Lower texture").HintText);

        // Seen from the east room, neither part shows.
        session.Selection.Set(shared.Back!);
        Assert.Equal("Not seen now: the ceiling beyond is not lower.", inspector.Rows.OfType<TextRow>().Single(r => r.Label == "Upper texture").HintText);

        // A change made here is one undoable step on this face only.
        session.Selection.Set(shared.Front);
        upper = inspector.Rows.OfType<TextRow>().Single(r => r.Label == "Upper texture");
        upper.Value = "metal";
        upper.Commit();
        Assert.Equal("METAL", shared.Front.Top);
        Assert.Equal("UPPER", shared.Back!.Top);
        session.History.Undo();
        Assert.Equal("UPPER", shared.Front.Top);
        Assert.Equal("UPPER", upper.Value);

        // The face's wall is one button away, and the other face too.
        inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Select the other face").Command.Execute(null);
        Assert.Same(shared.Back, Assert.Single(session.Selection.Items));
        inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Select the whole wall").Command.Execute(null);
        Assert.Same(shared, Assert.Single(session.Selection.Items));
    }

    [Fact]
    public void BetweenRoomsOfTheSameHeightNeitherPartIsSeen()
    {
        var (session, temp) = Open(SceneTests.TwoRooms(0, 128));
        using var _ = temp;
        using var __ = session;
        var inspector = new InspectorViewModel(session);
        session.Selection.Set(session.Map.Lines[^1].Front);
        Assert.Equal("Not seen now: the ceiling beyond is not lower.", inspector.Rows.OfType<TextRow>().Single(r => r.Label == "Upper texture").HintText);
        Assert.Equal("Not seen now: the floor beyond is not higher.", inspector.Rows.OfType<TextRow>().Single(r => r.Label == "Lower texture").HintText);
    }

    [Fact]
    public void ASolidWallsFaceHasOneTexture()
    {
        var (session, temp) = Open(SceneTests.TwoRooms(0, 128));
        using var _ = temp;
        using var __ = session;
        var inspector = new InspectorViewModel(session);
        session.Selection.Set(session.Map.Lines[0].Front);
        var textures = inspector.Rows.OfType<TextRow>().Select(r => r.Label).ToList();
        Assert.Equal(["Texture"], textures);
        Assert.Contains(inspector.Rows.OfType<FlagRow>(), r => r.Label == "Texture sits on the floor");
    }
}
