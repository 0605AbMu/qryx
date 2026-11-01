namespace Qryx.Core.Enums;

/// <summary>
/// Supported comparison and filter operators for Qryx query engine.
/// </summary>
public enum Op
{
    Equals,
    NotEquals,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    StartsWith,
    EndsWith,
    Contains,
    In,
    NotIn,
    Between,
    IsNull,
    IsNotNull,
    /// <summary>Full-text search (PostgreSQL to_tsvector / @@ or DB equivalent)</summary>
    Fts,
    /// <summary>JSON / JSONB containment or text search</summary>
    JsonContains
}
