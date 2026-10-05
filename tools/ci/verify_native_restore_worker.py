#!/usr/bin/env python3
from pathlib import Path
w=Path("NexVerse/Core/Jobs/OglJobWorkers.cs").read_text()
api=Path("NexVerse/Server/Api/OglJobsApi.cs").read_text()
conn=Path("NexVerse/Server/Api/NexVerseWorldApiConnector.cs").read_text()
req=[(w,"OglDirectoryRestoreWorker"),(w,'JobType=>"restore.directory"'),(w,'".ogl-backup.manifest"'),(w,"SHA256.HashDataAsync"),(w,"CryptographicOperations.FixedTimeEquals"),(w,"SafeChild("),(w,"Path.IsPathRooted"),(w,'".restore-"'),(w,"Directory.Move(staging,destinationRoot)"),(api,'"/api/v1/jobs/restore"'),(conn,"new OglDirectoryRestoreWorker()")]
missing=[x for t,x in req if x not in t]
if missing: raise SystemExit("Native restore worker missing: "+", ".join(missing))
print("OpenGenesisLINK native restore worker: OK")
