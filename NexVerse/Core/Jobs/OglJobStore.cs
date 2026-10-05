// OpenGenesisLINK persistent Job Engine foundation
// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NexVerse.Core.Jobs
{
    public enum OglJobState { Queued, Running, Completed, Failed, Cancelled }

    public sealed class OglJobSnapshot
    {
        public string JobId { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public OglJobState State { get; init; }
        public int ProgressPercent { get; init; }
        public string ProgressPhase { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string CorrelationId { get; init; } = string.Empty;
        public string Actor { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public DateTimeOffset? StartedAt { get; init; }
        public DateTimeOffset? FinishedAt { get; init; }
        public IReadOnlyDictionary<string,string> Metadata { get; init; } = new Dictionary<string,string>();
        public IReadOnlyDictionary<string,string> Result { get; init; } = new Dictionary<string,string>();
        public IReadOnlyList<string> Logs { get; init; } = Array.Empty<string>();
    }

    public interface IOglJobStore
    {
        OglJobSnapshot Create(string type, string actor, string correlationId, IReadOnlyDictionary<string,string> metadata = null, string jobId = null);
        OglJobSnapshot Get(string jobId);
        IReadOnlyList<OglJobSnapshot> List(int limit = 100);
        OglJobSnapshot Start(string jobId, string phase = "running");
        OglJobSnapshot Report(string jobId, int progressPercent, string phase, string message = null);
        OglJobSnapshot Complete(string jobId, IReadOnlyDictionary<string,string> result = null, string message = null);
        OglJobSnapshot Fail(string jobId, string error);
        OglJobSnapshot Cancel(string jobId, string reason);
        OglJobSnapshot Log(string jobId, string message);
    }

    public sealed class PersistentOglJobStore : IOglJobStore
    {
        private const int MaximumJobs = 4096;
        private const int MaximumLogEntries = 256;
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private readonly object m_Sync = new();
        private readonly string m_Path;
        private Document m_Document;

        public PersistentOglJobStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Job-Store-Pfad ist erforderlich.", nameof(path));
            m_Path = Path.GetFullPath(path);
            m_Document = Load();
            NormalizeAfterRestart();
            SaveLocked();
        }

        public OglJobSnapshot Create(string type, string actor, string correlationId, IReadOnlyDictionary<string,string> metadata = null, string jobId = null)
        {
            if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("Job-Typ ist erforderlich.", nameof(type));
            string id = string.IsNullOrWhiteSpace(jobId) ? Guid.NewGuid().ToString() : jobId.Trim();
            lock (m_Sync)
            {
                if (m_Document.Jobs.ContainsKey(id)) throw new InvalidOperationException("Job existiert bereits: " + id);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                Record r = new() { JobId=id, Type=type.Trim(), Actor=actor??string.Empty, CorrelationId=correlationId??string.Empty, State=OglJobState.Queued, CreatedAt=now, UpdatedAt=now, Metadata=Copy(metadata) };
                m_Document.Jobs[id]=r; TrimLocked(); SaveLocked(); return Snapshot(r);
            }
        }

        public OglJobSnapshot Get(string jobId) { lock(m_Sync) return Find(jobId) is Record r ? Snapshot(r) : null; }
        public IReadOnlyList<OglJobSnapshot> List(int limit=100) { lock(m_Sync) return m_Document.Jobs.Values.OrderByDescending(x=>x.CreatedAt).Take(Math.Clamp(limit,1,1000)).Select(Snapshot).ToArray(); }

        public OglJobSnapshot Start(string jobId,string phase="running") => Mutate(jobId,r => {
            Require(r,OglJobState.Queued); r.State=OglJobState.Running; r.StartedAt=DateTimeOffset.UtcNow; r.ProgressPhase=phase??"running";
        });

        public OglJobSnapshot Report(string jobId,int progressPercent,string phase,string message=null) => Mutate(jobId,r => {
            Require(r,OglJobState.Running); r.ProgressPercent=Math.Clamp(progressPercent,0,99); r.ProgressPhase=phase??string.Empty; if(message!=null)r.Message=message;
        });

        public OglJobSnapshot Complete(string jobId,IReadOnlyDictionary<string,string> result=null,string message=null) => Mutate(jobId,r => {
            Require(r,OglJobState.Running); r.State=OglJobState.Completed; r.ProgressPercent=100; r.ProgressPhase="completed"; r.FinishedAt=DateTimeOffset.UtcNow; r.Result=Copy(result); if(message!=null)r.Message=message;
        });

        public OglJobSnapshot Fail(string jobId,string error) => Mutate(jobId,r => {
            if(IsTerminal(r.State)) throw new InvalidOperationException("Terminaler Job kann nicht fehlschlagen."); r.State=OglJobState.Failed; r.Message=error??string.Empty; r.ProgressPhase="failed"; r.FinishedAt=DateTimeOffset.UtcNow;
        });

        public OglJobSnapshot Cancel(string jobId,string reason) => Mutate(jobId,r => {
            if(IsTerminal(r.State)) throw new InvalidOperationException("Terminaler Job kann nicht abgebrochen werden."); r.State=OglJobState.Cancelled; r.Message=reason??string.Empty; r.ProgressPhase="cancelled"; r.FinishedAt=DateTimeOffset.UtcNow;
        });

        public OglJobSnapshot Log(string jobId,string message) => Mutate(jobId,r => {
            if(string.IsNullOrWhiteSpace(message)) return; r.Logs.Add(DateTimeOffset.UtcNow.ToString("O")+" "+message.Trim()); if(r.Logs.Count>MaximumLogEntries) r.Logs.RemoveRange(0,r.Logs.Count-MaximumLogEntries);
        });

        private OglJobSnapshot Mutate(string id,Action<Record> change) { lock(m_Sync) { Record r=Find(id)??throw new KeyNotFoundException("Job wurde nicht gefunden: "+id); change(r); r.UpdatedAt=DateTimeOffset.UtcNow; SaveLocked(); return Snapshot(r); } }
        private Record Find(string id) => string.IsNullOrWhiteSpace(id)||!m_Document.Jobs.TryGetValue(id.Trim(),out Record r)?null:r;
        private static void Require(Record r,OglJobState state) { if(r.State!=state) throw new InvalidOperationException($"Ungueltiger Job-Zustandswechsel: {r.State} -> erwartet {state}."); }
        private static bool IsTerminal(OglJobState s) => s is OglJobState.Completed or OglJobState.Failed or OglJobState.Cancelled;
        private static Dictionary<string,string> Copy(IReadOnlyDictionary<string,string> d) => d==null?new(StringComparer.OrdinalIgnoreCase):new(d,StringComparer.OrdinalIgnoreCase);

        private void NormalizeAfterRestart()
        {
            m_Document ??= new Document(); m_Document.Jobs ??= new(StringComparer.OrdinalIgnoreCase);
            foreach(Record r in m_Document.Jobs.Values)
            {
                r.Metadata ??= new(StringComparer.OrdinalIgnoreCase); r.Result ??= new(StringComparer.OrdinalIgnoreCase); r.Logs ??= new();
                if(r.State==OglJobState.Running)
                {
                    r.State=OglJobState.Failed; r.ProgressPhase="failed"; r.Message="Job wurde durch einen Server-Neustart unterbrochen."; r.FinishedAt=DateTimeOffset.UtcNow; r.UpdatedAt=DateTimeOffset.UtcNow;
                }
            }
        }

        private void TrimLocked()
        {
            int remove=m_Document.Jobs.Count-MaximumJobs; if(remove<=0)return;
            foreach(string id in m_Document.Jobs.Values.Where(x=>IsTerminal(x.State)).OrderBy(x=>x.CreatedAt).Take(remove).Select(x=>x.JobId).ToArray())m_Document.Jobs.Remove(id);
        }

        private Document Load()
        {
            try { if(!File.Exists(m_Path))return new Document(); return JsonSerializer.Deserialize<Document>(File.ReadAllText(m_Path),JsonOptions)??new Document(); }
            catch(Exception e) when(e is IOException or JsonException) { throw new InvalidDataException("Job-Store konnte nicht geladen werden.",e); }
        }

        private void SaveLocked()
        {
            string dir=Path.GetDirectoryName(m_Path); if(!string.IsNullOrWhiteSpace(dir))Directory.CreateDirectory(dir);
            string temp=m_Path+".tmp"; File.WriteAllText(temp,JsonSerializer.Serialize(m_Document,JsonOptions)); File.Move(temp,m_Path,true);
        }

        private static OglJobSnapshot Snapshot(Record r) => new() { JobId=r.JobId,Type=r.Type,State=r.State,ProgressPercent=r.ProgressPercent,ProgressPhase=r.ProgressPhase,Message=r.Message,CorrelationId=r.CorrelationId,Actor=r.Actor,CreatedAt=r.CreatedAt,UpdatedAt=r.UpdatedAt,StartedAt=r.StartedAt,FinishedAt=r.FinishedAt,Metadata=new Dictionary<string,string>(r.Metadata),Result=new Dictionary<string,string>(r.Result),Logs=r.Logs.ToArray() };

        public sealed class Document { public Dictionary<string,Record> Jobs { get; set; } = new(StringComparer.OrdinalIgnoreCase); }
        public sealed class Record
        {
            public string JobId {get;set;}=string.Empty; public string Type {get;set;}=string.Empty; public OglJobState State {get;set;}=OglJobState.Queued; public int ProgressPercent {get;set;} public string ProgressPhase {get;set;}=string.Empty; public string Message {get;set;}=string.Empty; public string CorrelationId {get;set;}=string.Empty; public string Actor {get;set;}=string.Empty; public DateTimeOffset CreatedAt {get;set;} public DateTimeOffset UpdatedAt {get;set;} public DateTimeOffset? StartedAt {get;set;} public DateTimeOffset? FinishedAt {get;set;} public Dictionary<string,string> Metadata {get;set;}=new(StringComparer.OrdinalIgnoreCase); public Dictionary<string,string> Result {get;set;}=new(StringComparer.OrdinalIgnoreCase); public List<string> Logs {get;set;}=new();
        }
    }
}
