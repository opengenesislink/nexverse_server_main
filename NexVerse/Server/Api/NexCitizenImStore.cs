// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;
using System.IO;
using Mono.Data.Sqlite;

namespace NexVerse.Server.Api
{
    /// <summary>
    /// Separate, opt-in portal transcript store. This is NOT the OpenSim offline-IM
    /// database. Each message is recorded only for opted-in local participants.
    /// </summary>
    internal sealed class NexCitizenImStore
    {
        private readonly object m_Sync = new object();
        private readonly string m_ConnectionString;
        private readonly int m_RetentionDays;
        private const int MaxEntriesPerOwner = 10000;

        internal NexCitizenImStore(string path, int retentionDays)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A dedicated portal IM database path is required.", nameof(path));
            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            m_ConnectionString = "Data Source=" + full + ";Version=3;Pooling=False;";
            m_RetentionDays = Math.Clamp(retentionDays, 7, 365);
            lock (m_Sync)
            {
                using SqliteConnection db = Open();
                Execute(db, @"CREATE TABLE IF NOT EXISTS portal_im_settings (
                    owner TEXT PRIMARY KEY NOT NULL,
                    enabled INTEGER NOT NULL,
                    updated_at INTEGER NOT NULL
                )");
                Execute(db, @"CREATE TABLE IF NOT EXISTS portal_im_entries (
                    seq INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_id TEXT NOT NULL,
                    owner TEXT NOT NULL,
                    from_agent TEXT NOT NULL,
                    to_agent TEXT NOT NULL,
                    peer TEXT NOT NULL,
                    sender_name TEXT NOT NULL,
                    body TEXT NOT NULL,
                    source TEXT NOT NULL,
                    accepted_at INTEGER NOT NULL,
                    UNIQUE(owner, event_id)
                )");
                Execute(db, "CREATE INDEX IF NOT EXISTS ix_portal_im_owner_seq ON portal_im_entries(owner,seq)");
                Execute(db, "CREATE INDEX IF NOT EXISTS ix_portal_im_owner_peer_seq ON portal_im_entries(owner,peer,seq)");
                Execute(db, "CREATE INDEX IF NOT EXISTS ix_portal_im_expiry ON portal_im_entries(accepted_at)");
            }
        }

        private SqliteConnection Open()
        {
            var db = new SqliteConnection(m_ConnectionString);
            db.Open();
            using var pragma = db.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout=3000";
            pragma.ExecuteNonQuery();
            return db;
        }

        private static void Execute(SqliteConnection db, string sql)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private static void Add(SqliteCommand cmd, string name, object value)
            => cmd.Parameters.AddWithValue(name, value);

        internal bool IsEnabled(Guid owner)
        {
            lock (m_Sync)
            {
                using var db = Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT enabled FROM portal_im_settings WHERE owner=@owner";
                Add(cmd, "@owner", owner.ToString("D"));
                return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) == 1;
            }
        }

        internal void SetEnabled(Guid owner, bool enabled)
        {
            lock (m_Sync)
            {
                using var db = Open();
                using var transaction = db.BeginTransaction();
                using (var cmd = db.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"INSERT INTO portal_im_settings(owner,enabled,updated_at)
                        VALUES(@owner,@enabled,@now)
                        ON CONFLICT(owner) DO UPDATE SET enabled=excluded.enabled,updated_at=excluded.updated_at";
                    Add(cmd, "@owner", owner.ToString("D"));
                    Add(cmd, "@enabled", enabled ? 1 : 0);
                    Add(cmd, "@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    cmd.ExecuteNonQuery();
                }
                if (!enabled)
                    DeleteOwner(db, transaction, owner);
                transaction.Commit();
            }
        }

        private static void DeleteOwner(SqliteConnection db, SqliteTransaction tx, Guid owner)
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM portal_im_entries WHERE owner=@owner";
            Add(cmd, "@owner", owner.ToString("D"));
            cmd.ExecuteNonQuery();
        }

        internal void DeleteHistory(Guid owner)
        {
            lock (m_Sync)
            {
                using var db = Open();
                using var tx = db.BeginTransaction();
                DeleteOwner(db, tx, owner);
                tx.Commit();
            }
        }

        internal bool Append(
            Guid eventId, Guid sender, Guid receiver, string senderName,
            string message, string source)
        {
            if (eventId == Guid.Empty || sender == Guid.Empty || receiver == Guid.Empty ||
                sender == receiver || string.IsNullOrWhiteSpace(message))
                return false;

            lock (m_Sync)
            {
                using var db = Open();
                using var tx = db.BeginTransaction();
                var participants = new[] { sender, receiver };
                bool written = false;
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                foreach (Guid owner in participants)
                {
                    using var cmd = db.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT OR IGNORE INTO portal_im_entries
                        (event_id,owner,from_agent,to_agent,peer,sender_name,body,source,accepted_at)
                        SELECT @event,@owner,@from,@to,@peer,@name,@body,@source,@now
                        WHERE EXISTS (SELECT 1 FROM portal_im_settings
                                      WHERE owner=@owner AND enabled=1)";
                    Add(cmd, "@event", eventId.ToString("D"));
                    Add(cmd, "@owner", owner.ToString("D"));
                    Add(cmd, "@from", sender.ToString("D"));
                    Add(cmd, "@to", receiver.ToString("D"));
                    Add(cmd, "@peer", (owner == sender ? receiver : sender).ToString("D"));
                    Add(cmd, "@name", (senderName ?? string.Empty).Length > 160
                        ? senderName.Substring(0,160) : senderName ?? string.Empty);
                    Add(cmd, "@body", message);
                    Add(cmd, "@source", source);
                    Add(cmd, "@now", now);
                    written |= cmd.ExecuteNonQuery() != 0;
                }
                using (var expiry = db.CreateCommand())
                {
                    expiry.Transaction = tx;
                    expiry.CommandText = "DELETE FROM portal_im_entries WHERE accepted_at < @cutoff";
                    Add(expiry, "@cutoff", now - m_RetentionDays * 86400L);
                    expiry.ExecuteNonQuery();
                }
                foreach (Guid owner in participants)
                {
                    using var limit = db.CreateCommand();
                    limit.Transaction = tx;
                    limit.CommandText = @"DELETE FROM portal_im_entries WHERE owner=@owner
                        AND seq NOT IN (SELECT seq FROM portal_im_entries
                                        WHERE owner=@owner ORDER BY seq DESC LIMIT @limit)";
                    Add(limit, "@owner", owner.ToString("D"));
                    Add(limit, "@limit", MaxEntriesPerOwner);
                    limit.ExecuteNonQuery();
                }
                tx.Commit();
                return written;
            }
        }

        internal List<NexCitizenImEntry> Read(Guid owner, long after, int limit, Guid? peer = null)
        {
            List<NexCitizenImEntry> entries = new List<NexCitizenImEntry>();
            lock (m_Sync)
            {
                using var db = Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"SELECT seq,event_id,from_agent,to_agent,peer,sender_name,
                    body,source,accepted_at FROM portal_im_entries
                    WHERE owner=@owner AND seq>@after" +
                    (peer.HasValue ? " AND peer=@peer" : "") + " ORDER BY seq ASC LIMIT @limit";
                Add(cmd, "@owner", owner.ToString("D"));
                Add(cmd, "@after", Math.Max(0,after));
                Add(cmd, "@limit", Math.Clamp(limit,1,100));
                if (peer.HasValue) Add(cmd, "@peer", peer.Value.ToString("D"));
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    entries.Add(new NexCitizenImEntry
                    {
                        seq = reader.GetInt64(0),
                        event_id = reader.GetString(1),
                        from_agent_id = reader.GetString(2),
                        to_agent_id = reader.GetString(3),
                        peer_id = reader.GetString(4),
                        sender_name = reader.GetString(5),
                        message = reader.GetString(6),
                        source = reader.GetString(7),
                        accepted_at = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(8))
                    });
            }
            return entries;
        }

        internal List<object> Conversations(Guid owner, int limit)
        {
            List<object> entries = new List<object>();
            lock (m_Sync)
            {
                using var db = Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"SELECT e.peer,e.seq,e.accepted_at FROM portal_im_entries e
                    JOIN (SELECT peer,MAX(seq) seq FROM portal_im_entries
                          WHERE owner=@owner GROUP BY peer) latest ON latest.seq=e.seq
                    WHERE e.owner=@owner ORDER BY e.seq DESC LIMIT @limit";
                Add(cmd, "@owner", owner.ToString("D"));
                Add(cmd, "@limit", Math.Clamp(limit,1,100));
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    entries.Add(new
                    {
                        peer_id = reader.GetString(0),
                        last_seq = reader.GetInt64(1),
                        last_message_at = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2))
                    });
            }
            return entries;
        }
    }

    internal sealed class NexCitizenImEntry
    {
        public long seq { get; set; }
        public string event_id { get; set; }
        public string from_agent_id { get; set; }
        public string to_agent_id { get; set; }
        public string peer_id { get; set; }
        public string sender_name { get; set; }
        public string message { get; set; }
        public string source { get; set; }
        public DateTimeOffset accepted_at { get; set; }
    }
}
