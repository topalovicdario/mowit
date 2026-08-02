using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace MowIT.ScheduleServer.Auth;

public sealed class RobotTokenOptions
{
    public Dictionary<string, string> Tokens { get; init; } = new();
}

public interface IRobotTokenStore
{
    string? Resolve(string token);
}

public sealed class InMemoryRobotTokenStore : IRobotTokenStore
{
    private readonly Dictionary<string, string> _tokenToRobot;

    public InMemoryRobotTokenStore(IOptions<RobotTokenOptions> opts)
    {
        _tokenToRobot = opts.Value.Tokens
            .ToDictionary(kvp => kvp.Value, kvp => kvp.Key, StringComparer.Ordinal);
    }

    public string? Resolve(string token)
    {
        foreach (var (storedToken, robotId) in _tokenToRobot)
        {
            if (CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(storedToken),
                    System.Text.Encoding.UTF8.GetBytes(token)))
            {
                return robotId;
            }
        }
        return null;
    }
}
