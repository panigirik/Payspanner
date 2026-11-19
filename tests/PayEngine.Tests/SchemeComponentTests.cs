using System.Globalization;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using PayEngine;
using PayEngine.Contracts;
using Xunit;

namespace PayEngine.Tests;

public sealed class SchemeComponentTests
{
    private static readonly DateTimeOffset Moment = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<string, string> Settings = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Scheme:ApiKey"] = "test-key",
        ["Scheme:SecretKey"] = "test-secret",
    };

    [Fact]
    public async Task Payin_accepts_a_card_and_signs_the_body()
    {
        var outbound = new QueueOutbound();
        outbound.Enqueue(200, """{"id":"pay-1","url":"https://pay.scheme.test/go/pay-1","card":"token-card-1"}""");
        var session = OpenScheme(outbound);
        var request = Payloads.Request("att-accept", Methods.Card, 1500.50m, "https://callback.test/payin", "firm-a");

        var result = await SessionCheck.Payin(session, "Scheme").ExecuteAsync(request, CancellationToken.None);

        result.Kind.Should().Be(PayResultKind.Accepted);
        result.ProviderPaymentId.Should().Be("pay-1");
        result.RequisiteType.Should().Be("Card");
        result.RequisiteValue.Should().Be("token-card-1");
        result.RedirectUrl.Should().Be("https://pay.scheme.test/go/pay-1");
        var call = OnlyCall(outbound);
        call.Method.Should().Be("POST");
        call.Url.Should().Be("https://pay.scheme.test/v1/orders");
        call.ContentType.Should().Be("application/json");
        call.Body.Should().Contain("1500.5");
        call.Body.Should().Contain("150050");
        call.Body.Should().Contain("att-accept");
        call.Body.Should().Contain(Moment.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        Header(call, "X-Signature").Should().Be(Signatures.HmacSha256HexLower(call.Body, "test-secret"));
        Header(call, "Authorization").Should().Be("Bearer test-key");
    }

    [Fact]
    public async Task Payin_returns_a_qr_requisite()
    {
        var outbound = new QueueOutbound();
        outbound.Enqueue(200, """{"id":"pay-qr","url":"https://pay.scheme.test/go/pay-qr","qr":"qr-token-1"}""");
        var session = OpenScheme(outbound);
        var request = Payloads.Request("att-qr", Methods.Qr, 1500m, "https://callback.test/payin", "firm-a");

        var result = await SessionCheck.Payin(session, "Scheme").ExecuteAsync(request, CancellationToken.None);

        result.Kind.Should().Be(PayResultKind.Accepted);
        result.RequisiteType.Should().Be("Qr");
        result.RequisiteValue.Should().Be("qr-token-1");
        var call = OnlyCall(outbound);
        call.Body.Should().Contain("qr");
    }

    [Fact]
    public async Task Payin_declines_by_outcome_rule_without_a_requisite()
    {
        var outbound = new QueueOutbound();
        outbound.Enqueue(200, """{"id":"pay-2","status":"refused","card":"token-card-9"}""");
        var session = OpenScheme(outbound);
        var request = Payloads.Request("att-decline", Methods.Card, 1500m, "https://callback.test/payin", "firm-a");

        var result = await SessionCheck.Payin(session, "Scheme").ExecuteAsync(request, CancellationToken.None);

        result.Kind.Should().Be(PayResultKind.Declined);
        result.Code.Should().Be("Api.Refused");
        result.RequisiteType.Should().BeNullOrEmpty();
        result.RequisiteValue.Should().BeNullOrEmpty();
        outbound.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Payin_is_an_error_on_http_422()
    {
        var outbound = new QueueOutbound();
        outbound.Enqueue(422, """{"errorCode":"nope"}""");
        var session = OpenScheme(outbound);
        var request = Payloads.Request("att-422", Methods.Card, 1500m, "https://callback.test/payin", "firm-a");

        var result = await SessionCheck.Payin(session, "Scheme").ExecuteAsync(request, CancellationToken.None);

        result.Kind.Should().Be(PayResultKind.Error);
        result.Code.Should().Be("Api.HttpError");
        result.ProviderPaymentId.Should().BeNullOrEmpty();
        result.RequisiteValue.Should().BeNullOrEmpty();
        outbound.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Missing_method_does_not_call_http()
    {
        var outbound = new QueueOutbound();
        var session = OpenScheme(outbound);
        var request = Payloads.Request(
            "att-missing",
            "P2P_PAYIN_CARD_RUB_RUS_NONE_RUB_RUS_NONE",
            1500m,
            "https://callback.test/payin",
            "firm-a");

        var result = await SessionCheck.Payin(session, "Scheme").ExecuteAsync(request, CancellationToken.None);

        result.Kind.Should().BeOneOf(PayResultKind.Declined, PayResultKind.Error);
        outbound.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(10)]
    [InlineData(200000)]
    public async Task Amount_outside_the_band_does_not_call_http(int sum)
    {
        var outbound = new QueueOutbound();
        var session = OpenScheme(outbound);
        var request = Payloads.Request("att-band", Methods.Card, sum, "https://callback.test/payin", "firm-a");

        var result = await SessionCheck.Payin(session, "Scheme").ExecuteAsync(request, CancellationToken.None);

        result.Kind.Should().BeOneOf(PayResultKind.Declined, PayResultKind.Error);
        outbound.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1505)]
    [InlineData(100000)]
    public async Task Amount_on_the_step_is_sent(int sum)
    {
        var outbound = new QueueOutbound();
        outbound.Enqueue(200, """{"id":"pay-step"}""");
        var session = OpenScheme(outbound);
        var request = Payloads.Request("att-step", Methods.Stepped, sum, "https://callback.test/payin", "firm-a");

        var result = await SessionCheck.Payin(session, "Scheme").ExecuteAsync(request, CancellationToken.None);

        result.Kind.Should().Be(PayResultKind.Accepted);
        result.ProviderPaymentId.Should().Be("pay-step");
        OnlyCall(outbound).Body.Should().Contain(sum.ToString(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("1502")]
    [InlineData("1500.5")]
    [InlineData("99999")]
    public async Task Amount_off_the_step_is_declined_without_http(string sum)
    {
        var outbound = new QueueOutbound();
        var session = OpenScheme(outbound);
        var amount = decimal.Parse(sum, CultureInfo.InvariantCulture);
        var request = Payloads.Request("att-off-step", Methods.Stepped, amount, "https://callback.test/payin", "firm-a");

        var result = await SessionCheck.Payin(session, "Scheme").ExecuteAsync(request, CancellationToken.None);

        result.Kind.Should().Be(PayResultKind.Declined);
        result.Code.Should().Be("Method.Rejected");
        outbound.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("\"5\"")]
    public void Unusable_amount_step_is_a_problem(string step)
    {
        var problems = OpenWithStep(JsonNode.Parse(step));

        SessionCheck.HasAny(problems).Should().BeTrue();
    }

    [Fact]
    public void Zero_amount_step_means_no_step()
    {
        var problems = OpenWithStep(JsonValue.Create(0));

        problems.Should().BeEmpty();
    }

    [Theory]
    [InlineData("paid", CallbackStatus.PaymentSucceeded)]
    [InlineData("failed", CallbackStatus.PaymentFailed)]
    [InlineData("expired", CallbackStatus.PaymentExpired)]
    [InlineData("pending", CallbackStatus.NoAction)]
    public async Task Callback_maps_known_statuses(string raw, CallbackStatus expected)
    {
        var outbound = new QueueOutbound();
        var session = OpenScheme(outbound);
        var body = CallbackBody(raw);
        var notice = await SessionCheck.PayinCallback(session, "Scheme").HandleAsync(Incoming(body, Sign(body)), CancellationToken.None);

        notice.Status.Should().Be(expected);
        notice.RawStatus.Should().Be(raw);
        notice.AttemptId.Should().Be("att-cb");
        notice.ProviderPaymentId.Should().Be("pay-9");
        notice.Amount.Should().Be(100m);
        outbound.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Callback_unknown_status_is_unknown()
    {
        var outbound = new QueueOutbound();
        var session = OpenScheme(outbound);
        var body = CallbackBody("other");

        var notice = await SessionCheck.PayinCallback(session, "Scheme").HandleAsync(Incoming(body, Sign(body)), CancellationToken.None);

        notice.Status.Should().Be(CallbackStatus.Unknown);
        notice.RawStatus.Should().Be("other");
    }

    [Fact]
    public async Task Callback_bad_signature_is_unknown()
    {
        var outbound = new QueueOutbound();
        var session = OpenScheme(outbound);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["X-Signature"] = "00" };

        var notice = await SessionCheck.PayinCallback(session, "Scheme")
            .HandleAsync(Incoming(CallbackBody("paid"), headers), CancellationToken.None);

        notice.Status.Should().Be(CallbackStatus.Unknown);
        outbound.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Payout_create_is_accepted()
    {
        var outbound = new QueueOutbound();
        outbound.Enqueue(200, """{"id":"out-1"}""");
        var session = OpenScheme(outbound);
        var request = Payloads.Request("att-payout", Methods.Bank, 2500.50m, "https://callback.test/payout", "firm-a");

        var result = await SessionCheck.Payout(session, "Scheme").ExecuteAsync(request, CancellationToken.None);

        result.Kind.Should().Be(PayResultKind.Accepted);
        result.ProviderPaymentId.Should().Be("out-1");
        var call = OnlyCall(outbound);
        call.Method.Should().Be("POST");
        call.Url.Should().Be("https://pay.scheme.test/v1/payouts");
        call.Body.Should().Contain("2500.5");
        call.Body.Should().Contain("250050");
        call.Body.Should().Contain(Moment.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        Header(call, "X-Signature").Should().Be(Signatures.HmacSha256HexLower(call.Body, "test-secret"));
    }

    [Fact]
    public void Unknown_json_key_is_a_problem()
    {
        var source = Path.Combine(Repo.Root(), "tests", "fixtures", "scheme.json");
        var root = JsonNode.Parse(File.ReadAllText(source)) as JsonObject
            ?? throw new InvalidDataException("scheme.json is not an object.");
        var scheme = root["Providers"]?["Scheme"] as JsonObject
            ?? throw new InvalidDataException("scheme.json has no Scheme section.");
        scheme["UnknownKey"] = JsonValue.Create("x");
        var temp = Directory.CreateTempSubdirectory("payengine-scheme-");
        try
        {
            File.WriteAllText(Path.Combine(temp.FullName, "scheme.json"), root.ToJsonString());
            var session = EngineHost.Open(temp.FullName, Settings, new QueueOutbound(), new FixedClock(Moment));
            SessionCheck.HasAny(session.Problems).Should().BeTrue();
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    private static IReadOnlyList<string> OpenWithStep(JsonNode? step)
    {
        var source = Path.Combine(Repo.Root(), "tests", "fixtures", "scheme.json");
        var root = JsonNode.Parse(File.ReadAllText(source)) as JsonObject
            ?? throw new InvalidDataException("scheme.json is not an object.");
        var method = root["Providers"]?["Scheme"]?["PaymentMethods"]?[Methods.Stepped] as JsonObject
            ?? throw new InvalidDataException("scheme.json has no stepped method.");
        method["AmountStep"] = step;
        var temp = Directory.CreateTempSubdirectory("payengine-scheme-");
        try
        {
            File.WriteAllText(Path.Combine(temp.FullName, "scheme.json"), root.ToJsonString());
            return EngineHost.Open(temp.FullName, Settings, new QueueOutbound(), new FixedClock(Moment)).Problems;
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    private static EngineSession OpenScheme(IOutbound outbound)
    {
        var session = EngineHost.Open(
            Path.Combine(Repo.Root(), "tests", "fixtures"),
            Settings,
            outbound,
            new FixedClock(Moment));
        SessionCheck.RequireClean(session, "Scheme");
        return session;
    }

    private static OutboundCall OnlyCall(QueueOutbound outbound)
    {
        outbound.Calls.Should().ContainSingle();
        return outbound.Calls[0];
    }

    private static string CallbackBody(string status) =>
        "{\"attemptId\":\"att-cb\",\"id\":\"pay-9\",\"status\":\"" + status + "\",\"amount\":100}";

    private static Dictionary<string, string> Sign(string body) =>
        new(StringComparer.Ordinal)
        {
            ["X-Signature"] = Signatures.HmacSha256HexLower(body, "test-secret"),
        };

    private static IncomingCallback Incoming(string body, IReadOnlyDictionary<string, string> headers) =>
        new(body, "application/json", headers, new Dictionary<string, string>(), "/callback");

    private static string Header(OutboundCall call, string name)
    {
        foreach (var pair in call.Headers)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return string.Empty;
    }
}
