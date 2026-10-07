// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using log4net;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace NexVerse.Server.Api
{
    internal sealed class NexOfflineImMailRelay : IDisposable
    {
        private static readonly ILog m_Log = LogManager.GetLogger(typeof(NexOfflineImMailRelay));

        private readonly IUserAccountService m_UserAccounts;
        private readonly IInstantMessage m_InstantMessages;
        private readonly string m_RelayDomain;
        private readonly string m_SigningKey;
        private readonly string m_ImapHost;
        private readonly int m_ImapPort;
        private readonly SecureSocketOptions m_ImapSocketOptions;
        private readonly string m_ImapUsername;
        private readonly string m_ImapPassword;
        private readonly int m_PollSeconds;
        private readonly int m_BatchSize;
        private readonly int m_MaximumReplyChars;
        private readonly TimeSpan m_MaximumTokenLifetime;
        private readonly CancellationTokenSource m_Stop = new CancellationTokenSource();
        private Thread m_Thread;

        public NexOfflineImMailRelay(
            IConfig config,
            IUserAccountService userAccounts,
            IInstantMessage instantMessages)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            m_UserAccounts = userAccounts ?? throw new ArgumentNullException(nameof(userAccounts));
            m_InstantMessages = instantMessages ?? throw new ArgumentNullException(nameof(instantMessages));

            m_RelayDomain = config.GetString("RelayDomain", "im.stadt-nexverse.de").Trim().Trim('.').ToLowerInvariant();
            m_SigningKey = config.GetString("RelaySigningKey", string.Empty);
            m_ImapHost = config.GetString("IMAPHost", string.Empty).Trim();
            m_ImapPort = config.GetInt("IMAPPort", 993);
            m_ImapUsername = config.GetString("IMAPUsername", "relay@im.stadt-nexverse.de").Trim();
            m_ImapPassword = config.GetString("IMAPPassword", string.Empty);
            m_PollSeconds = Math.Max(5, config.GetInt("PollSeconds", 20));
            m_BatchSize = Math.Max(1, Math.Min(100, config.GetInt("BatchSize", 25)));
            m_MaximumReplyChars = Math.Max(128, Math.Min(4096, config.GetInt("MaximumReplyCharacters", 1024)));
            int maximumReplyDays = Math.Max(1, Math.Min(30, config.GetInt("MaximumReplyLifetimeDays", 5)));
            m_MaximumTokenLifetime = TimeSpan.FromDays(maximumReplyDays);

            bool sslOnConnect = config.GetBoolean("UseSslOnConnect", true);
            bool startTls = config.GetBoolean("UseStartTls", false);
            if (sslOnConnect && startTls)
                throw new InvalidOperationException("Offline IM mail relay cannot enable both IMAP SSL-on-connect and STARTTLS.");

            m_ImapSocketOptions =
                sslOnConnect
                    ? SecureSocketOptions.SslOnConnect
                    : startTls
                        ? SecureSocketOptions.StartTls
                        : SecureSocketOptions.None;

            if (string.IsNullOrWhiteSpace(m_ImapHost))
                throw new InvalidOperationException("Offline IM mail relay requires IMAPHost.");

            if (m_ImapPort < 1 || m_ImapPort > 65535)
                throw new InvalidOperationException("Offline IM mail relay IMAPPort is invalid.");

            if (string.IsNullOrWhiteSpace(m_ImapUsername) || string.IsNullOrEmpty(m_ImapPassword))
                throw new InvalidOperationException("Offline IM mail relay requires IMAPUsername and IMAPPassword.");

            if (!OfflineImMailRelayToken.IsSigningKeyStrongEnough(m_SigningKey))
                throw new InvalidOperationException("Offline IM mail relay requires RelaySigningKey with at least 32 UTF-8 bytes.");
        }

        public void Start()
        {
            if (m_Thread != null)
                return;

            m_Thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "NexVerse Offline IM Mail Relay"
            };
            m_Thread.Start();

            m_Log.InfoFormat(
                "[NEX-IM-MAIL-RELAY]: Started for catch-all mailbox {0} on {1}:{2}.",
                m_ImapUsername,
                m_ImapHost,
                m_ImapPort);
        }

        public void Dispose()
        {
            m_Stop.Cancel();

            try
            {
                if (m_Thread != null && m_Thread.IsAlive)
                    m_Thread.Join(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }

            m_Stop.Dispose();
        }

        private void Run()
        {
            while (!m_Stop.IsCancellationRequested)
            {
                try
                {
                    PollMailbox();
                }
                catch (Exception e)
                {
                    m_Log.WarnFormat("[NEX-IM-MAIL-RELAY]: IMAP poll failed: {0}", e.Message);
                }

                if (m_Stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(m_PollSeconds)))
                    break;
            }
        }

        private void PollMailbox()
        {
            using ImapClient client = new ImapClient();
            client.Connect(m_ImapHost, m_ImapPort, m_ImapSocketOptions, m_Stop.Token);
            client.Authenticate(m_ImapUsername, m_ImapPassword, m_Stop.Token);

            IMailFolder inbox = client.Inbox;
            inbox.Open(FolderAccess.ReadWrite, m_Stop.Token);

            IList<UniqueId> unseen = inbox.Search(SearchQuery.NotSeen, m_Stop.Token);
            int processed = 0;

            foreach (UniqueId uid in unseen)
            {
                if (processed >= m_BatchSize || m_Stop.IsCancellationRequested)
                    break;

                MimeMessage message = inbox.GetMessage(uid, m_Stop.Token);
                RelayDisposition disposition = ProcessMessage(message);

                if (disposition != RelayDisposition.Retry)
                    inbox.AddFlags(uid, MessageFlags.Seen, true, m_Stop.Token);

                processed++;
            }

            client.Disconnect(true, m_Stop.Token);
        }

        private RelayDisposition ProcessMessage(MimeMessage message)
        {
            if (message == null)
                return RelayDisposition.Consumed;

            string autoSubmitted = message.Headers["Auto-Submitted"];
            if (!string.IsNullOrWhiteSpace(autoSubmitted) &&
                !string.Equals(autoSubmitted.Trim(), "no", StringComparison.OrdinalIgnoreCase))
            {
                m_Log.Info("[NEX-IM-MAIL-RELAY]: Ignored automated/bounce message.");
                return RelayDisposition.Consumed;
            }

            MailboxAddress sender = message.From?.Mailboxes?.FirstOrDefault();
            if (sender == null || string.IsNullOrWhiteSpace(sender.Address))
            {
                m_Log.Warn("[NEX-IM-MAIL-RELAY]: Rejected reply without sender address.");
                return RelayDisposition.Consumed;
            }

            string senderEmail = OfflineImMailRelayToken.NormalizeEmail(sender.Address);
            UserAccount replyingAccount = m_UserAccounts.GetUserAccount(UUID.Zero, senderEmail);

            if (replyingAccount == null ||
                !replyingAccount.LocalToGrid ||
                !replyingAccount.Active ||
                replyingAccount.PrincipalID.IsZero())
            {
                m_Log.WarnFormat(
                    "[NEX-IM-MAIL-RELAY]: Rejected reply from email that is not an active local NexVerse account: {0}.",
                    senderEmail);
                return RelayDisposition.Consumed;
            }

            if (!TryFindReplyLocalPart(message, out string replyLocalPart))
            {
                m_Log.Warn("[NEX-IM-MAIL-RELAY]: Rejected catch-all mail without a NexVerse reply token.");
                return RelayDisposition.Consumed;
            }

            if (!OfflineImMailRelayToken.TryValidate(
                    replyLocalPart,
                    senderEmail,
                    m_SigningKey,
                    DateTimeOffset.UtcNow,
                    m_MaximumTokenLifetime,
                    out UUID targetAgentId,
                    out _))
            {
                m_Log.WarnFormat(
                    "[NEX-IM-MAIL-RELAY]: Rejected invalid or expired reply token from account {0}.",
                    replyingAccount.PrincipalID);
                return RelayDisposition.Consumed;
            }

            UserAccount target = m_UserAccounts.GetUserAccount(UUID.Zero, targetAgentId);
            if (target == null || !target.LocalToGrid || !target.Active)
            {
                m_Log.WarnFormat(
                    "[NEX-IM-MAIL-RELAY]: Reply target {0} is not an active local NexVerse account.",
                    targetAgentId);
                return RelayDisposition.Consumed;
            }

            string replyText = ExtractReplyText(message.TextBody);
            if (string.IsNullOrWhiteSpace(replyText))
            {
                m_Log.InfoFormat(
                    "[NEX-IM-MAIL-RELAY]: Empty reply from {0} ignored.",
                    replyingAccount.PrincipalID);
                return RelayDisposition.Consumed;
            }

            if (replyText.Length > m_MaximumReplyChars)
                replyText = replyText.Substring(0, m_MaximumReplyChars);

            string senderName = string.IsNullOrWhiteSpace(replyingAccount.EffectiveDisplayName)
                ? replyingAccount.Name
                : replyingAccount.EffectiveDisplayName;

            GridInstantMessage im =
                new GridInstantMessage(
                    null,
                    replyingAccount.PrincipalID,
                    senderName,
                    targetAgentId,
                    (byte)InstantMessageDialog.MessageFromAgent,
                    replyText,
                    false,
                    Vector3.Zero);

            bool delivered;
            try
            {
                delivered = m_InstantMessages.IncomingInstantMessage(im);
            }
            catch (Exception e)
            {
                m_Log.WarnFormat(
                    "[NEX-IM-MAIL-RELAY]: Inworld delivery raised an error for {0} -> {1}: {2}",
                    replyingAccount.PrincipalID,
                    targetAgentId,
                    e.Message);
                return RelayDisposition.Retry;
            }

            if (!delivered)
            {
                m_Log.WarnFormat(
                    "[NEX-IM-MAIL-RELAY]: Inworld delivery was not accepted for {0} -> {1}; message will be retried.",
                    replyingAccount.PrincipalID,
                    targetAgentId);
                return RelayDisposition.Retry;
            }

            m_Log.InfoFormat(
                "[NEX-IM-MAIL-RELAY]: Delivered email reply as Inworld-IM from {0} to {1}.",
                replyingAccount.PrincipalID,
                targetAgentId);

            return RelayDisposition.Consumed;
        }

        private bool TryFindReplyLocalPart(MimeMessage message, out string localPart)
        {
            localPart = null;

            foreach (MailboxAddress mailbox in message.To.Mailboxes)
            {
                if (TryMatchRelayAddress(mailbox.Address, out localPart))
                    return true;
            }

            foreach (string headerName in new[] { "X-Original-To", "Delivered-To", "Envelope-To" })
            {
                string header = message.Headers[headerName];
                if (string.IsNullOrWhiteSpace(header))
                    continue;

                if (MailboxAddress.TryParse(header, out MailboxAddress mailbox) &&
                    TryMatchRelayAddress(mailbox.Address, out localPart))
                    return true;

                string trimmed = header.Trim().Trim('<', '>');
                if (TryMatchRelayAddress(trimmed, out localPart))
                    return true;
            }

            return false;
        }

        private bool TryMatchRelayAddress(string address, out string localPart)
        {
            localPart = null;
            if (string.IsNullOrWhiteSpace(address))
                return false;

            int at = address.LastIndexOf('@');
            if (at <= 0 || at >= address.Length - 1)
                return false;

            string domain = address.Substring(at + 1).Trim().TrimEnd('.').ToLowerInvariant();
            if (!string.Equals(domain, m_RelayDomain, StringComparison.Ordinal))
                return false;

            string candidate = address.Substring(0, at);
            if (!candidate.StartsWith("r-", StringComparison.Ordinal))
                return false;

            localPart = candidate;
            return true;
        }

        private static string ExtractReplyText(string textBody)
        {
            if (string.IsNullOrWhiteSpace(textBody))
                return string.Empty;

            string normalized = textBody.Replace("\r\n", "\n").Replace('\r', '\n');
            List<string> lines = new List<string>();

            foreach (string rawLine in normalized.Split('\n'))
            {
                string line = rawLine.TrimEnd();
                string trimmed = line.Trim();

                if (trimmed.StartsWith(">", StringComparison.Ordinal) ||
                    trimmed.StartsWith("-----Original Message-----", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("-----Urspruengliche Nachricht-----", StringComparison.OrdinalIgnoreCase) ||
                    (trimmed.StartsWith("On ", StringComparison.OrdinalIgnoreCase) &&
                     trimmed.EndsWith("wrote:", StringComparison.OrdinalIgnoreCase)) ||
                    (trimmed.StartsWith("Am ", StringComparison.OrdinalIgnoreCase) &&
                     trimmed.EndsWith("schrieb", StringComparison.OrdinalIgnoreCase)))
                {
                    break;
                }

                lines.Add(line);
            }

            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[lines.Count - 1]))
                lines.RemoveAt(lines.Count - 1);

            return string.Join("\n", lines).Trim();
        }

        private enum RelayDisposition
        {
            Consumed,
            Retry
        }
    }
}
