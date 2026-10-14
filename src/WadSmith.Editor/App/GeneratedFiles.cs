using WadSmith.Core.Projects;

namespace WadSmith.Editor.App;

/// <summary>What a piece of a generated file is, for its colour.</summary>
public enum CodeKind
{
    Plain,
    Comment,
    Text,
    Number,
    Word,
}

/// <summary>
/// The text files WadSmith writes into a project for GZDoom (§3.10): zmapinfo, ANIMDEFS, SNDINFO, MODELDEF, KEYCONF, GLDEFS,
/// zscript.txt and the classes under zscript/wadsmith, to read as they are, coloured. Each says it is WadSmith's in its first
/// lines; the kept versions, credits and anything else of the project's are not listed.
/// </summary>
public static class GeneratedFiles
{
    private static readonly string[] Extensions = [".txt", ".zs", ".zsc"];

    /// <summary>The project's generated files, relative to its folder with '/' between folders: the root's first, then by folder.</summary>
    public static IReadOnlyList<string> List(Project project)
    {
        if (!Directory.Exists(project.Folder)) return [];
        var found = new List<string>();
        foreach (var path in Directory.EnumerateFiles(project.Folder, "*", SearchOption.AllDirectories))
        {
            if (!Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
            var relative = Path.GetRelativePath(project.Folder, path).Replace(Path.DirectorySeparatorChar, '/'); // archive path
            if (relative.StartsWith(".wadsmith/", StringComparison.Ordinal) || relative.StartsWith("credits/", StringComparison.Ordinal)) continue;
            if (IsWadSmiths(path)) found.Add(relative);
        }

        return [.. found.OrderBy(f => f.Contains('/', StringComparison.Ordinal)).ThenBy(f => f, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Whether a file says in its first lines that WadSmith wrote it.</summary>
    private static bool IsWadSmiths(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            var buffer = new char[400];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read).Contains("WadSmith", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    // The words of the languages these files are written in: ZScript and DECORATE, MAPINFO, ANIMDEFS, SNDINFO, MODELDEF,
    // GLDEFS, VOXELDEF and KEYCONF.
    private static readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        "class", "struct", "extend", "default", "states", "override", "virtual", "static", "native", "action", "const", "enum",
        "return", "if", "else", "for", "while", "do", "switch", "case", "break", "continue", "true", "false", "null", "self",
        "super", "int", "uint", "bool", "float", "double", "string", "name", "sound", "void", "let", "version", "include",
        "replaces", "stop", "loop", "wait", "fail", "goto", "actor", "map", "defaultmap", "adddefaultmap", "gameinfo", "episode",
        "cluster", "clearepisodes", "levelnum", "next", "secretnext", "sky1", "sky2", "music", "titlepatch", "lookup", "texture",
        "flat", "pic", "tics", "rand", "range", "cameratexture", "warp", "warp2", "on", "off", "model", "path", "skin",
        "scale", "offset", "frameindex", "frame", "angleoffset", "pitchoffset", "rolloffset", "zoffset", "pointlight",
        "pulselight", "flickerlight", "flickerlight2", "sectorlight", "object", "light", "skybox", "material", "normal",
        "specular", "brightmap", "voxeldef", "addslotdefault", "setslot", "weaponsection", "clearplayerclasses",
        "addplayerclass", "$random", "$limit", "$volume", "$attenuation", "$alias", "$musicalias",
    };

    /// <summary>
    /// The text in pieces of one kind each, which join back to it exactly: comments (// to the line's end, /* … */, and # at a
    /// line's start), quoted text, numbers, the languages' words, and everything else plain.
    /// </summary>
    public static IReadOnlyList<(string Text, CodeKind Kind)> Pieces(string text)
    {
        var pieces = new List<(string, CodeKind)>();
        var plain = 0;
        var i = 0;
        void Add(int end, CodeKind kind)
        {
            if (plain < i) pieces.Add((text[plain..i], CodeKind.Plain));
            pieces.Add((text[i..end], kind));
            i = end;
            plain = end;
        }

        while (i < text.Length)
        {
            var c = text[i];
            var lineStart = i == 0 || text[i - 1] == '\n';
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/' || c == '#' && lineStart)
            {
                var end = text.IndexOf('\n', i);
                Add(end < 0 ? text.Length : end, CodeKind.Comment);
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                Add(end < 0 ? text.Length : end + 2, CodeKind.Comment);
            }
            else if (c == '"')
            {
                var end = i + 1;
                while (end < text.Length && text[end] != '"' && text[end] != '\n') end += text[end] == '\\' && end + 1 < text.Length ? 2 : 1;
                Add(Math.Min(text.Length, end + 1), CodeKind.Text);
            }
            else if (char.IsAsciiDigit(c) && (i == 0 || !(char.IsAsciiLetterOrDigit(text[i - 1]) || text[i - 1] == '_')))
            {
                var end = i;
                while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == '.')) end++;
                Add(end, CodeKind.Number);
            }
            else if (char.IsAsciiLetter(c) || c is '_' or '$')
            {
                var end = i + 1;
                while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '_')) end++;
                if (Words.Contains(text[i..end])) Add(end, CodeKind.Word);
                else i = end;
            }
            else
            {
                i++;
            }
        }

        if (plain < text.Length) pieces.Add((text[plain..], CodeKind.Plain));
        return pieces;
    }
}
