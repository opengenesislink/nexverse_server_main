#!/usr/bin/env python3
from pathlib import Path
oar=Path("NexVerse/Server/Api/OglOarApi.cs").read_text()
iar=Path("NexVerse/Server/Api/OglIarApi.cs").read_text()
jobs=Path("NexVerse/Server/Api/OglJobsApi.cs").read_text()
connector=Path("NexVerse/Server/Api/NexVerseWorldApiConnector.cs").read_text()
req=[(oar,"OglJobEventBridge"),(oar,'RegisterOperation("archive.oar."'),(oar,"m_Jobs.Get(id)"),(iar,"OglJobEventBridge"),(iar,'RegisterOperation("archive.iar."'),(iar,"m_Jobs.Get(id.ToString())"),(jobs,'"/api/v1/jobs"'),(jobs,"m_Jobs.List()"),(jobs,"m_Jobs.Get(id)"),(connector,"PersistentOglJobStore"),(connector,'"JobStorePath"'),(connector,'"/api/v1/jobs"')]
missing=[n for t,n in req if n not in t]
if missing: raise SystemExit("Job API integration missing: "+", ".join(missing))
for text,name in [(oar,"OAR"),(iar,"IAR")]:
    if "ConcurrentDictionary" in text: raise SystemExit(name+" still has duplicate transient operation registry")
    if 'Subscribe("*"' in text: raise SystemExit(name+" still has duplicate event-state subscription")
if "System.Reflection" in oar+iar: raise SystemExit("Reflection must not be used for Job Store access")
print("OpenGenesisLINK OAR/IAR Job Engine integration: OK")
