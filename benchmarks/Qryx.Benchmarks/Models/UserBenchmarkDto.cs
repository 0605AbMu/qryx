using Qryx.Core.Attributes;
using Qryx.Core.Contracts;
using Qryx.Core.Enums;

namespace Qryx.Benchmarks.Models;

public record UserBenchmarkDto : QryxRequest<BenchmarkUser>
{
    [Sortable]
    [Filter(nameof(BenchmarkUser.Age), Op.GreaterThanOrEqual)]
    public int? MinAge { get; init; }

    [Sortable]
    [Filter(nameof(BenchmarkUser.Age), Op.LessThanOrEqual)]
    public int? MaxAge { get; init; }

    [Sortable]
    [Filter(Op.Equals)]
    public string? Status { get; init; }

    [Sortable]
    [Filter(Op.StartsWith, TargetProperties = [nameof(BenchmarkUser.FirstName), nameof(BenchmarkUser.LastName)])]
    public string? FullName { get; init; }
}

public record SimpleUserBenchmarkDto : QryxRequest<BenchmarkUser>
{
    [Filter(Op.Equals)]
    public string? Status { get; init; }
}

public record RangeUserBenchmarkDto : QryxRequest<BenchmarkUser>
{
    [Filter(nameof(BenchmarkUser.Age), Op.GreaterThanOrEqual)]
    public int? MinAge { get; init; }

    [Filter(nameof(BenchmarkUser.Age), Op.LessThanOrEqual)]
    public int? MaxAge { get; init; }
}

public record StringUserBenchmarkDto : QryxRequest<BenchmarkUser>
{
    [Filter(nameof(BenchmarkUser.FirstName), Op.StartsWith)]
    public string? FirstName { get; init; }
}

public record UserSummaryDto(int Id, string FirstName, int Age, string Status);
