using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace GhCopilot.Core.Index
{
    /// <summary>
    /// Shortlists candidate Grasshopper components for a user prompt using weighted Okapi BM25 and company-item boosting.
    /// </summary>
    public static class Retriever
    {
        private const double K1 = 1.5;
        private const double B = 0.75;
        private const double CompanyMultiplier = 2.5;
        private const double CompanyAdditiveBoost = 5.0;

        private static readonly Regex CamelBoundaryRegex = new Regex("(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);
        private static readonly Regex TokenSplitRegex = new Regex("[^a-zA-Z0-9]+", RegexOptions.Compiled);

        private static readonly string[] CorePrimitiveNames =
        {
            "Number Slider",
            "Panel",
            "Addition",
            "Subtraction",
            "Multiplication",
            "Division",
            "Series",
            "Range",
            "Construct Point",
            "Circle",
            "Line",
            "Move",
            "Extrude",
            "List Item",
            "Merge"
        };

        public static IReadOnlyList<ComponentRecord> Retrieve(
            IReadOnlyList<ComponentRecord> corpus,
            string query,
            int maxResults = 60)
        {
            if (corpus is null)
            {
                throw new ArgumentNullException(nameof(corpus));
            }

            if (corpus.Count == 0)
            {
                return Array.Empty<ComponentRecord>();
            }

            int limit = maxResults < 1 ? 1 : (maxResults > 80 ? 80 : maxResults);
            List<string> queryTokens = Tokenize(query);

            if (queryTokens.Count == 0)
            {
                return corpus
                    .OrderByDescending(c => c.IsCompany)
                    .ThenByDescending(IsCorePrimitive)
                    .ThenBy(c => c.ShortId, StringComparer.Ordinal)
                    .Take(limit)
                    .ToList();
            }

            int docCount = corpus.Count;
            var docFieldFreqs = new Dictionary<string, double>[docCount];
            var docLengths = new double[docCount];
            var docFreq = new Dictionary<string, int>(StringComparer.Ordinal);
            double totalLength = 0.0;

            for (int i = 0; i < docCount; i++)
            {
                ComponentRecord record = corpus[i];
                var freqs = new Dictionary<string, double>(StringComparer.Ordinal);

                AddWeightedTokens(freqs, Tokenize(record.Name), 3.0);
                AddWeightedTokens(freqs, Tokenize(record.NickName), 2.0);
                AddWeightedTokens(freqs, Tokenize(record.Category), 1.5);
                AddWeightedTokens(freqs, Tokenize(record.SubCategory), 1.5);
                AddWeightedTokens(freqs, Tokenize(record.Description), 1.0);

                double length = 0.0;
                foreach (KeyValuePair<string, double> kvp in freqs)
                {
                    length += kvp.Value;
                    docFreq[kvp.Key] = docFreq.TryGetValue(kvp.Key, out int currentDf) ? currentDf + 1 : 1;
                }

                docFieldFreqs[i] = freqs;
                docLengths[i] = length;
                totalLength += length;
            }

            double avgDocLength = totalLength > 0.0 ? totalLength / docCount : 1.0;
            List<string> distinctQueryTokens = queryTokens.Distinct(StringComparer.Ordinal).ToList();

            var scored = new List<(ComponentRecord Record, double Score)>(docCount);

            for (int i = 0; i < docCount; i++)
            {
                ComponentRecord record = corpus[i];
                Dictionary<string, double> tfMap = docFieldFreqs[i];
                double dl = docLengths[i];
                double bm25 = 0.0;

                foreach (string term in distinctQueryTokens)
                {
                    if (!tfMap.TryGetValue(term, out double tf) || tf <= 0.0)
                    {
                        continue;
                    }

                    int df = docFreq.TryGetValue(term, out int dfVal) ? dfVal : 0;
                    double idf = Math.Log((docCount - df + 0.5) / (df + 0.5) + 1.0);
                    double denom = tf + K1 * (1.0 - B + B * (dl / avgDocLength));
                    bm25 += idf * (tf * (K1 + 1.0) / denom);
                }

                if (bm25 > 0.0 && record.IsCompany)
                {
                    bm25 = bm25 * CompanyMultiplier + CompanyAdditiveBoost;
                }

                scored.Add((record, bm25));
            }

            var selected = new List<ComponentRecord>(limit);
            var selectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach ((ComponentRecord record, double score) in scored
                .Where(x => x.Score > 0.0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Record.IsCompany)
                .ThenBy(x => x.Record.ShortId, StringComparer.Ordinal))
            {
                if (selected.Count >= limit)
                {
                    break;
                }

                if (selectedIds.Add(record.ShortId))
                {
                    selected.Add(record);
                }
            }

            // Pad with core primitives and company items if fewer than limit scored > 0.
            if (selected.Count < limit)
            {
                foreach (ComponentRecord record in corpus
                    .Where(c => c.IsCompany || IsCorePrimitive(c))
                    .OrderByDescending(c => c.IsCompany)
                    .ThenByDescending(IsCorePrimitive)
                    .ThenBy(c => c.ShortId, StringComparer.Ordinal))
                {
                    if (selected.Count >= limit)
                    {
                        break;
                    }

                    if (selectedIds.Add(record.ShortId))
                    {
                        selected.Add(record);
                    }
                }
            }

            if (selected.Count < limit)
            {
                foreach (ComponentRecord record in corpus)
                {
                    if (selected.Count >= limit)
                    {
                        break;
                    }

                    if (selectedIds.Add(record.ShortId))
                    {
                        selected.Add(record);
                    }
                }
            }

            return selected;
        }

        internal static List<string> Tokenize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new List<string>();
            }

            string splitCamel = CamelBoundaryRegex.Replace(text!, " ");
            string[] parts = TokenSplitRegex.Split(splitCamel);
            var tokens = new List<string>(parts.Length);

            foreach (string raw in parts)
            {
                string trimmed = raw.Trim().ToLowerInvariant();
                if (trimmed.Length >= 2)
                {
                    tokens.Add(trimmed);
                }
            }

            return tokens;
        }

        private static void AddWeightedTokens(Dictionary<string, double> map, List<string> tokens, double weight)
        {
            foreach (string token in tokens)
            {
                map[token] = map.TryGetValue(token, out double current) ? current + weight : weight;
            }
        }

        private static bool IsCorePrimitive(ComponentRecord record)
        {
            foreach (string name in CorePrimitiveNames)
            {
                if (string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
