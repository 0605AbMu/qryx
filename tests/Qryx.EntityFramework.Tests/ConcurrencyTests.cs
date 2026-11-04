using System.Collections.Concurrent;
using System.Reflection;
using Qryx.Core.Attributes;
using Qryx.Expressions.Builders;

namespace Qryx.EntityFramework.Tests;

/// <summary>A fresh closed type per type argument: every closure is a never-seen entity for the keyset caches.</summary>
public sealed class RaceEntity<T>
{
    public int Id { get; set; }

    [Sortable]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Regression: keyset plans used to share one (not thread-safe) NullabilityInfoContext, so concurrent first requests
/// for a new entity/sort intermittently threw ArgumentException ("An item with the same key has already been added").
/// </summary>
public class ConcurrencyTests
{
    private static readonly MethodInfo FirstUseMethod =
        typeof(ConcurrencyTests).GetMethod(nameof(FirstUse), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static bool FirstUse<TEntity>() => PaginationBuilder.SupportsKeyset<TEntity>(SortBuilder.ResolveSort<TEntity>("name"));

    [Fact]
    public void FirstUse_FromManyThreads_DoesNotThrow()
    {
        const int threads = 8;
        var typeArguments = typeof(object).Assembly.GetExportedTypes()
            .Where(t => !t.IsGenericTypeDefinition && !t.ContainsGenericParameters && !t.IsByRefLike && !t.IsPointer
                        && !(t.IsAbstract && t.IsSealed) && t != typeof(void) && !t.IsByRef)
            .Take(150)
            .ToList();

        var errors = new ConcurrentBag<Exception>();
        foreach (var argument in typeArguments)
        {
            var method = FirstUseMethod.MakeGenericMethod(typeof(RaceEntity<>).MakeGenericType(argument));
            using var barrier = new Barrier(threads);

            var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    Assert.True((bool)method.Invoke(null, null)!);
                }
                catch (Exception ex)
                {
                    errors.Add(ex is TargetInvocationException { InnerException: { } inner } ? inner : ex);
                }
            })).ToList();

            workers.ForEach(t => t.Start());
            workers.ForEach(t => t.Join());
        }

        Assert.Empty(errors);
    }
}
