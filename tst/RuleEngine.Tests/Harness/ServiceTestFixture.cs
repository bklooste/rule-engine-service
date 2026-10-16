using Microsoft.AspNetCore.Mvc.Testing;

using Testcontainers.Redis;

using Xunit;

namespace Scenarify;

/// <summary>What a scenario needs to know about the service under test.</summary>
public sealed record ServiceTestOptions
{
    /// <summary>Scopes sent when a step gives none. Unused by this service (no auth) but part of the step contract.</summary>
    public string? DefaultScopes { get; init; }

    /// <summary>Brand for the <c>{{brand}}</c> token.</summary>
    public string Brand { get; init; } = "testBrand";
}

/// <summary>
/// Hosts the real service in-process (<see cref="WebApplicationFactory{TEntryPoint}"/>) against a real Redis —
/// a Testcontainers one, or the instance named by <c>RULE_ENGINE_TEST_REDIS</c> — and gives scenarios an HTTP client.
/// </summary>
public abstract class ServiceTestFixture(ServiceTestOptions options) : IAsyncLifetime
{
    private RedisContainer? redis;
    private WebApplicationFactory<Program>? factory;

    public ServiceTestOptions Options { get; } = options;

    public TestClients Clients { get; private set; } = null!;

    /// <summary>The in-process service's container, for tests that reach below HTTP (repository, clock, keys).</summary>
    public IServiceProvider Services => factory?.Services ?? throw new InvalidOperationException("the fixture is not initialized");

    public async ValueTask InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("RULE_ENGINE_TEST_REDIS");
        if (string.IsNullOrEmpty(connection))
        {
            redis = new RedisBuilder().WithImage("redis:7-alpine").Build();
            await redis.StartAsync();
            connection = redis.GetConnectionString();
        }

        // Env vars, not ConfigureAppConfiguration: Program reads the connection string while building, before
        // the factory's deferred configuration callbacks run.
        Environment.SetEnvironmentVariable("ConnectionStrings__Redis", connection);
        Environment.SetEnvironmentVariable("ENVIRONMENT", $"test-{Guid.NewGuid():N}");
        factory = new WebApplicationFactory<Program>();
        Clients = new TestClients(factory);
    }

    public HttpClient Client(string? scopes = null, string? userId = null, string? brand = null) => Clients.Client(scopes, userId, brand);

    public TestClients ClientsFor(string? service) =>
        service is null ? Clients : throw new NotSupportedException("this suite tests a single service");

    public ScenarioVariables NewVariables() => new(Options.Brand, Guid.NewGuid().ToString());

    public void Log(string message) => Console.WriteLine($"[{GetType().Name}] {message}");

    public async ValueTask DisposeAsync()
    {
        if (factory is not null) await factory.DisposeAsync();
        if (redis is not null) await redis.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Clients for the in-process service; the caller identity goes in the <c>userid</c> header.</summary>
public sealed class TestClients(WebApplicationFactory<Program> factory)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, HttpClient> clients = new();

    public HttpClient Client(string? scopes = null, string? userId = null, string? brand = null) =>
        clients.GetOrAdd(userId ?? "", id =>
        {
            var client = factory.CreateClient();
            if (id.Length > 0) client.DefaultRequestHeaders.TryAddWithoutValidation("userid", id);
            return client;
        });
}
