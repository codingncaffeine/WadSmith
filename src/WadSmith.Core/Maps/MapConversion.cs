using WadSmith.Core.Containers;
using WadSmith.Core.Maps.Classic;
using WadSmith.Core.Maps.Udmf;

namespace WadSmith.Core.Maps;

/// <summary>A bit of a classic flag word and the UDMF boolean field(s) it stands for.</summary>
/// <param name="Mask">The bit.</param>
/// <param name="Fields">The fields it sets.</param>
/// <param name="Inverted">The field is true when the bit is clear (Doom's "not in single player").</param>
internal readonly record struct FlagBit(int Mask, string[] Fields, bool Inverted = false);

/// <summary>
/// Classic flag layouts per UDMF namespace, as GZDoom reads them (its <c>xlat/*.txt</c> line flags and
/// its thing-flag loader).
/// </summary>
internal static class FlagLayouts
{
    private static FlagBit B(int mask, params string[] fields) => new(mask, fields);

    private static FlagBit Not(int mask, string field) => new(mask, [field], Inverted: true);

    private static readonly FlagBit[] BaseLine =
    [
        B(0x0001, "blocking"), B(0x0002, "blockmonsters"), B(0x0004, "twosided"), B(0x0008, "dontpegtop"),
        B(0x0010, "dontpegbottom"), B(0x0020, "secret"), B(0x0040, "blocksound"), B(0x0080, "dontdraw"), B(0x0100, "mapped"),
    ];

    public static readonly FlagBit[] DoomLine = [.. BaseLine, B(0x0200, "passuse"), B(0x0400, "midtex3d"), B(0x1000, "blocklandmonsters"), B(0x2000, "blockplayers")];

    public static readonly FlagBit[] HereticLine = [.. BaseLine, B(0x0200, "passuse"), B(0x0400, "midtex3d")];

    public static readonly FlagBit[] StrifeLine = [.. BaseLine, B(0x0200, "jumpover"), B(0x0400, "blockfloaters"), B(0x0800, "transparent"), B(0x1000, "translucent")];

    /// <summary>Hexen-format lines; bits 10–12 are the activation, handled separately.</summary>
    public static readonly FlagBit[] HexenLine = [.. BaseLine, B(0x0200, "repeatspecial"), B(0x2000, "monsteractivate"), B(0x4000, "blockplayers"), B(0x8000, "blockeverything")];

    public static readonly FlagBit[] DoomThing =
    [
        B(0x0001, "skill1", "skill2"), B(0x0002, "skill3"), B(0x0004, "skill4", "skill5"), B(0x0008, "ambush"),
        Not(0x0010, "single"), Not(0x0020, "dm"), Not(0x0040, "coop"), B(0x0080, "friend"),
    ];

    public static readonly FlagBit[] StrifeThing =
    [
        B(0x0001, "skill1", "skill2"), B(0x0002, "skill3"), B(0x0004, "skill4", "skill5"), B(0x0008, "standing"),
        Not(0x0010, "single"), B(0x0020, "ambush"), B(0x0040, "strifeally"), B(0x0100, "translucent"), B(0x0200, "invisible"),
    ];

    public static readonly FlagBit[] HexenThing =
    [
        B(0x0001, "skill1", "skill2"), B(0x0002, "skill3"), B(0x0004, "skill4", "skill5"), B(0x0008, "ambush"),
        B(0x0010, "dormant"), B(0x0020, "class1"), B(0x0040, "class2"), B(0x0080, "class3"),
        B(0x0100, "single"), B(0x0200, "coop"), B(0x0400, "dm"),
        B(0x0800, "translucent"), B(0x1000, "invisible"), B(0x2000, "friend"), B(0x4000, "standing"),
    ];

    /// <summary>Hexen-format activation (bits 10–12) as UDMF trigger fields.</summary>
    public static readonly string[][] HexenActivation =
    [
        ["playercross"], ["playeruse"], ["monstercross"], ["impact"], ["playerpush"], ["missilecross"],
        ["playeruse", "passuse"], ["playercross", "monstercross", "missilecross"],
    ];

    public static void Apply(int flags, FlagBit[] layout, UdmfFields into)
    {
        foreach (var bit in layout)
        {
            var on = (flags & bit.Mask) != 0;
            if (bit.Inverted) on = !on;
            if (!on) continue;
            foreach (var f in bit.Fields) into[f] = true;
        }
    }

    public static int Collect(UdmfFields from, FlagBit[] layout)
    {
        var flags = 0;
        foreach (var bit in layout)
        {
            var any = bit.Fields.Any(f => from.TryGet(f, out var v) && v.AsBool());
            if (any != bit.Inverted) flags |= bit.Mask;
        }

        return flags;
    }

    /// <summary>The field names a layout owns, so they can be told apart from other extra fields.</summary>
    public static IEnumerable<string> Names(FlagBit[] layout) => layout.SelectMany(b => b.Fields);
}

/// <summary>Converts between the map model and its stored forms.</summary>
public static class MapConversion
{
    /// <summary>
    /// The UDMF namespace a classic map's fields mean in, for a game: Doom-format maps keep their
    /// game's own specials ("doom", "heretic", "strife"); Hexen-format maps are "hexen" in Hexen and
    /// "zdoom" (ZDoom's Hexen-format extensions) everywhere else.
    /// </summary>
    public static string ClassicNamespace(MapFormat format, string game) => format switch
    {
        MapFormat.Hexen => game == "hexen" ? "hexen" : "zdoom",
        _ => game switch
        {
            "heretic" => "heretic",
            "strife" => "strife",
            _ => "doom",
        },
    };

    // ---- UDMF ----

    public static MapModel FromUdmf(string name, UdmfDocument doc, IEnumerable<(string Name, byte[] Data)>? otherLumps = null)
    {
        var m = new MapModel { Name = name, Namespace = doc.Namespace ?? "" };
        foreach (var (k, v) in doc.Globals)
        {
            if (!k.Equals("namespace", StringComparison.OrdinalIgnoreCase)) m.Globals[k] = v;
        }

        foreach (var b in doc.Blocks)
        {
            switch (b.Type)
            {
                case "vertex":
                {
                    var v = new MapVertex();
                    foreach (var (k, val) in b.Fields)
                    {
                        switch (k.ToLowerInvariant())
                        {
                            case "x": v.X = val.AsFloat(); break;
                            case "y": v.Y = val.AsFloat(); break;
                            default: v.Extra[k] = val; break;
                        }
                    }

                    m.Vertices.Add(v);
                    break;
                }

                case "linedef":
                {
                    var l = new MapLine();
                    foreach (var (k, val) in b.Fields)
                    {
                        switch (k.ToLowerInvariant())
                        {
                            case "v1": l.V1 = (int)val.AsInt(); break;
                            case "v2": l.V2 = (int)val.AsInt(); break;
                            case "sidefront": l.Front = (int)val.AsInt(); break;
                            case "sideback": l.Back = (int)val.AsInt(); break;
                            case "special": l.Special = (int)val.AsInt(); break;
                            case "id": l.Id = (int)val.AsInt(); break;
                            case "arg0": l.Args[0] = (int)val.AsInt(); break;
                            case "arg1": l.Args[1] = (int)val.AsInt(); break;
                            case "arg2": l.Args[2] = (int)val.AsInt(); break;
                            case "arg3": l.Args[3] = (int)val.AsInt(); break;
                            case "arg4": l.Args[4] = (int)val.AsInt(); break;
                            default: l.Extra[k] = val; break;
                        }
                    }

                    m.Lines.Add(l);
                    break;
                }

                case "sidedef":
                {
                    var s = new MapSide();
                    foreach (var (k, val) in b.Fields)
                    {
                        switch (k.ToLowerInvariant())
                        {
                            case "offsetx": s.OffsetX = val.AsFloat(); break;
                            case "offsety": s.OffsetY = val.AsFloat(); break;
                            case "texturetop": s.Top = val.AsString(); break;
                            case "texturebottom": s.Bottom = val.AsString(); break;
                            case "texturemiddle": s.Middle = val.AsString(); break;
                            case "sector": s.Sector = (int)val.AsInt(); break;
                            default: s.Extra[k] = val; break;
                        }
                    }

                    m.Sides.Add(s);
                    break;
                }

                case "sector":
                {
                    var s = new MapSector();
                    foreach (var (k, val) in b.Fields)
                    {
                        switch (k.ToLowerInvariant())
                        {
                            case "heightfloor": s.FloorHeight = val.AsFloat(); break;
                            case "heightceiling": s.CeilingHeight = val.AsFloat(); break;
                            case "texturefloor": s.FloorTexture = val.AsString(); break;
                            case "textureceiling": s.CeilingTexture = val.AsString(); break;
                            case "lightlevel": s.Light = (int)val.AsInt(); break;
                            case "special": s.Special = (int)val.AsInt(); break;
                            case "id": s.Id = (int)val.AsInt(); break;
                            default: s.Extra[k] = val; break;
                        }
                    }

                    m.Sectors.Add(s);
                    break;
                }

                case "thing":
                {
                    var t = new MapThing();
                    foreach (var (k, val) in b.Fields)
                    {
                        switch (k.ToLowerInvariant())
                        {
                            case "x": t.X = val.AsFloat(); break;
                            case "y": t.Y = val.AsFloat(); break;
                            case "height": t.Height = val.AsFloat(); break;
                            case "angle": t.Angle = (int)val.AsInt(); break;
                            case "type": t.Type = (int)val.AsInt(); break;
                            case "special": t.Special = (int)val.AsInt(); break;
                            case "id": t.Id = (int)val.AsInt(); break;
                            case "arg0": t.Args[0] = (int)val.AsInt(); break;
                            case "arg1": t.Args[1] = (int)val.AsInt(); break;
                            case "arg2": t.Args[2] = (int)val.AsInt(); break;
                            case "arg3": t.Args[3] = (int)val.AsInt(); break;
                            case "arg4": t.Args[4] = (int)val.AsInt(); break;
                            default: t.Extra[k] = val; break;
                        }
                    }

                    m.Things.Add(t);
                    break;
                }

                default:
                    m.OtherBlocks.Add(b);
                    break;
            }
        }

        if (otherLumps is not null) m.OtherLumps.AddRange(otherLumps);
        return m;
    }

    /// <summary>The map as a UDMF document: required fields always, the rest only where they differ from the defaults.</summary>
    public static UdmfDocument ToUdmf(MapModel m)
    {
        var doc = new UdmfDocument { Namespace = m.Namespace };
        foreach (var (k, v) in m.Globals) doc.Globals[k] = v;

        foreach (var t in m.Things)
        {
            var b = new UdmfBlock("thing");
            var f = b.Fields;
            if (t.Id != 0) f["id"] = t.Id;
            f["x"] = UdmfValue.Float(t.X);
            f["y"] = UdmfValue.Float(t.Y);
            if (t.Height != 0) f["height"] = Number(t.Height);
            if (t.Angle != 0) f["angle"] = t.Angle;
            f["type"] = t.Type;
            if (t.Special != 0) f["special"] = t.Special;
            for (var i = 0; i < 5; i++)
            {
                if (t.Args[i] != 0) f[$"arg{i}"] = t.Args[i];
            }

            foreach (var (k, v) in t.Extra) f[k] = v;
            doc.Blocks.Add(b);
        }

        foreach (var v in m.Vertices)
        {
            var b = new UdmfBlock("vertex");
            b.Fields["x"] = UdmfValue.Float(v.X);
            b.Fields["y"] = UdmfValue.Float(v.Y);
            foreach (var (k, val) in v.Extra) b.Fields[k] = val;
            doc.Blocks.Add(b);
        }

        foreach (var l in m.Lines)
        {
            var b = new UdmfBlock("linedef");
            var f = b.Fields;
            if (l.Id != -1) f["id"] = l.Id;
            f["v1"] = l.V1;
            f["v2"] = l.V2;
            f["sidefront"] = l.Front;
            if (l.Back != -1) f["sideback"] = l.Back;
            if (l.Special != 0) f["special"] = l.Special;
            for (var i = 0; i < 5; i++)
            {
                if (l.Args[i] != 0) f[$"arg{i}"] = l.Args[i];
            }

            foreach (var (k, val) in l.Extra) f[k] = val;
            doc.Blocks.Add(b);
        }

        foreach (var s in m.Sides)
        {
            var b = new UdmfBlock("sidedef");
            var f = b.Fields;
            if (s.OffsetX != 0) f["offsetx"] = Number(s.OffsetX);
            if (s.OffsetY != 0) f["offsety"] = Number(s.OffsetY);
            if (s.Top != "-") f["texturetop"] = s.Top;
            if (s.Bottom != "-") f["texturebottom"] = s.Bottom;
            if (s.Middle != "-") f["texturemiddle"] = s.Middle;
            f["sector"] = s.Sector;
            foreach (var (k, val) in s.Extra) f[k] = val;
            doc.Blocks.Add(b);
        }

        foreach (var s in m.Sectors)
        {
            var b = new UdmfBlock("sector");
            var f = b.Fields;
            if (s.FloorHeight != 0) f["heightfloor"] = Number(s.FloorHeight);
            if (s.CeilingHeight != 0) f["heightceiling"] = Number(s.CeilingHeight);
            f["texturefloor"] = s.FloorTexture;
            f["textureceiling"] = s.CeilingTexture;
            if (s.Light != 160) f["lightlevel"] = s.Light;
            if (s.Special != 0) f["special"] = s.Special;
            if (s.Id != 0) f["id"] = s.Id;
            foreach (var (k, val) in s.Extra) f[k] = val;
            doc.Blocks.Add(b);
        }

        doc.Blocks.AddRange(m.OtherBlocks);
        return doc;
    }

    /// <summary>The lumps of a UDMF map after its name marker: TEXTMAP, the kept lumps, ENDMAP.</summary>
    public static IReadOnlyList<(string Name, byte[] Data)> ToUdmfLumps(MapModel m)
    {
        var lumps = new List<(string, byte[])> { ("TEXTMAP", UdmfText.Write(ToUdmf(m))) };
        lumps.AddRange(m.OtherLumps.Where(l => !LumpName.Comparer.Equals(l.Name, "ENDMAP")));
        lumps.Add(("ENDMAP", []));
        return lumps;
    }

    /// <summary>Reads a map in any format into the model. <paramref name="game"/> gives classic maps their namespace.</summary>
    public static MapModel Read(MapLumps lumps, string game = "doom")
    {
        if (lumps.Format == MapFormat.Udmf)
        {
            var textmap = lumps.Get("TEXTMAP") ?? throw new CorruptDataException($"{lumps.Name} has no TEXTMAP lump.");
            var others = lumps.Lumps.Where(l => !LumpName.Comparer.Equals(l.Name, "TEXTMAP") && !LumpName.Comparer.Equals(l.Name, "ENDMAP"));
            return FromUdmf(lumps.Name, UdmfText.Parse(textmap), others);
        }

        return FromClassic(ClassicMap.Read(lumps), ClassicNamespace(lumps.Format, game));
    }

    // ---- Classic ----

    private static (FlagBit[] Lines, FlagBit[] Things) Layouts(MapFormat format, string ns) => format switch
    {
        MapFormat.Hexen => (FlagLayouts.HexenLine, FlagLayouts.HexenThing),
        _ => ns switch
        {
            "heretic" => (FlagLayouts.HereticLine, FlagLayouts.DoomThing),
            "strife" => (FlagLayouts.StrifeLine, FlagLayouts.StrifeThing),
            _ => (FlagLayouts.DoomLine, FlagLayouts.DoomThing),
        },
    };

    /// <summary>
    /// A classic map in the given namespace, with no change of meaning: specials, tags and flags
    /// keep what they meant in the classic format. Translating Doom-format actions into ZDoom's is a
    /// separate step.
    /// </summary>
    public static MapModel FromClassic(ClassicMap c, string ns)
    {
        var hexen = c.Format == MapFormat.Hexen;
        var (lineLayout, thingLayout) = Layouts(c.Format, ns);
        var m = new MapModel { Name = c.Name, Namespace = ns };

        foreach (var v in c.Vertices) m.Vertices.Add(new MapVertex { X = v.X, Y = v.Y });

        foreach (var t in c.Things)
        {
            var thing = new MapThing { X = t.X, Y = t.Y, Angle = t.Angle, Type = t.Type, SourceFlags = t.Flags };
            int flags = t.Flags;
            if (!hexen && ns != "strife" && (flags & 0x0100) != 0) flags &= 0x1F;
            FlagLayouts.Apply(flags, thingLayout, thing.Extra);
            if (!hexen && ns == "strife")
            {
                thing.SetFlag("coop", true);
                thing.SetFlag("dm", true);
            }

            if (hexen)
            {
                thing.Id = t.Tid;
                thing.Height = t.Z;
                thing.Special = t.Special;
                thing.Args[0] = t.Arg0;
                thing.Args[1] = t.Arg1;
                thing.Args[2] = t.Arg2;
                thing.Args[3] = t.Arg3;
                thing.Args[4] = t.Arg4;
            }

            m.Things.Add(thing);
        }

        foreach (var l in c.Lines)
        {
            var line = new MapLine
            {
                V1 = l.V1,
                V2 = l.V2,
                Front = l.Front == ClassicLine.NoSide ? -1 : l.Front,
                Back = l.Back == ClassicLine.NoSide ? -1 : l.Back,
                Special = l.Special,
                SourceFlags = l.Flags,
            };
            int flags = l.Flags;
            // Boom: bit 11 set means an old editor filled the high bits with garbage; only the vanilla nine count.
            if (!hexen && ns == "doom" && (flags & 0x0800) != 0) flags &= 0x01FF;
            FlagLayouts.Apply(flags, lineLayout, line.Extra);
            if (hexen)
            {
                foreach (var f in FlagLayouts.HexenActivation[(flags >> 10) & 7])
                {
                    if (line.Special != 0 || f != "playercross") line.SetFlag(f, true);
                }

                line.Args[0] = l.Arg0;
                line.Args[1] = l.Arg1;
                line.Args[2] = l.Arg2;
                line.Args[3] = l.Arg3;
                line.Args[4] = l.Arg4;
            }
            else
            {
                line.Id = l.Tag == 0 ? -1 : l.Tag;
            }

            m.Lines.Add(line);
        }

        foreach (var s in c.Sides)
        {
            m.Sides.Add(new MapSide
            {
                OffsetX = s.OffsetX,
                OffsetY = s.OffsetY,
                Top = s.Upper.Text,
                Bottom = s.Lower.Text,
                Middle = s.Middle.Text,
                Sector = s.Sector,
                SourceNames = (s.Upper, s.Lower, s.Middle),
            });
        }

        foreach (var s in c.Sectors)
        {
            m.Sectors.Add(new MapSector
            {
                FloorHeight = s.FloorHeight,
                CeilingHeight = s.CeilingHeight,
                FloorTexture = s.FloorTexture.Text,
                CeilingTexture = s.CeilingTexture.Text,
                Light = s.Light,
                Special = s.Special,
                Id = s.Tag,
                SourceNames = (s.FloorTexture, s.CeilingTexture),
            });
        }

        m.OtherLumps.AddRange(c.OtherLumps);
        foreach (var name in c.LumpOrder) m.ClassicLumpOrder.Add(name);
        return m;
    }

    /// <summary>
    /// The map in a classic format. Only meanings the format can hold survive: fractional
    /// coordinates are truncated and fields with no classic bit are dropped. An unchanged map read
    /// from a classic file writes back byte-identical.
    /// </summary>
    public static ClassicMap ToClassic(MapModel m, MapFormat format)
    {
        if (format == MapFormat.Udmf) throw new ArgumentException("Use ToUdmf for UDMF.", nameof(format));
        var hexen = format == MapFormat.Hexen;
        var (lineLayout, thingLayout) = Layouts(format, m.Namespace);
        var c = new ClassicMap { Name = m.Name, Format = format };

        foreach (var v in m.Vertices) c.Vertices.Add(new ClassicVertex(ToShort(v.X), ToShort(v.Y)));

        foreach (var t in m.Things)
        {
            var flags = FlagLayouts.Collect(t.Extra, thingLayout);
            if (t.SourceFlags is { } src && SameThingFlags(src, flags, format, m.Namespace, thingLayout)) flags = src;
            c.Things.Add(hexen
                ? new ClassicThing(ToShort(t.X), ToShort(t.Y), (short)t.Angle, (ushort)t.Type, (ushort)flags, (short)t.Id, ToShort(t.Height), (byte)t.Special, (byte)t.Args[0], (byte)t.Args[1], (byte)t.Args[2], (byte)t.Args[3], (byte)t.Args[4])
                : new ClassicThing(ToShort(t.X), ToShort(t.Y), (short)t.Angle, (ushort)t.Type, (ushort)flags));
        }

        foreach (var l in m.Lines)
        {
            var flags = FlagLayouts.Collect(l.Extra, lineLayout);
            if (hexen) flags |= HexenActivationBits(l) << 10;
            if (l.SourceFlags is { } src && SameLineFlags(src, flags, format, m.Namespace, lineLayout)) flags = src;
            var front = l.Front < 0 ? ClassicLine.NoSide : (ushort)l.Front;
            var back = l.Back < 0 ? ClassicLine.NoSide : (ushort)l.Back;
            c.Lines.Add(hexen
                ? new ClassicLine((ushort)l.V1, (ushort)l.V2, (ushort)flags, (ushort)l.Special, 0, front, back, (byte)l.Args[0], (byte)l.Args[1], (byte)l.Args[2], (byte)l.Args[3], (byte)l.Args[4])
                : new ClassicLine((ushort)l.V1, (ushort)l.V2, (ushort)flags, (ushort)l.Special, (ushort)Math.Max(l.Id, 0), front, back));
        }

        foreach (var s in m.Sides)
        {
            var src = s.SourceNames;
            c.Sides.Add(new ClassicSide(ToShort(s.OffsetX), ToShort(s.OffsetY),
                Keep(src?.Top, s.Top), Keep(src?.Bottom, s.Bottom), Keep(src?.Middle, s.Middle), (ushort)s.Sector));
        }

        foreach (var s in m.Sectors)
        {
            var src = s.SourceNames;
            c.Sectors.Add(new ClassicSector(ToShort(s.FloorHeight), ToShort(s.CeilingHeight),
                Keep(src?.Floor, s.FloorTexture), Keep(src?.Ceiling, s.CeilingTexture), (short)s.Light, (ushort)s.Special, (ushort)s.Id));
        }

        c.OtherLumps.AddRange(m.OtherLumps);
        if (m.ClassicLumpOrder.Count > 0)
        {
            c.LumpOrder.AddRange(m.ClassicLumpOrder);
        }
        else
        {
            c.LumpOrder.AddRange(["THINGS", "LINEDEFS", "SIDEDEFS", "VERTEXES", "SEGS", "SSECTORS", "NODES", "SECTORS", "REJECT", "BLOCKMAP"]);
            c.LumpOrder.AddRange(m.OtherLumps.Select(o => o.Name).Where(n => !c.LumpOrder.Contains(n, LumpName.Comparer)));
        }

        return c;
    }

    /// <summary>An integer token when the value is whole (heights and offsets usually are), a float otherwise.</summary>
    private static UdmfValue Number(double v) => v == Math.Truncate(v) && Math.Abs(v) < int.MaxValue ? UdmfValue.Int((long)v) : UdmfValue.Float(v);

    private static NameField Keep(NameField? source, string text) => source is { } s && s.Text == text ? s : NameField.FromText(text);

    private static short ToShort(double v) => (short)Math.Clamp(Math.Truncate(v), short.MinValue, short.MaxValue);

    private static int HexenActivationBits(MapLine l)
    {
        bool F(string n) => l.Flag(n);
        if (F("playeruse") && F("passuse")) return 6;
        if (F("playercross") && F("monstercross") && F("missilecross")) return 7;
        if (F("playeruse")) return 1;
        if (F("monstercross")) return 2;
        if (F("impact")) return 3;
        if (F("playerpush")) return 4;
        if (F("missilecross")) return 5;
        return 0;
    }

    /// <summary>Whether a source flag word still means what the model says, so its exact bits may be written back.</summary>
    private static bool SameThingFlags(int source, int collected, MapFormat format, string ns, FlagBit[] layout)
    {
        var effective = source;
        if (format != MapFormat.Hexen && ns != "strife" && (effective & 0x0100) != 0) effective &= 0x1F;
        var known = layout.Aggregate(0, (a, b) => a | b.Mask);
        return (effective & known) == (collected & known);
    }

    private static bool SameLineFlags(int source, int collected, MapFormat format, string ns, FlagBit[] layout)
    {
        var effective = source;
        if (format != MapFormat.Hexen && ns == "doom" && (effective & 0x0800) != 0) effective &= 0x01FF;
        var known = layout.Aggregate(0, (a, b) => a | b.Mask) | (format == MapFormat.Hexen ? 0x1C00 : 0);
        return (effective & known) == (collected & known);
    }
}
