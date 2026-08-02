using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MowIT.ScheduleServer.Auth;

public static class RobotAuth
{
    public const string Scheme   = "RobotToken";
    public const string RobotIdClaim = "robot_id";
}

public sealed class RobotTokenAuthOptions : AuthenticationSchemeOptions { }

public sealed class RobotTokenAuthHandler : AuthenticationHandler<RobotTokenAuthOptions>
{
    private readonly IRobotTokenStore _tokens;

    public RobotTokenAuthHandler(
        IOptionsMonitor<RobotTokenAuthOptions> opts,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IRobotTokenStore tokens)
        : base(opts, logger, encoder)
    {
        _tokens = tokens;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var values))
            return Task.FromResult(AuthenticateResult.NoResult());

        var raw = values.ToString();
        if (string.IsNullOrWhiteSpace(raw) || !raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        var token = raw["Bearer ".Length..].Trim();
        var robotId = _tokens.Resolve(token);

        if (robotId is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid robot token"));

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(RobotAuth.RobotIdClaim, robotId),
            new Claim(ClaimTypes.NameIdentifier, robotId)
        }, RobotAuth.Scheme);

        var principal = new ClaimsPrincipal(identity);
        var ticket    = new AuthenticationTicket(principal, RobotAuth.Scheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
