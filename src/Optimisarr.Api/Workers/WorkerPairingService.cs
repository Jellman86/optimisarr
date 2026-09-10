using Optimisarr.Core.Workers;

namespace Optimisarr.Api.Workers;

/// <summary>
/// Holds the one pairing PIN an operator currently has on screen.
///
/// Deliberately in memory and never persisted: a PIN is a short-lived secret, so writing it to
/// the config database would give it a durability it should not have and would leave it in
/// backups. Losing the active PIN on restart is the correct behaviour — the operator simply asks
/// for another.
///
/// One code is live at a time. Issuing a new PIN replaces any previous one, so an abandoned code
/// cannot linger alongside the one being shown.
/// </summary>
public sealed class WorkerPairingService
{
    private readonly Lock _gate = new();
    private PairingCode? _active;

    /// <summary>Issues a fresh PIN, discarding any previous one.</summary>
    public PairingCode Issue(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            _active = PairingCode.Issue(nowUtc);
            return _active;
        }
    }

    /// <summary>
    /// The PIN currently worth showing, or null when there is none the operator could still use.
    /// A spent, burned, or expired code is reported as absent rather than displayed as live.
    /// </summary>
    public PairingCode? Active(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (_active is null || _active.Redeemed
                || _active.FailedAttempts >= PairingCode.MaxAttempts
                || nowUtc >= _active.ExpiresUtc)
            {
                return null;
            }

            return _active;
        }
    }

    /// <summary>Withdraws the active PIN immediately.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _active = null;
        }
    }

    /// <summary>
    /// Checks a typed PIN and stores the code's resulting state, so failed attempts accumulate
    /// across requests and actually reach the cap. Without persisting the new state here, every
    /// request would start from zero attempts and the cap would be meaningless.
    /// </summary>
    public PairingRedemption Redeem(string supplied, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (_active is null)
            {
                // Nothing on screen, so there is nothing to redeem. Reported as an incorrect code
                // rather than a distinct state: whether a PIN is currently displayed is not
                // something an unauthenticated caller needs to learn.
                return PairingRedemption.IncorrectCode;
            }

            var result = _active.Redeem(supplied, nowUtc);
            _active = result.Code;
            return result.Outcome;
        }
    }
}
