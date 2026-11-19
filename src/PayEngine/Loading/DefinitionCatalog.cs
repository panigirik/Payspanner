using System.Text.Json.Nodes;
using PayEngine.Model;
namespace PayEngine.Loading;
internal sealed class LoadBatch
{
    internal LoadBatch(List<string> problems, List<ProviderPlan> plans)
    {
        this.Problems = problems; this.Plans = plans;
    }
    internal List<string> Problems { get; }
    internal List<ProviderPlan> Plans { get; }
}
internal readonly struct CompileOutcome
{
    internal bool Disabled { get; init; }
    internal ProviderPlan? Plan { get; init; }
    internal static CompileOutcome Off()
    {
        return new CompileOutcome { Disabled = true, Plan = null };
    }
    internal static CompileOutcome Failed()
    {
        return new CompileOutcome { Disabled = false, Plan = null };
    }
    internal static CompileOutcome Ready(ProviderPlan plan)
    {
        return new CompileOutcome { Disabled = false, Plan = plan };
    }
}
internal static class DefinitionCatalog
{
    internal static LoadBatch Load(string directory, IReadOnlyDictionary<string, string> settings)
    {
        var problems = new List<string>(); var plans = new List<ProviderPlan>();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) { problems.Add(directory + ": directory is missing."); return new LoadBatch(problems, plans); }
        string[] files;
        try { files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly); }
        catch (IOException ex)
        {
            problems.Add(directory + ": " + ex.Message); return new LoadBatch(problems, plans);
        }
        catch (UnauthorizedAccessException ex)
        {
            problems.Add(directory + ": " + ex.Message); return new LoadBatch(problems, plans);
        }
        Array.Sort(files, StringComparer.OrdinalIgnoreCase); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files) { ReadFile(file, settings, seen, problems, plans); }
        return new LoadBatch(problems, plans);
    }
    private static void ReadFile(string file,
        IReadOnlyDictionary<string, string> settings,
        HashSet<string> seen,
        List<string> problems,
        List<ProviderPlan> plans)
    {
        var name = Path.GetFileName(file); string text;
        try { text = File.ReadAllText(file); }
        catch (IOException ex)
        {
            problems.Add(name + ": " + ex.Message); return;
        }
        catch (UnauthorizedAccessException ex)
        {
            problems.Add(name + ": " + ex.Message); return;
        }
        JsonNode? root;
        try { root = JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException ex)
        {
            problems.Add(name + ": " + ex.Message); return;
        }
        if (root is not JsonObject document) { problems.Add(name + ": expected an object."); return; }
        JsonLocal.RejectUnknown(document, Words.RootKeys, name, problems);
        if (!document.TryGetPropertyValue("Providers", out var providersNode) || providersNode is not JsonObject providers) { problems.Add(name + ":Providers: expected an object."); return; }
        foreach (var pair in providers) { ReadSection(name, pair.Key, pair.Value, settings, seen, problems, plans); }
    }
    private static void ReadSection(
        string file,
        string section,
        JsonNode? node,
        IReadOnlyDictionary<string, string> settings,
        HashSet<string> seen,
        List<string> problems,
        List<ProviderPlan> plans)
    {
        var path = file + ":Providers:" + section;
        if (section.Length == 0 || section.Contains(':', StringComparison.Ordinal) || section.Contains(' ', StringComparison.Ordinal)) { problems.Add(path + ": section name is not usable."); return; }
        if (IsOff(settings, section)) { return; }
        if (!seen.Add(section)) { problems.Add(path + ": duplicate section."); return; }
        if (node is not JsonObject obj) { problems.Add(path + ": expected an object."); return; }
        var outcome = SectionCompiler.Compile(file, section, obj, problems);
        if (outcome.Disabled) { seen.Remove(section); return; }
        if (outcome.Plan is not null) { plans.Add(outcome.Plan); }
    }
    private static bool IsOff(IReadOnlyDictionary<string, string> settings, string section)
    {
        if (!settings.TryGetValue(section + ":Enabled", out var value) || value is null) { return false; }
        return value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
    }
}
