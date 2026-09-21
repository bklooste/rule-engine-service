using System.Text.Json;

using Templates.Compiled;

namespace Templates.Api;

// P-07 (D-3 step 2), moved onto v2 by P-20: writes merged evaluate results straight to the response body
// through a Utf8JsonWriter over HttpResponse.BodyWriter. The default minimal-API JSON path would first
// need a JsonObject tree to serialize; the plan writes the shared fragment nodes out in place, so the
// tree is never built. The JSON produced is byte-identical to serializing the equivalent JsonObject.
internal sealed class EvaluatePlanResult(MergePlan[] plans, bool single) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.ContentType = "application/json; charset=utf-8";

        var body = httpContext.Response.BodyWriter;
        await using (var writer = new Utf8JsonWriter(body))
        {
            // A13: a single-event evaluate answers with an object, never an array.
            if (single)
            {
                plans[0].WriteTo(writer);
            }
            else
            {
                writer.WriteStartArray();
                foreach (var plan in plans)
                    plan.WriteTo(writer);
                writer.WriteEndArray();
            }
            writer.Flush();
        }

        await body.FlushAsync(httpContext.RequestAborted);
    }
}

// The same streaming write for the multi-template evaluate (A15). Shape matches MultiEvaluateResponse:
// { "versions": { id: version }, "results": [ { id: <merged object> } ] }.
internal sealed class MultiEvaluatePlanResult(Dictionary<string, long> versions, string[] templateIds, MergePlan[][] results) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.ContentType = "application/json; charset=utf-8";

        var body = httpContext.Response.BodyWriter;
        await using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();

            writer.WritePropertyName("versions");
            writer.WriteStartObject();
            foreach (var (id, version) in versions)
                writer.WriteNumber(id, version);
            writer.WriteEndObject();

            writer.WritePropertyName("results");
            writer.WriteStartArray();
            foreach (var perEvent in results)
            {
                writer.WriteStartObject();
                for (var i = 0; i < templateIds.Length; i++)
                {
                    writer.WritePropertyName(templateIds[i]);
                    perEvent[i].WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
            writer.Flush();
        }

        await body.FlushAsync(httpContext.RequestAborted);
    }
}
