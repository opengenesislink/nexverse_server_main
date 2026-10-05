#!/usr/bin/env python3
from pathlib import Path
store=Path("NexVerse/Core/Jobs/OglJobStore.cs").read_text()
bridge=Path("NexVerse/Core/Jobs/OglJobEventBridge.cs").read_text()
req=[(store,"Queued, Running, Completed, Failed, Cancelled"),(store,"ProgressPercent"),(store,"ProgressPhase"),(store,"IReadOnlyList<string> Logs"),(store,"IReadOnlyDictionary<string,string> Result"),(store,"PersistentOglJobStore"),(store,"NormalizeAfterRestart"),(store,"Server-Neustart unterbrochen"),(store,"File.Move(temp,m_Path,true)"),(bridge,"RegisterOperation"),(bridge,'Subscribe("*"'),(bridge,'"operation_id"'),(bridge,'EndsWith(".completed")'),(bridge,'EndsWith(".failed")')]
missing=[n for t,n in req if n not in t]
if missing: raise SystemExit("Job Engine foundation missing: "+", ".join(missing))
if "Thread.Abort" in store+bridge: raise SystemExit("Unsafe cancellation primitive is forbidden")
print("OpenGenesisLINK Job Engine foundation: OK")
