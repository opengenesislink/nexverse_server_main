#!/usr/bin/env python3
from pathlib import Path

w=Path("NexVerse/Core/Jobs/OglJobWorkers.cs").read_text()
api=Path("NexVerse/Server/Api/OglJobsApi.cs").read_text()
conn=Path("NexVerse/Server/Api/NexVerseWorldApiConnector.cs").read_text()

req=[
    (w,"OglAssetReindexWorker"),
    (w,'JobType=>"assets.reindex"'),
    (w,"EnumerationOptions"),
    (w,"FileAttributes.ReparsePoint"),
    (w,"GZipStream"),
    (w,"SHA256.HashDataAsync"),
    (w,"TryExpectedHash"),
    (w,"RelativePath"),
    (w,'".staging-"'),
    (w,"File.Move(staging,indexPath,true)"),
    (api,'"/api/v1/jobs/assets/reindex"'),
    (api,'"assets.reindex"'),
    (conn,"new OglAssetReindexWorker(assetRoot, assetIndexPath)"),
    (conn,'"AssetIndexPath"'),
    (conn,'"BaseDirectory"'),
]
missing=[x for t,x in req if x not in t]
if missing:
    raise SystemExit("Native asset reindex worker missing: "+", ".join(missing))
print("OpenGenesisLINK native asset reindex worker: OK")
