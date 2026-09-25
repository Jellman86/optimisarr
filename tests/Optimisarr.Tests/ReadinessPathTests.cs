namespace Optimisarr.Tests;

[Collection(TokenedApiCollection.Name)]
public sealed class ReadinessPathTests(AdminTokenAuthEndpointTests.TokenedApi api)
{
    [Fact]
    public async Task Readiness_checks_configured_work_and_quarantine_paths_instead_of_container_defaults()
    {
        var root = Directory.CreateTempSubdirectory("optimisarr-readiness-").FullName;
        var work = Path.Combine(root, "work");
        var trash = Path.Combine(root, "trash");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(trash);
        var oldWork = Environment.GetEnvironmentVariable("OPTIMISARR_WORK_DIR");
        var oldTrash = Environment.GetEnvironmentVariable("OPTIMISARR_TRASH_DIR");
        try
        {
            Environment.SetEnvironmentVariable("OPTIMISARR_WORK_DIR", work);
            Environment.SetEnvironmentVariable("OPTIMISARR_TRASH_DIR", trash);
            using var client = api.CreateClient();
            var body = await (await client.GetAsync("/api/ready")).Content.ReadAsStringAsync();
            Assert.DoesNotContain("required path is not writable", body);

            Directory.Delete(trash);
            body = await (await client.GetAsync("/api/ready")).Content.ReadAsStringAsync();
            Assert.Contains(trash, body);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPTIMISARR_WORK_DIR", oldWork);
            Environment.SetEnvironmentVariable("OPTIMISARR_TRASH_DIR", oldTrash);
            Directory.Delete(root, recursive: true);
        }
    }
}
