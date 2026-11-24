using System;
using System.Collections.Generic;
using System.Text.Json;
using GhCopilot.Core.Index;

namespace GhCopilot.Core.Llm
{
    /// <summary>
    /// Builds per-request JSON Schema definitions constraining Gemini structured output to the retrieved shortlist IDs
    /// and optional Python 3 script fallback block.
    /// </summary>
    public static class SchemaBuilder
    {
        public static JsonElement BuildResponseJsonSchema(
            IEnumerable<ComponentRecord> shortlist,
            bool includeScript = true)
        {
            if (shortlist is null)
            {
                throw new ArgumentNullException(nameof(shortlist));
            }

            var shortlistIds = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (ComponentRecord record in shortlist)
            {
                if (record != null && !string.IsNullOrWhiteSpace(record.ShortId) && seen.Add(record.ShortId))
                {
                    shortlistIds.Add(record.ShortId);
                }
            }

            if (shortlistIds.Count == 0)
            {
                throw new ArgumentException("Shortlist must contain at least one valid ShortId.", nameof(shortlist));
            }

            var nodesSchema = new
            {
                type = "array",
                description = includeScript
                    ? "Grasshopper nodes to place on the canvas. MUST be empty [] when answering a greeting, clarification, or conversational question that does not request building geometry, or when script fallback is used."
                    : "Grasshopper nodes (native, plugin, Revit, sliders, panels, or selection parameters) to place on the canvas. MUST be empty [] only when answering a pure conversational question that does not request building or placing nodes.",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        id = new { type = "string", description = "Unique local node identifier (e.g., n1, n2)." },
                        @ref = new
                        {
                            type = "string",
                            description = "Short ID selected strictly from the retrieved shortlist.",
                            @enum = shortlistIds
                        },
                        x = new { type = "number", description = "Canvas X coordinate (typically 100..4000)." },
                        y = new { type = "number", description = "Canvas Y coordinate (typically 100..2000)." },
                        settings = new
                        {
                            type = "object",
                            description = "Optional settings for sliders (min, max, value, decimals), panels (text), input modifiers, or node nicknames.",
                            properties = new
                            {
                                min = new { type = "number" },
                                max = new { type = "number" },
                                value = new { type = "number" },
                                decimals = new { type = "integer" },
                                text = new { type = "string" },
                                nickname = new { type = "string" },
                                flattenInputs = new { type = "array", items = new { type = "string" } },
                                graftInputs = new { type = "array", items = new { type = "string" } },
                                simplifyInputs = new { type = "array", items = new { type = "string" } },
                                reverseInputs = new { type = "array", items = new { type = "string" } }
                            }
                        }
                    },
                    required = new[] { "id", "ref", "x", "y" }
                }
            };

            var wiresSchema = new
            {
                type = "array",
                description = "Directed connections from a node output parameter to a target node input parameter.",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        fromNode = new { type = "string" },
                        fromParam = new { type = "string" },
                        toNode = new { type = "string" },
                        toParam = new { type = "string" },
                        targetMapping = new { type = "string", @enum = new[] { "none", "flatten", "graft" } },
                        simplify = new { type = "boolean" },
                        reverse = new { type = "boolean" }
                    },
                    required = new[] { "fromNode", "fromParam", "toNode", "toParam" }
                }
            };

            var explanationSchema = new
            {
                type = "string",
                description = "Your conversational reply to the user, or a brief explanation of what the Grasshopper definition builds."
            };

            if (!includeScript)
            {
                var nativeOnlySchema = new
                {
                    type = "object",
                    properties = new
                    {
                        nodes = nodesSchema,
                        wires = wiresSchema,
                        explanation = explanationSchema
                    },
                    required = new[] { "nodes", "wires", "explanation" }
                };

                return JsonSerializer.SerializeToElement(nativeOnlySchema);
            }

            var schemaObject = new
            {
                type = "object",
                properties = new
                {
                    nodes = nodesSchema,
                    wires = wiresSchema,
                    script = new
                    {
                        type = new[] { "object", "null" },
                        description = "Optional Python 3 script component fallback ONLY when explicitly requested or when no native/plugin components exist.",
                        properties = new
                        {
                            code = new
                            {
                                type = "string",
                                description = "Python 3 script using only Rhino.Geometry (as rg) and standard math. Never import os, sys, subprocess, socket, urllib, requests, or open files."
                            },
                            inputs = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        name = new { type = "string" },
                                        type = new { type = "string" },
                                        access = new { type = "string", @enum = new[] { "item", "list", "tree" } }
                                    },
                                    required = new[] { "name", "type", "access" }
                                }
                            },
                            outputs = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        name = new { type = "string" },
                                        type = new { type = "string" }
                                    },
                                    required = new[] { "name", "type" }
                                }
                            }
                        },
                        required = new[] { "code", "inputs", "outputs" }
                    },
                    explanation = explanationSchema
                },
                required = new[] { "nodes", "wires", "explanation" }
            };

            return JsonSerializer.SerializeToElement(schemaObject);
        }
    }
}
