using System.Diagnostics.CodeAnalysis;
using Stellaegis.Primitives;

namespace Stellaegis.Access;

/// <summary>
/// A space on the wire: the segment <see cref="Space.ToSegment"/> projects it to — the compartment's value, or
/// <see cref="Space.SharedSegment"/> for the shared space — read back. The decision routes of
/// <c>Stellaegis.Access.AspNetCore</c> and the client of <c>Stellaegis.Access.Client</c> speak this one form, so
/// that a client of a compartment and the shared space never get confused: <c>shared</c> is a valid compartment
/// name, only <c>_shared</c> is the shared space.
/// </summary>
public static class SpaceSegments
{
    /// <summary>
    /// Reads a space from its segment: <see cref="Space.SharedSegment"/> is the shared space, a valid compartment
    /// name is that compartment, anything else fails.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? segment, [MaybeNullWhen(false)] out Space space)
    {
        if (string.Equals(segment, Space.SharedSegment, StringComparison.Ordinal))
        {
            space = Space.Shared;
            return true;
        }
        if (Compartment.TryParse(segment, out var compartment))
        {
            space = Space.Of(compartment);
            return true;
        }
        space = null;
        return false;
    }
}
