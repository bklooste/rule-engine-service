using System.Net;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using Templates.Data;

namespace Templates.Api;

// P-13's rollback response, moved here with the history routes it belongs to (A10).
public record RollbackResult([property: JsonPropertyName("version")] long Version, [property: JsonPropertyName("template")] Template Template);

// Design 2.1, D5: the group routes' bodies.
public record GroupWriteRequest(
    [property: JsonPropertyName("aggregationField")] string? AggregationField,
    [property: JsonPropertyName("rules")] DynamicTemplate[]? Rules);

public record GroupRules([property: JsonPropertyName("rules")] DynamicTemplate[] Rules);

public record GroupRollbackResult([property: JsonPropertyName("version")] long Version, [property: JsonPropertyName("rules")] DynamicTemplate[] Rules);

// P-14: v2's document, rule-level and history routes. P-13 built versions/history/rollback under v1
// because v2 did not exist yet; the repository methods are unchanged, they are simply exposed here now.
//
//   GET    v2/templates                          list every known template id (A11)
//   GET    v2/templates/{id}                     document + ETag (A8), expired rules pruned (D-16)
//   PUT    v2/templates/{id}                     compile-validated (A7), If-Match (A8), changedBy from userid (A9)
//   DELETE v2/templates/{id}                     (A11)
//   GET    v2/templates/{id}/rules/{ruleName}    rule + its own ETag
//   PUT    v2/templates/{id}/rules/{ruleName}    exact-name rule edit, per-rule If-Match (D-15)
//   DELETE v2/templates/{id}/rules/{ruleName}
//   GET    v2/templates/{id}/versions            (A10); ?limit, ?before (design 2.1, D3)
//   GET    v2/templates/{id}/versions/{n}
//   POST   v2/templates/{id}/rollback/{n}
//   GET    v2/templates/{id}/groups/{group}                    design 2.1, D5: the group + its group ETag
//   PUT    v2/templates/{id}/groups/{group}                    replace every rule of the group
//   DELETE v2/templates/{id}/groups/{group}
//   GET    v2/templates/{id}/groups/{group}/versions           versions that changed the group
//   GET    v2/templates/{id}/groups/{group}/versions/{n}       the group as it stood at version n
//   POST   v2/templates/{id}/groups/{group}/rollback/{n}       restore the group as of version n
public static class APIv2Templates
{
    // A9: the gateway in front of the service forwards the caller's identity in a header; its name is
    // Templates:ChangedByHeader (default "userid"). The service does no authentication of its own.
    private static string? ChangedBy(HttpRequest request, IOptions<TemplatesOptions> options) =>
        request.Headers.TryGetValue(options.Value.ChangedByHeader, out var v) && !string.IsNullOrWhiteSpace(v) ? v.ToString() : null;

    public static void AddV2TemplateApiMaps(this IEndpointRouteBuilder app)
    {
        app.MapGet("v2/templates", async (V2Logic logic) => Results.Ok(await logic.ListAsync()))
            .Produces<string[]>()
            .WithTags("Templates-V2");

        app.MapGet("v2/templates/{templateId}", async (
            V2Logic logic,
            [FromRoute] string templateId,
            HttpResponse response) =>
        {
            // Read straight through, never from the compiled cache: an admin's ETag has to describe the
            // version that is in Redis right now, or the If-Match it comes back with is meaningless.
            var loaded = await logic.GetAsync(templateId);
            if (loaded == null)
                return APIv2.NoSuchTemplate(templateId);

            response.Headers.ETag = V2Logic.DocumentETag(loaded.Value.Version);
            response.Headers[APIv2.VersionHeader] = loaded.Value.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Ok(loaded.Value.Template);
        })
        .Produces<Template>()
        .ProducesProblem((int)HttpStatusCode.NotFound)
        .WithTags("Templates-V2");

        app.MapPut("v2/templates/{templateId}", async (
            V2Logic logic,
            [FromRoute] string templateId,
            [FromBody] Template template,
            HttpResponse response,
            IOptions<TemplatesOptions> templatesOptions,
            HttpRequest request,
            [FromHeader(Name = "If-Match")] string? ifMatch = null) =>
        {
            var result = await logic.PutAsync(templateId, template, V2Logic.NormalizeIfMatch(ifMatch), ChangedBy(request, templatesOptions));
            return Respond(result, response, () => Results.Ok(new WriteAck(result.Version)));
        })
        .Produces<WriteAck>()
        .ProducesValidationProblem()
        .ProducesProblem((int)HttpStatusCode.PreconditionFailed)
        .WithTags("Templates-V2");

        app.MapDelete("v2/templates/{templateId}", async (
            V2Logic logic,
            [FromRoute] string templateId,
            HttpResponse response,
            [FromHeader(Name = "If-Match")] string? ifMatch = null) =>
        {
            var result = await logic.DeleteAsync(templateId, V2Logic.NormalizeIfMatch(ifMatch));
            return Respond(result, response, Results.NoContent, templateId);
        })
        .Produces((int)HttpStatusCode.NoContent)
        .ProducesProblem((int)HttpStatusCode.NotFound)
        .ProducesProblem((int)HttpStatusCode.PreconditionFailed)
        .WithTags("Templates-V2");

        app.MapGet("v2/templates/{templateId}/rules/{ruleName}", async (
            V2Logic logic,
            [FromRoute] string templateId,
            [FromRoute] string ruleName,
            HttpResponse response) =>
        {
            var found = await logic.GetRuleAsync(templateId, ruleName);
            if (found == null)
                return Results.Problem(detail: $"no rule '{ruleName}' in template '{templateId}'", statusCode: (int)HttpStatusCode.NotFound, title: "No such rule");

            response.Headers.ETag = found.Value.ETag;
            return Results.Ok(found.Value.Rule);
        })
        .Produces<DynamicTemplate>()
        .ProducesProblem((int)HttpStatusCode.NotFound)
        .WithTags("Templates-V2");

        app.MapPut("v2/templates/{templateId}/rules/{ruleName}", async (
            V2Logic logic,
            [FromRoute] string templateId,
            [FromRoute] string ruleName,
            [FromBody] DynamicTemplate rule,
            HttpResponse response,
            IOptions<TemplatesOptions> templatesOptions,
            HttpRequest request,
            [FromHeader(Name = "If-Match")] string? ifMatch = null) =>
        {
            var result = await logic.PutRuleAsync(templateId, ruleName, rule, V2Logic.NormalizeIfMatch(ifMatch), ChangedBy(request, templatesOptions));
            return Respond(result, response, () => Results.Ok(new WriteAck(result.Version)), templateId);
        })
        .Produces<WriteAck>()
        .ProducesValidationProblem()
        .ProducesProblem((int)HttpStatusCode.NotFound)
        .ProducesProblem((int)HttpStatusCode.PreconditionFailed)
        .WithTags("Templates-V2");

        app.MapDelete("v2/templates/{templateId}/rules/{ruleName}", async (
            V2Logic logic,
            [FromRoute] string templateId,
            [FromRoute] string ruleName,
            HttpResponse response,
            IOptions<TemplatesOptions> templatesOptions,
            HttpRequest request,
            [FromHeader(Name = "If-Match")] string? ifMatch = null) =>
        {
            var result = await logic.DeleteRuleAsync(templateId, ruleName, V2Logic.NormalizeIfMatch(ifMatch), ChangedBy(request, templatesOptions));
            return Respond(result, response, Results.NoContent, templateId);
        })
        .Produces((int)HttpStatusCode.NoContent)
        .ProducesProblem((int)HttpStatusCode.NotFound)
        .ProducesProblem((int)HttpStatusCode.PreconditionFailed)
        .WithTags("Templates-V2");

        app.MapGet("v2/templates/{templateId}/versions", async (
            V2Logic logic,
            [FromRoute] string templateId,
            [FromQuery] int? limit,
            [FromQuery] long? before) =>
        {
            if (BadLimit(limit) is { } bad)
                return bad;
            return Results.Ok((await logic.ListVersionsAsync(templateId, limit ?? V2Logic.DefaultHistoryLimit, before)).ToArray());
        })
        .Produces<TemplateVersionSummary[]>()
        .ProducesValidationProblem()
        .WithTags("Templates-V2");

        app.MapGet("v2/templates/{templateId}/versions/{version}", async (
            V2Logic logic,
            [FromRoute] string templateId,
            [FromRoute] long version) =>
        {
            var template = await logic.GetVersionAsync(templateId, version);
            return template == null
                ? Results.Problem(detail: $"no version {version} of template '{templateId}'", statusCode: (int)HttpStatusCode.NotFound, title: "No such version")
                : Results.Ok(template);
        })
        .Produces<Template>()
        .ProducesProblem((int)HttpStatusCode.NotFound)
        .WithTags("Templates-V2");

        app.MapPost("v2/templates/{templateId}/rollback/{version}", async (
            V2Logic logic,
            [FromRoute] string templateId,
            [FromRoute] long version,
            HttpResponse response,
            IOptions<TemplatesOptions> templatesOptions,
            HttpRequest request) =>
        {
            var result = await logic.RollbackAsync(templateId, version, ChangedBy(request, templatesOptions));
            if (result.Outcome == WriteOutcome.Conflict)
                return Respond(result, response, Results.NoContent, templateId);
            if (result.Outcome != WriteOutcome.Ok)
                return Results.Problem(detail: $"no version {version} of template '{templateId}'", statusCode: (int)HttpStatusCode.NotFound, title: "No such version");

            var restored = await logic.GetAsync(templateId)
                ?? throw new InvalidDataException($"CRITICAL rollback for {templateId} did not produce a current template");
            response.Headers.ETag = V2Logic.DocumentETag(result.Version);
            return Results.Ok(new RollbackResult(result.Version, restored.Template));
        })
        .Produces<RollbackResult>()
        .ProducesProblem((int)HttpStatusCode.NotFound)
        .WithTags("Templates-V2");

        AddGroupMaps(app);
    }

    // Design 2.1, D5. "Version n" of a group is document version n.
    private static void AddGroupMaps(IEndpointRouteBuilder app)
    {
        app.MapGet("v2/templates/{templateId}/groups/{group}", async (
            V2Logic logic,
            [FromRoute] string templateId,
            [FromRoute] string group,
            HttpResponse response) =>
        {
            var found = await logic.GetGroupAsync(templateId, group);
            if (found == null)
                return NoSuchGroup(templateId, group);

            response.Headers.ETag = found.ETag;
            response.Headers[APIv2.VersionHeader] = found.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Ok(new GroupRules(found.Rules));
        })
        .Produces<GroupRules>()
        .ProducesProblem((int)HttpStatusCode.NotFound)
        .WithTags("Templates-V2");

        app.MapPut("v2/templates/{templateId}/groups/{group}", async (
            V2Logic logic,
            [FromRoute] string templateId,
            [FromRoute] string group,
            [FromBody] GroupWriteRequest body,
            HttpResponse response,
            IOptions<TemplatesOptions> templatesOptions,
            HttpRequest request,
            [FromHeader(Name = "If-Match")] string? ifMatch = null) =>
        {
            var result = await logic.PutGroupAsync(templateId, group, body, V2Logic.NormalizeIfMatch(ifMatch), ChangedBy(request, templatesOptions));
            return Respond(result, response, () => Results.Ok(new WriteAck(result.Version)), templateId);
        })
        .Produces<WriteAck>()
        .ProducesValidationProblem()
        .ProducesProblem((int)HttpStatusCode.PreconditionFailed)
        .ProducesProblem((int)HttpStatusCode.Conflict)
        .WithTags("Templates-V2");

        app.MapDelete("v2/templates/{templateId}/groups/{group}", async (
            V2Logic logic,
            [FromRoute] string templateId,
            [FromRoute] string group,
            HttpResponse response,
            IOptions<TemplatesOptions> templatesOptions,
            HttpRequest request,
            [FromHeader(Name = "If-Match")] string? ifMatch = null) =>
        {
            var result = await logic.DeleteGroupAsync(templateId, group, V2Logic.NormalizeIfMatch(ifMatch), ChangedBy(request, templatesOptions));
            return Respond(result, response, Results.NoContent, templateId);
        })
        .Produces((int)HttpStatusCode.NoContent)
        .ProducesProblem((int)HttpStatusCode.NotFound)
        .ProducesProblem((int)HttpStatusCode.PreconditionFailed)
        .WithTags("Templates-V2");

        app.MapGet("v2/templates/{templateId}/groups/{group}/versions", async (
            V2Logic logic,
            [FromRoute] string templateId,
            [FromRoute] string group,
            [FromQuery] int? limit,
            [FromQuery] long? before) =>
        {
            if (BadLimit(limit) is { } bad)
                return bad;
            return Results.Ok((await logic.ListGroupVersionsAsync(templateId, group, limit ?? V2Logic.DefaultHistoryLimit, before)).ToArray());
        })
        .Produces<TemplateVersionSummary[]>()
        .ProducesValidationProblem()
        .WithTags("Templates-V2");

        app.MapGet("v2/templates/{templateId}/groups/{group}/versions/{version}", async (
            V2Logic logic,
            [FromRoute] string templateId,
            [FromRoute] string group,
            [FromRoute] long version) =>
        {
            var rules = await logic.GetGroupVersionAsync(templateId, group, version);
            return rules == null
                ? Results.Problem(detail: $"group '{group}' is not present at version {version} of template '{templateId}', or that version is not retained", statusCode: (int)HttpStatusCode.NotFound, title: "No such version")
                : Results.Ok(new GroupRules(rules));
        })
        .Produces<GroupRules>()
        .ProducesProblem((int)HttpStatusCode.NotFound)
        .WithTags("Templates-V2");

        app.MapPost("v2/templates/{templateId}/groups/{group}/rollback/{version}", async (
            V2Logic logic,
            [FromRoute] string templateId,
            [FromRoute] string group,
            [FromRoute] long version,
            HttpResponse response,
            IOptions<TemplatesOptions> templatesOptions,
            HttpRequest request,
            [FromHeader(Name = "If-Match")] string? ifMatch = null) =>
        {
            var (result, rules) = await logic.RollbackGroupAsync(templateId, group, version, V2Logic.NormalizeIfMatch(ifMatch), ChangedBy(request, templatesOptions));
            return Respond(result, response, () => Results.Ok(new GroupRollbackResult(result.Version, rules)), templateId);
        })
        .Produces<GroupRollbackResult>()
        .ProducesValidationProblem()
        .ProducesProblem((int)HttpStatusCode.NotFound)
        .ProducesProblem((int)HttpStatusCode.PreconditionFailed)
        .ProducesProblem((int)HttpStatusCode.Conflict)
        .WithTags("Templates-V2");
    }

    private static IResult NoSuchGroup(string templateId, string group) =>
        Results.Problem(detail: $"no group '{group}' in template '{templateId}'", statusCode: (int)HttpStatusCode.NotFound, title: "No such group");

    // D3: both history lists take ?limit= (default 100, at most 1000), since a history that is kept
    // forever has no natural length limit.
    private static IResult? BadLimit(int? limit) =>
        limit is < 1 or > V2Logic.MaxHistoryLimit
            ? APIv2.Problem("limit", $"limit must be between 1 and {V2Logic.MaxHistoryLimit}")
            : null;

    private static IResult Respond(WriteResult result, HttpResponse response, Func<IResult> ok, string? templateId = null)
    {
        switch (result.Outcome)
        {
            case WriteOutcome.Ok:
                if (result.ETag != null)
                    response.Headers.ETag = result.ETag;
                if (result.Version > 0)
                    response.Headers[APIv2.VersionHeader] = result.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return ok();

            case WriteOutcome.Invalid:
                // A2/A7: one entry per rule that would not compile, keyed by the rule's name.
                var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var group in result.Errors!.GroupBy(e => e.Rule, StringComparer.Ordinal))
                    errors[group.Key] = [.. group.Select(e => e.Reason)];
                return Results.ValidationProblem(errors);

            case WriteOutcome.NotFound:
                return Results.Problem(
                    detail: result.Detail ?? $"no template '{templateId}'",
                    statusCode: (int)HttpStatusCode.NotFound,
                    title: "Not Found");

            case WriteOutcome.Conflict:
                return result.Detail == null
                    ? Results.Problem(detail: "the template changed while this edit was being applied; retry", statusCode: (int)HttpStatusCode.Conflict, title: "Concurrent edit")
                    : Results.Problem(detail: result.Detail, statusCode: (int)HttpStatusCode.Conflict, title: "Conflict");

            default:
                // A8: the caller's If-Match no longer describes what is stored.
                return Results.Problem(detail: "If-Match does not match the current version", statusCode: (int)HttpStatusCode.PreconditionFailed, title: "Precondition Failed");
        }
    }
}

// A13: PUT declared 204 and returned 200 with no body in v1. v2 returns 200 with the version it wrote,
// which is the one thing the caller cannot work out for itself.
public record WriteAck(long version);
