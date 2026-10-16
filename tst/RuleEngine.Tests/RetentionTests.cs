using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;

using Templates;
using Templates.Data;

namespace ServiceTests;

// Design 2.1, L2: history retention per template id pattern (D4), on a clock the test controls.
[Collection(nameof(ServiceTestCollection))]
[Trait("TestType", "Service")]
public class RetentionTests(Fixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task promotion_snapshots_older_than_90_days_survive_a_write()
    {
        var (repository, clock) = NewRepository(new() { Rules = [new() { Pattern = "promotion-*", RetentionDays = 0 }] });
        var id = "promotion-" + Guid.NewGuid().ToString("N");

        await WriteAsync(repository, id);
        clock.Advance(TimeSpan.FromDays(200));
        await WriteAsync(repository, id);
        await WriteAsync(repository, id);

        (await RetainedAsync(repository, id)).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task b_snapshots_older_than_90_days_are_pruned()
    {
        var (repository, clock) = NewRepository(new() { Rules = [new() { Pattern = "promotion-*", RetentionDays = 0 }] });
        var id = "b-" + Guid.NewGuid().ToString("N");

        await WriteAsync(repository, id);                 // v1, day 0
        clock.Advance(TimeSpan.FromDays(1));
        await WriteAsync(repository, id);                 // v2, day 1
        clock.Advance(TimeSpan.FromDays(99));
        await WriteAsync(repository, id);                 // v3, day 100: the window opens at day 10

        // v1 is gone; v2 was in force when the window opened, so it stays.
        (await RetainedAsync(repository, id)).Should().Equal(2, 3);
        (await repository.GetVersionDocumentAsync(id, 1)).Should().BeNull();
    }

    // Design 2.1, bug 1: 2.0 pruned by write time alone, so the write of v2 deleted v1, the version that
    // was in force until that moment, and "roll back my last edit" failed.
    [Fact]
    public async Task the_version_in_force_at_the_cutoff_is_kept()
    {
        var (repository, clock) = NewRepository(new());
        var id = "b-" + Guid.NewGuid().ToString("N");

        await WriteAsync(repository, id);
        clock.Advance(TimeSpan.FromDays(100));
        await WriteAsync(repository, id);

        (await RetainedAsync(repository, id)).Should().Equal(1, 2);
        (await repository.GetVersionDocumentAsync(id, 1)).Should().NotBeNull();
    }

    // The configuration a promotions consumer runs: only the last 5 changes matter, whatever their age.
    [Fact]
    public async Task min_versions_keeps_the_newest_regardless_of_age()
    {
        var (repository, clock) = NewRepository(new() { Rules = [new() { Pattern = "promotion-*", RetentionDays = 1, MinVersions = 5 }] });
        var id = "promotion-" + Guid.NewGuid().ToString("N");

        for (var i = 0; i < 8; i++)
        {
            await WriteAsync(repository, id);
            clock.Advance(TimeSpan.FromDays(30));
        }

        (await RetainedAsync(repository, id)).Should().Equal(4, 5, 6, 7, 8);
    }

    [Fact]
    public async Task pruning_a_version_removes_its_meta_and_group_index_entries()
    {
        var (repository, clock) = NewRepository(new() { RetentionDays = 1 });
        var id = "b-" + Guid.NewGuid().ToString("N");

        await repository.CommitAsync(id, Doc(), 0, ["g1", "g2"]);   // v1
        await repository.CommitAsync(id, Doc(), 1, ["g1"]);         // v2
        clock.Advance(TimeSpan.FromDays(10));
        await repository.CommitAsync(id, Doc(), 2, ["g2"]);         // v3 prunes v1; v2 was in force at the cutoff

        var db = repository.Database;
        (await RetainedAsync(repository, id)).Should().Equal(2, 3);
        (await db.HashKeysAsync(repository.GetMetaKey(id))).Select(k => (long)k).Order().Should().Equal(2, 3);
        (await db.SortedSetRangeByRankAsync(repository.GetGroupIndexKey(id))).Select(m => (string)m!).Should().Equal(
            Repository.GroupIndexMember("g1", 2), Repository.GroupIndexMember("g2", 3));
    }

    [Fact]
    public void the_first_matching_rule_wins()
    {
        var options = new TemplateHistoryOptions
        {
            Rules =
            [
                new() { Pattern = "promotion-vip-*", RetentionDays = 0 },
                new() { Pattern = "promotion-*", RetentionDays = 1, MinVersions = 5 },
                new() { Pattern = "*", RetentionDays = 7 },
            ],
        };

        options.Resolve("promotion-vip-1").RetentionDays.Should().Be(0);
        options.Resolve("promotion-vip-1").MinVersions.Should().Be(0, "no merging across matching rules");
        options.Resolve("promotion-1").Should().Be(new TemplateHistoryPolicy(1, 5, 0));
        options.Resolve("b-acme").RetentionDays.Should().Be(7);
        options.Resolve("Promotion-1").RetentionDays.Should().Be(7, "matching is case-sensitive");
    }

    [Fact]
    public void an_unset_rule_field_takes_the_default()
    {
        var options = new TemplateHistoryOptions
        {
            RetentionDays = 30,
            MinVersions = 2,
            ExpiredRuleRetentionDays = 3,
            Rules = [new() { Pattern = "promotion-*", MinVersions = 5 }],
        };

        options.Resolve("promotion-1").Should().Be(new TemplateHistoryPolicy(30, 5, 3));
        options.Resolve("b-acme").Should().Be(new TemplateHistoryPolicy(30, 2, 3));
    }

    [Theory]
    [InlineData("promotion-*", "promotion-", true)]
    [InlineData("promotion-*", "promotion-summer", true)]
    [InlineData("promotion-*", "promotions", false)]
    [InlineData("*-promo", "b-acme-promo", true)]
    [InlineData("a*b*c", "a-b-x-c", true)]
    [InlineData("a*b*c", "a-c-b", false)]
    [InlineData("exact", "exact", true)]
    [InlineData("exact", "exactly", false)]
    [InlineData("*", "", true)]
    public void a_pattern_star_matches_any_run_of_characters(string pattern, string id, bool matches) =>
        TemplateHistoryRule.Matches(pattern, id).Should().Be(matches);

    // The service under test is started with these env vars (Fixture.Settings); the service assembly binds
    // them with the configuration binder source generator, the path a NativeAOT build takes.
    [Fact]
    public void options_bind_from_env_var_names()
    {
        var options = fixture.Services.GetRequiredService<IOptions<TemplateHistoryOptions>>().Value;

        options.RetentionDays.Should().Be(90);
        options.Rules.Should().HaveCount(2);
        options.Rules[0].Should().BeEquivalentTo(new TemplateHistoryRule { Pattern = "promotion-*", RetentionDays = 1, MinVersions = 5, ExpiredRuleRetentionDays = 380 });
        options.Rules[1].Should().BeEquivalentTo(new TemplateHistoryRule { Pattern = "loyalty-*", RetentionDays = 0 });
        options.Resolve("promotion-x").Should().Be(new TemplateHistoryPolicy(1, 5, 380));
        options.Resolve("b-x").Should().Be(new TemplateHistoryPolicy(90, 0, 0));
    }

    [Fact]
    public void a_negative_retention_fails_validation()
    {
        var result = new TemplateHistoryOptionsValidator().Validate(null, new TemplateHistoryOptions
        {
            RetentionDays = -1,
            Rules = [new() { Pattern = "" }, new() { Pattern = "p-*", MinVersions = -2 }],
        });

        result.Failed.Should().BeTrue();
        result.Failures.Should().BeEquivalentTo(
            "TemplateHistory:RetentionDays must be 0 or more (was -1)",
            "TemplateHistory:Rules:0:Pattern must not be empty",
            "TemplateHistory:Rules:1:MinVersions must be 0 or more (was -2)");
    }

    [Fact]
    public void a_negative_retention_fails_at_start()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("TemplateHistory:Rules:0:RetentionDays", "-1"));

        var start = () => factory.CreateClient();

        start.Should().Throw<OptionsValidationException>().WithMessage("*TemplateHistory:Rules:0:RetentionDays must be 0 or more*");
    }

    private (Repository Repository, FakeTimeProvider Clock) NewRepository(TemplateHistoryOptions options)
    {
        var multiplexer = fixture.Services.GetRequiredService<ConnectionMultiplexer>();
        var prefix = $"test-retention-{Guid.NewGuid():N}:";
        var clock = new FakeTimeProvider(Start);
        return (new Repository(() => multiplexer.GetDatabase().WithKeyPrefix(prefix), options, "{tenant}-templates", clock), clock);
    }

    private static async Task WriteAsync(Repository repository, string id)
    {
        var stored = await repository.ReadAsync(id);
        (await repository.CommitAsync(id, Doc(), stored.Version, [])).Should().NotBeNull();
    }

    private static async Task<long[]> RetainedAsync(Repository repository, string id)
    {
        var versions = (await repository.ListVersionsAsync(id)).Select(v => v.Version).Order().ToArray();
        foreach (var version in versions)
            (await repository.GetVersionDocumentAsync(id, version)).Should().NotBeNull($"listed version {version} must have its snapshot");
        return versions;
    }

    private static Template Doc() =>
        new([new DynamicTemplate("r1", JsonDocument.Parse("""{ "eventId": "x" }""").RootElement, JsonDocument.Parse("""{ "response": "x" }""").RootElement)], "code");
}
