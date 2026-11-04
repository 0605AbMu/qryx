// Polyfills required to use records and init-only setters when targeting netstandard2.0.

namespace System.Runtime.CompilerServices
{
    /// <summary>Marker type required by the compiler for init-only setters (records) on netstandard2.0.</summary>
    internal static class IsExternalInit
    {
    }
}
