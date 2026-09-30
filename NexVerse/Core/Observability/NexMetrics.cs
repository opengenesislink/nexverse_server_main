// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace NexVerse.Core.Observability
{
    public enum NexMetricType
    {
        Counter,
        Gauge
    }

    public sealed class NexMetricsRegistry
    {
        private readonly ConcurrentDictionary<string, MetricFamily> m_Families =
            new ConcurrentDictionary<string, MetricFamily>(StringComparer.Ordinal);

        public static NexMetricsRegistry Default { get; } = new NexMetricsRegistry();

        public void IncrementCounter(
            string name,
            string help,
            double amount = 1,
            IReadOnlyDictionary<string, string> labels = null)
        {
            if (amount < 0 || double.IsNaN(amount) || double.IsInfinity(amount))
                throw new ArgumentOutOfRangeException(nameof(amount));

            MetricFamily family = GetFamily(name, help, NexMetricType.Counter);
            family.GetSample(labels).Add(amount);
        }

        public void SetGauge(
            string name,
            string help,
            double value,
            IReadOnlyDictionary<string, string> labels = null)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(nameof(value));

            MetricFamily family = GetFamily(name, help, NexMetricType.Gauge);
            family.GetSample(labels).Set(value);
        }

        public string RenderPrometheus()
        {
            StringBuilder output = new StringBuilder(4096);

            foreach (MetricFamily family in m_Families.Values.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                output.Append("# HELP ")
                    .Append(family.Name)
                    .Append(' ')
                    .Append(EscapeHelp(family.Help))
                    .Append('\n');

                output.Append("# TYPE ")
                    .Append(family.Name)
                    .Append(' ')
                    .Append(family.Type == NexMetricType.Counter ? "counter" : "gauge")
                    .Append('\n');

                foreach (MetricSample sample in family.Samples.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    output.Append(family.Name);
                    AppendLabels(output, sample.Labels);
                    output.Append(' ')
                        .Append(sample.Value.ToString("R", CultureInfo.InvariantCulture))
                        .Append('\n');
                }
            }

            return output.ToString();
        }

        private MetricFamily GetFamily(string rawName, string help, NexMetricType type)
        {
            string name = NormalizeMetricName(rawName);
            string safeHelp = string.IsNullOrWhiteSpace(help) ? name : help.Trim();

            MetricFamily family = m_Families.GetOrAdd(
                name,
                _ => new MetricFamily(name, safeHelp, type));

            if (family.Type != type)
                throw new InvalidOperationException("Metric type conflict for " + name + ".");

            return family;
        }

        private static string NormalizeMetricName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Metric name is required.", nameof(value));

            StringBuilder result = new StringBuilder(value.Length);
            foreach (char c in value.Trim())
                result.Append(char.IsLetterOrDigit(c) || c == '_' || c == ':' ? c : '_');

            if (result.Length == 0 || !(char.IsLetter(result[0]) || result[0] == '_' || result[0] == ':'))
                result.Insert(0, '_');

            return result.ToString();
        }

        private static string NormalizeLabelName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "_";

            StringBuilder result = new StringBuilder(value.Length);
            foreach (char c in value.Trim())
                result.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');

            if (result.Length == 0 || !(char.IsLetter(result[0]) || result[0] == '_'))
                result.Insert(0, '_');

            return result.ToString();
        }

        private static void AppendLabels(StringBuilder output, IReadOnlyDictionary<string, string> labels)
        {
            if (labels == null || labels.Count == 0)
                return;

            output.Append('{');
            bool first = true;
            foreach (KeyValuePair<string, string> label in labels.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (!first)
                    output.Append(',');

                first = false;
                output.Append(NormalizeLabelName(label.Key))
                    .Append("=\"")
                    .Append(EscapeLabel(label.Value ?? string.Empty))
                    .Append('\"');
            }
            output.Append('}');
        }

        private static string EscapeHelp(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\n", "\\n");
        }

        private static string EscapeLabel(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\n", "\\n")
                .Replace("\"", "\\\"");
        }

        private sealed class MetricFamily
        {
            private readonly ConcurrentDictionary<string, MetricSample> m_Samples =
                new ConcurrentDictionary<string, MetricSample>(StringComparer.Ordinal);

            public string Name { get; }
            public string Help { get; }
            public NexMetricType Type { get; }
            public IEnumerable<MetricSample> Samples => m_Samples.Values;

            public MetricFamily(string name, string help, NexMetricType type)
            {
                Name = name;
                Help = help;
                Type = type;
            }

            public MetricSample GetSample(IReadOnlyDictionary<string, string> labels)
            {
                Dictionary<string, string> normalized =
                    labels == null
                        ? new Dictionary<string, string>(StringComparer.Ordinal)
                        : labels
                            .OrderBy(x => x.Key, StringComparer.Ordinal)
                            .ToDictionary(
                                x => NormalizeLabelName(x.Key),
                                x => x.Value ?? string.Empty,
                                StringComparer.Ordinal);

                string key = string.Join(
                    "\u001f",
                    normalized.Select(x => x.Key + "\u001e" + x.Value));

                return m_Samples.GetOrAdd(key, _ => new MetricSample(key, normalized));
            }
        }

        private sealed class MetricSample
        {
            private long m_ValueBits;

            public string Key { get; }
            public IReadOnlyDictionary<string, string> Labels { get; }
            public double Value => BitConverter.Int64BitsToDouble(
                System.Threading.Interlocked.Read(ref m_ValueBits));

            public MetricSample(string key, IReadOnlyDictionary<string, string> labels)
            {
                Key = key;
                Labels = labels;
                m_ValueBits = BitConverter.DoubleToInt64Bits(0);
            }

            public void Set(double value)
            {
                System.Threading.Interlocked.Exchange(
                    ref m_ValueBits,
                    BitConverter.DoubleToInt64Bits(value));
            }

            public void Add(double amount)
            {
                while (true)
                {
                    long currentBits = System.Threading.Interlocked.Read(ref m_ValueBits);
                    double current = BitConverter.Int64BitsToDouble(currentBits);
                    long nextBits = BitConverter.DoubleToInt64Bits(current + amount);

                    if (System.Threading.Interlocked.CompareExchange(
                        ref m_ValueBits,
                        nextBits,
                        currentBits) == currentBits)
                        return;
                }
            }
        }
    }
}
