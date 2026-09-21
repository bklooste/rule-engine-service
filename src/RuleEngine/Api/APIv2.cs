using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Mvc;

namespace Templates.Api;

// D-12/A15: one call evaluates several templates over the same events, replacing offer-products' two
// HTTP hops. Versions come back in the body rather than a header because there is one per template id.
public record EvaluateRequest(string[] templateIds, JsonElement[] events);

public record MultiEvaluateResponse(Dictionary<string, long> versions, Dictionary<string, JsonObject>[] results);

// P-14: the v2 evaluate and explain routes. P-20 removed v1 outright, so these are the only match
// endpoints the service has.
//
// What changed from v1, by finding:
//   A1  unknown template is 404, not 400 "No such template"
//   A2  ProblemDetails with the real field and message, not the message in a dictionary key
//   A3  no useCache - the version-key check (P-04) makes bypassing the cache pointless
//   A4  the event limit is configuration (TemplateApi:MaxEventsPerRequest), and the error names it
//   A5  X-Template-Version on every single-template response; a versions map on the multi-template one
//   A6  no request body in any log line
//   A13 a single-event evaluate returns {} when nothing matched, never []
//   A14/D-11 loose absent-field semantics by default (matches v1), per-condition required opts into strict
//   D-6 ago(Nh) is N hours in the past
public static class APIv2
{
    internal const string VersionHeader = "X-Template-Version";

    public static void AddV2ApiMaps(this IEndpointRouteBuilder app)
    {
        app.MapPost("v2/templates/evaluate", async (
            V2Logic logic,
            [FromBody] EvaluateRequest request,
            CancellationToken ct) =>
        {
            if (request?.templateIds == null || request.templateIds.Length == 0)
                return Problem("templateIds", "at least one template id is required");
            if (request.templateIds.Length > logic.Options.MaxTemplateIdsPerRequest)
                return Problem("templateIds", $"at most {logic.Options.MaxTemplateIdsPerRequest} template ids per request");
            if (request.events == null || request.events.Length == 0)
                return Problem("events", "at least one event is required");
            if (request.events.Length > logic.Options.MaxEventsPerRequest)
                return Problem("events", $"at most {logic.Options.MaxEventsPerRequest} events per request");

            var versions = new Dictionary<string, long>(request.templateIds.Length, StringComparer.Ordinal);
            var compiled = new List<(string Id, Compiled.CompiledV2Template Template)>(request.templateIds.Length);
            foreach (var templateId in request.templateIds.Distinct(StringComparer.Ordinal))
            {
                var loaded = await logic.LoadAsync(templateId, ct);
                if (loaded == null)
                    return NoSuchTemplate(templateId);

                versions[templateId] = loaded.Value.Version;
                compiled.Add((templateId, loaded.Value.Compiled));
            }

            var now = DateTime.UtcNow;
            var results = new Compiled.MergePlan[request.events.Length][];
            for (var i = 0; i < request.events.Length; i++)
            {
                var perTemplate = new Compiled.MergePlan[compiled.Count];
                for (var t = 0; t < compiled.Count; t++)
                    perTemplate[t] = V2Logic.EvaluateToPlan(compiled[t].Template, request.events[i], now);
                results[i] = perTemplate;
            }

            return (IResult)new MultiEvaluatePlanResult(versions, [.. compiled.Select(c => c.Id)], results);
        })
        .Produces<MultiEvaluateResponse>()
        .ProducesProblem((int)HttpStatusCode.BadRequest)
        .ProducesProblem((int)HttpStatusCode.NotFound)
        .WithTags("Templates-V2");

        app.MapPost("v2/templates/{templateId}/evaluate", async (
            V2Logic logic,
            [FromRoute] string templateId,
            [FromBody] JsonElement data,
            HttpResponse response,
            CancellationToken ct) =>
        {
            var (events, error) = ReadEvents(data, logic.Options.MaxEventsPerRequest);
            if (error != null)
                return error;

            var loaded = await logic.LoadAsync(templateId, ct);
            if (loaded == null)
                return NoSuchTemplate(templateId);

            response.Headers[VersionHeader] = loaded.Value.Version.ToString(CultureInfo.InvariantCulture);

            var now = DateTime.UtcNow;
            var results = new Compiled.MergePlan[events!.Length];
            for (var i = 0; i < events.Length; i++)
                results[i] = V2Logic.EvaluateToPlan(loaded.Value.Compiled, events[i], now);

            // A13: an object endpoint answers with an object. v1's /one returned [] for no match.
            return (IResult)new EvaluatePlanResult(results, data.ValueKind != JsonValueKind.Array);
        })
        .Produces<JsonObject[]>()
        .ProducesProblem((int)HttpStatusCode.BadRequest)
        .ProducesProblem((int)HttpStatusCode.NotFound)
        .WithTags("Templates-V2");

        app.MapPost("v2/templates/{templateId}/explain", async (
            V2Logic logic,
            [FromRoute] string templateId,
            [FromBody] JsonElement data,
            CancellationToken ct) =>
        {
            var (events, error) = ReadEvents(data, logic.Options.MaxEventsPerRequest);
            if (error != null)
                return error;
            if (events!.Length != 1)
                return Problem("events", "explain takes exactly one event");

            var loaded = await logic.LoadAsync(templateId, ct);
            if (loaded == null)
                return NoSuchTemplate(templateId);

            return Results.Ok(V2Logic.Explain(templateId, loaded.Value.Version, loaded.Value.Compiled, events[0]));
        })
        .Produces<ExplainResponse>()
        .ProducesProblem((int)HttpStatusCode.BadRequest)
        .ProducesProblem((int)HttpStatusCode.NotFound)
        .WithTags("Templates-V2");
    }

    private static (JsonElement[]? Events, IResult? Error) ReadEvents(JsonElement data, int maxEvents)
    {
        if (data.ValueKind == JsonValueKind.Object)
            return ([data], null);
        if (data.ValueKind != JsonValueKind.Array)
            return (null, Problem("events", "the body must be an event object or an array of event objects"));

        var events = data.EnumerateArray().ToArray();
        if (events.Length == 0)
            return (null, Problem("events", "at least one event is required"));
        if (events.Length > maxEvents)
            return (null, Problem("events", $"at most {maxEvents} events per request"));

        return (events, null);
    }

    // A2: the field is the field and the message is the message.
    internal static IResult Problem(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { { field, [message] } });

    // A1: an unknown template id is not a malformed request.
    internal static IResult NoSuchTemplate(string templateId) =>
        Results.Problem(detail: $"no template '{templateId}'", statusCode: (int)HttpStatusCode.NotFound, title: "No such template");
}
