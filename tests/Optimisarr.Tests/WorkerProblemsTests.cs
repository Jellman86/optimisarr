using Optimisarr.Api.Workers;

namespace Optimisarr.Tests;

public sealed class WorkerProblemsTests
{
    [Fact]
    public void Failed_verification_names_the_file_and_the_failed_gates_once()
    {
        var message = WorkerProblems.FailedVerification(
            "music/licensed-speech.wav", 7, "Verification failed: Perceptual audio quality (Zimtohrli).");

        Assert.Equal("Its candidate for licensed-speech.wav failed verification: Perceptual audio quality (Zimtohrli).", message);
    }

    [Fact]
    public void Failed_verification_without_a_path_or_reason_still_reads_as_a_sentence()
    {
        Assert.Equal(
            "Its candidate for job 12 failed verification: no reason recorded.",
            WorkerProblems.FailedVerification(null, 12, null));
    }
}
