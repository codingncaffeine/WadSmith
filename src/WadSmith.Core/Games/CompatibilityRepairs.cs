using System.Globalization;
using System.Text;
using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Core.Text;
using WadSmith.Core.Translation;

namespace WadSmith.Core.Games;

/// <summary>
/// Applies the engine's own repairs for an original map (its <c>zscript/level_compatibility.zs</c>)
/// to a translated copy of that map, so a remix keeps the fixes GZDoom gives the original: missing
/// textures, broken lines, unreachable secrets. The script is ZScript; this reads the part of it the
/// repairs use (the checksum switch, straight calls, integer for-loops, texture lookups, plane
/// lights) and executes it against the map. Constants come from the engine's own ZScript enums.
/// Anything that cannot be expressed in a map file is reported, never silently skipped.
/// </summary>
public sealed class CompatibilityRepairs
{
    private readonly string _script;
    private readonly Dictionary<string, double> _constants;

    private CompatibilityRepairs(string script, Dictionary<string, double> constants)
    {
        _script = script;
        _constants = constants;
    }

    /// <summary>Reads the repairs script and the constants it uses from the engine's files; null when the engine has none.</summary>
    public static CompatibilityRepairs? Load(ResourceStack engine)
    {
        ResourceEntry? script = null;
        foreach (var c in engine.Containers) script = c.FindPath("zscript/level_compatibility.zs") ?? script;
        if (script is null) return null;
        var constants = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in engine.Containers)
        {
            foreach (var e in c.Entries.Where(e => e.FullPath.StartsWith("zscript/", StringComparison.OrdinalIgnoreCase) && e.FullPath.EndsWith(".zs", StringComparison.OrdinalIgnoreCase)))
            {
                ReadEnums(Encoding.Latin1.GetString(e.Read()), e.FullPath, constants);
            }
        }

        return new CompatibilityRepairs(Encoding.Latin1.GetString(script.Read()), constants);
    }

    /// <summary>
    /// Applies the repairs for the map with this checksum to <paramref name="map"/> (zdoom namespace).
    /// Returns false when the engine has no repairs for it. Every step that has no map-file
    /// equivalent is described in <paramref name="notes"/>.
    /// </summary>
    public bool Apply(MapModel map, string checksum, List<string> notes)
    {
        var sc = new Scanner(_script, "level_compatibility.zs") { AllowNameLiterals = true };
        while (!sc.AtEnd)
        {
            var t = sc.Next();
            if (!t.Is("case")) continue;
            var label = sc.Next();
            if (label.Kind != TokenKind.String || !label.Text.Equals(checksum, StringComparison.OrdinalIgnoreCase)) continue;

            // Further case labels may share the block.
            sc.Expect(":");
            while (sc.Peek().Is("case"))
            {
                sc.Next();
                sc.Next();
                sc.Expect(":");
            }

            var runner = new Runner(this, map, notes);
            if (sc.Accept("{")) runner.Block(sc);
            else runner.UntilBreak(sc);
            return true;
        }

        return false;
    }

    // ---- Constants from the engine's enums ----

    private static void ReadEnums(string text, string source, Dictionary<string, double> into)
    {
        var sc = new Scanner(text, source) { AllowNameLiterals = true };
        var scopes = new Stack<(string Name, int Depth)>();
        var depth = 0;
        string? pendingScope = null;
        try
        {
            while (!sc.AtEnd)
            {
                var t = sc.Next();
                if (t.Is("struct") || t.Is("class"))
                {
                    if (sc.Peek().Kind == TokenKind.Identifier) pendingScope = sc.Next().Text;
                    continue;
                }

                if (t.Is("{"))
                {
                    depth++;
                    if (pendingScope is not null) scopes.Push((pendingScope, depth));
                    pendingScope = null;
                    continue;
                }

                if (t.Is("}"))
                {
                    if (scopes.Count > 0 && scopes.Peek().Depth == depth) scopes.Pop();
                    depth--;
                    continue;
                }

                if (t.Is(";")) pendingScope = null;
                if (t.Is("const") && sc.Peek().Kind == TokenKind.Identifier)
                {
                    // const NAME = expression;
                    var constName = sc.Next().Text;
                    if (!sc.Accept("=")) continue;
                    var expr = new List<Token>();
                    while (!sc.AtEnd && !sc.Peek().Is(";")) expr.Add(sc.Next());
                    if (TryEvaluate(expr, into, out var cv))
                    {
                        into.TryAdd(constName, cv);
                        if (scopes.Count > 0) into[scopes.Peek().Name + "." + constName] = cv;
                    }

                    continue;
                }

                if (!t.Is("enum")) continue;
                if (sc.Peek().Kind == TokenKind.Identifier) sc.Next();
                if (sc.Accept(":")) sc.Next();
                if (!sc.Accept("{")) continue;
                var scope = scopes.Count > 0 ? scopes.Peek().Name : null;
                double next = 0;
                while (!sc.AtEnd && !sc.Accept("}"))
                {
                    var name = sc.Next();
                    if (name.Kind != TokenKind.Identifier)
                    {
                        continue;
                    }

                    if (sc.Accept("="))
                    {
                        var expr = new List<Token>();
                        while (!sc.AtEnd && !sc.Peek().Is(",") && !sc.Peek().Is("}")) expr.Add(sc.Next());
                        if (TryEvaluate(expr, into, out var v)) next = v;
                    }

                    into.TryAdd(name.Text, next);
                    if (scope is not null) into[scope + "." + name.Text] = next;
                    next++;
                    sc.Accept(",");
                }
            }
        }
        catch (CorruptDataException)
        {
            // A construct this reader does not follow; the constants read so far stand.
        }
    }

    private static bool TryEvaluate(List<Token> tokens, Dictionary<string, double> known, out double value)
    {
        value = 0;
        double acc = 0;
        var op = '|';
        var started = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            double v;
            if (t.Kind is TokenKind.Integer or TokenKind.Float) v = t.Number;
            else if (t.Kind == TokenKind.Identifier && known.TryGetValue(t.Text, out var k)) v = k;
            else if (t.Is("<") && i + 1 < tokens.Count && tokens[i + 1].Is("<"))
            {
                op = 'L';
                i++;
                continue;
            }
            else if (t.Is("|") || t.Is("+") || t.Is("-"))
            {
                op = t.Text[0];
                continue;
            }
            else if (t.Is("(") || t.Is(")"))
            {
                continue;
            }
            else
            {
                return false;
            }

            acc = !started ? v : op switch
            {
                '|' => (long)acc | (long)v,
                '+' => acc + v,
                '-' => acc - v,
                'L' => (long)acc << (int)v,
                _ => v,
            };
            started = true;
        }

        value = acc;
        return started;
    }

    // ---- Execution ----

    private sealed class Runner(CompatibilityRepairs owner, MapModel map, List<string> notes)
    {
        private readonly Dictionary<string, object> _locals = new(StringComparer.OrdinalIgnoreCase);
        private bool _stopped;

        /// <summary>A case without braces: statements until break, return, the next case or the end of the switch.</summary>
        public void UntilBreak(Scanner sc)
        {
            while (!sc.AtEnd && !_stopped && !sc.Peek().Is("case") && !sc.Peek().Is("default") && !sc.Peek().Is("}")) Statement(sc);
        }

        /// <summary>Runs statements until the block's closing brace (or a break/return).</summary>
        public void Block(Scanner sc)
        {
            while (!sc.AtEnd)
            {
                if (sc.Accept("}")) return;
                if (_stopped)
                {
                    SkipStatement(sc);
                    continue;
                }

                Statement(sc);
            }
        }

        /// <summary>One statement; a statement this reader cannot follow is reported and skipped, never fatal.</summary>
        private void Statement(Scanner sc)
        {
            try
            {
                StatementCore(sc);
            }
            catch (Exception ex) when (ex is CorruptDataException or FormatException or InvalidCastException or ArgumentException or IndexOutOfRangeException)
            {
                notes.Add($"A repair near line {sc.Line.ToString(CultureInfo.InvariantCulture)} of the engine's script could not be read and was skipped ({ex.Message}).");
                SkipStatement(sc);
            }
        }

        private void StatementCore(Scanner sc)
        {
            var t = sc.Peek();
            if (t.Is("{"))
            {
                sc.Next();
                Block(sc);
                return;
            }

            if (t.Is("break") || t.Is("return"))
            {
                sc.Next();
                sc.Accept(";");
                _stopped = true;
                return;
            }

            if (t.Is("for"))
            {
                For(sc);
                return;
            }

            if (t.Is("TextureID") || t.Is("int") || t.Is("double") || t.Is("let"))
            {
                sc.Next();
                var name = sc.ExpectIdentifier();
                sc.Expect("=");
                _locals[name] = Expr(sc);
                sc.Expect(";");
                return;
            }

            if (t.Is("level"))
            {
                LevelStatement(sc);
                return;
            }

            if (t.Kind == TokenKind.Identifier)
            {
                var name = sc.Next().Text;
                if (sc.Peek().Is("("))
                {
                    var args = Args(sc);
                    if (sc.Peek().Is("."))
                    {
                        notes.Add($"The engine also changes a default of {name}({string.Join(", ", args)}) for this map; that is a game setting, not map data, so the remix does not carry it.");
                        SkipStatement(sc);
                        return;
                    }

                    sc.Accept(";");
                    Call(name, args);
                    return;
                }
            }

            notes.Add($"A repair at line {sc.Line.ToString(CultureInfo.InvariantCulture)} of the engine's script could not be read and was skipped.");
            SkipStatement(sc);
        }

        private void For(Scanner sc)
        {
            sc.Next();
            sc.Expect("(");
            sc.Accept("int");
            var var = sc.ExpectIdentifier();
            sc.Expect("=");
            var from = Convert.ToInt32(Expr(sc), CultureInfo.InvariantCulture);
            sc.Expect(";");
            sc.ExpectIdentifier();
            var op = sc.Next().Text;
            var inclusive = sc.Accept("=");
            var to = Convert.ToInt32(Expr(sc), CultureInfo.InvariantCulture);
            sc.Expect(";");
            // i++ or ++i
            while (!sc.Peek().Is(")")) sc.Next();
            sc.Expect(")");
            if (op != "<") throw sc.Error("only counting-up loops are understood.");
            var end = inclusive ? to + 1 : to;

            // The body is read once per pass; remember where it starts by collecting its tokens.
            var body = Capture(sc);
            for (var i = from; i < end && !_stopped; i++)
            {
                _locals[var] = (double)i;
                var inner = new Scanner(body, "loop") { AllowNameLiterals = true };
                if (body.TrimStart().StartsWith('{'))
                {
                    inner.Expect("{");
                    Block(inner);
                }
                else
                {
                    Statement(inner);
                }
            }
        }

        /// <summary>The text of the next statement or block, consumed from the scanner.</summary>
        private static string Capture(Scanner sc)
        {
            var sb = new StringBuilder();
            var depth = 0;
            do
            {
                var t = sc.Next();
                if (t.Kind == TokenKind.End) break;
                sb.Append(t.Kind == TokenKind.String ? '"' + t.Text + '"' : t.Text).Append(' ');
                if (t.Is("{")) depth++;
                else if (t.Is("}")) depth--;
                if (depth == 0 && (t.Is(";") || t.Is("}"))) break;
            }
            while (true);
            return sb.ToString();
        }

        private void LevelStatement(Scanner sc)
        {
            // level.lines[N].sidedef[S].SetTextureYOffset(part, value);
            // level.sectors[N].SetPlaneLight(plane, value);
            sc.Next();
            sc.Expect(".");
            var collection = sc.ExpectIdentifier();
            sc.Expect("[");
            var index = Int(Expr(sc));
            sc.Expect("]");
            if (collection.Equals("lines", StringComparison.OrdinalIgnoreCase))
            {
                sc.Expect(".");
                sc.ExpectIdentifier();
                sc.Expect("[");
                var side = Int(Expr(sc));
                sc.Expect("]");
                sc.Expect(".");
                var method = sc.ExpectIdentifier();
                var args = Args(sc);
                sc.Accept(";");
                var s = Side(index, side);
                if (s is not null && method.Equals("SetTextureYOffset", StringComparison.OrdinalIgnoreCase))
                {
                    s.Extra[$"offsety_{PartName(Int(args[0]))}"] = UdmfValue.Float(Double(args[1]));
                    return;
                }

                notes.Add($"The engine's repair calls {method} on a wall side, which WadSmith does not reproduce.");
                return;
            }

            sc.Expect(".");
            var call = sc.ExpectIdentifier();
            var a = Args(sc);
            sc.Accept(";");
            if (call.Equals("SetPlaneLight", StringComparison.OrdinalIgnoreCase) && index < map.Sectors.Count)
            {
                map.Sectors[index].Extra[Int(a[0]) == 0 ? "lightfloor" : "lightceiling"] = Int(a[1]);
                return;
            }

            notes.Add($"The engine's repair calls {call} on an area, which WadSmith does not reproduce.");
        }

        private List<object> Args(Scanner sc)
        {
            sc.Expect("(");
            var args = new List<object>();
            if (sc.Accept(")")) return args;
            do args.Add(Expr(sc));
            while (sc.Accept(","));
            sc.Expect(")");
            return args;
        }

        // Expressions: | and & lowest, then + -, then * /, then unary minus, then primaries.
        private object Expr(Scanner sc) => BinaryLevel(sc, 0);

        private static readonly string[][] Levels = [["|"], ["&"], ["+", "-"], ["*", "/"]];

        private object BinaryLevel(Scanner sc, int level)
        {
            if (level == Levels.Length) return Unary(sc);
            var left = BinaryLevel(sc, level + 1);
            while (Levels[level].FirstOrDefault(o => sc.Peek().Is(o)) is { } op)
            {
                sc.Next();
                var right = BinaryLevel(sc, level + 1);
                double a = Double(left), b = Double(right);
                left = op switch
                {
                    "|" => (double)((long)a | (long)b),
                    "&" => (double)((long)a & (long)b),
                    "+" => a + b,
                    "-" => a - b,
                    "*" => a * b,
                    _ => b == 0 ? 0 : a / b,
                };
            }

            return left;
        }

        private object Unary(Scanner sc)
        {
            if (sc.Accept("-")) return -Double(Unary(sc));
            if (sc.Accept("("))
            {
                var v = Expr(sc);
                sc.Expect(")");
                return v;
            }

            var t = sc.Next();
            if (t.Kind is TokenKind.Integer or TokenKind.Float) return t.Number;
            if (t.Kind == TokenKind.String) return t.Text;
            if (t.Is("level"))
            {
                sc.Expect(".");
                sc.ExpectIdentifier();
                sc.Expect("[");
                var n = Int(Expr(sc));
                sc.Expect("]");
                sc.Expect(".");
                var method = sc.ExpectIdentifier();
                Args(sc);
                if (method.Equals("GetLightLevel", StringComparison.OrdinalIgnoreCase) && n < map.Sectors.Count) return (double)map.Sectors[n].Light;
                throw sc.Error($"level ... {method} is not understood.");
            }

            if (t.Kind != TokenKind.Identifier) throw sc.Error($"unexpected {t}.");
            var name = t.Text;
            if (sc.Accept("."))
            {
                var member = sc.ExpectIdentifier();
                if (name.Equals("TexMan", StringComparison.OrdinalIgnoreCase) && member.Equals("CheckForTexture", StringComparison.OrdinalIgnoreCase))
                {
                    var args = Args(sc);
                    return args[0];
                }

                if (name.Equals("TexMan", StringComparison.OrdinalIgnoreCase)) return 0.0;
                if (owner._constants.TryGetValue($"{name}.{member}", out var scoped)) return scoped;
                if (owner._constants.TryGetValue(member, out var bare)) return bare;
                throw sc.Error($"{name}.{member} is not a constant the engine defines.");
            }

            if (sc.Peek().Is("(")) return Function(name, Args(sc));
            if (_locals.TryGetValue(name, out var local)) return local;
            if (owner._constants.TryGetValue(name, out var c)) return c;
            if (ActionCatalog.Find(name) is { } action) return (double)action.Number;
            throw sc.Error($"\"{name}\" is not a constant the engine defines.");
        }

        private object Function(string name, List<object> a)
        {
            switch (name.ToLowerInvariant())
            {
                case "getthingflags": return (double)ThingFlags(map.Things[Int(a[0])]);
                case "getthingskills": return (double)ThingSkills(map.Things[Int(a[0])]);
                case "getthingednum": return (double)map.Things[Int(a[0])].Type;
                case "getthingangle": return (double)map.Things[Int(a[0])].Angle;
                case "getthingid": return (double)map.Things[Int(a[0])].Id;
                case "getthingspecial": return (double)map.Things[Int(a[0])].Special;
                case "getthingargument": return (double)map.Things[Int(a[0])].Args[Int(a[1])];
                case "getthingcount": return (double)map.Things.Count;
                default: throw new CorruptDataException($"{name} is not a value the repairs can read.");
            }
        }

        private void Call(string name, List<object> a)
        {
            switch (name.ToLowerInvariant())
            {
                case "setlinespecial":
                {
                    var l = Line(a[0]);
                    if (l is null) return;
                    l.Special = Int(a[1]);
                    for (var i = 0; i < 5; i++) l.Args[i] = a.Count > 2 + i ? Int(a[2 + i]) : 0;
                    return;
                }

                case "clearlinespecial":
                    // As the engine does it: the action goes, its arguments stay.
                    if (Line(a[0]) is { } cl) cl.Special = 0;
                    return;
                case "setlineactivation":
                    if (Line(a[0]) is { } la)
                    {
                        foreach (var (field, _) in Activation) la.Extra.Remove(field);
                        la.Extra.Remove("passuse");
                        SetActivation(la, Int(a[1]));
                    }

                    return;
                case "setlineflags":
                    if (Line(a[0]) is { } lf)
                    {
                        var set = Int(a[1]);
                        var clear = a.Count > 2 ? Int(a[2]) : 0;
                        foreach (var (bit, field) in MapTranslation.LineFlagNamesTable)
                        {
                            if ((clear & bit) != 0) lf.Extra.Remove(field);
                            if ((set & bit) != 0) lf.Extra[field] = true;
                        }
                    }

                    return;
                case "setlinesectorref":
                {
                    var l = Line(a[0]);
                    var sideIndex = l is null ? -1 : Int(a[1]) == 0 ? l.Front : l.Back;
                    if (sideIndex >= 0 && sideIndex < map.Sides.Count) map.Sides[sideIndex].Sector = Int(a[2]);
                    return;
                }

                case "fliplinesiderefs":
                case "fliplinecompletely":
                case "fliplinevertexes":
                    if (Line(a[0]) is { } fl)
                    {
                        if (name.Equals("FlipLineSideRefs", StringComparison.OrdinalIgnoreCase) || name.Equals("FlipLineCompletely", StringComparison.OrdinalIgnoreCase))
                        {
                            (fl.Front, fl.Back) = (fl.Back, fl.Front);
                        }

                        if (!name.Equals("FlipLineSideRefs", StringComparison.OrdinalIgnoreCase)) (fl.V1, fl.V2) = (fl.V2, fl.V1);
                    }

                    return;
                case "setlinevertexes":
                    if (Line(a[0]) is { } lv)
                    {
                        lv.V1 = Int(a[1]);
                        lv.V2 = Int(a[2]);
                    }

                    return;
                case "setwalltexture":
                case "setwalltextureid":
                    if (Side(Int(a[0]), Int(a[1])) is { } st)
                    {
                        var tex = Convert.ToString(a[3], CultureInfo.InvariantCulture) ?? "-";
                        switch (Int(a[2]))
                        {
                            case 0: st.Top = tex; break;
                            case 1: st.Middle = tex; break;
                            default: st.Bottom = tex; break;
                        }
                    }

                    return;
                case "setwallyscale":
                    if (Side(Int(a[0]), Int(a[1])) is { } sy) sy.Extra[$"scaley_{PartName(Int(a[2]))}"] = UdmfValue.Float(Double(a[3]));
                    return;
                case "setsectorspecial":
                    if (Sector(a[0]) is { } ss) ss.Special = Int(a[1]);
                    return;
                case "setsectortexture":
                case "setsectortextureid":
                    if (Sector(a[0]) is { } stx)
                    {
                        var tex = Convert.ToString(a[2], CultureInfo.InvariantCulture) ?? "-";
                        if (Int(a[1]) == 0) stx.FloorTexture = tex;
                        else stx.CeilingTexture = tex;
                    }

                    return;
                case "setsectorlight":
                    if (Sector(a[0]) is { } sl) sl.Light = Int(a[1]);
                    return;
                case "offsetsectorplane":
                    if (Sector(a[0]) is { } so)
                    {
                        if (Int(a[1]) == 0) so.FloorHeight += Double(a[2]);
                        else so.CeilingHeight += Double(a[2]);
                    }

                    return;
                case "clearsectortags":
                    if (Sector(a[0]) is { } sc)
                    {
                        sc.Id = 0;
                        sc.Extra.Remove("moreids");
                    }

                    return;
                case "addsectortag":
                    if (Sector(a[0]) is { } sa) AddId(sa.Extra, () => sa.Id, v => sa.Id = v, Int(a[1]), 0);
                    return;
                case "clearlineids":
                    if (Line(a[0]) is { } lc)
                    {
                        lc.Id = -1;
                        lc.Extra.Remove("moreids");
                    }

                    return;
                case "addlineid":
                    if (Line(a[0]) is { } li) AddId(li.Extra, () => li.Id, v => li.Id = v, Int(a[1]), -1);
                    return;
                case "setthingflags":
                    if (Thing(a[0]) is { } tf) SetThingFlags(tf, Int(a[1]));
                    return;
                case "setthingskills":
                    if (Thing(a[0]) is { } tsk)
                    {
                        for (var i = 1; i <= 16; i++) tsk.SetFlag($"skill{i}", (Int(a[1]) & (1 << (i - 1))) != 0);
                    }

                    return;
                case "setthingednum":
                    if (Thing(a[0]) is { } te) te.Type = Int(a[1]);
                    return;
                case "setthingxy":
                    if (Thing(a[0]) is { } txy)
                    {
                        txy.X = Double(a[1]);
                        txy.Y = Double(a[2]);
                    }

                    return;
                case "setthingz":
                    if (Thing(a[0]) is { } tz) tz.Height = Double(a[1]);
                    return;
                case "setthingangle":
                    if (Thing(a[0]) is { } ta) ta.Angle = Int(a[1]);
                    return;
                case "setthingid":
                    if (Thing(a[0]) is { } tid) tid.Id = Int(a[1]);
                    return;
                case "setthingspecial":
                    if (Thing(a[0]) is { } tsp) tsp.Special = Int(a[1]);
                    return;
                case "setthingargument":
                    if (Thing(a[0]) is { } targ) targ.Args[Int(a[1])] = Int(a[2]);
                    return;
                case "setvertex":
                    if (Int(a[0]) < map.Vertices.Count)
                    {
                        map.Vertices[Int(a[0])].X = Double(a[1]);
                        map.Vertices[Int(a[0])].Y = Double(a[2]);
                    }

                    return;
                default:
                    notes.Add($"The engine's repair {name} has no map-file equivalent WadSmith writes; it was skipped.");
                    return;
            }
        }

        private static void AddId(UdmfFields extra, Func<int> get, Action<int> set, int id, int none)
        {
            if (get() == none || get() == 0)
            {
                set(id);
                return;
            }

            var more = extra.TryGet("moreids", out var v) ? v.AsString() : "";
            extra["moreids"] = UdmfValue.Str((more.Length > 0 ? more + " " : "") + id.ToString(CultureInfo.InvariantCulture));
        }

        private static readonly (string Field, int Bit)[] Activation =
        [
            ("playercross", 1), ("playeruse", 2), ("monstercross", 4), ("impact", 8), ("playerpush", 16), ("missilecross", 32),
            ("anycross", 128), ("monsteruse", 256), ("monsterpush", 512), ("playeruseback", 1024),
        ];

        private static void SetActivation(MapLine l, int spac)
        {
            // Use-through (64) is UDMF's use plus pass-use.
            if ((spac & 64) != 0) spac = (spac & ~64) | 2;
            foreach (var (field, bit) in Activation)
            {
                if ((spac & bit) != 0) l.Extra[field] = true;
            }

            if ((spac & 64) != 0) l.Extra["passuse"] = true;
        }

        private static readonly (string Field, int Bit)[] ThingFlagNames =
        [
            ("ambush", 0x8), ("dormant", 0x10), ("class1", 0x20), ("class2", 0x40), ("class3", 0x80), ("single", 0x100), ("coop", 0x200), ("dm", 0x400),
            ("translucent", 0x800), ("invisible", 0x1000), ("friend", 0x2000), ("standing", 0x4000), ("countsecret", 0x80000), ("nocount", 0x200000),
        ];

        private void SetThingFlags(MapThing t, int flags)
        {
            foreach (var (field, bit) in ThingFlagNames) t.SetFlag(field, (flags & bit) != 0);
            if ((flags & 0x100000) != 0) notes.Add("The engine stops a thing from fighting other monsters here; UDMF has no field for that, so the remix does not carry it.");
        }

        private static int ThingFlags(MapThing t) => ThingFlagNames.Where(f => t.Flag(f.Field)).Aggregate(0, (acc, f) => acc | f.Bit);

        private static int ThingSkills(MapThing t)
        {
            var bits = 0;
            for (var i = 1; i <= 16; i++)
            {
                if (t.Flag($"skill{i}")) bits |= 1 << (i - 1);
            }

            return bits;
        }

        private MapLine? Line(object index) => Int(index) is var i && i >= 0 && i < map.Lines.Count ? map.Lines[i] : null;

        private MapSector? Sector(object index) => Int(index) is var i && i >= 0 && i < map.Sectors.Count ? map.Sectors[i] : null;

        private MapThing? Thing(object index) => Int(index) is var i && i >= 0 && i < map.Things.Count ? map.Things[i] : null;

        private MapSide? Side(int line, int side)
        {
            if (line < 0 || line >= map.Lines.Count) return null;
            var index = side == 0 ? map.Lines[line].Front : map.Lines[line].Back;
            return index >= 0 && index < map.Sides.Count ? map.Sides[index] : null;
        }

        private static string PartName(int part) => part switch { 0 => "top", 1 => "mid", _ => "bottom" };

        private static int Int(object v) => Convert.ToInt32(Math.Truncate(Double(v)), CultureInfo.InvariantCulture);

        private static double Double(object v) => v switch
        {
            double d => d,
            string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0,
            _ => Convert.ToDouble(v, CultureInfo.InvariantCulture),
        };

        /// <summary>Skips one statement or block; never consumes the closing brace of the block around it.</summary>
        private static void SkipStatement(Scanner sc)
        {
            var depth = 0;
            while (!sc.AtEnd)
            {
                if (depth == 0 && sc.Peek().Is("}")) return;
                var t = sc.Next();
                if (t.Is("{"))
                {
                    depth++;
                }
                else if (t.Is("}"))
                {
                    depth--;
                    if (depth == 0) return;
                }
                else if (depth == 0 && t.Is(";"))
                {
                    return;
                }
            }
        }
    }
}
