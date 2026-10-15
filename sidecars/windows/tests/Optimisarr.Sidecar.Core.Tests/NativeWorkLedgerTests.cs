using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class NativeWorkLedgerTests
{
    private static readonly Guid Store = Guid.Parse("81223344-1234-4321-8123-123456789012");
    private static readonly Guid Owner = Guid.Parse("91223344-1234-4321-8123-123456789012");
    private static readonly Guid Lease = Guid.Parse("71223344-1234-4321-8123-123456789012");
    private static string Root => OperatingSystem.IsWindows() ? @"C:\owned" : "/owned";
    private static NativeWorkObligation Probe(NativeWorkPhase phase = NativeWorkPhase.Intent) =>
        new(Owner, NativeWorkPurpose.Capability, null, null, Root, new("root:1", "control:2", "gate:3"),
            "physical-volume:1", 100, 200, false, phase);
    private static NativeWorkSnapshot Snapshot(params NativeWorkObligation[] records) => new(1, Store, 0, records.ToImmutableArray());

    [Fact]
    public void A_cached_snapshot_cannot_be_mutated_through_its_exposed_obligation_collection()
    {
        var snapshot = Snapshot(Probe());
        var collection = (IList<NativeWorkObligation>)snapshot.Obligations;
        Assert.Throws<NotSupportedException>(() => collection[0] = Probe() with { NativeMemoryBytes = 1000 });
        Assert.Equal(100, snapshot.Obligations[0].NativeMemoryBytes);
    }

    [Theory]
    [InlineData("null-list")]
    [InlineData("null-record")]
    [InlineData("null-root")]
    [InlineData("null-scope")]
    [InlineData("null-volume")]
    [InlineData("invalid-unicode")]
    [InlineData("long-root")]
    [InlineData("long-scope")]
    [InlineData("long-volume")]
    public void Null_malformed_or_oversized_text_cannot_be_encoded_as_accounting(string invalid)
    {
        var record = invalid switch {
            "null-record" => null!,
            "null-root" => Probe() with { Root = null! },
            "null-scope" => Probe() with { Scope = null! },
            "null-volume" => Probe() with { VolumeIdentity = null! },
            "invalid-unicode" => Probe() with { VolumeIdentity = "volume:\ud800" },
            "long-root" => Probe() with { Root = Root + new string('a', 4096) },
            "long-scope" => Probe() with { Scope = new(new string('a', 257), "control:2", "gate:3") },
            "long-volume" => Probe() with { VolumeIdentity = new string('a', 513) },
            _ => Probe()
        };
        var snapshot = invalid == "null-list" ? Snapshot() with { Obligations = default! } : Snapshot(record);
        Assert.Throws<InvalidDataException>(() => NativeWorkLedger.Encode(snapshot));
    }

    [Fact]
    public void Exact_scope_and_volume_text_limits_round_trip_but_unknown_nested_scope_fields_are_refused()
    {
        var record = Probe() with { Scope = new(new string('a', 256), new string('b', 256), new string('c', 256)),
            VolumeIdentity = new('v', 512) };
        var raw = NativeWorkLedger.Encode(Snapshot(record));
        Assert.Equal(record, Assert.Single(NativeWorkLedger.Decode(raw).Obligations));
        var node = JsonNode.Parse(raw)!;
        node["Obligations"]![0]!["Scope"]!["Future"] = "unknown";
        Assert.Throws<InvalidDataException>(() => NativeWorkLedger.Decode(Encoding.UTF8.GetBytes(node.ToJsonString())));
    }

    [Fact]
    public void Empty_snapshot_encoding_is_frozen_and_round_trips_without_default_field_inference()
    {
        var bytes = NativeWorkLedger.Encode(Snapshot());
        Assert.Equal("{\"SchemaVersion\":1,\"StoreId\":\"81223344-1234-4321-8123-123456789012\",\"Revision\":0,\"Obligations\":[]}", Encoding.UTF8.GetString(bytes));
        Assert.Equal(bytes, NativeWorkLedger.Encode(NativeWorkLedger.Decode(bytes)));
    }

    [Fact]
    public void Obligation_encoding_freezes_null_fields_purpose_phase_property_order_and_escaping()
    {
        var record = Probe() with { VolumeIdentity = "physical-volume:<one>" };
        var bytes = NativeWorkLedger.Encode(Snapshot(record));
        var rootJson = OperatingSystem.IsWindows() ? "C:\\\\owned" : "/owned";
        var expected = "{\"SchemaVersion\":1,\"StoreId\":\"81223344-1234-4321-8123-123456789012\",\"Revision\":0,\"Obligations\":[{\"Owner\":\"91223344-1234-4321-8123-123456789012\",\"Purpose\":\"Capability\",\"JobId\":null,\"LeaseId\":null,\"Root\":\"" + rootJson
            + "\",\"Scope\":{\"Root\":\"root:1\",\"Control\":\"control:2\",\"Gate\":\"gate:3\"},\"VolumeIdentity\":\"physical-volume:\\u003Cone\\u003E\",\"NativeMemoryBytes\":100,\"ScratchBytes\":200,\"ScratchUsesMemory\":false,\"Phase\":\"Intent\"}]}";
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
        Assert.Equal(record, Assert.Single(NativeWorkLedger.Decode(bytes).Obligations));
    }

    [Theory]
    [InlineData(NativeWorkPhase.Intent)]
    [InlineData(NativeWorkPhase.Authorized)]
    [InlineData(NativeWorkPhase.Retired)]
    [InlineData(NativeWorkPhase.Released)]
    public void Charges_remain_through_positive_retirement_until_durable_release(NativeWorkPhase phase)
    {
        var disk = Probe(phase);
        var ram = disk with { ScratchUsesMemory = true };
        Assert.Equal(phase != NativeWorkPhase.Released, disk.HoldsResources);
        Assert.Equal(100, disk.MemoryCharge);
        Assert.Equal(300, ram.MemoryCharge);
        Assert.Equal(200, disk.DiskCharge);
        Assert.Equal(200, ram.DiskCharge);
        Assert.Equal(ram, Assert.Single(NativeWorkLedger.Decode(NativeWorkLedger.Encode(Snapshot(ram))).Obligations));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("purpose")]
    [InlineData("job")]
    [InlineData("lease")]
    [InlineData("root")]
    [InlineData("scope")]
    [InlineData("volume")]
    [InlineData("native-charge")]
    [InlineData("scratch-charge")]
    [InlineData("memory-backed")]
    public void A_valid_snapshot_cannot_rewrite_an_existing_obligations_identity_or_charge(string changed)
    {
        var job = Probe() with { Purpose = NativeWorkPurpose.Job, JobId = 12, LeaseId = Lease };
        var replacement = changed switch {
            "owner" => job with { Owner = Guid.NewGuid() },
            "purpose" => job with { Purpose = NativeWorkPurpose.Capability, JobId = null, LeaseId = null },
            "job" => job with { JobId = 13 },
            "lease" => job with { LeaseId = Guid.NewGuid() },
            "root" => job with { Root = Path.Combine(Root, "other") },
            "scope" => job with { Scope = job.Scope with { Gate = "gate:4" } },
            "volume" => job with { VolumeIdentity = "physical-volume:2" },
            "native-charge" => job with { NativeMemoryBytes = 101 },
            "scratch-charge" => job with { ScratchBytes = 201 },
            _ => job with { ScratchUsesMemory = true }
        };
        var next = Snapshot(replacement) with { Revision = 1 };
        NativeWorkLedger.Encode(next);
        Assert.Throws<InvalidDataException>(() => NativeWorkLedger.ValidateTransition(Snapshot(job), next));
    }

    [Fact]
    public void Admission_authorization_retirement_release_and_compaction_are_separate_forward_steps()
    {
        var record = Probe();
        var previous = Snapshot();
        foreach (var phase in Enum.GetValues<NativeWorkPhase>()) {
            var next = Snapshot(record with { Phase = phase }) with { Revision = previous.Revision + 1 };
            NativeWorkLedger.ValidateTransition(previous, next);
            previous = next;
        }
        NativeWorkLedger.ValidateTransition(previous, Snapshot() with { Revision = previous.Revision + 1 });
    }

    [Fact]
    public void No_child_authorized_intent_may_retire_without_granting_execution_permission()
    {
        var previous = Snapshot(Probe());
        var retired = Snapshot(Probe(NativeWorkPhase.Retired)) with { Revision = 1 };
        NativeWorkLedger.ValidateTransition(previous, retired);
        Assert.True(Assert.Single(retired.Obligations).HoldsResources);
    }

    [Theory]
    [InlineData(NativeWorkPhase.Intent, NativeWorkPhase.Released)]
    [InlineData(NativeWorkPhase.Authorized, NativeWorkPhase.Released)]
    [InlineData(NativeWorkPhase.Authorized, NativeWorkPhase.Intent)]
    [InlineData(NativeWorkPhase.Retired, NativeWorkPhase.Authorized)]
    [InlineData(NativeWorkPhase.Released, NativeWorkPhase.Retired)]
    public void Release_cannot_skip_retirement_or_regress_an_obligation(NativeWorkPhase before, NativeWorkPhase after)
    {
        Assert.Throws<InvalidDataException>(() => NativeWorkLedger.ValidateTransition(Snapshot(Probe(before)),
            Snapshot(Probe(after)) with { Revision = 1 }));
    }

    [Theory]
    [InlineData(NativeWorkPhase.Intent)]
    [InlineData(NativeWorkPhase.Authorized)]
    [InlineData(NativeWorkPhase.Retired)]
    public void Outstanding_obligations_cannot_disappear_during_compaction(NativeWorkPhase phase) =>
        Assert.Throws<InvalidDataException>(() => NativeWorkLedger.ValidateTransition(Snapshot(Probe(phase)), Snapshot() with { Revision = 1 }));

    [Theory]
    [InlineData(NativeWorkPhase.Authorized)]
    [InlineData(NativeWorkPhase.Retired)]
    [InlineData(NativeWorkPhase.Released)]
    public void A_new_owner_cannot_enter_after_the_admission_phase(NativeWorkPhase phase) =>
        Assert.Throws<InvalidDataException>(() => NativeWorkLedger.ValidateTransition(Snapshot(), Snapshot(Probe(phase)) with { Revision = 1 }));

    [Theory]
    [InlineData("schema")]
    [InlineData("store")]
    [InlineData("revision")]
    [InlineData("overflow")]
    public void Transitions_require_the_same_store_and_one_checked_revision_increment(string changed)
    {
        var previous = Snapshot();
        if (changed == "overflow") previous = previous with { Revision = long.MaxValue };
        var next = previous with { Revision = changed == "overflow" ? 0 : 1 };
        if (changed == "schema") next = next with { SchemaVersion = 2 };
        if (changed == "store") next = next with { StoreId = Guid.NewGuid() };
        if (changed == "revision") next = next with { Revision = 2 };
        Assert.Throws<InvalidDataException>(() => NativeWorkLedger.ValidateTransition(previous, next));
    }

    [Theory]
    [InlineData("empty-owner")]
    [InlineData("job-without-id")]
    [InlineData("job-without-lease")]
    [InlineData("capability-with-job")]
    [InlineData("scope")]
    [InlineData("relative-root")]
    [InlineData("trailing-root")]
    [InlineData("volume")]
    [InlineData("native")]
    [InlineData("scratch")]
    [InlineData("overflow")]
    [InlineData("phase")]
    public void Invalid_identity_charge_or_scope_never_becomes_a_recoverable_obligation(string invalid)
    {
        var record = invalid switch {
            "empty-owner" => Probe() with { Owner = Guid.Empty },
            "job-without-id" => Probe() with { Purpose = NativeWorkPurpose.Job, LeaseId = Lease },
            "job-without-lease" => Probe() with { Purpose = NativeWorkPurpose.Job, JobId = 12 },
            "capability-with-job" => Probe() with { JobId = 12 },
            "scope" => Probe() with { Scope = new("", "control:2", "gate:3") },
            "relative-root" => Probe() with { Root = "relative" },
            "trailing-root" => Probe() with { Root = Root + Path.DirectorySeparatorChar },
            "volume" => Probe() with { VolumeIdentity = "" },
            "native" => Probe() with { NativeMemoryBytes = 0 },
            "scratch" => Probe() with { ScratchBytes = -1 },
            "overflow" => Probe() with { NativeMemoryBytes = long.MaxValue, ScratchUsesMemory = true },
            _ => Probe() with { Phase = (NativeWorkPhase)99 }
        };
        Assert.Throws<InvalidDataException>(() => NativeWorkLedger.Encode(Snapshot(record)));
    }

    [Fact]
    public void Duplicate_owners_and_aggregate_charge_overflow_are_refused_even_when_individual_records_are_valid()
    {
        Assert.Throws<InvalidDataException>(() => NativeWorkLedger.Encode(Snapshot(Probe(), Probe())));
        var first = Probe() with { NativeMemoryBytes = long.MaxValue };
        var second = Probe() with { Owner = Guid.NewGuid() };
        Assert.Throws<InvalidDataException>(() => NativeWorkLedger.Encode(Snapshot(first, second)));
        first = Probe() with { ScratchBytes = long.MaxValue };
        Assert.Throws<InvalidDataException>(() => NativeWorkLedger.Encode(Snapshot(first, second)));
        second = second with { VolumeIdentity = "another-physical-volume" };
        NativeWorkLedger.Encode(Snapshot(first, second));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("numeric-enum")]
    [InlineData("null-scope")]
    [InlineData("whitespace")]
    public void Decoding_rejects_ambiguous_partial_or_unrecognized_json(string corrupt)
    {
        var raw = Encoding.UTF8.GetString(NativeWorkLedger.Encode(Snapshot(Probe())));
        if (corrupt == "duplicate") raw = raw.Replace("\"Revision\":0", "\"Revision\":1,\"Revision\":0");
        else if (corrupt == "whitespace") raw += "\n";
        else {
            var node = JsonNode.Parse(raw)!;
            if (corrupt == "unknown") node["Future"] = true;
            if (corrupt == "missing") node.AsObject().Remove("Revision");
            if (corrupt == "numeric-enum") node["Obligations"]![0]!["Phase"] = 0;
            if (corrupt == "null-scope") node["Obligations"]![0]!["Scope"] = null;
            raw = node.ToJsonString();
        }
        Assert.Throws<InvalidDataException>(() => NativeWorkLedger.Decode(Encoding.UTF8.GetBytes(raw)));
    }

    [Fact]
    public void Snapshot_bounds_refuse_unbounded_recovery_work_and_empty_or_oversized_input()
    {
        Assert.Throws<InvalidDataException>(() => NativeWorkLedger.Encode(Snapshot(Enumerable.Range(0, 17)
            .Select(_ => Probe() with { Owner = Guid.NewGuid() }).ToArray())));
        Assert.Throws<InvalidDataException>(() => NativeWorkLedger.Decode([]));
        Assert.Throws<InvalidDataException>(() => NativeWorkLedger.Decode(new byte[NativeWorkLedger.MaximumBytes + 1]));
    }
}
