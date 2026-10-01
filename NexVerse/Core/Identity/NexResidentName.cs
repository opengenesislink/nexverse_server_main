// SPDX-License-Identifier: MPL-2.0

using System;

namespace NexVerse.Core.Identity
{
    public sealed class NexResidentName
    {
        public string FirstName { get; }
        public string LastName { get; }
        public string CanonicalUsername { get; }
        public bool UsesResidentSuffix { get; }

        public NexResidentName(
            string firstName,
            string lastName)
        {
            FirstName = firstName ?? string.Empty;
            LastName = lastName ?? string.Empty;
            CanonicalUsername =
                (FirstName + "." + LastName)
                    .ToLowerInvariant();
            UsesResidentSuffix =
                string.Equals(
                    LastName,
                    "Resident",
                    StringComparison.OrdinalIgnoreCase);
        }
    }

    public static class NexResidentNameResolver
    {
        public const string ResidentLastName = "Resident";

        public static bool TryResolveLoginInput(
            string firstName,
            string lastName,
            out NexResidentName resolved)
        {
            resolved = null;

            string first =
                (firstName ?? string.Empty).Trim();
            string last =
                (lastName ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(first))
                return false;

            if (string.IsNullOrWhiteSpace(last))
            {
                if (!TrySplitCanonical(
                    first,
                    out string canonicalFirst,
                    out string canonicalLast))
                {
                    canonicalFirst = first;
                    canonicalLast =
                        ResidentLastName;
                }

                first = canonicalFirst;
                last = canonicalLast;
            }
            else if (
                string.Equals(
                    last,
                    ResidentLastName,
                    StringComparison.OrdinalIgnoreCase) &&
                first.EndsWith(
                    ".resident",
                    StringComparison.OrdinalIgnoreCase))
            {
                string shortName =
                    first.Substring(
                        0,
                        first.Length -
                        ".resident".Length);

                if (!string.IsNullOrWhiteSpace(shortName))
                    first = shortName;
            }

            if (string.IsNullOrWhiteSpace(first) ||
                string.IsNullOrWhiteSpace(last))
                return false;

            if (string.Equals(
                last,
                ResidentLastName,
                StringComparison.OrdinalIgnoreCase))
            {
                last = ResidentLastName;
            }

            resolved =
                new NexResidentName(
                    first,
                    last);
            return true;
        }

        public static bool TrySplitCanonical(
            string username,
            out string firstName,
            out string lastName)
        {
            firstName = string.Empty;
            lastName = string.Empty;

            string value =
                (username ?? string.Empty).Trim();

            int separator = value.IndexOf('.');
            if (separator <= 0 ||
                separator != value.LastIndexOf('.') ||
                separator >= value.Length - 1)
                return false;

            firstName =
                value.Substring(0, separator).Trim();
            lastName =
                value.Substring(separator + 1).Trim();

            if (string.IsNullOrWhiteSpace(firstName) ||
                string.IsNullOrWhiteSpace(lastName))
            {
                firstName = string.Empty;
                lastName = string.Empty;
                return false;
            }

            if (string.Equals(
                lastName,
                ResidentLastName,
                StringComparison.OrdinalIgnoreCase))
            {
                lastName = ResidentLastName;
            }

            return true;
        }
    }
}
