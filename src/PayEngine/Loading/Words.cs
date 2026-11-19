using PayEngine.Contracts;
using PayEngine.Model;

namespace PayEngine.Loading;

internal static class Words
{
    internal const PieceSet PaymentValue = PieceSet.Payment | PieceSet.Account;

    private static HashSet<string> Set(string csv)
    {
        return new HashSet<string>(csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
    }

    internal static readonly HashSet<string> RootKeys = Set("Providers");
    internal static readonly HashSet<string> SectionKeys = Set("Code, DisplayName, Firms, Capabilities, BaseUrl, TimeoutSeconds, Notes, Accounts, Auth, Signing, Payin, Payout, PaymentMethods, Masking, Enabled");
    internal static readonly HashSet<string> MaskingKeys = Set("Partial, Full, Wrapped");
    internal static readonly HashSet<string> DirectionKeys = Set("Create, Response, Callback"); internal static readonly HashSet<string> CreateKeys = Set("Path, Verb, ContentType, Headers, Body");
    internal static readonly HashSet<string> ResponseKeys = Set("PaymentId, Redirect, ErrorCode, ErrorMessage, Outcome, Requisites");
    internal static readonly HashSet<string> OutcomeKeys = Set("When, Result, Code, Message"); internal static readonly HashSet<string> RequisiteKeys = Set("When, Type, Value");
    internal static readonly HashSet<string> CallbackKeys = Set("Format, AttemptId, ProviderPaymentId, Status, StatusMap, Amount, AmountUnit");
    internal static readonly HashSet<string> SigningKeys = Set("Request, PayinCallback, PayoutCallback");
    internal static readonly HashSet<string> SignKeys = Set("Algorithm, Base, Encoding, Key, Header, BodyField, Required");
    internal static readonly HashSet<string> MethodKeys = Set("ProviderMethod, Currency, DetailsType, Account, MinAmount, MaxAmount, AmountStep");
    internal static readonly HashSet<string> CapabilityNames = Set("PayinExecute, PayinCallback, PayoutExecute, PayoutCallback");
    internal static readonly HashSet<string> DetailTypes = Set("Card, Phone, BankAccount, Qr");
    internal static readonly Dictionary<string, AuthKind> AuthKinds = new(StringComparer.Ordinal) { ["None"] = AuthKind.None, ["Bearer"] = AuthKind.Bearer, ["Header"] = AuthKind.Header };
    internal static readonly Dictionary<string, DigestAlgorithm> Algorithms = new(StringComparer.Ordinal) { ["None"] = DigestAlgorithm.None, ["HmacSha256"] = DigestAlgorithm.HmacSha256, ["HmacSha512"] = DigestAlgorithm.HmacSha512, ["Md5"] = DigestAlgorithm.Md5, ["Sha256"] = DigestAlgorithm.Sha256 };
    internal static readonly Dictionary<string, DigestEncoding> Encodings = new(StringComparer.Ordinal) { ["HexLower"] = DigestEncoding.HexLower, ["HexUpper"] = DigestEncoding.HexUpper, ["Base64"] = DigestEncoding.Base64 };
    internal static readonly Dictionary<string, BodyFormat> BodyFormats = new(StringComparer.Ordinal) { ["Json"] = BodyFormat.Json, ["Form"] = BodyFormat.Form };
    internal static readonly Dictionary<string, string> Verbs = new(StringComparer.Ordinal) { ["GET"] = "GET", ["POST"] = "POST", ["PUT"] = "PUT", ["PATCH"] = "PATCH", ["DELETE"] = "DELETE" };
    internal static readonly Dictionary<string, PayResultKind> Results = new(StringComparer.Ordinal) { ["Accepted"] = PayResultKind.Accepted, ["Declined"] = PayResultKind.Declined, ["Error"] = PayResultKind.Error };
    internal static readonly Dictionary<string, CallbackStatus> Statuses = new(StringComparer.Ordinal) { ["PaymentSucceeded"] = CallbackStatus.PaymentSucceeded, ["PaymentFailed"] = CallbackStatus.PaymentFailed, ["PaymentExpired"] = CallbackStatus.PaymentExpired, ["NoAction"] = CallbackStatus.NoAction };
    internal static readonly Dictionary<string, AmountScale> Scales = new(StringComparer.Ordinal) { ["Major"] = AmountScale.Major, ["Minor"] = AmountScale.Minor };
}
