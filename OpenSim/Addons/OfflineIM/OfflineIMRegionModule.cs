/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using log4net;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Client;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace OpenSim.OfflineIM
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "OfflineIMConnectorModule")]
    public class OfflineIMRegionModule : ISharedRegionModule, IOfflineIMService
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private bool m_Enabled = false;
        private List<Scene> m_SceneList = new List<Scene>();
        IMessageTransferModule m_TransferModule = null;
        private bool m_ForwardOfflineGroupMessages = true;

        private bool m_EmailEnabled;
        private string m_EmailRelayDomain = "im.stadt-nexverse.de";
        private string m_EmailSubjectTemplate = "Offline-IM Nachricht von {SENDER}";
        private string m_EmailSmtpHost = "127.0.0.1";
        private int m_EmailSmtpPort = 25;
        private bool m_EmailUseStartTls;
        private bool m_EmailUseSslOnConnect;
        private string m_EmailSmtpUsername = string.Empty;
        private string m_EmailSmtpPassword = string.Empty;
        private int m_EmailPerSenderPerHour = 30;
        private int m_EmailPerRecipientPerHour = 60;
        private int m_EmailGlobalPerHour = 500;

        private readonly object m_EmailRateLock = new object();
        private readonly Dictionary<UUID, Queue<double>> m_EmailSenderRate = new Dictionary<UUID, Queue<double>>();
        private readonly Dictionary<UUID, Queue<double>> m_EmailRecipientRate = new Dictionary<UUID, Queue<double>>();
        private readonly Queue<double> m_EmailGlobalRate = new Queue<double>();

        private IOfflineIMService m_OfflineIMService;

        public void Initialise(IConfigSource config)
        {
            IConfig cnf = config.Configs["Messaging"];
            if (cnf == null)
                return;
            if (cnf != null && cnf.GetString("OfflineMessageModule", string.Empty) != Name)
                return;

            m_Enabled = true;

            string serviceLocation = cnf.GetString("OfflineMessageURL", string.Empty);
            if (serviceLocation.Length == 0)
                m_OfflineIMService = new OfflineIMService(config);
            else
                m_OfflineIMService = new OfflineIMServiceRemoteConnector(config);

            m_ForwardOfflineGroupMessages = cnf.GetBoolean("ForwardOfflineGroupMessages", m_ForwardOfflineGroupMessages);

            IConfig emailConfig = config.Configs["OfflineIMEmail"];
            if (emailConfig is not null)
            {
                m_EmailEnabled = emailConfig.GetBoolean("Enabled", false);
                m_EmailRelayDomain = emailConfig.GetString("RelayDomain", m_EmailRelayDomain).Trim().Trim('.');
                m_EmailSubjectTemplate = emailConfig.GetString("SubjectTemplate", m_EmailSubjectTemplate);
                m_EmailSmtpHost = emailConfig.GetString("SMTPHost", m_EmailSmtpHost).Trim();
                m_EmailSmtpPort = emailConfig.GetInt("SMTPPort", m_EmailSmtpPort);
                m_EmailUseStartTls = emailConfig.GetBoolean("UseStartTls", false);
                m_EmailUseSslOnConnect = emailConfig.GetBoolean("UseSslOnConnect", false);
                m_EmailSmtpUsername = emailConfig.GetString("SMTPUsername", string.Empty).Trim();
                m_EmailSmtpPassword = emailConfig.GetString("SMTPPassword", string.Empty);
                m_EmailPerSenderPerHour = Math.Max(1, emailConfig.GetInt("PerSenderPerHour", m_EmailPerSenderPerHour));
                m_EmailPerRecipientPerHour = Math.Max(1, emailConfig.GetInt("PerRecipientPerHour", m_EmailPerRecipientPerHour));
                m_EmailGlobalPerHour = Math.Max(1, emailConfig.GetInt("GlobalPerHour", m_EmailGlobalPerHour));

                if (m_EmailUseStartTls && m_EmailUseSslOnConnect)
                {
                    m_log.Warn("[OfflineIM.V2.EMAIL]: UseStartTls and UseSslOnConnect cannot both be true. Offline IM email disabled.");
                    m_EmailEnabled = false;
                }

                if (m_EmailEnabled &&
                    (string.IsNullOrWhiteSpace(m_EmailSmtpHost) ||
                     m_EmailSmtpPort < 1 ||
                     m_EmailSmtpPort > 65535 ||
                     !IsValidRelayDomain(m_EmailRelayDomain)))
                {
                    m_log.Warn("[OfflineIM.V2.EMAIL]: Invalid SMTP host/port or RelayDomain. Offline IM email disabled.");
                    m_EmailEnabled = false;
                }

                if (m_EmailEnabled && !string.IsNullOrEmpty(m_EmailSmtpUsername) && string.IsNullOrEmpty(m_EmailSmtpPassword))
                {
                    m_log.Warn("[OfflineIM.V2.EMAIL]: SMTPUsername is configured but SMTPPassword is empty. Delivery may fail.");
                }
            }

            m_log.DebugFormat("[OfflineIM.V2]: Offline messages enabled by {0}", Name);
            if (m_EmailEnabled)
                m_log.InfoFormat("[OfflineIM.V2.EMAIL]: Offline IM email notification enabled via {0}:{1} using avatar relay domain {2}.", m_EmailSmtpHost, m_EmailSmtpPort, m_EmailRelayDomain);
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            scene.RegisterModuleInterface<IOfflineIMService>(this);
            lock (m_SceneList)
                m_SceneList.Add(scene);
            scene.EventManager.OnNewClient += OnNewClient;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled)
                return;

            if (m_TransferModule == null)
            {
                m_TransferModule = scene.RequestModuleInterface<IMessageTransferModule>();
                if (m_TransferModule == null)
                {
                    scene.EventManager.OnNewClient -= OnNewClient;

                    lock (m_SceneList)
                        m_SceneList.Clear();

                    m_log.Error("[OfflineIM.V2]: No message transfer module is enabled. Disabling offline messages");
                }
                m_TransferModule.OnUndeliveredMessage += UndeliveredMessage;
            }
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            lock (m_SceneList)
                m_SceneList.Remove(scene);
            scene.EventManager.OnNewClient -= OnNewClient;
            m_TransferModule.OnUndeliveredMessage -= UndeliveredMessage;

            scene.ForEachClient(delegate(IClientAPI client)
            {
                client.OnRetrieveInstantMessages -= RetrieveInstantMessages;
            });
        }

        public void PostInitialise()
        {
        }

        public string Name
        {
            get { return "Offline Message Module V2"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public void Close()
        {
            lock (m_SceneList)
                m_SceneList.Clear();
        }

        private Scene[] SnapshotScenes()
        {
            lock (m_SceneList)
                return m_SceneList.ToArray();
        }

        private Scene FindScene(UUID agentID)
        {
            foreach (Scene s in SnapshotScenes())
            {
                ScenePresence presence = s.GetScenePresence(agentID);
                if (presence != null && !presence.IsChildAgent)
                    return s;
            }
            return null;
        }

        private IClientAPI FindClient(UUID agentID)
        {
            foreach (Scene s in SnapshotScenes())
            {
                ScenePresence presence = s.GetScenePresence(agentID);
                if (presence != null && !presence.IsChildAgent)
                    return presence.ControllingClient;
            }
            return null;
        }

        private void OnNewClient(IClientAPI client)
        {
            client.OnRetrieveInstantMessages += RetrieveInstantMessages;
        }

        private void RetrieveInstantMessages(IClientAPI client)
        {
            m_log.DebugFormat("[OfflineIM.V2]: Retrieving stored messages for {0}", client.AgentId);

            List<GridInstantMessage> msglist = m_OfflineIMService.GetMessages(client.AgentId);

            if (msglist == null)
                m_log.DebugFormat("[OfflineIM.V2]: WARNING null message list.");

            foreach (GridInstantMessage im in msglist)
            {
                if (im.dialog == (byte)InstantMessageDialog.InventoryOffered)
                    // send it directly or else the item will be given twice
                    client.SendInstantMessage(im);
                else
                {
                    // Send through scene event manager so all modules get a chance
                    // to look at this message before it gets delivered.
                    //
                    // Needed for proper state management for stored group
                    // invitations
                    //
                    Scene s = client.Scene as Scene;
                    if (s != null)
                    {
                        im.offline = 1;
                        s.EventManager.TriggerIncomingInstantMessage(im);
                    }
                }
            }
        }

        private void UndeliveredMessage(GridInstantMessage im)
        {
            if (im.dialog != (byte)InstantMessageDialog.MessageFromObject &&
                im.dialog != (byte)InstantMessageDialog.MessageFromAgent &&
                im.dialog != (byte)InstantMessageDialog.GroupNotice &&
                im.dialog != (byte)InstantMessageDialog.GroupInvitation &&
                im.dialog != (byte)InstantMessageDialog.InventoryOffered)
            {
                return;
            }

            if (!m_ForwardOfflineGroupMessages)
            {
                if (im.dialog == (byte)InstantMessageDialog.GroupNotice ||
                    im.dialog == (byte)InstantMessageDialog.GroupInvitation)
                    return;
            }

            string reason = string.Empty;
            bool success = m_OfflineIMService.StoreMessage(im, out reason);

            if (success &&
                m_EmailEnabled &&
                im.dialog == (byte)InstantMessageDialog.MessageFromAgent &&
                !im.fromGroup)
            {
                QueueOfflineImEmail(im);
            }

            if (im.dialog == (byte)InstantMessageDialog.MessageFromAgent)
            {
                IClientAPI client = FindClient(new UUID(im.fromAgentID));
                if (client == null)
                    return;

                client.SendInstantMessage(new GridInstantMessage(
                        null, new UUID(im.toAgentID),
                        "System", new UUID(im.fromAgentID),
                        (byte)InstantMessageDialog.MessageFromAgent,
                        "User is not logged in. " +
                        (success ? "Message saved." : "Message not saved: " + reason),
                        false, new Vector3()));
            }
        }

        private void QueueOfflineImEmail(GridInstantMessage im)
        {
            UUID senderID = new UUID(im.fromAgentID);
            UUID recipientID = new UUID(im.toAgentID);

            if (!ConsumeEmailRate(senderID, recipientID))
            {
                m_log.WarnFormat(
                    "[OfflineIM.V2.EMAIL]: Rate limit suppressed offline IM email from {0} to {1}.",
                    senderID,
                    recipientID);
                return;
            }

            GridInstantMessage snapshot = new GridInstantMessage(im, false);
            ThreadPool.QueueUserWorkItem(_ => SendOfflineImEmail(snapshot));
        }

        private bool ConsumeEmailRate(UUID senderID, UUID recipientID)
        {
            double now = Util.GetTimeStamp();
            double cutoff = now - 3600.0;

            lock (m_EmailRateLock)
            {
                TrimRateQueue(m_EmailGlobalRate, cutoff);

                if (!m_EmailSenderRate.TryGetValue(senderID, out Queue<double> senderQueue))
                {
                    senderQueue = new Queue<double>();
                    m_EmailSenderRate[senderID] = senderQueue;
                }

                if (!m_EmailRecipientRate.TryGetValue(recipientID, out Queue<double> recipientQueue))
                {
                    recipientQueue = new Queue<double>();
                    m_EmailRecipientRate[recipientID] = recipientQueue;
                }

                TrimRateQueue(senderQueue, cutoff);
                TrimRateQueue(recipientQueue, cutoff);

                if (m_EmailGlobalRate.Count >= m_EmailGlobalPerHour ||
                    senderQueue.Count >= m_EmailPerSenderPerHour ||
                    recipientQueue.Count >= m_EmailPerRecipientPerHour)
                {
                    return false;
                }

                m_EmailGlobalRate.Enqueue(now);
                senderQueue.Enqueue(now);
                recipientQueue.Enqueue(now);
                return true;
            }
        }

        private static void TrimRateQueue(Queue<double> queue, double cutoff)
        {
            while (queue.Count > 0 && queue.Peek() < cutoff)
                queue.Dequeue();
        }

        private UserAccount FindLocalAccount(UUID principalID)
        {
            foreach (Scene scene in SnapshotScenes())
            {
                try
                {
                    IUserAccountService accounts = scene?.UserAccountService;
                    if (accounts is null)
                        continue;

                    UserAccount account = accounts.GetUserAccount(scene.RegionInfo.ScopeID, principalID)
                                          ?? accounts.GetUserAccount(UUID.Zero, principalID);

                    if (account is not null && account.LocalToGrid && account.PrincipalID == principalID)
                        return account;
                }
                catch (Exception e)
                {
                    m_log.DebugFormat(
                        "[OfflineIM.V2.EMAIL]: User account lookup failed for {0} on scene {1}: {2}",
                        principalID,
                        scene?.Name ?? "(unknown)",
                        e.Message);
                }
            }

            return null;
        }

        private void SendOfflineImEmail(GridInstantMessage im)
        {
            UUID recipientID = new UUID(im.toAgentID);
            UserAccount recipient = FindLocalAccount(recipientID);

            if (recipient is null || string.IsNullOrWhiteSpace(recipient.Email))
                return;

            if (!MailboxAddress.TryParse(recipient.Email.Trim(), out MailboxAddress recipientAddress))
            {
                m_log.WarnFormat(
                    "[OfflineIM.V2.EMAIL]: Invalid email address for local account {0}; notification skipped.",
                    recipientID);
                return;
            }

            string senderName = SanitizeHeaderValue(im.fromAgentName);
            if (string.IsNullOrWhiteSpace(senderName))
                senderName = new UUID(im.fromAgentID).ToString();

            string subject = SanitizeHeaderValue(
                (m_EmailSubjectTemplate ?? "Offline-IM Nachricht von {SENDER}")
                    .Replace("{SENDER}", senderName, StringComparison.Ordinal));

            string body = BuildOfflineImEmailBody(recipient, senderName, im);

            try
            {
                string relayEmail = new UUID(im.fromAgentID).ToString() + "@" + m_EmailRelayDomain;
                MailboxAddress relayAddress = new MailboxAddress(senderName, relayEmail);

                MimeMessage message = new MimeMessage();
                message.From.Add(relayAddress);
                message.ReplyTo.Add(relayAddress);
                message.To.Add(new MailboxAddress(recipient.Name, recipientAddress.Address));
                message.Subject = subject;
                message.Headers["X-NexVerse-IM-From-Agent"] = new UUID(im.fromAgentID).ToString();
                message.Headers["X-NexVerse-IM-To-Agent"] = recipientID.ToString();
                message.Body = new TextPart("plain")
                {
                    Text = body
                };

                using SmtpClient client = new SmtpClient();

                SecureSocketOptions socketOptions =
                    m_EmailUseSslOnConnect
                        ? SecureSocketOptions.SslOnConnect
                        : m_EmailUseStartTls
                            ? SecureSocketOptions.StartTls
                            : SecureSocketOptions.None;

                client.Connect(m_EmailSmtpHost, m_EmailSmtpPort, socketOptions);

                if (!string.IsNullOrWhiteSpace(m_EmailSmtpUsername))
                    client.Authenticate(m_EmailSmtpUsername, m_EmailSmtpPassword ?? string.Empty);

                client.Send(message);
                client.Disconnect(true);

                m_log.InfoFormat(
                    "[OfflineIM.V2.EMAIL]: Sent offline IM email to local account {0} from {1}.",
                    recipientID,
                    senderName);
            }
            catch (Exception e)
            {
                m_log.WarnFormat(
                    "[OfflineIM.V2.EMAIL]: SMTP delivery failed for local account {0}: {1}",
                    recipientID,
                    e.Message);
            }
        }

        private static string BuildOfflineImEmailBody(UserAccount recipient, string senderName, GridInstantMessage im)
        {
            StringBuilder body = new StringBuilder();
            body.AppendLine("Hallo " + (string.IsNullOrWhiteSpace(recipient.FirstName) ? "NexVerse Resident" : recipient.FirstName) + ",");
            body.AppendLine();
            body.AppendLine("du hast in NexVerse eine Nachricht erhalten, waehrend du offline warst.");
            body.AppendLine();
            body.AppendLine("Absender: " + senderName);

            if (im.timestamp > 0)
            {
                DateTimeOffset sentAt = DateTimeOffset.FromUnixTimeSeconds(im.timestamp);
                body.AppendLine("Zeit: " + sentAt.UtcDateTime.ToString("dd.MM.yyyy HH:mm:ss") + " UTC");
            }

            body.AppendLine();
            body.AppendLine("Nachricht:");
            body.AppendLine(im.message ?? string.Empty);
            body.AppendLine();
            body.AppendLine("Diese Nachricht wurde automatisch von Stadt NexVerse versendet.");
            body.AppendLine("Bitte antworte nicht auf diese E-Mail.");

            return body.ToString();
        }

        private static bool IsValidRelayDomain(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 253)
                return false;

            string[] labels = value.Split('.');
            if (labels.Length < 2)
                return false;

            foreach (string label in labels)
            {
                if (string.IsNullOrEmpty(label) || label.Length > 63)
                    return false;

                if (label[0] == '-' || label[label.Length - 1] == '-')
                    return false;

                foreach (char ch in label)
                {
                    if (!(char.IsLetterOrDigit(ch) || ch == '-'))
                        return false;
                }
            }

            return true;
        }

        private static string SanitizeHeaderValue(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value.Replace("\r", " ", StringComparison.Ordinal)
                        .Replace("\n", " ", StringComparison.Ordinal)
                        .Trim();
        }

        #region IOfflineIM

        public List<GridInstantMessage> GetMessages(UUID principalID)
        {
            return m_OfflineIMService.GetMessages(principalID);
        }

        public bool StoreMessage(GridInstantMessage im, out string reason)
        {
            return m_OfflineIMService.StoreMessage(im, out reason);
        }

        public void DeleteMessages(UUID userID)
        {
            m_OfflineIMService.DeleteMessages(userID);
        }

        #endregion
    }
}

