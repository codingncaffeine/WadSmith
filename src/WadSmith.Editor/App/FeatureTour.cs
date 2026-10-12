using System.Text;
using WadSmith.Core.Maps;
using WadSmith.Core.Projects;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Editor.App;

/// <summary>
/// The feature tour (D32): a small playable museum built for the user's own game from an edit script, so it holds no game
/// content: a hall with a room for each feature (door, lift, stairs, window, light, secret, locked door, switch,
/// teleporter, exit), each with a sign the game shows when the player walks in.
/// </summary>
public static class FeatureTour
{
    /// <summary>
    /// The tour as an edit script. Lines starting "doom:" (keys and locks, which are Doom's) run for the Doom family only.
    /// A sign may not hold '#', which starts a comment.
    /// </summary>
    public const string Script = """
        # The hall, and where the player starts, facing east along it.
        room 0 0 2560 256
        ceiling 1280 128 160
        start 128 128 0
        sign 1280 128 Welcome to WadSmith's tour. Each room off this hall shows one thing the editor makes: walk in and read its sign.

        # North side, west to east: door, lift, stairs, window, light.
        room 0 256 512 512
        room 0 512 512 768
        door 256 512
        sign 256 384 A door. Walk up to it and press Use: it opens, and closes again by itself.

        room 512 256 1024 512
        room 512 512 1024 768
        ceiling 768 384 192
        ceiling 768 640 320
        floor 768 640 128
        lift 768 640
        sign 768 384 A lift. Press Use on the wall of the raised floor: it comes down, waits, and carries you up.

        room 1024 256 1536 768
        ceiling 1280 512 352
        stairs 1088 320 1472 704
        sign 1056 288 Stairs. No step rises more than the player can climb: the Stairs tool cuts them for you.

        room 1536 256 2048 512
        room 1536 512 2048 768
        window 1792 512
        sign 1792 384 A window. You see the room behind through it, but cannot climb in.

        room 2048 256 2560 768
        brightness 2304 512 80
        light 2304 512
        sign 2304 300 A light. The room is dim, and the light's glow shows round it; its colour and reach are in the Inspector.

        # South side, west to east: secret, locked door, switch, teleporter, exit.
        room 0 -512 512 0
        room 192 -640 320 -512
        door 256 -512
        secret 256 -600
        sign 256 -256 A secret. The little room behind the door counts toward the map's secrets when you step in.

        doom: room 512 -512 1024 0
        doom: room 640 -768 896 -512
        doom: door 768 -512
        doom: lock 768 -520 red
        doom: key red 640 -256
        doom: sign 768 -256 A locked door. Pick up the red key in this room, and the door opens for you.

        room 1024 -512 1536 0
        room 1152 -768 1408 -512
        door 1280 -512
        switch 1280 -520 1088 -512
        sign 1280 -256 A switch. Press Use on the switch on the back wall: it opens the door beside it.

        room 1536 -512 2048 0
        room 1728 -320 1856 -192
        teleport 1792 -256 1280 200
        sign 1600 -100 A teleporter. Step onto the pad in the middle and it sends you back to the hall.

        room 2048 -512 2560 0
        exit 2304 -512
        sign 2304 -256 The exit. Press Use on the switch on the far wall to end the map.
        """;

    /// <summary>The script as it runs for a game of <paramref name="engine"/>'s family.</summary>
    public static string ScriptFor(string engine)
    {
        var doom = engine is "doom" or "chex";
        var lines = Script.Split('\n').Select(l => l.TrimStart()).Where(l => doom || !l.StartsWith("doom:", StringComparison.Ordinal)).Select(l => l.StartsWith("doom:", StringComparison.Ordinal) ? l[5..].TrimStart() : l);
        return string.Join('\n', lines);
    }

    /// <summary>The tour's map, built for <paramref name="engine"/>; throws when a line of the script fails (the tests run every family).</summary>
    public static EditorMap Build(string slot, string engine)
    {
        var map = EditorMap.FromModel(new MapModel { Name = slot, Namespace = "zdoom" });
        var failed = EditScript.Run(map, ScriptFor(engine), Look.For(engine), engine).Where(s => !s.Done).ToList();
        if (failed.Count > 0) throw new InvalidOperationException($"The tour's line {failed[0].Line} ({failed[0].Text}) failed: {failed[0].Said}");
        return map;
    }

    /// <summary>
    /// Makes the tour as a project in <paramref name="folder"/> for <paramref name="gameId"/>: its map in the game's first slot,
    /// and the sign handler with the MAPINFO that registers it. Writes files: call it on a worker.
    /// </summary>
    public static Project Create(string folder, string gameId, string engine, string slot)
    {
        var project = Project.Create(folder, "Feature tour", gameId);
        project.SaveMap(Build(slot, engine).Frame.ToModel(), "WadSmith's feature tour", new MapOrigin { Kind = "new" });
        project.WriteFile("zscript.txt", Encoding.UTF8.GetBytes(Signs.ZScript));
        project.WriteFile(ProjectMapInfo.FileName, Encoding.UTF8.GetBytes(Signs.MapInfo));
        return project;
    }
}
