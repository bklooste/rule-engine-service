using System.Runtime.CompilerServices;

using Microsoft.Extensions.Diagnostics.HealthChecks;

using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

using StackExchange.Redis;

using Templates;
using Templates.Api;
using Templates.Data;

[assembly: InternalsVisibleTo("RuleEngine.Tests")]
[assembly: InternalsVisibleTo("RuleEngine.Benchmarks")]

// Container HEALTHCHECK: the chiselled runtime image has no shell or curl, so the app probes itself.
if (args.Contains("--healthcheck"))
{
    var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';')[0] ?? "8080";
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try { return (await http.GetAsync($"http://localhost:{port}/health")).IsSuccessStatusCode ? 0 : 1; }
    catch { return 1; }
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.Configuration.AddSecretFiles();
var config = builder.Configuration;
var services = builder.Services;

services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));

services.AddOptions<TemplatesOptions>().BindConfiguration(TemplatesOptions.SectionName).ValidateOnStart();
services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<TemplatesOptions>, TemplatesOptionsValidator>();
services.AddOpenApi("v1", options => options.AddDocumentTransformer<BasePathTransformer>());

var redis = Repository.CreateClient(config);
services.AddSingleton(redis);
services.AddSingleton<Repository>();

// Memory-only HybridCache in front of the template version check (DynamicRulesEngine): lets a PUT
// propagate to every pod's cache in seconds. No IDistributedCache is registered; L2 is disabled per entry.
services.Configure<TemplateVersionCacheOptions>(config.GetSection(TemplateVersionCacheOptions.SectionName));
services.AddHybridCache();
services.AddSingleton<DynamicRulesEngine>();
services.AddOptions<TemplateHistoryOptions>().BindConfiguration(TemplateHistoryOptions.SectionName).ValidateOnStart();
services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<TemplateHistoryOptions>, TemplateHistoryOptionsValidator>();
services.AddSingleton(TimeProvider.System);
services.Configure<TemplateApiOptions>(config.GetSection(TemplateApiOptions.SectionName));
services.AddSingleton<V2Logic>();
services.AddHttpContextAccessor();

services.AddHealthChecks()
    .AddCheck("redis", () => redis.IsConnected ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Redis disconnected"));

// Telemetry: exporters are controlled entirely by the standard OTEL_* env vars. With none set, nothing is exported.
services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(builder.Environment.ApplicationName))
    .WithTracing(t => t.AddAspNetCoreInstrumentation()
        .AddRedisInstrumentation(redis, o => o.EnrichActivityWithTimingEvents = false)
        .AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddOtlpExporter());

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapOpenApi("/openapi/{documentName}.json");
app.AddV2ApiMaps();
app.AddV2TemplateApiMaps();

app.Run();
return 0;

public partial class Program;
