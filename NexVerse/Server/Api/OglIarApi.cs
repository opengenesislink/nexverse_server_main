// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using NexVerse.Core.ControlPlane;
using NexVerse.Core.Messaging;
using NexVerse.Core.Security;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;

namespace NexVerse.Server.Api
{
    internal sealed class OglIarApi : IDisposable
    {
        private readonly NexApiAuthenticator m_Authenticator;
        private readonly NexNodeRegistry m_Nodes;
        private readonly IUserAccountService m_Accounts;
        private readonly INexEventBus m_Bus;
        private readonly IDisposable m_Subscription;
        private readonly ConcurrentDictionary<Guid, Record> m_Operations = new();

        public OglIarApi(NexApiAuthenticator authenticator, NexNodeRegistry nodes, IUserAccountService accounts, INexEventBus bus)
        {
            m_Authenticator=authenticator; m_Nodes=nodes; m_Accounts=accounts; m_Bus=bus;
            m_Subscription=m_Bus.Subscribe("*", ApplyState);
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
                if(!m_Operations.TryGetValue(id,out Record record)){Error(response,HttpStatusCode.NotFound,"operation_not_found","IAR-Vorgang wurde nicht gefunden.");return;}
                Json(response,HttpStatusCode.OK,record.Payload());return;
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

                Guid id=Guid.NewGuid(); Record rec=new(id,regionId,userId,node.NodeId,action,fileName,invPath,merge,dryRun); m_Operations[id]=rec;
                m_Bus.Publish(new NexEvent("archive.iar.requested","opengenesislink.world-api",new Dictionary<string,string>{
                    ["operation_id"]=id.ToString(),["target_node_id"]=node.NodeId,["region_id"]=regionId,["user_id"]=userId,
                    ["action"]=action,["file_name"]=fileName,["inventory_path"]=invPath,["merge"]=merge.ToString(),["dry_run"]=dryRun.ToString()
                },id.ToString()));
                Json(response,HttpStatusCode.Accepted,rec.Payload());
            }
        }

        private void ApplyState(NexEvent e)
        {
            if(e==null||string.IsNullOrWhiteSpace(e.Name)||!e.Name.StartsWith("archive.iar.operation.",StringComparison.OrdinalIgnoreCase)||e.Data==null) return;
            if(!e.Data.TryGetValue("operation_id",out string raw)||!Guid.TryParse(raw,out Guid id)||!m_Operations.TryGetValue(id,out Record r)) return;
            if(e.Data.TryGetValue("state",out string state))r.State=state;
            if(e.Data.TryGetValue("message",out string msg))r.Message=msg;
            if(e.Data.TryGetValue("item_count",out string ic)&&int.TryParse(ic,out int items))r.ItemCount=items;
            if(e.Data.TryGetValue("filtered_count",out string fc)&&int.TryParse(fc,out int filtered))r.FilteredCount=filtered;
            r.UpdatedAt=DateTimeOffset.UtcNow;
        }

        private bool Authenticate(IOSHttpRequest req,IOSHttpResponse res)
        {
            if(m_Authenticator.TryAuthenticate(req,NexScopes.RegionsManage,out NexPrincipal _,out UserAccount _,out int status,out string error))return true;
            res.AddHeader("WWW-Authenticate","Bearer");Error(res,(HttpStatusCode)status,error,"Authentifizierung oder Berechtigung fehlgeschlagen.");return false;
        }
        private static string Str(JsonElement r,string n)=>r.TryGetProperty(n,out JsonElement v)&&v.ValueKind==JsonValueKind.String?v.GetString()?.Trim()??string.Empty:string.Empty;
        private static void Json(IOSHttpResponse r,HttpStatusCode s,object p){r.StatusCode=(int)s;r.ContentType="application/json; charset=utf-8";r.RawBuffer=JsonSerializer.SerializeToUtf8Bytes(p,new JsonSerializerOptions{WriteIndented=true});}
        private static void Error(IOSHttpResponse r,HttpStatusCode s,string e,string m)=>Json(r,s,new{error=e,message=m});
        public void Dispose()=>m_Subscription?.Dispose();

        private sealed class Record
        {
            public Guid Id; public string RegionId,UserId,NodeId,Action,FileName,InventoryPath; public bool Merge,DryRun; public string State="queued",Message=""; public int ItemCount,FilteredCount; public DateTimeOffset CreatedAt=DateTimeOffset.UtcNow,UpdatedAt=DateTimeOffset.UtcNow;
            public Record(Guid id,string region,string user,string node,string action,string file,string path,bool merge,bool dry){Id=id;RegionId=region;UserId=user;NodeId=node;Action=action;FileName=file;InventoryPath=path;Merge=merge;DryRun=dry;}
            public object Payload()=>new{operation_id=Id,region_id=RegionId,user_id=UserId,node_id=NodeId,action=Action,file_name=FileName,inventory_path=InventoryPath,merge=Merge,dry_run=DryRun,state=State,message=Message,item_count=ItemCount,filtered_count=FilteredCount,created_at=CreatedAt,updated_at=UpdatedAt};
        }
    }
}
