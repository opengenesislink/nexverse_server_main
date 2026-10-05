// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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
}
