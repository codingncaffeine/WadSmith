using WadSmith.Core.Maps;

namespace WadSmith.Editor;

/// <summary>
/// The map a new project starts with: one lit room with a player start in the middle, so Play works
/// before anything is drawn. The textures are each game's classic look.
/// </summary>
public static class StarterRoom
{
    public static MapModel Create(string slot, string engine)
    {
        var (floor, ceiling, wall) = engine switch
        {
            "heretic" => ("FLOOR03", "FLAT520", "GRSTNPB"),
            "hexen" => ("F_011", "F_032", "FOREST01"),
            "strife" => ("F_FLOOR1", "F_CEIL1", "BRKGRY01"),
            _ => ("FLOOR4_8", "CEIL3_5", "STARTAN3"),
        };

        var m = new MapModel { Name = slot, Namespace = "zdoom" };
        m.Vertices.AddRange([new MapVertex { X = -256, Y = -256 }, new MapVertex { X = 256, Y = -256 }, new MapVertex { X = 256, Y = 256 }, new MapVertex { X = -256, Y = 256 }]);
        m.Sectors.Add(new MapSector { FloorHeight = 0, CeilingHeight = 128, FloorTexture = floor, CeilingTexture = ceiling, Light = 192 });

        // Walls run clockwise around the room so the room is on each wall's front (right-hand) side.
        int[] order = [0, 3, 2, 1];
        for (var i = 0; i < 4; i++)
        {
            m.Sides.Add(new MapSide { Middle = wall, Sector = 0 });
            var line = new MapLine { V1 = order[i], V2 = order[(i + 1) % 4], Front = i };
            line.SetFlag("blocking", true);
            m.Lines.Add(line);
        }

        var start = new MapThing { X = 0, Y = -128, Angle = 90, Type = 1 };
        foreach (var f in new[] { "skill1", "skill2", "skill3", "skill4", "skill5", "single", "coop", "dm" }) start.SetFlag(f, true);
        m.Things.Add(start);
        return m;
    }
}
