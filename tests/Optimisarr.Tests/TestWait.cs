using System.Diagnostics;

namespace Optimisarr.Tests;

/// <summary>
/// Polls a background operation against a wall-clock deadline. An iteration count assumes each
/// check and pause take a fixed time, which a loaded CI runner does not honour.
/// </summary>
public static class TestWait
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(60);

    /// <summary>True once the condition holds, false if the deadline passes first.</summary>
    public static async Task<bool> UntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var clock = Stopwatch.StartNew();
        var limit = timeout ?? Default;
        while (clock.Elapsed < limit)
        {
            if (await condition()) return true;
            await Task.Delay(20);
        }
        return await condition();
    }
}
