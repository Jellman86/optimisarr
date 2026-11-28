using System.Net;
using System.Runtime.Versioning;
using System.Text;
using Optimisarr.Sidecar.Core.Capabilities;
using Optimisarr.Sidecar.Core.Session;
using Optimisarr.Sidecar.Service;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// The service's own wiring fails closed: a work location or work folder that cannot be used gives
/// a session that keeps checking in but reports no room and no slots, and never asks for a job.
/// Runs on every platform; nothing here touches Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProgramBuildTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("optimisarr-build-").FullName;

    public void Dispose() => Directory.Delete(_scratch, recursive: true);

    private const string Beat =
        """{"workerId":7,"protocolVersion":1,"serverTimeUtc":"2026-11-27T20:00:00Z","heartbeatIntervalSeconds":30,"draining":false}""";

    private const string AssignmentJson = """
        {"leaseId":"6f9619ff-8b86-d011-b42d-00c04fc964ff","jobId":41,"title":"T","sourceBytes":1,
         "videoEncoder":"hevc_nvenc","vmaf":"Cpu","expiresUtc":"2026-11-27T21:00:00Z",
         "renewWithinSeconds":30,"arguments":["-i","INPUT","OUTPUT.mkv"],
         "outputExtension":"mkv",
         "quality":{"measure":false,"model":"m","frameSubsample":1,"clipVmaf":false,
                    "minimumHarmonicMean":0,"minimumMinimum":0,"commands":[]}}
        """;

    private sealed class Server(string? claimReply = null) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<long> FreeScratch { get; } = [];
        public List<int> Slots { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            if (path.EndsWith("/heartbeat", StringComparison.Ordinal))
            {
                using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                FreeScratch.Add(body.RootElement.GetProperty("freeScratchBytes").GetInt64());
                Slots.Add(body.RootElement.GetProperty("maxConcurrency").GetInt32());
                return Json(Beat);
            }

            if (path.EndsWith("/claim", StringComparison.Ordinal))
                return claimReply is null ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json(claimReply);

            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static Program.BuildSeams Seams(Server server, int beats = 2)
    {
        var made = 0;
        return new(
            new SidecarClient(new HttpClient(server)),
            new InMemoryCredentialStore(new StoredPairing("https://server.example.com", "good", 7)),
            ProveEncoders: (free, slots, _) => Task.FromResult(new SidecarCapabilities(
                "PICARD", "windows", "x64", ["hevc_nvenc"], ["aac"], [], VmafCapability.Cuda, free, slots)),
            Load: () => null,
            Delay: (_, _) => ++made >= beats ? throw new OperationCanceledException() : Task.CompletedTask);
    }

    private static async Task Run(SidecarSession session)
    {
        try { await session.RunAsync(CancellationToken.None); }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task A_refused_work_location_never_claims_and_reports_no_room()
    {
        var server = new Server(AssignmentJson);
        var refused = new WorkLocationChoice(null, WorkLocationSource.OverrideFile, "The work-location file cannot be used.");

        await Run(Program.Build(refused, workFolderProblem: () => null, out _, seams: Seams(server)));

        Assert.NotEmpty(server.FreeScratch);
        Assert.All(server.FreeScratch, free => Assert.Equal(0, free));
        Assert.All(server.Slots, slots => Assert.Equal(0, slots));
        Assert.DoesNotContain(server.Requests, path => path.EndsWith("/claim", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_work_folder_never_claims_and_reports_no_room()
    {
        var server = new Server(AssignmentJson);
        var usable = new WorkLocationChoice(_scratch, WorkLocationSource.Default, null);

        await Run(Program.Build(usable, workFolderProblem: () => "Owned by somebody else.", out _, seams: Seams(server)));

        Assert.NotEmpty(server.FreeScratch);
        Assert.All(server.FreeScratch, free => Assert.Equal(0, free));
        Assert.All(server.Slots, slots => Assert.Equal(0, slots));
        Assert.DoesNotContain(server.Requests, path => path.EndsWith("/claim", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_work_folder_refusal_is_logged_once_in_the_start_up_lines_words()
    {
        var server = new Server();
        var usable = new WorkLocationChoice(_scratch, WorkLocationSource.Default, null);
        var warnings = new List<string>();

        await Run(Program.Build(usable, workFolderProblem: () => "Owned by somebody else.", out _, warn: warnings.Add,
            seams: Seams(server, beats: 3)));

        Assert.Equal([WorkLocation.Describe(usable, memoryBacked: null, workFolderProblem: "Owned by somebody else.")],
            warnings.Where(line => line.Contains("Owned by somebody else.", StringComparison.Ordinal)));
        Assert.EndsWith("This worker takes no jobs until it is corrected.", warnings.Single());
    }

    [Fact]
    public async Task A_work_folder_refusal_the_start_up_line_already_reported_is_not_logged_again()
    {
        var server = new Server();
        var usable = new WorkLocationChoice(_scratch, WorkLocationSource.Default, null);
        var warnings = new List<string>();

        await Run(Program.Build(usable, workFolderProblem: () => "Owned by somebody else.", out _, warn: warnings.Add,
            seams: Seams(server, beats: 3), reportedWorkFolderProblem: "Owned by somebody else."));

        Assert.Empty(warnings);
    }

    [Fact]
    public async Task A_work_folder_refusal_that_differs_from_the_start_up_one_is_still_logged()
    {
        var server = new Server();
        var usable = new WorkLocationChoice(_scratch, WorkLocationSource.Default, null);
        var warnings = new List<string>();

        await Run(Program.Build(usable, workFolderProblem: () => "It became a junction.", out _, warn: warnings.Add,
            seams: Seams(server), reportedWorkFolderProblem: "Owned by somebody else."));

        Assert.Contains(warnings, line => line.Contains("It became a junction.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_usable_work_folder_offers_its_slots_and_asks_for_work()
    {
        // The control for the two above: without it they could pass because nothing ever claims.
        var server = new Server();
        var usable = new WorkLocationChoice(_scratch, WorkLocationSource.Default, null);

        await Run(Program.Build(usable, workFolderProblem: () => null, out _, seams: Seams(server)));

        Assert.All(server.Slots, slots => Assert.Equal(1, slots));
        Assert.Contains(server.Requests, path => path.EndsWith("/claim", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_work_folder_that_fails_its_check_before_a_job_hands_that_job_back_unstarted()
    {
        var server = new Server(AssignmentJson);
        var usable = new WorkLocationChoice(_scratch, WorkLocationSource.Default, null);
        var checks = 0;
        var warnings = new List<string>();

        // Sound for the start-up probe and the first check-in, refused by the time a job arrives.
        var session = Program.Build(usable, workFolderProblem: () => ++checks > 2 ? "It became a junction." : null, out _,
            warn: warnings.Add, seams: Seams(server, beats: 3));
        await Run(session);
        for (var waited = 0; session.ActiveJobCount > 0 && waited < 100; waited++) await Task.Delay(20);

        Assert.Contains(server.Requests, path => path.EndsWith("/capacity-unavailable", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Requests, path => path.EndsWith("/source", StringComparison.Ordinal));
        Assert.Contains(warnings, line => line.Contains("It became a junction."));
    }
}
