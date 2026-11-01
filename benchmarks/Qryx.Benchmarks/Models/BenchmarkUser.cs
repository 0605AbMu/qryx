using Sieve.Attributes;

namespace Qryx.Benchmarks.Models;

public class BenchmarkUser
{
    [Sieve(CanFilter = true, CanSort = true)]
    public int Id { get; set; }

    [Sieve(CanFilter = true, CanSort = true)]
    public string FirstName { get; set; } = string.Empty;

    [Sieve(CanFilter = true, CanSort = true)]
    public string LastName { get; set; } = string.Empty;

    [Sieve(CanFilter = true, CanSort = true)]
    public int Age { get; set; }

    [Sieve(CanFilter = true, CanSort = true)]
    public string Status { get; set; } = "Active";

    [Sieve(CanFilter = true, CanSort = true)]
    public DateTime CreatedAt { get; set; }
}
