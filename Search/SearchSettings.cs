namespace ServeurIa.Modules.Search;

public sealed record SearchSettings
{
    public bool RespectRobots { get; init; } = true;
    public int RequestsPerMinute { get; init; } = 120;
    public int HostIntervalMs { get; init; } = 1000;
    public bool BrowserEnabled { get; init; } = true;
}
