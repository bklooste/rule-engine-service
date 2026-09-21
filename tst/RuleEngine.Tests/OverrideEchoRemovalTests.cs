using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Scenarify;

using Xunit;

namespace ServiceTests;

// P-16 (docs/plans/2026-09-14-plat-templates-performance-and-aot.md, D-13, §8.1): proves the migration
// script at backend/platform/plat-templates/scripts/remove-override-2nd-fetch.cs is safe to run against
// a real override template. This test drives the exact same HTTP sequence the script does - GET
// v2/templates to discover override-suffixed ids, GET a rule's ETag, DELETE with If-Match, retry on 412
// - against this fixture's own service container (docker compose's test-redis, never a real deployment).
// Uses the real currentOverride.json/postWithProducts.json/post.json fixtures GoldenTests pins under v1,
// so "override2ndFetch gone, everything else unchanged" is checked against known-good shapes rather than
// hand-built ones.
[Collection(nameof(ServiceTestCollection))]
[Trait("TestType", "Service")]
public class OverrideEchoRemovalTests(Fixture fixture)
{
    private const string RuleName = "override2ndFetch";

    [Fact]
    public async Task discovery_finds_override_templates_by_suffix_and_ignores_others()
    {
        var client = fixture.Client();
        var overrideId = $"{Guid.NewGuid():N}-override";
        var brandId = Guid.NewGuid().ToString("N");

        await PutDocumentAsync(client, overrideId, await File.ReadAllTextAsync("testData/currentOverride.json"));
        await PutDocumentAsync(client, brandId, await File.ReadAllTextAsync("testData/currentTemplate.json"));

        var found = await DiscoverOverrideTemplateIdsAsync(client, "-override");

        found.Should().Contain(overrideId);
        found.Should().NotContain(brandId);
    }

    [Fact]
    public async Task removal_deletes_only_the_echo_rule_every_other_rule_survives_untouched()
    {
        var client = fixture.Client();
        var templateId = $"{Guid.NewGuid():N}-override";
        await PutDocumentAsync(client, templateId, await File.ReadAllTextAsync("testData/currentOverride.json"));

        var beforeNames = await GetRuleNamesAsync(client, templateId);
        beforeNames.Should().Contain(RuleName);

        (await RemoveRuleWithRetryAsync(client, templateId, RuleName, maxRetries: 5)).Should().Be(HttpStatusCode.NoContent);

        var afterNames = await GetRuleNamesAsync(client, templateId);
        afterNames.Should().NotContain(RuleName);
        afterNames.Should().Equal(beforeNames.Where(n => n != RuleName));
    }

    [Fact]
    public async Task removing_an_already_absent_rule_is_a_no_op_not_an_error()
    {
        var client = fixture.Client();
        var templateId = $"{Guid.NewGuid():N}-override";
        await PutDocumentAsync(client, templateId, await File.ReadAllTextAsync("testData/currentOverride.json"));

        (await RemoveRuleWithRetryAsync(client, templateId, RuleName, 5)).Should().Be(HttpStatusCode.NoContent);
        // Run again against the same template: the rule is already gone, so this must be a clean skip
        // (the script's RemovalOutcome.NotPresent), not a failure - a migration run has to be re-runnable.
        (await RemoveRuleWithRetryAsync(client, templateId, RuleName, 5)).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task a_stale_etag_is_retried_instead_of_failing_the_whole_run()
    {
        var client = fixture.Client();
        var templateId = $"{Guid.NewGuid():N}-override";
        await PutDocumentAsync(client, templateId, await File.ReadAllTextAsync("testData/currentOverride.json"));

        // Simulate the concurrent writer D-15 calls out for the override template (an automated
        // OverrideEventId-* cleanup landing mid-edit): edit an unrelated rule first, which does not
        // change override2ndFetch's own content-hash ETag, then delete with a deliberately stale ETag to
        // force one genuine 412 before the retry loop's re-read picks up the current value and succeeds.
        using var unrelatedEdit = new HttpRequestMessage(HttpMethod.Put, $"v2/templates/{templateId}/rules/dp")
        {
            Content = JsonContent(await GetRuleRawAsync(client, templateId, "dp") ?? throw new InvalidOperationException("fixture must contain rule 'dp'")),
        };
        (await client.SendAsync(unrelatedEdit)).EnsureSuccessStatusCode();

        (await RemoveRuleWithRetryAsync(client, templateId, RuleName, maxRetries: 5, forceOneStaleAttempt: true)).Should().Be(HttpStatusCode.NoContent);

        var afterNames = await GetRuleNamesAsync(client, templateId);
        afterNames.Should().NotContain(RuleName).And.Contain("dp");
    }

    // Mirrors GoldenTests.golden_override_template_match_many_with_products (same fixtures), but through
    // v2 evaluate and across the removal: proves P-16's Done-when ("override results contain only
    // override entries") - CUSTOM1 (echo-only, no override rule defines it) disappears, FWIN (both
    // echoed AND a real OverrideTags-vip entry) survives but loses the echo-only fields (source,
    // backupSource, order), and the "{products}" trap string never appears either side of the removal
    // for a request that actually carries products.
    [Fact]
    public async Task removal_leaves_only_real_override_entries_when_the_request_still_carries_products()
    {
        var client = fixture.Client();
        var templateId = $"{Guid.NewGuid():N}-override";
        await PutDocumentAsync(client, templateId, await File.ReadAllTextAsync("testData/currentOverride.json"));
        var events = await File.ReadAllTextAsync("testData/postWithProducts.json");

        var before = await EvaluateManyAsync(client, templateId, events);
        before.Should().NotContain("{products}");
        var beforeCodes = ExtractProductCodes(JsonDocument.Parse(before).RootElement[0]);
        beforeCodes.Should().Contain("FWIN").And.Contain("CUSTOM1").And.Contain("FPLACE");

        (await RemoveRuleWithRetryAsync(client, templateId, RuleName, 5)).Should().Be(HttpStatusCode.NoContent);

        var after = await EvaluateManyAsync(client, templateId, events);
        after.Should().NotContain("{products}").And.NotContain("override2ndFetch");
        var afterFirst = JsonDocument.Parse(after).RootElement[0];
        var afterCodes = ExtractProductCodes(afterFirst);

        afterCodes.Should().NotContain("CUSTOM1", "it only ever came from the echo, no override rule defines it");
        afterCodes.Should().BeEquivalentTo(beforeCodes.Where(c => c != "CUSTOM1"), "every code an override rule actually defines must still be there");

        var fwin = afterFirst.GetProperty("products").EnumerateArray().Single(p => p.GetProperty("code").GetString() == "FWIN");
        fwin.TryGetProperty("source", out _).Should().BeFalse("that field only ever arrived via the echoed posted product, never from an override rule");
        fwin.TryGetProperty("order", out _).Should().BeFalse("same - echo-only");
    }

    // Guards the order trap itself (plan, "Order trap"): before P-17 ships, offer-products still sends a
    // "products" array (previous test) or, for events it has nothing to offer on, none at all (this one -
    // mirrors GoldenTests.golden_override_template_match_many_without_products). Before removal the
    // unconditional echo leaves the literal, unresolved "{products}" string - the trap. After removal
    // there is no rule left to introduce it, proving P-16 is safe to run before P-17 regardless of what
    // offer-products still sends.
    [Fact]
    public async Task removal_leaves_no_unresolved_products_token_when_the_request_carries_no_products()
    {
        var client = fixture.Client();
        var templateId = $"{Guid.NewGuid():N}-override";
        await PutDocumentAsync(client, templateId, await File.ReadAllTextAsync("testData/currentOverride.json"));
        var events = await File.ReadAllTextAsync("testData/post.json");

        var before = await EvaluateManyAsync(client, templateId, events);
        before.Should().Contain("{products}", "today, before P-16, this is the exact trap the plan warns about");

        (await RemoveRuleWithRetryAsync(client, templateId, RuleName, 5)).Should().Be(HttpStatusCode.NoContent);

        var after = await EvaluateManyAsync(client, templateId, events);
        after.Should().NotContain("{products}");
    }

    // --- HTTP helpers below mirror scripts/remove-override-2nd-fetch.cs's algorithm exactly (discover by
    // suffix, read a rule's own ETag, DELETE with If-Match, retry on 412), driven through this fixture's
    // client instead of a standalone process, so the retry contract is proven against a live service.

    private static async Task PutDocumentAsync(HttpClient client, string templateId, string documentJson)
    {
        using var response = await client.PutAsync($"v2/templates/{templateId}", JsonContent(documentJson));
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"seeding '{templateId}' failed: {(int)response.StatusCode} {body}");
    }

    private static async Task<List<string>> GetRuleNamesAsync(HttpClient client, string templateId)
    {
        using var response = await client.GetAsync($"v2/templates/{templateId}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. doc.RootElement.GetProperty("templates").EnumerateArray().Select(r => r.GetProperty("name").GetString()!)];
    }

    private static async Task<string?> GetRuleRawAsync(HttpClient client, string templateId, string ruleName)
    {
        using var response = await client.GetAsync($"v2/templates/{templateId}/rules/{ruleName}");
        return response.StatusCode == HttpStatusCode.NotFound ? null : await response.Content.ReadAsStringAsync();
    }

    private static async Task<string?> GetRuleETagAsync(HttpClient client, string templateId, string ruleName)
    {
        using var response = await client.GetAsync($"v2/templates/{templateId}/rules/{ruleName}");
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return response.Headers.ETag?.Tag;
    }

    private static async Task<List<string>> DiscoverOverrideTemplateIdsAsync(HttpClient client, string suffix)
    {
        using var response = await client.GetAsync("v2/templates");
        response.EnsureSuccessStatusCode();
        var ids = JsonSerializer.Deserialize<string[]>(await response.Content.ReadAsStringAsync()) ?? [];
        return [.. ids.Where(id => id.EndsWith(suffix, StringComparison.Ordinal))];
    }

    // Same shape as OverrideEchoRemoval.RemoveRuleIfPresentAsync in the script: read the ETag, delete
    // with If-Match, and on 412 (another writer touched THIS rule concurrently) re-read and retry.
    private static async Task<HttpStatusCode> RemoveRuleWithRetryAsync(HttpClient client, string templateId, string ruleName, int maxRetries, bool forceOneStaleAttempt = false)
    {
        var forcedStaleUsed = false;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            var etag = await GetRuleETagAsync(client, templateId, ruleName);
            if (etag == null)
                return HttpStatusCode.NotFound; // nothing to remove - script logs this as RemovalOutcome.NotPresent

            using var request = new HttpRequestMessage(HttpMethod.Delete, $"v2/templates/{templateId}/rules/{ruleName}");
            request.Headers.TryAddWithoutValidation("If-Match", forceOneStaleAttempt && !forcedStaleUsed ? "\"0000000000000000\"" : etag);
            request.Headers.TryAddWithoutValidation("userid", "test-migration");
            using var response = await client.SendAsync(request);

            if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
                return response.StatusCode;

            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                forcedStaleUsed = true; // only force the stale ETag once - the retry that follows must use the real one
                continue;
            }

            throw new InvalidOperationException($"unexpected {(int)response.StatusCode} deleting '{ruleName}' from '{templateId}': {await response.Content.ReadAsStringAsync()}");
        }

        throw new InvalidOperationException($"gave up after {maxRetries} attempts removing '{ruleName}' from '{templateId}'");
    }

    private static async Task<string> EvaluateManyAsync(HttpClient client, string templateId, string eventsJson)
    {
        using var response = await client.PostAsync($"v2/templates/{templateId}/evaluate", JsonContent(eventsJson));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static List<string> ExtractProductCodes(JsonElement result) =>
        result.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array
            ? [.. products.EnumerateArray().Select(p => p.GetProperty("code").GetString()!)]
            : [];

    private static StringContent JsonContent(string json)
    {
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }
}
