namespace Optimisarr.Tests;

/// <summary>
/// Reads the process id a fake tool publishes for a process-lifetime test. A fixture creates the
/// file before it writes the digits, so a reader that only waits for the file to exist can read
/// an empty or partial id and fail with a format error that says nothing about the behaviour
/// under test. Every reader waits for, or tolerates, an incomplete id instead.
/// </summary>
public static class TestPidFile
{
    public static int? TryRead(string path)
    {
        try
        {
            return int.TryParse(File.ReadAllText(path), out var pid) && pid > 0 ? pid : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static async Task<int> WaitAsync(string path, TimeSpan timeout, CancellationToken token = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (TryRead(path) is { } pid) return pid;
            await Task.Delay(20, token);
        }
        Assert.Fail($"The fixture did not publish a complete process id at {path} within {timeout}.");
        return 0;
    }
}
