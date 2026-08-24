using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

/// <summary>
/// Pairing is the moment an unknown machine becomes trusted to receive media, so the PIN must be
/// hard to guess inside its short life and must burn itself rather than allow indefinite attempts.
/// A human types this code, so it is deliberately short — which makes the attempt cap, not the
/// length, the thing that actually stops a brute force.
/// </summary>
public class WorkerPairingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Issue_produces_a_code_of_the_advertised_length_and_only_digits()
    {
        var code = PairingCode.Issue(Now);

        Assert.Equal(PairingCode.Digits, code.Code.Length);
        Assert.All(code.Code, c => Assert.True(char.IsAsciiDigit(c)));
    }

    [Fact]
    public void Issue_expires_the_code_after_the_pairing_window()
    {
        var code = PairingCode.Issue(Now);

        Assert.Equal(Now, code.IssuedUtc);
        Assert.Equal(Now + PairingCode.Lifetime, code.ExpiresUtc);
        Assert.False(code.Redeemed);
        Assert.Equal(0, code.FailedAttempts);
    }

    [Fact]
    public void Issue_does_not_repeat_itself_across_many_codes()
    {
        // Not a statistical proof, but a wired-constant or clock-seeded generator would collapse
        // this set immediately.
        var codes = Enumerable.Range(0, 200).Select(_ => PairingCode.Issue(Now).Code).ToHashSet();

        Assert.True(codes.Count > 190, $"Only {codes.Count} distinct codes in 200 draws.");
    }

    [Fact]
    public void Redeem_accepts_the_correct_code_and_marks_it_used()
    {
        var code = PairingCode.Issue(Now);

        var result = code.Redeem(code.Code, Now.AddSeconds(30));

        Assert.Equal(PairingRedemption.Accepted, result.Outcome);
        Assert.True(result.Code.Redeemed);
    }

    [Fact]
    public void Redeem_refuses_a_code_that_has_already_been_used()
    {
        var code = PairingCode.Issue(Now);
        var first = code.Redeem(code.Code, Now.AddSeconds(30));

        var second = first.Code.Redeem(code.Code, Now.AddSeconds(31));

        Assert.Equal(PairingRedemption.AlreadyRedeemed, second.Outcome);
    }

    [Fact]
    public void Redeem_refuses_an_expired_code_even_when_it_is_correct()
    {
        var code = PairingCode.Issue(Now);

        var result = code.Redeem(code.Code, code.ExpiresUtc);

        Assert.Equal(PairingRedemption.Expired, result.Outcome);
        Assert.False(result.Code.Redeemed);
    }

    [Fact]
    public void Redeem_counts_a_wrong_code_against_the_attempt_cap()
    {
        var code = PairingCode.Issue(Now);

        var result = code.Redeem(WrongCodeFor(code), Now.AddSeconds(1));

        Assert.Equal(PairingRedemption.IncorrectCode, result.Outcome);
        Assert.Equal(1, result.Code.FailedAttempts);
        Assert.False(result.Code.Redeemed);
    }

    [Fact]
    public void Redeem_burns_the_code_once_the_attempt_cap_is_reached()
    {
        var code = PairingCode.Issue(Now);
        var wrong = WrongCodeFor(code);

        for (var i = 0; i < PairingCode.MaxAttempts; i++)
        {
            code = code.Redeem(wrong, Now.AddSeconds(1)).Code;
        }

        // Even the genuine code must now be refused: a burned code is dead, not merely throttled.
        var result = code.Redeem(code.Code, Now.AddSeconds(2));

        Assert.Equal(PairingRedemption.TooManyAttempts, result.Outcome);
        Assert.False(result.Code.Redeemed);
    }

    [Fact]
    public void Redeem_treats_a_malformed_attempt_as_a_spent_attempt()
    {
        // Otherwise probing the format is free and the cap only limits well-formed guesses.
        var code = PairingCode.Issue(Now);

        var result = code.Redeem("nonsense", Now.AddSeconds(1));

        Assert.Equal(PairingRedemption.IncorrectCode, result.Outcome);
        Assert.Equal(1, result.Code.FailedAttempts);
    }

    [Fact]
    public void Redeem_ignores_spacing_the_operator_may_have_typed()
    {
        var code = PairingCode.Issue(Now);
        var spaced = code.Code[..4] + " " + code.Code[4..];

        var result = code.Redeem(spaced, Now.AddSeconds(5));

        Assert.Equal(PairingRedemption.Accepted, result.Outcome);
    }

    [Fact]
    public void MaxAttempts_keeps_a_brute_force_far_below_the_code_space()
    {
        // The whole security argument: a short human-typed PIN is safe only because the attempt
        // budget is a vanishing fraction of the space, inside a short window.
        var space = Math.Pow(10, PairingCode.Digits);

        Assert.True(PairingCode.MaxAttempts / space < 0.0001);
        Assert.True(PairingCode.Lifetime <= TimeSpan.FromMinutes(15));
    }

    private static string WrongCodeFor(PairingCode code) =>
        new(code.Code.Select(c => c == '0' ? '1' : '0').ToArray());
}

/// <summary>
/// The credential a paired worker keeps. Only its fingerprint is ever stored, so a leaked database
/// does not yield usable worker credentials, and revocation is simply forgetting the fingerprint.
/// </summary>
public class WorkerCredentialTests
{
    [Fact]
    public void Issue_produces_a_secret_with_real_entropy()
    {
        var secrets = Enumerable.Range(0, 100).Select(_ => WorkerCredential.Issue()).ToHashSet();

        Assert.Equal(100, secrets.Count);
        Assert.All(secrets, s => Assert.True(s.Length >= 32, $"Secret too short: {s.Length}"));
    }

    [Fact]
    public void Fingerprint_is_stable_for_the_same_secret()
    {
        var secret = WorkerCredential.Issue();

        Assert.Equal(WorkerCredential.Fingerprint(secret), WorkerCredential.Fingerprint(secret));
    }

    [Fact]
    public void Fingerprint_does_not_contain_the_secret()
    {
        var secret = WorkerCredential.Issue();

        Assert.DoesNotContain(secret, WorkerCredential.Fingerprint(secret), StringComparison.Ordinal);
    }

    [Fact]
    public void Matches_accepts_the_issued_secret()
    {
        var secret = WorkerCredential.Issue();

        Assert.True(WorkerCredential.Matches(secret, WorkerCredential.Fingerprint(secret)));
    }

    [Fact]
    public void Matches_rejects_a_different_secret()
    {
        var stored = WorkerCredential.Fingerprint(WorkerCredential.Issue());

        Assert.False(WorkerCredential.Matches(WorkerCredential.Issue(), stored));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Matches_rejects_an_empty_secret(string supplied)
    {
        var stored = WorkerCredential.Fingerprint(WorkerCredential.Issue());

        Assert.False(WorkerCredential.Matches(supplied, stored));
    }

    [Fact]
    public void Matches_rejects_anything_when_no_fingerprint_is_stored()
    {
        // A revoked worker's fingerprint is cleared, so this is the revocation path.
        Assert.False(WorkerCredential.Matches(WorkerCredential.Issue(), ""));
    }
}
