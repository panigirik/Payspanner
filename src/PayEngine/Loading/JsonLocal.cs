using System.Text.Json.Nodes;
namespace PayEngine.Loading;
internal static class JsonLocal
{
    internal static void RejectUnknown(JsonObject obj, HashSet<string> allowed, string path, List<string> problems)
    {
        foreach (var pair in obj)
        {
            if (!allowed.Contains(pair.Key)) { problems.Add(path + ":" + pair.Key + ": unknown key."); }
        }
    }
    internal static bool TryString(JsonNode? node, string path, List<string> problems, out string value)
    {
        if (node is JsonValue json && json.TryGetValue<string>(out var text) && text is not null) { value = text; return true; }
        problems.Add(path + ": expected a string."); value = ""; return false;
    }
    internal static bool TryBool(JsonNode? node, string path, List<string> problems, out bool value)
    {
        if (node is JsonValue json && json.TryGetValue<bool>(out var flag)) { value = flag; return true; }
        problems.Add(path + ": expected a boolean."); value = false; return false;
    }
    internal static bool TryDecimal(JsonNode? node, string path, List<string> problems, out decimal value)
    {
        if (node is JsonValue json && json.TryGetValue<decimal>(out var number)) { value = number; return true; }
        problems.Add(path + ": expected a number."); value = 0m; return false;
    }
    internal static bool Has(JsonObject obj, string key)
    {
        return obj.ContainsKey(key);
    }
    internal static string Required(JsonObject obj, string key, string path, List<string> problems)
    {
        var child = path + ":" + key;
        if (!obj.TryGetPropertyValue(key, out var node)) { problems.Add(child + ": expected a string."); return ""; }
        if (!TryString(node, child, problems, out var text)) { return ""; }
        if (text.Length == 0 || text.Trim().Length != text.Length) { problems.Add(child + ": expected a non-empty string."); return ""; }
        return text;
    }
    internal static string Optional(JsonObject obj, string key, string path, List<string> problems)
    {
        var child = path + ":" + key;
        if (!obj.TryGetPropertyValue(key, out var node) || node is null) { return ""; }
        if (!TryString(node, child, problems, out var text)) { return ""; }
        if (text.Trim().Length != text.Length) { problems.Add(child + ": expected a trimmed string."); return ""; }
        return text;
    }
}
