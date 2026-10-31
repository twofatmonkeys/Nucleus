using System;
using System.Security.Cryptography;
using System.Text;

namespace TFM.Nucleus.Data.Schema
{
    /// <summary>
    /// Fits a name into an engine's identifier limit, deterministically, so the name a provider
    /// creates is the name its next schema check looks for. A name that fits is unchanged. A
    /// longer one keeps its start and end, which carry the prefix and the column, and replaces the
    /// middle with eight hex characters of the full name's SHA-256, so two long names that share
    /// a start and an end still differ.
    /// </summary>
    public static class IdentifierShortener
    {
        private const int HashLength = 8;

        public static string Shorten(string name, int maxBytes)
        {
            ArgumentNullException.ThrowIfNull(name);
            if (Encoding.UTF8.GetByteCount(name) <= maxBytes)
                return name;
            if (maxBytes < HashLength + 4)
                throw new ArgumentOutOfRangeException(nameof(maxBytes), "The limit leaves no room for a shortened name.");

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..HashLength].ToLowerInvariant();
            // Two underscores around the hash; the tail keeps up to 23 bytes (the column), the head the rest.
            var budget = maxBytes - HashLength - 2;
            var tail = TakeBytes(name, Math.Min(23, budget / 2), fromEnd: true);
            var head = TakeBytes(name, budget - Encoding.UTF8.GetByteCount(tail), fromEnd: false);
            return $"{head}_{hash}_{tail}";
        }

        /// <summary>The longest run of whole characters at the start or end of <paramref name="value"/> within <paramref name="maxBytes"/>.</summary>
        private static string TakeBytes(string value, int maxBytes, bool fromEnd)
        {
            var bytes = 0;
            var count = 0;
            while (count < value.Length)
            {
                var index = fromEnd ? value.Length - 1 - count : count;
                var width = char.IsSurrogate(value[index]) ? 2 : 1;
                var size = Encoding.UTF8.GetByteCount(value.AsSpan(fromEnd ? index - width + 1 : index, width));
                if (bytes + size > maxBytes)
                    break;
                bytes += size;
                count += width;
            }
            return fromEnd ? value[^count..] : value[..count];
        }
    }
}
