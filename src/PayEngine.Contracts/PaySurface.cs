namespace PayEngine.Contracts;
public enum PayResultKind
{
    Accepted,
    Declined,
    Error,
}
public sealed record PayResult(
    PayResultKind Kind,
    string Code,
    string Message,
    string ProviderPaymentId,
    string RequisiteType,
    string RequisiteValue,
    string RedirectUrl);
public enum CallbackStatus
{
    PaymentSucceeded,
    PaymentFailed,
    PaymentExpired,
    NoAction,
    Unknown,
}
public sealed record CallbackNotice(
    CallbackStatus Status,
    string AttemptId,
    string ProviderPaymentId,
    decimal? Amount,
    string RawStatus);
public sealed record PayRequest(
    string AttemptId,
    string MethodCode,
    decimal Sum,
    string Currency,
    string CallbackUrl,
    string ClientPhone,
    string Firm);
public sealed record IncomingCallback(
    string Body,
    string ContentType,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> Query,
    string Path);
public interface IPayinExecutor
{
    Task<PayResult> ExecuteAsync(PayRequest request, CancellationToken cancellationToken);
}
public interface IPayoutExecutor
{
    Task<PayResult> ExecuteAsync(PayRequest request, CancellationToken cancellationToken);
}
public interface IPayinCallbackHandler
{
    Task<CallbackNotice> HandleAsync(IncomingCallback callback, CancellationToken cancellationToken);
}
public interface IPayoutCallbackHandler
{
    Task<CallbackNotice> HandleAsync(IncomingCallback callback, CancellationToken cancellationToken);
}
public sealed record OutboundCall(
    string Method,
    string Url,
    string ContentType,
    string Body,
    IReadOnlyDictionary<string, string> Headers);
public sealed record OutboundReply(int Status, string Body);
public interface IOutbound
{
    Task<OutboundReply> SendAsync(OutboundCall call, CancellationToken cancellationToken);
}
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
