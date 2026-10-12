namespace WadSmith.Editor.Smart;

/// <summary>Objects as WadSmith places them.</summary>
public static class Things
{
    /// <summary>The flags that put an object in every difficulty and game mode, and for every class where the game has classes.</summary>
    public static IReadOnlyList<string> Everywhere(string engine) => engine == "hexen"
        ? ["skill1", "skill2", "skill3", "skill4", "skill5", "single", "coop", "dm", "class1", "class2", "class3"]
        : ["skill1", "skill2", "skill3", "skill4", "skill5", "single", "coop", "dm"];
}
