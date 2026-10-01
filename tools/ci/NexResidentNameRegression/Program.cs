using System;
using NexVerse.Core.Identity;

internal static class Program
{
    private static void Require(
        bool condition,
        string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static NexResidentName Resolve(
        string first,
        string last)
    {
        Require(
            NexResidentNameResolver.TryResolveLoginInput(
                first,
                last,
                out NexResidentName resolved),
            "name did not resolve: " + first + " / " + last);
        return resolved;
    }

    private static int Main()
    {
        NexResidentName shortName =
            Resolve("Jam", "");
        Require(
            shortName.FirstName == "Jam" &&
            shortName.LastName == "Resident" &&
            shortName.CanonicalUsername == "jam.resident",
            "short Resident login normalization failed");

        NexResidentName dotted =
            Resolve("jam.resident", "");
        Require(
            dotted.FirstName == "jam" &&
            dotted.LastName == "Resident" &&
            dotted.CanonicalUsername == "jam.resident",
            "dotted Resident login normalization failed");

        NexResidentName legacy =
            Resolve("Jam", "Resident");
        Require(
            legacy.FirstName == "Jam" &&
            legacy.LastName == "Resident",
            "legacy Resident login normalization failed");

        NexResidentName doubled =
            Resolve("jam.resident", "Resident");
        Require(
            doubled.FirstName == "jam" &&
            doubled.LastName == "Resident",
            "viewer-supplied Resident suffix was not de-duplicated");

        NexResidentName traditional =
            Resolve("Sleimer", "Akina");
        Require(
            traditional.FirstName == "Sleimer" &&
            traditional.LastName == "Akina" &&
            traditional.CanonicalUsername == "sleimer.akina",
            "traditional two-name account changed");

        NexResidentName canonicalTraditional =
            Resolve("sleimer.akina", "");
        Require(
            canonicalTraditional.FirstName == "sleimer" &&
            canonicalTraditional.LastName == "akina",
            "dotted two-name account normalization failed");

        Require(
            !NexResidentNameResolver.TryResolveLoginInput(
                "",
                "",
                out _),
            "empty login name must fail");

        Require(
            !NexResidentNameResolver.TrySplitCanonical(
                "too.many.dots",
                out _,
                out _),
            "ambiguous canonical username must fail");

        Console.WriteLine(
            "NexVerse resident username normalization regression: OK");
        return 0;
    }
}
