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
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;

namespace NexVerse.Server.Api
{
    internal sealed class OglOarApi
    {
        private readonly NexApiAuthenticator m_Authenticator; private readonly NexNodeRegistry m_Nodes; private readonly INexEventBus m_Bus; private readonly OglJobEventBridge m_Jobs;
        public OglOarApi(NexApiAuthenticator authenticator,NexNodeRegistry nodes,INexEventBus bus,OglJobEventBridge jobs){m_Authenticator=authenticator;m_Nodes=nodes;m_Bus=bus;m_Jobs=jobs;}

        public void Handle(IOSHttpRequest request,IOSHttpResponse response)
        {
            string path=(request?.UriPath??string.Empty).TrimEnd('/'); if(!Authenticate(request,response))return;
            if(path.Equals("/api/v1/oar/export",StringComparison.OrdinalIgnoreCase)){Start(request,response,"export");return;}
            if(path.Equals("/api/v1/oar/import",StringComparison.OrdinalIgnoreCase)){Start(request,response,"import");return;}
            const string prefix="/api/v1/oar/operations/";
            if(path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)){if(!request.HttpMethod.Equals("GET",StringComparison.OrdinalIgnoreCase)){Error(response,HttpStatusCode.MethodNotAllowed,"method_not_allowed","GET ist erforderlich.");return;} string id=path.Substring(prefix.Length); var job=m_JobsStore(id); if(job==null){Error(response,HttpStatusCode.NotFound,"operation_not_found","OAR-Vorgang wurde nicht gefunden.");return;} Json(response,HttpStatusCode.OK,job);return;}
            Error(response,HttpStatusCode.NotFound,"not_found","Unbekannter OAR-Endpunkt.");
        }
        private NexVerse.Core.Jobs.OglJobSnapshot m_JobsStore(string id)=>JobStoreAccessor.Get(m_Jobs,id);
        private void Start(IOSHttpRequest request,IOSHttpResponse response,string action)
        {
            if(!request.HttpMethod.Equals("POST",StringComparison.OrdinalIgnoreCase)){Error(response,HttpStatusCode.MethodNotAllowed,"method_not_allowed","POST ist erforderlich.");return;}
            JsonDocument doc; try{using StreamReader r=new(request.InputStream,Encoding.UTF8,true,1024,true);doc=JsonDocument.Parse(r.ReadToEnd());}catch{Error(response,HttpStatusCode.BadRequest,"invalid_json","Ungueltiger JSON-Request.");return;}
            using(doc){JsonElement root=doc.RootElement;string region=Str(root,"region_id"),file=Str(root,"file_name");bool dry=root.TryGetProperty("dry_run",out JsonElement d)&&d.ValueKind==JsonValueKind.True;
                if(!Guid.TryParse(region,out _)){Error(response,HttpStatusCode.BadRequest,"invalid_region_id","region_id muss eine gueltige UUID sein.");return;} if(string.IsNullOrWhiteSpace(file)){Error(response,HttpStatusCode.BadRequest,"invalid_file_name","file_name ist erforderlich.");return;}
                NexNodeSnapshot node=m_Nodes.FindNodeForRegion(region);if(node==null||!node.State.Equals("online",StringComparison.OrdinalIgnoreCase)){Error(response,HttpStatusCode.ServiceUnavailable,"region_node_unavailable","Simulator der Region ist nicht erreichbar.");return;}
                string id=Guid.NewGuid().ToString(); var job=m_Jobs.RegisterOperation("archive.oar."+action,id,"world-api",id,new Dictionary<string,string>{{"region_id",region},{"node_id",node.NodeId},{"action",action},{"file_name",file},{"dry_run",dry.ToString()}});
                m_Bus.Publish(new NexEvent("archive.oar.requested","opengenesislink.world-api",new Dictionary<string,string>{{"operation_id",id},{"target_node_id",node.NodeId},{"region_id",region},{"action",action},{"file_name",file},{"dry_run",dry.ToString()}},id)); Json(response,HttpStatusCode.Accepted,job);
            }
        }
        private bool Authenticate(IOSHttpRequest q,IOSHttpResponse r){if(m_Authenticator.TryAuthenticate(q,NexScopes.RegionsManage,out NexPrincipal _,out UserAccount _,out int s,out string e))return true;r.AddHeader("WWW-Authenticate","Bearer");Error(r,(HttpStatusCode)s,e,"Authentifizierung oder Berechtigung fehlgeschlagen.");return false;}
        private static string Str(JsonElement r,string n)=>r.TryGetProperty(n,out JsonElement v)&&v.ValueKind==JsonValueKind.String?v.GetString()?.Trim()??"":""; private static void Json(IOSHttpResponse r,HttpStatusCode s,object p){r.StatusCode=(int)s;r.ContentType="application/json; charset=utf-8";r.RawBuffer=JsonSerializer.SerializeToUtf8Bytes(p,new JsonSerializerOptions{WriteIndented=true});} private static void Error(IOSHttpResponse r,HttpStatusCode s,string e,string m)=>Json(r,s,new{error=e,message=m});
    }

    internal static class JobStoreAccessor
    {
        public static OglJobSnapshot Get(OglJobEventBridge bridge,string id)
        {
            var field=typeof(OglJobEventBridge).GetField("m_Jobs",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
            return (field?.GetValue(bridge) as IOglJobStore)?.Get(id);
        }
    }
}
