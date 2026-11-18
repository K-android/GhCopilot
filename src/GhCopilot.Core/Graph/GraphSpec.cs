using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GhCopilot.Core.Graph
{
    /// <summary>
    /// Structured graph specification produced by the LLM (or hardcoded in Phase 1) and spawned onto the Grasshopper canvas.
    /// </summary>
    public sealed class GraphSpec
    {
        [JsonPropertyName("nodes")]
        public List<NodeSpec> Nodes { get; set; } = new List<NodeSpec>();

        [JsonPropertyName("wires")]
        public List<WireSpec> Wires { get; set; } = new List<WireSpec>();

        [JsonPropertyName("script")]
        public ScriptSpec? Script { get; set; }

        [JsonPropertyName("explanation")]
        public string Explanation { get; set; } = string.Empty;
    }

    /// <summary>
    /// Single node placement referencing a short proxy ID rather than a raw GUID.
    /// </summary>
    public sealed class NodeSpec
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("ref")]
        public string Ref { get; set; } = string.Empty;

        [JsonPropertyName("x")]
        public float X { get; set; }

        [JsonPropertyName("y")]
        public float Y { get; set; }

        [JsonPropertyName("settings")]
        public NodeSettings? Settings { get; set; }
    }

    /// <summary>
    /// Optional parameter configuration for sliders, panels, and display nicknames.
    /// </summary>
    public sealed class NodeSettings
    {
        [JsonPropertyName("min")]
        public double? Min { get; set; }

        [JsonPropertyName("max")]
        public double? Max { get; set; }

        [JsonPropertyName("value")]
        public double? Value { get; set; }

        [JsonPropertyName("decimals")]
        public int? Decimals { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("nickname")]
        public string? Nickname { get; set; }

        [JsonPropertyName("flattenInputs")]
        public List<string>? FlattenInputs { get; set; }

        [JsonPropertyName("graftInputs")]
        public List<string>? GraftInputs { get; set; }

        [JsonPropertyName("simplifyInputs")]
        public List<string>? SimplifyInputs { get; set; }

        [JsonPropertyName("reverseInputs")]
        public List<string>? ReverseInputs { get; set; }
    }

    /// <summary>
    /// Directed wire connection from a source node output parameter to a target node input parameter,
    /// with optional pin-level data-tree modifiers on the target input.
    /// </summary>
    public sealed class WireSpec
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
    /// Optional Python 3 script component specification used in Phase 6+.
    /// </summary>
    public sealed class ScriptSpec
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = string.Empty;

        [JsonPropertyName("inputs")]
        public List<ScriptInputSpec> Inputs { get; set; } = new List<ScriptInputSpec>();

        [JsonPropertyName("outputs")]
        public List<ScriptOutputSpec> Outputs { get; set; } = new List<ScriptOutputSpec>();
    }

    /// <summary>
    /// Typed input parameter specification for a Python 3 script node.
    /// </summary>
    public sealed class ScriptInputSpec
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("access")]
        public string Access { get; set; } = "item";
    }

    /// <summary>
    /// Typed output parameter specification for a Python 3 script node.
    /// </summary>
    public sealed class ScriptOutputSpec
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;
    }
}
