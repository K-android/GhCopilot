using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GhCopilot.Core.Graph;
using Grasshopper.Kernel;

namespace GhCopilot.Gh.Canvas
{
    /// <summary>
    /// Resolves input/output parameters on both standalone IGH_Param objects and IGH_Component objects,
    /// wires them via AddSource, and applies pin-level data-tree modifiers (Flatten/Graft/Simplify/Reverse).
    /// </summary>
    public static class WireConnector
    {
        private static readonly Regex IndexedNamePattern = new Regex(@"^(?<name>.+)\[(?<index>\d+)\]$", RegexOptions.Compiled);

        /// <summary>
        /// Connects a source output parameter to a target input parameter and applies optional data-tree modifiers.
        /// </summary>
        public static bool TryConnect(
            IGH_DocumentObject sourceObj,
            string fromParamSpec,
            IGH_DocumentObject targetObj,
            string toParamSpec,
            out string? error,
            string? targetMapping = null,
            bool? simplify = null,
            bool? reverse = null)
        {
            if (sourceObj is null)
            {
                error = "Source object is null.";
                return false;
            }

            if (targetObj is null)
            {
                error = "Target object is null.";
                return false;
            }

            IGH_Param? sourceParam = ResolveOutputParam(sourceObj, fromParamSpec, out string? sourceError);
            if (sourceParam is null)
            {
                error = sourceError ?? $"Could not resolve output parameter '{fromParamSpec}' on '{sourceObj.Name}'.";
                return false;
            }

            IGH_Param? targetParam = ResolveInputParam(targetObj, toParamSpec, out string? targetError);
            if (targetParam is null)
            {
                error = targetError ?? $"Could not resolve input parameter '{toParamSpec}' on '{targetObj.Name}'.";
                return false;
            }

            if (ReferenceEquals(sourceParam, targetParam))
            {
                error = $"Cannot wire parameter '{sourceParam.Name}' to itself.";
                return false;
            }

            targetParam.AddSource(sourceParam);
            ApplyTargetParamModifiers(targetParam, targetMapping, simplify, reverse);
            error = null;
            return true;
        }

        public static void ApplyTargetParamModifiers(
            IGH_Param targetParam,
            string? targetMapping,
            bool? simplify,
            bool? reverse)
        {
            if (targetParam is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(targetMapping))
            {
                switch (targetMapping!.Trim().ToLowerInvariant())
                {
                    case "flatten":
                        targetParam.DataMapping = GH_DataMapping.Flatten;
                        break;
                    case "graft":
                        targetParam.DataMapping = GH_DataMapping.Graft;
                        break;
                    case "none":
                        targetParam.DataMapping = GH_DataMapping.None;
                        break;
                }
            }

            if (simplify.HasValue)
            {
                targetParam.Simplify = simplify.Value;
            }

            if (reverse.HasValue)
            {
                targetParam.Reverse = reverse.Value;
            }
        }

        public static void ApplyNodeInputModifiers(IGH_DocumentObject obj, NodeSettings? settings)
        {
            if (obj is null || settings is null)
            {
                return;
            }

            ApplyListModifier(obj, settings.FlattenInputs, p => p.DataMapping = GH_DataMapping.Flatten);
            ApplyListModifier(obj, settings.GraftInputs, p => p.DataMapping = GH_DataMapping.Graft);
            ApplyListModifier(obj, settings.SimplifyInputs, p => p.Simplify = true);
            ApplyListModifier(obj, settings.ReverseInputs, p => p.Reverse = true);
        }

        private static void ApplyListModifier(IGH_DocumentObject obj, List<string>? paramNames, Action<IGH_Param> modifier)
        {
            if (paramNames is null || paramNames.Count == 0)
            {
                return;
            }

            foreach (string paramName in paramNames)
            {
                IGH_Param? param = ResolveInputParam(obj, paramName, out _);
                if (param != null)
                {
                    modifier(param);
                }
            }
        }

        public static IGH_Param? ResolveOutputParam(IGH_DocumentObject obj, string paramSpec, out string? error)
        {
            if (obj is IGH_Param standalone)
            {
                error = null;
                return standalone;
            }

            if (obj is IGH_Component component)
            {
                return ResolveFromParamList(component.Params.Output, paramSpec, obj.Name, "output", out error);
            }

            error = $"Object '{obj.Name}' is neither an IGH_Param nor an IGH_Component.";
            return null;
        }

        public static IGH_Param? ResolveInputParam(IGH_DocumentObject obj, string paramSpec, out string? error)
        {
            if (obj is IGH_Param standalone)
            {
                error = null;
                return standalone;
            }

            if (obj is IGH_Component component)
            {
                if (int.TryParse(paramSpec?.Trim(), out int requestedIndex) &&
                    requestedIndex >= component.Params.Input.Count &&
                    obj is IGH_VariableParameterComponent variableComponent)
                {
                    if (!TryExpandVariableInputs(component, variableComponent, requestedIndex))
                    {
                        error = $"Could not expand variable input parameters to index {requestedIndex} on '{obj.Name}'.";
                        return null;
                    }
                }

                return ResolveFromParamList(component.Params.Input, paramSpec, obj.Name, "input", out error);
            }

            error = $"Object '{obj.Name}' is neither an IGH_Param nor an IGH_Component.";
            return null;
        }

        private static IGH_Param? ResolveFromParamList(
            IList<IGH_Param> parameters,
            string? paramSpec,
            string ownerName,
            string direction,
            out string? error)
        {
            if (parameters.Count == 0)
            {
                error = $"Component '{ownerName}' has no {direction} parameters.";
                return null;
            }

            string spec = paramSpec?.Trim() ?? string.Empty;
            if (spec.Length == 0)
            {
                if (parameters.Count == 1)
                {
                    error = null;
                    return parameters[0];
                }

                error = $"Empty {direction} parameter spec for '{ownerName}' which has {parameters.Count} parameters.";
                return null;
            }

            // Handle Name[index] syntax for duplicate parameter names.
            Match indexedMatch = IndexedNamePattern.Match(spec);
            if (indexedMatch.Success)
            {
                string targetName = indexedMatch.Groups["name"].Value.Trim();
                int matchIndex = int.Parse(indexedMatch.Groups["index"].Value);
                int seen = 0;

                foreach (IGH_Param p in parameters)
                {
                    if (MatchesParamName(p, targetName))
                    {
                        if (seen == matchIndex)
                        {
                            error = null;
                            return p;
                        }
                        seen++;
                    }
                }
            }

            // Exact Name or NickName match (case-insensitive).
            foreach (IGH_Param p in parameters)
            {
                if (MatchesParamName(p, spec))
                {
                    error = null;
                    return p;
                }
            }

            // Fallback to numeric parameter index.
            if (int.TryParse(spec, out int index) && index >= 0 && index < parameters.Count)
            {
                error = null;
                return parameters[index];
            }

            error = $"Could not find {direction} parameter '{spec}' on '{ownerName}'.";
            return null;
        }

        private static bool MatchesParamName(IGH_Param param, string candidate)
        {
            return string.Equals(param.Name, candidate, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(param.NickName, candidate, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryExpandVariableInputs(
            IGH_Component component,
            IGH_VariableParameterComponent variableComponent,
            int targetIndex)
        {
            while (component.Params.Input.Count <= targetIndex)
            {
                int nextIndex = component.Params.Input.Count;
                if (!variableComponent.CanInsertParameter(GH_ParameterSide.Input, nextIndex))
                {
                    return false;
                }

                IGH_Param? created = variableComponent.CreateParameter(GH_ParameterSide.Input, nextIndex);
                if (created is null)
                {
                    return false;
                }

                if (!component.Params.RegisterInputParam(created))
                {
                    return false;
                }
            }

            variableComponent.VariableParameterMaintenance();
            component.Params.OnParametersChanged();
            return true;
        }
    }
}
