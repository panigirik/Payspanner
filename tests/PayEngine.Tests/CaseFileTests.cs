using System.Text.Json.Nodes;
using AwesomeAssertions;
using PayEngine;
using PayEngine.Contracts;
using Xunit;

namespace PayEngine.Tests;

public sealed class CaseFileTests
{
    [Fact]
    public async Task Case_files_match_expected_results()
    {
        var root = Repo.Root();
        var update = Environment.GetEnvironmentVariable("CASES_UPDATE");
        var directory = Path.Combine(root, "tests", "Cases");
        var files = Directory.EnumerateFiles(directory, "*.json").OrderBy(path => path, StringComparer.Ordinal).ToList();
        files.Should().NotBeEmpty();

        var failures = new List<string>();
        foreach (var path in files)
        {
            var document = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)) as JsonObject
                ?? throw new InvalidDataException($"{path} is not a JSON object.");
            var section = Payloads.ReadString(document, "section");
            if (section.Length == 0)
            {
                section = Path.GetFileNameWithoutExtension(path);
            }

            var cases = document["cases"] as JsonArray
                ?? throw new InvalidDataException($"{path} has no cases array.");
            var updating = string.Equals(update, section, StringComparison.OrdinalIgnoreCase);
            var changed = false;

            foreach (var node in cases)
            {
                if (node is not JsonObject testCase)
                {
                    throw new InvalidDataException($"{path} has a case that is not an object.");
                }

                var name = Payloads.ReadString(testCase, "name");
                var actual = await RunAsync(root, section, testCase);
                if (updating)
                {
                    testCase["expected"] = actual;
                    changed = true;
                    continue;
                }

                if (testCase["expected"] is not JsonObject expected)
                {
                    failures.Add($"{section}/{name}: expected is missing");
                    continue;
                }

                var wanted = expected.ToJsonString();
                var got = actual.ToJsonString();
                if (!string.Equals(wanted, got, StringComparison.Ordinal))
                {
                    failures.Add($"{section}/{name}\nexpected: {wanted}\nactual:   {got}");
                }
            }

            if (changed)
            {
                await File.WriteAllTextAsync(path, Snapshots.Write(document), TestContext.Current.CancellationToken);
            }
        }

        failures.Should().BeEmpty();
    }

    private static async Task<JsonObject> RunAsync(string root, string section, JsonObject testCase)
    {
        var definition = FindDefinition(root, section);
        var settings = ReadSettings(testCase["settings"] as JsonObject);
        settings[$"{section}:ApiKey"] = "test-key";
        settings[$"{section}:SecretKey"] = "test-secret";
        var clockText = Payloads.ReadString(testCase, "clock");
        var moment = DateTimeOffset.Parse(clockText, System.Globalization.CultureInfo.InvariantCulture);
        var outbound = new QueueOutbound();
        Enqueue(outbound, testCase["replies"] as JsonArray);
        var temp = Directory.CreateTempSubdirectory("payengine-case-");
        try
        {
            File.Copy(definition, Path.Combine(temp.FullName, Path.GetFileName(definition)));
            var session = EngineHost.Open(temp.FullName, settings, outbound, new FixedClock(moment));
            SessionCheck.RequireClean(session, section);
            return await ExecuteAsync(session, section, testCase, outbound);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    private static async Task<JsonObject> ExecuteAsync(
        EngineSession session,
        string section,
        JsonObject testCase,
        QueueOutbound outbound)
    {
        var kind = Payloads.ReadString(testCase, "kind");
        var token = CancellationToken.None;
        switch (kind)
        {
            case "payin":
            {
                var result = await SessionCheck.Payin(session, section).ExecuteAsync(ReadRequest(testCase), token);
                return Snapshots.Pay(result, outbound.Calls);
            }

            case "payout":
            {
                var result = await SessionCheck.Payout(session, section).ExecuteAsync(ReadRequest(testCase), token);
                return Snapshots.Pay(result, outbound.Calls);
            }

            case "payin-callback":
            {
                var notice = await SessionCheck.PayinCallback(session, section).HandleAsync(ReadCallback(testCase), token);
                return Snapshots.Callback(notice, outbound.Calls);
            }

            case "payout-callback":
            {
                var notice = await SessionCheck.PayoutCallback(session, section).HandleAsync(ReadCallback(testCase), token);
                return Snapshots.Callback(notice, outbound.Calls);
            }

            default:
                throw new InvalidDataException($"Unknown case kind '{kind}'.");
        }
    }

    private static PayRequest ReadRequest(JsonObject testCase)
    {
        var request = testCase["request"] as JsonObject ?? throw new InvalidDataException("A payment case needs request.");
        var firm = Payloads.ReadString(testCase, "firm");
        var requestFirm = Payloads.ReadString(request, "Firm");
        return new PayRequest(
            Payloads.ReadString(request, "AttemptId"),
            Payloads.ReadString(request, "MethodCode"),
            request["Sum"] is JsonNode sum ? Payloads.ReadDecimal(sum) : 0m,
            Payloads.ReadString(request, "Currency"),
            Payloads.ReadString(request, "CallbackUrl"),
            Payloads.ReadString(request, "ClientPhone"),
            requestFirm.Length > 0 ? requestFirm : firm);
    }

    private static IncomingCallback ReadCallback(JsonObject testCase)
    {
        var callback = testCase["callback"] as JsonObject ?? throw new InvalidDataException("A callback case needs callback.");
        return new IncomingCallback(
            Payloads.ReadString(callback, "body"),
            Payloads.ReadString(callback, "contentType"),
            ReadMap(callback["headers"] as JsonObject),
            ReadMap(callback["query"] as JsonObject),
            Payloads.ReadString(callback, "path"));
    }

    private static Dictionary<string, string> ReadSettings(JsonObject? settings) => ReadMap(settings);

    private static Dictionary<string, string> ReadMap(JsonObject? node)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is null)
        {
            return map;
        }

        foreach (var pair in node)
        {
            map[pair.Key] = pair.Value is JsonValue value && value.TryGetValue<string>(out var text)
                ? text
                : pair.Value?.ToJsonString() ?? string.Empty;
        }

        return map;
    }

    private static void Enqueue(QueueOutbound outbound, JsonArray? replies)
    {
        if (replies is null)
        {
            return;
        }

        foreach (var node in replies)
        {
            if (node is not JsonObject reply)
            {
                continue;
            }

            var status = reply["status"] is JsonNode code ? Payloads.ReadInt(code) : 200;
            outbound.Enqueue(status, BodyText(reply["body"]));
        }
    }

    private static string BodyText(JsonNode? body) => body switch
    {
        null => string.Empty,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => body.ToJsonString(),
    };

    private static string FindDefinition(string root, string section)
    {
        var hits = new List<string>();
        foreach (var folder in new[] { Path.Combine(root, "integrations"), Path.Combine(root, "tests", "fixtures") })
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                if (JsonNode.Parse(File.ReadAllText(file)) is JsonObject document
                    && document["Providers"] is JsonObject providers
                    && providers.ContainsKey(section))
                {
                    hits.Add(file);
                }
            }
        }

        return hits.Count == 1
            ? hits[0]
            : throw new InvalidOperationException(
                hits.Count == 0
                    ? $"No definition for {section}."
                    : $"More than one definition for {section}: {string.Join(", ", hits)}");
    }
}
