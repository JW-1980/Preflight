using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Preflight.Core.Ai;

public interface IAiClient
{
    /// <summary>Returns the model's JSON object answer. Input must already be redacted.</summary>
    Task<JsonObject> CompleteJsonAsync(string system, string user, CancellationToken ct = default);
}

/// <summary>DeepSeek chat completions (OpenAI-compatible) in JSON mode.</summary>
public sealed class DeepSeekClient(HttpClient http, string apiKey, string model = "deepseek-chat",
    string endpoint = "https://api.deepseek.com/chat/completions") : IAiClient
{
    public static DeepSeekClient? FromEnvironment(HttpClient http)
    {
        var key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        return string.IsNullOrWhiteSpace(key) ? null : new DeepSeekClient(http, key);
    }

    public async Task<JsonObject> CompleteJsonAsync(string system, string user, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                model,
                temperature = 0,
                response_format = new { type = "json_object" },
                messages = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = Redactor.Redact(user) }, // defence in depth
                },
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"DeepSeek API returned {(int)resp.StatusCode}: {Truncate(body)}");
        var content = JsonNode.Parse(body)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
            ?? throw new InvalidDataException("DeepSeek response has no message content");
        try
        {
            return JsonNode.Parse(content) as JsonObject
                ?? throw new InvalidDataException("DeepSeek answer is not a JSON object");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"DeepSeek answer is not valid JSON: {e.Message}");
        }
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
