using ServeurIa.Core.Web;
using ServeurIa.Modules.Search.Providers;

namespace ServeurIa.Modules.Search;

/// <summary>A failure the caller can fix or must know about; <see cref="Status"/> is the HTTP status.</summary>
public sealed class SearchException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;

    /// <summary>The 503 of a feature whose companion is not installed or not reachable.</summary>
    public static SearchException Unavailable(string message) => new(503, message);
}

/// <summary>Turns the failures of a handler into ProblemDetails.</summary>
public static class SearchErrors
{
    /// <summary>The HTTP status and message a failure maps to; null for what is not a Search failure (the host answers 500).</summary>
    public static (int Status, string Message)? Describe(Exception ex) => ex switch
    {
        SearchException e => (e.Status, e.Message),
        SearchProviderException e => (502, e.Message),
        WebFetchException e => (e.Reason switch
        {
            WebFetchFailure.InvalidUrl or WebFetchFailure.BlockedScheme or WebFetchFailure.BlockedPort
                or WebFetchFailure.BlockedAddress or WebFetchFailure.Credentials or WebFetchFailure.ContentType => 400,
            WebFetchFailure.RobotsDisallowed => 403,
            WebFetchFailure.RateLimited => 429,
            WebFetchFailure.TooLarge => 413,
            WebFetchFailure.Timeout => 504,
            _ => 502,
        }, e.Message),
        _ => null,
    };
}
