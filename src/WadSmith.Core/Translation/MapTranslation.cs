using WadSmith.Core.Games;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;

namespace WadSmith.Core.Translation;

/// <summary>
/// Turns a classic map (read with <see cref="MapConversion.FromClassic"/>) into GZDoom's own UDMF
/// namespace, <c>zdoom</c>, giving every line, sector and thing the meaning GZDoom gives it when it
/// loads the classic map itself (checked against the engine: tools/engine). Doom-format maps go through the engine's translator; Hexen-format
/// maps already use GZDoom's actions and only need their line ids made explicit.
/// </summary>
public static class MapTranslation
{
    // GZDoom's line flag bits (the ML_ values) and their UDMF names in the zdoom namespace.
    private static readonly (int Bit, string Field)[] LineFlagNames =
    [
        (0x00000001, "blocking"), (0x00000002, "blockmonsters"), (0x00000004, "twosided"), (0x00000008, "dontpegtop"),
        (0x00000010, "dontpegbottom"), (0x00000020, "secret"), (0x00000040, "blocksound"), (0x00000080, "dontdraw"),
        (0x00000100, "mapped"), (0x00000200, "repeatspecial"), (0x00002000, "monsteractivate"), (0x00004000, "blockplayers"),
        (0x00008000, "blockeverything"), (0x00010000, "zoneboundary"), (0x00020000, "jumpover"), (0x00040000, "blockfloaters"),
        (0x00080000, "clipmidtex"), (0x00100000, "wrapmidtex"), (0x00200000, "midtex3d"), (0x00400000, "checkswitchrange"),
        (0x00800000, "firstsideonly"), (0x01000000, "blockprojectiles"), (0x02000000, "blockuse"),
    ];

    /// <summary>GZDoom's ML_ line flag bits and their UDMF names, for the repairs interpreter.</summary>
    internal static IReadOnlyList<(int Bit, string Field)> LineFlagNamesTable => LineFlagNames;

    private const int MlSpacMask = 0x1C00;
    private const int MlRepeat = 0x0200;
    private const int MlMonstersCanActivate = 0x2000;
    private const int MlSecret = 0x0020;
    private const int MlFirstSideOnly = 0x00800000;

    // Activation bits as GZDoom keeps them (SPAC_*), and their UDMF fields.
    private const int SpacCross = 1, SpacUse = 2, SpacMCross = 4, SpacImpact = 8, SpacPush = 16, SpacPCross = 32, SpacUseThrough = 64, SpacAnyCross = 128;

    /// <summary>
    /// The map in the zdoom namespace. <paramref name="xlat"/> is required for Doom-format maps
    /// (namespaces doom, heretic, strife) and ignored for Hexen-format ones. Line types the
    /// translator does not know become plain lines, each listed in <paramref name="notes"/>.
    /// </summary>
    public static MapModel ToZDoom(MapModel source, XlatTranslator? xlat, List<string> notes)
    {
        var doomFormat = source.Namespace is "doom" or "heretic" or "strife";
        if (doomFormat && xlat is null) throw new ArgumentException("A Doom-format map needs the engine's translator.", nameof(xlat));
        if (source.Namespace == "zdoom" && source.Lines.All(l => l.SourceFlags is null)) return Copy(source, "zdoom");

        var m = Copy(source, "zdoom");
        m.OtherLumps.RemoveAll(l => l.Name is "SEGS" or "SSECTORS" or "NODES" or "REJECT" or "BLOCKMAP");

        if (doomFormat)
        {
            var layout = source.Namespace switch
            {
                "heretic" => FlagLayouts.HereticLine,
                "strife" => FlagLayouts.StrifeLine,
                _ => FlagLayouts.DoomLine,
            };
            for (var i = 0; i < m.Lines.Count; i++) TranslateDoomLine(m.Lines[i], source.Lines[i], layout, xlat!, notes, i);
            foreach (var s in m.Sectors)
            {
                s.Special = TranslateSector(s.Special, xlat!);

                // Classic maps let things ride a fast-lowering floor down (vanilla behaviour, which the
                // engine turns on for every sector of a map without BEHAVIOR); UDMF states it per sector.
                s.Extra["dropactors"] = true;
                // A secret stays in the type as GZDoom's 1024 bit: its UDMF reader has no sector
                // "secret" key and turns that bit into the secret flag when the map loads.
            }
        }
        else
        {
            foreach (var l in m.Lines) MakeHexenLineIdExplicit(l);
        }

        return m;
    }

    private static void TranslateDoomLine(MapLine line, MapLine original, FlagBit[] layout, XlatTranslator xlat, List<string> notes, int index)
    {
        // Back to the classic flag word, then through the translator's line flag table.
        var classic = original.SourceFlags ?? FlagLayouts.Collect(original.Extra, layout);
        foreach (var name in FlagLayouts.Names(layout)) line.Extra.Remove(name);
        var tag = original.Id < 0 ? 0 : original.Id;
        var special = original.Special;

        var flags1 = classic;
        for (var i = 0; i < 16; i++)
        {
            if ((classic & (1 << i)) != 0 && xlat.LineFlags[i] is { IsMask: true } m) flags1 &= m.Value;
        }

        var flags = 0;
        var flags2 = 0;
        var passthrough = false;
        for (var i = 0; i < 16; i++)
        {
            if ((flags1 & (1 << i)) == 0 || xlat.LineFlags[i] is not { IsMask: false } t) continue;
            switch (t.Value)
            {
                case -1:
                    passthrough = true;
                    break;
                case -2:
                    line.Extra["alpha"] = 0.75;
                    break;
                case -3:
                    line.Extra["alpha"] = 0.25;
                    break;
                default:
                    if ((t.Value & unchecked((int)0x80000000)) != 0 && t.Value != unchecked((int)0x80000000)) flags2 |= t.Value & 0x7FFFFFFF;
                    else flags |= t.Value;
                    break;
            }
        }

        // Boom: every line also takes its tag as its line id.
        line.Id = tag != 0 ? tag : -1;
        Array.Clear(line.Args);
        var activation = 0;

        if (special == 0)
        {
            line.Special = 0;
            line.Args[0] = tag;
        }
        else if (xlat.Lines.TryGetValue(special, out var lt) && lt.Special != 0)
        {
            line.Special = lt.Special;
            flags |= (lt.Flags & 0x1F) << 9;
            if ((lt.Flags & 0x20) != 0) flags |= MlFirstSideOnly;
            activation = 1 << ((flags & MlSpacMask) >> 10);
            if (activation == SpacAnyCross || activation == SpacImpact) activation = SpacImpact | SpacPCross;
            flags &= ~MlSpacMask;
            if (passthrough && activation == SpacUse) activation = SpacUseThrough;
            for (var a = 0; a < 5; a++) line.Args[a] = lt.Args[a].Eval(tag);
            if ((flags & MlSecret) != 0 && (activation & (SpacUse | SpacUseThrough)) != 0) flags &= ~MlMonstersCanActivate;
        }
        else if (xlat.Boom.FirstOrDefault(b => special >= b.First && special <= b.Last) is { } boom)
        {
            line.Special = boom.Special;
            var trigger = special & 7;
            if (trigger is 1 or 3 or 5 or 7) flags |= MlRepeat;
            activation = trigger switch
            {
                0 or 1 => SpacCross,
                4 or 5 => SpacImpact,
                _ => passthrough ? SpacUseThrough : SpacUse,
            };
            line.Args[0] = tag;
            foreach (var arg in boom.Args)
            {
                var flagTemp = (flags >> 9) & 0x3F;
                var found = arg.Filters is null;
                var val = arg.Constant;
                if (arg.Filters is not null)
                {
                    foreach (var (filter, value) in arg.Filters)
                    {
                        if ((special & arg.Mask) == filter)
                        {
                            val = value;
                            found = true;
                        }
                    }
                }

                if (!found) continue;
                val &= 0xFF;
                if (arg.ArgNum < 4)
                {
                    line.Args[arg.ArgNum + 1] = arg.OrExisting ? line.Args[arg.ArgNum + 1] | val : val;
                }
                else
                {
                    flagTemp = arg.OrExisting ? flagTemp | val : val;
                    flags = (flags & ~0x7E00) | (flagTemp << 9);
                }
            }

            // Push triggers act like switches with no tag; a generalized door is told it is manual.
            if (trigger is 6 or 7)
            {
                if (ActionCatalog.Find("Generic_Door")?.Number == boom.Special) line.Args[2] |= 128;
                else line.Args[0] = 0;
            }

            if ((flags & MlMonstersCanActivate) != 0 && activation == SpacCross) activation = SpacAnyCross;
        }
        else
        {
            notes.Add($"Line {index} has the action {special}, which the engine's translator does not define; it became a plain line.");
            line.Special = 0;
        }

        foreach (var (bit, field) in LineFlagNames)
        {
            if ((flags & bit) != 0) line.Extra[field] = true;
        }

        if ((flags2 & 1) != 0) line.Extra["blocklandmonsters"] = true;
        foreach (var f in ActivationFields(activation)) line.Extra[f] = true;
    }

    private static IEnumerable<string> ActivationFields(int activation)
    {
        if ((activation & SpacCross) != 0) yield return "playercross";
        if ((activation & (SpacUse | SpacUseThrough)) != 0) yield return "playeruse";
        if ((activation & SpacUseThrough) != 0) yield return "passuse";
        if ((activation & SpacMCross) != 0) yield return "monstercross";
        if ((activation & SpacImpact) != 0) yield return "impact";
        if ((activation & SpacPush) != 0) yield return "playerpush";
        if ((activation & SpacPCross) != 0) yield return "missilecross";
        if ((activation & SpacAnyCross) != 0) yield return "anycross";
    }

    /// <summary>A Doom-format sector type in GZDoom's numbering: Boom's flag bits moved to GZDoom's positions, the rest through the table.</summary>
    internal static int TranslateSector(int special, XlatTranslator xlat)
    {
        var mask = 0;
        for (var i = xlat.SectorMasks.Count - 1; i >= 0; i--)
        {
            var (bits, op, shift) = xlat.SectorMasks[i];
            var newMask = special & bits;
            if (newMask == 0) continue;
            special &= ~newMask;
            if (op == 1) newMask <<= shift;
            else if (op == -1) newMask >>= shift;
            else if (op == 0 && shift == 1) newMask = 0;
            mask |= newMask;
        }

        if (xlat.Sectors.TryGetValue(special, out var tr))
        {
            special = !tr.BitmaskAllowed && mask != 0 ? 0 : tr.NewType;
        }

        return special | mask;
    }

    /// <summary>
    /// Hexen-format lines get their id from certain actions' arguments; UDMF states it directly.
    /// Line_SetIdentification itself is removed (its arguments stay), as the engine does.
    /// </summary>
    private static void MakeHexenLineIdExplicit(MapLine l)
    {
        string? name = ActionCatalog.Get(l.Special)?.Name;
        int id = -1;
        var moreFlags = 0;
        switch (name)
        {
            case "Line_SetIdentification":
                id = l.Args[0] + 256 * l.Args[4];
                moreFlags = l.Args[1] << 16;
                // The engine removes the action but keeps its arguments.
                l.Special = 0;
                break;
            case "TranslucentLine":
                id = l.Args[0];
                moreFlags = l.Args[3] << 16;
                break;
            case "Teleport_Line":
            case "Scroll_Texture_Model":
                id = l.Args[0];
                break;
            case "Polyobj_StartLine":
                id = l.Args[3];
                break;
            case "Polyobj_ExplicitLine":
                id = l.Args[4];
                break;
            case "Plane_Align":
                id = l.Args[2];
                break;
            case "Static_Init" when l.Args[1] == 3: // Init_SectorLink
                id = l.Args[0];
                break;
            case "Line_SetPortal":
                id = l.Args[1];
                break;
        }

        if (id != -1 && id != 0) l.Id = id;
        foreach (var (bit, field) in LineFlagNames)
        {
            if ((moreFlags & bit) != 0) l.Extra[field] = true;
        }
    }

    /// <summary>A deep copy with a new namespace; classic source bits are dropped, since UDMF has no use for them.</summary>
    private static MapModel Copy(MapModel s, string ns)
    {
        var m = new MapModel { Name = s.Name, Namespace = ns };
        foreach (var (k, v) in s.Globals) m.Globals[k] = v;
        m.OtherBlocks.AddRange(s.OtherBlocks);
        m.OtherLumps.AddRange(s.OtherLumps);
        foreach (var v in s.Vertices)
        {
            var c = new MapVertex { X = v.X, Y = v.Y };
            CopyFields(v.Extra, c.Extra);
            m.Vertices.Add(c);
        }

        foreach (var l in s.Lines)
        {
            var c = new MapLine { V1 = l.V1, V2 = l.V2, Front = l.Front, Back = l.Back, Special = l.Special, Id = l.Id };
            l.Args.CopyTo(c.Args, 0);
            CopyFields(l.Extra, c.Extra);
            m.Lines.Add(c);
        }

        foreach (var sd in s.Sides)
        {
            var c = new MapSide { OffsetX = sd.OffsetX, OffsetY = sd.OffsetY, Top = sd.Top, Bottom = sd.Bottom, Middle = sd.Middle, Sector = sd.Sector };
            CopyFields(sd.Extra, c.Extra);
            m.Sides.Add(c);
        }

        foreach (var sec in s.Sectors)
        {
            var c = new MapSector { FloorHeight = sec.FloorHeight, CeilingHeight = sec.CeilingHeight, FloorTexture = sec.FloorTexture, CeilingTexture = sec.CeilingTexture, Light = sec.Light, Special = sec.Special, Id = sec.Id };
            CopyFields(sec.Extra, c.Extra);
            m.Sectors.Add(c);
        }

        foreach (var t in s.Things)
        {
            var c = new MapThing { X = t.X, Y = t.Y, Height = t.Height, Angle = t.Angle, Type = t.Type, Special = t.Special, Id = t.Id };
            t.Args.CopyTo(c.Args, 0);
            CopyFields(t.Extra, c.Extra);
            m.Things.Add(c);
        }

        return m;
    }

    private static void CopyFields(UdmfFields from, UdmfFields to)
    {
        foreach (var (k, v) in from) to[k] = v;
    }
}
