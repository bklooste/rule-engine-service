using RuleEngine.Wrapped;

// Wrapped-engine HTTP baseline: ASP.NET Core minimal API, model binding into the deep DTO (source-generated
// System.Text.Json), rules over typed properties, result model serialized back. RULES env var = rule count.
var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, WrappedJson.Default));
var app = builder.Build();

var engine = new WrappedEngine(int.Parse(Environment.GetEnvironmentVariable("RULES") ?? "10"));

app.MapGet("/health", () => "ok");
app.MapPost("/evaluate", (EventDto e) => engine.Evaluate(e));
app.MapPost("/evaluate-batch", (List<EventDto> es) => es.Select(engine.Evaluate).ToList());
app.MapPost("/evaluate-small", (SmallEventDto e) => engine.Evaluate(e));
app.MapPost("/evaluate-small-batch", (List<SmallEventDto> es) => es.Select(engine.Evaluate).ToList());

app.Run();
