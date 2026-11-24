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
        private const double ExpansionTermWeight = 0.55;

        private static readonly Regex CamelBoundaryRegex = new Regex("(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);
        private static readonly Regex TokenSplitRegex = new Regex("[^a-zA-Z0-9]+", RegexOptions.Compiled);

        private static readonly HashSet<string> ExplicitRevitTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "revit", "rir", "bim", "directshape", "toposolid", "workset", "worksets"
        };

        private static readonly HashSet<string> BimElementTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "wall", "walls", "floor", "floors", "slab", "slabs", "roof", "roofs",
            "column", "columns", "beam", "beams", "framing", "structural",
            "level", "levels", "grid", "grids", "family", "families",
            "category", "categories", "element", "elements", "parameter", "parameters",
            "schedule", "schedules", "sheet", "sheets", "room", "rooms",
            "mullion", "mullions", "curtain", "ceiling", "ceilings", "door", "doors", "window", "windows"
        };

        private static readonly Dictionary<string, string[]> DomainExpansions = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["revit"] = new[] { "revit", "element", "category", "type", "parameter", "level", "query", "add", "inspect", "directshape" },
            ["rir"] = new[] { "revit", "element", "category", "type", "parameter", "level", "query", "add", "inspect", "directshape" },
            ["bim"] = new[] { "revit", "element", "category", "type", "parameter", "level", "query", "add", "directshape" },
            ["wall"] = new[] { "wall", "add", "curve", "profile", "level", "type", "revit" },
            ["walls"] = new[] { "wall", "add", "curve", "profile", "level", "type", "revit" },
            ["floor"] = new[] { "floor", "add", "boundary", "level", "type", "revit" },
            ["floors"] = new[] { "floor", "add", "boundary", "level", "type", "revit" },
            ["slab"] = new[] { "floor", "add", "boundary", "level", "type", "revit" },
            ["roof"] = new[] { "roof", "add", "boundary", "level", "type", "revit" },
            ["roofs"] = new[] { "roof", "add", "boundary", "level", "type", "revit" },
            ["column"] = new[] { "column", "structural", "add", "level", "type", "revit" },
            ["columns"] = new[] { "column", "structural", "add", "level", "type", "revit" },
            ["beam"] = new[] { "beam", "structural", "framing", "add", "curve", "type", "revit" },
            ["beams"] = new[] { "beam", "structural", "framing", "add", "curve", "type", "revit" },
            ["framing"] = new[] { "framing", "structural", "beam", "add", "curve", "revit" },
            ["level"] = new[] { "level", "levels", "query", "add", "identity", "elevation", "revit" },
            ["levels"] = new[] { "level", "levels", "query", "add", "identity", "elevation", "revit" },
            ["grid"] = new[] { "grid", "grids", "query", "add", "revit" },
            ["grids"] = new[] { "grid", "grids", "query", "add", "revit" },
            ["family"] = new[] { "family", "type", "types", "query", "picker", "element", "component", "location", "revit" },
            ["families"] = new[] { "family", "type", "types", "query", "picker", "element", "component", "location", "revit" },
            ["category"] = new[] { "category", "categories", "query", "picker", "filter", "revit" },
            ["categories"] = new[] { "category", "categories", "query", "picker", "filter", "revit" },
            ["element"] = new[] { "element", "elements", "query", "inspect", "parameter", "filter", "geometry", "revit" },
            ["elements"] = new[] { "element", "elements", "query", "inspect", "parameter", "filter", "geometry", "revit" },
            ["parameter"] = new[] { "parameter", "parameters", "element", "inspect", "identity", "revit" },
            ["parameters"] = new[] { "parameter", "parameters", "element", "inspect", "identity", "revit" },
            ["directshape"] = new[] { "directshape", "geometry", "brep", "mesh", "add", "category", "revit" },
            ["room"] = new[] { "room", "area", "spatial", "element", "query", "add", "revit" },
            ["rooms"] = new[] { "room", "area", "spatial", "element", "query", "add", "revit" },
            ["schedule"] = new[] { "view", "sheet", "schedule", "query", "revit" },
            ["schedules"] = new[] { "view", "sheet", "schedule", "query", "revit" },
            ["sheet"] = new[] { "view", "sheet", "schedule", "query", "revit" },
            ["sheets"] = new[] { "view", "sheet", "schedule", "query", "revit" },
            ["diagrid"] = new[] { "panel", "panels", "lunchbox", "grid", "divide", "surface", "structure" },
            ["paneling"] = new[] { "panel", "panels", "lunchbox", "grid", "divide", "surface" },
            ["panels"] = new[] { "panel", "panels", "lunchbox", "diamond", "quad", "triangular", "surface" },
            ["hex"] = new[] { "hexagon", "cells", "panel", "lunchbox", "grid", "surface" },
            ["hexagon"] = new[] { "hexagon", "cells", "panel", "lunchbox", "grid", "surface" },
            ["diamond"] = new[] { "diamond", "panel", "panels", "lunchbox", "surface" },
            ["subdivide"] = new[] { "weaverbird", "mesh", "subdivision", "catmull", "clark", "loop" },
            ["catmull"] = new[] { "weaverbird", "mesh", "subdivision", "catmull", "clark" },
            ["thicken"] = new[] { "weaverbird", "mesh", "thicken", "offset", "pufferfish" },
            ["tween"] = new[] { "pufferfish", "tween", "morph", "curve", "surface", "plane" },
            ["morph"] = new[] { "pufferfish", "morph", "tween", "box", "surface" },
            ["bake"] = new[] { "bake", "attributes", "layer", "elefront", "human", "object" },
            ["attributes"] = new[] { "attributes", "layer", "elefront", "human", "object" },
            ["slider"] = new[] { "number", "slider", "params", "input" },
            ["sliders"] = new[] { "number", "slider", "params", "input" },
            ["toggle"] = new[] { "boolean", "toggle", "params", "input" },
            ["input"] = new[] { "number", "slider", "panel", "boolean", "toggle", "geometry", "curve", "brep", "surface", "point", "params" },
            ["inputs"] = new[] { "number", "slider", "panel", "boolean", "toggle", "geometry", "curve", "brep", "surface", "point", "params" },
            ["selection"] = new[] { "geometry", "curve", "brep", "surface", "mesh", "point", "params", "model", "object" },
            ["selected"] = new[] { "geometry", "curve", "brep", "surface", "mesh", "point", "params", "model", "object" }
        };

        private static readonly string[] CoreRevitPrimitiveNames =
        {
            "Query Categories",
            "Category Picker",
            "Query Types",
            "Element Type Picker",
            "Query Elements",
            "Inspect Element",
            "Element Parameter",
            "Query Levels",
            "Add Level",
            "Add Wall (Curve)",
            "Add Floor",
            "Add DirectShape Geometry",
            "Graphical Element"
        };

        private static readonly string[] EssentialUserInputNames =
        {
            "Number Slider",
            "Panel",
            "Boolean Toggle",
            "Value List",
            "Button"
        };

        private static readonly string[] EssentialSelectionParamNames =
        {
            "Geometry",
            "Curve",
            "Brep",
            "Surface",
            "Mesh",
            "Point",
            "Vector",
            "Plane",
            "Box"
        };

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
            "Vector XYZ",
            "Unit Z",
            "Circle",
            "Rectangle",
            "Line",
            "Move",
            "Rotate",
            "Scale",
            "Extrude",
            "Loft",
            "Boundary Surfaces",
            "Cap Holes",
            "List Item",
            "Merge"
        };

        public static IReadOnlyList<ComponentRecord> Retrieve(
            IReadOnlyList<ComponentRecord> corpus,
            string query,
            int maxResults = 90)
        {
            if (corpus is null)
            {
                throw new ArgumentNullException(nameof(corpus));
            }

            if (corpus.Count == 0)
            {
                return Array.Empty<ComponentRecord>();
            }

            int limit = maxResults < 1 ? 1 : (maxResults > 140 ? 140 : maxResults);
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
                AddWeightedTokens(freqs, Tokenize(record.LibraryName), 2.2);
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
            Dictionary<string, double> weightedQueryTerms = BuildWeightedQueryTerms(queryTokens, out bool hasExplicitRevitIntent, out bool hasBimElementIntent);
            bool corpusHasRevit = corpus.Any(c => c.IsRevitComponent);

            var scored = new List<(ComponentRecord Record, double Score)>(docCount);

            for (int i = 0; i < docCount; i++)
            {
                ComponentRecord record = corpus[i];
                Dictionary<string, double> tfMap = docFieldFreqs[i];
                double dl = docLengths[i];
                double bm25 = 0.0;

                foreach (KeyValuePair<string, double> qTerm in weightedQueryTerms)
                {
                    if (!tfMap.TryGetValue(qTerm.Key, out double tf) || tf <= 0.0)
                    {
                        continue;
                    }

                    int df = docFreq.TryGetValue(qTerm.Key, out int dfVal) ? dfVal : 0;
                    double idf = Math.Log((docCount - df + 0.5) / (df + 0.5) + 1.0);
                    double denom = tf + K1 * (1.0 - B + B * (dl / avgDocLength));
                    bm25 += qTerm.Value * idf * (tf * (K1 + 1.0) / denom);
                }

                if (bm25 > 0.0 && record.IsRevitComponent)
                {
                    if (hasExplicitRevitIntent)
                    {
                        bm25 *= 2.0;
                    }
                    else if (hasBimElementIntent && corpusHasRevit)
                    {
                        bm25 *= 1.45;
                    }
                }

                if (bm25 > 0.0 && record.IsCompany)
                {
                    bm25 = bm25 * CompanyMultiplier + CompanyAdditiveBoost;
                }

                scored.Add((record, bm25));
            }

            var selected = new List<ComponentRecord>(limit);
            var selectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            List<(ComponentRecord Record, double Score)> positiveScored = scored
                .Where(x => x.Score > 0.0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Record.IsCompany)
                .ThenBy(x => x.Record.ShortId, StringComparer.Ordinal)
                .ToList();

            List<ComponentRecord> guaranteedFoundation = limit >= 25
                ? CollectEssentialFoundation(corpus, Math.Min(24, limit / 3))
                : new List<ComponentRecord>();

            var reservedIds = new HashSet<string>(
                guaranteedFoundation.Select(c => c.ShortId),
                StringComparer.OrdinalIgnoreCase);

            int nonReservedAdded = 0;
            int maxNonReservedBeforeGuarantee = Math.Max(1, limit - guaranteedFoundation.Count);

            foreach ((ComponentRecord record, double _) in positiveScored)
            {
                if (selected.Count >= limit)
                {
                    break;
                }

                bool isReserved = reservedIds.Contains(record.ShortId);
                if (!isReserved && nonReservedAdded >= maxNonReservedBeforeGuarantee)
                {
                    continue;
                }

                if (selectedIds.Add(record.ShortId))
                {
                    selected.Add(record);
                    if (!isReserved)
                    {
                        nonReservedAdded++;
                    }
                }
            }

            foreach (ComponentRecord essential in guaranteedFoundation)
            {
                if (selected.Count >= limit)
                {
                    break;
                }

                if (selectedIds.Add(essential.ShortId))
                {
                    selected.Add(essential);
                }
            }

            foreach ((ComponentRecord record, double _) in positiveScored)
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

            // Pad with core Revit foundation primitives when working in Rhino.Inside.Revit, then core primitives and company items.
            if (selected.Count < limit && corpusHasRevit && (hasExplicitRevitIntent || hasBimElementIntent))
            {
                foreach (ComponentRecord record in corpus
                    .Where(IsCoreRevitPrimitive)
                    .OrderBy(c => c.ShortId, StringComparer.Ordinal))
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

        private static Dictionary<string, double> BuildWeightedQueryTerms(
            List<string> queryTokens,
            out bool hasExplicitRevitIntent,
            out bool hasBimElementIntent)
        {
            var terms = new Dictionary<string, double>(StringComparer.Ordinal);
            hasExplicitRevitIntent = false;
            hasBimElementIntent = false;

            foreach (string token in queryTokens)
            {
                terms[token] = 1.0;
                if (ExplicitRevitTokens.Contains(token))
                {
                    hasExplicitRevitIntent = true;
                }

                if (BimElementTokens.Contains(token))
                {
                    hasBimElementIntent = true;
                }
            }

            foreach (string token in queryTokens)
            {
                if (!DomainExpansions.TryGetValue(token, out string[]? expansions))
                {
                    continue;
                }

                foreach (string expansion in expansions)
                {
                    if (!terms.ContainsKey(expansion))
                    {
                        terms[expansion] = ExpansionTermWeight;
                    }
                }
            }

            return terms;
        }

        private static bool IsCoreRevitPrimitive(ComponentRecord record)
        {
            if (!record.IsRevitComponent)
            {
                return false;
            }

            foreach (string name in CoreRevitPrimitiveNames)
            {
                if (string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<ComponentRecord> CollectEssentialFoundation(
            IReadOnlyList<ComponentRecord> corpus,
            int maxFoundation)
        {
            var foundation = new List<ComponentRecord>(maxFoundation);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void TryAddByName(string targetName, bool preferStandaloneParam)
            {
                if (foundation.Count >= maxFoundation) return;

                ComponentRecord? best = null;
                foreach (ComponentRecord item in corpus)
                {
                    if (item == null || !string.Equals(item.Name, targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (preferStandaloneParam &&
                        (item.IsStandaloneParam || string.Equals(item.Category, "Params", StringComparison.OrdinalIgnoreCase)))
                    {
                        best = item;
                        break;
                    }

                    if (best == null)
                    {
                        best = item;
                    }
                }

                if (best != null && !string.IsNullOrWhiteSpace(best.ShortId) && seen.Add(best.ShortId))
                {
                    foundation.Add(best);
                }
            }

            foreach (string inputName in EssentialUserInputNames)
            {
                TryAddByName(inputName, preferStandaloneParam: true);
            }

            foreach (string paramName in EssentialSelectionParamNames)
            {
                TryAddByName(paramName, preferStandaloneParam: true);
            }

            foreach (string coreName in CorePrimitiveNames)
            {
                TryAddByName(coreName, preferStandaloneParam: false);
            }

            return foundation;
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
