using System.Text;
using System.Text.Json.Nodes;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class NativeWorkCheckpointTests
{
    private static readonly Guid Store = Guid.Parse("81223344-1234-4321-8123-123456789012");
    private static string Root => OperatingSystem.IsWindows() ? @"C:\owned" : "/owned";
    private static NativeWorkBinding Binding => new(Root, "journal-directory:1", "coordinator:2");
    private static NativeWorkSnapshot Snapshot(long revision = 0) => new(1, Store, revision, []);
    private static NativeWorkObligation Probe() => new(Guid.Parse("91223344-1234-4321-8123-123456789012"),
        NativeWorkPurpose.Capability, null, null, Root, new("root:1", "control:2", "gate:3"),
        "physical-volume:1", 100, 200, false, NativeWorkPhase.Intent);
    private static byte[] Bytes(NativeWorkSnapshot snapshot) => NativeWorkLedger.Encode(snapshot);

    [Fact]
    public void Initial_authority_cannot_exceed_its_serialized_bound_through_escaped_valid_path_text()
    {
        var binding = Binding with { Path = Root + new string('\u00e9', 1800) };
        Assert.True(Encoding.UTF8.GetByteCount(binding.Path) < 4096);
        Assert.Throws<InvalidDataException>(() => NativeWorkCheckpointProtocol.Initialize(binding, Bytes(Snapshot())));
    }

    [Fact]
    public void A_first_checkpoint_requires_a_new_empty_revision_zero_store()
    {
        var bytes = Bytes(Snapshot());
        var checkpoint = NativeWorkCheckpointProtocol.Initialize(Binding, bytes);
        Assert.Equal(Store, checkpoint.StoreId);
        Assert.Equal(Binding, checkpoint.Binding);
        Assert.Equal(NativeWorkCheckpointProtocol.Digest(bytes), checkpoint.Committed);
        Assert.Null(checkpoint.Pending);
        Assert.Throws<InvalidDataException>(() => NativeWorkCheckpointProtocol.Initialize(Binding, Bytes(Snapshot(1))));
        Assert.Throws<InvalidDataException>(() => NativeWorkCheckpointProtocol.Initialize(Binding,
            Bytes(Snapshot() with { Obligations = [Probe()] })));
    }

    [Fact]
    public void Checkpoint_encoding_freezes_binding_null_pending_and_complete_snapshot_digest()
    {
        var checkpoint = NativeWorkCheckpointProtocol.Initialize(Binding, Bytes(Snapshot()));
        var bytes = NativeWorkCheckpointProtocol.Encode(checkpoint);
        var path = OperatingSystem.IsWindows() ? "C:\\\\owned" : "/owned";
        Assert.Equal("{\"SchemaVersion\":1,\"StoreId\":\"81223344-1234-4321-8123-123456789012\",\"Binding\":{\"Path\":\"" + path
            + "\",\"DirectoryIdentity\":\"journal-directory:1\",\"CoordinatorIdentity\":\"coordinator:2\"},\"Committed\":\""
            + "b8a944b5693116064164a88966d050191137e134527e58287594fefe397398f6\",\"Pending\":null}", Encoding.UTF8.GetString(bytes));
        Assert.Equal(checkpoint, NativeWorkCheckpointProtocol.Decode(bytes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_interrupted_save_can_reconcile_only_the_recorded_old_or_new_bytes(bool replaceCompleted)
    {
        var oldBytes = Bytes(Snapshot());
        var newBytes = Bytes(Snapshot(1) with { Obligations = [Probe()] });
        var committed = NativeWorkCheckpointProtocol.Initialize(Binding, oldBytes);
        var pending = NativeWorkCheckpointProtocol.Begin(committed, oldBytes, newBytes);
        Assert.Equal(committed.Committed, pending.Committed);
        Assert.Equal(NativeWorkCheckpointProtocol.Digest(newBytes), pending.Pending);
        var visible = replaceCompleted ? newBytes : oldBytes;
        var reopened = NativeWorkCheckpointProtocol.Resolve(pending, Binding, visible);
        Assert.Equal(NativeWorkCheckpointProtocol.Digest(visible), reopened.Committed);
        Assert.Null(reopened.Pending);
        Assert.Equal(reopened, NativeWorkCheckpointProtocol.Resolve(reopened, Binding, visible));
    }

    [Fact]
    public void A_completed_checkpoint_rejects_an_older_valid_snapshot_including_a_pre_debt_empty_copy()
    {
        var empty = Bytes(Snapshot());
        var owed = Bytes(Snapshot(1) with { Obligations = [Probe()] });
        var checkpoint = NativeWorkCheckpointProtocol.Initialize(Binding, empty);
        checkpoint = NativeWorkCheckpointProtocol.Resolve(NativeWorkCheckpointProtocol.Begin(checkpoint, empty, owed), Binding, owed);
        Assert.Throws<InvalidDataException>(() => NativeWorkCheckpointProtocol.Resolve(checkpoint, Binding, empty));
        var authorized = Bytes(Snapshot(2) with { Obligations = [Probe() with { Phase = NativeWorkPhase.Authorized }] });
        var pending = NativeWorkCheckpointProtocol.Begin(checkpoint, owed, authorized);
        Assert.Throws<InvalidDataException>(() => NativeWorkCheckpointProtocol.Resolve(pending, Binding, empty));
    }

    [Theory]
    [InlineData("path")]
    [InlineData("directory")]
    [InlineData("coordinator")]
    [InlineData("store")]
    [InlineData("missing-checkpoint")]
    public void Reopening_cannot_redirect_or_reinitialize_independently_bound_accounting(string changed)
    {
        var bytes = Bytes(Snapshot());
        NativeWorkCheckpoint? checkpoint = NativeWorkCheckpointProtocol.Initialize(Binding, bytes);
        var binding = Binding;
        if (changed == "path") binding = binding with { Path = Path.Combine(Root, "another") };
        if (changed == "directory") binding = binding with { DirectoryIdentity = "journal-directory:3" };
        if (changed == "coordinator") binding = binding with { CoordinatorIdentity = "coordinator:4" };
        if (changed == "store") bytes = Bytes(Snapshot() with { StoreId = Guid.NewGuid() });
        if (changed == "missing-checkpoint") checkpoint = null;
        Assert.Throws<InvalidDataException>(() => NativeWorkCheckpointProtocol.Resolve(checkpoint, binding, bytes));
    }

    [Fact]
    public void A_pending_transition_cannot_be_overwritten_or_begun_from_changed_bytes()
    {
        var oldBytes = Bytes(Snapshot());
        var newBytes = Bytes(Snapshot(1) with { Obligations = [Probe()] });
        var nextBytes = Bytes(Snapshot(2) with { Obligations = [Probe() with { Phase = NativeWorkPhase.Authorized }] });
        var current = NativeWorkCheckpointProtocol.Initialize(Binding, oldBytes);
        var pending = NativeWorkCheckpointProtocol.Begin(current, oldBytes, newBytes);
        Assert.Throws<InvalidDataException>(() => NativeWorkCheckpointProtocol.Begin(pending, newBytes, nextBytes));
        Assert.Throws<InvalidDataException>(() => NativeWorkCheckpointProtocol.Begin(current, newBytes, nextBytes));
    }

    [Fact]
    public void Pending_authority_cannot_be_created_for_a_valid_but_unsafe_state_transition()
    {
        var oldBytes = Bytes(Snapshot());
        var checkpoint = NativeWorkCheckpointProtocol.Initialize(Binding, oldBytes);
        var released = Bytes(Snapshot(1) with { Obligations = [Probe() with { Phase = NativeWorkPhase.Released }] });
        Assert.Throws<InvalidDataException>(() => NativeWorkCheckpointProtocol.Begin(checkpoint, oldBytes, released));
        Assert.Throws<InvalidDataException>(() => NativeWorkCheckpointProtocol.Begin(checkpoint, oldBytes, Bytes(Snapshot(2))));
        Assert.Throws<InvalidDataException>(() => NativeWorkCheckpointProtocol.Begin(checkpoint, oldBytes,
            Bytes(Snapshot(1) with { StoreId = Guid.NewGuid() })));
    }

    [Theory]
    [InlineData("committed")]
    [InlineData("pending")]
    [InlineData("equal-digests")]
    [InlineData("schema")]
    [InlineData("store")]
    [InlineData("path")]
    [InlineData("directory")]
    [InlineData("coordinator")]
    public void Malformed_checkpoint_authority_is_refused(string invalid)
    {
        var value = NativeWorkCheckpointProtocol.Initialize(Binding, Bytes(Snapshot()));
        value = invalid switch {
            "committed" => value with { Committed = "missing" },
            "pending" => value with { Pending = new('G', 64) },
            "equal-digests" => value with { Pending = value.Committed },
            "schema" => value with { SchemaVersion = 2 },
            "store" => value with { StoreId = Guid.Empty },
            "path" => value with { Binding = Binding with { Path = "relative" } },
            "directory" => value with { Binding = Binding with { DirectoryIdentity = "" } },
            _ => value with { Binding = Binding with { CoordinatorIdentity = "" } }
        };
        Assert.Throws<InvalidDataException>(() => NativeWorkCheckpointProtocol.Encode(value));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("null-binding")]
    [InlineData("whitespace")]
    public void Checkpoint_decoding_rejects_ambiguous_or_inferred_authority(string corrupt)
    {
        var value = NativeWorkCheckpointProtocol.Initialize(Binding, Bytes(Snapshot()));
        var raw = Encoding.UTF8.GetString(NativeWorkCheckpointProtocol.Encode(value));
        if (corrupt == "duplicate") raw = raw.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":2,\"SchemaVersion\":1");
        else if (corrupt == "whitespace") raw += "\n";
        else {
            var node = JsonNode.Parse(raw)!;
            if (corrupt == "unknown") node["Future"] = true;
            if (corrupt == "missing") node.AsObject().Remove("Pending");
            if (corrupt == "null-binding") node["Binding"] = null;
            raw = node.ToJsonString();
        }
        Assert.Throws<InvalidDataException>(() => NativeWorkCheckpointProtocol.Decode(Encoding.UTF8.GetBytes(raw)));
        Assert.Throws<InvalidDataException>(() => NativeWorkCheckpointProtocol.Decode([]));
        Assert.Throws<InvalidDataException>(() => NativeWorkCheckpointProtocol.Decode(new byte[NativeWorkCheckpointProtocol.MaximumBytes + 1]));
    }
}
