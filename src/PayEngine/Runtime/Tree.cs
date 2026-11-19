using System.Globalization;
using System.Text;
using System.Text.Json;
using PayEngine.Model;
namespace PayEngine.Runtime;
internal sealed class FieldNode
{
    internal FieldNode(string name)
    {
        this.Name = name;
    }
    internal string Name { get; }
    internal List<FieldNode>? Children { get; set; }
    internal string? Scalar { get; set; }
    internal ValueKind Kind { get; set; }
    internal bool IsContainer => this.Children is not null;
    internal FieldNode? Find(IReadOnlyList<string> segments)
    {
        var current = this;
        foreach (var segment in segments)
        {
            if (current.Children is null) { return null; }
            FieldNode? next = null;
            foreach (var child in current.Children)
            {
                if (child.Name.Equals(segment, StringComparison.OrdinalIgnoreCase)) { next = child; break; }
            }
            if (next is null) { return null; }
            current = next;
        }
        return current;
    }
    internal static void RemoveTop(FieldNode root, string name)
    {
        if (root.Children is null || name.Length == 0) { return; }
        for (var i = root.Children.Count - 1; i >= 0; i--)
        {
            if (root.Children[i].Name.Equals(name, StringComparison.Ordinal)) { root.Children.RemoveAt(i); }
        }
    }
    internal static void AddTop(FieldNode root, string name, string scalar, ValueKind kind)
    {
        root.Children ??= [];
        root.Children.Add(new FieldNode(name)
        {
            Scalar = scalar,
            Kind = kind,
        });
    }
    internal static bool Insert(FieldNode root, string key, string scalar, ValueKind kind)
    {
        var parts = key.Split('.'); var current = root; current.Children ??= [];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) { return false; }
            FieldNode? child = null;
            if (current.Children is null) { return false; }
            foreach (var existing in current.Children)
            {
                if (existing.Name.Equals(parts[i], StringComparison.Ordinal)) { child = existing; break; }
            }
            var last = i == parts.Length - 1;
            if (child is null)
            {
                child = new FieldNode(parts[i]); current.Children.Add(child);
                if (last) { child.Scalar = scalar; child.Kind = kind; }
                else { child.Children = []; }
                current = child; continue;
            }
            if (last || !child.IsContainer) { return false; }
            current = child;
        }
        return true;
    }
}
internal static class JsonTree
{
    internal static FieldNode FromElement(JsonElement element)
    {
        var root = new FieldNode(""); Fill(root, element); return root;
    }
    private static void Fill(FieldNode node, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                node.Children = [];
                foreach (var property in element.EnumerateObject()) { var child = new FieldNode(property.Name); node.Children.Add(child); Fill(child, property.Value); }
                break;
            case JsonValueKind.Array:
                node.Children = []; var index = 0;
                foreach (var item in element.EnumerateArray()) { var child = new FieldNode(index.ToString(CultureInfo.InvariantCulture)); node.Children.Add(child); Fill(child, item); index++; }
                break;
            case JsonValueKind.String:
                node.Scalar = element.GetString() ?? ""; node.Kind = ValueKind.Text; break;
            case JsonValueKind.Number:
                node.Scalar = element.GetRawText(); node.Kind = ValueKind.Number; break;
            case JsonValueKind.True:
                node.Scalar = "true"; node.Kind = ValueKind.Boolean; break;
            case JsonValueKind.False:
                node.Scalar = "false"; node.Kind = ValueKind.Boolean; break;
            default:
                node.Scalar = ""; node.Kind = ValueKind.Text; break;
        }
    }
}
internal static class JsonBody
{
    internal static string Write(FieldNode node)
    {
        var builder = new StringBuilder(); WriteNode(builder, node); return builder.ToString();
    }
    private static void WriteNode(StringBuilder builder, FieldNode node)
    {
        if (!node.IsContainer) { WriteScalar(builder, node); return; }
        builder.Append('{');
        var children = node.Children ?? [];
        for (var i = 0; i < children.Count; i++)
        {
            if (i > 0) { builder.Append(','); }
            var child = children[i]; AppendQuoted(builder, child.Name); builder.Append(':'); WriteNode(builder, child);
        }
        builder.Append('}');
    }
    private static void WriteScalar(StringBuilder builder, FieldNode node)
    {
        if (node.Kind is ValueKind.Number or ValueKind.Boolean) { builder.Append(node.Scalar ?? "null"); return; }
        AppendQuoted(builder, node.Scalar ?? "");
    }
    private static void AppendQuoted(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': builder.Append("\\\""); break; case '\\': builder.Append("\\\\"); break; case '\b': builder.Append("\\b"); break; case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break; case '\r': builder.Append("\\r"); break; case '\t': builder.Append("\\t"); break;
                default:
                    if (ch < ' ') { builder.Append("\\u"); builder.Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture)); }
                    else { builder.Append(ch); }
                    break;
            }
        }
        builder.Append('"');
    }
}
internal static class FormCodec
{
    internal static string Encode(FieldNode root)
    {
        var builder = new StringBuilder(); var children = root.Children ?? []; var first = true;
        foreach (var child in children)
        {
            if (child.IsContainer) { continue; }
            if (!first) { builder.Append('&'); }
            first = false; builder.Append(Escape(child.Name)); builder.Append('='); builder.Append(Escape(child.Scalar ?? ""));
        }
        return builder.ToString();
    }
    internal static FieldNode Parse(string body)
    {
        var root = new FieldNode("")
        {
            Children = [],
        };
        if (body.Length == 0) { return root; }
        var parts = body.Split('&');
        foreach (var part in parts)
        {
            if (part.Length == 0) { continue; }
            var eq = part.IndexOf('=', StringComparison.Ordinal); var rawKey = eq < 0 ? part : part[..eq]; var rawValue = eq < 0 ? "" : part[(eq + 1)..]; var key = Unescape(rawKey);
            if (key.Length == 0) { continue; }
            root.Children.Add(new FieldNode(key)
            {
                Scalar = Unescape(rawValue),
                Kind = ValueKind.Text,
            });
        }
        return root;
    }
    private static string Escape(string value)
    {
        return Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);
    }
    private static string Unescape(string value)
    {
        return Uri.UnescapeDataString(value.Replace('+', ' '));
    }
}
