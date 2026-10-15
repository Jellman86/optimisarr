using Optimisarr.Core.Domain;
using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

public sealed class PerceptualResourceAdmissionTests
{
    private const long Peak = PerceptualResourceBudget.ImageMemoryBytes;
    private readonly string _root = Path.GetFullPath("reservation-tests");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Source_qualification_then_workspace_reserve_their_combined_peak_until_both_are_cleaned(bool ram)
    {
        var pool = new PerceptualJobResourcePool();
        var resources = new PerceptualResourceAdmission(_ => new(_root, 1000, Peak + 600, ram), pool);
        using var native = resources.ReservePeak(MediaKind.Image, _root);
        Assert.NotNull(native);
        using var workspace = resources.ReserveWorkspace(600, _root);
        Assert.NotNull(workspace);
        var remaining = pool.Available(_root, 1000, Peak + 600);
        Assert.Equal(400, remaining.Disk);
        Assert.Equal(ram ? 0 : 600, remaining.Memory);
        Assert.Null(resources.ReservePeak(MediaKind.Image, _root));
        native.Dispose();
        Assert.Equal(ram ? Peak : Peak + 600, pool.Available(_root, 1000, Peak + 600).Memory);
        workspace.Dispose();
        Assert.Equal((1000L, Peak + 600), pool.Available(_root, 1000, Peak + 600));
    }

    [Fact]
    public void A_later_workspace_refusal_keeps_the_source_qualification_reservation_until_the_caller_finishes()
    {
        var pool = new PerceptualJobResourcePool();
        var resources = new PerceptualResourceAdmission(_ => new(_root, 1000, Peak + 599, true), pool);
        using var peak = resources.ReservePeak(MediaKind.Image, _root);
        Assert.NotNull(peak);
        Assert.Null(resources.ReserveWorkspace(600, _root));
        Assert.Equal(599, pool.Available(_root, 1000, Peak + 599).Memory);
    }

    [Theory]
    [InlineData(null, 1000L)]
    [InlineData(1000L, null)]
    [InlineData(1000L, 0L)]
    public void Unknown_or_inadequate_memory_or_disk_cannot_start_source_qualification(long? disk, long? memory)
    {
        var resources = new PerceptualResourceAdmission(_ => new(_root, disk, memory, false), new());
        Assert.Null(resources.ReservePeak(MediaKind.Image, _root));
    }

    [Fact]
    public void Missing_volume_and_invalid_workspace_never_become_unbounded_capacity()
    {
        var absent = new PerceptualResourceAdmission(_ => null, new());
        Assert.Null(absent.ReservePeak(MediaKind.Audio, _root));
        var ready = new PerceptualResourceAdmission(_ => new(_root, long.MaxValue, long.MaxValue, false), new());
        Assert.Null(ready.ReserveWorkspace(0, _root));
        Assert.Null(ready.ReserveWorkspace(-1, _root));
        Assert.Null(ready.ReservePeak(MediaKind.Video, _root));
    }

    [Fact]
    public void Reservations_on_different_disks_still_share_native_RAM()
    {
        var pool = new PerceptualJobResourcePool();
        var resources = new PerceptualResourceAdmission(root => new(root, 1000, PerceptualResourceBudget.AudioMemoryBytes, false), pool);
        using var first = resources.ReservePeak(MediaKind.Audio, _root);
        Assert.NotNull(first);
        Assert.Null(resources.ReservePeak(MediaKind.Audio, _root + "-other"));
    }

    [Fact]
    public void Workspace_cleanup_can_finish_before_native_memory_cleanup_without_stranding_a_job_slot()
    {
        var pool = new PerceptualJobResourcePool();
        var resources = new PerceptualResourceAdmission(_ => new(_root, 1000, Peak + 600, true), pool);
        var native = resources.ReservePeak(MediaKind.Image, _root);
        var workspace = resources.ReserveWorkspace(600, _root);
        Assert.NotNull(native); Assert.NotNull(workspace);
        workspace.Dispose();
        Assert.Equal((1000L, 600L), pool.Available(_root, 1000, Peak + 600));
        native.Dispose();
        Assert.Equal((1000L, Peak + 600), pool.Available(_root, 1000, Peak + 600));
        using var next = resources.ReservePeak(MediaKind.Image, _root);
        Assert.NotNull(next);
    }

}
