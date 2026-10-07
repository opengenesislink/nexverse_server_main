#!/usr/bin/env python3
"""Regression contract for OpenSim variable-path HTTP handler precedence."""

from pathlib import Path

server = Path("OpenSim/Framework/Servers/HttpServer/BaseHttpServer.cs").read_text(encoding="utf-8")
connector = Path("NexVerse/Server/Api/NexVerseWorldApiConnector.cs").read_text(encoding="utf-8")

method = server.split("private bool TryGetSimpleStreamHandler(", 1)[1].split(
    "\n        /// <summary>", 1
)[0]

# Enforce the production dispatch order so an old first-segment-only router
# cannot silently return when the API grows multi-segment endpoints.
exact = "m_simpleStreamHandlers.TryGetValue(uripath, out handler)"
variable_exact = "m_simpleStreamVarPath.TryGetValue(uripath, out handler)"
variable_prefix = "m_simpleStreamVarPath.TryGetValue(uripath[..slash], out handler)"
assert exact in method, "exact simple-stream routes must retain priority"
assert variable_exact in method, "variable routes must match their root path"
assert variable_prefix in method, "nested variable routes must use segment prefixes"
assert method.index(exact) < method.index(variable_exact) < method.index(variable_prefix)
assert "uripath.LastIndexOf('/')" in method
assert "uripath.LastIndexOf('/', slash - 1)" in method
assert "uripath.IndexOf('/', 2)" not in method, "first-segment-only routing returned"

for required in (
    '"/api/v1/experiences"',
    '"/api/v1/economy"',
    '"/api/v1/groups"',
    '"/api/v1/places"',
    '"/api/v1/inventory"',
    '"/api/v1/jobs"',
    '"/api"',
):
    assert required in connector, f"missing representative router registration: {required}"


def match(path, exact_routes, variable_routes):
    """Model the tested C# precedence with only path-segment boundaries."""
    if path in exact_routes:
        return exact_routes[path]
    if path in variable_routes:
        return variable_routes[path]
    slash = path.rfind("/")
    while slash > 0:
        prefix = path[:slash]
        if prefix in variable_routes:
            return variable_routes[prefix]
        slash = path.rfind("/", 0, slash)
    return None


exact_routes = {
    "/api/v1": "root",
    "/api/v1/health": "health",
    "/api/v1/docs": "docs",
}
variable_routes = {
    "/api": "users",
    "/api/v1/experiences": "experiences",
    "/api/v1/economy": "economy",
    "/api/v1/groups": "groups",
    "/api/v1/places": "places",
    "/api/v1/inventory": "inventory",
    "/api/v1/jobs": "jobs",
}
examples = {
    "/api": "users",
    "/api/v1": "root",
    "/api/v1/health": "health",
    "/api/v1/docs": "docs",
    "/api/v1/experiences": "experiences",
    "/api/v1/experiences/script/resolve": "experiences",
    "/api/v1/experiences/script/details": "experiences",
    "/api/v1/experiences/00000000-0000-0000-0000-000000000001": "experiences",
    "/api/v1/economy/balance": "economy",
    "/api/v1/groups/00000000-0000-0000-0000-000000000001": "groups",
    "/api/v1/places/some-place": "places",
    "/api/v1/inventory/objects": "inventory",
    "/api/v1/jobs/job-1": "jobs",
    "/api/v1/users/me": "users",
    "/api/v1/experiences-other": "users",  # no partial-segment match
    "/apix/v1/experiences": None,             # no partial-segment match
}
for path, expected in examples.items():
    actual = match(path, exact_routes, variable_routes)
    assert actual == expected, f"{path}: expected {expected}, got {actual}"

print("OpenGenesisLINK nested simple-stream routing regression: OK")
