using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using GhCopilot.Core;
using GhCopilot.Core.Graph;
using GhCopilot.Gh.Script;
using Grasshopper;
using Grasshopper.GUI.Base;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace GhCopilot.Gh.Canvas
{
    /// <summary>
    /// Instantiates a GraphSpec onto a Grasshopper document inside a colour-coded preview GH_Group,
    /// supporting pin-level data-tree modifiers, wiring to existing canvas selections, and external Fix rewires.
    /// Must be invoked on the Rhino UI thread when no solution is executing (via UiThread.MutateActiveDocument).
    /// </summary>
    public static class CanvasSpawner
    {
        public static GroupPreview Spawn(
            GH_Document doc,
            GraphSpec spec,
            Func<string, IGH_ObjectProxy?> proxyResolver,
            out List<string> warnings,
            IReadOnlyList<Guid>? replacedOriginalGuids = null,
            CanvasSnapshot? selectionSnapshot = null,
            bool preserveExternalWiresOnFix = false)
        {
            if (doc is null) throw new ArgumentNullException(nameof(doc));
            if (spec is null) throw new ArgumentNullException(nameof(spec));
            if (proxyResolver is null) throw new ArgumentNullException(nameof(proxyResolver));

            warnings = new List<string>();
            var spawnedById = new Dictionary<string, IGH_DocumentObject>(StringComparer.Ordinal);
            var externalByAlias = BuildExternalLookup(doc, selectionSnapshot);
            var addedObjects = new List<IGH_DocumentObject>();
            var nodeGuids = new List<Guid>();

            foreach (NodeSpec node in spec.Nodes)
            {
                IGH_ObjectProxy? proxy = proxyResolver(node.Ref);
                if (proxy is null)
                {
                    throw new InvalidOperationException($"Could not resolve object proxy for ref '{node.Ref}' (node '{node.Id}').");
                }

                IGH_DocumentObject instance = proxy.CreateInstance();
                if (instance is null)
                {
                    throw new InvalidOperationException($"Proxy '{proxy.Desc.Name}' ({proxy.Guid}) returned null from CreateInstance().");
                }

                ConfigureObject(instance, node);
                WireConnector.ApplyNodeInputModifiers(instance, node.Settings);

                if (instance.Attributes == null)
                {
                    instance.CreateAttributes();
                }

                doc.AddObject(instance, false);

                if (instance.Attributes != null)
                {
                    instance.Attributes.Pivot = new PointF(node.X, node.Y);
                    instance.Attributes.ExpireLayout();
                    instance.Attributes.PerformLayout();
                }

                spawnedById[node.Id] = instance;
                addedObjects.Add(instance);
                nodeGuids.Add(instance.InstanceGuid);
            }

            if (spec.Script != null && !string.IsNullOrWhiteSpace(spec.Script.Code))
            {
                float scriptX = spec.Nodes.Count > 0 ? spec.Nodes.Max(n => n.X) + 220f : 380f;
                float scriptY = spec.Nodes.Count > 0 ? spec.Nodes.Average(n => n.Y) : 200f;

                IGH_DocumentObject scriptObj = PythonScriptSpawner.Spawn(
                    doc,
                    spec.Script,
                    new PointF(scriptX, scriptY),
                    out List<string> scriptWarnings);

                warnings.AddRange(scriptWarnings);
                spawnedById[GraphValidator.ScriptNodeId] = scriptObj;
                addedObjects.Add(scriptObj);
                nodeGuids.Add(scriptObj.InstanceGuid);
            }

            foreach (WireSpec wire in spec.Wires)
            {
                IGH_DocumentObject? fromObj = null;
                if (!spawnedById.TryGetValue(wire.FromNode, out fromObj) &&
                    !externalByAlias.TryGetValue(wire.FromNode, out fromObj))
                {
                    warnings.Add($"Wire skipped: unknown source node '{wire.FromNode}'.");
                    continue;
                }

                if (!spawnedById.TryGetValue(wire.ToNode, out IGH_DocumentObject? toObj))
                {
                    warnings.Add($"Wire skipped: unknown target node '{wire.ToNode}'.");
                    continue;
                }

                if (!WireConnector.TryConnect(
                        fromObj,
                        wire.FromParam,
                        toObj,
                        wire.ToParam,
                        out string? wireError,
                        wire.TargetMapping,
                        wire.Simplify,
                        wire.Reverse) &&
                    wireError != null)
                {
                    warnings.Add(wireError);
                }
            }

            // For Fix Selection: re-attach external upstream wires into preview root nodes and plan downstream rewires for Accept.
            List<PendingExternalRewire>? pendingRewires = null;
            if (preserveExternalWiresOnFix && selectionSnapshot != null && addedObjects.Count > 0)
            {
                AttachExternalIncomingWires(doc, selectionSnapshot, spawnedById, addedObjects, warnings);
                pendingRewires = BuildPendingOutgoingRewires(selectionSnapshot, addedObjects);
            }

            var group = new GH_Group();
            group.CreateAttributes();
            group.NickName = $"[Preview] {PluginConstants.ProductName}";
            group.Colour = GroupPreview.PreviewColour;
            group.Border = GH_GroupBorder.Box;
            doc.AddObject(group, false);

            foreach (Guid nodeGuid in nodeGuids)
            {
                group.AddObject(nodeGuid);
            }

            group.ExpireCaches();
            addedObjects.Add(group);

            doc.UndoUtil.RecordAddObjectEvent("GH Copilot: Spawn Preview", addedObjects);

            doc.NewSolution(false);
            Instances.ActiveCanvas?.Invalidate();

            return new GroupPreview(group.InstanceGuid, nodeGuids, replacedOriginalGuids, pendingRewires);
        }

        public static List<string> CollectRuntimeMessages(GH_Document doc, GroupPreview preview)
        {
            if (doc is null) throw new ArgumentNullException(nameof(doc));
            if (preview is null) throw new ArgumentNullException(nameof(preview));

            var messages = new List<string>();
            foreach (Guid nodeGuid in preview.NodeInstanceGuids)
            {
                IGH_DocumentObject? obj = doc.FindObject(nodeGuid, true);
                if (obj is IGH_ActiveObject activeObj)
                {
                    IList<string>? errors = activeObj.RuntimeMessages(GH_RuntimeMessageLevel.Error);
                    if (errors != null)
                    {
                        foreach (string msg in errors)
                        {
                            messages.Add($"[Error] {obj.Name} ({obj.NickName}): {msg}");
                        }
                    }

                    IList<string>? warnings = activeObj.RuntimeMessages(GH_RuntimeMessageLevel.Warning);
                    if (warnings != null)
                    {
                        foreach (string msg in warnings)
                        {
                            messages.Add($"[Warning] {obj.Name} ({obj.NickName}): {msg}");
                        }
                    }
                }
            }

            return messages;
        }

        public static GraphSpec CreatePhase1DemoSpec()
        {
            return new GraphSpec
            {
                Explanation = "Phase 1 hardcoded demo: adds two sliders (12.5 + 7.5) and displays the result in a panel.",
                Nodes = new List<NodeSpec>
                {
                    new NodeSpec
                    {
                        Id = "n1",
                        Ref = "c_slider",
                        X = 160f,
                        Y = 180f,
                        Settings = new NodeSettings
                        {
                            Min = 0.0,
                            Max = 50.0,
                            Value = 12.5,
                            Decimals = 1,
                            Nickname = "Input A"
                        }
                    },
                    new NodeSpec
                    {
                        Id = "n2",
                        Ref = "c_slider",
                        X = 160f,
                        Y = 245f,
                        Settings = new NodeSettings
                        {
                            Min = 0.0,
                            Max = 50.0,
                            Value = 7.5,
                            Decimals = 1,
                            Nickname = "Input B"
                        }
                    },
                    new NodeSpec
                    {
                        Id = "n3",
                        Ref = "c_add",
                        X = 390f,
                        Y = 212f,
                        Settings = new NodeSettings
                        {
                            Nickname = "Add"
                        }
                    },
                    new NodeSpec
                    {
                        Id = "n4",
                        Ref = "c_panel",
                        X = 530f,
                        Y = 195f,
                        Settings = new NodeSettings
                        {
                            Text = string.Empty,
                            Nickname = "Sum Result"
                        }
                    }
                },
                Wires = new List<WireSpec>
                {
                    new WireSpec { FromNode = "n1", FromParam = string.Empty, ToNode = "n3", ToParam = "A" },
                    new WireSpec { FromNode = "n2", FromParam = string.Empty, ToNode = "n3", ToParam = "B" },
                    new WireSpec { FromNode = "n3", FromParam = "Result", ToNode = "n4", ToParam = string.Empty }
                }
            };
        }

        public static IGH_ObjectProxy? ResolveDemoProxy(string shortRef)
        {
            IList<IGH_ObjectProxy>? proxies = Instances.ComponentServer?.ObjectProxies;
            if (proxies is null)
            {
                return null;
            }

            switch (shortRef)
            {
                case "c_slider":
                    return proxies.FirstOrDefault(p => !p.Obsolete && p.Type == typeof(GH_NumberSlider))
                        ?? proxies.FirstOrDefault(p => !p.Obsolete && string.Equals(p.Desc.Name, "Number Slider", StringComparison.OrdinalIgnoreCase));

                case "c_add":
                    return proxies.FirstOrDefault(p =>
                               !p.Obsolete &&
                               string.Equals(p.Desc.Name, "Addition", StringComparison.OrdinalIgnoreCase) &&
                               (string.Equals(p.Desc.Category, "Maths", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(p.Desc.Category, "Math", StringComparison.OrdinalIgnoreCase)))
                        ?? proxies.FirstOrDefault(p => !p.Obsolete && string.Equals(p.Desc.Name, "Addition", StringComparison.OrdinalIgnoreCase));

                case "c_panel":
                    return proxies.FirstOrDefault(p => !p.Obsolete && p.Type == typeof(GH_Panel))
                        ?? proxies.FirstOrDefault(p => !p.Obsolete && string.Equals(p.Desc.Name, "Panel", StringComparison.OrdinalIgnoreCase));

                default:
                    return null;
            }
        }

        private static Dictionary<string, IGH_DocumentObject> BuildExternalLookup(
            GH_Document doc,
            CanvasSnapshot? selectionSnapshot)
        {
            var map = new Dictionary<string, IGH_DocumentObject>(StringComparer.OrdinalIgnoreCase);
            if (selectionSnapshot is null)
            {
                return map;
            }

            List<CanvasNodeSnapshot> selected = selectionSnapshot.Nodes.Where(n => n.IsSelected).ToList();
            for (int i = 0; i < selected.Count; i++)
            {
                IGH_DocumentObject? liveObj = doc.FindObject(selected[i].InstanceGuid, true);
                if (liveObj is null)
                {
                    continue;
                }

                map[$"${selected[i].Id}"] = liveObj;
                if (i == 0)
                {
                    map["$selected"] = liveObj;
                }
            }

            return map;
        }

        private static void AttachExternalIncomingWires(
            GH_Document doc,
            CanvasSnapshot snapshot,
            Dictionary<string, IGH_DocumentObject> spawnedById,
            List<IGH_DocumentObject> addedObjects,
            List<string> warnings)
        {
            if (snapshot.ExternalIncomingWires.Count == 0)
            {
                return;
            }

            IGH_DocumentObject? primaryTarget = addedObjects.FirstOrDefault(o => !(o is GH_NumberSlider))
                                                ?? addedObjects.FirstOrDefault();
            if (primaryTarget is null)
            {
                return;
            }

            foreach (CanvasExternalWire extIn in snapshot.ExternalIncomingWires)
            {
                IGH_DocumentObject? extSource = doc.FindObject(extIn.ExternalInstanceGuid, true);
                if (extSource is null)
                {
                    continue;
                }

                IGH_DocumentObject target = primaryTarget;
                IGH_Param? resolvedTargetInput = WireConnector.ResolveInputParam(target, extIn.SelectedParamName, out _);
                if (resolvedTargetInput != null && resolvedTargetInput.Sources.Count == 0)
                {
                    WireConnector.TryConnect(extSource, extIn.ExternalParamName, target, extIn.SelectedParamName, out _);
                }
                else
                {
                    // Avoid double-wiring if the replacement already generated its own slider for that input.
                    warnings.Add($"Preserved original upstream wire '{extSource.Name}.{extIn.ExternalParamName}' only if compatible input was unconnected.");
                }
            }
        }

        private static List<PendingExternalRewire> BuildPendingOutgoingRewires(
            CanvasSnapshot snapshot,
            List<IGH_DocumentObject> addedObjects)
        {
            var rewires = new List<PendingExternalRewire>();
            if (snapshot.ExternalOutgoingWires.Count == 0 || addedObjects.Count == 0)
            {
                return rewires;
            }

            IGH_DocumentObject newOutputOwner = addedObjects.LastOrDefault(o => !(o is GH_Panel))
                                                ?? addedObjects[addedObjects.Count - 1];

            foreach (CanvasExternalWire extOut in snapshot.ExternalOutgoingWires)
            {
                CanvasNodeSnapshot? oldNode = snapshot.Nodes.FirstOrDefault(n => string.Equals(n.Id, extOut.SelectedNodeId, StringComparison.Ordinal));
                if (oldNode is null || oldNode.InstanceGuid == Guid.Empty)
                {
                    continue;
                }

                string targetParamName = string.Empty;
                if (newOutputOwner is IGH_Component outComp && outComp.Params.Output.Count > 0)
                {
                    targetParamName = WireConnector.ResolveOutputParam(outComp, extOut.SelectedParamName, out _) != null
                        ? extOut.SelectedParamName
                        : outComp.Params.Output[0].Name;
                }

                rewires.Add(new PendingExternalRewire(
                    oldSourceNodeGuid: oldNode.InstanceGuid,
                    oldSourceParamName: extOut.SelectedParamName,
                    newSourceNodeGuid: newOutputOwner.InstanceGuid,
                    newSourceParamName: targetParamName,
                    externalRecipientNodeGuid: extOut.ExternalInstanceGuid,
                    externalRecipientParamName: extOut.ExternalParamName));
            }

            return rewires;
        }

        internal static void ConfigureObject(IGH_DocumentObject instance, NodeSpec node)
        {
            if (node.Settings is null)
            {
                return;
            }

            if (instance is GH_NumberSlider slider)
            {
                int decimals = node.Settings.Decimals ?? 2;
                if (decimals <= 0)
                {
                    slider.Slider.Type = GH_SliderAccuracy.Integer;
                }
                else
                {
                    slider.Slider.Type = GH_SliderAccuracy.Float;
                    slider.Slider.DecimalPlaces = decimals;
                }

                decimal min = (decimal)(node.Settings.Min ?? 0.0);
                decimal max = (decimal)(node.Settings.Max ?? 100.0);
                if (max < min)
                {
                    max = min;
                }

                slider.Slider.Minimum = min;
                slider.Slider.Maximum = max;

                decimal val = (decimal)(node.Settings.Value ?? (double)min);
                if (val < min)
                {
                    val = min;
                }
                else if (val > max)
                {
                    val = max;
                }

                slider.SetSliderValue(val);
            }
            else if (instance is GH_Panel panel && node.Settings.Text != null)
            {
                panel.SetUserText(node.Settings.Text);
            }

            if (!string.IsNullOrWhiteSpace(node.Settings.Nickname))
            {
                instance.NickName = node.Settings.Nickname!;
            }
        }
    }
}
