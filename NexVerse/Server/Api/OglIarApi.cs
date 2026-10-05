// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using NexVerse.Core.ControlPlane;
using NexVerse.Core.Jobs;
using NexVerse.Core.Messaging;
using NexVerse.Core.Security;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;

namespace NexVerse.Server.Api
{
    internal sealed class OglIarApi
    {
        private readonly NexApiAuthenticator m_Authenticator;
        private readonly NexNodeRegistry m_Nodes;
        private readonly IUserAccountService m_Accounts;
        private readonly INexEventBus m_Bus;
        private readonly OglJobEventBridge m_Jobs;

        public OglIarApi(NexApiAuthenticator authenticator, NexNodeRegistry nodes, IUserAccountService accounts, INexEventBus bus, OglJobEventBridge jobs)
        {
            m_Authenticator=authenticator; m_Nodes=nodes; m_Accounts=accounts; m_Bus=bus; m_Jobs=jobs;
        }

        public void Handle(IOSHttpRequest request, IOSHttpResponse response)
        {
            string path=(request?.UriPath??string.Empty).TrimEnd('/');
            if(!Authenticate(request,response)) return;
            if(path.Equals("/api/v1/iar/export",StringComparison.OrdinalIgnoreCase)){Start(request,response,"export");return;}
            if(path.Equals("/api/v1/iar/import",StringComparison.OrdinalIgnoreCase)){Start(request,response,"import");return;}
            const string prefix="/api/v1/iar/operations/";
            if(path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)&&Guid.TryParse(path.Substring(prefix.Length),out Guid id))
            {
                if(!request.HttpMethod.Equals("GET",StringComparison.OrdinalIgnoreCase)){Error(response,HttpStatusCode.MethodNotAllowed,"method_not_allowed","GET ist erforderlich.");return;}
                OglJobSnapshot job=m_Jobs.Get(id.ToString()); if(job==null){Error(response,HttpStatusCode.NotFound,"operation_not_found","IAR-Vorgang wurde nicht gefunden.");return;}
                Json(response,HttpStatusCode.OK,job);return;
            }
            Error(response,HttpStatusCode.NotFound,"not_found","Unbekannter IAR-Endpunkt.");
        }

        private void Start(IOSHttpRequest request, IOSHttpResponse response, string action)
        {
            if(!request.HttpMethod.Equals("POST",StringComparison.OrdinalIgnoreCase)){Error(response,HttpStatusCode.MethodNotAllowed,"method_not_allowed","POST ist erforderlich.");return;}
            JsonDocument doc;
            try{using StreamReader r=new(request.InputStream,Encoding.UTF8,true,1024,true);doc=JsonDocument.Parse(r.ReadToEnd());}
            catch{Error(response,HttpStatusCode.BadRequest,"invalid_json","Ungueltiger JSON-Request.");return;}
            using(doc)
            {
                JsonElement root=doc.RootElement;
                string regionId=Str(root,"region_id"), userId=Str(root,"user_id"), fileName=Str(root,"file_name"), invPath=Str(root,"inventory_path");
                bool merge=root.TryGetProperty("merge",out JsonElement m)&&m.ValueKind==JsonValueKind.True;
                bool dryRun=root.TryGetProperty("dry_run",out JsonElement d)&&d.ValueKind==JsonValueKind.True;
                if(!Guid.TryParse(regionId,out _)){Error(response,HttpStatusCode.BadRequest,"invalid_region_id","region_id muss eine gueltige UUID sein.");return;}
                if(!UUID.TryParse(userId,out UUID uid)){Error(response,HttpStatusCode.BadRequest,"invalid_user_id","user_id muss eine gueltige UUID sein.");return;}
                if(string.IsNullOrWhiteSpace(fileName)||string.IsNullOrWhiteSpace(invPath)){Error(response,HttpStatusCode.BadRequest,"missing_fields","file_name und inventory_path sind erforderlich.");return;}
                UserAccount user=m_Accounts.GetUserAccount(UUID.Zero,uid);
                if(user==null){Error(response,HttpStatusCode.NotFound,"user_not_found","Benutzer wurde nicht gefunden.");return;}
                NexNodeSnapshot node=m_Nodes.FindNodeForRegion(regionId);
                if(node==null||!node.State.Equals("online",StringComparison.OrdinalIgnoreCase)){Error(response,HttpStatusCode.ServiceUnavailable,"region_node_unavailable","Simulator der Region ist nicht erreichbar.");return;}

                Guid id=Guid.NewGuid(); OglJobSnapshot rec=m_Jobs.RegisterOperation("archive.iar."+action,id.ToString(),"world-api",id.ToString(),new Dictionary<string,string>{{"region_id",regionId},{"user_id",userId},{"node_id",node.NodeId},{"action",action},{"file_name",fileName},{"inventory_path",invPath},{"merge",merge.ToString()},{"dry_run",dryRun.ToString()}});
                m_Bus.Publish(new NexEvent("archive.iar.requested","opengenesislink.world-api",new Dictionary<string,string>{
                    ["operation_id"]=id.ToString(),["target_node_id"]=node.NodeId,["region_id"]=regionId,["user_id"]=userId,
                    ["action"]=action,["file_name"]=fileName,["inventory_path"]=invPath,["merge"]=merge.ToString(),["dry_run"]=dryRun.ToString()
                },id.ToString()));
                Json(response,HttpStatusCode.Accepted,rec);
            }
        }

        private bool Authenticate(IOSHttpRequest req,IOSHttpResponse res)
        {
            if(m_Authenticator.TryAuthenticate(req,NexScopes.RegionsManage,out NexPrincipal _,out UserAccount _,out int status,out string error))return true;
            res.AddHeader("WWW-Authenticate","Bearer");Error(res,(HttpStatusCode)status,error,"Authentifizierung oder Berechtigung fehlgeschlagen.");return false;
        }
        private static string Str(JsonElement r,string n)=>r.TryGetProperty(n,out JsonElement v)&&v.ValueKind==JsonValueKind.String?v.GetString()?.Trim()??string.Empty:string.Empty;
        private static void Json(IOSHttpResponse r,HttpStatusCode s,object p){r.StatusCode=(int)s;r.ContentType="application/json; charset=utf-8";r.RawBuffer=JsonSerializer.SerializeToUtf8Bytes(p,new JsonSerializerOptions{WriteIndented=true});}
        private static void Error(IOSHttpResponse r,HttpStatusCode s,string e,string m)=>Json(r,s,new{error=e,message=m});
    }
}
