#!/usr/bin/env python3
"""Guard the OpenGenesisLINK .NET 8 threadpool defaults and runtime telemetry."""
from pathlib import Path
import re

root = Path(__file__).resolve().parents[2]
application = (root / "OpenSim/Region/Application/Application.cs").read_text(encoding="utf-8")
section = application.split("// Configure Log4Net", 1)[1].split(
    "// Configure nIni aliases", 1
)[0]

assert "MONO_THREADS_PER_CPU" not in application, "obsolete Mono check still active"
assert "ThreadPool.SetMaxThreads(" not in section, "legacy global max override returned"
assert "ThreadPool.SetMinThreads(" not in section, "unmeasured min override returned"
assert 'GetMinThreads(out int minWorkerThreads, out int minIocpThreads)' in section
assert 'GetMaxThreads(out int maxWorkerThreads, out int maxIocpThreads)' in section
assert "ThreadPool.ThreadCount" in section
assert "ThreadPool.PendingWorkItemCount" in section
assert "[OGL RUNTIME]" in section
assert "[OPENSIM MAIN]" not in section
assert "500;" not in section, "historical fixed worker cap found"
assert "2000;" not in section, "historical fixed IOCP cap found"
print("OGL .NET 8 runtime defaults: no inherited Mono tuning; startup diagnostics OK")
