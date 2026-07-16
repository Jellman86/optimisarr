using Optimisarr.Api.Setup;

namespace Optimisarr.Tests;

public sealed class SetupReadinessEvidenceTests
{
    [Fact]
    public void ParseMountInfo_uses_the_deepest_mount_and_decodes_escaped_paths()
    {
        const string mountInfo = """
            100 90 0:40 / / rw,relatime - overlay overlay rw
            101 100 8:1 /media /data rw,relatime - ext4 /dev/sda1 rw
            102 101 8:1 /shows /data/TV\040Shows rw,relatime - ext4 /dev/sda1 rw
            """;

        var mount = FileSystemEvidenceProbe.FindLinuxMount("/data/TV Shows/Example", mountInfo);

        Assert.NotNull(mount);
        Assert.Equal("102", mount.MountId);
        Assert.Equal("8:1", mount.FileSystemId);
        Assert.Equal("/data/TV Shows", mount.MountPoint);
        Assert.Equal("ext4", mount.FileSystemType);
    }

    [Fact]
    public void SharesAtomicBoundary_requires_the_same_mount_not_only_the_same_device()
    {
        var first = new FileSystemEvidence("8:1", "101", "/data", "ext4", 10, 20);
        var sameMount = new FileSystemEvidence("8:1", "101", "/data", "ext4", 10, 20);
        var separateBindMount = new FileSystemEvidence("8:1", "102", "/trash", "ext4", 10, 20);

        Assert.True(FileSystemEvidenceProbe.SharesAtomicBoundary(first, sameMount));
        Assert.False(FileSystemEvidenceProbe.SharesAtomicBoundary(first, separateBindMount));
    }

    [Theory]
    [InlineData(false, false, false, 20L, 10L, SetupPathIssue.Missing)]
    [InlineData(true, false, false, 20L, 10L, SetupPathIssue.Unreadable)]
    [InlineData(true, true, false, 20L, 10L, SetupPathIssue.Unwritable)]
    [InlineData(true, true, true, 9L, 10L, SetupPathIssue.LowSpace)]
    [InlineData(true, true, true, 10L, 10L, SetupPathIssue.None)]
    [InlineData(true, true, true, null, 10L, SetupPathIssue.None)]
    public void Classify_prioritises_access_then_required_free_space(
        bool exists,
        bool readable,
        bool writable,
        long? availableBytes,
        long? requiredBytes,
        SetupPathIssue expected)
    {
        Assert.Equal(expected, SetupPathIssueClassifier.Classify(
            exists,
            readable,
            writable,
            availableBytes,
            requiredBytes));
    }

    [Fact]
    public void DetectPlatform_prefers_specific_appliance_markers_over_generic_container_detection()
    {
        Assert.Equal(DeploymentPlatform.Unraid, DeploymentPlatformDetector.Detect(new Dictionary<string, string?>
        {
            ["DOTNET_RUNNING_IN_CONTAINER"] = "true",
            ["HOST_OS"] = "Unraid"
        }));
        Assert.Equal(DeploymentPlatform.TrueNas, DeploymentPlatformDetector.Detect(new Dictionary<string, string?>
        {
            ["DOTNET_RUNNING_IN_CONTAINER"] = "true",
            ["IX_APP_NAME"] = "optimisarr"
        }));
        Assert.Equal(DeploymentPlatform.Compose, DeploymentPlatformDetector.Detect(new Dictionary<string, string?>
        {
            ["DOTNET_RUNNING_IN_CONTAINER"] = "true"
        }));
        Assert.Equal(DeploymentPlatform.Local, DeploymentPlatformDetector.Detect(new Dictionary<string, string?>()));
    }
}
