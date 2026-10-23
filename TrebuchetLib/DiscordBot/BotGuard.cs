namespace TrebuchetLib.DiscordBot;

public enum BotGuardOutcome
{
    /// <summary>Do it.</summary>
    Allowed,
    /// <summary>Refused: answer with <see cref="BotDecision.PublicMessage"/>.</summary>
    Denied,
    /// <summary>Allowed in principle, but too fast or too often: tell the user to wait.</summary>
    Throttled,
    /// <summary>A locked out user or a repeated interaction: no answer at all.</summary>
    Ignored
}

public readonly record struct BotGuardResult(
    BotGuardOutcome Outcome,
    BotDenyReason Reason = BotDenyReason.None,
    BotThrottle Throttle = BotThrottle.None,
    bool LockedOutNow = false)
{
    public bool Allowed => Outcome == BotGuardOutcome.Allowed;
}

/// <summary>
/// The whole gate in front of the launcher: double check (<see cref="BotAuthorizer"/>), then wait and limits
/// (<see cref="BotRateLimiter"/>). Whatever the bot does goes through <see cref="Evaluate"/> first.
/// </summary>
public sealed class BotGuard(BotRateLimiter limiter, Func<DateTime>? clock = null)
{
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);

    public BotRateLimiter Limiter { get; } = limiter;

    /// <param name="instance">The server instance the request is about, for the limit of critical actions.</param>
    /// <param name="followUp">The click on a confirmation or selection the bot offered itself.</param>
    public BotGuardResult Evaluate(BotPolicy? policy, BotRequest request, int? instance, bool followUp = false)
    {
        try
        {
            if (!Limiter.TryMarkInteraction(request.InteractionId))
                return new BotGuardResult(BotGuardOutcome.Ignored);
            if (policy is null)
                return new BotGuardResult(BotGuardOutcome.Denied, BotDenyReason.NoPolicy);

            var now = _clock();
            if (Limiter.IsLockedOut(request.UserId, now))
                return new BotGuardResult(BotGuardOutcome.Ignored);

            var decision = BotAuthorizer.Authorize(policy, request);
            if (!decision.Allowed)
            {
                var lockedOut = Limiter.RegisterDenied(request.UserId, now, policy);
                return new BotGuardResult(BotGuardOutcome.Denied, decision.Reason, LockedOutNow: lockedOut);
            }

            var throttle = Limiter.TryAcquire(request.UserId, request.Command, instance, followUp, now, policy);
            return throttle == BotThrottle.None
                ? new BotGuardResult(BotGuardOutcome.Allowed)
                : new BotGuardResult(BotGuardOutcome.Throttled, Throttle: throttle);
        }
        catch (Exception)
        {
            return new BotGuardResult(BotGuardOutcome.Denied, BotDenyReason.Error);
        }
    }
}
