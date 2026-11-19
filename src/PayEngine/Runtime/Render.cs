using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PayEngine.Contracts;
using PayEngine.Model;
namespace PayEngine.Runtime;
internal readonly struct Rendered
{
    internal bool Ok { get; init; }
    internal string Text { get; init; }
    internal string Code { get; init; }
    internal string Message { get; init; }
    internal static Rendered Done(string text)
    {
        return new Rendered { Ok = true, Text = text, Code = "", Message = "" };
    }
    internal static Rendered Fail(string code, string message)
    {
        return new Rendered { Ok = false, Text = "", Code = code, Message = message };
    }
}
internal sealed class RenderScope
{
    internal string AttemptId { get; init; } = "";
    internal decimal Sum { get; init; }
    internal string Currency { get; init; } = "";
    internal string CallbackUrl { get; init; } = "";
    internal string ClientPhone { get; init; } = "";
    internal string ProviderMethod { get; init; } = "";
    internal long UnixSeconds { get; init; }
    internal required AccountValues Values { get; init; }
    internal required IReadOnlyDictionary<string, string> Settings { get; init; }
    internal static RenderScope Capture(PayRequest request, MethodSpec method, AccountValues values, IClock clock, bool readClock, IReadOnlyDictionary<string, string> settings)
    {
        return new RenderScope
        {
            AttemptId = request.AttemptId ?? "",
            Sum = request.Sum,
            Currency = (request.Currency ?? "").Trim(),
            CallbackUrl = request.CallbackUrl ?? "",
            ClientPhone = request.ClientPhone ?? "",
            ProviderMethod = method.ProviderMethod,
            UnixSeconds = readClock ? clock.UtcNow.ToUnixTimeSeconds() : 0L,
            Values = values,
            Settings = settings,
        };
    }
}
internal sealed class AccountValues
{
    private readonly Dictionary<string, AccountFieldSpec> fields; private readonly IReadOnlyDictionary<string, string> settings;
    private readonly Dictionary<string, string> cache = new(StringComparer.Ordinal);
    internal AccountValues(AccountSpec account, IReadOnlyDictionary<string, string> settings)
    {
        this.settings = settings; this.fields = new Dictionary<string, AccountFieldSpec>(StringComparer.Ordinal);
        foreach (var field in account.Fields) { this.fields[field.Name] = field; }
    }
    internal bool TryRead(string name, out string value, out string code, out string message)
    {
        value = ""; code = ""; message = "";
        if (this.cache.TryGetValue(name, out var cached)) { value = cached; return true; }
        if (!this.fields.TryGetValue(name, out var field)) { code = "Account.Field"; message = name; return false; }
        if (!field.IsSecret) { value = field.Literal; this.cache[name] = value; return true; }
        var key = field.SecretSection + ":" + field.SecretField;
        if (!this.settings.TryGetValue(key, out var secret) || string.IsNullOrWhiteSpace(secret)) { code = "Secret.Missing"; message = key; return false; }
        value = secret; this.cache[name] = value; return true;
    }
}
internal static class AmountText
{
    internal static string Major(decimal sum)
    {
        return sum.ToString("0.#############################", CultureInfo.InvariantCulture);
    }
    internal static string Minor(decimal sum)
    {
        var minor = decimal.Round(sum * 100m, 0, MidpointRounding.AwayFromZero); return minor.ToString("0", CultureInfo.InvariantCulture);
    }
}
internal static class TemplateEngine
{
    internal static Rendered Render(Template template, RenderScope scope)
    {
        var builder = new StringBuilder();
        foreach (var piece in template.Pieces)
        {
            switch (piece.Kind)
            {
                case TokenKind.Literal: builder.Append(piece.Text ?? ""); break; case TokenKind.AttemptId: builder.Append(scope.AttemptId); break;
                case TokenKind.SumMajor: builder.Append(AmountText.Major(scope.Sum)); break; case TokenKind.SumMinor: builder.Append(AmountText.Minor(scope.Sum)); break;
                case TokenKind.Currency: builder.Append(scope.Currency); break; case TokenKind.CallbackUrl: builder.Append(scope.CallbackUrl); break;
                case TokenKind.ProviderMethod: builder.Append(scope.ProviderMethod); break; case TokenKind.UnixSeconds: builder.Append(scope.UnixSeconds.ToString(CultureInfo.InvariantCulture)); break;
                case TokenKind.ClientPhone: builder.Append(scope.ClientPhone); break;
                case TokenKind.AccountField:
                    if (!scope.Values.TryRead(piece.Text ?? "", out var field, out var code, out var message)) { return Rendered.Fail(code, message); }
                    builder.Append(field); break;
                case TokenKind.Secret:
                    var secretKey = (piece.Text ?? "") + ":" + (piece.Extra ?? "");
                    if (!scope.Settings.TryGetValue(secretKey, out var secret) || string.IsNullOrWhiteSpace(secret)) { return Rendered.Fail("Secret.Missing", secretKey); }
                    builder.Append(secret); break;
                default:
                    return Rendered.Fail("Template.Unknown", piece.Kind.ToString());
            }
        }
        return Rendered.Done(builder.ToString());
    }
}
internal sealed class Composed
{
    internal bool Ok { get; init; }
    internal string Code { get; init; } = "";
    internal string Message { get; init; } = "";
    internal string Body { get; init; } = "";
    internal FieldNode Root { get; init; } = new("");
    internal BodyFormat Format { get; init; }
    internal static Composed Fail(string code, string message)
    {
        return new Composed { Ok = false, Code = code, Message = message };
    }
    internal static Composed Ready(string body, FieldNode root, BodyFormat format)
    {
        return new Composed { Ok = true, Body = body, Root = root, Format = format, Code = "", Message = "" };
    }
}
internal static class BodyComposer
{
    internal static Composed Compose(CreateSpec create, RenderScope scope)
    {
        var root = new FieldNode("")
        {
            Children = [],
        };
        foreach (var field in create.Fields)
        {
            var rendered = TemplateEngine.Render(field.Template, scope);
            if (!rendered.Ok) { return Composed.Fail(rendered.Code, rendered.Message); }
            if (!Canonical(field, rendered.Text, out var text, out var code, out var message)) { return Composed.Fail(code, message); }
            if (create.Format == BodyFormat.Json)
            {
                if (!FieldNode.Insert(root, field.Key, text, field.Kind)) { return Composed.Fail("Body.Shape", field.SourcePath); }
            }
            else
            {
                root.Children.Add(new FieldNode(field.Key)
                {
                    Scalar = text,
                    Kind = field.Kind,
                });
            }
        }
        var body = create.Format == BodyFormat.Json ? JsonBody.Write(root) : FormCodec.Encode(root); return Composed.Ready(body, root, create.Format);
    }
    private static bool Canonical(BodyFieldSpec field, string rendered, out string text, out string code, out string message)
    {
        text = rendered; code = ""; message = "";
        if (field.Kind == ValueKind.Text) { return true; }
        if (field.Kind == ValueKind.Number)
        {
            if (!decimal.TryParse(rendered, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)) { code = "Body.Number"; message = field.SourcePath + ": '" + rendered + "' is not a number."; return false; }
            text = AmountText.Major(number); return true;
        }
        if (rendered.Equals("true", StringComparison.OrdinalIgnoreCase)) { text = "true"; return true; }
        if (rendered.Equals("false", StringComparison.OrdinalIgnoreCase)) { text = "false"; return true; }
        code = "Body.Boolean"; message = field.SourcePath + ": '" + rendered + "' is not a boolean."; return false;
    }
}
internal static class HeaderBag
{
    internal static void Set(Dictionary<string, string> headers, string name, string value)
    {
        string? remove = null;
        foreach (var key in headers.Keys)
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase)) { remove = key; break; }
        }
        if (remove is not null && !remove.Equals(name, StringComparison.Ordinal)) { headers.Remove(remove); }
        headers[name] = value;
    }
    internal static bool TryGet(IReadOnlyDictionary<string, string>? headers, string name, out string value)
    {
        if (headers is not null)
        {
            foreach (var pair in headers)
            {
                if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = pair.Value ?? ""; return true; }
            }
        }
        value = ""; return false;
    }
    internal static Rendered Fill(CreateSpec create, RenderScope scope, string signatureHeader, string signatureValue, out Dictionary<string, string> headers)
    {
        headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in create.Headers)
        {
            var rendered = TemplateEngine.Render(header.Value, scope);
            if (!rendered.Ok) { return rendered; }
            Set(headers, header.Name, rendered.Text);
        }
        Set(headers, "Content-Type", create.MediaType);
        if (signatureHeader.Length > 0) { Set(headers, signatureHeader, signatureValue); }
        return Rendered.Done("");
    }
}
internal static class AuthWriter
{
    internal static Rendered Apply(AuthSpec auth, Dictionary<string, string> headers, RenderScope scope)
    {
        if (auth.Kind == AuthKind.None) { return Rendered.Done(""); }
        var value = TemplateEngine.Render(auth.Value, scope);
        if (!value.Ok) { return value; }
        if (auth.Kind == AuthKind.Bearer) { HeaderBag.Set(headers, "Authorization", "Bearer " + value.Text); return Rendered.Done(""); }
        HeaderBag.Set(headers, auth.Header, value.Text); return Rendered.Done("");
    }
}
internal static class UrlJoin
{
    internal static string Combine(string baseUrl, string path)
    {
        if (baseUrl.EndsWith('/')) { return baseUrl + path; }
        return baseUrl + "/" + path;
    }
}
