namespace MowIT.Infrastructure.ScheduleSync;

public sealed class ScheduleSyncOptions
{
    public string BaseUrl { get; init; } = string.Empty;

    public string RobotId { get; init; } = string.Empty;

    public string BearerToken { get; init; } = string.Empty;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(RobotId)
        && !string.IsNullOrWhiteSpace(BearerToken);
}
