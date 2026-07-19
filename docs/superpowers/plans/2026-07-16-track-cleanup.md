# Track Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task (this repo forbids sub-agents — see CLAUDE.md §10, so subagent-driven-development must NOT be used). Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `TrackCleanup` rule profile that only removes unwanted-language audio/subtitle tracks (no re-encode, no container change), a per-library "Keep subtitle languages" rule that works on all profiles, and per-job queue reasons.

**Architecture:** Mirrors the existing `KeepAudioLanguages` feature end to end: pure selection/eligibility/verification logic in `Optimisarr.Core` (unit-tested without FFmpeg/DB), persistence + EF migrations in `Optimisarr.Data`, composition in `Optimisarr.Api`, Svelte 5 UI in `web`. `RuleSettings.TargetContainer` becomes nullable, where `null` means "keep the source container".

**Tech Stack:** .NET 10 / C#, EF Core + SQLite, xUnit, Svelte 5 + TypeScript.

**Spec:** `docs/superpowers/specs/2026-07-16-track-cleanup-design.md`

## Global Constraints

- `dotnet build Optimisarr.slnx` with **zero warnings** (CI runs `-warnaserror`); `dotnet test Optimisarr.slnx` fully green before every commit.
- `cd web && npm run check` clean (zero errors, zero warnings) whenever the frontend changes.
- Every schema change ships an EF migration in the same commit; migrations idempotent (`MigrationTests` prove it).
- Safety model intact: unknown-language tracks never removed; audio keeps its no-match guard; verification tightened, not relaxed.
- FFmpeg args are argument arrays, never shell strings.
- File-scoped namespaces, primary constructors, `sealed` by default, nullable refs on. Comments say *why*, not *what*.
- No sub-agents; work inline on branch `feat/keep-audio-languages`.
- Shell setup if `dotnet` is missing from PATH (non-interactive shells):
  ```bash
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
  export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
  ```
- `CHANGELOG.md` (Unreleased) is updated in the final task.

---

### Task 1: Shared language matching + `SubtitleTrackSelection`

Extract the language table/matching/parsing out of `AudioTrackSelection` into a shared public `TrackLanguages` class, then add `SubtitleTrackSelection` (no keep-at-least-one guard).

**Files:**
- Create: `src/Optimisarr.Core/Queue/TrackLanguages.cs`
- Create: `src/Optimisarr.Core/Queue/SubtitleTrackSelection.cs`
- Modify: `src/Optimisarr.Core/Queue/AudioTrackSelection.cs`
- Modify (call sites of moved parse helpers): `src/Optimisarr.Api/Library/LibraryRuleResolution.cs`, `src/Optimisarr.Api/Library/LibraryRequestParser.cs`, `src/Optimisarr.Api/Library/CandidateService.cs:196`, `src/Optimisarr.Api/Queue/QueueDispatcher.cs:533` — plus any other `AudioTrackSelection.Parse…`/`TryNormalise…` callers found by `grep -rn "AudioTrackSelection\." src tests`
- Test: `tests/Optimisarr.Tests/SubtitleTrackSelectionTests.cs` (new), `tests/Optimisarr.Tests/AudioTrackSelectionTests.cs` (update references only)

**Interfaces:**
- Produces: `TrackLanguages.Canonicalise(string) : string`, `TrackLanguages.IsUnknown(string?) : bool`, `TrackLanguages.ParseLanguageList(string?) : IReadOnlyList<string>`, `TrackLanguages.ParseTrackLanguages(string?) : IReadOnlyList<string?>?`, `TrackLanguages.TryNormaliseLanguageList(string?, out string?) : bool`, `TrackLanguages.MaxLanguageListLength = 256`
- Produces: `SubtitleTrackSelection.SelectRemovals(IReadOnlyList<string?> trackLanguages, IReadOnlyList<string> keepLanguages) : IReadOnlyList<int>`
- `AudioTrackSelection.SelectRemovals` keeps its exact signature and behaviour (guard included). Its parse/normalise helpers move to `TrackLanguages`; call sites are updated (no delegating shims left behind).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Optimisarr.Tests/SubtitleTrackSelectionTests.cs
using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class SubtitleTrackSelectionTests
{
    [Fact]
    public void Empty_keep_list_removes_nothing()
    {
        var removals = SubtitleTrackSelection.SelectRemovals(
            new string?[] { "eng", "fra" }, Array.Empty<string>());
        Assert.Empty(removals);
    }

    [Fact]
    public void Removes_known_tracks_not_in_the_kept_languages()
    {
        var removals = SubtitleTrackSelection.SelectRemovals(
            new string?[] { "eng", "fra", "deu" }, new[] { "eng" });
        Assert.Equal(new[] { 1, 2 }, removals);
    }

    [Fact]
    public void Unknown_untagged_and_private_use_tracks_are_never_removed()
    {
        var removals = SubtitleTrackSelection.SelectRemovals(
            new string?[] { "und", null, "zxx", "mul", "qaa", "fra" }, new[] { "eng" });
        Assert.Equal(new[] { 5 }, removals);
    }

    [Fact]
    public void All_foreign_subtitles_are_removed_even_when_nothing_matches()
    {
        // Unlike audio there is no keep-at-least-one guard: subtitles are optional
        // streams, so a file can legitimately end with zero subtitle tracks.
        var removals = SubtitleTrackSelection.SelectRemovals(
            new string?[] { "fra", "deu" }, new[] { "eng" });
        Assert.Equal(new[] { 0, 1 }, removals);
    }

    [Fact]
    public void Bibliographic_and_terminology_spellings_match()
    {
        var removals = SubtitleTrackSelection.SelectRemovals(
            new string?[] { "ger", "fre" }, new[] { "deu" });
        Assert.Equal(new[] { 1 }, removals);
    }

    [Fact]
    public void Two_letter_codes_match_their_three_letter_equivalents()
    {
        var removals = SubtitleTrackSelection.SelectRemovals(
            new string?[] { "en", "fr" }, new[] { "eng" });
        Assert.Equal(new[] { 1 }, removals);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Optimisarr.slnx --filter "FullyQualifiedName~SubtitleTrackSelectionTests"`
Expected: compile error — `SubtitleTrackSelection` does not exist.

- [ ] **Step 3: Implement**

Create `TrackLanguages.cs`: move `UnknownTags`, `CanonicalCodes`, `Alpha2ToTerminology`, `BuildCanonicalCodes`, `Canonicalise`, `IsUnknown`, `IsAsciiLanguageCode`, `MaxLanguageListLength`, `ParseLanguageList`, `ParseTrackLanguages`, and `TryNormaliseLanguageList` verbatim from `AudioTrackSelection` into a new public static class (same namespace `Optimisarr.Core.Queue`), with `Canonicalise` and `IsUnknown` made `public`. Class doc:

```csharp
/// <summary>
/// Shared ISO 639 language handling for track-removal rules: parsing stored language
/// lists, canonicalising B/T and two-letter spellings, and recognising tags that mean
/// "language unknown" (which never prove a track is safe to remove). Used by both
/// <see cref="AudioTrackSelection"/> and <see cref="SubtitleTrackSelection"/>.
/// </summary>
public static class TrackLanguages
```

Shrink `AudioTrackSelection` to `SelectRemovals` only, delegating to the shared helpers:

```csharp
public static class AudioTrackSelection
{
    public static IReadOnlyList<int> SelectRemovals(
        IReadOnlyList<string?> trackLanguages,
        IReadOnlyList<string> keepLanguages)
    {
        if (keepLanguages.Count == 0)
        {
            return Array.Empty<int>();
        }

        var kept = keepLanguages.Select(TrackLanguages.Canonicalise)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removals = new List<int>();
        var anyKeptMatch = false;
        for (var index = 0; index < trackLanguages.Count; index++)
        {
            if (TrackLanguages.IsUnknown(trackLanguages[index]))
            {
                continue;
            }

            if (kept.Contains(TrackLanguages.Canonicalise(trackLanguages[index]!)))
            {
                anyKeptMatch = true;
            }
            else
            {
                removals.Add(index);
            }
        }

        return anyKeptMatch ? removals : Array.Empty<int>();
    }
}
```

Create `SubtitleTrackSelection.cs`:

```csharp
namespace Optimisarr.Core.Queue;

/// <summary>
/// Decides which of a file's subtitle tracks a kept-languages rule removes. A track
/// with an unknown language is never removed. Unlike <see cref="AudioTrackSelection"/>
/// there is no keep-at-least-one guard: subtitles are optional streams, so a file whose
/// subtitles are all in non-kept languages ends up with none — a normal state for media.
/// </summary>
public static class SubtitleTrackSelection
{
    /// <summary>
    /// Returns the subtitle-relative stream indexes to remove: every track whose
    /// language is known and matches none of <paramref name="keepLanguages"/>.
    /// An empty keep list keeps everything.
    /// </summary>
    public static IReadOnlyList<int> SelectRemovals(
        IReadOnlyList<string?> trackLanguages,
        IReadOnlyList<string> keepLanguages)
    {
        if (keepLanguages.Count == 0)
        {
            return Array.Empty<int>();
        }

        var kept = keepLanguages.Select(TrackLanguages.Canonicalise)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removals = new List<int>();
        for (var index = 0; index < trackLanguages.Count; index++)
        {
            if (TrackLanguages.IsUnknown(trackLanguages[index]))
            {
                continue;
            }

            if (!kept.Contains(TrackLanguages.Canonicalise(trackLanguages[index]!)))
            {
                removals.Add(index);
            }
        }

        return removals;
    }
}
```

Update every caller of the moved helpers (`grep -rn "AudioTrackSelection\.Parse\|AudioTrackSelection\.TryNormalise\|AudioTrackSelection\.MaxLanguageListLength" src tests`) to `TrackLanguages.…`. Known sites: `LibraryRuleResolution.cs:52`, `LibraryRequestParser.cs:281`, `CandidateService.cs:196`, `QueueDispatcher.cs:533`; check `ConfigSnapshotValidator` and tests too.

- [ ] **Step 4: Run the full suite**

Run: `dotnet test Optimisarr.slnx`
Expected: PASS (new subtitle tests plus every pre-existing audio-selection test unchanged and green).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: shared language matching and subtitle track selection"
```

---

### Task 2: `TrackCleanup` profile — never re-encode, never change the container

**Files:**
- Modify: `src/Optimisarr.Core/Domain/RuleProfile.cs`
- Modify: `src/Optimisarr.Core/Rules/RuleSettings.cs` (`TargetContainer` → `string?`)
- Modify: `src/Optimisarr.Core/Rules/RuleProfileDefaults.cs`
- Modify: `src/Optimisarr.Core/Rules/RuleResolver.cs`
- Modify: `src/Optimisarr.Core/Rules/CandidateEvaluator.cs`
- Modify: `src/Optimisarr.Core/Queue/TranscodeSpecResolver.cs`
- Test: `tests/Optimisarr.Tests/RuleProfileDefaultsTests.cs`, `tests/Optimisarr.Tests/RuleResolverTests.cs`, `tests/Optimisarr.Tests/CandidateEvaluatorTests.cs`, `tests/Optimisarr.Tests/TranscodeSpecResolverTests.cs`

**Interfaces:**
- Produces: `RuleProfile.TrackCleanup = 5`.
- Produces: `RuleSettings.TargetContainer : string?` — `null` means "keep the source container". All existing profiles keep non-null defaults, so nothing else changes behaviour.
- Produces: `TranscodeSpecResolver.Resolve` output path keeps the source extension when `rules.TargetContainer is null`.
- Consumes: `AudioTrackSelection.SelectRemovals` (Task 1).

- [ ] **Step 1: Write the failing tests** (add to the existing test files, following their local style)

```csharp
// RuleProfileDefaultsTests.cs
[Fact]
public void Track_cleanup_never_reencodes_and_keeps_the_source_container()
{
    var settings = RuleProfileDefaults.For(RuleProfile.TrackCleanup);

    Assert.Null(settings.TargetVideoCodec);
    Assert.Null(settings.TargetContainer);
    Assert.Null(settings.DefaultCrf);
    Assert.Equal(0, settings.MinFileSizeBytes);
    Assert.Equal(HdrHandling.Preserve, settings.Hdr);
}

// RuleResolverTests.cs
[Fact]
public void Track_cleanup_ignores_a_library_container_override()
{
    // The profile's whole promise is "container unchanged"; a stale per-library
    // override must not silently reintroduce a remux.
    var resolved = RuleResolver.Resolve(
        RuleProfile.TrackCleanup, new RuleOverrides { TargetContainer = "mkv" });

    Assert.Null(resolved.TargetContainer);
}

// CandidateEvaluatorTests.cs — use the file's existing MediaProperties helper/style
[Fact]
public void Track_cleanup_skips_everything_when_no_kept_languages_are_configured()
{
    var media = Video() with { Container = "matroska,webm", AudioLanguages = new string?[] { "eng", "fra" } };
    var rules = RuleProfileDefaults.For(RuleProfile.TrackCleanup);

    var decision = CandidateEvaluator.Evaluate(media, rules);

    Assert.False(decision.IsEligible);
    Assert.Contains("nothing to remove", decision.Reason, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public void Track_cleanup_is_eligible_only_when_tracks_are_removable()
{
    var rules = RuleProfileDefaults.For(RuleProfile.TrackCleanup) with
    {
        KeepAudioLanguages = new[] { "eng" }
    };

    var removable = Video() with { AudioLanguages = new string?[] { "eng", "fra" } };
    var clean = Video() with { AudioLanguages = new string?[] { "eng" } };

    Assert.True(CandidateEvaluator.Evaluate(removable, rules).IsEligible);
    Assert.False(CandidateEvaluator.Evaluate(clean, rules).IsEligible);
}

[Fact]
public void Track_cleanup_stays_conservative_when_languages_were_never_captured()
{
    var rules = RuleProfileDefaults.For(RuleProfile.TrackCleanup) with
    {
        KeepAudioLanguages = new[] { "eng" }
    };
    var media = Video() with { AudioLanguages = null };

    var decision = CandidateEvaluator.Evaluate(media, rules);

    Assert.False(decision.IsEligible);
    Assert.Contains("re-probe", decision.Reason, StringComparison.OrdinalIgnoreCase);
}

// TranscodeSpecResolverTests.cs
[Fact]
public void Null_target_container_keeps_the_source_extension()
{
    var spec = TranscodeSpecResolver.Resolve(
        RuleProfileDefaults.For(RuleProfile.TrackCleanup) with { KeepAudioLanguages = new[] { "eng" } },
        "/data/Movies/Film (2020)/Film.mp4",
        "Film (2020)/Film.mp4",
        "/work",
        sourceIsHdr: false,
        crf: null,
        preset: null,
        sourceAudioLanguages: new string?[] { "eng", "fra" });

    Assert.EndsWith("Film (2020)/Film.mp4", spec.OutputPath);
    Assert.Null(spec.VideoCodec);
    Assert.Equal(new[] { 1 }, spec.RemoveAudioStreamIndexes);
}
```

(Adapt `Video()` to whatever builder the existing `CandidateEvaluatorTests` use for a probed video file; the removable case must set `Container` to any value since no container check applies.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Optimisarr.slnx --filter "FullyQualifiedName~RuleProfileDefaultsTests|FullyQualifiedName~RuleResolverTests|FullyQualifiedName~CandidateEvaluatorTests|FullyQualifiedName~TranscodeSpecResolverTests"`
Expected: compile error — `RuleProfile.TrackCleanup` does not exist.

- [ ] **Step 3: Implement**

`RuleProfile.cs` — append:

```csharp
    /// <summary>
    /// Removes audio/subtitle tracks not in the library's kept languages, and nothing
    /// else: no re-encode, no container change. Streams are copied bit-identically.
    /// </summary>
    TrackCleanup = 5
```

`RuleSettings.cs` — change the property and its doc:

```csharp
    /// <summary>
    /// The container to remux/mux into (e.g. "mkv"). A file whose container already
    /// matches is considered clean for remux-only profiles. <c>null</c> means the
    /// output keeps the source's container — used by the track-cleanup profile,
    /// whose promise is that nothing but unwanted tracks changes.
    /// </summary>
    public string? TargetContainer { get; init; } = "mkv";
```

`RuleProfileDefaults.cs` — add before the fallback arm:

```csharp
        // Track cleanup: strips audio/subtitle tracks outside the kept languages via a
        // lossless stream copy. No codec, no CRF, and no target container — the output
        // keeps the source container so nothing but the unwanted tracks changes.
        RuleProfile.TrackCleanup => new RuleSettings
        {
            Profile = profile,
            TargetVideoCodec = null,
            TargetContainer = null,
            DefaultCrf = null,
            MinFileSizeBytes = 0,
            Hdr = HdrHandling.Preserve
        },
```

`RuleResolver.cs` — the container override line becomes:

```csharp
            // TrackCleanup's promise is "container unchanged", so a per-library container
            // override (e.g. left over from a previous profile) must not reintroduce a remux.
            TargetContainer = profile == Domain.RuleProfile.TrackCleanup
                ? null
                : Normalise(overrides.TargetContainer) ?? settings.TargetContainer,
```

`CandidateEvaluator.cs` — replace the `rules.TargetVideoCodec is null` block (lines 218–242) with (subtitle parts arrive in Task 3; keep the structure ready):

```csharp
        // Profiles with no target codec never re-encode; they act on containers and,
        // when kept-languages rules are set, on unwanted tracks.
        if (rules.TargetVideoCodec is null)
        {
            var audioRemovals = media.AudioLanguages is null
                ? (IReadOnlyList<int>)Array.Empty<int>()
                : Queue.AudioTrackSelection.SelectRemovals(media.AudioLanguages, rules.KeepAudioLanguages);

            // Track cleanup: the only work this profile does is removing unwanted tracks,
            // so eligibility is exactly "is there anything to remove".
            if (rules.TargetContainer is null)
            {
                if (rules.KeepAudioLanguages.Count == 0)
                {
                    return CandidateDecision.Skipped(
                        "No kept audio or subtitle languages configured — nothing to remove");
                }

                if (audioRemovals.Count > 0)
                {
                    return CandidateDecision.Eligible(RemovalReason(media, audioRemovals));
                }

                return CandidateDecision.Skipped(media.AudioLanguages is null
                    ? "Track languages not captured yet — re-probe the file to evaluate the kept-languages rule"
                    : "No removable tracks (all tracks match the kept languages or are unknown)");
            }

            var keyword = ContainerKeyword(rules.TargetContainer);
            var alreadyClean = media.Container is not null &&
                media.Container.Contains(keyword, StringComparison.OrdinalIgnoreCase);

            if (!alreadyClean)
            {
                return CandidateDecision.Eligible(
                    $"Remux to {rules.TargetContainer} ({media.Container} → {rules.TargetContainer})");
            }

            // A container-clean file can still carry tracks the kept-languages rules remove;
            // stripping them (a stream copy, no re-encode) is part of this profile's cleanup.
            return audioRemovals.Count > 0
                ? CandidateDecision.Eligible(RemovalReason(media, audioRemovals))
                : CandidateDecision.Skipped($"Already in the target container ({rules.TargetContainer})");
        }
```

with a private reason builder (extended for subtitles in Task 3):

```csharp
    // Names the languages being removed, not just counts, so the inventory and the
    // queue answer "why is this file here?" at a glance.
    private static string RemovalReason(MediaProperties media, IReadOnlyList<int> audioRemovals)
    {
        var languages = string.Join(", ", audioRemovals.Select(index => media.AudioLanguages![index] ?? "und"));
        return $"Remove {audioRemovals.Count} audio track(s) ({languages}) not in the kept languages";
    }
```

`TranscodeSpecResolver.cs` — container resolution (lines 74–79) becomes:

```csharp
        var copyingAudio = rules.VideoAudioCodec is null;
        var audioForcesMkv = sourceHasMp4IncompatibleAudio && copyingAudio;
        // A null target container means "keep the source container" (the track-cleanup
        // promise). Every kept stream already lives in that container, so the MP4
        // image-subtitle / incompatible-audio fallback does not apply.
        var container = rules.TargetContainer is null
            ? Path.GetExtension(relativePath).TrimStart('.')
            : (sourceHasImageSubtitles || audioForcesMkv) && IsMp4Container(rules.TargetContainer)
                ? "mkv"
                : rules.TargetContainer;
        var outputPath = BuildOutputPath(workRoot, relativePath, container);
```

(The scanner only admits known media extensions, so `container` cannot be empty for a real inventory row; no extra guard.)

Fix any remaining nullability warnings from `TargetContainer` becoming `string?` (`ContainerKeyword` already takes the value only on the non-null path; `IsMp4Container` already accepts `string?`). Build with zero warnings.

- [ ] **Step 4: Run the full suite**

Run: `dotnet test Optimisarr.slnx`
Expected: PASS. Existing RemuxCleanup tests must be untouched and green.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: TrackCleanup profile — remove tracks without re-encode or container change"
```

---

### Task 3: `KeepSubtitleLanguages` through the Core rules

**Files:**
- Modify: `src/Optimisarr.Core/Rules/RuleSettings.cs`, `RuleOverrides.cs`, `RuleResolver.cs`
- Modify: `src/Optimisarr.Core/Rules/MediaProperties.cs`
- Modify: `src/Optimisarr.Core/Rules/CandidateEvaluator.cs`
- Modify: `src/Optimisarr.Core/Queue/TranscodeSpecResolver.cs` (spec resolution), `src/Optimisarr.Core/Queue/FfmpegCommandBuilder.cs` (`TranscodeSpec` + `-map -0:s:N`)
- Test: `tests/Optimisarr.Tests/RuleResolverTests.cs`, `CandidateEvaluatorTests.cs`, `TranscodeSpecResolverTests.cs`, `FfmpegCommandBuilderTests.cs`

**Interfaces:**
- Produces: `RuleSettings.KeepSubtitleLanguages : IReadOnlyList<string>` (default empty), `RuleOverrides.KeepSubtitleLanguages : IReadOnlyList<string>?`
- Produces: `MediaProperties.SubtitleLanguages : IReadOnlyList<string?>?` (last positional param, default `null`)
- Produces: `TranscodeSpec.RemoveSubtitleStreamIndexes : IReadOnlyList<int>?` (last positional param, default `null`); `TranscodeSpecResolver.Resolve` gains `IReadOnlyList<string?>? sourceSubtitleLanguages = null`
- Consumes: `SubtitleTrackSelection.SelectRemovals` (Task 1)

- [ ] **Step 1: Write the failing tests**

```csharp
// RuleResolverTests.cs
[Fact]
public void Keep_subtitle_languages_override_layers_onto_the_profile_default()
{
    var resolved = RuleResolver.Resolve(
        RuleProfile.ConservativeHevc,
        new RuleOverrides { KeepSubtitleLanguages = new[] { "eng" } });

    Assert.Equal(new[] { "eng" }, resolved.KeepSubtitleLanguages);
    Assert.Empty(RuleResolver.Resolve(RuleProfile.ConservativeHevc, RuleOverrides.None).KeepSubtitleLanguages);
}

// CandidateEvaluatorTests.cs
[Fact]
public void Remux_cleanup_offers_a_container_clean_file_with_removable_subtitles()
{
    var rules = RuleProfileDefaults.For(RuleProfile.RemuxCleanup) with
    {
        KeepSubtitleLanguages = new[] { "eng" }
    };
    var media = Video() with
    {
        Container = "matroska,webm",
        SubtitleLanguages = new string?[] { "eng", "fra" }
    };

    var decision = CandidateEvaluator.Evaluate(media, rules);

    Assert.True(decision.IsEligible);
    Assert.Contains("subtitle", decision.Reason, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("fra", decision.Reason);
}

[Fact]
public void Track_cleanup_counts_subtitle_removals_toward_eligibility()
{
    var rules = RuleProfileDefaults.For(RuleProfile.TrackCleanup) with
    {
        KeepSubtitleLanguages = new[] { "eng" }
    };
    var media = Video() with { SubtitleLanguages = new string?[] { "fra", "spa" } };

    var decision = CandidateEvaluator.Evaluate(media, rules);

    Assert.True(decision.IsEligible);
    Assert.Contains("2 subtitle track(s) (fra, spa)", decision.Reason);
}

// TranscodeSpecResolverTests.cs
[Fact]
public void Subtitle_removals_are_resolved_positionally()
{
    var rules = RuleProfileDefaults.For(RuleProfile.ConservativeHevc) with
    {
        KeepSubtitleLanguages = new[] { "eng" }
    };

    var spec = TranscodeSpecResolver.Resolve(
        rules, "/in/a.mkv", "a.mkv", "/work",
        sourceIsHdr: false, crf: 24, preset: null,
        sourceSubtitleLanguages: new string?[] { "eng", "fra", null, "deu" });

    Assert.Equal(new[] { 1, 3 }, spec.RemoveSubtitleStreamIndexes);
}

// FfmpegCommandBuilderTests.cs
[Fact]
public void Removed_subtitle_tracks_become_negative_subtitle_maps()
{
    var spec = new TranscodeSpec(
        "/in/a.mkv", "/out/a.mkv", VideoCodec: null, Crf: null, Preset: null,
        TonemapToSdr: false,
        RemoveAudioStreamIndexes: new[] { 1 },
        RemoveSubtitleStreamIndexes: new[] { 0, 2 });

    var args = FfmpegCommandBuilder.Build(spec);

    var joined = string.Join(' ', args);
    Assert.Contains("-map -0:a:1", joined);
    Assert.Contains("-map -0:s:0", joined);
    Assert.Contains("-map -0:s:2", joined);
    Assert.Contains("-c copy", joined);
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Optimisarr.slnx --filter "FullyQualifiedName~RuleResolverTests|FullyQualifiedName~CandidateEvaluatorTests|FullyQualifiedName~TranscodeSpecResolverTests|FullyQualifiedName~FfmpegCommandBuilderTests"`
Expected: compile errors for the new members.

- [ ] **Step 3: Implement**

`RuleSettings.cs` — after `KeepAudioLanguages`:

```csharp
    /// <summary>
    /// The subtitle languages a video job keeps (ISO 639 codes); tracks in any other
    /// language are removed from the output. Empty (the default) keeps every track.
    /// Tracks whose language is unknown are always kept. Unlike audio there is no
    /// keep-at-least-one guard — subtitles are optional streams, so a file may end
    /// with none. See <see cref="Queue.SubtitleTrackSelection"/>.
    /// </summary>
    public IReadOnlyList<string> KeepSubtitleLanguages { get; init; } = Array.Empty<string>();
```

`RuleOverrides.cs` — after `KeepAudioLanguages`: `public IReadOnlyList<string>? KeepSubtitleLanguages { get; init; }`

`RuleResolver.cs` — after the KeepAudioLanguages line: `KeepSubtitleLanguages = overrides.KeepSubtitleLanguages ?? settings.KeepSubtitleLanguages,`

`MediaProperties.cs` — append final param:

```csharp
    // Per-track subtitle language tags in stream order; null when the probe predates
    // subtitle-language capture, so the kept-subtitles rule stays conservative.
    IReadOnlyList<string?>? SubtitleLanguages = null);
```

`CandidateEvaluator.cs` — in the `TargetVideoCodec is null` block, add next to `audioRemovals`:

```csharp
            var subtitleRemovals = media.SubtitleLanguages is null
                ? (IReadOnlyList<int>)Array.Empty<int>()
                : Queue.SubtitleTrackSelection.SelectRemovals(media.SubtitleLanguages, rules.KeepSubtitleLanguages);
```

Then:
- TrackCleanup empty-rule guard becomes `rules.KeepAudioLanguages.Count == 0 && rules.KeepSubtitleLanguages.Count == 0`.
- Eligibility checks become `audioRemovals.Count + subtitleRemovals.Count > 0`, passing both lists to `RemovalReason`.
- The languages-not-captured skip becomes:

```csharp
                var languagesUnknown =
                    (rules.KeepAudioLanguages.Count > 0 && media.AudioLanguages is null)
                    || (rules.KeepSubtitleLanguages.Count > 0 && media.SubtitleLanguages is null);
                return CandidateDecision.Skipped(languagesUnknown
                    ? "Track languages not captured yet — re-probe the file to evaluate the kept-languages rule"
                    : "No removable tracks (all tracks match the kept languages or are unknown)");
```

- `RemovalReason` becomes:

```csharp
    private static string RemovalReason(
        MediaProperties media, IReadOnlyList<int> audioRemovals, IReadOnlyList<int> subtitleRemovals)
    {
        var parts = new List<string>(2);
        if (audioRemovals.Count > 0)
        {
            parts.Add(DescribeRemovals("audio", media.AudioLanguages!, audioRemovals));
        }
        if (subtitleRemovals.Count > 0)
        {
            parts.Add(DescribeRemovals("subtitle", media.SubtitleLanguages!, subtitleRemovals));
        }
        return $"Remove {string.Join(" + ", parts)} not in the kept languages";
    }

    private static string DescribeRemovals(string kind, IReadOnlyList<string?> languages, IReadOnlyList<int> removals) =>
        $"{removals.Count} {kind} track(s) ({string.Join(", ", removals.Select(index => languages[index] ?? "und"))})";
```

`FfmpegCommandBuilder.cs` — `TranscodeSpec` gains the final param:

```csharp
    // Subtitle-relative indexes of the source tracks the kept-languages rule removes
    // (see SubtitleTrackSelection). Null or empty keeps every track.
    IReadOnlyList<int>? RemoveSubtitleStreamIndexes = null);
```

and in `AppendVideoArguments`, directly after the audio-removal loop:

```csharp
        if (spec.RemoveSubtitleStreamIndexes is { Count: > 0 } removedSubtitles)
        {
            foreach (var index in removedSubtitles)
            {
                args.Add("-map");
                args.Add($"-0:s:{index}");
            }
        }
```

`TranscodeSpecResolver.cs` — add parameter `IReadOnlyList<string?>? sourceSubtitleLanguages = null` (after `sourceAudioLanguages`), with the same doc pattern, resolve:

```csharp
        var removedSubtitles = sourceSubtitleLanguages is null
            ? Array.Empty<int>()
            : SubtitleTrackSelection.SelectRemovals(sourceSubtitleLanguages, rules.KeepSubtitleLanguages);
```

and pass `RemoveSubtitleStreamIndexes: removedSubtitles.Count > 0 ? removedSubtitles : null` in the returned spec.

- [ ] **Step 4: Run the full suite** — `dotnet test Optimisarr.slnx` → PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: keep-subtitle-languages rule through rules, spec, and ffmpeg command"
```

---

### Task 4: Probe + inventory capture of subtitle languages (+ migration)

**Files:**
- Modify: `src/Optimisarr.Core/Library/MediaProbeService.cs`
- Modify: `src/Optimisarr.Data/MediaFile.cs`, `src/Optimisarr.Data/OptimisarrDbContext.cs`
- Modify: `src/Optimisarr.Api/Library/LibraryInventoryService.cs` (store at probe, clear on change)
- Modify: `src/Optimisarr.Api/Library/CandidateService.cs` (`Describe` feeds `MediaProperties.SubtitleLanguages`)
- Create: migration `src/Optimisarr.Data/Migrations/*_AddSubtitleLanguages.*`
- Test: `tests/Optimisarr.Tests/MediaProbeParseTests.cs`, `LibraryInventoryServiceTests.cs`, `MigrationTests.cs` (existing idempotency test covers the new migration automatically)

**Interfaces:**
- Produces: `MediaProbeResult.SubtitleLanguages : IReadOnlyList<string?>` — positional param inserted **immediately after `SubtitleTrackCount`**; update the `Failure` factory (`Array.Empty<string?>()`) and the `Parse` return in the same edit.
- Produces: `MediaFile.SubtitleLanguages : string?` — comma-separated positional summary, `"und"` standing in for untagged tracks (mirrors `AudioLanguages`), max length 512.

- [ ] **Step 1: Write the failing tests**

```csharp
// MediaProbeParseTests.cs — follow the file's existing ffprobe-JSON fixture style
[Fact]
public void Parse_captures_subtitle_languages_positionally()
{
    const string json = """
        {
          "format": { "format_name": "matroska,webm" },
          "streams": [
            { "codec_type": "video", "codec_name": "hevc" },
            { "codec_type": "subtitle", "codec_name": "subrip", "tags": { "language": "ENG" } },
            { "codec_type": "subtitle", "codec_name": "hdmv_pgs_subtitle" },
            { "codec_type": "subtitle", "codec_name": "subrip", "tags": { "language": "fra" } }
          ]
        }
        """;

    var result = MediaProbeService.Parse(json, ".mkv");

    Assert.Equal(new string?[] { "eng", null, "fra" }, result.SubtitleLanguages);
    Assert.Equal(3, result.SubtitleTrackCount);
}
```

```csharp
// LibraryInventoryServiceTests.cs — mirror the existing AudioLanguages store/clear tests
// (find them with: grep -n "AudioLanguages" tests/Optimisarr.Tests/LibraryInventoryServiceTests.cs)
// 1) a probe stores "eng, und, fra" for the JSON above;
// 2) a scan that sees a changed file nulls SubtitleLanguages alongside AudioLanguages.
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Optimisarr.slnx --filter "FullyQualifiedName~MediaProbeParseTests|FullyQualifiedName~LibraryInventoryServiceTests"`
Expected: compile error — `SubtitleLanguages` missing.

- [ ] **Step 3: Implement**

`MediaProbeService.cs`:
- record: insert `IReadOnlyList<string?> SubtitleLanguages,` after `int SubtitleTrackCount,`; update `Failure` (add `Array.Empty<string?>()` in the matching position).
- `Parse`: add `var subtitleLanguages = new List<string?>();`; in `case "subtitle":` add `subtitleLanguages.Add(ReadLanguageTag(stream));`; pass `subtitleLanguages` in the result.

`MediaFile.cs` — after `SubtitleTrackCount`:

```csharp
    /// <summary>
    /// Comma-separated subtitle track languages in stream order, e.g. "eng, und, fra"
    /// ("und" standing in for an untagged track). Null when the file was probed before
    /// subtitle languages were captured; language rules treat that as unknown and change
    /// nothing.
    /// </summary>
    public string? SubtitleLanguages { get; set; }
```

`OptimisarrDbContext.cs` — next to the `AudioLanguages` config: `entity.Property(file => file.SubtitleLanguages).HasMaxLength(512);`

`LibraryInventoryService.cs`:
- probe store (next to `file.AudioLanguages = …`):

```csharp
            file.SubtitleLanguages = result.SubtitleLanguages.Count > 0
                ? string.Join(", ", result.SubtitleLanguages.Select(language => language ?? "und"))
                : null;
```

- change-clear block (next to `file.AudioLanguages = null;`): `file.SubtitleLanguages = null;`

`CandidateService.cs` `Describe` — append to the `MediaProperties` construction: `TrackLanguages.ParseTrackLanguages(file.SubtitleLanguages)`.

- [ ] **Step 4: Add the migration**

```bash
dotnet ef migrations add AddSubtitleLanguages \
  --project src/Optimisarr.Data --startup-project src/Optimisarr.Api \
  --output-dir Migrations
```

Inspect the generated migration: it must only add the nullable `SubtitleLanguages` TEXT column to `MediaFiles`.

- [ ] **Step 5: Run the full suite** — `dotnet test Optimisarr.slnx` → PASS (including `MigrationTests`).

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat: capture per-track subtitle languages in probe and inventory"
```

---

### Task 5: `Library.KeepSubtitleLanguages` — persistence, API, config portability (+ migration)

**Files:**
- Modify: `src/Optimisarr.Data/Library.cs`, `src/Optimisarr.Data/OptimisarrDbContext.cs`
- Modify: `src/Optimisarr.Api/Library/LibraryRequestParser.cs`, `src/Optimisarr.Api/Endpoints/LibraryEndpoints.cs`, `src/Optimisarr.Api/Program.cs` (`SaveLibraryRequest`, `LibraryDto`), `src/Optimisarr.Api/Library/LibraryRuleResolution.cs`, `src/Optimisarr.Api/Library/ConfigPortabilityService.cs`, `src/Optimisarr.Core/Settings/ConfigSnapshot.cs`
- Create: migration `*_AddKeepSubtitleLanguages.*`
- Test: `tests/Optimisarr.Tests/LibraryRequestParserTests.cs`, `LibraryRuleResolutionTests.cs`, `ConfigPortabilityServiceTests.cs`

**Interfaces:**
- Produces: `Library.KeepSubtitleLanguages : string?` (comma-separated canonical list, max length 256, null = keep all).
- Produces: `ParsedLibrary.KeepSubtitleLanguages : string?` immediately after `KeepAudioLanguages`; `SaveLibraryRequest.KeepSubtitleLanguages : string?` and `LibraryDto.KeepSubtitleLanguages : string?` likewise after their audio twins.
- Produces: `LibraryRuleResolution.Resolve` maps it into `RuleOverrides.KeepSubtitleLanguages`.
- Produces: `LibraryConfigSnapshot` (in `ConfigSnapshot.cs`) gains `string? KeepSubtitleLanguages = null` as the **last** defaulted parameter, so configs exported before this change still import.

- [ ] **Step 1: Write the failing tests**

```csharp
// LibraryRequestParserTests.cs — mirror the existing KeepAudioLanguages cases
[Theory]
[InlineData("eng, FRA", "eng, fra")]
[InlineData("  ", null)]
[InlineData(null, null)]
public void Keep_subtitle_languages_are_normalised(string? input, string? expected)
{
    var request = ValidRequest() with { KeepSubtitleLanguages = input };
    Assert.True(LibraryRequestParser.TryParse(request, out var parsed, out _));
    Assert.Equal(expected, parsed.KeepSubtitleLanguages);
}

[Fact]
public void Invalid_subtitle_language_codes_are_rejected()
{
    var request = ValidRequest() with { KeepSubtitleLanguages = "english" };
    Assert.False(LibraryRequestParser.TryParse(request, out _, out var error));
    Assert.Contains("Subtitle languages", error);
}

// LibraryRuleResolutionTests.cs
[Fact]
public void Keep_subtitle_languages_flow_into_the_resolved_rules()
{
    var library = new Library { RuleProfile = RuleProfile.ConservativeHevc, KeepSubtitleLanguages = "eng, jpn" };
    var rules = LibraryRuleResolution.Resolve(library);
    Assert.Equal(new[] { "eng", "jpn" }, rules.KeepSubtitleLanguages);
}

// ConfigPortabilityServiceTests.cs — extend the existing round-trip test so a library
// with KeepSubtitleLanguages = "eng" exports and re-imports with the value intact.
```

(`ValidRequest()` = whatever helper the parser tests already use to build a passing `SaveLibraryRequest`.)

- [ ] **Step 2: Run to verify failure** — targeted filter as before; expected compile errors.

- [ ] **Step 3: Implement**

`Library.cs` — after `KeepAudioLanguages`:

```csharp
    /// <summary>
    /// Comma-separated ISO 639 codes of the subtitle languages a video job keeps;
    /// tracks in any other language are removed from the output. Null (the default)
    /// keeps every track. Unknown-language tracks are always kept; unlike audio there
    /// is no keep-at-least-one guard, so a file may end with zero subtitles.
    /// </summary>
    public string? KeepSubtitleLanguages { get; set; }
```

`OptimisarrDbContext.cs` — next to the audio twin: `entity.Property(library => library.KeepSubtitleLanguages).HasMaxLength(256);`

`LibraryRequestParser.cs`:
- rename `TryParseKeepAudioLanguages` → `TryParseLanguageList` (it is already track-agnostic) and call it for both fields:

```csharp
        if (!TryParseLanguageList(request.KeepAudioLanguages, out var keepAudioLanguages))
        {
            error = "Audio languages must be comma-separated ISO 639 codes of 2–3 letters (e.g. \"eng, jpn\").";
            return false;
        }

        if (!TryParseLanguageList(request.KeepSubtitleLanguages, out var keepSubtitleLanguages))
        {
            error = "Subtitle languages must be comma-separated ISO 639 codes of 2–3 letters (e.g. \"eng, jpn\").";
            return false;
        }
```

- `ParsedLibrary`: add `string? KeepSubtitleLanguages,` after `KeepAudioLanguages`, and pass `keepSubtitleLanguages` in the constructor call after `keepAudioLanguages`.
- Inside `TryParseLanguageList`, switch to the shared helpers: `var codes = TrackLanguages.ParseLanguageList(value);` (validation logic unchanged).

`Program.cs`: add `string? KeepSubtitleLanguages,` to `SaveLibraryRequest` (after `KeepAudioLanguages`) and to `LibraryDto` (after `KeepAudioLanguages`), and `library.KeepSubtitleLanguages,` in `LibraryDto.From` after the audio line.

`LibraryEndpoints.cs`: in the create block add `KeepSubtitleLanguages = parsed.KeepSubtitleLanguages,` after line 107's audio twin; in the update block add `library.KeepSubtitleLanguages = parsed.KeepSubtitleLanguages;` after line 175's twin.

`LibraryRuleResolution.cs`: after the KeepAudioLanguages mapping add:

```csharp
            KeepSubtitleLanguages = string.IsNullOrWhiteSpace(library.KeepSubtitleLanguages)
                ? null
                : TrackLanguages.ParseLanguageList(library.KeepSubtitleLanguages),
```

`ConfigSnapshot.cs`: append `string? KeepSubtitleLanguages = null);` as the new final parameter of the library snapshot record (currently ends with `KeepAudioLanguages` at line 60).

`ConfigPortabilityService.cs`: in the apply block (line ~130) add `library.KeepSubtitleLanguages = snapshot.KeepSubtitleLanguages;`; in `ToSnapshot` (line ~298) append `library.KeepSubtitleLanguages` as the final argument.

- [ ] **Step 4: Add the migration**

```bash
dotnet ef migrations add AddKeepSubtitleLanguages \
  --project src/Optimisarr.Data --startup-project src/Optimisarr.Api \
  --output-dir Migrations
```

Inspect: only the nullable `KeepSubtitleLanguages` TEXT column on `Libraries`.

- [ ] **Step 5: Run the full suite** — `dotnet test Optimisarr.slnx` → PASS.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat: per-library keep-subtitle-languages setting end to end"
```

---

### Task 6: Verification — exact subtitle retention + container-unchanged gate (pure)

**Files:**
- Modify: `src/Optimisarr.Core/Verification/VerificationInput.cs`
- Modify: `src/Optimisarr.Core/Verification/VerificationEvaluator.cs`
- Test: `tests/Optimisarr.Tests/VerificationEvaluatorTests.cs`

**Interfaces:**
- Produces (new defaulted params on `VerificationInput`, appended at the end): `int SubtitleTracksRemoved = 0`, `bool RequireContainerUnchanged = false`, `string? OriginalContainer = null`, `string? OutputContainer = null`.
- Consumed by Task 7's composition wiring.

- [ ] **Step 1: Write the failing tests** (use the file's existing `PassingInput()`-style helper)

```csharp
[Fact]
public void Planned_subtitle_removal_expects_exactly_the_remaining_tracks()
{
    var input = PassingInput() with
    {
        OriginalSubtitleTrackCount = 3,
        OutputSubtitleTrackCount = 1,
        SubtitleTracksRemoved = 2
    };

    var report = VerificationEvaluator.Evaluate(input, VerificationPolicy.Default);

    Assert.Contains(report.Checks, c => c.Name == "Subtitle tracks" && c.Outcome == CheckOutcome.Passed);
}

[Fact]
public void Losing_an_extra_subtitle_beyond_the_plan_fails_even_when_retention_is_not_required()
{
    // Policy default has RequireSubtitlesRetained = false; the planned-removal
    // contract is stricter than the policy — tightened, never relaxed.
    var input = PassingInput() with
    {
        OriginalSubtitleTrackCount = 3,
        OutputSubtitleTrackCount = 0,
        SubtitleTracksRemoved = 2
    };

    var report = VerificationEvaluator.Evaluate(input, VerificationPolicy.Default);

    Assert.Contains(report.Checks, c => c.Name == "Subtitle tracks" && c.Outcome == CheckOutcome.Failed);
}

[Fact]
public void A_track_cleanup_output_must_keep_the_source_container()
{
    var pass = PassingInput() with
    {
        RequireContainerUnchanged = true,
        OriginalContainer = "matroska,webm",
        OutputContainer = "matroska,webm"
    };
    var fail = pass with { OutputContainer = "mov,mp4,m4a,3gp,3g2,mj2" };

    Assert.Contains(VerificationEvaluator.Evaluate(pass, VerificationPolicy.Default).Checks,
        c => c.Name == "Container unchanged" && c.Outcome == CheckOutcome.Passed);
    Assert.Contains(VerificationEvaluator.Evaluate(fail, VerificationPolicy.Default).Checks,
        c => c.Name == "Container unchanged" && c.Outcome == CheckOutcome.Failed);
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test Optimisarr.slnx --filter "FullyQualifiedName~VerificationEvaluatorTests"` → compile errors.

- [ ] **Step 3: Implement**

`VerificationInput.cs` — append the four defaulted params, with:

```csharp
    // How many subtitle tracks the kept-languages rule removed on purpose; the retention
    // gate then expects exactly that many fewer, regardless of the policy's subtitle flag.
    int SubtitleTracksRemoved = 0,
    // A track-cleanup job promises the container type is untouched; both values are the
    // probes' format_name so a silent remux fails verification.
    bool RequireContainerUnchanged = false,
    string? OriginalContainer = null,
    string? OutputContainer = null);
```

`VerificationEvaluator.cs`:
- `SubtitlesRetained` gains a planned-removal branch **before** the policy check (mirror `AudioRetained`, but with no minimum-one floor):

```csharp
    private static VerificationCheck SubtitlesRetained(VerificationInput input, VerificationPolicy policy)
    {
        var detail = $"Original {input.OriginalSubtitleTrackCount} subtitle track(s), output {input.OutputSubtitleTrackCount}.";

        if (input.SubtitleTracksRemoved > 0)
        {
            // The plan is exact: a crash that drops an extra stream must still fail, and
            // unlike audio a zero-subtitle output is legitimate (no minimum-one floor).
            var expected = Math.Max(input.OriginalSubtitleTrackCount - input.SubtitleTracksRemoved, 0);
            detail += $" {input.SubtitleTracksRemoved} track(s) intentionally removed by the kept-languages rule.";
            return input.OutputSubtitleTrackCount == expected
                ? Pass("Subtitle tracks", detail)
                : Fail("Subtitle tracks", $"{detail} Expected exactly {expected} track(s) after the planned removal.");
        }

        if (!policy.RequireSubtitlesRetained)
        {
            return Pass("Subtitle tracks", $"{detail} Retention not required by policy.");
        }

        return input.OutputSubtitleTrackCount >= input.OriginalSubtitleTrackCount
            ? Pass("Subtitle tracks", detail)
            : Fail("Subtitle tracks", $"{detail} Subtitle tracks were lost.");
    }
```

- Add a container gate, registered in `Evaluate` for time-based media right after the retention checks (`if (!isImage)` block):

```csharp
            if (input.RequireContainerUnchanged)
            {
                checks.Add(ContainerUnchanged(input));
            }
```

```csharp
    // ffprobe's format_name is a demuxer name shared by every extension it serves, so a
    // straight comparison holds exactly when the container type is genuinely the same.
    private static VerificationCheck ContainerUnchanged(VerificationInput input)
    {
        var detail = $"Original \"{input.OriginalContainer}\", output \"{input.OutputContainer}\".";
        return input.OutputContainer is not null
            && string.Equals(input.OriginalContainer, input.OutputContainer, StringComparison.OrdinalIgnoreCase)
            ? Pass("Container unchanged", detail)
            : Fail("Container unchanged", $"{detail} The container type must not change under this profile.");
    }
```

- [ ] **Step 4: Run the full suite** — PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: removal-aware subtitle gate and container-unchanged verification"
```

---

### Task 7: Dispatcher + verification service wiring

**Files:**
- Modify: `src/Optimisarr.Api/Queue/VerificationService.cs` (`OriginalSnapshot` + input construction)
- Modify: `src/Optimisarr.Api/Queue/QueueDispatcher.cs` (`LoadWorkAsync`)
- Test: `tests/Optimisarr.Tests/VerificationPolicyTests.cs` or the existing dispatcher-adjacent pure tests are unaffected; the new snapshot fields are exercised through `VerificationEvaluatorTests` (Task 6). This task's changes are composition-layer; the build plus existing integration-style tests gate it.

**Interfaces:**
- Produces: `OriginalSnapshot` gains `IReadOnlyList<int>? RemovedSubtitleStreamIndexes = null` and `bool ContainerMustMatch = false` (appended, defaulted).
- Consumes: `TranscodeSpecResolver.Resolve(..., sourceSubtitleLanguages)` (Task 3), `MediaProbeResult.SubtitleLanguages` (Task 4), `VerificationInput.SubtitleTracksRemoved/RequireContainerUnchanged/…Container` (Task 6).

- [ ] **Step 1: Implement `OriginalSnapshot`** — append:

```csharp
    // Subtitle-relative indexes the kept-languages rule removed on purpose; verification
    // expects exactly those tracks gone (and, unlike audio, tolerates zero remaining).
    IReadOnlyList<int>? RemovedSubtitleStreamIndexes = null,
    // True for a track-cleanup job, whose promise includes an unchanged container type.
    bool ContainerMustMatch = false);
```

- [ ] **Step 2: Wire `VerificationService.VerifyAsync`** — in the `VerificationInput` construction add:

```csharp
                SubtitleTracksRemoved: reference.RemovedSubtitleStreamIndexes?.Count ?? 0,
                RequireContainerUnchanged: reference.ContainerMustMatch,
                OriginalContainer: originalProbe.Container,
                OutputContainer: outputProbe.Container,
```

- [ ] **Step 3: Wire `QueueDispatcher.LoadWorkAsync`**

Next to the audio-language probe logic (lines ~530–548):

```csharp
        var sourceSubtitleLanguages = TrackLanguages.ParseTrackLanguages(media.SubtitleLanguages);
        var needsSubtitleLanguageProbe = isVideoJob
            && rules.KeepSubtitleLanguages.Count > 0
            && sourceSubtitleLanguages is null
            && (media.SubtitleTrackCount ?? 0) > 0;
```

Fold it into the single-probe gate (`if (needsSubtitleProbe || needsLanguageProbe || needsSubtitleLanguageProbe)`) and, inside, after the audio assignment:

```csharp
            if (needsSubtitleLanguageProbe && probeResult.Success)
            {
                sourceSubtitleLanguages = probeResult.SubtitleLanguages;
            }
```

Pass `sourceSubtitleLanguages` as the new final argument of `TranscodeSpecResolver.Resolve`.

In the `OriginalSnapshot` construction append:

```csharp
            RemovedSubtitleStreamIndexes: spec.RemoveSubtitleStreamIndexes,
            // Track cleanup (no codec, no target container) promises the container type
            // is untouched; verification holds the output to that promise.
            ContainerMustMatch: rules.TargetVideoCodec is null && rules.TargetContainer is null);
```

- [ ] **Step 4: Run the full suite** — `dotnet test Optimisarr.slnx` → PASS with zero warnings.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: wire subtitle removal and container promise through dispatch and verification"
```

---

### Task 8: Queue reasons — `Job.EnqueueReason` (+ migration)

**Files:**
- Modify: `src/Optimisarr.Data/Job.cs`, `src/Optimisarr.Data/OptimisarrDbContext.cs`
- Modify: `src/Optimisarr.Api/Library/JobEnqueueService.cs`
- Modify: `src/Optimisarr.Api/Queue/JobQueries.cs` (`JobDto`)
- Create: migration `*_AddJobEnqueueReason.*`
- Test: `tests/Optimisarr.Tests/JobEnqueueServiceTests.cs`, `JobQueriesTests.cs`

**Interfaces:**
- Produces: `Job.EnqueueReason : string?` (max length 512); `JobDto.EnqueueReason : string?` inserted immediately after `ErrorMessage`.
- Consumed by Task 9's queue UI.

- [ ] **Step 1: Write the failing tests**

```csharp
// JobEnqueueServiceTests.cs — extend the existing enqueue test (it already builds a
// library + probed eligible file with an in-memory/SQLite DbContext; follow its setup)
[Fact]
public async Task Enqueue_records_the_candidate_reason_on_the_job()
{
    // arrange: one eligible probed file (reuse the file's existing fixture pattern)
    // act:    await service.EnqueueEligibleAsync(library, CancellationToken.None);
    var job = await db.Jobs.SingleAsync();
    Assert.False(string.IsNullOrWhiteSpace(job.EnqueueReason));
}

// JobQueriesTests.cs — extend the existing projection test:
// a job saved with EnqueueReason = "h264 → hevc" comes back on JobDto.EnqueueReason.
```

- [ ] **Step 2: Run to verify failure** — compile error, `EnqueueReason` missing.

- [ ] **Step 3: Implement**

`Job.cs` — after `Attempt`:

```csharp
    /// <summary>
    /// Why this job was enqueued — the eligibility reason computed at enqueue time
    /// (e.g. "h264 → hevc", "Remove 2 audio track(s) (fra, deu) not in the kept
    /// languages"), shown in the queue so a row explains itself. Null for jobs that
    /// predate the column.
    /// </summary>
    public string? EnqueueReason { get; set; }
```

`OptimisarrDbContext.cs` — in the Job configuration: `entity.Property(job => job.EnqueueReason).HasMaxLength(512);`

`JobEnqueueService.cs` — in the `db.Jobs.Add(new Job { … })` initialiser add `EnqueueReason = Truncate(candidate.Reason),` and:

```csharp
    // The reason column is bounded; an overlong reason keeps its head, which carries
    // the meaningful part ("Remove N audio track(s) (…").
    private static string? Truncate(string? reason) =>
        reason is { Length: > 512 } ? reason[..512] : reason;
```

`JobQueries.cs` — `JobDto` gains `string? EnqueueReason,` after `ErrorMessage`; projection adds `job.EnqueueReason,` in the matching position.

- [ ] **Step 4: Add the migration**

```bash
dotnet ef migrations add AddJobEnqueueReason \
  --project src/Optimisarr.Data --startup-project src/Optimisarr.Api \
  --output-dir Migrations
```

- [ ] **Step 5: Run the full suite** — PASS.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat: record and expose why each job was enqueued"
```

---

### Task 9: Frontend — library form, profile option, queue reasons, i18n

**Files:**
- Modify: `web/src/lib/api.ts` (`Library`, `SaveLibraryRequest`-equivalent, `Job` types)
- Modify: `web/src/lib/pages/Libraries.svelte`
- Modify: `web/src/lib/pages/Queue.svelte`
- Modify: `web/src/lib/i18n/{en,de,es,fr,it,ja,pt,ru,zh}.ts`

**Interfaces:**
- Consumes: `LibraryDto.KeepSubtitleLanguages` (Task 5), `JobDto.EnqueueReason` (Task 8), `RuleProfile` string `"TrackCleanup"`.

- [ ] **Step 1: api.ts** — add `keepSubtitleLanguages: string | null` next to `keepAudioLanguages` in the library type(s) (line ~88 and the save-request shape), and `enqueueReason: string | null` to the `Job` type (line ~271 block, after `errorMessage`).

- [ ] **Step 2: Libraries.svelte**

- Empty-form default (line ~393): `keepSubtitleLanguages: null,` after the audio twin.
- Edit-form load (line ~536): `keepSubtitleLanguages: library.keepSubtitleLanguages,`
- Save payload (line ~597): `keepSubtitleLanguages: emptyToNull(form.keepSubtitleLanguages),`
- Below the keep-audio-languages block (after line ~1029), the mirrored field:

```svelte
        <div class="mt-4">
          <label class="label" for="lib-keep-subtitle-languages">{i18n.m.libraries.keep_subtitle_langs} <InfoTip text={i18n.m.libraries.keep_subtitle_langs_tip} /></label>
          <input
            id="lib-keep-subtitle-languages"
            class="input"
            type="text"
            placeholder={i18n.m.libraries.keep_subtitle_langs_ph}
            bind:value={form.keepSubtitleLanguages}
          />
          <p class="mt-1 text-xs text-slate-400">{i18n.m.libraries.keep_subtitle_langs_hint}</p>
        </div>
```

- Profile dropdown: find where `profile_remux_cleanup` is offered (`grep -n "profile_remux_cleanup\|ScottsSettings" web/src/lib/pages/Libraries.svelte web/src/lib/pages/Settings.svelte`) and add `TrackCleanup` → `i18n.m.libraries.profile_track_cleanup` in the same list(s); if the setup/preset flow lists `preset_remux_cleanup`, add `preset_track_cleanup` alongside.
- Inline hint when TrackCleanup is selected with both keep fields empty, directly under the profile select:

```svelte
        {#if form.ruleProfile === 'TrackCleanup' && !form.keepAudioLanguages?.trim() && !form.keepSubtitleLanguages?.trim()}
          <p class="mt-1 text-xs text-amber-600 dark:text-amber-400">{i18n.m.libraries.track_cleanup_hint}</p>
        {/if}
```

- [ ] **Step 3: Queue.svelte** — under the row title in both the active-job card (after line ~462's folder line) and the table row (after line ~618), show the reason when present:

```svelte
              {#if job.enqueueReason}
                <div class="truncate text-xs text-slate-400 dark:text-slate-500" title={job.enqueueReason}>{job.enqueueReason}</div>
              {/if}
```

- [ ] **Step 4: i18n** — add to the `libraries` section of every locale (English shown; translate the others in the same register the file already uses — the existing `keep_audio_langs*` entries in each locale are the model):

```ts
    keep_subtitle_langs: 'Keep subtitle languages',
    keep_subtitle_langs_tip:
      'Comma-separated ISO 639 codes (e.g. "eng, jpn"). When a video is optimised or remuxed, subtitle tracks in any other language are removed from the output. Tracks with no language tag are always kept. Unlike audio, all subtitles are removed if none match — a file may end with no subtitles. The original is untouched until every verification gate passes.',
    keep_subtitle_langs_ph: 'All languages (e.g. eng)',
    keep_subtitle_langs_hint: 'Leave empty to keep every subtitle track. Unknown-language tracks are never removed.',
    profile_track_cleanup: 'Track cleanup',
    preset_track_cleanup: 'Remove unwanted audio/subtitle languages only — no re-encode, container unchanged.',
    track_cleanup_hint: 'This profile only removes tracks: set "Keep audio languages" and/or "Keep subtitle languages", or every file will be skipped.',
```

Reference translations (keep terminology consistent with each file's existing `keep_audio_langs*` strings):

| key | de | es | fr |
|---|---|---|---|
| keep_subtitle_langs | 'Untertitelsprachen behalten' | 'Conservar idiomas de subtítulos' | 'Langues de sous-titres à conserver' |
| profile_track_cleanup | 'Spur-Bereinigung' | 'Limpieza de pistas' | 'Nettoyage des pistes' |

| key | it | ja | pt | ru | zh |
|---|---|---|---|---|---|
| keep_subtitle_langs | 'Mantieni lingue sottotitoli' | '保持する字幕言語' | 'Manter idiomas de legendas' | 'Сохраняемые языки субтитров' | '保留字幕语言' |
| profile_track_cleanup | 'Pulizia tracce' | 'トラッククリーンアップ' | 'Limpeza de faixas' | 'Очистка дорожек' | '轨道清理' |

Translate `keep_subtitle_langs_tip/_ph/_hint`, `preset_track_cleanup`, and `track_cleanup_hint` in full for every locale following the sentence patterns of that locale's `keep_audio_langs_tip/_hint` and `preset_remux_cleanup`.

- [ ] **Step 5: Check** — `cd web && npm run check` → zero errors, zero warnings. Then `npm run build` and `dotnet build Optimisarr.slnx` to confirm nothing downstream broke.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat: track-cleanup profile, subtitle languages, and queue reasons in the UI"
```

---

### Task 10: Docs + CHANGELOG + final gates

**Files:**
- Modify: `CHANGELOG.md` (Unreleased), `docs/setup/configuration.md` (the section that documents Keep audio languages — mirror it for subtitles and describe the TrackCleanup profile)

- [ ] **Step 1: CHANGELOG (Unreleased → Added)**

```markdown
- **Track cleanup profile**: a new rule profile that only removes audio/subtitle tracks
  outside the library's kept languages — no re-encode, no container change. Files with
  nothing to remove are skipped with a clear reason.
- **Keep subtitle languages**: a per-library rule (all profiles) that strips subtitle
  tracks not in the kept languages. Unknown-language tracks are never removed; unlike
  audio, a file may legitimately end with zero subtitles. Verification now expects
  exactly the planned subtitle retention, and track-cleanup outputs must keep the
  source container.
- **Queue reasons**: every queued job records why it is queued (e.g. "Remove 2 audio
  track(s) (fra, deu) not in the kept languages") and the queue shows it per row.
```

- [ ] **Step 2: configuration.md** — extend the kept-audio-languages docs with the subtitle rule (no-guard semantics spelled out) and a short "Track cleanup" profile subsection (what it does, that removing a track rewrites the file via stream copy, that container/codecs are untouched).

- [ ] **Step 3: Full Definition-of-Done gates**

```bash
dotnet build Optimisarr.slnx          # zero warnings
dotnet test  Optimisarr.slnx          # fully green
cd web && npm run check && npm run build
```

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "docs: track cleanup, subtitle languages, and queue reasons"
```

---

## Self-review notes

- Spec coverage: profile (§1 → Tasks 2, 9), subtitle rule (§2 → Tasks 1, 3, 4, 5), verification (§3 → Tasks 6, 7), queue reasons (§4 → Tasks 8, 9), plumbing (§5 → Tasks 5, 9, 10). Reason strings name languages (§4) via `RemovalReason` in Tasks 2–3.
- Every migration lands in the same commit as its schema change (Tasks 4, 5, 8); `MigrationTests.Migrations_apply_to_an_empty_sqlite_database` gates idempotency each time.
- `RemuxCleanup` behaviour is preserved verbatim for audio; subtitles extend it symmetrically.
- The audio keep-at-least-one guard is untouched; subtitles deliberately have none (user decision, spec §2).
