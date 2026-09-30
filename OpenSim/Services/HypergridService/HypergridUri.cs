/*
 * NexVerse Hypergrid URI canonicalization helpers.
 *
 * Hypergrid endpoints are identifiers as well as network locations. Equivalent
 * representations such as http://grid.example and http://grid.example:80/
 * must therefore compare equal without collapsing different schemes, paths or
 * non-default ports.
 */

using System;
using System.Globalization;

namespace OpenSim.Services.HypergridService
{
    public static class HypergridUri
    {
        public static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string candidate = value.Trim();
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri uri))
            {
                string fallback = candidate.TrimEnd('/');
                return fallback.Length == 0 ? string.Empty : fallback + "/";
            }

            string scheme = uri.Scheme.ToLowerInvariant();
            string host = uri.IdnHost.ToLowerInvariant();
            if (uri.HostNameType == UriHostNameType.IPv6)
                host = "[" + host + "]";

            string authority = host;
            if (!uri.IsDefaultPort)
                authority += ":" + uri.Port.ToString(CultureInfo.InvariantCulture);

            string path = uri.AbsolutePath;
            if (string.IsNullOrEmpty(path))
                path = "/";
            else if (!path.EndsWith("/", StringComparison.Ordinal))
                path += "/";

            return scheme + "://" + authority + path;
        }

        public static bool Equivalent(string left, string right)
        {
            string normalizedLeft = Normalize(left);
            string normalizedRight = Normalize(right);

            return normalizedLeft.Length != 0 &&
                   normalizedRight.Length != 0 &&
                   string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }
    }
}
