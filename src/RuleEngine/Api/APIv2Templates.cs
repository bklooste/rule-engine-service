using System.Net;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using Templates.Data;

namespace Templates.Api;

// P-13's rollback response, moved here with the history routes it belongs to (A10).
public record RollbackResult([property: JsonPropertyName("version")] long Version, [property: JsonPropertyName("template")] Template Template);

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
//   GET    v2/templates/{id}/versions            (A10)
//   GET    v2/templates/{id}/versions/{n}
//   POST   v2/templates/{id}/rollback/{n}
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
            [FromRoute] string templateId) => Results.Ok((await logic.ListVersionsAsync(templateId)).ToArray()))
        .Produces<TemplateVersionSummary[]>()
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
            HttpResponse response) =>
        {
            var result = await logic.RollbackAsync(templateId, version);
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
    }

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
                return Results.Problem(detail: "the template changed while this edit was being applied; retry", statusCode: (int)HttpStatusCode.Conflict, title: "Concurrent edit");

            default:
                // A8: the caller's If-Match no longer describes what is stored.
                return Results.Problem(detail: "If-Match does not match the current version", statusCode: (int)HttpStatusCode.PreconditionFailed, title: "Precondition Failed");
        }
    }
}

// A13: PUT declared 204 and returned 200 with no body in v1. v2 returns 200 with the version it wrote,
// which is the one thing the caller cannot work out for itself.
public record WriteAck(long version);
