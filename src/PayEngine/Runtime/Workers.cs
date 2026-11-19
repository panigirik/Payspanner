using System.Text.Json;
using PayEngine.Contracts;
using PayEngine.Model;
namespace PayEngine.Runtime;
internal sealed class EngineDeps
{
    internal required ProviderPlan Plan { get; init; }
    internal required IOutbound Outbound { get; init; }
    internal required IClock Clock { get; init; }
    internal required IReadOnlyDictionary<string, string> Settings { get; init; }
}
internal interface ISectioned
{
    string Section { get; }
}
internal static class DirectionCall
{
    internal static async Task<PayResult> ExecuteAsync(EngineDeps deps, bool payout, PayRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        if (deps.Plan.TimeoutSeconds < 1) { throw new InvalidOperationException("TimeoutSeconds is not usable for " + deps.Plan.Code + "."); }
        if (!MethodMatcher.TrySelect(deps.Plan, request, deps.Settings, out var method, out var rejection)) { return rejection; }
        var account = AccountBinder.Find(deps.Plan, method);
        if (account is null) { return PayResults.Error("Account.Field", deps.Plan.Code + " / " + deps.Plan.DisplayName + " has no account."); }
        var create = payout ? deps.Plan.PayoutCreate : deps.Plan.PayinCreate; var response = payout ? deps.Plan.PayoutResponse : deps.Plan.PayinResponse;
        if (create is null) { return PayResults.Error("Api.NoActionInResponse", deps.Plan.Code + " / " + deps.Plan.DisplayName + " has no create call."); }
        var values = new AccountValues(account, deps.Settings); var scope = RenderScope.Capture(request, method, values, deps.Clock, create.NeedsClock, deps.Settings);
        var composed = BodyComposer.Compose(create, scope);
        if (!composed.Ok) { return PayResults.Error(composed.Code, composed.Message); }
        var signed = SignatureBuilder.Apply(deps.Plan.RequestSign, composed, scope);
        if (!signed.Ok) { return PayResults.Error(signed.Code, signed.Message); }
        var headers = HeaderBag.Fill(create, scope, signed.HeaderName, signed.HeaderValue, out var bag);
        if (!headers.Ok) { return PayResults.Error(headers.Code, headers.Message); }
        var auth = AuthWriter.Apply(deps.Plan.Auth, bag, scope);
        if (!auth.Ok) { return PayResults.Error(auth.Code, auth.Message); }
        cancellationToken.ThrowIfCancellationRequested();
        var reply = await deps.Outbound.SendAsync(
            new OutboundCall(create.Verb, UrlJoin.Combine(deps.Plan.BaseUrl, create.Path), create.MediaType, signed.Body, bag),
            cancellationToken);
        return OutcomeReader.Read(reply, response ?? new ResponseSpec());
    }
}
internal static class CallbackCall
{
    internal static Task<CallbackNotice> HandleAsync(EngineDeps deps, bool payout, IncomingCallback callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback); cancellationToken.ThrowIfCancellationRequested(); var spec = payout ? deps.Plan.PayoutCallbackSpec : deps.Plan.PayinCallbackSpec;
        if (spec is null) { return Task.FromResult(Empty()); }
        var body = callback.Body ?? "";
        if (string.IsNullOrWhiteSpace(body)) { return Task.FromResult(Empty()); }
        FieldNode tree;
        if (spec.Format == BodyFormat.Json)
        {
            if (LooksBroken(body)) { return Task.FromResult(Empty()); }
            tree = OutcomeReader.Parse(body);
            if (tree.Children is null && !body.TrimStart().StartsWith('[') && !body.TrimStart().StartsWith('{')) { return Task.FromResult(Empty()); }
        }
        else
        {
            try { tree = FormCodec.Parse(body); }
            catch (UriFormatException)
            {
                return Task.FromResult(Empty());
            }
        }
        var sign = payout ? deps.Plan.PayoutCallbackSign : deps.Plan.PayinCallbackSign; var verified = SignatureBuilder.MatchesHeader(sign, body, tree, callback.Headers, deps.Plan, deps.Settings);
        var attempt = OutcomeReader.ReadPath(tree, spec.AttemptId); var paymentId = OutcomeReader.ReadPath(tree, spec.ProviderPaymentId);
        var raw = OutcomeReader.ReadPath(tree, spec.Status); var amount = OutcomeReader.ReadAmount(tree, spec.Amount, spec.Scale); var status = CallbackStatus.Unknown;
        if (raw.Length > 0 && spec.StatusMap.TryGetValue(raw, out var mapped)) { status = mapped; }
        if (!verified) { status = CallbackStatus.Unknown; }
        return Task.FromResult(new CallbackNotice(status, attempt, paymentId, amount, raw));
    }
    private static bool LooksBroken(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.Length == 0) { return true; }
        if (trimmed[0] is not ('{' or '[' or '"' or '-' or 't' or 'f' or 'n') && !char.IsDigit(trimmed[0])) { return true; }
        try { using var document = JsonDocument.Parse(body); return false; }
        catch (JsonException)
        {
            return true;
        }
    }
    private static CallbackNotice Empty()
    {
        return new CallbackNotice(CallbackStatus.Unknown, "", "", null, "");
    }
}
internal sealed class PayinWorker : IPayinExecutor, ISectioned
{
    private readonly EngineDeps deps;
    internal PayinWorker(EngineDeps deps)
    {
        this.deps = deps;
    }
    public string Section => this.deps.Plan.Section;
    public Task<PayResult> ExecuteAsync(PayRequest request, CancellationToken cancellationToken)
    {
        return DirectionCall.ExecuteAsync(this.deps, payout: false, request, cancellationToken);
    }
}
internal sealed class PayoutWorker : IPayoutExecutor, ISectioned
{
    private readonly EngineDeps deps;
    internal PayoutWorker(EngineDeps deps)
    {
        this.deps = deps;
    }
    public string Section => this.deps.Plan.Section;
    public Task<PayResult> ExecuteAsync(PayRequest request, CancellationToken cancellationToken)
    {
        return DirectionCall.ExecuteAsync(this.deps, payout: true, request, cancellationToken);
    }
}
internal sealed class PayinCallbackWorker : IPayinCallbackHandler, ISectioned
{
    private readonly EngineDeps deps;
    internal PayinCallbackWorker(EngineDeps deps)
    {
        this.deps = deps;
    }
    public string Section => this.deps.Plan.Section;
    public Task<CallbackNotice> HandleAsync(IncomingCallback callback, CancellationToken cancellationToken)
    {
        return CallbackCall.HandleAsync(this.deps, payout: false, callback, cancellationToken);
    }
}
internal sealed class PayoutCallbackWorker : IPayoutCallbackHandler, ISectioned
{
    private readonly EngineDeps deps;
    internal PayoutCallbackWorker(EngineDeps deps)
    {
        this.deps = deps;
    }
    public string Section => this.deps.Plan.Section;
    public Task<CallbackNotice> HandleAsync(IncomingCallback callback, CancellationToken cancellationToken)
    {
        return CallbackCall.HandleAsync(this.deps, payout: true, callback, cancellationToken);
    }
}
