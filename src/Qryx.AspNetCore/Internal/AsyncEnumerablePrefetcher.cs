using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Http;

namespace Qryx.AspNetCore.Internal;

/// <summary>
/// Starts an <see cref="IAsyncEnumerable{T}"/> handler result (first <c>MoveNextAsync</c>) before the response is
/// written, so that errors raised while the query is being built inside a lazy iterator surface inside the endpoint
/// filter (and can become HTTP 400) instead of aborting an already-started 200 response.
/// The remaining items are still streamed one by one.
/// </summary>
internal static class AsyncEnumerablePrefetcher
{
    private static readonly ConcurrentDictionary<Type, Func<object, CancellationToken, ValueTask<object>>?> Cache = new();

    private static readonly MethodInfo PrefetchMethod =
        typeof(AsyncEnumerablePrefetcher).GetMethod(nameof(PrefetchAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    public static bool TryGetPrefetcher(Type resultType, [MaybeNullWhen(false)] out Func<object, CancellationToken, ValueTask<object>> prefetcher)
    {
        prefetcher = Cache.GetOrAdd(resultType, static type => Create(type));
        return prefetcher != null;
    }

    private static Func<object, CancellationToken, ValueTask<object>>? Create(Type type)
    {
        if (typeof(IResult).IsAssignableFrom(type) || type == typeof(string))
            return null;

        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
            {
                return PrefetchMethod
                    .MakeGenericMethod(iface.GenericTypeArguments[0])
                    .CreateDelegate<Func<object, CancellationToken, ValueTask<object>>>();
            }
        }

        return null;
    }

    private static async ValueTask<object> PrefetchAsync<T>(object source, CancellationToken cancellationToken)
    {
        if (source is PrefetchedAsyncEnumerable<T>)
            return source;

        var enumerator = ((IAsyncEnumerable<T>)source).GetAsyncEnumerator(cancellationToken);
        bool hasFirst;
        try
        {
            hasFirst = await enumerator.MoveNextAsync().ConfigureAwait(false);
        }
        catch
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new PrefetchedAsyncEnumerable<T>(enumerator, hasFirst);
    }

    /// <summary>Single-use sequence that replays the prefetched first item and then continues the original enumerator.</summary>
    private sealed class PrefetchedAsyncEnumerable<T>(IAsyncEnumerator<T> enumerator, bool hasFirst) : IAsyncEnumerable<T>
    {
        private int _enumerated;

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _enumerated, 1) != 0)
                throw new InvalidOperationException("A prefetched Qryx result stream can only be enumerated once.");

            return Enumerate(cancellationToken);
        }

        private async IAsyncEnumerator<T> Enumerate(CancellationToken cancellationToken)
        {
            await using (enumerator.ConfigureAwait(false))
            {
                if (!hasFirst)
                    yield break;

                yield return enumerator.Current;

                while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return enumerator.Current;
                }
            }
        }
    }
}
