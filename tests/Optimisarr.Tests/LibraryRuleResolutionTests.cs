using Optimisarr.Api.Library;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class LibraryRuleResolutionTests
{
    [Fact]
    public void A_library_keeps_the_profile_efficiency_floor_by_default()
    {
        var library = new Library { Name = "Films", Path = "/data/films", RuleProfile = RuleProfile.ConservativeHevc };

        var rules = LibraryRuleResolution.Resolve(library);

        Assert.True(library.SkipEfficientSources);                         // default on
        Assert.NotNull(rules.MinSourceBitsPerPixelSecond);                 // the profile's floor applies
    }

    [Fact]
    public void Turning_off_skip_efficient_sources_disables_the_floor_for_that_library()
    {
        var library = new Library
        {
            Name = "Films", Path = "/data/films", RuleProfile = RuleProfile.ConservativeHevc,
            SkipEfficientSources = false
        };

        var rules = LibraryRuleResolution.Resolve(library);

        Assert.Null(rules.MinSourceBitsPerPixelSecond);   // floor removed, so every source reaches the encoder
    }
}
