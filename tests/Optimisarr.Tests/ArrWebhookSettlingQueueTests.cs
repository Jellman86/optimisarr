using Optimisarr.Api.Library;

namespace Optimisarr.Tests;

public sealed class ArrWebhookSettlingQueueTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Two_spellings_of_the_same_local_file_wait_as_one()
    {
        var settling = new ArrWebhookSettlingQueue();

        settling.Schedule(new SettlingWebhookFile(1, @"D:\TV\Show\S01E01.mkv", "/data/tv/Show/S01E01.mkv", Now, 1));
        settling.Schedule(new SettlingWebhookFile(1, "D:/TV/Show/S01E01.mkv", "/data/tv/Show/S01E01.mkv", Now.AddSeconds(30), 2));

        var due = Assert.Single(settling.TakeDue(Now.AddMinutes(1)));
        Assert.Equal(2, due.Attempts);
    }

    [Fact]
    public void The_same_file_from_two_connections_waits_once_for_each()
    {
        var settling = new ArrWebhookSettlingQueue();

        settling.Schedule(new SettlingWebhookFile(1, "/tv/S01E01.mkv", "/data/tv/S01E01.mkv", Now, 1));
        settling.Schedule(new SettlingWebhookFile(2, "/tv/S01E01.mkv", "/data/tv/S01E01.mkv", Now, 1));

        Assert.Equal(2, settling.TakeDue(Now).Count);
    }
}
