namespace ServeurIa.Core.Llm;

/// <summary>
/// How a completion is sampled and shaped. Everything is optional; the default is the engine's own behaviour.
/// There is no JSON field in the engine contract (<c>llm.proto</c>): <see cref="Json"/> asks the model for JSON with an
/// instruction, and the caller checks the answer (see <see cref="LlmJson"/>).
/// </summary>
public sealed record LlmOptions
{
    /// <summary>Sampling temperature; null leaves the engine default.</summary>
    public float? Temperature { get; init; }

    /// <summary>The answer must be one JSON object: an instruction is added to the conversation.</summary>
    public bool Json { get; init; }

    /// <summary>No thinking phase (reasoning effort <c>none</c>): a short structured answer does not need one.</summary>
    public bool ReasoningOff { get; init; }

    /// <summary>When no model is named, reuse a ready model before considering the configured default.</summary>
    public bool PreferReadyModel { get; init; }

    /// <summary>The generation stops before any of these strings.</summary>
    public IReadOnlyList<string>? Stop { get; init; }
}

public sealed class LlmUnavailableException(string message) : Exception(message);

public interface ILlmClient
{
    void Validate();

    IAsyncEnumerable<string> CompleteAsync(Guid accountId, string? model,
        IReadOnlyList<(string role, string text)> messages, int? maxTokens = null,
        LlmOptions? options = null, CancellationToken ct = default);
}
