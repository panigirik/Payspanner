using System.Globalization;
using System.Text.Json;
using PayEngine.Contracts;
using PayEngine.Model;
namespace PayEngine.Runtime;
internal static class PayResults
{
    internal static PayResult Declined(string code, string message)
    {
        return new PayResult(PayResultKind.Declined, code, message, "", "", "", "");
    }
    internal static PayResult Error(string code, string message)
    {
        return new PayResult(PayResultKind.Error, code, message, "", "", "", "");
    }
}
internal static class MethodMatcher
{
    internal static bool TrySelect(
        ProviderPlan plan,
        PayRequest request,
        IReadOnlyDictionary<string, string> settings,
        out MethodSpec method,
        out PayResult rejection)
    {
        var requested = (request.Firm ?? "").Trim();
        if (requested.Length == 0 && settings.TryGetValue(plan.Section + ":Firm", out var configured) && configured is not null) { requested = configured.Trim(); }
        if (!TryFirm(plan, requested, out var firm)) { method = null!; rejection = PayResults.Declined("Method.Rejected", plan.Code + " / " + plan.DisplayName + " rejected the firm."); return false; }
        var table = Table(plan, firm); var code = (request.MethodCode ?? "").Trim();
        if (!table.TryGetValue(code, out var found) || found is null) { method = null!; rejection = PayResults.Declined("Method.Rejected", plan.Code + " / " + plan.DisplayName + " rejected the method."); return false; }
        method = found;
        if (method.DetailsType.Length == 0) { rejection = PayResults.Declined("Method.Rejected", plan.Code + " / " + plan.DisplayName + " rejected the method."); return false; }
        var currency = (request.Currency ?? "").Trim();
        if (!currency.Equals(method.Currency, StringComparison.Ordinal)) { rejection = PayResults.Declined("Method.Rejected", plan.Code + " / " + plan.DisplayName + " rejected the currency."); return false; }
        if (method.MinAmount > 0m && request.Sum < method.MinAmount) { rejection = PayResults.Declined("Method.Rejected", plan.Code + " / " + plan.DisplayName + " rejected the sum."); return false; }
        if (method.MaxAmount > 0m && request.Sum > method.MaxAmount) { rejection = PayResults.Declined("Method.Rejected", plan.Code + " / " + plan.DisplayName + " rejected the sum."); return false; }
        if (method.AmountStep > 0m && request.Sum % method.AmountStep != 0m) { rejection = PayResults.Declined("Method.Rejected", plan.Code + " / " + plan.DisplayName + " rejected the sum step."); return false; }
        rejection = null!; return true;
    }
    private static bool TryFirm(ProviderPlan plan, string requested, out string firm)
    {
        firm = "";
        if (plan.Firms.Length == 1)
        {
            if (requested.Length > 0 && !requested.Equals(plan.Firms[0], StringComparison.Ordinal)) { return false; }
            firm = plan.Firms[0]; return true;
        }
        if (requested.Length == 0) { return false; }
        foreach (var name in plan.Firms)
        {
            if (name.Equals(requested, StringComparison.Ordinal)) { firm = name; return true; }
        }
        return false;
    }
    private static Dictionary<string, MethodSpec> Table(ProviderPlan plan, string firm)
    {
        var table = new Dictionary<string, MethodSpec>(StringComparer.Ordinal);
        foreach (var method in plan.CommonMethods) { table[method.Code] = method; }
        if (!plan.FlatMethods && plan.FirmMethods.TryGetValue(firm, out var slice))
        {
            foreach (var method in slice) { table[method.Code] = method; }
        }
        return table;
    }
}
internal static class AccountBinder
{
    internal static AccountSpec? Find(ProviderPlan plan, MethodSpec method)
    {
        if (method.Account.Length > 0)
        {
            foreach (var account in plan.Accounts)
            {
                if (account.Name.Equals(method.Account, StringComparison.Ordinal)) { return account; }
            }
            return null;
        }
        if (plan.Accounts.Length == 1) { return plan.Accounts[0]; }
        return null;
    }
}
internal static class WhenMatch
{
    internal static bool Matches(WhenExpression expression, int status, FieldNode root)
    {
        if (expression.Clauses.Length == 0) { return false; }
        foreach (var clause in expression.Clauses)
        {
            if (!MatchClause(clause, status, root)) { return false; }
        }
        return true;
    }
    private static bool MatchClause(WhenClause clause, int status, FieldNode root)
    {
        if (clause is HttpClause http)
        {
            if (http.SuccessClass) { return status >= 200 && status <= 299; }
            return status == http.Exact;
        }
        if (clause is not FieldClause field) { return false; }
        var node = root.Find(field.Segments);
        switch (field.Op)
        {
            case FieldOp.Exists:
                return node is not null;
            case FieldOp.Missing:
                return node is null;
            case FieldOp.Equals:
                return Scalar(node).Equals(field.Expected.Trim(), StringComparison.Ordinal);
            case FieldOp.OneOf:
                var actual = Scalar(node);
                foreach (var option in field.Options)
                {
                    if (actual.Equals(option.Trim(), StringComparison.Ordinal)) { return true; }
                }
                return false;
            default:
                return false;
        }
    }
    private static string Scalar(FieldNode? node)
    {
        if (node is null || node.IsContainer) { return ""; }
        return (node.Scalar ?? "").Trim();
    }
}
internal static class OutcomeReader
{
    internal static PayResult Read(OutboundReply reply, ResponseSpec spec)
    {
        var root = Parse(reply.Body ?? ""); var requisite = ReadRequisite(spec, reply.Status, root); var redirect = ReadPath(root, spec.Redirect);
        var paymentId = ReadPath(root, spec.PaymentId); var errorMessage = ReadPath(root, spec.ErrorMessage); var errorCode = ReadPath(root, spec.ErrorCode);
        foreach (var rule in spec.Outcomes)
        {
            if (!WhenMatch.Matches(rule.When, reply.Status, root)) { continue; }
            var message = rule.Message;
            if (message.Length == 0) { message = errorMessage; }
            if (message.Length == 0) { message = errorCode; }
            if (rule.Kind != PayResultKind.Accepted)
            {
                return new PayResult(rule.Kind, rule.Code, message, paymentId, "", "", "");
            }

            return new PayResult(rule.Kind, rule.Code, message, paymentId, requisite.Type, requisite.Value, redirect);
        }
        if (requisite.Value.Length > 0 || redirect.Length > 0) { return new PayResult(PayResultKind.Accepted, "", "", paymentId, requisite.Type, requisite.Value, redirect); }
        return new PayResult(PayResultKind.Error, "Api.NoActionInResponse", "No outcome rule matched.", paymentId, "", "", "");
    }
    private static (string Type, string Value) ReadRequisite(ResponseSpec spec, int status, FieldNode root)
    {
        foreach (var rule in spec.Requisites)
        {
            if (!WhenMatch.Matches(rule.When, status, root)) { continue; }
            var text = ReadPath(root, rule.Value);
            if (text.Length == 0) { continue; }
            return (rule.Type, text);
        }
        return ("", "");
    }
    internal static string ReadPath(FieldNode root, SelectorPath? path)
    {
        if (path is null) { return ""; }
        var node = root.Find(path.Segments);
        if (node is null || node.IsContainer) { return ""; }
        return (node.Scalar ?? "").Trim();
    }
    internal static FieldNode Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) { return new FieldNode(""); }
        try { using var document = JsonDocument.Parse(body); return JsonTree.FromElement(document.RootElement); }
        catch (JsonException)
        {
            return new FieldNode("");
        }
    }
    internal static decimal? ReadAmount(FieldNode root, SelectorPath? path, AmountScale scale)
    {
        var text = ReadPath(root, path);
        if (text.Length == 0) { return null; }
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)) { return null; }
        if (scale == AmountScale.Minor) { return amount / 100m; }
        return amount;
    }
}
