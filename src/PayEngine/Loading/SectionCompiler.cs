using System.Globalization;
using System.Text.Json.Nodes;
using PayEngine.Model;
namespace PayEngine.Loading;
internal static partial class SectionCompiler
{
    internal static CompileOutcome Compile(string file, string section, JsonObject obj, List<string> problems)
    {
        var root = file + ":Providers:" + section;
        if (obj.TryGetPropertyValue("Enabled", out var enabledNode) && enabledNode is not null)
        {
            if (!JsonLocal.TryBool(enabledNode, root + ":Enabled", problems, out var enabled)) { return CompileOutcome.Failed(); }
            if (!enabled) { return CompileOutcome.Off(); }
        }
        var mark = problems.Count; JsonLocal.RejectUnknown(obj, Words.SectionKeys, root, problems);
        var plan = new ProviderPlan
        {
            Section = section,
            Code = JsonLocal.Required(obj, "Code", root, problems),
            DisplayName = JsonLocal.Required(obj, "DisplayName", root, problems),
        };
        plan.BaseUrl = ReadBaseUrl(obj, root, problems); plan.TimeoutSeconds = ReadTimeout(obj, root, problems); ReadNotes(obj, root, problems); ReadMasking(obj, root, problems);
        plan.Firms = ReadFirms(obj, root, plan, problems); plan.Accounts = ReadAccounts(obj, root, section, problems);
        plan.Auth = ReadAuth(obj, root, section, problems); ReadSigning(obj, root, section, plan, problems); ReadDirection(obj, root, section, "Payin", payout: false, plan, problems);
        ReadDirection(obj, root, section, "Payout", payout: true, plan, problems); ReadMethods(obj, root, plan, problems);
        ReadCapabilities(obj, root, plan, problems); RequireBlocks(obj, root, plan, problems); LinkAccounts(root, plan, problems);
        if (problems.Count != mark) { return CompileOutcome.Failed(); }
        return CompileOutcome.Ready(plan);
    }
    private static string ReadBaseUrl(JsonObject obj, string root, List<string> problems)
    {
        var path = root + ":BaseUrl"; var text = JsonLocal.Required(obj, "BaseUrl", root, problems);
        if (text.Length == 0) { return ""; }
        if (text.Contains('{', StringComparison.Ordinal) || text.Contains('?', StringComparison.Ordinal) || text.Contains('#', StringComparison.Ordinal) || text.Contains(' ', StringComparison.Ordinal)) { problems.Add(path + ": expected an absolute http URL."); return ""; }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            problems.Add(path + ": expected an absolute http URL."); return "";
        }
        return text;
    }
    private static int ReadTimeout(JsonObject obj, string root, List<string> problems)
    {
        var path = root + ":TimeoutSeconds";
        if (!obj.TryGetPropertyValue("TimeoutSeconds", out var node) || node is null) { return 30; }
        if (!JsonLocal.TryDecimal(node, path, problems, out var number)) { return 30; }
        if (number != decimal.Truncate(number)) { problems.Add(path + ": expected a whole number."); return 30; }
        if (number < 1m || number > 3600m) { problems.Add(path + ": expected a number from 1 to 3600."); return 30; }
        return (int)number;
    }
    private static void ReadNotes(JsonObject obj, string root, List<string> problems)
    {
        if (!obj.TryGetPropertyValue("Notes", out var node) || node is null) { return; }
        if (!JsonLocal.TryString(node, root + ":Notes", problems, out _)) { return; }
    }
    private static void ReadMasking(JsonObject obj, string root, List<string> problems)
    {
        if (!obj.TryGetPropertyValue("Masking", out var node) || node is null) { return; }
        var path = root + ":Masking";
        if (node is not JsonObject mask) { problems.Add(path + ": expected an object."); return; }
        JsonLocal.RejectUnknown(mask, Words.MaskingKeys, path, problems);
        foreach (var key in Words.MaskingKeys)
        {
            if (!mask.TryGetPropertyValue(key, out var list) || list is null) { continue; }
            if (list is not JsonArray array) { problems.Add(path + ":" + key + ": expected an array."); continue; }
            for (var i = 0; i < array.Count; i++) { JsonLocal.TryString(array[i], path + ":" + key + ":" + i.ToString(CultureInfo.InvariantCulture), problems, out _); }
        }
    }
    private static string[] ReadFirms(JsonObject obj, string root, ProviderPlan plan, List<string> problems)
    {
        var path = root + ":Firms";
        if (!obj.TryGetPropertyValue("Firms", out var node) || node is not JsonArray array) { problems.Add(path + ": expected an array."); plan.FirmsBroken = true; return []; }
        if (array.Count == 0) { problems.Add(path + ": expected at least one firm."); plan.FirmsBroken = true; return []; }
        var names = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal); var broken = false;
        for (var i = 0; i < array.Count; i++)
        {
            var itemPath = path + ":" + i.ToString(CultureInfo.InvariantCulture);
            if (!JsonLocal.TryString(array[i], itemPath, problems, out var name)
                || name.Length == 0
                || name.Trim().Length != name.Length)
            {
                problems.Add(itemPath + ": expected a non-empty string."); broken = true; continue;
            }
            if (name.Equals("Common", StringComparison.Ordinal) || name.Contains(':', StringComparison.Ordinal)) { problems.Add(itemPath + ": firm name is not usable."); broken = true; continue; }
            if (!seen.Add(name)) { problems.Add(itemPath + ": duplicate firm."); broken = true; continue; }
            names.Add(name);
        }
        plan.FirmsBroken = broken || names.Count == 0; return names.ToArray();
    }
    private static AccountSpec[] ReadAccounts(JsonObject obj, string root, string section, List<string> problems)
    {
        var path = root + ":Accounts";
        if (!obj.TryGetPropertyValue("Accounts", out var node) || node is null) { return []; }
        if (node is not JsonObject accounts) { problems.Add(path + ": expected an object."); return []; }
        var list = new List<AccountSpec>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in accounts)
        {
            var child = path + ":" + pair.Key;
            if (!UsableName(pair.Key)) { problems.Add(child + ": account name is not usable."); continue; }
            if (!seen.Add(pair.Key)) { problems.Add(child + ": duplicate account."); continue; }
            if (pair.Value is not JsonObject fields) { problems.Add(child + ": expected an object."); continue; }
            list.Add(new AccountSpec
            {
                Name = pair.Key,
                Fields = ReadAccountFields(fields, child, section, problems),
            });
        }
        return list.ToArray();
    }
    private static AccountFieldSpec[] ReadAccountFields(JsonObject fields, string path, string section, List<string> problems)
    {
        var list = new List<AccountFieldSpec>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in fields)
        {
            var child = path + ":" + pair.Key;
            if (!UsableName(pair.Key)) { problems.Add(child + ": field name is not usable."); continue; }
            if (!seen.Add(pair.Key)) { problems.Add(child + ": duplicate field."); continue; }
            if (!JsonLocal.TryString(pair.Value, child, problems, out var text)) { continue; }
            if (text.StartsWith("$secret:", StringComparison.Ordinal))
            {
                if (!SecretSyntax.TryWhole(text, out var owner, out var field)) { problems.Add(child + ": secret reference must be $secret:<section>:<field>."); continue; }
                if (!string.Equals(owner, section, StringComparison.Ordinal)) { problems.Add(child + ": secret section '" + owner + "' does not match '" + section + "'."); continue; }
                list.Add(new AccountFieldSpec
                {
                    Name = pair.Key,
                    IsSecret = true,
                    SecretSection = owner,
                    SecretField = field,
                });
                continue;
            }
            list.Add(new AccountFieldSpec
            {
                Name = pair.Key,
                Literal = text,
            });
        }
        return list.ToArray();
    }
    private static AuthSpec ReadAuth(JsonObject obj, string root, string section, List<string> problems)
    {
        var path = root + ":Auth";
        if (!obj.TryGetPropertyValue("Auth", out var node) || node is null)
        {
            return new AuthSpec { Kind = AuthKind.None, Value = new Template() };
        }
        if (node is not JsonObject auth)
        {
            problems.Add(path + ": expected an object.");
            return new AuthSpec { Kind = AuthKind.None, Value = new Template() };
        }
        JsonLocal.RejectUnknown(auth, AuthAllowed, path, problems);
        if (!auth.TryGetPropertyValue("Kind", out var kindNode) || !JsonLocal.TryString(kindNode, path + ":Kind", problems, out var kindText))
        {
            problems.Add(path + ":Kind: expected a string.");
            return new AuthSpec { Kind = AuthKind.None, Value = new Template() };
        }
        if (!Words.AuthKinds.TryGetValue(kindText, out var kind))
        {
            problems.Add(path + ":Kind: closed list rejected '" + kindText + "'.");
            return new AuthSpec { Kind = AuthKind.None, Value = new Template() };
        }
        var header = JsonLocal.Optional(auth, "Header", path, problems); var valueText = JsonLocal.Optional(auth, "Value", path, problems);
        var value = valueText.Length == 0
            ? new Template()
            : TemplateGrammar.Parse(valueText, path + ":Value", Words.PaymentValue, section, problems);
        if (kind == AuthKind.None && (header.Length > 0 || valueText.Length > 0)) { problems.Add(path + ": Kind None does not take Header or Value."); }
        if (kind == AuthKind.Bearer && header.Length > 0) { problems.Add(path + ":Header: not used when Kind is Bearer."); }
        if (kind == AuthKind.Bearer && valueText.Length == 0) { problems.Add(path + ":Value: expected a string."); }
        if (kind == AuthKind.Header && (header.Length == 0 || valueText.Length == 0)) { problems.Add(path + ": Header and Value are required when Kind is Header."); }
        if (header.Length > 0 && !UsableHeader(header)) { problems.Add(path + ":Header: header name is not usable."); }
        return new AuthSpec
        {
            Kind = kind,
            Header = header,
            Value = value,
        };
    }
    private static readonly HashSet<string> AuthAllowed = new(StringComparer.Ordinal)
    {
        "Kind",
        "Header",
        "Value",
    };
    private static bool UsableName(string name)
    {
        return name.Length > 0 && name.Trim().Length == name.Length && !name.Contains(':', StringComparison.Ordinal) && !name.Contains(' ', StringComparison.Ordinal);
    }
    private static bool UsableHeader(string name)
    {
        if (!UsableName(name)) { return false; }
        foreach (var ch in name)
        {
            if (ch is '\r' or '\n' or ':') { return false; }
        }
        return true;
    }
}
