using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Text;

namespace WadSmith.Core.Translation;

/// <summary>An expression from a translation file, evaluated with the line's tag.</summary>
internal abstract record XExpr
{
    public abstract int Eval(int tag);

    public virtual bool UsesTag => false;

    public sealed record Const(int Value) : XExpr
    {
        public override int Eval(int tag) => Value;
    }

    public sealed record Tag : XExpr
    {
        public override int Eval(int tag) => tag;

        public override bool UsesTag => true;
    }

    public sealed record Neg(XExpr A) : XExpr
    {
        public override int Eval(int tag) => -A.Eval(tag);

        public override bool UsesTag => A.UsesTag;
    }

    public sealed record Bin(char Op, XExpr A, XExpr B) : XExpr
    {
        public override bool UsesTag => A.UsesTag || B.UsesTag;

        public override int Eval(int tag)
        {
            int a = A.Eval(tag), b = B.Eval(tag);
            return Op switch
            {
                '+' => a + b,
                '-' => a - b,
                '*' => a * b,
                '/' => b == 0 ? 0 : a / b,
                '%' => b == 0 ? 0 : a % b,
                '&' => a & b,
                '|' => a | b,
                '^' => a ^ b,
                _ => throw new InvalidOperationException(),
            };
        }
    }
}

/// <summary>A classic line type: GZDoom's special, its trigger flags and its five argument expressions.</summary>
internal sealed record LineTranslation(int Special, int Flags, XExpr[] Args);

/// <summary>One statement of a Boom generalized-type block: which argument (0–3 for arg2–arg5, 4 for the flags), and how it is set.</summary>
internal sealed record BoomArg(int ArgNum, bool OrExisting, int Constant, int Mask, (int Filter, int Value)[]? Filters);

/// <summary>A range of Boom generalized line types and the special they become.</summary>
internal sealed record BoomTranslation(int First, int Last, int Special, List<BoomArg> Args);

/// <summary>
/// One of GZDoom's classic-to-ZDoom translators (<c>xlat/doom.txt</c>, <c>heretic.txt</c>,
/// <c>strife.txt</c> and what they include), read from the engine's own files. It answers what a
/// Doom-format line type, sector type and flag word mean in GZDoom.
/// </summary>
public sealed class XlatTranslator
{
    private readonly Dictionary<string, int> _constants = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<int, LineTranslation> Lines { get; } = [];
    internal List<BoomTranslation> Boom { get; } = [];
    internal Dictionary<int, (int NewType, bool BitmaskAllowed)> Sectors { get; } = [];
    internal List<(int Mask, int Op, int Shift)> SectorMasks { get; } = [];
    internal (int Value, bool IsMask)?[] LineFlags { get; } = new (int, bool)?[16];
    private int _maxLineSpecial = int.MaxValue;

    /// <summary>The file it was read from, for messages.</summary>
    public string Source { get; private set; } = "";

    /// <summary>
    /// Reads a translator from a resource stack holding the engine's pk3 (or any file with an
    /// <c>xlat/</c> folder). <paramref name="path"/> is e.g. <c>xlat/doom.txt</c>.
    /// </summary>
    public static XlatTranslator Load(ResourceStack engine, string path)
    {
        var t = new XlatTranslator { Source = path };
        t.ParseFile(engine, path, depth: 0);
        return t;
    }

    /// <summary>The translator a game's classic maps use, as the engine's MAPINFO picks it.</summary>
    public static string PathFor(string mapNamespace) => mapNamespace switch
    {
        "heretic" => "xlat/heretic.txt",
        "strife" => "xlat/strife.txt",
        _ => "xlat/doom.txt",
    };

    private void ParseFile(ResourceStack engine, string path, int depth)
    {
        if (depth > 8) throw new CorruptDataException($"{path}: includes nest too deeply.");
        ResourceEntry? entry = null;
        for (var i = engine.Containers.Count - 1; i >= 0 && entry is null; i--) entry = engine.Containers[i].FindPath(path);
        if (entry is null) throw new CorruptDataException($"The engine's translation file {path} was not found.");
        var sc = Scanner.FromBytes(entry.Read(), path);

        while (!sc.AtEnd)
        {
            var t = sc.Peek();
            if (t.Is("include"))
            {
                sc.Next();
                ParseFile(engine, sc.ExpectName(), depth + 1);
            }
            else if (t.Is("define"))
            {
                sc.Next();
                var name = sc.ExpectIdentifier();
                _constants[name] = ParseExpr(sc, allowTag: false).Eval(0);
            }
            else if (t.Is("enum"))
            {
                sc.Next();
                sc.Expect("{");
                var next = 0;
                while (!sc.Accept("}"))
                {
                    var name = sc.ExpectIdentifier();
                    if (sc.Accept("=")) next = ParseExpr(sc, allowTag: false).Eval(0);
                    _constants[name] = next++;
                    sc.Accept(",");
                }

                sc.Accept(";");
            }
            else if (t.Is("maxlinespecial"))
            {
                sc.Next();
                sc.Expect("=");
                _maxLineSpecial = ParseExpr(sc, false).Eval(0);
                foreach (var k in Lines.Keys.Where(k => k > _maxLineSpecial).ToList()) Lines.Remove(k);
                sc.Expect(";");
            }
            else if (t.Is("sector"))
            {
                sc.Next();
                ParseSector(sc);
            }
            else if (t.Is("lineflag"))
            {
                sc.Next();
                // The bit number is a plain integer: "lineflag 11 & (mask)" must not read as one expression.
                var bit = (int)sc.ExpectInteger();
                if (bit is < 0 or > 15) throw sc.Error($"line flag {bit} is outside 0–15.");
                if (sc.Accept("&")) LineFlags[bit] = (ParseExpr(sc, false).Eval(0), true);
                else
                {
                    sc.Expect("=");
                    LineFlags[bit] = (ParseExpr(sc, false).Eval(0), false);
                }

                sc.Expect(";");
            }
            else if (t.Is("["))
            {
                sc.Next();
                ParseBoom(sc);
            }
            else if (t.Kind == TokenKind.Integer)
            {
                ParseLine(sc);
            }
            else
            {
                throw sc.Error($"unexpected {t}.");
            }
        }
    }

    private void ParseLine(Scanner sc)
    {
        var number = (int)sc.ExpectInteger();
        sc.Expect("=");
        var flags = ParseExpr(sc, false).Eval(0);
        sc.Expect(",");
        var specialName = sc.ExpectIdentifier();
        var special = SpecialNumber(sc, specialName);
        var args = new XExpr[5];
        for (var i = 0; i < 5; i++) args[i] = new XExpr.Const(0);
        sc.Expect("(");
        var n = 0;
        if (!sc.Accept(")"))
        {
            do
            {
                if (n >= 5) throw sc.Error($"line type {number} gives more than five arguments.");
                args[n++] = ParseExpr(sc, allowTag: true);
            }
            while (sc.Accept(","));
            sc.Expect(")");
        }

        sc.Accept(";");
        // A definition after maxlinespecial may add types above it again, as in the engine.
        Lines[number] = new LineTranslation(special, flags, args);
    }

    private void ParseSector(Scanner sc)
    {
        if (sc.Accept("bitmask"))
        {
            var mask = ParseExpr(sc, false).Eval(0);
            if (sc.Accept("clear"))
            {
                SectorMasks.Add((mask, 0, 1));
            }
            else if (sc.Peek().Is("<") || sc.Peek().Is(">"))
            {
                var left = sc.Next().Is("<");
                sc.Expect(left ? "<" : ">");
                sc.Expect("=");
                SectorMasks.Add((mask, left ? 1 : -1, ParseExpr(sc, false).Eval(0)));
            }
            else
            {
                SectorMasks.Add((mask, 0, 0));
            }

            sc.Expect(";");
            return;
        }

        var from = ParseExpr(sc, false).Eval(0);
        sc.Expect("=");
        var to = ParseExpr(sc, false).Eval(0);
        var allowed = !sc.Accept("nobitmask");
        sc.Expect(";");
        Sectors[from] = (to, allowed);
    }

    private void ParseBoom(Scanner sc)
    {
        var special = SpecialNumber(sc, sc.ExpectIdentifier());
        sc.Expect("]");
        sc.Expect("(");
        var first = ParseExpr(sc, false).Eval(0);
        sc.Expect(",");
        var last = ParseExpr(sc, false).Eval(0);
        sc.Expect(")");
        sc.Expect("{");
        var args = new List<BoomArg>();
        while (!sc.Accept("}"))
        {
            var sel = sc.ExpectIdentifier().ToLowerInvariant();
            var argNum = sel switch
            {
                "flags" => 4,
                "arg2" => 0,
                "arg3" => 1,
                "arg4" => 2,
                "arg5" => 3,
                _ => throw sc.Error($"\"{sel}\" is not flags or arg2–arg5."),
            };
            var or = false;
            if (sc.Accept("|")) or = true;
            sc.Expect("=");
            var value = ParseExpr(sc, false).Eval(0);
            if (sc.Accept("["))
            {
                var filters = new List<(int, int)>();
                do
                {
                    var filter = ParseExpr(sc, false).Eval(0);
                    sc.Expect(":");
                    filters.Add((filter, ParseExpr(sc, false).Eval(0)));
                }
                while (sc.Accept(","));
                sc.Expect("]");
                args.Add(new BoomArg(argNum, or, 0, value, filters.ToArray()));
            }
            else
            {
                args.Add(new BoomArg(argNum, or, value, 0, null));
            }
        }

        Boom.Add(new BoomTranslation(first, last, special, args));
    }

    private int SpecialNumber(Scanner sc, string name)
    {
        if (ActionCatalog.Find(name) is { } a) return a.Number;
        if (_constants.TryGetValue(name, out var v)) return v;
        throw sc.Error($"\"{name}\" is not an action special WadSmith knows.");
    }

    // Precedence, lowest first: | ^ & + - * / %.
    private XExpr ParseExpr(Scanner sc, bool allowTag) => ParseBinary(sc, allowTag, 0);

    private static readonly string[][] Levels = [["|"], ["^"], ["&"], ["+", "-"], ["*", "/", "%"]];

    private XExpr ParseBinary(Scanner sc, bool allowTag, int level)
    {
        if (level == Levels.Length) return ParseUnary(sc, allowTag);
        var left = ParseBinary(sc, allowTag, level + 1);
        while (true)
        {
            var t = sc.Peek();
            var op = Levels[level].FirstOrDefault(o => t.Is(o));
            if (op is null) return left;
            sc.Next();
            var right = ParseBinary(sc, allowTag, level + 1);
            left = left is XExpr.Const a && right is XExpr.Const b
                ? new XExpr.Const(new XExpr.Bin(op[0], a, b).Eval(0))
                : new XExpr.Bin(op[0], left, right);
        }
    }

    private XExpr ParseUnary(Scanner sc, bool allowTag)
    {
        if (sc.Accept("-"))
        {
            var operand = ParseUnary(sc, allowTag);
            return operand is XExpr.Const c ? new XExpr.Const(-c.Value) : new XExpr.Neg(operand);
        }
        if (sc.Accept("("))
        {
            var inner = ParseExpr(sc, allowTag);
            sc.Expect(")");
            return inner;
        }

        var t = sc.Next();
        switch (t.Kind)
        {
            case TokenKind.Integer:
                return new XExpr.Const((int)t.Integer);
            case TokenKind.Identifier when t.Is("tag") || t.Is("lineid"):
                if (!allowTag) throw sc.Error($"{t} can only be used in a line's arguments.", t.Line);
                return new XExpr.Tag();
            case TokenKind.Identifier when _constants.TryGetValue(t.Text, out var v):
                return new XExpr.Const(v);
            case TokenKind.Identifier when ActionCatalog.Find(t.Text) is { } a:
                return new XExpr.Const(a.Number);
            default:
                throw sc.Error($"{t} is not a number or a known name.", t.Line);
        }
    }
}
