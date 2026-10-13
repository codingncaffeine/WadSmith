using WadSmith.Core.Containers;
using WadSmith.Core.Graphics;
using WadSmith.Core.Projects;
using WadSmith.Editor;

namespace WadSmith.Tests;

/// <summary>
/// A sprite frame named with a \ (Hexen's cleric, CLER\0): a WadSmith before 0.0.8 wrote it as a file with a \ in its
/// name, which the folder reader took for a folder, so the project's game would not load (and its zmapinfo.txt lost the
/// game's own settings, the sky among them: the user's made map, 2026-10-13). Now the file is GZDoom's ^, read as \.
/// </summary>
public sealed class SpriteNameTests
{
    private static readonly byte[] Picture = ImageCodec.EncodePng(new RgbaImage(4, 4));

    [Fact]
    public void ACaretFrameIsTheBackslashFrame()
    {
        Assert.True(SpriteFrameName.TryParse("CLER^0", out var name));
        Assert.Equal(('\\', '0'), (name.Frame, name.Rotation));
        Assert.True(SpriteFrameName.TryParse("CLER^2^8", out var mirrored));
        Assert.Equal('\\', mirrored.MirrorFrame);
    }

    [Fact]
    public void AProjectWithABackslashSpriteOpensWithItRenamedAndItsGameLoads()
    {
        if (Path.DirectorySeparatorChar == '\\') return; // Windows never allowed the name.
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "freedoom2");
        Directory.CreateDirectory(Path.Combine(project.Folder, "sprites"));
        File.WriteAllBytes(Path.Combine(project.Folder, "sprites", "CLER\\0.png"), Picture);
        project.Record(new CopiedAsset { Kind = "sprite", Game = "hexen", Original = "CLER\\0", Name = "CLER\\0", File = "sprites/CLER\\0.png" });

        // The control: as 0.0.7 read the folder, the file is a folder and reading it fails.
        var oldName = Path.GetRelativePath(project.Folder, Path.Combine(project.Folder, "sprites", "CLER\\0.png")).Replace('\\', '/');
        Assert.False(File.Exists(Path.Combine(project.Folder, oldName)));

        var opened = Project.Open(project.Folder);
        Assert.True(File.Exists(Path.Combine(opened.Folder, "sprites", "CLER^0.png")));
        Assert.False(File.Exists(Path.Combine(opened.Folder, "sprites", "CLER\\0.png")));
        Assert.Equal("sprites/CLER^0.png", Assert.Single(opened.Info.Copied).File);

        var wad = GameFiles.Require("freedoom2.wad");
        var games = Core.Games.GameLocator.Scan([Path.GetDirectoryName(wad)!], cancel: TestContext.Current.CancellationToken);
        var resources = GameResources.Load("freedoom2", opened.Folder, games, GameFiles.Require("gzdoom.pk3"));
        Assert.NotNull(resources);
        Assert.Contains(resources.Sprites.Frames("CLER"), f => f.Name.Frame == '\\');
    }

    [Fact]
    public void AFolderReadsABackslashInANameAsALetter()
    {
        if (Path.DirectorySeparatorChar == '\\') return;
        using var temp = new TempFolder();
        Directory.CreateDirectory(Path.Combine(temp.Path, "sprites"));
        File.WriteAllBytes(Path.Combine(temp.Path, "sprites", "CLER\\0.png"), Picture);
        var entry = Assert.Single(ResourceContainer.Open(temp.Path).Entries);
        Assert.Equal("sprites/CLER\\0.png", entry.FullPath);
        Assert.Equal(Picture, entry.Read());
    }
}
