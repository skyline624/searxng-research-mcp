using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ServeurIa.Core.Llm;

namespace SearxngResearchMcp;

public sealed class OpenAiOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:1234/v1";
    public string? Model { get; set; }
    public string? AnalysisModel { get; set; }
    public string? AnswerModel { get; set; }
    public string ApiKeyEnv { get; set; } = "OPENAI_API_KEY";
    public int TimeoutSeconds { get; set; } = 120;
    public string TokenLimitField { get; set; } = "max_tokens";
    public bool UseJsonResponseFormat { get; set; }
}

public sealed class OpenAiClient(HttpClient http, IOptions<OpenAiOptions> options) : ILlmClient
{
    public void Validate()
    {
        var local = options.Value;
        if (!Uri.TryCreate(local.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var url) ||
            url.Scheme is not ("http" or "https") || url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0)
        {
            throw new LlmUnavailableException("OpenAI:BaseUrl must be an HTTP(S) API base URL, for example http://127.0.0.1:1234/v1.");
        }

        if (string.IsNullOrWhiteSpace(local.AnalysisModel ?? local.Model) ||
            string.IsNullOrWhiteSpace(local.AnswerModel ?? local.Model))
        {
            throw new LlmUnavailableException("Set OpenAI:Model or both OpenAI:AnalysisModel and OpenAI:AnswerModel.");
        }

        if (local.TimeoutSeconds is < 1 or > 900 ||
            local.TokenLimitField is not ("max_tokens" or "max_completion_tokens"))
        {
            throw new LlmUnavailableException("Check OpenAI:TimeoutSeconds (1–900) and OpenAI:TokenLimitField.");
        }
    }

    public async IAsyncEnumerable<string> CompleteAsync(
        Guid accountId, string? model, IReadOnlyList<(string role, string text)> messages,
        int? maxTokens = null, LlmOptions? sampling = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Validate();
        var local = options.Value;
        var baseUrl = new Uri(local.BaseUrl.TrimEnd('/') + "/");
        var json = sampling?.Json == true;
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model ?? (json ? local.AnalysisModel : local.AnswerModel) ?? local.Model,
            ["messages"] = messages.Select(message => new { role = message.role, content = message.text }),
            ["stream"] = false,
        };
        if (maxTokens.HasValue)
        {
            payload[local.TokenLimitField] = maxTokens.Value;
        }

        if (json && local.UseJsonResponseFormat)
        {
            payload["response_format"] = new { type = "json_object" };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, "chat/completions"))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(local.ApiKeyEnv) &&
            Environment.GetEnvironmentVariable(local.ApiKeyEnv) is { Length: > 0 } key)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(local.TimeoutSeconds));
        string? answer;
        try
        {
            using var response = await http.SendAsync(request, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new LlmUnavailableException($"The OpenAI-compatible endpoint answered HTTP {(int)response.StatusCode}.");
            }

            var result = await response.Content.ReadFromJsonAsync<ChatCompletion>(deadline.Token);
            answer = result?.Choices?.FirstOrDefault()?.Message?.Content;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmUnavailableException("The OpenAI-compatible endpoint timed out.");
        }
        catch (HttpRequestException)
        {
            throw new LlmUnavailableException("The OpenAI-compatible endpoint could not be reached.");
        }
        catch (JsonException)
        {
            throw new LlmUnavailableException("The OpenAI-compatible endpoint returned invalid Chat Completions JSON.");
        }

        if (string.IsNullOrWhiteSpace(answer))
        {
            throw new LlmUnavailableException("The OpenAI-compatible endpoint returned no answer text.");
        }

        yield return answer;
    }

    private sealed record ChatCompletion(Choice[]? Choices);
    private sealed record Choice(Message? Message);
    private sealed record Message(string? Content);
}
