using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GhCopilot.Core.Index;

namespace GhCopilot.Core.Graph
{
    /// <summary>An ordered edit plan. Validation never changes the supplied canvas or plan.</summary>
    [JsonConverter(typeof(GraphPatchJsonConverter))]
    public sealed class GraphPatch
    {
        [JsonPropertyName("ops")]
        public List<GraphPatchOp> Ops { get; set; } = new List<GraphPatchOp>();

        [JsonPropertyName("explanation")]
        public string Explanation { get; set; } = string.Empty;
    }

    /// <summary>
    /// add_node uses Node; delete_node uses NodeId; set_param uses NodeId and Settings;
    /// wire/unwire use Wire. Unused payloads must be omitted in JSON (null in CLR).
    /// </summary>
    public sealed class GraphPatchOp
    {
        [JsonPropertyName("op")]
        public string Op { get; set; } = string.Empty;

        [JsonPropertyName("nodeId")]
        public string? NodeId { get; set; }

        [JsonPropertyName("node")]
        public NodeSpec? Node { get; set; }

        [JsonPropertyName("settings")]
        public NodeSettings? Settings { get; set; }

        [JsonPropertyName("wire")]
        public WireSpec? Wire { get; set; }
    }

    public sealed class PatchValidationResult
    {
        public bool IsValid => Errors.Count == 0;
        public List<string> Errors { get; } = new List<string>();
    }

    // Existing NodeSpec/NodeSettings cannot distinguish omitted nullable properties from explicit
    // JSON null. Check the JSON shape before deserializing those shared, unchanged contracts.
    internal sealed class GraphPatchJsonConverter : JsonConverter<GraphPatch>
    {
        public GraphPatchJsonConverter() { }

        public override bool HandleNull => true;

        public override GraphPatch Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using (JsonDocument document = JsonDocument.ParseValue(ref reader))
            {
                JsonElement root = document.RootElement;
                CheckShape(root, GraphPatchSchema.Build(), "patch");
                return new GraphPatch
                {
                    Ops = JsonSerializer.Deserialize<List<GraphPatchOp>>(root.GetProperty("ops").GetRawText(), options)!,
                    Explanation = root.GetProperty("explanation").GetString()!
                };
            }
        }

        public override void Write(Utf8JsonWriter writer, GraphPatch value, JsonSerializerOptions options)
        {
            if (value == null) throw new JsonException("Patch must not be null.");
            var omitNulls = new JsonSerializerOptions(options) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
            JsonSerializer.Serialize(writer, new { ops = value.Ops, explanation = value.Explanation }, omitNulls);
        }

        private static void CheckShape(JsonElement value, JsonElement schema, string path)
        {
            string type = schema.GetProperty("type").GetString()!;
            bool matches = type switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "number" => value.ValueKind == JsonValueKind.Number,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
                "boolean" => value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False,
                _ => false
            };
            if (!matches) throw new JsonException($"{path} must be a non-null {type}.");
            if (type == "array")
            {
                foreach (JsonElement item in value.EnumerateArray()) CheckShape(item, schema.GetProperty("items"), path + "[]");
            }
            else if (type == "object")
            {
                if (schema.TryGetProperty("required", out JsonElement required))
                    foreach (JsonElement name in required.EnumerateArray())
                        if (!value.TryGetProperty(name.GetString()!, out _)) throw new JsonException($"{path}.{name.GetString()} is required.");
                JsonElement properties = schema.GetProperty("properties");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    if (!seen.Add(property.Name) || !properties.TryGetProperty(property.Name, out JsonElement child))
                        throw new JsonException($"{path}.{property.Name} is unknown or duplicated.");
                    CheckShape(property.Value, child, path + "." + property.Name);
                }
            }
        }
    }

    /// <summary>
    /// Simulates operations in order, failing at the first invalid operation. Existing nodes use
    /// snapshot ports, not the shortlist. IDs are nonblank, trimmed, ordinal strings. Deleted IDs
    /// cannot be reused in the same patch. Deletion removes incident internal wires.
    /// </summary>
    public static class GraphPatchValidator
    {
        private static readonly Regex IndexedPort = new Regex(@"^(?<name>.+)\[(?<index>\d+)\]$", RegexOptions.Compiled);

        private sealed class StateNode
        {
            public ComponentRecord Record = new ComponentRecord();
            public NodeSettings Settings = new NodeSettings();
        }

        private sealed class StateWire
        {
            public string From = string.Empty;
            public string Output = string.Empty;
            public string To = string.Empty;
            public string Input = string.Empty;
        }

        public static PatchValidationResult Validate(
            GraphPatch patch, CanvasSnapshot snapshot, IReadOnlyList<ComponentRecord> shortlist)
        {
            var result = new PatchValidationResult();
            if (patch == null || snapshot == null || shortlist == null)
                return Fail(result, "patch, snapshot and shortlist must not be null.");
            if (patch.Ops == null || patch.Explanation == null)
                return Fail(result, "Patch ops and explanation must not be null.");
            if (snapshot.Nodes == null || snapshot.Wires == null)
                return Fail(result, "Snapshot nodes and wires must not be null.");

            var records = new Dictionary<string, ComponentRecord>(StringComparer.Ordinal);
            foreach (ComponentRecord record in shortlist)
            {
                if (record == null || !ValidId(record.ShortId) || !ValidPorts(record.Inputs) || !ValidPorts(record.Outputs))
                    return Fail(result, "Shortlist contains null or invalid component metadata.");
                if (records.ContainsKey(record.ShortId))
                    return Fail(result, $"Duplicate shortlist ref '{record.ShortId}'.");
                records.Add(record.ShortId, record);
            }

            var nodes = new Dictionary<string, StateNode>(StringComparer.Ordinal);
            var usedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (CanvasNodeSnapshot node in snapshot.Nodes)
            {
                if (node == null || !ValidId(node.Id) || !usedIds.Add(node.Id) ||
                    node.Name == null || !ValidPorts(node.Inputs) || !ValidPorts(node.Outputs))
                    return Fail(result, "Snapshot contains null, duplicate or invalid node metadata.");
                nodes.Add(node.Id, new StateNode
                {
                    Record = new ComponentRecord { Name = node.Name, Inputs = node.Inputs, Outputs = node.Outputs },
                    Settings = Merge(null, node.Settings)
                });
            }

            var wires = new List<StateWire>();
            foreach (CanvasWireSnapshot wire in snapshot.Wires)
            {
                if (wire == null || wire.FromNode == null || wire.ToNode == null ||
                    wire.FromParam == null || wire.ToParam == null ||
                    !nodes.ContainsKey(wire.FromNode) || !nodes.ContainsKey(wire.ToNode))
                    return Fail(result, "Snapshot contains null or dangling internal wire metadata.");
                // Preserve old, possibly unresolvable wires without requiring their components to be indexed.
                wires.Add(Connection(wire.FromNode, wire.FromParam, wire.ToNode, wire.ToParam, nodes));
            }

            for (int i = 0; i < patch.Ops.Count; i++)
            {
                GraphPatchOp op = patch.Ops[i];
                string? error = ValidateOperation(op, nodes, usedIds, wires, records);
                if (error != null)
                    return Fail(result, $"Op {i}: {error}");
            }
            return result;
        }

        private static string? ValidateOperation(GraphPatchOp? op, Dictionary<string, StateNode> nodes,
            HashSet<string> usedIds, List<StateWire> wires, Dictionary<string, ComponentRecord> shortlist)
        {
            if (op == null || op.Op == null) return "Operation and op must not be null.";
            switch (op.Op)
            {
                case "add_node":
                {
                    if (op.Node == null || op.NodeId != null || op.Settings != null || op.Wire != null)
                        return "add_node requires only node.";
                    NodeSpec node = op.Node;
                    if (!ValidId(node.Id) || usedIds.Contains(node.Id)) return "Node id must be valid and unique across the snapshot and patch.";
                    if (!ValidId(node.Ref) || !shortlist.TryGetValue(node.Ref, out ComponentRecord? record))
                        return $"Unknown shortlist ref '{node.Ref}'.";
                    // Reuse existing coordinate/ref validation without imposing a size cap on the old canvas.
                    ValidationResult placement = GraphValidator.Validate(
                        new GraphSpec { Nodes = new List<NodeSpec> { node } }, shortlist);
                    if (!placement.IsValid) return string.Join("; ", placement.Errors.Select(e => e.Message));
                    var state = new StateNode { Record = record, Settings = Merge(null, node.Settings) };
                    string? error = ValidateSettings(state, node.Settings, requireSettings: false);
                    if (error != null) return error;
                    MaterializeSliderDefaults(state);
                    nodes.Add(node.Id, state);
                    usedIds.Add(node.Id);
                    return null;
                }
                case "delete_node":
                    if (!ValidId(op.NodeId) || op.Node != null || op.Settings != null || op.Wire != null)
                        return "delete_node requires only nodeId.";
                    if (!nodes.Remove(op.NodeId!)) return $"Unknown node '{op.NodeId}'.";
                    wires.RemoveAll(w => w.From == op.NodeId || w.To == op.NodeId);
                    return null;
                case "set_param":
                {
                    if (!ValidId(op.NodeId) || op.Node != null || op.Wire != null || op.Settings == null)
                        return "set_param requires only nodeId and non-null settings.";
                    if (!nodes.TryGetValue(op.NodeId!, out StateNode? state)) return $"Unknown node '{op.NodeId}'.";
                    var changed = new StateNode { Record = state.Record, Settings = Merge(state.Settings, op.Settings) };
                    string? error = ValidateSettings(changed, op.Settings, requireSettings: true);
                    if (error != null) return error;
                    MaterializeSliderDefaults(changed);
                    nodes[op.NodeId!] = changed;
                    return null;
                }
                case "wire":
                case "unwire":
                {
                    if (op.Wire == null || op.NodeId != null || op.Node != null || op.Settings != null)
                        return $"{op.Op} requires only wire.";
                    WireSpec wire = op.Wire;
                    if (!ValidId(wire.FromNode) || !ValidId(wire.ToNode) || wire.FromParam == null || wire.ToParam == null)
                        return "Wire endpoints and port names must not be null; node IDs must be valid.";
                    if (!nodes.TryGetValue(wire.FromNode, out StateNode? source) ||
                        !nodes.TryGetValue(wire.ToNode, out StateNode? target)) return "Wire references an unknown node.";
                    int from = ResolvePort(source.Record.Outputs, wire.FromParam);
                    int to = ResolvePort(target.Record.Inputs, wire.ToParam);
                    if (from < 0 || to < 0) return "Wire output/input parameter does not exist (or has the wrong direction).";
                    if (wire.TargetMapping != null && wire.TargetMapping != "none" &&
                        wire.TargetMapping != "flatten" && wire.TargetMapping != "graft") return "Invalid targetMapping.";
                    StateWire connection = Connection(wire.FromNode, wire.FromParam, wire.ToNode, wire.ToParam, nodes);
                    int existing = wires.FindIndex(w => SameConnection(w, connection));
                    if (op.Op == "unwire")
                    {
                        if (existing < 0) return "Cannot unwire: wire does not exist.";
                        wires.RemoveAt(existing);
                        return null;
                    }
                    if (existing >= 0) return "Wire already exists.";
                    if (!TypeCompat.AreCompatible(source.Record.Outputs[from].TypeName, target.Record.Inputs[to].TypeName))
                        return "Wire has incompatible types.";
                    if (CreatesCycle(connection, wires)) return "Wire would create a cycle.";
                    wires.Add(connection);
                    return null;
                }
                default:
                    return $"Unknown operation '{op.Op}'.";
            }
        }

        private static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id == id!.Trim();

        private static bool ValidPorts(List<ParamRecord>? ports) => ports != null && ports.All(p =>
            p != null && p.Name != null && p.NickName != null && p.TypeName != null && p.Access != null);

        private static PatchValidationResult Fail(PatchValidationResult result, string message)
        {
            result.Errors.Add(message);
            return result;
        }

        private static bool IsSlider(ComponentRecord record) =>
            string.Equals(record.Name, "Number Slider", StringComparison.OrdinalIgnoreCase);

        private static string? ValidateSettings(StateNode node, NodeSettings? supplied, bool requireSettings)
        {
            if (supplied == null) return requireSettings ? "Settings must not be null." : null;
            bool numeric = supplied.Min.HasValue || supplied.Max.HasValue || supplied.Value.HasValue || supplied.Decimals.HasValue;
            bool modifiers = supplied.FlattenInputs != null || supplied.GraftInputs != null ||
                supplied.SimplifyInputs != null || supplied.ReverseInputs != null;
            if (!numeric && !modifiers && supplied.Text == null && supplied.Nickname == null)
                return "Settings must contain at least one change.";
            if (numeric && !IsSlider(node.Record)) return "Numeric settings are allowed only on Number Slider nodes.";
            if (supplied.Text != null && !string.Equals(node.Record.Name, "Panel", StringComparison.OrdinalIgnoreCase))
                return "Text is allowed only on Panel nodes.";
            if (supplied.Nickname != null && string.IsNullOrWhiteSpace(supplied.Nickname)) return "Nickname must not be blank.";

            NodeSettings settings = node.Settings;
            if (numeric || IsSlider(node.Record))
            {
                double min = settings.Min ?? 0, max = settings.Max ?? 100, value = settings.Value ?? min;
                if (!DecimalSafe(min) || !DecimalSafe(max) || !DecimalSafe(value)) return "Slider numbers must be finite and decimal-representable.";
                if (min >= max) return "Slider min must be less than max.";
                if (value < min || value > max) return "Slider value must be within min and max.";
                if (settings.Decimals.HasValue && (settings.Decimals < 0 || settings.Decimals > 12)) return "Slider decimals must be between 0 and 12.";
            }

            var flattened = new HashSet<int>();
            foreach (List<string>? list in new[] { settings.FlattenInputs, settings.GraftInputs, settings.SimplifyInputs, settings.ReverseInputs })
            {
                if (list == null) continue;
                var seen = new HashSet<int>();
                foreach (string name in list)
                {
                    if (name == null) return "Input modifiers must not contain null ports.";
                    int index = ResolvePort(node.Record.Inputs, name);
                    if (index < 0) return $"Input modifier references unknown input '{name}'.";
                    if (!seen.Add(index)) return "Input modifiers contain duplicate ports.";
                    if (ReferenceEquals(list, settings.FlattenInputs)) flattened.Add(index);
                    if (ReferenceEquals(list, settings.GraftInputs) && flattened.Contains(index)) return "An input cannot be both flattened and grafted.";
                }
            }
            return null;
        }

        private static bool DecimalSafe(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return false;
            try { _ = (decimal)value; return true; }
            catch (OverflowException) { return false; }
        }

        private static void MaterializeSliderDefaults(StateNode node)
        {
            if (!IsSlider(node.Record)) return;
            node.Settings.Min ??= 0;
            node.Settings.Max ??= 100;
            node.Settings.Value ??= node.Settings.Min;
            node.Settings.Decimals ??= 2;
        }

        private static NodeSettings Merge(NodeSettings? previous, NodeSettings? change) => new NodeSettings
        {
            Min = change?.Min ?? previous?.Min,
            Max = change?.Max ?? previous?.Max,
            Value = change?.Value ?? previous?.Value,
            Decimals = change?.Decimals ?? previous?.Decimals,
            Text = change?.Text ?? previous?.Text,
            Nickname = change?.Nickname ?? previous?.Nickname,
            FlattenInputs = Copy(change?.FlattenInputs ?? previous?.FlattenInputs),
            GraftInputs = Copy(change?.GraftInputs ?? previous?.GraftInputs),
            SimplifyInputs = Copy(change?.SimplifyInputs ?? previous?.SimplifyInputs),
            ReverseInputs = Copy(change?.ReverseInputs ?? previous?.ReverseInputs)
        };

        private static List<string>? Copy(List<string>? values) => values == null ? null : new List<string>(values);

        // Same selectors as GraphValidator: name, nickname, zero-based index, Name[occurrence],
        // and the empty selector for a single port. TryParse prevents model integer overflow.
        private static int ResolvePort(IReadOnlyList<ParamRecord> ports, string spec)
        {
            spec = spec.Trim();
            if (spec.Length == 0) return ports.Count == 1 ? 0 : -1;
            Match match = IndexedPort.Match(spec);
            if (match.Success)
            {
                if (!int.TryParse(match.Groups["index"].Value, out int occurrence)) return -1;
                string name = match.Groups["name"].Value.Trim();
                for (int i = 0; i < ports.Count; i++)
                    if (Matches(ports[i], name) && occurrence-- == 0) return i;
            }
            for (int i = 0; i < ports.Count; i++)
                if (Matches(ports[i], spec)) return i;
            return int.TryParse(spec, out int index) && index >= 0 && index < ports.Count ? index : -1;
        }

        private static bool Matches(ParamRecord port, string name) =>
            string.Equals(port.Name, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(port.NickName, name, StringComparison.OrdinalIgnoreCase);

        private static StateWire Connection(string from, string output, string to, string input, Dictionary<string, StateNode> nodes)
        {
            int source = ResolvePort(nodes[from].Record.Outputs, output);
            int target = ResolvePort(nodes[to].Record.Inputs, input);
            return new StateWire { From = from, To = to,
                Output = source >= 0 ? "index:" + source : "raw:" + output,
                Input = target >= 0 ? "index:" + target : "raw:" + input };
        }

        private static bool SameConnection(StateWire a, StateWire b) =>
            a.From == b.From && a.To == b.To && a.Output == b.Output && a.Input == b.Input;

        private static bool CreatesCycle(StateWire added, List<StateWire> wires)
        {
            var pending = new Stack<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            pending.Push(added.To);
            while (pending.Count > 0)
            {
                string id = pending.Pop();
                if (id == added.From) return true;
                if (!visited.Add(id)) continue;
                foreach (StateWire wire in wires)
                    if (wire.From == id) pending.Push(wire.To);
            }
            return false;
        }
    }

    /// <summary>
    /// Provider-facing JSON Schema using objects, arrays, primitive types and enums (no conditional
    /// schema dialect). Operation-specific payload exclusivity is enforced by GraphPatchValidator.
    /// </summary>
    public static class GraphPatchSchema
    {
        public static JsonElement Build()
        {
            object text = new { type = "string" };
            object number = new { type = "number" };
            object strings = new { type = "array", items = text };
            var settings = new
            {
                type = "object",
                additionalProperties = false,
                properties = new { min = number, max = number, value = number,
                    decimals = new { type = "integer", minimum = 0, maximum = 12 },
                    text, nickname = text, flattenInputs = strings, graftInputs = strings,
                    simplifyInputs = strings, reverseInputs = strings }
            };
            return JsonSerializer.SerializeToElement(new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    ops = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            additionalProperties = false,
                            description = "add_node: node only; delete_node: nodeId only; set_param: nodeId and settings only; wire/unwire: wire only. Omit unused properties.",
                            properties = new
                            {
                                op = new { type = "string", @enum = new[] { "add_node", "delete_node", "set_param", "wire", "unwire" } },
                                nodeId = text,
                                node = new { type = "object", additionalProperties = false,
                                    properties = new { id = text, @ref = text, x = number, y = number, settings },
                                    required = new[] { "id", "ref", "x", "y" } },
                                settings,
                                wire = new { type = "object", additionalProperties = false,
                                    properties = new { fromNode = text, fromParam = text, toNode = text, toParam = text,
                                        targetMapping = new { type = "string", @enum = new[] { "none", "flatten", "graft" } },
                                        simplify = new { type = "boolean" }, reverse = new { type = "boolean" } },
                                    required = new[] { "fromNode", "fromParam", "toNode", "toParam" } }
                            },
                            required = new[] { "op" }
                        }
                    },
                    explanation = text
                },
                required = new[] { "ops", "explanation" }
            });
        }
    }
}