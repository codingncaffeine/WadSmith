namespace WadSmith.Core.Containers;

/// <summary>
/// Where a resource lives. In a WAD this comes from marker lumps (<c>S_START</c>…<c>S_END</c> and
/// friends); in a PK3 or a folder from the top-level directory (<c>sprites/</c> and friends).
/// The same name in two namespaces is two different resources.
/// </summary>
public enum ResourceNamespace
{
    /// <summary>Outside any marker or directory: maps, palettes, TEXTURE1, sounds and music in a WAD.</summary>
    Global,
    Sprites,
    Flats,
    Patches,
    /// <summary>ZDoom's <c>TX_START</c> markers and the <c>textures/</c> directory.</summary>
    Textures,
    /// <summary>High-resolution replacements (<c>HI_START</c>, <c>hires/</c>).</summary>
    HiRes,
    /// <summary>Boom colormaps (<c>C_START</c>, <c>colormaps/</c>).</summary>
    Colormaps,
    /// <summary>Compiled ACS libraries (<c>A_START</c>, <c>acs/</c>).</summary>
    Acs,
    /// <summary>Strife voice lines (<c>V_START</c>, <c>voices/</c>).</summary>
    Voices,
    /// <summary>Voxel models (<c>VX_START</c>, <c>voxels/</c>).</summary>
    Voxels,
    /// <summary>The <c>graphics/</c> directory of a PK3.</summary>
    Graphics,
    /// <summary>The <c>sounds/</c> directory of a PK3.</summary>
    Sounds,
    /// <summary>The <c>music/</c> directory of a PK3.</summary>
    Music,
    /// <summary>The <c>maps/</c> directory of a PK3: one WAD per map.</summary>
    Maps,
    /// <summary>The <c>filter/</c> directory of a PK3: content loaded only for a matching game.</summary>
    Filter,
    /// <summary>Any other directory of a PK3 (models, shaders, scripts kept in folders).</summary>
    Other,
}
