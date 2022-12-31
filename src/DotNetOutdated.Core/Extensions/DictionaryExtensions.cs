using System.Collections.Generic;

namespace DotNetOutdated.Core.Extensions
{
    internal static class DictionaryExtensions
    {
        /// <summary>
        /// Tries to get a value in a dictionary identified by its key, otherwise returns default value for passed-in type.
        /// </summary>
        /// <remarks>Once this library is upgraded to .NET Standard 2.1, this method will become obsolete as it's already built in there</remarks>
        public static TValue GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dict, TKey key) =>
            dict.TryGetValue(key, out var value) ? value : default(TValue);
    }
}
