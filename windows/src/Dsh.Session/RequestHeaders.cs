using System.Text.Json;
using Dsh.Llm;

namespace Dsh.Session;

/// <summary>
/// Canonicalizes and compares request headers.
/// </summary>
/// <remarks>
/// Two headers that would produce the same request must compare equal, or the log
/// fills with header events that record no actual change. Canonical form is what
/// makes that comparison meaningful: an empty system prompt and an absent one are
/// the same request, so they are stored the same way.
/// </remarks>
public static class RequestHeaders
{
    private static readonly JsonSerializerOptions ParameterComparison = new() { WriteIndented = false };

    /// <summary>
    /// Put a header in canonical form.
    /// </summary>
    /// <param name="header">The header to canonicalize.</param>
    /// <returns>The header with empty optional fields dropped.</returns>
    public static EpochHeader Canonical(EpochHeader header)
        => header with
        {
            AdapterDefaults = header.AdapterDefaults is { IsEmpty: false } ? header.AdapterDefaults : null,
            System = string.IsNullOrEmpty(header.System) ? null : header.System,
            Tools = header.Tools is { Count: > 0 } ? header.Tools : null,
        };

    /// <summary>
    /// Whether two headers describe the same request.
    /// </summary>
    /// <param name="left">One header.</param>
    /// <param name="right">The other.</param>
    /// <returns>True when the configuration, prompt, and tools all match.</returns>
    public static bool Equal(EpochHeader? left, EpochHeader? right)
    {
        if (left is null || right is null) return ReferenceEquals(left, right);
        if (!left.Config.Matches(right.Config)) return false;
        if (!Equals(left.AdapterDefaults, right.AdapterDefaults)) return false;
        if (!string.Equals(left.System, right.System, StringComparison.Ordinal)) return false;
        return ToolsEqual(left.Tools, right.Tools);
    }

    private static bool ToolsEqual(IReadOnlyList<ToolSchema>? left, IReadOnlyList<ToolSchema>? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].Name, right[index].Name, StringComparison.Ordinal)) return false;
            if (!string.Equals(left[index].Description, right[index].Description, StringComparison.Ordinal)) return false;
            if (!string.Equals(Serialize(left[index].Parameters), Serialize(right[index].Parameters), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string Serialize(IReadOnlyDictionary<string, object?> parameters)
        => JsonSerializer.Serialize(parameters, ParameterComparison);

    /// <summary>
    /// The latest header recorded in a log.
    /// </summary>
    /// <param name="events">The log to fold.</param>
    /// <returns>The last recorded header, or null when none was recorded.</returns>
    public static EpochHeader? Fold(IReadOnlyList<SessionEvent> events)
    {
        EpochHeader? folded = null;
        foreach (var entry in events)
        {
            if (string.Equals(entry.Type, SessionEvents.RequestHeader.Name, StringComparison.Ordinal))
            {
                folded = entry.DataAs<RequestHeaderData>().Header;
            }
        }

        return folded;
    }
}
