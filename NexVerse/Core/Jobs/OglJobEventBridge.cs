// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;
using NexVerse.Core.Messaging;

namespace NexVerse.Core.Jobs
{
    /// <summary>Projects correlated NexBus operation events into the persistent Job Engine.</summary>
    public sealed class OglJobEventBridge : IDisposable
    {
        private readonly IOglJobStore m_Jobs;
        private readonly IDisposable m_Subscription;

        public OglJobEventBridge(IOglJobStore jobs, INexEventBus bus)
        {
            m_Jobs=jobs??throw new ArgumentNullException(nameof(jobs));
            if(bus==null)throw new ArgumentNullException(nameof(bus));
            m_Subscription=bus.Subscribe("*",Handle);
        }

        public OglJobSnapshot RegisterOperation(string jobType,string operationId,string actor,string correlationId,IReadOnlyDictionary<string,string> metadata=null)
            => m_Jobs.Create(jobType,actor,correlationId,metadata,operationId);

        public OglJobSnapshot Get(string jobId) => m_Jobs.Get(jobId);

        private void Handle(NexEvent e)
        {
            if(e?.Data==null||!e.Data.TryGetValue("operation_id",out string id)||string.IsNullOrWhiteSpace(id))return;
            OglJobSnapshot job=m_Jobs.Get(id); if(job==null)return;

            string name=e.Name?.ToLowerInvariant()??string.Empty;
            try
            {
                if(name.EndsWith(".accepted")||name.EndsWith(".running"))
                {
                    if(job.State==OglJobState.Queued)m_Jobs.Start(id,"running");
                    return;
                }
                if(name.EndsWith(".completed"))
                {
                    if(job.State==OglJobState.Queued)m_Jobs.Start(id,"running");
                    m_Jobs.Complete(id,CopyResult(e.Data),Message(e));
                    return;
                }
                if(name.EndsWith(".failed"))
                {
                    m_Jobs.Fail(id,Message(e)); return;
                }
                if(name.EndsWith(".cancelled"))
                {
                    m_Jobs.Cancel(id,Message(e)); return;
                }

                if(e.Data.TryGetValue("progress",out string raw)&&int.TryParse(raw,out int progress))
                {
                    if(job.State==OglJobState.Queued)m_Jobs.Start(id,"running");
                    string phase=e.Data.TryGetValue("phase",out string p)?p:"running";
                    m_Jobs.Report(id,progress,phase,Message(e));
                }
            }
            catch(InvalidOperationException)
            {
                // Duplicate/out-of-order terminal events are ignored; state transitions remain strict in the store.
            }
        }

        private static string Message(NexEvent e)=>e.Data.TryGetValue("message",out string m)?m??string.Empty:string.Empty;
        private static Dictionary<string,string> CopyResult(IReadOnlyDictionary<string,string> data)
        {
            Dictionary<string,string> result=new(StringComparer.OrdinalIgnoreCase);
            foreach(KeyValuePair<string,string> kv in data)
                if(!string.Equals(kv.Key,"operation_id",StringComparison.OrdinalIgnoreCase))result[kv.Key]=kv.Value??string.Empty;
            return result;
        }
        public void Dispose()=>m_Subscription?.Dispose();
    }
}
