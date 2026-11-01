using Qryx.Core.Attributes;
using Sieve.Attributes;

namespace Qryx.Benchmarks.Models;

public class BenchmarkUser
{
    [Sortable]
    [Sieve(CanFilter = true, CanSort = true)]
    public int Id { get; set; }

    [Sortable]
    [Sieve(CanFilter = true, CanSort = true)]
    public string FirstName { get; set; } = string.Empty;

    [Sortable]
    [Sieve(CanFilter = true, CanSort = true)]
    public string LastName { get; set; } = string.Empty;

    [Sortable]
    [Sieve(CanFilter = true, CanSort = true)]
    public int Age { get; set; }

    [Sortable]
    [Sieve(CanFilter = true, CanSort = true)]
    public string Status { get; set; } = "Active";

    [Sortable]
    [Sieve(CanFilter = true, CanSort = true)]
    public DateTime CreatedAt { get; set; }
}
