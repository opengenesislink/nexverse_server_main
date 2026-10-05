// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NexVerse.Core.Jobs
{
    public interface IOglJobWorker
    {
        string JobType { get; }
        Task<IReadOnlyDictionary<string,string>> ExecuteAsync(OglJobContext context, IReadOnlyDictionary<string,string> parameters, CancellationToken cancellationToken);
    }

    public sealed class OglJobContext
    {
        private readonly IOglJobStore m_Store;
        public string JobId { get; }
        internal OglJobContext(IOglJobStore store,string jobId){m_Store=store;JobId=jobId;}
        public void Progress(int percent,string phase,string message=null)=>m_Store.Report(JobId,percent,phase,message);
        public void Log(string message)=>m_Store.Log(JobId,message);
    }

    public sealed class OglJobRunner
    {
        private readonly IOglJobStore m_Store;
        private readonly ConcurrentDictionary<string,IOglJobWorker> m_Workers=new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string,CancellationTokenSource> m_Running=new(StringComparer.OrdinalIgnoreCase);
        public OglJobRunner(IOglJobStore store)=>m_Store=store??throw new ArgumentNullException(nameof(store));
        public void Register(IOglJobWorker worker){if(worker==null||string.IsNullOrWhiteSpace(worker.JobType))throw new ArgumentException("Ungueltiger Job-Worker.");if(!m_Workers.TryAdd(worker.JobType,worker))throw new InvalidOperationException("Job-Worker bereits registriert: "+worker.JobType);}
        public OglJobSnapshot Queue(string type,string actor,IReadOnlyDictionary<string,string> parameters,string correlationId=null)
        {
            if(!m_Workers.ContainsKey(type))throw new InvalidOperationException("Unbekannter Job-Typ: "+type);
            OglJobSnapshot job=m_Store.Create(type,actor,correlationId??Guid.NewGuid().ToString(),parameters);
            _=Task.Run(()=>RunAsync(job.JobId,type,parameters??new Dictionary<string,string>()));
            return job;
        }
        public bool Cancel(string jobId,out string reason)
        {
            reason=string.Empty;OglJobSnapshot job=m_Store.Get(jobId);if(job==null){reason="job_not_found";return false;}
            if(job.State==OglJobState.Queued){m_Store.Cancel(jobId,"Vom Administrator abgebrochen.");return true;}
            if(job.State!=OglJobState.Running){reason="job_already_terminal";return false;}
            if(!m_Running.TryGetValue(jobId,out CancellationTokenSource cts)){reason="worker_not_cancellable";return false;}
            cts.Cancel();return true;
        }
        private async Task RunAsync(string id,string type,IReadOnlyDictionary<string,string> parameters)
        {
            using CancellationTokenSource cts=new(); if(!m_Running.TryAdd(id,cts))return;
            try
            {
                OglJobSnapshot current=m_Store.Get(id);if(current==null||current.State==OglJobState.Cancelled)return;
                m_Store.Start(id,"starting");
                IReadOnlyDictionary<string,string> result=await m_Workers[type].ExecuteAsync(new OglJobContext(m_Store,id),parameters,cts.Token).ConfigureAwait(false);
                cts.Token.ThrowIfCancellationRequested();m_Store.Complete(id,result,"Job erfolgreich abgeschlossen.");
            }
            catch(OperationCanceledException){OglJobSnapshot j=m_Store.Get(id);if(j!=null&&j.State==OglJobState.Running)m_Store.Cancel(id,"Job kontrolliert abgebrochen.");}
            catch(Exception e){OglJobSnapshot j=m_Store.Get(id);if(j!=null&&j.State==OglJobState.Running)m_Store.Fail(id,e.Message);}
            finally{m_Running.TryRemove(id,out _);}
        }
    }

    public sealed class OglDirectoryBackupWorker : IOglJobWorker
    {
        public string JobType=>"backup.directory";
        public async Task<IReadOnlyDictionary<string,string>> ExecuteAsync(OglJobContext context,IReadOnlyDictionary<string,string> parameters,CancellationToken cancellationToken)
        {
            string source=Required(parameters,"source");string destination=Required(parameters,"destination");
            string sourceRoot=Path.GetFullPath(source);string destinationRoot=Path.GetFullPath(destination);
            if(!Directory.Exists(sourceRoot))throw new DirectoryNotFoundException("Backup-Quelle existiert nicht: "+sourceRoot);
            if(destinationRoot.StartsWith(sourceRoot.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.Ordinal))throw new InvalidOperationException("Backup-Ziel darf nicht innerhalb der Quelle liegen.");
            string[] files=Directory.GetFiles(sourceRoot,"*",SearchOption.AllDirectories);long total=0;foreach(string f in files)total+=new FileInfo(f).Length;
            string staging=destinationRoot+".staging-"+Guid.NewGuid().ToString("N");long copied=0;context.Progress(2,"inventory","Backup-Dateien erfasst.");context.Log($"{files.Length} Dateien, {total} Bytes.");
            try
            {
                Directory.CreateDirectory(staging);
                for(int i=0;i<files.Length;i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();string relative=Path.GetRelativePath(sourceRoot,files[i]);string target=Path.Combine(staging,relative);Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await CopyAsync(files[i],target,cancellationToken).ConfigureAwait(false);copied+=new FileInfo(files[i]).Length;int progress=5+(int)(80L*(i+1)/Math.Max(1,files.Length));context.Progress(progress,"copying",relative);
                }
                context.Progress(88,"verifying","Backup wird verifiziert.");string manifest=Path.Combine(staging,".ogl-backup.manifest");using StreamWriter writer=new(manifest,false);foreach(string file in Directory.GetFiles(staging,"*",SearchOption.AllDirectories)){if(file==manifest)continue;cancellationToken.ThrowIfCancellationRequested();string rel=Path.GetRelativePath(staging,file);string hash=Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(file),cancellationToken).ConfigureAwait(false));await writer.WriteLineAsync(hash+"  "+rel).ConfigureAwait(false);}
                await writer.FlushAsync().ConfigureAwait(false);cancellationToken.ThrowIfCancellationRequested();context.Progress(96,"committing","Backup wird atomar aktiviert.");
                if(Directory.Exists(destinationRoot))throw new IOException("Backup-Ziel existiert bereits: "+destinationRoot);Directory.Move(staging,destinationRoot);
                return new Dictionary<string,string>{{"destination",destinationRoot},{"file_count",files.Length.ToString()},{"bytes",copied.ToString()},{"manifest",Path.Combine(destinationRoot,".ogl-backup.manifest")}};
            }
            finally{if(Directory.Exists(staging))Directory.Delete(staging,true);}
        }
        private static async Task CopyAsync(string source,string target,CancellationToken token){await using FileStream input=new(source,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,true);await using FileStream output=new(target,FileMode.CreateNew,FileAccess.Write,FileShare.None,1024*1024,true);await input.CopyToAsync(output,1024*1024,token).ConfigureAwait(false);await output.FlushAsync(token).ConfigureAwait(false);}
        private static string Required(IReadOnlyDictionary<string,string> p,string key)=>p!=null&&p.TryGetValue(key,out string v)&&!string.IsNullOrWhiteSpace(v)?v.Trim():throw new ArgumentException("Job-Parameter fehlt: "+key);
    }
    public sealed class OglDirectoryRestoreWorker : IOglJobWorker
    {
        public string JobType=>"restore.directory";
        public async Task<IReadOnlyDictionary<string,string>> ExecuteAsync(OglJobContext context,IReadOnlyDictionary<string,string> parameters,CancellationToken cancellationToken)
        {
            string backup=Required(parameters,"backup");string destination=Required(parameters,"destination");
            string backupRoot=Path.GetFullPath(backup);string destinationRoot=Path.GetFullPath(destination);
            if(!Directory.Exists(backupRoot))throw new DirectoryNotFoundException("Backup existiert nicht: "+backupRoot);
            if(Directory.Exists(destinationRoot)||File.Exists(destinationRoot))throw new IOException("Restore-Ziel existiert bereits: "+destinationRoot);
            string manifestPath=Path.Combine(backupRoot,".ogl-backup.manifest");if(!File.Exists(manifestPath))throw new InvalidDataException("OpenGenesisLINK Backup-Manifest fehlt.");
            string[] lines=await File.ReadAllLinesAsync(manifestPath,cancellationToken).ConfigureAwait(false);List<(string Hash,string Relative)> entries=new();
            foreach(string line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();if(string.IsNullOrWhiteSpace(line))continue;int split=line.IndexOf("  ",StringComparison.Ordinal);
                if(split!=64)throw new InvalidDataException("Ungueltiger Manifest-Eintrag.");
                string hash=line.Substring(0,64);string relative=line.Substring(split+2);string source=SafeChild(backupRoot,relative);
                if(!File.Exists(source))throw new InvalidDataException("Backup-Datei fehlt: "+relative);entries.Add((hash,relative));
            }
            context.Progress(5,"verifying","Backup-Manifest wird vollstaendig geprueft.");long bytes=0;
            for(int i=0;i<entries.Count;i++)
            {
                cancellationToken.ThrowIfCancellationRequested();string source=SafeChild(backupRoot,entries[i].Relative);await using FileStream stream=File.OpenRead(source);string actual=Convert.ToHexString(await SHA256.HashDataAsync(stream,cancellationToken).ConfigureAwait(false));
                if(!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(entries[i].Hash),Convert.FromHexString(actual)))throw new InvalidDataException("Backup-Integritaetspruefung fehlgeschlagen: "+entries[i].Relative);
                bytes+=new FileInfo(source).Length;context.Progress(5+(int)(40L*(i+1)/Math.Max(1,entries.Count)),"verifying",entries[i].Relative);
            }
            string staging=destinationRoot+".restore-"+Guid.NewGuid().ToString("N");long restored=0;
            try
            {
                Directory.CreateDirectory(staging);context.Progress(48,"restoring","Restore in Staging gestartet.");
                for(int i=0;i<entries.Count;i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();string source=SafeChild(backupRoot,entries[i].Relative);string target=SafeChild(staging,entries[i].Relative);Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await CopyAsync(source,target,cancellationToken).ConfigureAwait(false);restored+=new FileInfo(source).Length;context.Progress(48+(int)(45L*(i+1)/Math.Max(1,entries.Count)),"restoring",entries[i].Relative);
                }
                cancellationToken.ThrowIfCancellationRequested();context.Progress(96,"committing","Restore wird atomar aktiviert.");if(Directory.Exists(destinationRoot)||File.Exists(destinationRoot))throw new IOException("Restore-Ziel wurde zwischenzeitlich angelegt.");
                Directory.Move(staging,destinationRoot);return new Dictionary<string,string>{{"destination",destinationRoot},{"file_count",entries.Count.ToString()},{"bytes",restored.ToString()},{"verified","true"}};
            }
            finally{if(Directory.Exists(staging))Directory.Delete(staging,true);}
        }
        private static string SafeChild(string root,string relative)
        {
            if(string.IsNullOrWhiteSpace(relative)||Path.IsPathRooted(relative))throw new InvalidDataException("Ungueltiger relativer Backup-Pfad.");
            string normalizedRoot=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;string child=Path.GetFullPath(Path.Combine(root,relative));
            if(!child.StartsWith(normalizedRoot,StringComparison.Ordinal))throw new InvalidDataException("Backup-Pfad verlaesst den erlaubten Bereich.");
            return child;
        }
        private static async Task CopyAsync(string source,string target,CancellationToken token){await using FileStream input=new(source,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,true);await using FileStream output=new(target,FileMode.CreateNew,FileAccess.Write,FileShare.None,1024*1024,true);await input.CopyToAsync(output,1024*1024,token).ConfigureAwait(false);await output.FlushAsync(token).ConfigureAwait(false);}
        private static string Required(IReadOnlyDictionary<string,string> p,string key)=>p!=null&&p.TryGetValue(key,out string v)&&!string.IsNullOrWhiteSpace(v)?v.Trim():throw new ArgumentException("Job-Parameter fehlt: "+key);
    }

    public sealed class OglAssetReindexWorker : IOglJobWorker
    {
        private readonly string m_AssetRoot;
        private readonly string m_IndexPath;

        public OglAssetReindexWorker(string assetRoot,string indexPath)
        {
            m_AssetRoot=assetRoot??string.Empty;
            m_IndexPath=indexPath??string.Empty;
        }

        public string JobType=>"assets.reindex";

        public async Task<IReadOnlyDictionary<string,string>> ExecuteAsync(OglJobContext context,IReadOnlyDictionary<string,string> parameters,CancellationToken cancellationToken)
        {
            if(string.IsNullOrWhiteSpace(m_AssetRoot))throw new InvalidOperationException("FSAssetStore BaseDirectory ist nicht konfiguriert.");
            if(string.IsNullOrWhiteSpace(m_IndexPath))throw new InvalidOperationException("AssetIndexPath ist nicht konfiguriert.");

            string assetRoot=Path.GetFullPath(m_AssetRoot);
            string indexPath=Path.GetFullPath(m_IndexPath);
            if(!Directory.Exists(assetRoot))throw new DirectoryNotFoundException("FSAssetStore-Verzeichnis existiert nicht: "+assetRoot);

            context.Progress(2,"inventory","Asset-Dateien werden erfasst.");
            EnumerationOptions options=new(){RecurseSubdirectories=true,IgnoreInaccessible=false,AttributesToSkip=FileAttributes.ReparsePoint};
            int candidateCount=0;int ignoredFiles=0;
            foreach(string file in Directory.EnumerateFiles(assetRoot,"*",options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(TryExpectedHash(file,out _,out _))candidateCount++;else ignoredFiles++;
            }
            context.Log($"{candidateCount} Asset-Dateien erkannt, {ignoredFiles} sonstige Dateien ignoriert.");
            context.Progress(8,"verifying","Asset-Inhalte werden gegen ihre SHA-256-Dateinamen geprueft.");

            string indexDirectory=Path.GetDirectoryName(indexPath);
            if(!string.IsNullOrWhiteSpace(indexDirectory))Directory.CreateDirectory(indexDirectory);
            string staging=indexPath+".staging-"+Guid.NewGuid().ToString("N");
            long storedBytes=0;int indexedAssets=0;int compressedAssets=0;int duplicateHashes=0;int processed=0;int lastProgress=-1;
            HashSet<string> seenHashes=new(StringComparer.OrdinalIgnoreCase);

            try
            {
                {
                    await using FileStream output=new(staging,FileMode.CreateNew,FileAccess.Write,FileShare.None,1024*1024,true);
                    using Utf8JsonWriter writer=new(output,new JsonWriterOptions{Indented=true});
                writer.WriteStartObject();
                writer.WriteNumber("format_version",1);
                writer.WriteString("generated_utc",DateTimeOffset.UtcNow);
                writer.WriteString("asset_root",assetRoot);
                writer.WriteStartArray("assets");

                foreach(string file in Directory.EnumerateFiles(assetRoot,"*",options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if(!TryExpectedHash(file,out string expectedHash,out bool compressed))continue;

                    string actualHash=await ComputePayloadHashAsync(file,compressed,cancellationToken).ConfigureAwait(false);
                    if(!string.Equals(expectedHash,actualHash,StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Asset-Integritaetspruefung fehlgeschlagen: "+RelativePath(assetRoot,file));

                    processed++;
                    if(!seenHashes.Add(expectedHash))
                    {
                        duplicateHashes++;
                    }
                    else
                    {
                        FileInfo info=new(file);storedBytes+=info.Length;if(compressed)compressedAssets++;
                        writer.WriteStartObject();
                        writer.WriteString("hash",expectedHash);
                        writer.WriteString("relative_path",RelativePath(assetRoot,file));
                        writer.WriteBoolean("compressed",compressed);
                        writer.WriteNumber("stored_bytes",info.Length);
                        writer.WriteEndObject();
                        indexedAssets++;
                    }

                    int progress=Math.Min(90,8+(int)(82L*processed/Math.Max(1,candidateCount)));
                    if(progress!=lastProgress)
                    {
                        context.Progress(progress,"verifying",RelativePath(assetRoot,file));
                        lastProgress=progress;
                    }
                }

                writer.WriteEndArray();
                writer.WriteNumber("indexed_assets",indexedAssets);
                writer.WriteNumber("compressed_assets",compressedAssets);
                writer.WriteNumber("duplicate_hashes",duplicateHashes);
                writer.WriteNumber("ignored_files",ignoredFiles);
                writer.WriteNumber("stored_bytes",storedBytes);
                writer.WriteEndObject();
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                context.Progress(96,"committing","Asset-Index wird atomar aktiviert.");
                File.Move(staging,indexPath,true);

                return new Dictionary<string,string>
                {
                    {"asset_root",assetRoot},
                    {"index_path",indexPath},
                    {"indexed_assets",indexedAssets.ToString()},
                    {"compressed_assets",compressedAssets.ToString()},
                    {"duplicate_hashes",duplicateHashes.ToString()},
                    {"ignored_files",ignoredFiles.ToString()},
                    {"stored_bytes",storedBytes.ToString()},
                    {"verified","true"}
                };
            }
            finally
            {
                if(File.Exists(staging))File.Delete(staging);
            }
        }

        private static async Task<string> ComputePayloadHashAsync(string path,bool compressed,CancellationToken token)
        {
            await using FileStream input=new(path,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,true);
            if(compressed)
            {
                using GZipStream gzip=new(input,CompressionMode.Decompress,false);
                return Convert.ToHexString(await SHA256.HashDataAsync(gzip,token).ConfigureAwait(false));
            }
            return Convert.ToHexString(await SHA256.HashDataAsync(input,token).ConfigureAwait(false));
        }

        private static bool TryExpectedHash(string path,out string hash,out bool compressed)
        {
            string name=Path.GetFileName(path);compressed=name.EndsWith(".gz",StringComparison.OrdinalIgnoreCase);
            if(compressed)name=Path.GetFileNameWithoutExtension(name);
            if(name.Length!=64){hash=string.Empty;return false;}
            for(int i=0;i<name.Length;i++)
            {
                char c=name[i];
                if(!((c>='0'&&c<='9')||(c>='a'&&c<='f')||(c>='A'&&c<='F'))){hash=string.Empty;return false;}
            }
            hash=name.ToUpperInvariant();return true;
        }

        private static string RelativePath(string root,string file)
        {
            string normalizedRoot=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string full=Path.GetFullPath(file);
            if(!full.StartsWith(normalizedRoot,StringComparison.Ordinal))throw new InvalidDataException("Asset-Pfad verlaesst den erlaubten Bereich.");
            string relative=Path.GetRelativePath(root,full).Replace(Path.DirectorySeparatorChar,'/');
            if(Path.AltDirectorySeparatorChar!=Path.DirectorySeparatorChar)relative=relative.Replace(Path.AltDirectorySeparatorChar,'/');
            return relative;
        }
    }

}
