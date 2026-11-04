namespace Qryx.Core.Attributes;

/// <summary>
/// Disables the total count (<c>includeTotal=true</c>) for a query DTO. Use it for very large tables, where
/// <c>COUNT(*)</c> on every request would be too expensive: the parameter is then ignored and the response never
/// contains <c>total</c>. Paging with <c>hasNext</c> / <c>nextCursor</c> keeps working.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class NoTotalAttribute : Attribute
{
}
