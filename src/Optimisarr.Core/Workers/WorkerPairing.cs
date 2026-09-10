using System.Security.Cryptography;
using System.Text;

namespace Optimisarr.Core.Workers;

/// <summary>Why a pairing attempt was accepted or refused.</summary>
public enum PairingRedemption
{
    Accepted,
    IncorrectCode,
    Expired,
    AlreadyRedeemed,
    TooManyAttempts
}

/// <summary>The outcome plus the code's new state, since redemption always consumes something.</summary>
public sealed record PairingResult(PairingRedemption Outcome, PairingCode Code);

/// <summary>
/// A short-lived, single-use PIN the operator reads from Optimisarr and types into a sidecar,
/// alongside this server's URL. The PIN is deliberately short enough to type, so its security
/// rests on three things together and not on length: a short life, single use, and a hard attempt
/// cap that burns the code rather than throttling it.
/// </summary>
public sealed record PairingCode(
    string Code,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc,
    int FailedAttempts,
    bool Redeemed)
{
    /// <summary>Digits in a PIN. Long enough that the attempt cap is a vanishing slice of the space.</summary>
    public const int Digits = 8;

    /// <summary>Wrong guesses before the code is dead — not merely delayed.</summary>
    public const int MaxAttempts = 5;

    /// <summary>How long a displayed PIN stays usable.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>Mints a fresh PIN from a cryptographic generator.</summary>
    public static PairingCode Issue(DateTimeOffset nowUtc)
    {
        var digits = new char[Digits];
        for (var i = 0; i < Digits; i++)
        {
            digits[i] = (char)('0' + RandomNumberGenerator.GetInt32(0, 10));
        }

        return new PairingCode(new string(digits), nowUtc, nowUtc + Lifetime, 0, false);
    }

    /// <summary>
    /// Checks a typed PIN and returns the code's new state. Order matters: a dead code (used,
    /// expired, or burned) is reported as such without comparing, so a caller cannot learn
    /// anything by racing a spent code.
    /// </summary>
    public PairingResult Redeem(string supplied, DateTimeOffset nowUtc)
    {
        if (Redeemed)
        {
            return new PairingResult(PairingRedemption.AlreadyRedeemed, this);
        }

        if (FailedAttempts >= MaxAttempts)
        {
            return new PairingResult(PairingRedemption.TooManyAttempts, this);
        }

        if (nowUtc >= ExpiresUtc)
        {
            return new PairingResult(PairingRedemption.Expired, this);
        }

        // Operators read the PIN in groups, so tolerate the spacing they type. Everything else
        // counts as a wrong guess: probing the format must not be cheaper than guessing digits.
        var normalised = new string(supplied.Where(char.IsAsciiDigit).ToArray());

        if (normalised.Length == Digits && FixedTimeEquals(normalised, Code))
        {
            return new PairingResult(PairingRedemption.Accepted, this with { Redeemed = true });
        }

        return new PairingResult(
            PairingRedemption.IncorrectCode,
            this with { FailedAttempts = FailedAttempts + 1 });
    }

    private static bool FixedTimeEquals(string supplied, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}

/// <summary>
/// The long-lived secret a paired worker presents on every later call. Optimisarr stores only the
/// fingerprint, so a leaked database yields no usable credential, and revoking a worker is simply
/// discarding its fingerprint.
/// </summary>
public static class WorkerCredential
{
    private const int SecretBytes = 32;

    /// <summary>Mints a credential. Returned to the worker once and never recoverable afterwards.</summary>
    public static string Issue() => Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>The stored form of a credential.</summary>
    public static string Fingerprint(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>
    /// Constant-time check of a presented secret against a stored fingerprint. An absent
    /// fingerprint matches nothing, which is what makes revocation total.
    /// </summary>
    public static bool Matches(string supplied, string storedFingerprint)
    {
        if (string.IsNullOrWhiteSpace(supplied) || string.IsNullOrWhiteSpace(storedFingerprint))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(Fingerprint(supplied))),
            SHA256.HashData(Encoding.UTF8.GetBytes(storedFingerprint)));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
