using System.Text.Json;
using Optimisarr.Sidecar.Core.Session;
using Optimisarr.Sidecar.Native;

namespace Optimisarr.Sidecar.Core.Tests;

[Collection("Native scratch ownership")]
public sealed class NativeHostRetirementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-host-retire-" + Guid.NewGuid().ToString("N"));
    private static readonly Guid Lease = Guid.Parse("81223344-1234-4321-8123-123456789012");
    private readonly Guid _owner = Guid.NewGuid();
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private NativeHostRequest Request(string mode = "retire") => new(2, mode, Path.GetFullPath("missing-ffmpeg"), _root,
        AccountedOwner: new(_owner, 12, Lease), AccountingScope: NativeScratchWorkspace.PrepareAccountingScope(_root));
    private static NativeHostRequest Parse(NativeHostRequest request) =>
        NativeHostProtocol.Parse(JsonSerializer.SerializeToUtf8Bytes(request, NativeHostProtocol.Json));

    [Fact]
    public async Task Version_two_targeted_retirement_returns_positive_evidence_and_consumes_only_after_acknowledgement()
    {
        var owner = NativeScratchWorkspace.CreateAccountedJob(_root, 12, Lease, _owner);
        owner.Dispose();
        var retired = await NativeHostExecutor.RunAsync(Parse(Request()), _ => Assert.Fail("Retirement cannot run media tools"), default);
        Assert.Equal(2, retired.Version);
        Assert.Equal("retired", retired.Type);
        Assert.True(retired.Retirement!.Retired);
        Assert.True(File.Exists(owner.RecordPath));
        var consumed = await NativeHostExecutor.RunAsync(Parse(Request("forget")), _ => Assert.Fail("Consumption cannot run media tools"), default);
        Assert.Equal(2, consumed.Version);
        Assert.Equal("forgotten", consumed.Type);
        Assert.True(consumed.Consumption!.Consumed);
        Assert.False(File.Exists(owner.RecordPath));
        Assert.True((await NativeHostExecutor.RunAsync(Parse(Request("forget")), _ => { }, default)).Consumption!.AlreadyAbsent);
        Assert.False((await NativeHostExecutor.RunAsync(Parse(Request()), _ => { }, default)).Retirement!.Retired);
    }

    [Fact]
    public async Task Busy_and_missing_expected_owners_cannot_return_retirement_success()
    {
        Assert.False((await NativeHostExecutor.RunAsync(Parse(Request()), _ => { }, default)).Retirement!.Retired);
        using var live = NativeScratchWorkspace.CreateAccountedJob(_root, 12, Lease, _owner);
        var busy = (await NativeHostExecutor.RunAsync(Parse(Request()), _ => { }, default)).Retirement!;
        Assert.True(busy.Busy);
        Assert.False(busy.Retired);
        Assert.False((await NativeHostExecutor.RunAsync(Parse(Request("forget")), _ => { }, default)).Consumption!.Consumed);
        Assert.True(live.Cleanup());
    }

    [Theory]
    [InlineData("retire")]
    [InlineData("forget")]
    public void Retirement_operations_require_version_two_and_no_media_or_network_authority(string mode)
    {
        var request = Request(mode);
        Assert.Throws<InvalidDataException>(() => Parse(request with { Version = 1 }));
        Assert.Throws<InvalidDataException>(() => Parse(request with { AccountedOwner = null }));
        Assert.Throws<InvalidDataException>(() => Parse(request with { AccountingScope = null }));
        Assert.Throws<InvalidDataException>(() => Parse(request with { AccountedOwner = request.AccountedOwner! with { Owner = Guid.Empty } }));
        Assert.Throws<InvalidDataException>(() => Parse(request with { Pairing = new("http://localhost", new('a', 43), 1) }));
        Assert.Throws<InvalidDataException>(() => Parse(request with { GrantedMemoryBytes = 1 }));
        Assert.Throws<InvalidDataException>(() => Parse(request with { Assignment = JsonSerializer.SerializeToElement(new { }) }));
        Assert.Throws<InvalidDataException>(() => Parse(request with { RecoveryAfterOwner = "owner-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.json" }));
    }

    [Fact]
    public void Accounted_run_identity_must_match_the_frozen_assignment_and_cannot_downgrade_to_version_one()
    {
        var assignment = PerceptualJobFixture.Assignment();
        var request = Request("run") with {
            Pairing = new("http://127.0.0.1:9999", new('a', 43), 1), GrantedMemoryBytes = 8L * 1024 * 1024 * 1024,
            Assignment = JsonSerializer.SerializeToElement(assignment, NativeHostProtocol.Json),
            AccountedOwner = new(_owner, assignment.JobId, assignment.LeaseId)
        };
        Assert.Equal(request.AccountedOwner, Parse(request).AccountedOwner);
        Assert.Throws<InvalidDataException>(() => Parse(request with { Version = 1 }));
        Assert.Throws<InvalidDataException>(() => Parse(request with { AccountedOwner = null }));
        Assert.Throws<InvalidDataException>(() => Parse(request with { AccountingScope = null }));
        Assert.Throws<InvalidDataException>(() => Parse(request with { AccountedOwner = request.AccountedOwner! with { JobId = assignment.JobId + 1 } }));
        Assert.Throws<InvalidDataException>(() => Parse(request with { AccountedOwner = request.AccountedOwner! with { LeaseId = Guid.NewGuid() } }));
    }

    [Fact]
    public async Task A_version_two_scope_is_prepared_without_media_tools_or_network_authority()
    {
        var request = new NativeHostRequest(2, "scope", Path.GetFullPath("missing-ffmpeg"), _root);
        var result = await NativeHostExecutor.RunAsync(Parse(request), _ => Assert.Fail("Scope preparation cannot run media"), default);
        Assert.Equal(2, result.Version);
        Assert.Equal("scope", result.Type);
        Assert.Equal(NativeScratchWorkspace.PrepareAccountingScope(_root), result.AccountingScope);
        Assert.Throws<InvalidDataException>(() => Parse(request with { Version = 1 }));
        Assert.Throws<InvalidDataException>(() => Parse(request with { AccountedOwner = new(_owner, 12, Lease) }));
        Assert.Throws<InvalidDataException>(() => Parse(request with { GrantedMemoryBytes = 1 }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid scope")]
    [InlineData("invalid\nidentity")]
    public void Scope_fields_must_be_bounded_unambiguous_physical_identifiers(string invalid)
    {
        var request = Request();
        Assert.Throws<InvalidDataException>(() => Parse(request with { AccountingScope = request.AccountingScope! with { Root = invalid } }));
        Assert.Throws<InvalidDataException>(() => Parse(request with { AccountingScope = request.AccountingScope! with { Gate = new('a', 257) } }));
        var json = JsonSerializer.Serialize(request, NativeHostProtocol.Json);
        json = json.Replace("\"accountingScope\":{", "\"accountingScope\":{\"unknown\":true,", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => NativeHostProtocol.Parse(System.Text.Encoding.UTF8.GetBytes(json)));
    }
}
