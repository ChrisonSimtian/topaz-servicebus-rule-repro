using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Messaging.ServiceBus;

namespace TopazServiceBusRepro;

/// <summary>
/// Reproduces two Service Bus defects in Topaz: a subscription rule loses its filter definition, and as a
/// result the topic does not route by correlation filter.
///
/// <para>Run with Topaz up (see the README), then <c>dotnet run --project src/Repro</c>.</para>
/// </summary>
internal static class Program
{
    private const string Arm = "https://localhost:8899";
    private const string TenantId = "50717675-3E5E-4A1E-8CB5-C62D8BE8CA48";
    private const string SubscriptionId = "11111111-1111-1111-1111-111111111111";
    private const string ResourceGroup = "rg-topaz-repro";
    private const string Namespace = "topazrepro";
    private const string Topic = "demo-topic";
    private const string Subscription = "matching-only";
    private const string FilterProperty = "PayloadType";
    private const string MatchingValue = "Wanted";
    private const string OtherValue = "Unwanted";

    private static readonly HttpClient Http = CreateHttpClient();
    private static int _failures;

    private static async Task<int> Main()
    {
        await AuthenticateAsync();

        Section("Control plane — create the topology");
        await PutAsync($"/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}?api-version=2021-04-01",
            """{"location":"local"}""");
        await PutAsync($"{NamespacePath}?api-version=2024-01-01",
            """{"location":"local","sku":{"name":"Standard","tier":"Standard"}}""");
        await PutAsync($"{NamespacePath}/topics/{Topic}?api-version=2024-01-01",
            """{"properties":{"maxSizeInMegabytes":1024}}""");
        await PutAsync($"{NamespacePath}/topics/{Topic}/subscriptions/{Subscription}?api-version=2024-01-01",
            """{"properties":{"maxDeliveryCount":10,"lockDuration":"PT1M"}}""");

        Section($"Defect 1 — a rule does not keep its filter");
        var sent =
            $$"""{"properties":{"filterType":"CorrelationFilter","correlationFilter":{"properties":{"{{FilterProperty}}":"{{MatchingValue}}"} } } }""";
        Console.WriteLine($"  PUT  {Compact(sent)}");
        await PutAsync($"{RulePath}?api-version=2024-01-01", sent);

        // A subscription is born with a $Default TrueFilter rule. Left in place, every message matches
        // through it whatever else is defined — so remove it, exactly as you would against Azure when
        // replacing the default with a filter of your own.
        await DeleteAsync($"{DefaultRulePath}?api-version=2024-01-01");

        var stored = await GetAsync($"{RulePath}?api-version=2024-01-01");
        var properties = JsonNode.Parse(stored)?["properties"];
        Console.WriteLine($"  GET  {Compact(properties?.ToJsonString() ?? "null")}");

        Expect("filterType survives the round trip",
            properties?["filterType"]?.GetValue<string>() == "CorrelationFilter",
            $"expected CorrelationFilter, got {properties?["filterType"]?.GetValue<string>() ?? "null"}");
        Expect("the filter definition survives the round trip",
            properties?["correlationFilter"] is not null,
            "no correlationFilter on the stored rule — the definition was dropped");

        Section("Defect 2 — the topic does not route by that filter");
        await RunDataPlaneAsync();

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? "All expectations held — nothing to report."
            : $"{_failures} expectation(s) failed. See README.md for what Azure does instead.");
        return _failures == 0 ? 0 : 1;
    }

    private static async Task RunDataPlaneAsync()
    {
        var keys = JsonNode.Parse(await PostAsync(
            $"{NamespacePath}/AuthorizationRules/RootManageSharedAccessKey/listKeys?api-version=2024-01-01"));
        var connectionString = keys?["primaryConnectionString"]?.GetValue<string>();
        if (connectionString is null)
        {
            Expect("listKeys returns a connection string", false, "no primaryConnectionString");
            return;
        }

        Console.WriteLine($"  connection: {connectionString.Split(';')[0]}");

        // Topaz hands out a *.servicebus.topaz.local.dev host. CustomEndpointAddress keeps that hostname for
        // the SAS signature while actually dialling the published port, so no hosts-file entry is needed.
        var options = new ServiceBusClientOptions
        {
            TransportType = ServiceBusTransportType.AmqpTcp,
            CustomEndpointAddress = new Uri("sb://localhost:5671")
        };

        try
        {
            await using var client = new ServiceBusClient(connectionString, options);

            await using (var sender = client.CreateSender(Topic))
            {
                await sender.SendMessageAsync(Labelled(MatchingValue));
                await sender.SendMessageAsync(Labelled(OtherValue));
            }

            await using var receiver = client.CreateReceiver(Topic, Subscription,
                new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete });

            var received = await receiver.ReceiveMessagesAsync(10, TimeSpan.FromSeconds(5));
            var labels = received
                .Select(m => m.ApplicationProperties.TryGetValue(FilterProperty, out var v) ? $"{v}" : "(none)")
                .ToList();

            Console.WriteLine($"  sent     : {MatchingValue}, {OtherValue}");
            Console.WriteLine($"  received : {(labels.Count == 0 ? "(nothing)" : string.Join(", ", labels))}");

            Expect($"only {MatchingValue} is delivered",
                labels.Count == 1 && labels[0] == MatchingValue,
                $"the correlation filter did not select — got [{string.Join(", ", labels)}]");
        }
        catch (Exception ex)
        {
            Expect("the data plane accepts a send/receive", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static ServiceBusMessage Labelled(string payloadType)
    {
        var message = new ServiceBusMessage(BinaryData.FromString($$"""{"payloadType":"{{payloadType}}"}"""));
        message.ApplicationProperties[FilterProperty] = payloadType;
        return message;
    }

    private static string NamespacePath =>
        $"/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}" +
        $"/providers/Microsoft.ServiceBus/namespaces/{Namespace}";

    private static string RulePath =>
        $"{NamespacePath}/topics/{Topic}/subscriptions/{Subscription}/rules/only-wanted";

    private static string DefaultRulePath =>
        $"{NamespacePath}/topics/{Topic}/subscriptions/{Subscription}/rules/" + "$Default";

    private static async Task AuthenticateAsync()
    {
        var response = await Http.PostAsync($"{Arm}/{TenantId}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = "topazadmin@topaz.local.dev",
                ["password"] = "admin",
                ["scope"] = "https://management.azure.com/.default"
            }));

        var token = JsonNode.Parse(await response.Content.ReadAsStringAsync())?["access_token"]?.GetValue<string>();
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task PutAsync(string path, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await Http.PutAsync($"{Arm}{path}", content);
        if (!response.IsSuccessStatusCode)
        {
            Expect($"PUT {path.Split('/').Last()}", false, $"HTTP {(int)response.StatusCode}");
        }
    }

    private static async Task DeleteAsync(string path)
    {
        var response = await Http.DeleteAsync($"{Arm}{path}");
        Console.WriteLine($"  DEL  default rule → HTTP {(int)response.StatusCode}");
    }

    private static async Task<string> GetAsync(string path) =>
        await (await Http.GetAsync($"{Arm}{path}")).Content.ReadAsStringAsync();

    private static async Task<string> PostAsync(string path) =>
        await (await Http.PostAsync($"{Arm}{path}", new StringContent("", Encoding.UTF8, "application/json")))
            .Content.ReadAsStringAsync();

    private static HttpClient CreateHttpClient() =>
        new(new HttpClientHandler
        {
            // The emulator serves a self-signed certificate.
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        });

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', title.Length));
    }

    private static void Expect(string what, bool held, string detail)
    {
        if (held)
        {
            Console.WriteLine($"  PASS  {what}");
            return;
        }

        _failures++;
        Console.WriteLine($"  FAIL  {what}");
        Console.WriteLine($"        {detail}");
    }

    private static string Compact(string json)
    {
        try
        {
            return JsonSerializer.Serialize(JsonNode.Parse(json));
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
