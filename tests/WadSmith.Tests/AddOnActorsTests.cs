using System.Text;
using WadSmith.Core.Containers;
using WadSmith.Core.Games;

namespace WadSmith.Tests;

/// <summary>
/// The objects an add-on defines (D65 B2): a friend's PK3 of monsters shows its monsters by name, kind and picture, as the
/// engine reads them: ZScript and DECORATE actors (following #include), MAPINFO's DoomEdNums, DECORATE's numbers, LANGUAGE
/// names, <c>replaces</c>, and the Doom Builder keys in their comments.
/// </summary>
public sealed class AddOnActorsTests
{
    private static ThingCatalog Engine() => ThingCatalog.Load(ResourceContainer.Open(GameFiles.Require("gzdoom.pk3")), "doom");

    private const string FireImp = """
        // A hotter imp, everywhere the game puts one.
        class FireImp : DoomImp replaces DoomImp
        {
            //$Title "Fire imp"
            //$Category "Monsters/Hell"
            Default
            {
                Health 120;
            }
        }

        class Lantern : Actor
        {
            //$Category "Light sources"
            //$Sprite "LANTB0"
            Default
            {
                Radius 8;
                Height 32;
                +SOLID
            }
            States
            {
            Spawn:
                LANT A -1;
                Stop;
            }
        }
        """;

    private const string Ghoul = """
        actor Ghoul 20002
        {
          Monster
          Health 80
          Radius 20
          Height 56
          Tag "$GHOUL_TAG" // the name the language table gives
          SeeSound "ghoul/sight"
          States
          {
          Spawn:
            GHUL AB 10 A_Look
            Loop
          See:
            GHUL ABCD 4 A_Chase
            Loop
          }
        }

        /* An old-style pickup with no number: not on the shelf. */
        actor GhoulTooth : Inventory
        {
          Inventory.Amount 1
          States { Spawn: TOTH A -1 Stop }
        }
        """;

    [Fact]
    public void AFoldersZScriptDecorateMapInfoAndLanguageBringItsObjects()
    {
        using var temp = new TempFolder();
        var pack = Path.Combine(temp.Path, "monsters");
        Directory.CreateDirectory(Path.Combine(pack, "actors"));
        File.WriteAllText(Path.Combine(pack, "zscript.txt"), "version \"4.6\"\n#include \"actors/fireimp.zs\"\n");
        File.WriteAllText(Path.Combine(pack, "actors", "fireimp.zs"), FireImp);
        File.WriteAllText(Path.Combine(pack, "decorate.txt"), "#include \"actors/ghoul.dec\"\n");
        File.WriteAllText(Path.Combine(pack, "actors", "ghoul.dec"), Ghoul);
        File.WriteAllText(Path.Combine(pack, "mapinfo.txt"), "DoomEdNums\n{\n    20001 = Lantern\n}\n");
        File.WriteAllText(Path.Combine(pack, "language.txt"), "[enu default]\nGHOUL_TAG = \"Hungry ghoul\";\n");

        var engine = Engine();
        var things = engine.WithResources([ResourceContainer.Open(pack)]);

        // The imp's number now spawns the fire imp: its own title and health, the imp's picture.
        var imp = things.Find(3001)!;
        Assert.Equal(("FireImp", "Fire imp", ThingCategory.Monster, 120, "TROO"), (imp.ClassName, imp.Name, imp.Category, imp.Health ?? 0, imp.Sprite));
        Assert.Equal("DoomImp", engine.Find(3001)!.ClassName);

        // MAPINFO's number for the lantern: a decoration on the shelf (Light sources), the picture its key names.
        var lantern = things.Find(20001)!;
        Assert.Equal(("Lantern", ThingCategory.Decoration, "LANT", 'B', 8.0, 32.0), (lantern.ClassName, lantern.Category, lantern.Sprite, lantern.Frame, lantern.Radius, lantern.Height));

        // DECORATE's own number for the ghoul, named by the language table.
        var ghoul = things.Find(20002)!;
        Assert.Equal(("Ghoul", "Hungry ghoul", ThingCategory.Monster, 80, "GHUL", 'A'), (ghoul.ClassName, ghoul.Name, ghoul.Category, ghoul.Health ?? 0, ghoul.Sprite, ghoul.Frame));

        // A class with no number is no object on the shelf; the engine's catalogue is left as it was.
        Assert.DoesNotContain(things.All, t => t.ClassName == "GhoulTooth");
        Assert.Equal(engine.All.Count + 2, things.All.Count);
        Assert.Null(engine.Find(20001));
    }

    [Fact]
    public void AWadsLumpsBringTheirObjectsAndIncludesNameLumps()
    {
        var wad = WadWriter.Write(WadKind.Pwad,
        [
            ("DECORATE", Encoding.UTF8.GetBytes("#include \"GHOULDEC\"\n")),
            ("GHOULDEC", Encoding.UTF8.GetBytes(Ghoul)),
            ("LANGUAGE", Encoding.UTF8.GetBytes("[enu default]\nGHOUL_TAG = \"Hungry ghoul\";\n")),
            ("ZMAPINFO", Encoding.UTF8.GetBytes("DoomEdNums { 20003 = GhoulTooth }\n")),
            ("MAPINFO", Encoding.UTF8.GetBytes("DoomEdNums { 20004 = Ghoul }\n")),
        ]);
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "ghoul.wad");
        File.WriteAllBytes(path, wad);

        var things = Engine().WithResources([ResourceContainer.Open(path)]);
        Assert.Equal("Hungry ghoul", things.Find(20002)?.Name);
        Assert.Equal(ThingCategory.Powerup, things.Find(20003)?.Category);

        // The file's ZMAPINFO is read, its MAPINFO not, as GZDoom does.
        Assert.Null(things.Find(20004));
    }

    [Fact]
    public void FilesThatDefineNoObjectsLeaveTheCatalogueAsItWas()
    {
        using var temp = new TempFolder();
        var pack = Path.Combine(temp.Path, "textures-only");
        Directory.CreateDirectory(Path.Combine(pack, "textures"));
        File.WriteAllBytes(Path.Combine(pack, "textures", "WALL1.png"), [0x89, 0x50, 0x4E, 0x47]);
        var engine = Engine();
        Assert.Same(engine, engine.WithResources([ResourceContainer.Open(pack)]));
    }
}
