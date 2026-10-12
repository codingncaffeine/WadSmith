namespace WadSmith.Editor.Smart;

/// <summary>
/// Signs (D32): a sentence kept on a room (a UDMF user field) that WadSmith's tour shows in the game when the player walks
/// in, through a small event handler of our own (<see cref="ZScript"/>), registered by the tour's MAPINFO.
/// </summary>
public static class Signs
{
    /// <summary>The room's field that holds its sign.</summary>
    public const string Field = "user_wadsmith_sign";

    /// <summary>The event handler's class.</summary>
    public const string Handler = "WadSmithSigns";

    /// <summary>The handler: when the player comes into a room with a sign, the sign is printed in the middle of the screen.</summary>
    public const string ZScript = """
        version "4.6"

        // Written by WadSmith: shows a room's sign (its user_wadsmith_sign field) when the player walks in.
        class WadSmithSigns : EventHandler
        {
            int last;

            override void WorldLoaded(WorldEvent e)
            {
                last = -1;
            }

            override void WorldTick()
            {
                let mo = players[consoleplayer].mo;
                if (mo == null) return;
                int room = mo.CurSector.Index();
                if (room == last) return;
                last = room;
                string sign = level.GetUDMFString(LevelLocals.UDMF_Sector, room, "user_wadsmith_sign");
                if (sign.Length() > 0) Console.MidPrint(smallfont, sign);
            }
        }

        """;

    /// <summary>The MAPINFO that registers the handler.</summary>
    public const string MapInfo = """
        // Written by WadSmith for its feature tour: shows each room's sign in the game.
        gameinfo
        {
            AddEventHandlers = "WadSmithSigns"
        }

        """;
}
