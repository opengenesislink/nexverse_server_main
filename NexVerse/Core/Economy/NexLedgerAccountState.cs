// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;

namespace NexVerse.Core.Economy
{
    public enum NexLedgerAccountStatus
    {
        Active = 1,
        Locked = 2,
        Closed = 3
    }

    public sealed class NexLedgerAccountState
    {
        public const int ActorLengthLimit = 128;
        public const int ReasonLengthLimit = 255;

        public NexLedgerAccountState(
            Guid accountId,
            NexLedgerAccountStatus status,
            long version,
            DateTimeOffset changedAt,
            string changedBy,
            string reason)
        {
            if (accountId == Guid.Empty)
                throw new ArgumentException("Ledger account ID is required.", nameof(accountId));
            if (!Enum.IsDefined(typeof(NexLedgerAccountStatus), status))
                throw new ArgumentOutOfRangeException(nameof(status));
            if (version < 0)
                throw new ArgumentOutOfRangeException(nameof(version));

            AccountId = accountId;
            Status = status;
            Version = version;
            ChangedAt = changedAt;
            ChangedBy = Normalize(changedBy, ActorLengthLimit, nameof(changedBy));
            Reason = Normalize(reason, ReasonLengthLimit, nameof(reason));
        }

        public Guid AccountId { get; }
        public NexLedgerAccountStatus Status { get; }
        public long Version { get; }
        public DateTimeOffset ChangedAt { get; }
        public string ChangedBy { get; }
        public string Reason { get; }

        private static string Normalize(
            string value,
            int limit,
            string parameter)
        {
            string normalized =
                (value ?? string.Empty)
                    .Trim();

            if (normalized.Length > limit)
                throw new ArgumentOutOfRangeException(parameter);

            return normalized;
        }
    }

    public sealed class NexLedgerAccountStateEvent
    {
        public NexLedgerAccountStateEvent(
            Guid eventId,
            Guid accountId,
            NexLedgerAccountStatus previousStatus,
            NexLedgerAccountStatus newStatus,
            long version,
            DateTimeOffset occurredAt,
            string actor,
            string reason)
        {
            if (eventId == Guid.Empty)
                throw new ArgumentException("Ledger account event ID is required.", nameof(eventId));
            if (accountId == Guid.Empty)
                throw new ArgumentException("Ledger account ID is required.", nameof(accountId));
            if (!Enum.IsDefined(typeof(NexLedgerAccountStatus), previousStatus))
                throw new ArgumentOutOfRangeException(nameof(previousStatus));
            if (!Enum.IsDefined(typeof(NexLedgerAccountStatus), newStatus))
                throw new ArgumentOutOfRangeException(nameof(newStatus));
            if (version < 1)
                throw new ArgumentOutOfRangeException(nameof(version));

            EventId = eventId;
            AccountId = accountId;
            PreviousStatus = previousStatus;
            NewStatus = newStatus;
            Version = version;
            OccurredAt = occurredAt;
            Actor = Normalize(actor, NexLedgerAccountState.ActorLengthLimit, nameof(actor));
            Reason = Normalize(reason, NexLedgerAccountState.ReasonLengthLimit, nameof(reason));
        }

        public Guid EventId { get; }
        public Guid AccountId { get; }
        public NexLedgerAccountStatus PreviousStatus { get; }
        public NexLedgerAccountStatus NewStatus { get; }
        public long Version { get; }
        public DateTimeOffset OccurredAt { get; }
        public string Actor { get; }
        public string Reason { get; }

        private static string Normalize(
            string value,
            int limit,
            string parameter)
        {
            string normalized =
                (value ?? string.Empty)
                    .Trim();

            if (normalized.Length > limit)
                throw new ArgumentOutOfRangeException(parameter);

            return normalized;
        }
    }

    public interface INexLedgerAccountStateStore
    {
        NexLedgerAccountState GetAccountState(Guid accountId);

        NexLedgerAccountState SetAccountStatus(
            Guid accountId,
            NexLedgerAccountStatus status,
            string actor,
            string reason);

        IReadOnlyList<NexLedgerAccountStateEvent> ListAccountStateEvents(
            Guid accountId,
            int offset,
            int limit);
    }

    public sealed class NexLedgerPolicyException : Exception
    {
        public NexLedgerPolicyException(string message)
            : base(message)
        {
        }
    }
}
