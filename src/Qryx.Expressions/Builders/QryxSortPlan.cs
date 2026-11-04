namespace Qryx.Expressions.Builders;

/// <summary>One validated sort key: canonical (declared) property path and direction.</summary>
public readonly record struct QryxSortKey(string Path, bool Descending);

/// <summary>
/// Resolved, validated ordering of a request: the whitelisted <c>sortBy</c> clauses (canonical names, duplicates
/// removed) followed by the primary key as tie-breaker. The same plan drives offset ordering and keyset cursors,
/// so a cursor taken from the last row of ANY page (offset or keyset) continues exactly where that page ended.
/// Create it with <see cref="SortBuilder.ResolveSort{TEntity}"/>.
/// </summary>
public sealed class QryxSortPlan
{
    internal QryxSortPlan(QryxSortKey[] keys, bool hasClientKeys)
    {
        KeyArray = keys;
        HasClientKeys = hasClientKeys;
        Signature = string.Join('|', keys.Select(static k => (k.Descending ? "-" : "+") + k.Path));
        Fingerprint = ComputeFingerprint(Signature);
    }

    /// <summary>Ordered sort keys (client clauses, then the primary-key tie-breaker).</summary>
    public IReadOnlyList<QryxSortKey> Keys => KeyArray;

    /// <summary>True when no key could be resolved (no valid clause and no primary key).</summary>
    public bool IsEmpty => KeyArray.Length == 0;

    /// <summary>True when at least one key comes from the client's <c>sortBy</c>.</summary>
    public bool HasClientKeys { get; }

    internal QryxSortKey[] KeyArray { get; }

    /// <summary>Stable textual form, e.g. <c>-CreatedAt|-Id</c>.</summary>
    internal string Signature { get; }

    /// <summary>Short hash of <see cref="Signature"/> stored in cursors: a cursor only continues the ordering it came from.</summary>
    internal string Fingerprint { get; }

    private static string ComputeFingerprint(string signature)
    {
        // FNV-1a: deterministic across processes (string.GetHashCode is randomized)
        uint hash = 2166136261;
        foreach (var ch in signature)
        {
            hash ^= ch;
            hash *= 16777619;
        }

        return hash.ToString("x8");
    }

    public override string ToString() => Signature;
}
