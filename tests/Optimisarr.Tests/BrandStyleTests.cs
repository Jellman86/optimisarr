using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Core.Settings;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class BrandStyleTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;

    public BrandStyleTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task The_brand_defaults_to_precession_and_a_choice_round_trips()
    {
        await using var db = new OptimisarrDbContext(_options);
        var store = new SettingsStore(db);
        Assert.Equal(BrandStyle.Precession, await store.GetBrandStyleAsync(CancellationToken.None));
        await store.SetBrandStyleAsync(BrandStyle.Stellar, CancellationToken.None);
        Assert.Equal(BrandStyle.Stellar, await store.GetBrandStyleAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_unrecognised_stored_brand_reads_as_the_default()
    {
        await using (var seed = new OptimisarrDbContext(_options))
        {
            seed.AppSettings.Add(new AppSetting { Key = SettingKeys.BrandStyle, Value = "neon", UpdatedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }
        await using var db = new OptimisarrDbContext(_options);
        Assert.Equal(BrandStyle.Precession, await new SettingsStore(db).GetBrandStyleAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("stellar", BrandStyle.Stellar)]
    [InlineData("Precession", BrandStyle.Precession)]
    public void Wire_names_parse_without_regard_to_case(string wire, BrandStyle expected)
    {
        Assert.True(BrandStyles.TryParse(wire, out var parsed));
        Assert.Equal(expected, parsed);
        Assert.Equal(wire.ToLowerInvariant(), BrandStyles.WireName(parsed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("galaxy")]
    public void Anything_else_is_not_a_brand(string? wire) => Assert.False(BrandStyles.TryParse(wire, out _));

    [Fact]
    public void The_brand_travels_with_a_settings_backup() =>
        Assert.Contains(SettingKeys.BrandStyle, SettingsStore.PortableSettingKeys);

    public void Dispose() => _connection.Dispose();
}

/// <summary>
/// The brand used to live only in each browser's storage, so a sidecar's own page — on another
/// origin — could never show the icon the operator picked. It is now a server setting that workers
/// learn on every check-in.
/// </summary>
[Collection(TokenedApiCollection.Name)]
public sealed class BrandStyleEndpointTests(AdminTokenAuthEndpointTests.TokenedApi api)
{
    private HttpClient Admin()
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        return client;
    }

    [Fact]
    public async Task The_appearance_route_reads_and_writes_the_brand()
    {
        var admin = Admin();
        try
        {
            using var saved = await admin.PutAsJsonAsync("/api/settings/appearance", new { brandStyle = "stellar" });
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var read = await (await admin.GetAsync("/api/settings/appearance")).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("stellar", read.GetProperty("brandStyle").GetString());

            using var refused = await admin.PutAsJsonAsync("/api/settings/appearance", new { brandStyle = "neon" });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            read = await (await admin.GetAsync("/api/settings/appearance")).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("stellar", read.GetProperty("brandStyle").GetString());
        }
        finally
        {
            (await admin.PutAsJsonAsync("/api/settings/appearance", new { brandStyle = "precession" })).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task The_appearance_route_needs_the_admin_token() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/settings/appearance")).StatusCode);
}
