using System.Text.Json;

namespace WadSmith.Core.Games;

/// <summary>An action special: what a line or thing does when triggered.</summary>
public sealed record ActionSpecial(int Number, string Name, int LineArgs);

/// <summary>GZDoom's action specials by number and by name, from <c>data/udmf/actions.json</c>.</summary>
public static class ActionCatalog
{
    private static readonly Lazy<(Dictionary<int, ActionSpecial> ByNumber, Dictionary<string, ActionSpecial> ByName)> Table = new(Load);

    public static IReadOnlyCollection<ActionSpecial> All => Table.Value.ByNumber.Values;

    public static ActionSpecial? Get(int number) => Table.Value.ByNumber.GetValueOrDefault(number);

    public static ActionSpecial? Find(string name) => Table.Value.ByName.GetValueOrDefault(name);

    private static (Dictionary<int, ActionSpecial>, Dictionary<string, ActionSpecial>) Load()
    {
        using var doc = JsonDocument.Parse(DataFiles.ReadText("udmf/actions.json"));
        var byNumber = new Dictionary<int, ActionSpecial>();
        var byName = new Dictionary<string, ActionSpecial>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in doc.RootElement.GetProperty("actions").EnumerateArray())
        {
            var s = new ActionSpecial(a.GetProperty("number").GetInt32(), a.GetProperty("name").GetString()!, a.GetProperty("lineArgs").GetInt32());
            if (!byNumber.TryAdd(s.Number, s)) throw new InvalidDataException($"actions.json lists action {s.Number} twice.");
            if (!byName.TryAdd(s.Name, s)) throw new InvalidDataException($"actions.json lists {s.Name} twice.");
        }

        return (byNumber, byName);
    }
}
