using System.Diagnostics;
using Preflight.Core.Manifest;

namespace Preflight.Core.Smoke;

public sealed record SmokeResult(string Route, int? Status, long ElapsedMs, bool Passed, string? Failure);

/// <summary>Parallel HTTP GET checks on every manifest endpoint (§5 step 1.4 and 3.4).</summary>
public sealed class SmokeTester(HttpClient http)
{
    public async Task<IReadOnlyList<SmokeResult>> RunAsync(string baseUrl, SmokeTestSettings settings,
        Action<SmokeResult>? onResult = null, CancellationToken ct = default)
    {
        var baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
        var tasks = settings.Endpoints.Select(async route =>
        {
            var result = await CheckAsync(new Uri(baseUri, route.TrimStart('/')), route, settings, ct);
            onResult?.Invoke(result);
            return result;
        });
        return await Task.WhenAll(tasks);
    }

    private async Task<SmokeResult> CheckAsync(Uri url, string route, SmokeTestSettings s, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(s.TimeoutSeconds));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            using var resp = await http.SendAsync(req, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            sw.Stop();
            var status = (int)resp.StatusCode;
            if (status != s.AssertStatus)
                return new SmokeResult(route, status, sw.ElapsedMilliseconds, false, $"expected {s.AssertStatus}, got {status}");
            var bad = s.DisallowedStrings.FirstOrDefault(d => body.Contains(d, StringComparison.OrdinalIgnoreCase));
            return bad is null
                ? new SmokeResult(route, status, sw.ElapsedMilliseconds, true, null)
                : new SmokeResult(route, status, sw.ElapsedMilliseconds, false, $"response contains \"{bad}\"");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SmokeResult(route, null, sw.ElapsedMilliseconds, false, $"timeout after {s.TimeoutSeconds}s");
        }
        catch (HttpRequestException e)
        {
            return new SmokeResult(route, null, sw.ElapsedMilliseconds, false, e.Message);
        }
    }

    /// <summary>HttpClient that does not follow redirects, so a redirect to an error page is not hidden.</summary>
    public static HttpClient CreateDefaultClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        DefaultRequestHeaders = { { "User-Agent", "Preflight-SmokeTest/1.0" } },
    };
}
