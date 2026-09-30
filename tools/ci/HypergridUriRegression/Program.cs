using System;
using OpenSim.Services.HypergridService;

static void AssertEquivalent(string left, string right)
{
    if (!HypergridUri.Equivalent(left, right))
        throw new Exception($"Expected equivalent HG URIs: '{left}' and '{right}'.");
}

static void AssertDifferent(string left, string right)
{
    if (HypergridUri.Equivalent(left, right))
        throw new Exception($"Expected distinct HG URIs: '{left}' and '{right}'.");
}

AssertEquivalent("http://hg.stadt-nexverse.de", "http://HG.STADT-NEXVERSE.DE:80/");
AssertEquivalent("https://grid.example", "https://grid.example:443/");
AssertEquivalent("http://grid.example/world", "http://grid.example:80/world/");
AssertDifferent("http://grid.example", "https://grid.example/");
AssertDifferent("http://grid.example", "http://grid.example:8002/");
AssertDifferent("http://grid.example/a", "http://grid.example/b");

Console.WriteLine("Hypergrid URI normalization regression checks passed.");
