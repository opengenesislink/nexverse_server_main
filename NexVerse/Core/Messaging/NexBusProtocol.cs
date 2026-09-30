// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexVerse.Core.Messaging
{
    public sealed class NexBusEnvelope
    {
        [JsonPropertyName("schema")]
        public string Schema { get; set; } = string.Empty;

        [JsonPropertyName("event_id")]
        public Guid EventId { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTimeOffset Timestamp { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("correlation_id")]
        public string CorrelationId { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public Dictionary<string, string> Data { get; set; } =
            new Dictionary<string, string>();
    }

    public static class NexBusProtocol
    {
        public static byte[] Serialize(NexEvent nexEvent)
        {
            if (nexEvent == null)
                throw new ArgumentNullException(nameof(nexEvent));

            return JsonSerializer.SerializeToUtf8Bytes(new NexBusEnvelope
            {
                Schema = NexVersePlatform.NexBusSchemaVersion,
                EventId = nexEvent.EventId,
                Timestamp = nexEvent.Timestamp,
                Name = nexEvent.Name,
                Source = nexEvent.Source,
                CorrelationId = nexEvent.CorrelationId,
                Data = new Dictionary<string, string>(nexEvent.Data)
            });
        }

        public static NexEvent Deserialize(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
                throw new InvalidDataException("NexBus payload is empty.");

            NexBusEnvelope envelope =
                JsonSerializer.Deserialize<NexBusEnvelope>(payload);

            if (envelope == null ||
                !string.Equals(
                    envelope.Schema,
                    NexVersePlatform.NexBusSchemaVersion,
                    StringComparison.Ordinal))
                throw new InvalidDataException("Unsupported NexBus schema version.");

            return new NexEvent(
                envelope.EventId,
                envelope.Timestamp,
                envelope.Name,
                envelope.Source,
                envelope.Data ?? new Dictionary<string, string>(),
                envelope.CorrelationId);
        }

        public static string Sign(byte[] payload, string sharedKey)
        {
            ValidateSharedKey(sharedKey);

            using HMACSHA256 hmac =
                new HMACSHA256(Encoding.UTF8.GetBytes(sharedKey));
            return Convert.ToHexString(hmac.ComputeHash(payload))
                .ToLowerInvariant();
        }

        public static bool Verify(
            byte[] payload,
            string sharedKey,
            string suppliedSignature)
        {
            if (payload == null ||
                string.IsNullOrWhiteSpace(suppliedSignature))
                return false;

            try
            {
                ValidateSharedKey(sharedKey);
                string value = suppliedSignature.StartsWith(
                    "sha256=",
                    StringComparison.OrdinalIgnoreCase)
                        ? suppliedSignature.Substring("sha256=".Length)
                        : suppliedSignature;

                byte[] expected = Convert.FromHexString(Sign(payload, sharedKey));
                byte[] actual = Convert.FromHexString(value);

                return expected.Length == actual.Length &&
                       CryptographicOperations.FixedTimeEquals(expected, actual);
            }
            catch
            {
                return false;
            }
        }

        public static void ValidateSharedKey(string sharedKey)
        {
            if (string.IsNullOrEmpty(sharedKey) ||
                Encoding.UTF8.GetByteCount(sharedKey) < 32)
                throw new ArgumentException(
                    "NexBus shared key must contain at least 32 UTF-8 bytes.",
                    nameof(sharedKey));
        }
    }

    public interface INexVerseEventBusModule
    {
        string NodeId { get; }
        IDisposable Subscribe(string eventName, Action<NexEvent> handler);
        void Publish(NexEvent nexEvent);
    }
}
