using Optimisarr.Api.Workers;
using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

/// <summary>
/// The service is what makes the attempt cap real. <see cref="PairingCode"/> is immutable, so
/// without storing the returned state every request would restart from zero failed attempts and
/// the cap would never be reached — the single most important behaviour to pin down here.
/// </summary>
public class WorkerPairingServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Redeem_refuses_when_no_code_has_been_issued()
    {
        var service = new WorkerPairingService();

        Assert.Equal(PairingRedemption.IncorrectCode, service.Redeem("12345678", Now));
    }

    [Fact]
    public void Redeem_accepts_the_issued_code()
    {
        var service = new WorkerPairingService();
        var code = service.Issue(Now);

        Assert.Equal(PairingRedemption.Accepted, service.Redeem(code.Code, Now.AddSeconds(10)));
    }

    [Fact]
    public void Failed_attempts_accumulate_across_calls_and_reach_the_cap()
    {
        var service = new WorkerPairingService();
        var code = service.Issue(Now);

        for (var i = 0; i < PairingCode.MaxAttempts; i++)
        {
            Assert.Equal(PairingRedemption.IncorrectCode, service.Redeem("00000000", Now.AddSeconds(1)));
        }

        // The genuine code must now fail too. If state were not stored, this would return Accepted.
        Assert.Equal(PairingRedemption.TooManyAttempts, service.Redeem(code.Code, Now.AddSeconds(2)));
    }

    [Fact]
    public void A_code_cannot_be_redeemed_twice()
    {
        var service = new WorkerPairingService();
        var code = service.Issue(Now);

        Assert.Equal(PairingRedemption.Accepted, service.Redeem(code.Code, Now.AddSeconds(1)));
        Assert.Equal(PairingRedemption.AlreadyRedeemed, service.Redeem(code.Code, Now.AddSeconds(2)));
    }

    [Fact]
    public void Issuing_a_new_code_invalidates_the_previous_one()
    {
        var service = new WorkerPairingService();
        var first = service.Issue(Now);
        service.Issue(Now.AddSeconds(5));

        Assert.Equal(PairingRedemption.IncorrectCode, service.Redeem(first.Code, Now.AddSeconds(6)));
    }

    [Fact]
    public void Active_hides_a_code_that_is_spent_expired_or_burned()
    {
        var service = new WorkerPairingService();

        var code = service.Issue(Now);
        Assert.NotNull(service.Active(Now.AddSeconds(1)));

        Assert.Null(service.Active(code.ExpiresUtc));

        service.Issue(Now);
        service.Redeem(service.Active(Now)!.Code, Now.AddSeconds(1));
        Assert.Null(service.Active(Now.AddSeconds(2)));

        service.Issue(Now);
        for (var i = 0; i < PairingCode.MaxAttempts; i++)
        {
            service.Redeem("00000000", Now.AddSeconds(1));
        }

        Assert.Null(service.Active(Now.AddSeconds(2)));
    }

    [Fact]
    public void Cancel_withdraws_the_active_code()
    {
        var service = new WorkerPairingService();
        var code = service.Issue(Now);

        service.Cancel();

        Assert.Null(service.Active(Now.AddSeconds(1)));
        Assert.Equal(PairingRedemption.IncorrectCode, service.Redeem(code.Code, Now.AddSeconds(1)));
    }

    [Fact]
    public void Active_reports_the_remaining_attempts_shrinking()
    {
        var service = new WorkerPairingService();
        service.Issue(Now);

        service.Redeem("00000000", Now.AddSeconds(1));

        Assert.Equal(1, service.Active(Now.AddSeconds(2))!.FailedAttempts);
    }
}
