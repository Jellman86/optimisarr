using Optimisarr.Core.Workers;
using Optimisarr.Sidecar.Core.Session;
using System.Text.Json;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class FullVerificationTests
{
    [Fact]
    public async Task A_missing_media_tool_returns_bound_failure_evidence_without_claiming_a_pass()
    {
        var contract = new RemoteVerificationContract(1, Guid.NewGuid(), true);
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "ffmpeg");
        var evidence = await FullVerification.MeasureAsync(missing, "source", "candidate", contract,
            new string('a', 64), new string('b', 64), CancellationToken.None);
        Assert.Equal(contract.Id, evidence.ContractId);
        Assert.NotNull(evidence.Error);
        Assert.Null(evidence.Decode);
    }

    [Fact]
    public void The_wire_contract_keeps_strict_verification_optional_for_older_servers()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var contract = new RemoteVerificationContract(1, Guid.NewGuid(), true);
        var copy = JsonSerializer.Deserialize<RemoteVerificationContract>(JsonSerializer.Serialize(contract, options), options);
        Assert.Equal(contract, copy);
        Assert.Equal(2, Optimisarr.Sidecar.Core.Session.WorkerProtocol.Maximum);
    }
}
