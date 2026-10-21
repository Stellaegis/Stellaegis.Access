using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Stellaegis.Access.InMemory;
using Stellaegis.Primitives;

namespace Stellaegis.Access.Tests;

/// <summary>Keeps every event published, for the tests to read.</summary>
internal sealed class CapturingEventPublisher : IEventPublisher
{
    public ConcurrentQueue<IEvent> Events { get; } = new();

    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default) where TEvent : IEvent
    {
        Events.Enqueue(@event);
        return Task.CompletedTask;
    }
}

/// <summary>The module on its in-memory stores, with a capturing publisher: what every test decides against.</summary>
internal sealed class TestHost : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;

    public TestHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEventPublisher>(Events);
        services.AddStellaegisAccessInMemory();
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        _scope = _provider.CreateScope();
    }

    public CapturingEventPublisher Events { get; } = new();

    public IServiceProvider Services => _scope.ServiceProvider;

    public IAccessDecision Decision => Services.GetRequiredService<IAccessDecision>();

    public IAccessProjection Projection => Services.GetRequiredService<IAccessProjection>();

    public InMemoryAccessData Data => Services.GetRequiredService<InMemoryAccessData>();

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
    }
}

/// <summary>The fixtures every test shares: two compartments, a person, two clients, two operations.</summary>
internal static class Fixtures
{
    public static readonly Compartment Acme = Compartment.Parse("acme");
    public static readonly Compartment Globex = Compartment.Parse("globex");
    public static readonly Guid JaneId = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");
    public static readonly string Jane = JaneId.ToString("N");
    public static readonly Actor JaneActor = Actor.Parse(Jane);
    public const string Desktop = "c_acme_desktop";
    public const string Inventory = "inventory";
    public static readonly Operation ItemsRead = new("items", "read", OperationKind.Read);
    public static readonly Operation ItemsWrite = new("items", "write", OperationKind.Write);
    public static readonly Operation UsersRead = new("users", "read", OperationKind.Read);
}
