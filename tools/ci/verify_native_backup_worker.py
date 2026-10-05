#!/usr/bin/env python3
from pathlib import Path
w=Path("NexVerse/Core/Jobs/OglJobWorkers.cs").read_text()
api=Path("NexVerse/Server/Api/OglJobsApi.cs").read_text()
conn=Path("NexVerse/Server/Api/NexVerseWorldApiConnector.cs").read_text()
req=[(w,"IOglJobWorker"),(w,"OglJobRunner"),(w,"CancellationTokenSource"),(w,"ThrowIfCancellationRequested"),(w,".staging-"),(w,"SHA256.HashDataAsync"),(w,"Directory.Move(staging,destinationRoot)"),(w,"finally{if(Directory.Exists(staging))"),(api,'"/api/v1/jobs/backup"'),(api,'"/cancel"'),(api,"m_Runner.Cancel"),(conn,"jobRunner.Register(new OglDirectoryBackupWorker())")]
missing=[x for t,x in req if x not in t]
if missing: raise SystemExit("Native backup worker missing: "+", ".join(missing))
print("OpenGenesisLINK native backup worker: OK")
