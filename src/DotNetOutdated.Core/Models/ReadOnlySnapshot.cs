using System;
using System.Collections.Generic;
using System.Linq;

namespace DotNetOutdated.Core.Models
{
    internal static class ReadOnlySnapshot
    {
        /// <summary>
        /// Copies <paramref name="items"/> into a list that can't be changed, either through a cast or through the
        /// caller's own reference, so a model's collections can't change after it is created.
        /// </summary>
        public static IReadOnlyList<T> Of<T>(IEnumerable<T> items)
        {
            ArgumentNullException.ThrowIfNull(items);
            return Array.AsReadOnly(items.ToArray());
        }
    }
}
