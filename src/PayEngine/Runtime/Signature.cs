using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using PayEngine.Model;
namespace PayEngine.Runtime;
internal sealed class Signed
{
    internal bool Ok { get; init; }
    internal string Code { get; init; } = "";
    internal string Message { get; init; } = "";
    internal string Body { get; init; } = "";
    internal string HeaderName { get; init; } = "";
    internal string HeaderValue { get; init; } = "";
    internal static Signed Fail(string code, string message)
    {
        return new Signed { Ok = false, Code = code, Message = message };
    }
    internal static Signed Ready(string body, string headerName, string headerValue)
    {
        return new Signed
        {
            Ok = true,
            Body = body,
            HeaderName = headerName,
            HeaderValue = headerValue,
            Code = "",
            Message = "",
        };
    }
}
internal static class SignatureBuilder
{
    internal static Signed Apply(SignSpec? sign, Composed composed, RenderScope scope)
    {
        if (sign is null || sign.Algorithm == DigestAlgorithm.None) { return Signed.Ready(composed.Body, "", ""); }
        var key = TemplateEngine.Render(sign.Key, scope);
        if (!key.Ok) { return Signed.Fail(key.Code, key.Message); }
        if (sign.BodyField.Length > 0) { FieldNode.RemoveTop(composed.Root, sign.BodyField); }
        var raw = composed.Format == BodyFormat.Json ? JsonBody.Write(composed.Root) : FormCodec.Encode(composed.Root);
        var material = BaseMaterial.Compose(sign.Base, raw, composed.Root, sign.BodyField, scope);
        if (!material.Ok) { return Signed.Fail(material.Code, material.Message); }
        var digest = Digests.Compute(sign.Algorithm, sign.Encoding, material.Text, key.Text); var body = raw;
        if (sign.BodyField.Length > 0) { FieldNode.AddTop(composed.Root, sign.BodyField, digest, ValueKind.Text); body = composed.Format == BodyFormat.Json ? JsonBody.Write(composed.Root) : FormCodec.Encode(composed.Root); }
        return Signed.Ready(body, sign.Header, digest);
    }
    internal static bool MatchesHeader(
        SignSpec? sign,
        string rawBody,
        FieldNode tree,
        IReadOnlyDictionary<string, string>? headers,
        ProviderPlan plan,
        IReadOnlyDictionary<string, string> settings)
    {
        if (sign is null || sign.Algorithm == DigestAlgorithm.None || !sign.Required) { return true; }
        if (sign.Header.Length == 0 || !HeaderBag.TryGet(headers, sign.Header, out var presented)) { return false; }
        presented = presented.Trim();
        if (!sign.Key.UsesAccount()) { return OneAccount(sign, rawBody, tree, presented, new AccountValues(new AccountSpec(), settings), settings); }
        var matched = false;
        foreach (var account in plan.Accounts)
        {
            if (OneAccount(sign, rawBody, tree, presented, new AccountValues(account, settings), settings)) { matched = true; break; }
        }
        return matched;
    }
    private static bool OneAccount(
        SignSpec sign,
        string rawBody,
        FieldNode tree,
        string presented,
        AccountValues values,
        IReadOnlyDictionary<string, string> settings)
    {
        var scope = new RenderScope
        {
            Values = values,
            Settings = settings,
        };
        var key = TemplateEngine.Render(sign.Key, scope);
        if (!key.Ok) { return false; }
        var material = BaseMaterial.Compose(sign.Base, rawBody, tree, sign.BodyField, scope);
        if (!material.Ok) { return false; }
        var digest = Digests.Compute(sign.Algorithm, sign.Encoding, material.Text, key.Text); return Same(digest, presented);
    }
    private static bool Same(string expected, string presented)
    {
        var left = Encoding.UTF8.GetBytes(expected); var right = Encoding.UTF8.GetBytes(presented);
        if (left.Length != right.Length) { return false; }
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}
internal static class BaseMaterial
{
    internal static Rendered Compose(Template template, string rawBody, FieldNode root, string bodyField, RenderScope scope)
    {
        var builder = new StringBuilder();
        foreach (var piece in template.Pieces)
        {
            switch (piece.Kind)
            {
                case TokenKind.Literal: builder.Append(piece.Text ?? ""); break; case TokenKind.BodyRaw: builder.Append(rawBody); break;
                case TokenKind.BodyKv: AppendKv(builder, root, bodyField); break; case TokenKind.BodyQuery: builder.Append(BuildQuery(root, bodyField)); break;
                case TokenKind.AccountField:
                case TokenKind.Secret:
                    var rendered = TemplateEngine.Render(new Template { Pieces = [piece] }, scope);
                    if (!rendered.Ok) { return rendered; }
                    builder.Append(rendered.Text); break;
                default:
                    return Rendered.Fail("Template.Unknown", "signing base");
            }
        }
        return Rendered.Done(builder.ToString());
    }
    private static void AppendKv(StringBuilder builder, FieldNode root, string bodyField)
    {
        if (root.Children is null) { return; }
        foreach (var child in root.Children) { AppendKvNode(builder, child, nested: false, prefix: "", bodyField); }
    }
    private static void AppendKvNode(StringBuilder builder, FieldNode node, bool nested, string prefix, string bodyField)
    {
        if (SkipKv(node.Name, bodyField)) { return; }
        if (!node.IsContainer)
        {
            if (nested) { builder.Append(prefix); }
            builder.Append(node.Name); builder.Append(node.Scalar ?? ""); return;
        }
        var next = nested ? prefix + node.Name + "." : "result.";
        foreach (var child in node.Children ?? []) { AppendKvNode(builder, child, true, next, bodyField); }
    }
    private static bool SkipKv(string name, string bodyField)
    {
        if (name.Equals("signature", StringComparison.Ordinal)) { return true; }
        return bodyField.Length > 0 && name.Equals(bodyField, StringComparison.Ordinal);
    }
    private static string BuildQuery(FieldNode root, string bodyField)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        if (root.Children is not null)
        {
            foreach (var child in root.Children) { Collect(child, "", pairs, bodyField); }
        }
        pairs.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key)); var builder = new StringBuilder();
        for (var i = 0; i < pairs.Count; i++)
        {
            if (i > 0) { builder.Append('&'); }
            builder.Append(pairs[i].Key); builder.Append('='); builder.Append(pairs[i].Value);
        }
        return builder.ToString();
    }
    private static void Collect(FieldNode node, string prefix, List<KeyValuePair<string, string>> pairs, string bodyField)
    {
        if (SkipQuery(node.Name, bodyField)) { return; }
        var key = prefix.Length == 0 ? node.Name : prefix + "." + node.Name;
        if (!node.IsContainer)
        {
            var text = node.Scalar ?? "";
            if (text.Length == 0) { return; }
            pairs.Add(new KeyValuePair<string, string>(key, text)); return;
        }
        foreach (var child in node.Children ?? []) { Collect(child, key, pairs, bodyField); }
    }
    private static bool SkipQuery(string name, string bodyField)
    {
        if (name.Equals("sign", StringComparison.Ordinal)) { return true; }
        return bodyField.Length > 0 && name.Equals(bodyField, StringComparison.Ordinal);
    }
}
internal static class Digests
{
    internal static string Compute(DigestAlgorithm algorithm, DigestEncoding encoding, string material, string key)
    {
        var data = Encoding.UTF8.GetBytes(material); var keyBytes = Encoding.UTF8.GetBytes(key);
        var hash = algorithm switch
        {
            DigestAlgorithm.HmacSha256 => HMACSHA256.HashData(keyBytes, data),
            DigestAlgorithm.HmacSha512 => HMACSHA512.HashData(keyBytes, data),
            DigestAlgorithm.Sha256 => SHA256.HashData(Join(data, keyBytes)),
            DigestAlgorithm.Md5 => Md5(Join(data, keyBytes)),
            _ => [],
        };
        return Encode(hash, encoding);
    }
    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "Md5 is a digest the signing schema names, and it is used only when a definition selects that algorithm.")]
    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "Md5 is a digest the signing schema names, and it is used only when a definition selects that algorithm.")]
    private static byte[] Md5(byte[] data)
    {
        return MD5.HashData(data);
    }
    private static byte[] Join(byte[] left, byte[] right)
    {
        var both = new byte[left.Length + right.Length]; Buffer.BlockCopy(left, 0, both, 0, left.Length); Buffer.BlockCopy(right, 0, both, left.Length, right.Length); return both;
    }
    private static string Encode(byte[] digest, DigestEncoding encoding)
    {
        return encoding switch
        {
            DigestEncoding.HexUpper => ToHex(digest, upper: true),
            DigestEncoding.Base64 => Convert.ToBase64String(digest),
            _ => ToHex(digest, upper: false),
        };
    }
    private static string ToHex(byte[] data, bool upper)
    {
        const string lower = "0123456789abcdef"; const string upperAlphabet = "0123456789ABCDEF"; var alphabet = upper ? upperAlphabet : lower; var chars = new char[data.Length * 2];
        for (var i = 0; i < data.Length; i++) { chars[i * 2] = alphabet[data[i] >> 4]; chars[(i * 2) + 1] = alphabet[data[i] & 0x0F]; }
        return new string(chars);
    }
}
