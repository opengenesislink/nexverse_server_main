// SPDX-License-Identifier: MPL-2.0
using System;
using System.Net;
using System.Text.Json;
using NexVerse.Core.Jobs;
using NexVerse.Core.Security;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;

namespace NexVerse.Server.Api
{
    internal sealed class OglJobsApi
    {
        private readonly NexApiAuthenticator m_Authenticator;
        private readonly IOglJobStore m_Jobs; private readonly OglJobRunner m_Runner;
        public OglJobsApi(NexApiAuthenticator authenticator, IOglJobStore jobs, OglJobRunner runner){m_Authenticator=authenticator;m_Jobs=jobs;m_Runner=runner;}

        public void Handle(IOSHttpRequest request,IOSHttpResponse response)
        {
            if(!Authenticate(request,response))return;
            string path=(request.UriPath??string.Empty).TrimEnd('/');
            if(path.Equals("/api/v1/jobs/backup",StringComparison.OrdinalIgnoreCase) && request.HttpMethod.Equals("POST",StringComparison.OrdinalIgnoreCase)){QueueBackup(request,response);return;}
            if(path.Equals("/api/v1/jobs/restore",StringComparison.OrdinalIgnoreCase) && request.HttpMethod.Equals("POST",StringComparison.OrdinalIgnoreCase)){QueueRestore(request,response);return;}
            if(path.Equals("/api/v1/jobs/assets/reindex",StringComparison.OrdinalIgnoreCase) && request.HttpMethod.Equals("POST",StringComparison.OrdinalIgnoreCase)){QueueAssetReindex(response);return;}
            if(path.Equals("/api/v1/jobs/regions/migrate",StringComparison.OrdinalIgnoreCase) && request.HttpMethod.Equals("POST",StringComparison.OrdinalIgnoreCase)){QueueRegionMigration(request,response);return;}
            if(path.EndsWith("/cancel",StringComparison.OrdinalIgnoreCase) && request.HttpMethod.Equals("POST",StringComparison.OrdinalIgnoreCase)){string id=path.Substring("/api/v1/jobs/".Length);id=id.Substring(0,id.Length-"/cancel".Length);if(m_Runner.Cancel(id,out string reason)){Json(response,HttpStatusCode.Accepted,m_Jobs.Get(id));return;}Error(response,HttpStatusCode.Conflict,"cancel_rejected",reason);return;}
            if(!string.Equals(request.HttpMethod,"GET",StringComparison.OrdinalIgnoreCase)){Error(response,HttpStatusCode.MethodNotAllowed,"method_not_allowed","GET oder unterstuetztes POST ist erforderlich.");return;}
            if(path.Equals("/api/v1/jobs",StringComparison.OrdinalIgnoreCase)){Json(response,HttpStatusCode.OK,new{jobs=m_Jobs.List()});return;}
            const string prefix="/api/v1/jobs/";
            if(path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))
            {
                string id=path.Substring(prefix.Length);
                OglJobSnapshot job=m_Jobs.Get(id);
                if(job==null){Error(response,HttpStatusCode.NotFound,"job_not_found","Job wurde nicht gefunden.");return;}
                Json(response,HttpStatusCode.OK,job);return;
            }
            Error(response,HttpStatusCode.NotFound,"not_found","Unbekannter Job-Endpunkt.");
        }
        private void QueueBackup(IOSHttpRequest request,IOSHttpResponse response)
        {
            try
            {
                using JsonDocument doc=JsonDocument.Parse(request.InputStream);
                JsonElement root=doc.RootElement;
                string source=root.TryGetProperty("source",out JsonElement s)?s.GetString()??string.Empty:string.Empty;
                string destination=root.TryGetProperty("destination",out JsonElement d)?d.GetString()??string.Empty:string.Empty;
                OglJobSnapshot job=m_Runner.Queue("backup.directory","world-api",new System.Collections.Generic.Dictionary<string,string>{{"source",source},{"destination",destination}});
                Json(response,HttpStatusCode.Accepted,job);
            }
            catch(Exception e){Error(response,HttpStatusCode.BadRequest,"backup_job_rejected",e.Message);}
        }
        private void QueueRestore(IOSHttpRequest request,IOSHttpResponse response)
        {
            try
            {
                using JsonDocument doc=JsonDocument.Parse(request.InputStream);JsonElement root=doc.RootElement;
                string backup=root.TryGetProperty("backup",out JsonElement b)?b.GetString()??string.Empty:string.Empty;
                string destination=root.TryGetProperty("destination",out JsonElement d)?d.GetString()??string.Empty:string.Empty;
                OglJobSnapshot job=m_Runner.Queue("restore.directory","world-api",new System.Collections.Generic.Dictionary<string,string>{{"backup",backup},{"destination",destination}});
                Json(response,HttpStatusCode.Accepted,job);
            }
            catch(Exception e){Error(response,HttpStatusCode.BadRequest,"restore_job_rejected",e.Message);}
        }
        private void QueueAssetReindex(IOSHttpResponse response)
        {
            try
            {
                OglJobSnapshot job=m_Runner.Queue("assets.reindex","world-api",new System.Collections.Generic.Dictionary<string,string>());
                Json(response,HttpStatusCode.Accepted,job);
            }
            catch(Exception e){Error(response,HttpStatusCode.BadRequest,"asset_reindex_job_rejected",e.Message);}
        }
        private void QueueRegionMigration(IOSHttpRequest request,IOSHttpResponse response)
        {
            try
            {
                using JsonDocument doc=JsonDocument.Parse(request.InputStream);
                JsonElement root=doc.RootElement;
                string regionId=root.TryGetProperty("region_id",out JsonElement r)&&r.ValueKind==JsonValueKind.String?r.GetString()??string.Empty:string.Empty;
                string targetNodeId=root.TryGetProperty("target_node_id",out JsonElement n)&&n.ValueKind==JsonValueKind.String?n.GetString()??string.Empty:string.Empty;
                bool dryRun=root.TryGetProperty("dry_run",out JsonElement d)&&d.ValueKind==JsonValueKind.True;

                OglJobSnapshot job=m_Runner.Queue(
                    "regions.migrate",
                    "world-api",
                    new System.Collections.Generic.Dictionary<string,string>
                    {
                        {"region_id",regionId},
                        {"target_node_id",targetNodeId},
                        {"dry_run",dryRun.ToString()}
                    });

                Json(response,HttpStatusCode.Accepted,job);
            }
            catch(Exception e){Error(response,HttpStatusCode.BadRequest,"region_migration_job_rejected",e.Message);}
        }
        private bool Authenticate(IOSHttpRequest req,IOSHttpResponse res)
        {
            if(m_Authenticator.TryAuthenticate(req,NexScopes.RegionsManage,out NexPrincipal _,out UserAccount _,out int status,out string error))return true;
            res.AddHeader("WWW-Authenticate","Bearer");Error(res,(HttpStatusCode)status,error,"Authentifizierung oder Berechtigung fehlgeschlagen.");return false;
        }
        private static void Json(IOSHttpResponse r,HttpStatusCode s,object p){r.StatusCode=(int)s;r.ContentType="application/json; charset=utf-8";r.RawBuffer=JsonSerializer.SerializeToUtf8Bytes(p,new JsonSerializerOptions{WriteIndented=true});}
        private static void Error(IOSHttpResponse r,HttpStatusCode s,string e,string m)=>Json(r,s,new{error=e,message=m});
    }
}
