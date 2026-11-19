using System.Globalization;
using PayEngine.Model;
namespace PayEngine.Loading;
internal static class SecretSyntax
{
    internal static bool TryWhole(string text, out string section, out string field)
    {
        section = ""; field = ""; const string prefix = "$secret:";
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) { return false; }
        var rest = text[prefix.Length..]; var colon = rest.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon != rest.LastIndexOf(':') || colon >= rest.Length - 1) { return false; }
        section = rest[..colon]; field = rest[(colon + 1)..];
        if (section.Contains(' ', StringComparison.Ordinal) || field.Contains(' ', StringComparison.Ordinal) || section.Contains(':', StringComparison.Ordinal) || field.Contains(':', StringComparison.Ordinal)) { return false; }
        return section.Length > 0 && field.Length > 0;
    }
}
internal static class TemplateGrammar
{
    internal static Template Parse(string text, string path, PieceSet allowed, string owner, List<string> problems)
    {
        if (text.StartsWith("$secret:", StringComparison.Ordinal)) { return ParseSecret(text, path, allowed, owner, problems); }
        var pieces = new List<TemplatePiece>(); var literal = new System.Text.StringBuilder(); var ok = true;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch != '{')
            {
                if (ch == '}')
                {
                    problems.Add(path + ": extra '}'.");
                    ok = false; continue;
                }
                literal.Append(ch); continue;
            }
            var close = text.IndexOf('}', i + 1);
            if (close < 0) { problems.Add(path + ": placeholder is not closed."); ok = false; break; }
            var nested = text.IndexOf('{', i + 1);
            if (nested >= 0 && nested < close) { problems.Add(path + ": placeholder is nested."); ok = false; break; }
            var name = text[(i + 1)..close];
            if (name.Length == 0) { problems.Add(path + ": placeholder is empty."); ok = false; }
            else if (!TryPiece(name, allowed, path, problems, out var piece)) { ok = false; }
            else { Flush(literal, pieces); pieces.Add(piece); }
            i = close;
        }
        Flush(literal, pieces);
        if (!ok) { return new Template(); }
        return new Template { Pieces = pieces.ToArray() };
    }
    private static Template ParseSecret(string text, string path, PieceSet allowed, string owner, List<string> problems)
    {
        if ((allowed & PieceSet.Account) == 0) { problems.Add(path + ": unknown placeholder '" + text + "'."); return new Template(); }
        if (!SecretSyntax.TryWhole(text, out var section, out var field)) { problems.Add(path + ": secret reference must be $secret:<section>:<field>."); return new Template(); }
        if (!string.Equals(section, owner, StringComparison.Ordinal)) { problems.Add(path + ": secret section '" + section + "' does not match '" + owner + "'."); return new Template(); }
        return new Template
        {
            Pieces =
            [
                new TemplatePiece
                {
                    Kind = TokenKind.Secret,
                    Text = section,
                    Extra = field,
                },
            ],
        };
    }
    private static void Flush(System.Text.StringBuilder literal, List<TemplatePiece> pieces)
    {
        if (literal.Length == 0) { return; }
        pieces.Add(new TemplatePiece
        {
            Kind = TokenKind.Literal,
            Text = literal.ToString(),
            Extra = "",
        });
        literal.Clear();
    }
    private static bool TryPiece(string name, PieceSet allowed, string path, List<string> problems, out TemplatePiece piece)
    {
        piece = default;
        if (name.Equals("attemptId", StringComparison.Ordinal)) { return Allow(TokenKind.AttemptId, PieceSet.Payment, allowed, name, path, problems, "", out piece); }
        if (name.Equals("sum", StringComparison.Ordinal)) { return Allow(TokenKind.SumMajor, PieceSet.Payment, allowed, name, path, problems, "", out piece); }
        if (name.Equals("sum:minor", StringComparison.Ordinal)) { return Allow(TokenKind.SumMinor, PieceSet.Payment, allowed, name, path, problems, "", out piece); }
        if (name.Equals("currency", StringComparison.Ordinal)) { return Allow(TokenKind.Currency, PieceSet.Payment, allowed, name, path, problems, "", out piece); }
        if (name.Equals("callbackUrl", StringComparison.Ordinal)) { return Allow(TokenKind.CallbackUrl, PieceSet.Payment, allowed, name, path, problems, "", out piece); }
        if (name.Equals("method.ProviderMethod", StringComparison.Ordinal)) { return Allow(TokenKind.ProviderMethod, PieceSet.Payment, allowed, name, path, problems, "", out piece); }
        if (name.Equals("now:unix", StringComparison.Ordinal)) { return Allow(TokenKind.UnixSeconds, PieceSet.Payment, allowed, name, path, problems, "", out piece); }
        if (name.Equals("clientPhone", StringComparison.Ordinal)) { return Allow(TokenKind.ClientPhone, PieceSet.Payment, allowed, name, path, problems, "", out piece); }
        if (name.Equals("body", StringComparison.Ordinal)) { return Allow(TokenKind.BodyRaw, PieceSet.Body, allowed, name, path, problems, "", out piece); }
        if (name.Equals("body:kv", StringComparison.Ordinal)) { return Allow(TokenKind.BodyKv, PieceSet.Body, allowed, name, path, problems, "", out piece); }
        if (name.Equals("body:query", StringComparison.Ordinal)) { return Allow(TokenKind.BodyQuery, PieceSet.Body, allowed, name, path, problems, "", out piece); }
        if (name.StartsWith("account.", StringComparison.Ordinal))
        {
            var field = name["account.".Length..];
            if (field.Length == 0 || field.Contains('.', StringComparison.Ordinal) || field.Contains(' ', StringComparison.Ordinal) || field.Contains('{', StringComparison.Ordinal) || field.Contains('}', StringComparison.Ordinal))
            {
                problems.Add(path + ": unknown placeholder '{" + name + "}'.");
                return false;
            }
            return Allow(TokenKind.AccountField, PieceSet.Account, allowed, name, path, problems, field, out piece);
        }
        problems.Add(path + ": unknown placeholder '{" + name + "}'.");
        return false;
    }
    private static bool Allow(
        TokenKind kind,
        PieceSet needed,
        PieceSet allowed,
        string name,
        string path,
        List<string> problems,
        string text,
        out TemplatePiece piece)
    {
        piece = default;
        if ((allowed & needed) == 0)
        {
            problems.Add(path + ": unknown placeholder '{" + name + "}'.");
            return false;
        }
        piece = new TemplatePiece
        {
            Kind = kind,
            Text = text,
            Extra = "",
        };
        return true;
    }
}
internal static class SelectorGrammar
{
    internal static bool TryParse(string text, string path, List<string> problems, out SelectorPath selector)
    {
        selector = new SelectorPath(); var trimmed = text.Trim();
        if (!trimmed.StartsWith("$.", StringComparison.Ordinal)) { problems.Add(path + ": selector must start with '$.'."); return false; }
        var segments = new List<string>(); var index = 2;
        if (index >= trimmed.Length) { problems.Add(path + ": selector is missing a field."); return false; }
        while (index < trimmed.Length)
        {
            if (trimmed[index] == '.') { problems.Add(path + ": selector has an empty segment."); return false; }
            if (trimmed[index] == '[')
            {
                var close = trimmed.IndexOf(']', index + 1);
                if (close < 0) { problems.Add(path + ": selector index is not closed."); return false; }
                var number = trimmed[(index + 1)..close];
                if (!IsDigits(number)) { problems.Add(path + ": selector index must be digits."); return false; }
                segments.Add(number); index = close + 1;
                if (index < trimmed.Length && trimmed[index] == '.')
                {
                    index++;
                    if (index >= trimmed.Length) { problems.Add(path + ": selector has an empty segment."); return false; }
                }
                continue;
            }
            var start = index;
            while (index < trimmed.Length && trimmed[index] != '.' && trimmed[index] != '[')
            {
                if (char.IsWhiteSpace(trimmed[index])) { problems.Add(path + ": selector contains whitespace."); return false; }
                index++;
            }
            if (start == index) { problems.Add(path + ": selector has an empty segment."); return false; }
            segments.Add(trimmed[start..index]);
            if (index < trimmed.Length && trimmed[index] == '.')
            {
                index++;
                if (index >= trimmed.Length) { problems.Add(path + ": selector has an empty segment."); return false; }
            }
        }
        if (segments.Count == 0) { problems.Add(path + ": selector is missing a field."); return false; }
        selector = new SelectorPath { Segments = segments.ToArray() };
        return true;
    }
    internal static bool IsDigits(string text)
    {
        if (text.Length == 0) { return false; }
        foreach (var ch in text)
        {
            if (ch is < '0' or > '9') { return false; }
        }
        return true;
    }
}
internal static class WhenGrammar
{
    internal static WhenExpression Parse(string text, string path, List<string> problems)
    {
        var banned = false; var parts = Split(text, path, problems, ref banned);
        if (banned) { return new WhenExpression(); }
        if (parts.Count == 0) { problems.Add(path + ": When is empty."); return new WhenExpression(); }
        var clauses = new List<WhenClause>(); var ok = true;
        foreach (var part in parts)
        {
            if (!TryClause(part, path, problems, out var clause)) { ok = false; continue; }
            clauses.Add(clause);
        }
        if (!ok) { return new WhenExpression(); }
        return new WhenExpression { Clauses = clauses.ToArray() };
    }
    private static List<string> Split(string text, string path, List<string> problems, ref bool banned)
    {
        var parts = new List<string>(); var depth = 0; var start = 0; var index = 0;
        while (index < text.Length)
        {
            var ch = text[index];
            if (ch == '[') { depth++; index++; continue; }
            if (ch == ']')
            {
                if (depth > 0) { depth--; }
                index++; continue;
            }
            if (depth == 0 && MatchJoin(text, index, "and", out var andLength)) { parts.Add(text[start..index].Trim()); index += andLength; start = index; continue; }
            if (depth == 0 && MatchJoin(text, index, "or", out var orLength)) { problems.Add(path + ": When does not allow 'or'."); banned = true; index += orLength; start = index; continue; }
            index++;
        }
        parts.Add(text[start..].Trim()); parts.RemoveAll(static part => part.Length == 0); return parts;
    }
    private static bool MatchJoin(string text, int index, string word, out int length)
    {
        length = 0;
        if (index >= text.Length || text[index] != ' ') { return false; }
        var mark = index;
        while (mark < text.Length && text[mark] == ' ') { mark++; }
        if (mark + word.Length > text.Length) { return false; }
        if (!text.AsSpan(mark, word.Length).Equals(word.AsSpan(), StringComparison.Ordinal)) { return false; }
        var after = mark + word.Length;
        if (after >= text.Length || text[after] != ' ') { return false; }
        while (after < text.Length && text[after] == ' ') { after++; }
        length = after - index; return true;
    }
    private static bool TryClause(string clause, string path, List<string> problems, out WhenClause clauseNode)
    {
        clauseNode = null!;
        if (clause.StartsWith("http:", StringComparison.Ordinal)) { return TryHttp(clause, path, problems, out clauseNode); }
        if (!clause.StartsWith("$.", StringComparison.Ordinal)) { problems.Add(path + ": When clause '" + clause + "' is not valid."); return false; }
        var space = clause.IndexOf(' ', StringComparison.Ordinal);
        if (space < 0) { problems.Add(path + ": When clause '" + clause + "' is not valid."); return false; }
        var head = clause[..space]; var tail = clause[(space + 1)..].Trim();
        if (!SelectorGrammar.TryParse(head, path, problems, out var selector)) { return false; }
        if (tail.Equals("exists", StringComparison.Ordinal)) { clauseNode = Field(selector, FieldOp.Exists, "", []); return true; }
        if (tail.Equals("missing", StringComparison.Ordinal)) { clauseNode = Field(selector, FieldOp.Missing, "", []); return true; }
        if (tail.StartsWith("==", StringComparison.Ordinal)) { var expected = tail[2..].Trim(); clauseNode = Field(selector, FieldOp.Equals, expected, []); return true; }
        if (IsInOperator(tail))
        {
            if (!TryOptions(tail, path, problems, out var options)) { return false; }
            clauseNode = Field(selector, FieldOp.OneOf, "", options); return true;
        }
        problems.Add(path + ": When clause '" + clause + "' is not valid."); return false;
    }
    private static bool TryHttp(string clause, string path, List<string> problems, out WhenClause clauseNode)
    {
        clauseNode = null!; var code = clause["http:".Length..];
        if (code.Equals("2xx", StringComparison.Ordinal))
        {
            clauseNode = new HttpClause { SuccessClass = true, Exact = 0 };
            return true;
        }
        if (code.Length == 3
            && SelectorGrammar.IsDigits(code)
            && int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var status))
        {
            clauseNode = new HttpClause { SuccessClass = false, Exact = status };
            return true;
        }
        problems.Add(path + ": When clause '" + clause + "' is not valid."); return false;
    }
    private static bool IsInOperator(string tail)
    {
        if (!tail.StartsWith("in", StringComparison.Ordinal)) { return false; }
        if (tail.Length == 2) { return true; }
        return tail[2] == ' ' || tail[2] == '[';
    }
    private static bool TryOptions(string tail, string path, List<string> problems, out string[] options)
    {
        options = []; var open = tail.IndexOf('[', StringComparison.Ordinal); var close = tail.LastIndexOf(']');
        if (open < 0 || close < open) { problems.Add(path + ": When list is not closed."); return false; }
        if (tail[(close + 1)..].Trim().Length > 0) { problems.Add(path + ": When list has trailing text."); return false; }
        var inside = tail[(open + 1)..close]; var raw = inside.Split(','); var list = new List<string>();
        foreach (var item in raw)
        {
            var trimmed = item.Trim();
            if (trimmed.Length == 0) { continue; }
            list.Add(trimmed);
        }
        if (list.Count == 0) { problems.Add(path + ": When list is empty."); return false; }
        options = list.ToArray(); return true;
    }
    private static FieldClause Field(SelectorPath selector, FieldOp op, string expected, string[] options)
    {
        return new FieldClause
        {
            Segments = selector.Segments,
            Op = op,
            Expected = expected,
            Options = options,
        };
    }
}
