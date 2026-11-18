using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using GhCopilot.Core.Index;

namespace GhCopilot.Core.Graph
{
    public sealed class CanvasRuntimeMessage
    {
        [JsonPropertyName("level")]
        public string Level { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    public sealed class CanvasExternalWire
    {
        [JsonPropertyName("externalInstanceGuid")]
        public Guid ExternalInstanceGuid { get; set; }

        [JsonPropertyName("externalParamName")]
        public string ExternalParamName { get; set; } = string.Empty;

        [JsonPropertyName("selectedNodeId")]
        public string SelectedNodeId { get; set; } = string.Empty;

        [JsonPropertyName("selectedParamName")]
        public string SelectedParamName { get; set; } = string.Empty;
    }

    public sealed class CanvasNodeSnapshot
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("instanceGuid")]
        public Guid InstanceGuid { get; set; }

        [JsonPropertyName("componentGuid")]
        public Guid ComponentGuid { get; set; }

        [JsonPropertyName("shortId")]
        public string ShortId { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("nickName")]
        public string NickName { get; set; } = string.Empty;

        [JsonPropertyName("category")]
        public string Category { get; set; } = string.Empty;

        [JsonPropertyName("subCategory")]
        public string SubCategory { get; set; } = string.Empty;

        [JsonPropertyName("x")]
        public float X { get; set; }

        [JsonPropertyName("y")]
        public float Y { get; set; }

        [JsonPropertyName("isSelected")]
        public bool IsSelected { get; set; } = true;

        [JsonPropertyName("settings")]
        public NodeSettings? Settings { get; set; }

        [JsonPropertyName("outputs")]
        public List<ParamRecord> Outputs { get; set; } = new List<ParamRecord>();

        [JsonPropertyName("inputs")]
        public List<ParamRecord> Inputs { get; set; } = new List<ParamRecord>();

        [JsonPropertyName("runtimeMessages")]
        public List<CanvasRuntimeMessage> RuntimeMessages { get; set; } = new List<CanvasRuntimeMessage>();
    }

    public sealed class CanvasWireSnapshot
    {
        [JsonPropertyName("fromNode")]
        public string FromNode { get; set; } = string.Empty;

        [JsonPropertyName("fromParam")]
        public string FromParam { get; set; } = string.Empty;

        [JsonPropertyName("toNode")]
        public string ToNode { get; set; } = string.Empty;

        [JsonPropertyName("toParam")]
        public string ToParam { get; set; } = string.Empty;

        [JsonPropertyName("targetMapping")]
        public string? TargetMapping { get; set; }

        [JsonPropertyName("simplify")]
        public bool? Simplify { get; set; }

        [JsonPropertyName("reverse")]
        public bool? Reverse { get; set; }
    }

    /// <summary>
    /// Privacy-safe metadata snapshot of selected Grasshopper canvas objects, their connections, and runtime messages.
    /// Contains zero Rhino geometry data.
    /// </summary>
    public sealed class CanvasSnapshot
    {
        [JsonPropertyName("nodes")]
        public List<CanvasNodeSnapshot> Nodes { get; set; } = new List<CanvasNodeSnapshot>();

        [JsonPropertyName("wires")]
        public List<CanvasWireSnapshot> Wires { get; set; } = new List<CanvasWireSnapshot>();

        [JsonPropertyName("externalIncomingWires")]
        public List<CanvasExternalWire> ExternalIncomingWires { get; set; } = new List<CanvasExternalWire>();

        [JsonPropertyName("externalOutgoingWires")]
        public List<CanvasExternalWire> ExternalOutgoingWires { get; set; } = new List<CanvasExternalWire>();

        [JsonIgnore]
        public List<Guid> SelectedInstanceGuids => Nodes
            .Where(n => n.IsSelected && n.InstanceGuid != Guid.Empty)
            .Select(n => n.InstanceGuid)
            .ToList();

        public Dictionary<string, ComponentRecord> BuildExternalSelectionRecordMap()
        {
            var map = new Dictionary<string, ComponentRecord>(StringComparer.OrdinalIgnoreCase);
            List<CanvasNodeSnapshot> selected = Nodes.Where(n => n.IsSelected).ToList();

            for (int i = 0; i < selected.Count; i++)
            {
                CanvasNodeSnapshot node = selected[i];
                var record = new ComponentRecord
                {
                    ShortId = $"${node.Id}",
                    ComponentGuid = node.ComponentGuid,
                    Name = node.Name,
                    NickName = node.NickName,
                    IsHydrated = true,
                    Inputs = node.Inputs,
                    Outputs = node.Outputs
                };

                map[$"${node.Id}"] = record;
                if (i == 0)
                {
                    map["$selected"] = record;
                }
            }

            return map;
        }

        public string FormatSelectedOutputsForWiring()
        {
            List<CanvasNodeSnapshot> selected = Nodes.Where(n => n.IsSelected).ToList();
            if (selected.Count == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            sb.AppendLine("SELECTED CANVAS OUTPUTS AVAILABLE FOR WIRING (use fromNode '$selected' or '$s1', '$s2', ...):");
            for (int i = 0; i < selected.Count; i++)
            {
                CanvasNodeSnapshot node = selected[i];
                string alias = i == 0 ? $"${node.Id} (alias: $selected)" : $"${node.Id}";
                sb.Append($"{alias} | {node.Name}");
                if (node.Outputs.Count > 0)
                {
                    sb.Append(" | out: ");
                    sb.Append(string.Join(" ", node.Outputs.Select(o => $"{o.Name}({o.TypeName},{o.Access})")));
                }
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        public string FormatForPrompt(bool includeNonSelectedContext)
        {
            List<CanvasNodeSnapshot> includedNodes = Nodes
                .Where(n => n.IsSelected || includeNonSelectedContext)
                .ToList();

            if (includedNodes.Count == 0)
            {
                return "(no objects selected)";
            }

            var includedIds = new HashSet<string>(includedNodes.Select(n => n.Id), StringComparer.Ordinal);
            List<CanvasWireSnapshot> includedWires = Wires
                .Where(w => includedIds.Contains(w.FromNode) && includedIds.Contains(w.ToNode))
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("NODES:");
            foreach (CanvasNodeSnapshot node in includedNodes)
            {
                string selectionTag = node.IsSelected ? "selected" : "neighbor-context";
                string shortRef = string.IsNullOrWhiteSpace(node.ShortId) ? "unindexed" : node.ShortId;
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0} [{1}] | ref={2} | {3}", node.Id, selectionTag, shortRef, node.Name);
                if (!string.IsNullOrWhiteSpace(node.NickName) &&
                    !string.Equals(node.NickName, node.Name, StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append($" (nick='{node.NickName}')");
                }

                sb.AppendFormat(CultureInfo.InvariantCulture, " | pos=({0:F0},{1:F0})", node.X, node.Y);

                if (node.Settings != null)
                {
                    if (node.Settings.Value.HasValue)
                    {
                        sb.AppendFormat(
                            CultureInfo.InvariantCulture,
                            " | slider=[min={0}, max={1}, val={2}, dec={3}]",
                            node.Settings.Min,
                            node.Settings.Max,
                            node.Settings.Value,
                            node.Settings.Decimals);
                    }
                    if (node.Settings.Text != null)
                    {
                        string compactText = node.Settings.Text.Replace("\r", " ").Replace("\n", " ");
                        sb.Append($" | text='{compactText}'");
                    }
                }

                sb.AppendLine();

                foreach (CanvasRuntimeMessage msg in node.RuntimeMessages)
                {
                    sb.AppendLine($"  [{msg.Level}] {msg.Message}");
                }
            }

            sb.AppendLine("WIRES:");
            if (includedWires.Count == 0)
            {
                sb.AppendLine("  (none)");
            }
            else
            {
                foreach (CanvasWireSnapshot w in includedWires)
                {
                    sb.Append($"  {w.FromNode}.{w.FromParam} -> {w.ToNode}.{w.ToParam}");
                    if (!string.IsNullOrWhiteSpace(w.TargetMapping))
                    {
                        sb.Append($" [{w.TargetMapping}]");
                    }
                    sb.AppendLine();
                }
            }

            return sb.ToString().TrimEnd();
        }
    }
}
