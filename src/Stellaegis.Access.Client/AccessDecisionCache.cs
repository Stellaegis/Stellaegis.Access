using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Stellaegis.Caching;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Access.Client;

/// <summary>
/// The options of the cache of decisions, bound from <c>Stellaegis:Access:Cache</c>. A decision over HTTP costs a
/// round trip; a yes is kept for a while, a refusal never — a right granted is seen at once, a right withdrawn is
/// seen when the host invalidates, or when the entry expires.
/// <code>
/// Stellaegis:Access:Cache:Enabled = true
/// Stellaegis:Access:Cache:Expiration = 00:05:00
/// Stellaegis:Access:Cache:SizeLimit = 10000
/// </code>
/// </summary>
public sealed class AccessCacheOptions : IValidatableOptions
{
    /// <summary>The configuration section of the options, <c>Stellaegis:Access:Cache</c>.</summary>
    public const string SectionName = "Stellaegis:Access:Cache";

    /// <summary>Whether decisions are cached at all. On unless set: every decision then reaches Access.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long a yes is kept, unless invalidated before. Five minutes unless set.</summary>
    public TimeSpan Expiration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The number of decisions kept, the oldest evicted beyond. Ten thousand unless set.</summary>
    public long SizeLimit { get; set; } = 10_000;

    /// <inheritdoc/>
    public string? FindError()
    {
        if (Expiration <= TimeSpan.Zero)
            return "Expiration must be positive.";
        if (SizeLimit < 1)
            return "SizeLimit must be at least 1.";
        return null;
    }
}

/// <summary>
/// The cache of the decisions a resource server apart asked over HTTP: only the yes are kept, each under the
/// segments of its space and of its subjects, so that what Access announces as changed is dropped at once. The host
/// connects its subscriptions to the events of Access — <see cref="GrantsChangedEvent"/>,
/// <see cref="MembershipsChangedEvent"/>, <see cref="CompartmentRemovedEvent"/>, <see cref="SubjectRemovedEvent"/> —
/// on <see cref="Invalidate(IEvent)"/>; a module subscribes to nothing by itself.
/// </summary>
public sealed class AccessDecisionCache : IDisposable
{
    private readonly BoundedCache? _cache;
    private readonly CacheSegmentInvalidator _segments = new();

    /// <summary>The cache, sized by the options; disabled when they say so.</summary>
    public AccessDecisionCache(IOptions<AccessCacheOptions> options)
    {
        var value = Guard.NotNull(options).Value;
        _cache = value.Enabled
            ? new BoundedCache("access.decisions", new BoundedCacheOptions { SizeLimit = value.SizeLimit, Expiration = value.Expiration })
            : null;
    }

    /// <summary>Whether decisions are cached.</summary>
    public bool Enabled => _cache is not null;

    /// <summary>Whether a yes is held for the question.</summary>
    public bool TryGetYes(AccessSubject subject, AccessSubject? via, Space space, Operation operation)
        => _cache is not null && _cache.TryGet<bool>(Key(subject, via, space, operation), out var yes) && yes;

    /// <summary>Keeps a yes for the question, under the segments of its space and of its subjects.</summary>
    public void SetYes(AccessSubject subject, AccessSubject? via, Space space, Operation operation)
    {
        if (_cache is null)
            return;
        var tokens = new List<CancellationChangeToken>
        {
            _segments.GetToken(SpaceSegment(space)),
            _segments.GetToken(SubjectSegment(subject.Id)),
        };
        if (via is not null)
            tokens.Add(_segments.GetToken(SubjectSegment(via.Id)));
        _cache.Set(Key(subject, via, space, operation), true, tokens);
    }

    /// <summary>Drops every decision taken in the space — a compartment terminated, the rights of a space replaced.</summary>
    public void Invalidate(Space space) => _segments.Invalidate(SpaceSegment(Guard.NotNull(space)));

    /// <summary>Drops every decision taken for the subject, as actor or as the client acting on behalf of one.</summary>
    public void Invalidate(AccessSubject subject) => _segments.Invalidate(SubjectSegment(Guard.NotNull(subject).Id));

    /// <summary>
    /// Drops what an event of Access announces as changed: the subject of a <see cref="GrantsChangedEvent"/>, of a
    /// <see cref="MembershipsChangedEvent"/>, of a <see cref="SubjectRemovedEvent"/>; the compartment of a
    /// <see cref="CompartmentRemovedEvent"/>. Any other event is ignored. The host calls it from its subscriptions.
    /// </summary>
    public void Invalidate(IEvent @event)
    {
        switch (Guard.NotNull(@event))
        {
            case GrantsChangedEvent grants:
                Invalidate(grants.Subject);
                break;
            case MembershipsChangedEvent memberships:
                _segments.Invalidate(SubjectSegment(memberships.Subject));
                break;
            case CompartmentRemovedEvent compartment:
                Invalidate(Space.Of(compartment.Compartment));
                break;
            case SubjectRemovedEvent subject:
                Invalidate(subject.Subject);
                break;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _cache?.Dispose();

    private static string Key(AccessSubject subject, AccessSubject? via, Space space, Operation operation)
        => $"{subject.Kind}:{subject.Id}:{via?.Id ?? "-"}:{space.ToSegment()}:{operation.Identity}";

    private static string SpaceSegment(Space space) => $"space:{space.ToSegment()}";

    private static string SubjectSegment(string subject) => $"subject:{subject}";
}

/// <summary>The decision over HTTP behind the cache: a yes held is answered without a round trip.</summary>
internal sealed class CachedAccessDecision(IAccessDecision inner, AccessDecisionCache cache) : IAccessDecision
{
    public ValueTask<Result> DecideAsync(Actor actor, Space space, Operation operation, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(actor);
        return DecideAsync(AccessSubject.Of(actor), actor.Via is { } via ? AccessSubject.Of(via) : null, space, operation, cancellationToken);
    }

    public async ValueTask<Result> DecideAsync(AccessSubject subject, AccessSubject? via, Space space, Operation operation, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(subject);
        Guard.NotNull(space);
        Guard.NotNull(operation);
        if (cache.TryGetYes(subject, via, space, operation))
            return Result.Success();
        var result = await inner.DecideAsync(subject, via, space, operation, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
            cache.SetYes(subject, via, space, operation);
        return result;
    }
}
