namespace Optimisarr.Tests;

public sealed class TestPidFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pid file " + Guid.NewGuid());
    private string Pid => Path.Combine(_root, "pid");

    public TestPidFileTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Wait_continues_until_the_id_is_written_after_the_file_appears()
    {
        await File.WriteAllTextAsync(Pid, "");
        var waiting = TestPidFile.WaitAsync(Pid, TimeSpan.FromSeconds(10));
        await Task.Delay(40);
        Assert.False(waiting.IsCompleted);
        await File.WriteAllTextAsync(Pid, "123");
        Assert.Equal(123, await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12x")]
    [InlineData("0")]
    public void Try_read_treats_an_incomplete_or_invalid_id_as_unpublished(string contents)
    {
        File.WriteAllText(Pid, contents);
        Assert.Null(TestPidFile.TryRead(Pid));
    }

    [Fact]
    public void Try_read_treats_a_missing_file_as_unpublished() => Assert.Null(TestPidFile.TryRead(Pid));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
