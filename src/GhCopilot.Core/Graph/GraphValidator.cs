using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GhCopilot.Core.Index;

namespace GhCopilot.Core.Graph
{
    public enum ValidationErrorCode
    {
        EmptyGraph,
        TooManyNodes,
        CoordinateOutOfRange,
        DuplicateNodeId,
        UnknownComponentRef,
        UnknownNodeInWire,
        UnknownParameter,
        WireDirectionError,
        TypeMismatch,
        AccessMismatch,
        CycleDetected,
        InvalidScriptSpec,
        UnsafeScriptCode
    }

    public sealed class ValidationIssue
    {
        public ValidationIssue(ValidationErrorCode code, string message, string? nodeId = null, int? wireIndex = null)
        {
            Code = code;
            Message = message;
            NodeId = nodeId;
            WireIndex = wireIndex;
        }

        public ValidationErrorCode Code { get; }
        public string Message { get; }
        public string? NodeId { get; }
        public int? WireIndex { get; }

        public override string ToString() => $"[{Code}] {Message}";
    }

    public sealed class ValidationResult
    {
        public bool IsValid => Errors.Count == 0;
        public List<ValidationIssue> Errors { get; } = new List<ValidationIssue>();
        public List<ValidationIssue> Warnings { get; } = new List<ValidationIssue>();
    }

    /// <summary>
    /// Validates a GraphSpec (native nodes/wires and optional Python 3 script block) against a retrieved component shortlist.
    /// </summary>
    public static class GraphValidator
    {
        public const int MaxNodeCount = 200;
        public const float MaxCoordinateMagnitude = 25000f;
        public const string ScriptNodeId = "script";

        private static readonly Regex IndexedNamePattern = new Regex(@"^(?<name>.+)\[(?<index>\d+)\]$", RegexOptions.Compiled);

        private static readonly (Regex Pattern, string Reason)[] UnsafeScriptPatterns =
        {
            (new Regex(@"\b(?:import|from)\s+(?:os|sys|subprocess|socket|urllib|requests|http|ftplib|shutil|pathlib|ctypes|winreg|pickle|marshal|importlib|webbrowser|tempfile|glob|multiprocessing|threading)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Restricted Python module import (OS, filesystem, network, process, or dynamic loading)."),
            (new Regex(@"\b(?:__import__|eval|exec|compile|open|execfile|getattr|setattr)\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Dynamic code execution, reflection, or file I/O call is forbidden."),
            (new Regex(@"\bSystem\.(?:IO|Net|Diagnostics|Reflection|Runtime\.InteropServices)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), ".NET filesystem, network, process, reflection, or interop namespace is forbidden."),
            (new Regex(@"\bRhino\.RhinoApp\.RunScript\b|\bRhinoApp\.RunScript\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Executing arbitrary Rhino command macros from generated script is forbidden.")
        };

        public static ValidationResult Validate(
            GraphSpec spec,
            IReadOnlyDictionary<string, ComponentRecord> shortlist,
            IReadOnlyDictionary<string, ComponentRecord>? externalNodes = null,
            int maxNodeCount = MaxNodeCount)
        {
            if (spec is null)
            {
                throw new ArgumentNullException(nameof(spec));
            }

            if (shortlist is null)
            {
                throw new ArgumentNullException(nameof(shortlist));
            }

            int effectiveMaxNodes = maxNodeCount < 1 ? 1 : maxNodeCount;
            var result = new ValidationResult();
            bool hasScript = spec.Script != null;

            if (spec.Nodes.Count == 0 && !hasScript)
            {
                result.Errors.Add(new ValidationIssue(
                    ValidationErrorCode.EmptyGraph,
                    "GraphSpec contains zero nodes and no script block."));
                return result;
            }

            if (hasScript)
            {
                ValidateScript(spec.Script!, result);
            }

            if (spec.Nodes.Count > effectiveMaxNodes)
            {
                result.Errors.Add(new ValidationIssue(
                    ValidationErrorCode.TooManyNodes,
                    $"GraphSpec has {spec.Nodes.Count} nodes, exceeding limit of {effectiveMaxNodes}."));
            }

            var nodesById = new Dictionary<string, NodeSpec>(StringComparer.Ordinal);
            var recordsByNodeId = new Dictionary<string, ComponentRecord>(StringComparer.Ordinal);

            if (hasScript)
            {
                var virtualScriptRecord = BuildVirtualScriptRecord(spec.Script!);
                nodesById[ScriptNodeId] = new NodeSpec { Id = ScriptNodeId, Ref = ScriptNodeId, X = 380f, Y = 200f };
                recordsByNodeId[ScriptNodeId] = virtualScriptRecord;
            }

            if (externalNodes != null)
            {
                foreach (KeyValuePair<string, ComponentRecord> kvp in externalNodes)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Key) && !nodesById.ContainsKey(kvp.Key))
                    {
                        nodesById[kvp.Key] = new NodeSpec { Id = kvp.Key, Ref = kvp.Key, X = 0f, Y = 0f };
                        recordsByNodeId[kvp.Key] = kvp.Value;
                    }
                }
            }

            foreach (NodeSpec node in spec.Nodes)
            {
                if (string.IsNullOrWhiteSpace(node.Id))
                {
                    result.Errors.Add(new ValidationIssue(
                        ValidationErrorCode.DuplicateNodeId,
                        "Node id must not be empty.",
                        nodeId: node.Id));
                    continue;
                }

                if (nodesById.ContainsKey(node.Id))
                {
                    result.Errors.Add(new ValidationIssue(
                        ValidationErrorCode.DuplicateNodeId,
                        $"Duplicate node id '{node.Id}'.",
                        nodeId: node.Id));
                    continue;
                }

                nodesById[node.Id] = node;

                if (!IsFinite(node.X) || !IsFinite(node.Y) ||
                    Math.Abs(node.X) > MaxCoordinateMagnitude ||
                    Math.Abs(node.Y) > MaxCoordinateMagnitude)
                {
                    result.Errors.Add(new ValidationIssue(
                        ValidationErrorCode.CoordinateOutOfRange,
                        $"Node '{node.Id}' coordinates ({node.X}, {node.Y}) must be finite and within [-{MaxCoordinateMagnitude}, {MaxCoordinateMagnitude}].",
                        nodeId: node.Id));
                }

                if (string.IsNullOrWhiteSpace(node.Ref) || !shortlist.TryGetValue(node.Ref, out ComponentRecord? record))
                {
                    result.Errors.Add(new ValidationIssue(
                        ValidationErrorCode.UnknownComponentRef,
                        $"Node '{node.Id}' references unknown shortlist ref '{node.Ref}'.",
                        nodeId: node.Id));
                    continue;
                }

                recordsByNodeId[node.Id] = record;
            }

            var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string id in nodesById.Keys)
            {
                adjacency[id] = new List<string>();
            }

            for (int i = 0; i < spec.Wires.Count; i++)
            {
                WireSpec wire = spec.Wires[i];
                bool hasFrom = !string.IsNullOrWhiteSpace(wire.FromNode) && nodesById.ContainsKey(wire.FromNode);
                bool hasTo = !string.IsNullOrWhiteSpace(wire.ToNode) && nodesById.ContainsKey(wire.ToNode);

                if (!hasFrom)
                {
                    result.Errors.Add(new ValidationIssue(
                        ValidationErrorCode.UnknownNodeInWire,
                        $"Wire {i}: unknown source node '{wire.FromNode}'.",
                        wireIndex: i));
                }

                if (!hasTo)
                {
                    result.Errors.Add(new ValidationIssue(
                        ValidationErrorCode.UnknownNodeInWire,
                        $"Wire {i}: unknown target node '{wire.ToNode}'.",
                        wireIndex: i));
                }

                if (!hasFrom || !hasTo)
                {
                    continue;
                }

                if (externalNodes != null && externalNodes.ContainsKey(wire.ToNode))
                {
                    result.Errors.Add(new ValidationIssue(
                        ValidationErrorCode.WireDirectionError,
                        $"Wire {i}: cannot wire into external canvas node '{wire.ToNode}'.",
                        nodeId: wire.ToNode,
                        wireIndex: i));
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(wire.TargetMapping))
                {
                    string mapping = wire.TargetMapping!.Trim().ToLowerInvariant();
                    if (mapping != "none" && mapping != "flatten" && mapping != "graft")
                    {
                        result.Errors.Add(new ValidationIssue(
                            ValidationErrorCode.UnknownParameter,
                            $"Wire {i}: invalid targetMapping '{wire.TargetMapping}' (must be none, flatten, or graft).",
                            wireIndex: i));
                    }
                }

                adjacency[wire.FromNode].Add(wire.ToNode);

                if (!recordsByNodeId.TryGetValue(wire.FromNode, out ComponentRecord? sourceRecord) ||
                    !recordsByNodeId.TryGetValue(wire.ToNode, out ComponentRecord? targetRecord))
                {
                    continue;
                }

                bool sourceStandalone = sourceRecord.IsStandaloneParam ||
                                        string.Equals(sourceRecord.Name, "Panel", StringComparison.OrdinalIgnoreCase);
                bool targetStandalone = targetRecord.IsStandaloneParam ||
                                        string.Equals(targetRecord.Name, "Panel", StringComparison.OrdinalIgnoreCase);

                ParamRecord? sourceParam = ResolveParam(sourceRecord.Outputs, wire.FromParam, sourceStandalone);
                if (sourceParam is null)
                {
                    bool matchesInput = !string.IsNullOrWhiteSpace(wire.FromParam) &&
                                        ResolveParam(sourceRecord.Inputs, wire.FromParam, sourceStandalone) != null;

                    if (sourceRecord.Outputs.Count == 0 || matchesInput)
                    {
                        result.Errors.Add(new ValidationIssue(
                            ValidationErrorCode.WireDirectionError,
                            $"Wire {i}: cannot wire out of input or output-less parameter '{wire.FromParam}' on '{sourceRecord.Name}' (node '{wire.FromNode}').",
                            nodeId: wire.FromNode,
                            wireIndex: i));
                    }
                    else
                    {
                        result.Errors.Add(new ValidationIssue(
                            ValidationErrorCode.UnknownParameter,
                            $"Wire {i}: output parameter '{wire.FromParam}' does not exist on '{sourceRecord.Name}' (node '{wire.FromNode}').",
                            nodeId: wire.FromNode,
                            wireIndex: i));
                    }
                }

                ParamRecord? targetParam = ResolveParam(targetRecord.Inputs, wire.ToParam, targetStandalone);
                if (targetParam is null)
                {
                    bool matchesOutput = !string.IsNullOrWhiteSpace(wire.ToParam) &&
                                         ResolveParam(targetRecord.Outputs, wire.ToParam, targetStandalone) != null;

                    if (targetRecord.Inputs.Count == 0 || matchesOutput)
                    {
                        result.Errors.Add(new ValidationIssue(
                            ValidationErrorCode.WireDirectionError,
                            $"Wire {i}: cannot wire into output or input-less parameter '{wire.ToParam}' on '{targetRecord.Name}' (node '{wire.ToNode}').",
                            nodeId: wire.ToNode,
                            wireIndex: i));
                    }
                    else
                    {
                        result.Errors.Add(new ValidationIssue(
                            ValidationErrorCode.UnknownParameter,
                            $"Wire {i}: input parameter '{wire.ToParam}' does not exist on '{targetRecord.Name}' (node '{wire.ToNode}').",
                            nodeId: wire.ToNode,
                            wireIndex: i));
                    }
                }

                if (sourceParam is null || targetParam is null)
                {
                    continue;
                }

                if (!TypeCompat.AreCompatible(sourceParam.TypeName, targetParam.TypeName))
                {
                    result.Errors.Add(new ValidationIssue(
                        ValidationErrorCode.TypeMismatch,
                        $"Wire {i}: incompatible types '{sourceParam.TypeName}' ({wire.FromNode}.{sourceParam.Name}) -> '{targetParam.TypeName}' ({wire.ToNode}.{targetParam.Name}).",
                        wireIndex: i));
                }

                if (!string.Equals(sourceParam.Access, targetParam.Access, StringComparison.OrdinalIgnoreCase))
                {
                    result.Warnings.Add(new ValidationIssue(
                        ValidationErrorCode.AccessMismatch,
                        $"Wire {i}: access mismatch '{sourceParam.Access}' ({wire.FromNode}.{sourceParam.Name}) -> '{targetParam.Access}' ({wire.ToNode}.{targetParam.Name}).",
                        wireIndex: i));
                }
            }

            if (HasCycle(adjacency, out string? cycleMessage))
            {
                result.Errors.Add(new ValidationIssue(
                    ValidationErrorCode.CycleDetected,
                    cycleMessage ?? "Cycle detected in graph wires."));
            }

            return result;
        }

        public static string? ValidateScriptCodeOnly(string? code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return "Script block must contain non-empty Python 3 code.";
            }

            foreach ((Regex pattern, string reason) in UnsafeScriptPatterns)
            {
                Match match = pattern.Match(code!);
                if (match.Success)
                {
                    return $"Unsafe script token '{match.Value.Trim()}': {reason}";
                }
            }

            return null;
        }

        private static void ValidateScript(ScriptSpec script, ValidationResult result)
        {
            string? codeError = ValidateScriptCodeOnly(script.Code);
            if (codeError != null)
            {
                ValidationErrorCode errCode = string.IsNullOrWhiteSpace(script.Code)
                    ? ValidationErrorCode.InvalidScriptSpec
                    : ValidationErrorCode.UnsafeScriptCode;
                result.Errors.Add(new ValidationIssue(
                    errCode,
                    codeError,
                    nodeId: ScriptNodeId));
            }

            if (script.Outputs is null || script.Outputs.Count == 0)
            {
                result.Errors.Add(new ValidationIssue(
                    ValidationErrorCode.InvalidScriptSpec,
                    "Script block must define at least one output parameter.",
                    nodeId: ScriptNodeId));
            }
            else
            {
                for (int i = 0; i < script.Outputs.Count; i++)
                {
                    if (string.IsNullOrWhiteSpace(script.Outputs[i].Name))
                    {
                        result.Errors.Add(new ValidationIssue(
                            ValidationErrorCode.InvalidScriptSpec,
                            $"Script output at index {i} must have a non-empty name.",
                            nodeId: ScriptNodeId));
                    }
                }
            }

            if (script.Inputs != null)
            {
                for (int i = 0; i < script.Inputs.Count; i++)
                {
                    ScriptInputSpec input = script.Inputs[i];
                    if (string.IsNullOrWhiteSpace(input.Name))
                    {
                        result.Errors.Add(new ValidationIssue(
                            ValidationErrorCode.InvalidScriptSpec,
                            $"Script input at index {i} must have a non-empty name.",
                            nodeId: ScriptNodeId));
                    }

                    string access = input.Access?.Trim().ToLowerInvariant() ?? "item";
                    if (access != "item" && access != "list" && access != "tree")
                    {
                        result.Errors.Add(new ValidationIssue(
                            ValidationErrorCode.InvalidScriptSpec,
                            $"Script input '{input.Name}' has invalid access '{input.Access}' (must be item, list, or tree).",
                            nodeId: ScriptNodeId));
                    }
                }
            }
        }

        private static ComponentRecord BuildVirtualScriptRecord(ScriptSpec script)
        {
            var record = new ComponentRecord
            {
                ShortId = ScriptNodeId,
                Name = "Python 3 Script",
                IsHydrated = true
            };

            if (script.Inputs != null)
            {
                foreach (ScriptInputSpec input in script.Inputs)
                {
                    record.Inputs.Add(new ParamRecord
                    {
                        Name = input.Name,
                        NickName = input.Name,
                        TypeName = string.IsNullOrWhiteSpace(input.Type) ? "Generic" : input.Type,
                        Access = string.IsNullOrWhiteSpace(input.Access) ? "item" : input.Access.ToLowerInvariant()
                    });
                }
            }

            if (script.Outputs != null)
            {
                foreach (ScriptOutputSpec output in script.Outputs)
                {
                    record.Outputs.Add(new ParamRecord
                    {
                        Name = output.Name,
                        NickName = output.Name,
                        TypeName = string.IsNullOrWhiteSpace(output.Type) ? "Generic" : output.Type,
                        Access = "item"
                    });
                }
            }

            return record;
        }

        private static ParamRecord? ResolveParam(IReadOnlyList<ParamRecord> parameters, string? paramSpec, bool allowTypeNameMatch = false)
        {
            if (parameters.Count == 0)
            {
                return null;
            }

            string spec = paramSpec?.Trim() ?? string.Empty;
            if (spec.Length == 0)
            {
                return parameters.Count == 1 ? parameters[0] : null;
            }

            Match indexedMatch = IndexedNamePattern.Match(spec);
            if (indexedMatch.Success)
            {
                string targetName = indexedMatch.Groups["name"].Value.Trim();
                int matchIndex = int.Parse(indexedMatch.Groups["index"].Value);
                int seen = 0;

                foreach (ParamRecord p in parameters)
                {
                    if (MatchesParamName(p, targetName))
                    {
                        if (seen == matchIndex)
                        {
                            return p;
                        }
                        seen++;
                    }
                }
            }

            foreach (ParamRecord p in parameters)
            {
                if (MatchesParamName(p, spec))
                {
                    return p;
                }
            }

            if (allowTypeNameMatch &&
                parameters.Count == 1 &&
                string.Equals(parameters[0].TypeName, spec, StringComparison.OrdinalIgnoreCase))
            {
                return parameters[0];
            }

            if (int.TryParse(spec, out int index) && index >= 0 && index < parameters.Count)
            {
                return parameters[index];
            }

            return null;
        }

        private static bool MatchesParamName(ParamRecord param, string candidate)
        {
            return string.Equals(param.Name, candidate, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(param.NickName, candidate, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasCycle(Dictionary<string, List<string>> adjacency, out string? message)
        {
            // 0 = unvisited, 1 = visiting (on active stack), 2 = visited
            var state = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string nodeId in adjacency.Keys)
            {
                state[nodeId] = 0;
            }

            foreach (string nodeId in adjacency.Keys)
            {
                if (state[nodeId] == 0 && DfsHasCycle(nodeId, adjacency, state, out message))
                {
                    return true;
                }
            }

            message = null;
            return false;
        }

        private static bool DfsHasCycle(
            string current,
            Dictionary<string, List<string>> adjacency,
            Dictionary<string, int> state,
            out string? message)
        {
            state[current] = 1;

            foreach (string next in adjacency[current])
            {
                if (state[next] == 1)
                {
                    message = $"Cycle detected involving '{current}' -> '{next}'.";
                    return true;
                }

                if (state[next] == 0 && DfsHasCycle(next, adjacency, state, out message))
                {
                    return true;
                }
            }

            state[current] = 2;
            message = null;
            return false;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
