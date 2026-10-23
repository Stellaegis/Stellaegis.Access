using System.Collections.Concurrent;
using Stellaegis.Primitives;

namespace Stellaegis.Access.InMemory;

/// <summary>The rights, in memory: for development and tests, lost with the process. One set per domain.</summary>
public sealed class InMemoryAccessData
{
    /// <summary>The memberships: subject → compartments.</summary>
    internal ConcurrentDictionary<string, HashSet<Compartment>> Memberships { get; } = new(StringComparer.Ordinal);

    /// <summary>The user grants: (subject, compartment) → operation identity → whether it requires a second factor.</summary>
    internal ConcurrentDictionary<(string Subject, Compartment Compartment), Dictionary<string, bool>> UserGrants { get; } = new();

    /// <summary>The admin grants: subject → operation identity → whether it requires a second factor.</summary>
    internal ConcurrentDictionary<string, Dictionary<string, bool>> AdminGrants { get; } = new(StringComparer.Ordinal);

    /// <summary>The client grants: (client, space) → operation identities.</summary>
    internal ConcurrentDictionary<(string Client, Space Space), HashSet<string>> ClientGrants { get; } = new();

    /// <summary>Removes every right and membership.</summary>
    public void Clear()
    {
        Memberships.Clear();
        UserGrants.Clear();
        AdminGrants.Clear();
        ClientGrants.Clear();
    }

    internal static HashSet<string> Set(IReadOnlyCollection<string> values) => new(values, StringComparer.Ordinal);

    internal static Dictionary<string, bool> Map(IReadOnlyCollection<AccessGrant> grants)
        => AccessGrant.Merge(grants).ToDictionary(grant => grant.Operation, grant => grant.RequiresSecondFactor, StringComparer.Ordinal);
}

internal sealed class InMemoryMembershipStore(InMemoryAccessData data) : IMembershipStore
{
    public Task<bool> IsMemberAsync(string subject, Compartment compartment, CancellationToken cancellationToken = default)
        => Task.FromResult(data.Memberships.TryGetValue(subject, out var set) && Contains(set, compartment));

    public Task<IReadOnlyList<Compartment>> ListAsync(string subject, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Compartment>>(data.Memberships.TryGetValue(subject, out var set) ? Snapshot(set) : []);

    public Task ReplaceAsync(string subject, IReadOnlyCollection<Compartment> compartments, CancellationToken cancellationToken = default)
    {
        if (compartments.Count == 0)
            data.Memberships.TryRemove(subject, out _);
        else
            data.Memberships[subject] = [.. compartments];
        return Task.CompletedTask;
    }

    public Task RemoveCompartmentAsync(Compartment compartment, CancellationToken cancellationToken = default)
    {
        foreach (var (subject, set) in data.Memberships)
        {
            lock (set)
                set.Remove(compartment);
            if (set.Count == 0)
                data.Memberships.TryRemove(subject, out _);
        }
        return Task.CompletedTask;
    }

    public Task RemoveSubjectAsync(string subject, CancellationToken cancellationToken = default)
    {
        data.Memberships.TryRemove(subject, out _);
        return Task.CompletedTask;
    }

    private static bool Contains(HashSet<Compartment> set, Compartment compartment)
    {
        lock (set)
            return set.Contains(compartment);
    }

    private static List<Compartment> Snapshot(HashSet<Compartment> set)
    {
        lock (set)
            return set.OrderBy(c => c.Value, StringComparer.Ordinal).ToList();
    }
}

internal sealed class InMemoryUserGrantStore(InMemoryAccessData data) : IUserGrantStore
{
    public Task<AccessGrant?> FindAsync(string subject, Compartment compartment, string operation, CancellationToken cancellationToken = default)
        => Task.FromResult(data.UserGrants.TryGetValue((subject, compartment), out var map) ? Grants.Find(map, operation) : null);

    public Task<IReadOnlyList<AccessGrant>> ListAsync(string subject, Compartment compartment, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AccessGrant>>(data.UserGrants.TryGetValue((subject, compartment), out var map) ? Grants.Snapshot(map) : []);

    public Task ReplaceAsync(string subject, Compartment compartment, IReadOnlyCollection<AccessGrant> grants, CancellationToken cancellationToken = default)
    {
        if (grants.Count == 0)
            data.UserGrants.TryRemove((subject, compartment), out _);
        else
            data.UserGrants[(subject, compartment)] = InMemoryAccessData.Map(grants);
        return Task.CompletedTask;
    }

    public Task RemoveCompartmentAsync(Compartment compartment, CancellationToken cancellationToken = default)
    {
        foreach (var key in data.UserGrants.Keys.Where(k => k.Compartment == compartment).ToList())
            data.UserGrants.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task RemoveSubjectAsync(string subject, CancellationToken cancellationToken = default)
    {
        foreach (var key in data.UserGrants.Keys.Where(k => string.Equals(k.Subject, subject, StringComparison.Ordinal)).ToList())
            data.UserGrants.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryAdminGrantStore(InMemoryAccessData data) : IAdminGrantStore
{
    public Task<AccessGrant?> FindAsync(string subject, string operation, CancellationToken cancellationToken = default)
        => Task.FromResult(data.AdminGrants.TryGetValue(subject, out var map) ? Grants.Find(map, operation) : null);

    public Task<bool> HasAnyAsync(string subject, CancellationToken cancellationToken = default)
        => Task.FromResult(data.AdminGrants.TryGetValue(subject, out var map) && Grants.Any(map));

    public Task<IReadOnlyList<AccessGrant>> ListAsync(string subject, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AccessGrant>>(data.AdminGrants.TryGetValue(subject, out var map) ? Grants.Snapshot(map) : []);

    public Task ReplaceAsync(string subject, IReadOnlyCollection<AccessGrant> grants, CancellationToken cancellationToken = default)
    {
        if (grants.Count == 0)
            data.AdminGrants.TryRemove(subject, out _);
        else
            data.AdminGrants[subject] = InMemoryAccessData.Map(grants);
        return Task.CompletedTask;
    }

    public Task RemoveSubjectAsync(string subject, CancellationToken cancellationToken = default)
    {
        data.AdminGrants.TryRemove(subject, out _);
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryClientGrantStore(InMemoryAccessData data) : IClientGrantStore
{
    public Task<bool> HoldsAsync(string clientId, Space space, string operation, CancellationToken cancellationToken = default)
        => Task.FromResult(data.ClientGrants.TryGetValue((clientId, space), out var set) && Sets.Contains(set, operation));

    public Task<bool> HasAnyAsync(string clientId, Space space, CancellationToken cancellationToken = default)
        => Task.FromResult(data.ClientGrants.TryGetValue((clientId, space), out var set) && Sets.Any(set));

    public Task<IReadOnlyList<string>> ListAsync(string clientId, Space space, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(data.ClientGrants.TryGetValue((clientId, space), out var set) ? Sets.Snapshot(set) : []);

    public Task ReplaceAsync(string clientId, Space space, IReadOnlyCollection<string> operations, CancellationToken cancellationToken = default)
    {
        if (operations.Count == 0)
            data.ClientGrants.TryRemove((clientId, space), out _);
        else
            data.ClientGrants[(clientId, space)] = InMemoryAccessData.Set(operations);
        return Task.CompletedTask;
    }

    public Task RemoveCompartmentAsync(Compartment compartment, CancellationToken cancellationToken = default)
    {
        foreach (var key in data.ClientGrants.Keys.Where(k => k.Space.Compartment == compartment).ToList())
            data.ClientGrants.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task RemoveSubjectAsync(string clientId, CancellationToken cancellationToken = default)
    {
        foreach (var key in data.ClientGrants.Keys.Where(k => string.Equals(k.Client, clientId, StringComparison.Ordinal)).ToList())
            data.ClientGrants.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

/// <summary>Reads of a set of operation identities under its lock, the set being replaced whole by a projection.</summary>
internal static class Sets
{
    public static bool Contains(HashSet<string> set, string operation)
    {
        lock (set)
            return set.Contains(operation);
    }

    public static bool Any(HashSet<string> set)
    {
        lock (set)
            return set.Count > 0;
    }

    public static List<string> Snapshot(HashSet<string> set)
    {
        lock (set)
            return set.Order(StringComparer.Ordinal).ToList();
    }
}

/// <summary>Reads of the grants of a person under their lock, the map being replaced whole by a projection.</summary>
internal static class Grants
{
    public static AccessGrant? Find(Dictionary<string, bool> map, string operation)
    {
        lock (map)
            return map.TryGetValue(operation, out var requires) ? new AccessGrant(operation, requires) : null;
    }

    public static bool Any(Dictionary<string, bool> map)
    {
        lock (map)
            return map.Count > 0;
    }

    public static List<AccessGrant> Snapshot(Dictionary<string, bool> map)
    {
        lock (map)
            return map.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new AccessGrant(pair.Key, pair.Value)).ToList();
    }
}
