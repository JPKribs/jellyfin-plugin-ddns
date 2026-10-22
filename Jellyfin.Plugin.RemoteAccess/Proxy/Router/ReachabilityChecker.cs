using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.DynamicDns.Proxy.Router;

/// <summary>
/// Asks check-host.net to open a TCP connection to the public name on port 443 from a few places on the
/// internet, which is the only way to know a forward works from outside. Only the name and port are sent.
/// A connection counts even when the proxy's country rules close it at once: the goal is the port.
/// </summary>
public sealed class ReachabilityChecker
{
    /// <summary>The port checked, the one the internet reaches HTTPS on.</summary>
    public const int Port = ProxyOptions.PublicHttpsPort;

    private const string Service = "https://check-host.net/";
    private const string Unreachable = "The outside check could not be reached.";

    private static readonly TimeSpan _pollEvery = TimeSpan.FromSeconds(2);
    private const int Polls = 10;

    private static readonly TimeSpan _pollLimit = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan _requestLimit = TimeSpan.FromSeconds(8);

    private readonly IHttpClientFactory _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _lock = new();
    private ReachabilityResult _last = new();
    private Task<ReachabilityResult>? _running;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReachabilityChecker"/> class.
    /// </summary>
    /// <param name="http">The HTTP client factory.</param>
    public ReachabilityChecker(IHttpClientFactory http)
        : this(http, Task.Delay)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReachabilityChecker"/> class with a stand-in wait, for tests.
    /// </summary>
    /// <param name="http">The HTTP client factory.</param>
    /// <param name="delay">Waits between polls.</param>
    internal ReachabilityChecker(IHttpClientFactory http, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _http = http;
        _delay = delay;
    }

    /// <summary>Gets a copy of the last result, an empty one before any check.</summary>
    public ReachabilityResult Last
    {
        get
        {
            lock (_lock)
            {
                return _last.Copy();
            }
        }
    }

    /// <summary>
    /// Runs the outside check now and keeps its result. A check already running is shared, not started twice.
    /// </summary>
    /// <param name="hostname">The public name, empty when there is none.</param>
    /// <param name="cancellationToken">Cancels the wait, not the check.</param>
    /// <returns>The result.</returns>
    public async Task<ReachabilityResult> CheckAsync(string hostname, CancellationToken cancellationToken)
    {
        Task<ReachabilityResult> running;
        lock (_lock)
        {
            if (_running is null || _running.IsCompleted)
            {
                _running = Task.Run(() => RunAsync(hostname), CancellationToken.None);
            }

            running = _running;
        }

        var result = await running.WaitAsync(cancellationToken).ConfigureAwait(false);
        return result.Copy();
    }

    /// <summary>
    /// Reads the answer to the request that starts a check.
    /// </summary>
    /// <param name="json">The answer.</param>
    /// <returns>The request id and each node with its location, or null when the check did not start.</returns>
    internal static (string RequestId, List<(string Id, string Location)> Nodes)? ParseStart(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.Number || ok.GetInt32() != 1
                || !root.TryGetProperty("request_id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString()))
            {
                return null;
            }

            var nodes = new List<(string, string)>();
            if (root.TryGetProperty("nodes", out var list) && list.ValueKind == JsonValueKind.Object)
            {
                foreach (var node in list.EnumerateObject())
                {
                    nodes.Add((node.Name, Location(node.Value)));
                }
            }

            return (id.GetString()!, nodes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a poll of the check's result.
    /// </summary>
    /// <param name="json">The answer.</param>
    /// <returns>Each node's state: null while it is still trying, otherwise whether it connected and why not.
    /// Null as a whole when the answer is not a result.</returns>
    internal static Dictionary<string, (bool Ok, string Error)?>? ParseResult(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var states = new Dictionary<string, (bool, string)?>(StringComparer.Ordinal);
            foreach (var node in doc.RootElement.EnumerateObject())
            {
                states[node.Name] = NodeState(node.Value);
            }

            return states;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts the nodes and their states together into the result the dashboard shows.
    /// </summary>
    /// <param name="nodes">Each node with its location, from the start of the check.</param>
    /// <param name="states">Each node's state from the last poll.</param>
    /// <param name="checkedUtc">When the check ran.</param>
    /// <returns>The result.</returns>
    internal static ReachabilityResult Aggregate(IReadOnlyList<(string Id, string Location)> nodes, IReadOnlyDictionary<string, (bool Ok, string Error)?> states, DateTime checkedUtc)
    {
        var result = new ReachabilityResult { CheckedUtc = checkedUtc };
        foreach (var (id, location) in nodes)
        {
            var state = states.TryGetValue(id, out var s) ? s : null;
            result.Nodes.Add(new ReachabilityNode
            {
                Location = location,
                Ok = state is { Ok: true },
                Error = state is null ? "The check did not finish in time." : state.Value.Error
            });
        }

        if (result.Nodes.Count == 0)
        {
            result.Error = Unreachable;
            return result;
        }

        result.Reachable = result.Nodes.Any(n => n.Ok);
        if (result.Reachable == false)
        {
            result.Error = $"No outside location could connect to port {Port}. Check that the router forwards port {Port} to this server.";
        }

        return result;
    }

    // "City, Country" from a node's ["cc", "Country", "City", "ip", "ASn"].
    private static string Location(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var parts = node.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : string.Empty).ToList();
        var country = parts.Count > 1 ? parts[1].Trim() : string.Empty;
        var city = parts.Count > 2 ? parts[2].Trim() : string.Empty;
        return string.Join(", ", new[] { city, country }.Where(p => p.Length > 0));
    }

    // null while pending. Otherwise the first entry says what happened: a time on success, an error on failure.
    private static (bool Ok, string Error)? NodeState(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var first = value;
        while (first.ValueKind == JsonValueKind.Array)
        {
            if (first.GetArrayLength() == 0)
            {
                return (false, "The connection failed.");
            }

            first = first[0];
        }

        if (first.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (first.ValueKind != JsonValueKind.Object)
        {
            return (false, "The connection failed.");
        }

        if (first.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
        {
            return (false, DescribeError(error.ValueKind == JsonValueKind.String ? error.GetString() ?? string.Empty : string.Empty));
        }

        return first.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Number
            ? (true, string.Empty)
            : (false, "The connection failed.");
    }

    // The service's own words vary, so only what they mean is shown.
    private static string DescribeError(string error)
        => error.Contains("timed out", StringComparison.OrdinalIgnoreCase) || error.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            ? "The connection timed out."
            : error.Contains("refused", StringComparison.OrdinalIgnoreCase)
                ? "The connection was refused."
                : "The connection failed.";

    private async Task<ReachabilityResult> RunAsync(string hostname)
    {
        var started = DateTime.UtcNow;
        var result = string.IsNullOrEmpty(hostname) || !HostnameMatcher.IsPublicName(hostname) || hostname.StartsWith('*')
            ? new ReachabilityResult { CheckedUtc = started, Error = "Add a public address people use from the internet first." }
            : await RunCheckAsync(hostname, started).ConfigureAwait(false);

        lock (_lock)
        {
            _last = result;
        }

        return result;
    }

    private async Task<ReachabilityResult> RunCheckAsync(string hostname, DateTime started)
    {
        using var client = _http.CreateClient(nameof(ReachabilityChecker));
        var target = Uri.EscapeDataString(hostname) + ":" + Port.ToString(CultureInfo.InvariantCulture);
        var (startCode, startBody) = await GetAsync(client, Service + "check-tcp?host=" + target + "&max_nodes=3").ConfigureAwait(false);
        if (startBody is null)
        {
            return new ReachabilityResult { CheckedUtc = started, Error = Unreachable };
        }

        if (ParseStart(startBody) is not { } start)
        {
            // The service answers a request it will not run, such as one past its rate limit, with an error.
            var busy = startCode == 429 || startBody.Contains("\"error\"", StringComparison.Ordinal);
            return new ReachabilityResult { CheckedUtc = started, Error = busy ? "The outside check is busy right now. Try again in a minute." : Unreachable };
        }

        IReadOnlyDictionary<string, (bool Ok, string Error)?> states = new Dictionary<string, (bool, string)?>();
        // Polls are counted rather than timed, so the whole check stays near 20 seconds however slow each one is.
        // A slow service is cut off as well, so the page never waits much past that.
        var deadline = DateTime.UtcNow + _pollLimit;
        for (var poll = 0; poll < Polls && DateTime.UtcNow < deadline; poll++)
        {
            await _delay(_pollEvery, CancellationToken.None).ConfigureAwait(false);
            var (_, body) = await GetAsync(client, Service + "check-result/" + Uri.EscapeDataString(start.RequestId)).ConfigureAwait(false);
            if (body is not null && ParseResult(body) is { } polled)
            {
                states = polled;
                if (start.Nodes.All(n => polled.TryGetValue(n.Id, out var s) && s is not null))
                {
                    break;
                }
            }
        }

        return Aggregate(start.Nodes, states, started);
    }

    private static async Task<(int Code, string? Body)> GetAsync(HttpClient client, string url)
    {
        using var limit = new CancellationTokenSource(_requestLimit);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await client.SendAsync(request, limit.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
            return ((int)response.StatusCode, response.IsSuccessStatusCode || (int)response.StatusCode == 429 ? body : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return (0, null);
        }
    }
}
