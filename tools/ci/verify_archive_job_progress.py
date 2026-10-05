#!/usr/bin/env python3
from pathlib import Path
oar=Path("NexVerse/RegionModules/Archives/OglOarOperationManager.cs").read_text()
iar=Path("NexVerse/RegionModules/Archives/OglIarOperationManager.cs").read_text()
node=Path("NexVerse/RegionModules/NodeAgent/NexVerseNodeAgentModule.cs").read_text()
bridge=Path("NexVerse/Core/Jobs/OglJobEventBridge.cs").read_text()
required=["ProgressPercent","ProgressPhase","TryCancel(","operation_already_running_not_safely_cancellable"]
for name,text in [("OAR",oar),("IAR",iar)]:
    missing=[x for x in required if x not in text]
    if missing: raise SystemExit(name+" progress contract missing: "+", ".join(missing))
if '["progress"]' not in node or '["phase"]' not in node: raise SystemExit("NodeAgent progress payload missing")
if "runningProgress" not in bridge or "m_Jobs.Report" not in bridge: raise SystemExit("Job bridge progress projection missing")
print("OpenGenesisLINK archive progress contract: OK")
