using System.Net.Http.Headers;
using System.Text;
using Cordis.Composition;

internal static class ManagementCommands
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        try
        {
            if (arguments.Length < 2) throw new ArgumentException("Usage: cordis <command> <management-url> [arguments] [--authorization-env NAME]");
            var values = new List<string>();
            string? authorization = null;
            string? source = null;
            string? requestId = null;
            var approved = false;
            for (var index = 2; index < arguments.Length; index++)
            {
                switch (arguments[index])
                {
                    case "--authorization-env": authorization = Environment.GetEnvironmentVariable(Next()); break;
                    case "--source": source = Next(); break;
                    case "--request-id": requestId = Next(); break;
                    case "--approve-build": approved = true; break;
                    default:
                        if (arguments[index].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Unknown management option.");
                        values.Add(arguments[index]); break;
                }
                string Next() => ++index < arguments.Length ? arguments[index] : throw new ArgumentException("An option value is missing.");
            }
            using var client = new HttpClient { BaseAddress = new Uri(arguments[1].TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
            if (authorization is not null) client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(authorization);
            var state = (EntryOptions)(await GetAsync("state"))!;
            var generation = (string)state["generation"]!;
            client.DefaultRequestHeaders.Add("If-Cordis-Generation", generation);
            object? result = arguments[0] switch
            {
                "state" => state,
                "plugins" => await GetAsync("plugins"),
                "bundles" => await GetAsync("bundles"),
                "sources" => await GetAsync("sources"),
                "versions" => await GetAsync("versions?name=" + Uri.EscapeDataString(Value(0)) + "&source=" + Uri.EscapeDataString(source ?? throw new ArgumentException("--source is required."))),
                "settings" => await GetAsync("settings?entryId=" + Uri.EscapeDataString(Value(0))),
                "configuration" => await GetAsync("configuration?entryId=" + Uri.EscapeDataString(Value(0))),
                "schema" => await GetAsync((Value(0) == "settings" ? "settings" : Value(0) == "configuration" ? "configuration" : throw new ArgumentException("Choose settings or configuration."))
                    + "/schema?entryId=" + Uri.EscapeDataString(Value(1))),
                "edit" => await EditAsync(),
                "compatibility" => await GetAsync("compatibility"),
                "grant" => await PostAsync("compatibility", new()
                {
                    ["packageVersion"] = Value(0), ["runtimeVersion"] = Value(1),
                    ["enabled"] = bool.Parse(Value(2)), ["acceptRisk"] = bool.Parse(Value(3)),
                }),
                "wait" => await GetAsync("install/wait?requestId=" + Uri.EscapeDataString(Value(0))),
                "cancel" => await PostAsync("install/cancel", new() { ["requestId"] = Value(0) }),
                "remove" => await PostAsync("remove", new() { ["name"] = Value(0) }),
                "enable" => await PostAsync("enable", new() { ["kind"] = Value(0), ["target"] = Value(1), ["enabled"] = bool.Parse(Value(2)) }),
                "inspect" => await PostAsync("inspect", Package()),
                "install" => await InstallAsync(),
                _ => throw new ArgumentException("Unknown management command."),
            };
            Console.WriteLine(ConfigurationFile.Write(result, true));
            if (result is null && arguments[0] == "wait") return 3;
            return result is EntryOptions row && (row.GetValueOrDefault("error") is not null || row.GetValueOrDefault("application") as string is "failed" or "cancelled") ? 1 : 0;

            string Value(int index) => values.Count > index ? values[index] : throw new ArgumentException("A command argument is missing.");
            EntryOptions Package() => new() { ["name"] = Value(0), ["version"] = Value(1), ["source"] = source ?? throw new ArgumentException("--source is required.") };
            async Task<object?> EditAsync()
            {
                var scope = Value(0);
                if (scope is not ("settings" or "configuration")) throw new ArgumentException("Choose settings or configuration.");
                var operations = ConfigurationFile.Parse(await File.ReadAllTextAsync(Value(3)), true);
                return await PostAsync(scope, new()
                {
                    ["entryId"] = Value(1), ["revision"] = Value(2), ["operations"] = operations,
                });
            }
            async Task<object?> GetAsync(string path)
            {
                using var response = await client.GetAsync(path);
                return await ReadAsync(response);
            }
            async Task<object?> PostAsync(string path, EntryOptions body)
            {
                using var response = await client.PostAsync(path, new StringContent(ConfigurationFile.Write(body, true), Encoding.UTF8, "application/json"));
                return await ReadAsync(response);
            }
            async Task<object?> InstallAsync()
            {
                if (!approved) throw new ArgumentException("--approve-build acknowledges package code execution; the host must also authorize this package.");
                var package = Package();
                var inspection = (EntryOptions)(await PostAsync("inspect", package))!;
                package["inspectedHash"] = inspection["hash"];
                package["requestId"] = requestId ?? Guid.NewGuid().ToString("N");
                Console.Error.WriteLine("Installation request: " + package["requestId"]);
                try
                {
                    return await PostAsync("install", package);
                }
                catch (HttpRequestException error) when (error.StatusCode is null)
                {
                    // Only transport loss leaves the submission outcome unknown. An explicit
                    // rejection must not borrow the result of a different request with the same ID.
                    var unknown = new EntryOptions { ["error"] = "unknown-result", ["requestId"] = package["requestId"] };
                    try
                    {
                        if (!await IsOriginalHostAsync()) return unknown;
                        var active = await GetAsync("install/wait?requestId=" + Uri.EscapeDataString((string)package["requestId"]!));
                        return await IsOriginalHostAsync() ? active ?? unknown : unknown;
                    }
                    catch (HttpRequestException)
                    {
                        return unknown;
                    }
                }
            }
            async Task<bool> IsOriginalHostAsync()
                => await GetAsync("state") is EntryOptions current && current.GetValueOrDefault("generation") as string == generation;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static async Task<object?> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Management returned {(int)response.StatusCode}: {text}", null, response.StatusCode);
        return ConfigurationFile.Parse(text, true);
    }
}
