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
        public static JsonElement BuildResponseJsonSchema(IEnumerable<ComponentRecord> shortlist)
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

            var schemaObject = new
            {
                type = "object",
                properties = new
                {
                    nodes = new
                    {
                        type = "array",
                        description = "Grasshopper nodes to place on the canvas (can be empty if script fallback is used).",
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
                                x = new { type = "number", description = "Canvas X coordinate (typically 100..2000)." },
                                y = new { type = "number", description = "Canvas Y coordinate (typically 100..1200)." },
                                settings = new
                                {
                                    type = "object",
                                    description = "Optional settings for sliders, panels, or node nicknames.",
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
                    },
                    wires = new
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
                    },
                    script = new
                    {
                        type = new[] { "object", "null" },
                        description = "Optional Python 3 script component fallback for complex procedural geometry or math. Leave null when native nodes handle the task.",
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
                    explanation = new
                    {
                        type = "string",
                        description = "Brief explanation of what the Grasshopper definition builds."
                    }
                },
                required = new[] { "nodes", "wires", "explanation" }
            };

            return JsonSerializer.SerializeToElement(schemaObject);
        }
    }
}
