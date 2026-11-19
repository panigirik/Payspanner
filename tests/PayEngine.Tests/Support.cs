using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PayEngine;
using PayEngine.Contracts;

namespace PayEngine.Tests;

internal static class Methods
{
    public const string Card = "P2P_PAYIN_CARD_RUB_RUS_ANY_RUB_RUS_ANY";

    public const string Qr = "P2P_PAYIN_QR_RUB_RUS_ANY_RUB_RUS_ANY";

    public const string Bank = "P2P_PAYOUT_BANK_ACCOUNT_RUB_RUS_ANY_RUB_RUS_ANY";

    public const string Stepped = "P2P_PAYIN_PHONE_RUB_RUS_STEP_RUB_RUS_STEP";

    public const string Phone = "+10000000000";
}

internal static class Repo
{
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var cases = Path.Combine(dir.FullName, "tests", "Cases", "Scheme.json");
            var example = Path.Combine(dir.FullName, "integrations", "example.json");
            if (File.Exists(cases) && File.Exists(example))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("The hiring base directory was not found from " + AppContext.BaseDirectory);
    }
}

internal sealed class FixedClock : IClock
{
    public FixedClock(DateTimeOffset now) => UtcNow = now;

    public DateTimeOffset UtcNow { get; }
}

internal sealed class QueueOutbound : IOutbound
{
    private readonly Queue<OutboundReply> _pending = new();
    private readonly List<OutboundCall> _calls = [];

    public IReadOnlyList<OutboundCall> Calls => _calls;

    public void Enqueue(int status, string body) => _pending.Enqueue(new OutboundReply(status, body));

    public Task<OutboundReply> SendAsync(OutboundCall call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _calls.Add(call);
        if (_pending.Count == 0)
        {
            return Task.FromResult(new OutboundReply(599, string.Empty));
        }

        return Task.FromResult(_pending.Dequeue());
    }
}

internal static class SessionCheck
{
    public static void RequireClean(EngineSession session, string section)
    {
        if (HasAny(session.Problems))
        {
            throw new InvalidOperationException($"{section} has problems: {Describe(session.Problems)}");
        }

        RequireSection(session.Sections, section);
    }

    public static IPayinExecutor Payin(EngineSession session, string section)
    {
        if (!session.TryPayin(section, out var executor) || executor is null)
        {
            throw new InvalidOperationException($"{section} did not register a payin executor. {Describe(session.Problems)}");
        }

        return executor;
    }

    public static IPayoutExecutor Payout(EngineSession session, string section)
    {
        if (!session.TryPayout(section, out var executor) || executor is null)
        {
            throw new InvalidOperationException($"{section} did not register a payout executor. {Describe(session.Problems)}");
        }

        return executor;
    }

    public static IPayinCallbackHandler PayinCallback(EngineSession session, string section)
    {
        if (!session.TryPayinCallback(section, out var handler) || handler is null)
        {
            throw new InvalidOperationException($"{section} did not register a payin callback. {Describe(session.Problems)}");
        }

        return handler;
    }

    public static IPayoutCallbackHandler PayoutCallback(EngineSession session, string section)
    {
        if (!session.TryPayoutCallback(section, out var handler) || handler is null)
        {
            throw new InvalidOperationException($"{section} did not register a payout callback. {Describe(session.Problems)}");
        }

        return handler;
    }

    public static string Describe<T>(IEnumerable<T> items) =>
        string.Join("; ", items.Select(static item => item?.ToString() ?? string.Empty));

    public static bool HasAny<T>(IEnumerable<T> items) => items.Any();

    private static void RequireSection<T>(IEnumerable<T> sections, string section)
    {
        var names = sections.Select(static item => item?.ToString() ?? string.Empty).ToList();
        if (names.Contains(section) || names.Exists(name => name.Contains(section, StringComparison.Ordinal)))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Section {section} was not loaded. Sections: {string.Join(", ", names)}.");
    }
}

internal static class Signatures
{
    public static string HmacSha256HexLower(string body, string key)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(body));
        var text = new StringBuilder(hash.Length * 2);
        foreach (var part in hash)
        {
            text.Append(part.ToString("x2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
}

internal static class Snapshots
{
    private static readonly JsonSerializerOptions FileJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static JsonObject Pay(PayResult result, IReadOnlyList<OutboundCall> calls) =>
        new()
        {
            ["kind"] = JsonValue.Create(result.Kind.ToString()),
            ["code"] = JsonValue.Create(Text(result.Code)),
            ["providerPaymentId"] = JsonValue.Create(Text(result.ProviderPaymentId)),
            ["requisiteType"] = JsonValue.Create(Text(result.RequisiteType)),
            ["requisiteValue"] = JsonValue.Create(Text(result.RequisiteValue)),
            ["redirect"] = JsonValue.Create(Text(result.RedirectUrl)),
            ["callbackStatus"] = JsonValue.Create(string.Empty),
            ["attemptId"] = JsonValue.Create(string.Empty),
            ["rawStatus"] = JsonValue.Create(string.Empty),
            ["calls"] = Calls(calls),
        };

    public static JsonObject Callback(CallbackNotice notice, IReadOnlyList<OutboundCall> calls)
    {
        var node = new JsonObject
        {
            ["kind"] = JsonValue.Create(string.Empty),
            ["code"] = JsonValue.Create(string.Empty),
            ["providerPaymentId"] = JsonValue.Create(Text(notice.ProviderPaymentId)),
            ["requisiteType"] = JsonValue.Create(string.Empty),
            ["requisiteValue"] = JsonValue.Create(string.Empty),
            ["redirect"] = JsonValue.Create(string.Empty),
            ["callbackStatus"] = JsonValue.Create(notice.Status.ToString()),
            ["attemptId"] = JsonValue.Create(Text(notice.AttemptId)),
        };

        if (notice.Amount is decimal amount)
        {
            node["amount"] = WholeOrDecimal(amount);
        }

        node["rawStatus"] = JsonValue.Create(Text(notice.RawStatus));
        node["calls"] = Calls(calls);
        return node;
    }

    public static string Write(JsonNode node)
    {
        var json = node.ToJsonString(FileJson);
        return json.EndsWith('\n') ? json : json + "\n";
    }

    private static JsonArray Calls(IReadOnlyList<OutboundCall> calls)
    {
        var array = new JsonArray();
        foreach (var call in calls)
        {
            array.Add(new JsonObject
            {
                ["method"] = JsonValue.Create(Text(call.Method)),
                ["url"] = JsonValue.Create(Text(call.Url)),
                ["contentType"] = JsonValue.Create(Text(call.ContentType)),
                ["body"] = JsonValue.Create(Text(call.Body)),
            });
        }

        return array;
    }

    private static JsonValue WholeOrDecimal(decimal amount) =>
        amount == decimal.Truncate(amount)
            ? JsonValue.Create(decimal.ToInt64(amount))!
            : JsonValue.Create(amount)!;

    private static string Text(string? value) => value ?? string.Empty;
}

internal static class Payloads
{
    public static PayRequest Request(string attemptId, string methodCode, decimal sum, string callbackUrl, string firm) =>
        new(attemptId, methodCode, sum, "RUB", callbackUrl, Methods.Phone, firm);

    public static decimal ReadDecimal(JsonNode node) =>
        decimal.Parse(node.ToJsonString(), CultureInfo.InvariantCulture);

    public static int ReadInt(JsonNode node) =>
        int.Parse(node.ToJsonString(), CultureInfo.InvariantCulture);

    public static string ReadString(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;
}
