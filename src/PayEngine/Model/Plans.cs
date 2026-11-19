using PayEngine.Contracts;

namespace PayEngine.Model;

internal enum BodyFormat { Json, Form }
internal enum DigestAlgorithm { None, HmacSha256, HmacSha512, Md5, Sha256 }
internal enum DigestEncoding { HexLower, HexUpper, Base64 }
internal enum AuthKind { None, Bearer, Header }
internal enum AmountScale { Major, Minor }
internal enum ValueKind { Text, Number, Boolean }
internal enum TokenKind { Literal, AttemptId, SumMajor, SumMinor, Currency, CallbackUrl, ProviderMethod, AccountField, UnixSeconds, ClientPhone, Secret, BodyRaw, BodyKv, BodyQuery }
internal enum FieldOp { Exists, Missing, Equals, OneOf }

[Flags]
internal enum PieceSet { None = 0, Payment = 1, Account = 2, Body = 4 }

internal readonly struct TemplatePiece
{
    internal TokenKind Kind { get; init; }
    internal string Text { get; init; }
    internal string Extra { get; init; }
}

internal sealed class Template
{
    internal TemplatePiece[] Pieces { get; init; } = [];

    internal void Collect(HashSet<string> fields, ref bool clock)
    {
        foreach (var piece in this.Pieces)
        {
            if (piece.Kind == TokenKind.AccountField) { fields.Add(piece.Text ?? ""); }
            else if (piece.Kind == TokenKind.UnixSeconds) { clock = true; }
        }
    }

    internal bool UsesAccount()
    {
        foreach (var piece in this.Pieces)
        {
            if (piece.Kind == TokenKind.AccountField) { return true; }
        }

        return false;
    }
}

internal abstract class WhenClause { }
internal sealed class HttpClause : WhenClause { internal bool SuccessClass { get; init; } internal int Exact { get; init; } }
internal sealed class FieldClause : WhenClause { internal string[] Segments { get; init; } = []; internal FieldOp Op { get; init; } internal string Expected { get; init; } = ""; internal string[] Options { get; init; } = []; }
internal sealed class WhenExpression { internal WhenClause[] Clauses { get; init; } = []; }
internal sealed class SelectorPath { internal string[] Segments { get; init; } = []; }
internal sealed class BodyFieldSpec { internal string Key { get; init; } = ""; internal ValueKind Kind { get; init; } internal Template Template { get; init; } = new(); internal string SourcePath { get; init; } = ""; }
internal sealed class HeaderSpec { internal string Name { get; init; } = ""; internal Template Value { get; init; } = new(); }
internal sealed class CreateSpec { internal string Path { get; init; } = ""; internal string Verb { get; init; } = ""; internal BodyFormat Format { get; init; } internal string MediaType { get; init; } = ""; internal HeaderSpec[] Headers { get; init; } = []; internal BodyFieldSpec[] Fields { get; init; } = []; internal bool NeedsClock { get; set; } }
internal sealed class OutcomeRule { internal WhenExpression When { get; init; } = new(); internal PayResultKind Kind { get; init; } internal string Code { get; init; } = ""; internal string Message { get; init; } = ""; }
internal sealed class RequisiteRule { internal WhenExpression When { get; init; } = new(); internal string Type { get; init; } = ""; internal SelectorPath Value { get; init; } = new(); }
internal sealed class ResponseSpec { internal SelectorPath? PaymentId { get; init; } internal SelectorPath? Redirect { get; init; } internal SelectorPath? ErrorCode { get; init; } internal SelectorPath? ErrorMessage { get; init; } internal OutcomeRule[] Outcomes { get; init; } = []; internal RequisiteRule[] Requisites { get; init; } = []; }
internal sealed class SignSpec { internal DigestAlgorithm Algorithm { get; init; } internal Template Base { get; init; } = new(); internal DigestEncoding Encoding { get; init; } internal Template Key { get; init; } = new(); internal string Header { get; init; } = ""; internal string BodyField { get; init; } = ""; internal bool Required { get; init; } = true; }
internal sealed class AuthSpec { internal AuthKind Kind { get; init; } internal string Header { get; init; } = ""; internal Template Value { get; init; } = new(); }
internal sealed class AccountFieldSpec { internal string Name { get; init; } = ""; internal bool IsSecret { get; init; } internal string Literal { get; init; } = ""; internal string SecretSection { get; init; } = ""; internal string SecretField { get; init; } = ""; }
internal sealed class AccountSpec { internal string Name { get; init; } = ""; internal AccountFieldSpec[] Fields { get; init; } = []; }
internal sealed class MethodSpec { internal string Code { get; init; } = ""; internal string ProviderMethod { get; init; } = ""; internal string Currency { get; init; } = ""; internal string DetailsType { get; init; } = ""; internal string Account { get; init; } = ""; internal decimal MinAmount { get; init; } internal decimal MaxAmount { get; init; } internal decimal AmountStep { get; init; } internal string SourcePath { get; init; } = ""; }
internal sealed class CallbackSpec { internal BodyFormat Format { get; init; } internal SelectorPath AttemptId { get; init; } = new(); internal SelectorPath? ProviderPaymentId { get; init; } internal SelectorPath Status { get; init; } = new(); internal SelectorPath? Amount { get; init; } internal AmountScale Scale { get; init; } internal Dictionary<string, CallbackStatus> StatusMap { get; init; } = new(StringComparer.Ordinal); }

internal sealed class ProviderPlan
{
    internal string Section { get; set; } = "";
    internal string Code { get; set; } = "";
    internal string DisplayName { get; set; } = "";
    internal string[] Firms { get; set; } = [];
    internal bool FirmsBroken { get; set; }
    internal string BaseUrl { get; set; } = "";
    internal int TimeoutSeconds { get; set; } = 30;
    internal bool PayinExecute { get; set; }
    internal bool PayinCallback { get; set; }
    internal bool PayoutExecute { get; set; }
    internal bool PayoutCallback { get; set; }
    internal AccountSpec[] Accounts { get; set; } = [];
    internal AuthSpec Auth { get; set; } = new();
    internal SignSpec? RequestSign { get; set; }
    internal SignSpec? PayinCallbackSign { get; set; }
    internal SignSpec? PayoutCallbackSign { get; set; }
    internal CreateSpec? PayinCreate { get; set; }
    internal ResponseSpec? PayinResponse { get; set; }
    internal CallbackSpec? PayinCallbackSpec { get; set; }
    internal CreateSpec? PayoutCreate { get; set; }
    internal ResponseSpec? PayoutResponse { get; set; }
    internal CallbackSpec? PayoutCallbackSpec { get; set; }
    internal bool FlatMethods { get; set; } = true;
    internal MethodSpec[] CommonMethods { get; set; } = [];
    internal Dictionary<string, MethodSpec[]> FirmMethods { get; set; } = new(StringComparer.Ordinal);
}
