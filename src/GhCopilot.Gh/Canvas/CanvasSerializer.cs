using System;
using System.Collections.Generic;
using System.Linq;
using GhCopilot.Core.Graph;
using GhCopilot.Core.Index;
using Grasshopper.GUI.Base;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace GhCopilot.Gh.Canvas
{
    /// <summary>
    /// Serializes selected Grasshopper canvas objects, their connections, parameter settings, data-tree modifiers,
    /// external boundary wires, and RuntimeMessages into a geometry-free CanvasSnapshot respecting privacy settings.
    /// </summary>
    public static class CanvasSerializer
    {
        private const int MaxPanelTextLength = 200;

        public static CanvasSnapshot SerializeSelection(
            GH_Document doc,
            IndexStore indexStore,
            bool includeNonSelectedNeighbors)
        {
            if (doc is null) throw new ArgumentNullException(nameof(doc));
            if (indexStore is null) throw new ArgumentNullException(nameof(indexStore));

            var snapshot = new CanvasSnapshot();

            List<IGH_DocumentObject> selected = doc.SelectedObjects()
                .Where(o => o != null && !(o is GH_Group))
                .OrderBy(o => o.Attributes?.Pivot.X ?? 0f)
                .ThenBy(o => o.Attributes?.Pivot.Y ?? 0f)
                .ThenBy(o => o.InstanceGuid)
                .ToList();

            if (selected.Count == 0)
            {
                return snapshot;
            }

            var idByInstanceGuid = new Dictionary<Guid, string>();
            var selectedInstanceSet = new HashSet<Guid>();
            var includedObjects = new List<(IGH_DocumentObject Obj, string Id, bool IsSelected)>();

            for (int i = 0; i < selected.Count; i++)
            {
                IGH_DocumentObject obj = selected[i];
                string id = $"s{i + 1}";
                idByInstanceGuid[obj.InstanceGuid] = id;
                selectedInstanceSet.Add(obj.InstanceGuid);
                includedObjects.Add((obj, id, true));
            }

            if (includeNonSelectedNeighbors)
            {
                var neighborSet = new Dictionary<Guid, IGH_DocumentObject>();
                foreach (IGH_DocumentObject obj in selected)
                {
                    foreach (IGH_Param input in GetInputParams(obj))
                    {
                        foreach (IGH_Param src in input.Sources)
                        {
                            IGH_DocumentObject owner = GetTopLevelOwner(src);
                            if (!(owner is GH_Group) &&
                                !idByInstanceGuid.ContainsKey(owner.InstanceGuid) &&
                                !neighborSet.ContainsKey(owner.InstanceGuid))
                            {
                                neighborSet[owner.InstanceGuid] = owner;
                            }
                        }
                    }

                    foreach (IGH_Param output in GetOutputParams(obj))
                    {
                        foreach (IGH_Param recipient in output.Recipients)
                        {
                            IGH_DocumentObject owner = GetTopLevelOwner(recipient);
                            if (!(owner is GH_Group) &&
                                !idByInstanceGuid.ContainsKey(owner.InstanceGuid) &&
                                !neighborSet.ContainsKey(owner.InstanceGuid))
                            {
                                neighborSet[owner.InstanceGuid] = owner;
                            }
                        }
                    }
                }

                List<IGH_DocumentObject> sortedNeighbors = neighborSet.Values
                    .OrderBy(o => o.Attributes?.Pivot.X ?? 0f)
                    .ThenBy(o => o.Attributes?.Pivot.Y ?? 0f)
                    .ThenBy(o => o.InstanceGuid)
                    .ToList();

                for (int i = 0; i < sortedNeighbors.Count; i++)
                {
                    IGH_DocumentObject neighbor = sortedNeighbors[i];
                    string id = $"ctx{i + 1}";
                    idByInstanceGuid[neighbor.InstanceGuid] = id;
                    includedObjects.Add((neighbor, id, false));
                }
            }

            foreach ((IGH_DocumentObject obj, string id, bool isSelected) in includedObjects)
            {
                ComponentRecord? record = indexStore.GetByGuid(obj.ComponentGuid);

                var nodeSnap = new CanvasNodeSnapshot
                {
                    Id = id,
                    InstanceGuid = obj.InstanceGuid,
                    ComponentGuid = obj.ComponentGuid,
                    ShortId = record?.ShortId ?? string.Empty,
                    Name = obj.Name ?? string.Empty,
                    NickName = obj.NickName ?? string.Empty,
                    Category = obj.Category ?? string.Empty,
                    SubCategory = obj.SubCategory ?? string.Empty,
                    X = obj.Attributes?.Pivot.X ?? 0f,
                    Y = obj.Attributes?.Pivot.Y ?? 0f,
                    IsSelected = isSelected,
                    Settings = ExtractNodeSettings(obj),
                    Inputs = ExtractParamRecords(obj, isInput: true),
                    Outputs = ExtractParamRecords(obj, isInput: false)
                };

                if (obj is IGH_ActiveObject activeObj)
                {
                    AppendRuntimeMessages(activeObj, GH_RuntimeMessageLevel.Error, "Error", nodeSnap.RuntimeMessages);
                    AppendRuntimeMessages(activeObj, GH_RuntimeMessageLevel.Warning, "Warning", nodeSnap.RuntimeMessages);
                    AppendRuntimeMessages(activeObj, GH_RuntimeMessageLevel.Remark, "Remark", nodeSnap.RuntimeMessages);
                }

                snapshot.Nodes.Add(nodeSnap);
            }

            // Capture internal wires among included nodes.
            var seenWires = new HashSet<string>(StringComparer.Ordinal);
            foreach ((IGH_DocumentObject targetObj, string toId, _) in includedObjects)
            {
                foreach (IGH_Param input in GetInputParams(targetObj))
                {
                    foreach (IGH_Param sourceParam in input.Sources)
                    {
                        IGH_DocumentObject sourceOwner = GetTopLevelOwner(sourceParam);
                        if (!idByInstanceGuid.TryGetValue(sourceOwner.InstanceGuid, out string? fromId))
                        {
                            continue;
                        }

                        string fromParam = sourceOwner is IGH_Param ? string.Empty : (sourceParam.Name ?? string.Empty);
                        string toParam = targetObj is IGH_Param ? string.Empty : (input.Name ?? string.Empty);
                        string key = $"{fromId}:{fromParam}->{toId}:{toParam}";
                        if (seenWires.Add(key))
                        {
                            string? mapping = input.DataMapping switch
                            {
                                GH_DataMapping.Flatten => "flatten",
                                GH_DataMapping.Graft => "graft",
                                _ => null
                            };

                            snapshot.Wires.Add(new CanvasWireSnapshot
                            {
                                FromNode = fromId,
                                FromParam = fromParam,
                                ToNode = toId,
                                ToParam = toParam,
                                TargetMapping = mapping,
                                Simplify = input.Simplify ? true : (bool?)null,
                                Reverse = input.Reverse ? true : (bool?)null
                            });
                        }
                    }
                }
            }

            // Capture external boundary wires crossing into/out of the selected set (for Fix Selection preservation).
            foreach (IGH_DocumentObject selectedObj in selected)
            {
                string selectedId = idByInstanceGuid[selectedObj.InstanceGuid];

                foreach (IGH_Param input in GetInputParams(selectedObj))
                {
                    foreach (IGH_Param sourceParam in input.Sources)
                    {
                        IGH_DocumentObject sourceOwner = GetTopLevelOwner(sourceParam);
                        if (!selectedInstanceSet.Contains(sourceOwner.InstanceGuid))
                        {
                            snapshot.ExternalIncomingWires.Add(new CanvasExternalWire
                            {
                                ExternalInstanceGuid = sourceOwner.InstanceGuid,
                                ExternalParamName = sourceOwner is IGH_Param ? string.Empty : (sourceParam.Name ?? string.Empty),
                                SelectedNodeId = selectedId,
                                SelectedParamName = selectedObj is IGH_Param ? string.Empty : (input.Name ?? string.Empty)
                            });
                        }
                    }
                }

                foreach (IGH_Param output in GetOutputParams(selectedObj))
                {
                    foreach (IGH_Param recipient in output.Recipients)
                    {
                        IGH_DocumentObject targetOwner = GetTopLevelOwner(recipient);
                        if (!selectedInstanceSet.Contains(targetOwner.InstanceGuid))
                        {
                            snapshot.ExternalOutgoingWires.Add(new CanvasExternalWire
                            {
                                SelectedNodeId = selectedId,
                                SelectedParamName = selectedObj is IGH_Param ? string.Empty : (output.Name ?? string.Empty),
                                ExternalInstanceGuid = targetOwner.InstanceGuid,
                                ExternalParamName = targetOwner is IGH_Param ? string.Empty : (recipient.Name ?? string.Empty)
                            });
                        }
                    }
                }
            }

            return snapshot;
        }

        private static List<ParamRecord> ExtractParamRecords(IGH_DocumentObject obj, bool isInput)
        {
            var list = new List<ParamRecord>();
            IEnumerable<IGH_Param> parameters = isInput ? GetInputParams(obj) : GetOutputParams(obj);

            if (!isInput && obj is GH_NumberSlider)
            {
                list.Add(new ParamRecord { Name = "Number", NickName = "N", TypeName = "Number", Access = "item" });
                return list;
            }

            if (isInput && obj is GH_NumberSlider)
            {
                return list;
            }

            foreach (IGH_Param p in parameters)
            {
                list.Add(new ParamRecord
                {
                    Name = p.Name ?? string.Empty,
                    NickName = p.NickName ?? string.Empty,
                    TypeName = string.IsNullOrWhiteSpace(p.TypeName) ? "Generic" : p.TypeName,
                    Access = p.Access.ToString().ToLowerInvariant(),
                    Optional = p.Optional
                });
            }

            return list;
        }

        private static NodeSettings? ExtractNodeSettings(IGH_DocumentObject obj)
        {
            NodeSettings? settings = null;

            if (obj is GH_NumberSlider slider)
            {
                int decimals = slider.Slider.Type == GH_SliderAccuracy.Integer
                    ? 0
                    : slider.Slider.DecimalPlaces;

                settings = new NodeSettings
                {
                    Min = (double)slider.Slider.Minimum,
                    Max = (double)slider.Slider.Maximum,
                    Value = (double)slider.CurrentValue,
                    Decimals = decimals,
                    Nickname = string.IsNullOrWhiteSpace(slider.NickName) ? null : slider.NickName
                };
            }
            else if (obj is GH_Panel panel)
            {
                string raw = panel.UserText ?? string.Empty;
                string truncated = raw.Length > MaxPanelTextLength
                    ? raw.Substring(0, MaxPanelTextLength) + "..."
                    : raw;

                settings = new NodeSettings
                {
                    Text = truncated,
                    Nickname = string.IsNullOrWhiteSpace(panel.NickName) ? null : panel.NickName
                };
            }
            else if (!string.IsNullOrWhiteSpace(obj.NickName) &&
                     !string.Equals(obj.NickName, obj.Name, StringComparison.OrdinalIgnoreCase))
            {
                settings = new NodeSettings { Nickname = obj.NickName };
            }

            foreach (IGH_Param input in GetInputParams(obj))
            {
                string paramName = input.Name ?? string.Empty;
                if (input.DataMapping == GH_DataMapping.Flatten)
                {
                    settings ??= new NodeSettings();
                    settings.FlattenInputs ??= new List<string>();
                    settings.FlattenInputs.Add(paramName);
                }
                else if (input.DataMapping == GH_DataMapping.Graft)
                {
                    settings ??= new NodeSettings();
                    settings.GraftInputs ??= new List<string>();
                    settings.GraftInputs.Add(paramName);
                }

                if (input.Simplify)
                {
                    settings ??= new NodeSettings();
                    settings.SimplifyInputs ??= new List<string>();
                    settings.SimplifyInputs.Add(paramName);
                }

                if (input.Reverse)
                {
                    settings ??= new NodeSettings();
                    settings.ReverseInputs ??= new List<string>();
                    settings.ReverseInputs.Add(paramName);
                }
            }

            return settings;
        }

        private static void AppendRuntimeMessages(
            IGH_ActiveObject activeObj,
            GH_RuntimeMessageLevel level,
            string levelName,
            List<CanvasRuntimeMessage> target)
        {
            IList<string>? messages = activeObj.RuntimeMessages(level);
            if (messages is null)
            {
                return;
            }

            foreach (string msg in messages)
            {
                if (!string.IsNullOrWhiteSpace(msg))
                {
                    target.Add(new CanvasRuntimeMessage
                    {
                        Level = levelName,
                        Message = msg
                    });
                }
            }
        }

        private static IEnumerable<IGH_Param> GetInputParams(IGH_DocumentObject obj)
        {
            if (obj is IGH_Component comp)
            {
                return comp.Params.Input;
            }

            if (obj is IGH_Param param)
            {
                return new[] { param };
            }

            return Array.Empty<IGH_Param>();
        }

        private static IEnumerable<IGH_Param> GetOutputParams(IGH_DocumentObject obj)
        {
            if (obj is IGH_Component comp)
            {
                return comp.Params.Output;
            }

            if (obj is IGH_Param param)
            {
                return new[] { param };
            }

            return Array.Empty<IGH_Param>();
        }

        private static IGH_DocumentObject GetTopLevelOwner(IGH_Param param)
        {
            return param.Attributes?.GetTopLevel?.DocObject ?? param;
        }
    }
}
