using System.Text.Json.Nodes;
using PayEngine.Contracts;
using PayEngine.Model;
namespace PayEngine.Loading;
internal static partial class SectionCompiler
{
    private static ResponseSpec? ReadResponse(JsonNode? node, string path, List<string> problems)
    {
        if (node is not JsonObject obj) { problems.Add(path + ": expected an object."); return null; }
        JsonLocal.RejectUnknown(obj, Words.ResponseKeys, path, problems);
        return new ResponseSpec
        {
            PaymentId = ReadSelector(obj, "PaymentId", path, required: false, problems),
            Redirect = ReadSelector(obj, "Redirect", path, required: false, problems),
            ErrorCode = ReadSelector(obj, "ErrorCode", path, required: false, problems),
            ErrorMessage = ReadSelector(obj, "ErrorMessage", path, required: false, problems),
            Outcomes = ReadOutcomes(obj, path, problems),
            Requisites = ReadRequisites(obj, path, problems),
        };
    }
    private static SelectorPath? ReadSelector(JsonObject obj, string key, string path, bool required, List<string> problems)
    {
        var child = path + ":" + key;
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            if (required) { problems.Add(child + ": expected a selector."); }
            return null;
        }
        if (!JsonLocal.TryString(node, child, problems, out var text)) { return null; }
        if (!SelectorGrammar.TryParse(text, child, problems, out var selector)) { return null; }
        return selector;
    }
    private static OutcomeRule[] ReadOutcomes(JsonObject obj, string path, List<string> problems)
    {
        var child = path + ":Outcome";
        if (!obj.TryGetPropertyValue("Outcome", out var node) || node is null) { return []; }
        if (node is not JsonArray array) { problems.Add(child + ": expected an array."); return []; }
        var list = new List<OutcomeRule>();
        for (var i = 0; i < array.Count; i++)
        {
            var itemPath = child + ":" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (array[i] is not JsonObject item) { problems.Add(itemPath + ": expected an object."); continue; }
            JsonLocal.RejectUnknown(item, Words.OutcomeKeys, itemPath, problems); var whenText = JsonLocal.Required(item, "When", itemPath, problems);
            var when = whenText.Length == 0
                ? new WhenExpression()
                : WhenGrammar.Parse(whenText, itemPath + ":When", problems);
            var resultText = JsonLocal.Required(item, "Result", itemPath, problems);
            if (!Words.Results.TryGetValue(resultText, out var kind))
            {
                if (resultText.Length > 0) { problems.Add(itemPath + ":Result: closed list rejected '" + resultText + "'."); }
                kind = PayResultKind.Error;
            }
            var code = JsonLocal.Optional(item, "Code", itemPath, problems); var message = JsonLocal.Optional(item, "Message", itemPath, problems);
            if (kind != PayResultKind.Accepted && code.Length == 0) { problems.Add(itemPath + ":Code: Declined and Error require Code."); }
            list.Add(new OutcomeRule
            {
                When = when,
                Kind = kind,
                Code = code,
                Message = message,
            });
        }
        return list.ToArray();
    }
    private static RequisiteRule[] ReadRequisites(JsonObject obj, string path, List<string> problems)
    {
        var child = path + ":Requisites";
        if (!obj.TryGetPropertyValue("Requisites", out var node) || node is null) { return []; }
        if (node is not JsonArray array) { problems.Add(child + ": expected an array."); return []; }
        var list = new List<RequisiteRule>();
        for (var i = 0; i < array.Count; i++)
        {
            var itemPath = child + ":" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (array[i] is not JsonObject item) { problems.Add(itemPath + ": expected an object."); continue; }
            JsonLocal.RejectUnknown(item, Words.RequisiteKeys, itemPath, problems); var whenText = JsonLocal.Required(item, "When", itemPath, problems);
            var when = whenText.Length == 0
                ? new WhenExpression()
                : WhenGrammar.Parse(whenText, itemPath + ":When", problems);
            var type = JsonLocal.Required(item, "Type", itemPath, problems);
            if (type.Length > 0 && !Words.DetailTypes.Contains(type)) { problems.Add(itemPath + ":Type: closed list rejected '" + type + "'."); }
            var value = ReadSelector(item, "Value", itemPath, required: true, problems) ?? new SelectorPath();
            list.Add(new RequisiteRule
            {
                When = when,
                Type = type,
                Value = value,
            });
        }
        return list.ToArray();
    }
    private static CallbackSpec? ReadCallback(JsonNode? node, string path, List<string> problems)
    {
        if (node is not JsonObject obj) { problems.Add(path + ": expected an object."); return null; }
        JsonLocal.RejectUnknown(obj, Words.CallbackKeys, path, problems); var formatText = JsonLocal.Required(obj, "Format", path, problems); var format = BodyFormat.Json;
        if (formatText.Length > 0 && !Words.BodyFormats.TryGetValue(formatText, out format)) { problems.Add(path + ":Format: closed list rejected '" + formatText + "'."); }
        var attempt = ReadSelector(obj, "AttemptId", path, required: true, problems) ?? new SelectorPath(); var paymentId = ReadSelector(obj, "ProviderPaymentId", path, required: false, problems);
        var status = ReadSelector(obj, "Status", path, required: true, problems) ?? new SelectorPath();
        var amount = ReadSelector(obj, "Amount", path, required: false, problems); var scale = ReadScale(obj, path, problems); var map = ReadStatusMap(obj, path, problems);
        return new CallbackSpec
        {
            Format = format,
            AttemptId = attempt,
            ProviderPaymentId = paymentId,
            Status = status,
            Amount = amount,
            Scale = scale,
            StatusMap = map,
        };
    }
    private static AmountScale ReadScale(JsonObject obj, string path, List<string> problems)
    {
        var child = path + ":AmountUnit";
        if (!obj.TryGetPropertyValue("AmountUnit", out var node) || node is null) { return AmountScale.Major; }
        if (!JsonLocal.TryString(node, child, problems, out var text)) { return AmountScale.Major; }
        if (text.Length == 0) { return AmountScale.Major; }
        if (!Words.Scales.TryGetValue(text, out var scale)) { problems.Add(child + ": closed list rejected '" + text + "'."); return AmountScale.Major; }
        return scale;
    }
    private static Dictionary<string, CallbackStatus> ReadStatusMap(JsonObject obj, string path, List<string> problems)
    {
        var child = path + ":StatusMap"; var map = new Dictionary<string, CallbackStatus>(StringComparer.Ordinal);
        if (!obj.TryGetPropertyValue("StatusMap", out var node) || node is null) { problems.Add(child + ": expected an object."); return map; }
        if (node is not JsonObject table) { problems.Add(child + ": expected an object."); return map; }
        foreach (var pair in table)
        {
            var itemPath = child + ":" + pair.Key;
            if (pair.Key.Length == 0 || pair.Key.Trim().Length != pair.Key.Length) { problems.Add(itemPath + ": status key is not usable."); continue; }
            if (!JsonLocal.TryString(pair.Value, itemPath, problems, out var word)) { continue; }
            if (!Words.Statuses.TryGetValue(word, out var status)) { problems.Add(itemPath + ": closed list rejected '" + word + "'."); continue; }
            map[pair.Key] = status;
        }
        return map;
    }
    private static void ReadMethods(JsonObject obj, string root, ProviderPlan plan, List<string> problems)
    {
        var path = root + ":PaymentMethods"; plan.FirmMethods = new Dictionary<string, MethodSpec[]>(StringComparer.Ordinal);
        if (plan.FirmsBroken) { plan.CommonMethods = []; return; }
        if (!obj.TryGetPropertyValue("PaymentMethods", out var node) || node is null) { plan.CommonMethods = []; plan.FlatMethods = plan.Firms.Length <= 1; return; }
        if (node is not JsonObject methods) { problems.Add(path + ": expected an object."); plan.CommonMethods = []; return; }
        if (plan.Firms.Length <= 1) { plan.FlatMethods = true; plan.CommonMethods = ReadMethodMap(methods, path, problems); return; }
        plan.FlatMethods = false; var common = new List<MethodSpec>(); var firms = new HashSet<string>(plan.Firms, StringComparer.Ordinal);
        foreach (var pair in methods)
        {
            var child = path + ":" + pair.Key;
            if (pair.Value is not JsonObject slice) { problems.Add(child + ": expected an object."); continue; }
            if (pair.Key.Equals("Common", StringComparison.Ordinal)) { common.AddRange(ReadMethodMap(slice, child, problems)); continue; }
            if (!firms.Contains(pair.Key)) { problems.Add(child + ": unknown key."); continue; }
            plan.FirmMethods[pair.Key] = ReadMethodMap(slice, child, problems);
        }
        plan.CommonMethods = common.ToArray();
    }
    private static MethodSpec[] ReadMethodMap(JsonObject map, string path, List<string> problems)
    {
        var list = new List<MethodSpec>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in map)
        {
            var child = path + ":" + pair.Key;
            if (pair.Key.Length == 0 || pair.Key.Trim().Length != pair.Key.Length) { problems.Add(child + ": method code is not usable."); continue; }
            if (!seen.Add(pair.Key)) { problems.Add(child + ": duplicate method."); continue; }
            if (pair.Value is not JsonObject obj) { problems.Add(child + ": expected an object."); continue; }
            list.Add(ReadMethod(pair.Key, obj, child, problems));
        }
        return list.ToArray();
    }
    private static MethodSpec ReadMethod(string code, JsonObject obj, string path, List<string> problems)
    {
        JsonLocal.RejectUnknown(obj, Words.MethodKeys, path, problems); var providerMethod = JsonLocal.Required(obj, "ProviderMethod", path, problems);
        var currency = JsonLocal.Required(obj, "Currency", path, problems); var details = JsonLocal.Required(obj, "DetailsType", path, problems);
        if (details.Length > 0 && !Words.DetailTypes.Contains(details)) { problems.Add(path + ":DetailsType: closed list rejected '" + details + "'."); }
        var account = JsonLocal.Optional(obj, "Account", path, problems); var min = ReadBound(obj, "MinAmount", path, problems); var max = ReadBound(obj, "MaxAmount", path, problems);
        if (min > 0m && max > 0m && min > max) { problems.Add(path + ":MinAmount: minimum is above maximum."); }
        var step = ReadBound(obj, "AmountStep", path, problems);
        return new MethodSpec
        {
            Code = code,
            ProviderMethod = providerMethod,
            Currency = currency,
            DetailsType = details,
            Account = account,
            MinAmount = min,
            MaxAmount = max,
            AmountStep = step,
            SourcePath = path,
        };
    }
    private static decimal ReadBound(JsonObject obj, string key, string path, List<string> problems)
    {
        var child = path + ":" + key;
        if (!obj.TryGetPropertyValue(key, out var node) || node is null) { return 0m; }
        if (!JsonLocal.TryDecimal(node, child, problems, out var number)) { return 0m; }
        if (number < 0m) { problems.Add(child + ": expected a number that is zero or greater."); return 0m; }
        return number;
    }
    private static void ReadCapabilities(JsonObject obj, string root, ProviderPlan plan, List<string> problems)
    {
        var path = root + ":Capabilities";
        if (!obj.TryGetPropertyValue("Capabilities", out var node) || node is not JsonArray array) { problems.Add(path + ": expected an array."); return; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < array.Count; i++)
        {
            var child = path + ":" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!JsonLocal.TryString(array[i], child, problems, out var name)) { continue; }
            if (!Words.CapabilityNames.Contains(name)) { problems.Add(child + ": closed list rejected '" + name + "'."); continue; }
            if (!seen.Add(name)) { problems.Add(child + ": duplicate capability."); continue; }
            switch (name)
            {
                case "PayinExecute": plan.PayinExecute = true; break; case "PayinCallback": plan.PayinCallback = true; break; case "PayoutExecute": plan.PayoutExecute = true; break;
                case "PayoutCallback": plan.PayoutCallback = true; break;
            }
        }
    }
    private static void RequireBlocks(JsonObject obj, string root, ProviderPlan plan, List<string> problems)
    {
        var payin = obj.TryGetPropertyValue("Payin", out var payinNode) && payinNode is JsonObject payinObj ? payinObj : null;
        var payout = obj.TryGetPropertyValue("Payout", out var payoutNode) && payoutNode is JsonObject payoutObj ? payoutObj : null;
        RequireOne(plan.PayinExecute, payin, "Create", root + ":Capabilities: PayinExecute requires Payin:Create.", problems);
        RequireOne(plan.PayinCallback, payin, "Callback", root + ":Capabilities: PayinCallback requires Payin:Callback.", problems);
        RequireOne(plan.PayoutExecute, payout, "Create", root + ":Capabilities: PayoutExecute requires Payout:Create.", problems);
        RequireOne(plan.PayoutCallback, payout, "Callback", root + ":Capabilities: PayoutCallback requires Payout:Callback.", problems);
        if ((plan.PayinExecute || plan.PayoutExecute) && CountMethods(plan) == 0 && !plan.FirmsBroken) { problems.Add(root + ":PaymentMethods: expected at least one method."); }
    }
    private static void RequireOne(bool needed, JsonObject? block, string key, string message, List<string> problems)
    {
        if (!needed) { return; }
        if (block is null || !block.ContainsKey(key)) { problems.Add(message); }
    }
    private static int CountMethods(ProviderPlan plan)
    {
        var count = plan.CommonMethods.Length;
        foreach (var slice in plan.FirmMethods.Values) { count += slice.Length; }
        return count;
    }
    private static void LinkAccounts(string root, ProviderPlan plan, List<string> problems)
    {
        var payinFields = new HashSet<string>(StringComparer.Ordinal); var payoutFields = new HashSet<string>(StringComparer.Ordinal);
        var payinClock = false; var payoutClock = false; CollectCreate(plan.PayinCreate, payinFields, ref payinClock);
        CollectCreate(plan.PayoutCreate, payoutFields, ref payoutClock); plan.Auth.Value.Collect(payinFields, ref payinClock);
        plan.Auth.Value.Collect(payoutFields, ref payoutClock); CollectSign(plan.RequestSign, payinFields, ref payinClock); CollectSign(plan.RequestSign, payoutFields, ref payoutClock);
        if (plan.PayinCreate is not null) { plan.PayinCreate.NeedsClock = payinClock; }
        if (plan.PayoutCreate is not null) { plan.PayoutCreate.NeedsClock = payoutClock; }
        var executeFields = new HashSet<string>(payinFields, StringComparer.Ordinal);
        foreach (var field in payoutFields) { executeFields.Add(field); }
        foreach (var method in plan.CommonMethods) { Bind(plan, method, executeFields, problems); }
        foreach (var slice in plan.FirmMethods.Values)
        {
            foreach (var method in slice) { Bind(plan, method, executeFields, problems); }
        }
        var callbackFields = new HashSet<string>(StringComparer.Ordinal); var ignored = false;
        plan.PayinCallbackSign?.Key.Collect(callbackFields, ref ignored); plan.PayoutCallbackSign?.Key.Collect(callbackFields, ref ignored);
        if (callbackFields.Count == 0) { return; }
        if (plan.Accounts.Length == 0) { problems.Add(root + ":Accounts: callback signing needs an account field."); return; }
        var any = false;
        foreach (var account in plan.Accounts)
        {
            if (HasAll(account, callbackFields)) { any = true; break; }
        }
        if (!any) { problems.Add(root + ":Accounts: callback signing field is not on any account."); }
    }
    private static void CollectCreate(CreateSpec? create, HashSet<string> fields, ref bool clock)
    {
        if (create is null) { return; }
        foreach (var field in create.Fields) { field.Template.Collect(fields, ref clock); }
        foreach (var header in create.Headers) { header.Value.Collect(fields, ref clock); }
    }
    private static void CollectSign(SignSpec? sign, HashSet<string> fields, ref bool clock)
    {
        if (sign is null) { return; }
        sign.Key.Collect(fields, ref clock); sign.Base.Collect(fields, ref clock);
    }
    private static void Bind(ProviderPlan plan, MethodSpec method, HashSet<string> fields, List<string> problems)
    {
        var account = Resolve(plan, method, problems);
        if (account is null) { return; }
        foreach (var field in fields)
        {
            if (!HasField(account, field)) { problems.Add(method.SourcePath + ":Account: field '" + field + "' is not on '" + account.Name + "'."); }
        }
    }
    private static AccountSpec? Resolve(ProviderPlan plan, MethodSpec method, List<string> problems)
    {
        if (method.Account.Length > 0)
        {
            foreach (var account in plan.Accounts)
            {
                if (account.Name.Equals(method.Account, StringComparison.Ordinal)) { return account; }
            }
            problems.Add(method.SourcePath + ":Account: '" + method.Account + "' is not defined."); return null;
        }
        if (plan.Accounts.Length == 1) { return plan.Accounts[0]; }
        problems.Add(method.SourcePath + ":Account: empty name needs exactly one account."); return null;
    }
    private static bool HasAll(AccountSpec account, HashSet<string> fields)
    {
        foreach (var field in fields)
        {
            if (!HasField(account, field)) { return false; }
        }
        return true;
    }
    private static bool HasField(AccountSpec account, string name)
    {
        foreach (var field in account.Fields)
        {
            if (field.Name.Equals(name, StringComparison.Ordinal)) { return true; }
        }
        return false;
    }
}
