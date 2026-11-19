using System.Globalization;
using System.Text.Json.Nodes;
using PayEngine.Contracts;
using PayEngine.Model;
namespace PayEngine.Loading;
internal static partial class SectionCompiler
{
    private static void ReadSigning(JsonObject obj, string root, string section, ProviderPlan plan, List<string> problems)
    {
        var path = root + ":Signing";
        if (!obj.TryGetPropertyValue("Signing", out var node) || node is null) { return; }
        if (node is not JsonObject signing) { problems.Add(path + ": expected an object."); return; }
        JsonLocal.RejectUnknown(signing, Words.SigningKeys, path, problems); plan.RequestSign = ReadSign(signing, "Request", path, section, callbackSide: false, problems);
        plan.PayinCallbackSign = ReadSign(signing, "PayinCallback", path, section, callbackSide: true, problems);
        if (!signing.TryGetPropertyValue("PayoutCallback", out var payoutNode) || payoutNode is null) { plan.PayoutCallbackSign = plan.PayinCallbackSign; return; }
        if (payoutNode is not JsonObject payout) { problems.Add(path + ":PayoutCallback: expected an object."); return; }
        if (payout.Count == 0) { plan.PayoutCallbackSign = plan.PayinCallbackSign; return; }
        plan.PayoutCallbackSign = CompileSign(payout, path + ":PayoutCallback", section, callbackSide: true, problems);
    }
    private static SignSpec? ReadSign(
        JsonObject parent,
        string key,
        string path,
        string section,
        bool callbackSide,
        List<string> problems)
    {
        if (!parent.TryGetPropertyValue(key, out var node) || node is null) { return null; }
        if (node is not JsonObject obj) { problems.Add(path + ":" + key + ": expected an object."); return null; }
        return CompileSign(obj, path + ":" + key, section, callbackSide, problems);
    }
    private static SignSpec CompileSign(JsonObject obj, string path, string section, bool callbackSide, List<string> problems)
    {
        JsonLocal.RejectUnknown(obj, Words.SignKeys, path, problems); var algorithmText = JsonLocal.Required(obj, "Algorithm", path, problems);
        if (!Words.Algorithms.TryGetValue(algorithmText, out var algorithm))
        {
            if (algorithmText.Length > 0) { problems.Add(path + ":Algorithm: closed list rejected '" + algorithmText + "'."); }
            algorithm = DigestAlgorithm.None;
        }
        var encoding = DigestEncoding.HexLower;
        if (obj.TryGetPropertyValue("Encoding", out var encodingNode) && encodingNode is not null)
        {
            if (JsonLocal.TryString(encodingNode, path + ":Encoding", problems, out var encodingText)
                && !Words.Encodings.TryGetValue(encodingText, out encoding)
                && encodingText.Length > 0)
            {
                problems.Add(path + ":Encoding: closed list rejected '" + encodingText + "'.");
            }
        }
        else if (algorithm != DigestAlgorithm.None) { problems.Add(path + ":Encoding: expected a string."); }
        var baseTemplate = ReadOptionalTemplate(obj, "Base", path, PieceSet.Body, section, problems); var keyTemplate = ReadOptionalTemplate(obj, "Key", path, PieceSet.Account, section, problems);
        var header = JsonLocal.Optional(obj, "Header", path, problems); var bodyField = JsonLocal.Optional(obj, "BodyField", path, problems); var required = true;
        if (obj.TryGetPropertyValue("Required", out var requiredNode) && requiredNode is not null)
        {
            if (!JsonLocal.TryBool(requiredNode, path + ":Required", problems, out required)) { required = true; }
        }
        if (algorithm != DigestAlgorithm.None)
        {
            if (!JsonLocal.Has(obj, "Base")) { problems.Add(path + ":Base: expected a string."); }
            if (!JsonLocal.Has(obj, "Key")) { problems.Add(path + ":Key: expected a string."); }
            if (header.Length == 0 && bodyField.Length == 0) { problems.Add(path + ": Header or BodyField is required."); }
            if (callbackSide && header.Length == 0) { problems.Add(path + ":Header: verification needs a header."); }
        }
        if (header.Length > 0 && !UsableHeader(header)) { problems.Add(path + ":Header: header name is not usable."); }
        if (bodyField.Length > 0 && !UsableName(bodyField)) { problems.Add(path + ":BodyField: field name is not usable."); }
        return new SignSpec
        {
            Algorithm = algorithm,
            Base = baseTemplate,
            Encoding = encoding,
            Key = keyTemplate,
            Header = header,
            BodyField = bodyField,
            Required = required,
        };
    }
    private static Template ReadOptionalTemplate(
        JsonObject obj,
        string key,
        string path,
        PieceSet allowed,
        string section,
        List<string> problems)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null) { return new Template(); }
        if (!JsonLocal.TryString(node, path + ":" + key, problems, out var text)) { return new Template(); }
        return TemplateGrammar.Parse(text, path + ":" + key, allowed, section, problems);
    }
    private static void ReadDirection(
        JsonObject section,
        string root,
        string owner,
        string name,
        bool payout,
        ProviderPlan plan,
        List<string> problems)
    {
        var path = root + ":" + name;
        if (!section.TryGetPropertyValue(name, out var node) || node is null) { return; }
        if (node is not JsonObject obj) { problems.Add(path + ": expected an object."); return; }
        JsonLocal.RejectUnknown(obj, Words.DirectionKeys, path, problems);
        if (obj.TryGetPropertyValue("Create", out var createNode))
        {
            var create = ReadCreate(createNode, path + ":Create", owner, problems);
            if (payout) { plan.PayoutCreate = create; }
            else { plan.PayinCreate = create; }
        }
        if (obj.TryGetPropertyValue("Response", out var responseNode))
        {
            var response = ReadResponse(responseNode, path + ":Response", problems);
            if (payout) { plan.PayoutResponse = response; }
            else { plan.PayinResponse = response; }
        }
        if (obj.TryGetPropertyValue("Callback", out var callbackNode))
        {
            var callback = ReadCallback(callbackNode, path + ":Callback", problems);
            if (payout) { plan.PayoutCallbackSpec = callback; }
            else { plan.PayinCallbackSpec = callback; }
        }
    }
    private static CreateSpec? ReadCreate(JsonNode? node, string path, string owner, List<string> problems)
    {
        if (node is not JsonObject obj) { problems.Add(path + ": expected an object."); return null; }
        JsonLocal.RejectUnknown(obj, Words.CreateKeys, path, problems); var route = JsonLocal.Required(obj, "Path", path, problems);
        if (route.StartsWith('/') || route.Contains('\\', StringComparison.Ordinal) || route.Contains(' ', StringComparison.Ordinal) || route.Contains('{', StringComparison.Ordinal) || route.Contains('?', StringComparison.Ordinal)) { problems.Add(path + ":Path: path must not start with a slash or contain a placeholder."); }
        var verbText = JsonLocal.Required(obj, "Verb", path, problems);
        if (verbText.Length > 0 && !Words.Verbs.TryGetValue(verbText, out _)) { problems.Add(path + ":Verb: closed list rejected '" + verbText + "'."); }
        var formatText = JsonLocal.Required(obj, "ContentType", path, problems); var format = BodyFormat.Json;
        if (formatText.Length > 0 && !Words.BodyFormats.TryGetValue(formatText, out format)) { problems.Add(path + ":ContentType: closed list rejected '" + formatText + "'."); format = BodyFormat.Json; }
        var headers = ReadHeaders(obj, path, owner, problems); var fields = ReadBody(obj, path, owner, format, problems); CheckShape(fields, format, path + ":Body", problems);
        return new CreateSpec
        {
            Path = route,
            Verb = verbText,
            Format = format,
            MediaType = format == BodyFormat.Form
                ? "application/x-www-form-urlencoded"
                : "application/json",
            Headers = headers,
            Fields = fields,
        };
    }
    private static HeaderSpec[] ReadHeaders(JsonObject obj, string path, string owner, List<string> problems)
    {
        if (!obj.TryGetPropertyValue("Headers", out var node) || node is null) { return []; }
        if (node is not JsonObject headers) { problems.Add(path + ":Headers: expected an object."); return []; }
        var list = new List<HeaderSpec>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in headers)
        {
            var child = path + ":Headers:" + pair.Key;
            if (!UsableHeader(pair.Key)) { problems.Add(child + ": header name is not usable."); continue; }
            if (!seen.Add(pair.Key)) { problems.Add(child + ": duplicate header."); continue; }
            if (!JsonLocal.TryString(pair.Value, child, problems, out var text)) { continue; }
            list.Add(new HeaderSpec
            {
                Name = pair.Key,
                Value = TemplateGrammar.Parse(text, child, Words.PaymentValue, owner, problems),
            });
        }
        return list.ToArray();
    }
    private static BodyFieldSpec[] ReadBody(JsonObject obj, string path, string owner, BodyFormat format, List<string> problems)
    {
        var bodyPath = path + ":Body";
        if (!obj.TryGetPropertyValue("Body", out var node) || node is null) { return []; }
        if (node is not JsonObject body) { problems.Add(bodyPath + ": expected an object."); return []; }
        var list = new List<BodyFieldSpec>(); WalkBody(body, bodyPath, "", owner, format, list, problems); return list.ToArray();
    }
    private static void WalkBody(
        JsonObject body,
        string path,
        string keyPrefix,
        string owner,
        BodyFormat format,
        List<BodyFieldSpec> list,
        List<string> problems)
    {
        foreach (var pair in body)
        {
            var child = path + ":" + pair.Key; var key = keyPrefix.Length == 0 ? pair.Key : keyPrefix + "." + pair.Key;
            if (pair.Key.Length == 0 || pair.Key.Trim().Length != pair.Key.Length) { problems.Add(child + ": field name is not usable."); continue; }
            if (pair.Value is JsonObject nested)
            {
                if (format == BodyFormat.Form) { problems.Add(child + ": form body does not nest."); continue; }
                WalkBody(nested, child, key, owner, format, list, problems); continue;
            }
            if (!JsonLocal.TryString(pair.Value, child, problems, out var raw)) { continue; }
            var kind = SplitKind(raw, child, problems, out var expression);
            if (expression.Length == 0) { continue; }
            if (!expression.Contains('{', StringComparison.Ordinal) && !expression.StartsWith("$secret:", StringComparison.Ordinal)) { CheckLiteral(kind, expression, child, problems); }
            list.Add(new BodyFieldSpec
            {
                Key = key,
                Kind = kind,
                SourcePath = child,
                Template = TemplateGrammar.Parse(expression, child, Words.PaymentValue, owner, problems),
            });
        }
    }
    private static ValueKind SplitKind(string raw, string path, List<string> problems, out string expression)
    {
        if (raw.StartsWith("num:", StringComparison.Ordinal))
        {
            expression = raw[4..];
            if (expression.Length == 0) { problems.Add(path + ": number prefix is empty."); }
            return ValueKind.Number;
        }
        if (raw.StartsWith("bool:", StringComparison.Ordinal))
        {
            expression = raw[5..];
            if (expression.Length == 0) { problems.Add(path + ": boolean prefix is empty."); }
            return ValueKind.Boolean;
        }
        expression = raw; return ValueKind.Text;
    }
    private static void CheckLiteral(ValueKind kind, string expression, string path, List<string> problems)
    {
        if (kind == ValueKind.Number
            && !decimal.TryParse(expression, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))
        {
            problems.Add(path + ": '" + expression + "' is not a number.");
        }
        if (kind == ValueKind.Boolean
            && !expression.Equals("true", StringComparison.Ordinal)
            && !expression.Equals("false", StringComparison.Ordinal))
        {
            problems.Add(path + ": '" + expression + "' is not a boolean.");
        }
    }
    private static void CheckShape(BodyFieldSpec[] fields, BodyFormat format, string path, List<string> problems)
    {
        if (format == BodyFormat.Form)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in fields)
            {
                if (field.Key.Length == 0 || !seen.Add(field.Key)) { problems.Add(field.SourcePath + ": duplicate field."); }
            }
            return;
        }
        var root = new ShapeNode();
        foreach (var field in fields)
        {
            if (!root.Insert(field.Key)) { problems.Add(field.SourcePath + ": field '" + field.Key + "' collides with another field."); }
        }
    }
    private sealed class ShapeNode
    {
        private readonly List<ShapeNode> children = []; private bool leaf;
        internal bool Insert(string key)
        {
            var parts = key.Split('.'); var current = this;
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) { return false; }
                ShapeNode? child = null;
                foreach (var existing in current.children)
                {
                    if (existing.Name.Equals(parts[i], StringComparison.Ordinal)) { child = existing; break; }
                }
                var last = i == parts.Length - 1;
                if (child is null)
                {
                    child = new ShapeNode { Name = parts[i], leaf = last };
                    current.children.Add(child); current = child; continue;
                }
                if (last || child.leaf) { return false; }
                current = child;
            }
            return true;
        }
        private string Name { get; init; } = "";
    }
}
