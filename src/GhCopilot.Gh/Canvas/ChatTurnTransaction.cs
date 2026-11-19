using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using GH_IO.Serialization;
using GhCopilot.Core.Graph;
using GhCopilot.Gh.Script;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Undo;

namespace GhCopilot.Gh.Canvas
{
    /// <summary>
    /// A live, in-place chat-turn preview. Call on the creating (Rhino UI) thread, outside a
    /// solution. Patches must already have been validated against the supplied snapshot.
    /// Successful Apply calls accumulate; a failed call restores its entry state, not earlier
    /// successful repairs. Accept publishes one undo record; Reject publishes none.
    /// </summary>
    /// <remarks>
    /// Only changed objects are serialized, never the document. Settings changes own the
    /// touched object's serialized settings; concurrent edits to those same settings are not
    /// merged. Other objects, live positions/selections, unrelated wires and group members
    /// are preserved. Do not undo a pending preview; accept or reject it first.
    /// </remarks>
    public sealed class ChatTurnTransaction
    {
        private readonly int _threadId = Thread.CurrentThread.ManagedThreadId;
        private readonly Dictionary<string, Guid> _nodes = new Dictionary<string, Guid>(StringComparer.Ordinal);
        private readonly Dictionary<Guid, IGH_DocumentObject> _added = new Dictionary<Guid, IGH_DocumentObject>();
        private readonly List<Guid> _addedNodeIds = new List<Guid>();
        private readonly List<Edit> _edits = new List<Edit>();
        private readonly IReadOnlyList<Guid> _addedView;
        private bool _busy;
        private Completion _completion;

        public ChatTurnTransaction(GH_Document doc)
        {
            Document = doc ?? throw new ArgumentNullException(nameof(doc));
            _addedView = _addedNodeIds.AsReadOnly();
        }

        public GH_Document Document { get; }

        /// <summary>Currently present nodes added by this turn (not original nodes or groups).</summary>
        public IReadOnlyList<Guid> AddedNodeIds
        {
            get { RefreshAdded(); return _addedView; }
        }

        /// <summary>Zero until a nonempty turn is accepted; then one. Never counts user records.</summary>
        public int UndoRecordCount { get; private set; }

        public void Apply(GraphPatch patch, CanvasSnapshot snapshot, Func<string, IGH_ObjectProxy?> resolver)
        {
            if (patch == null) throw new ArgumentNullException(nameof(patch));
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            if (patch.Ops == null) throw new ArgumentException("Patch operations must not be null.", nameof(patch));
            if (snapshot.Nodes == null) throw new ArgumentException("Snapshot nodes must not be null.", nameof(snapshot));
            Enter();
            var oldNodes = new Dictionary<string, Guid>(_nodes, StringComparer.Ordinal);
            var oldAdded = new Dictionary<Guid, IGH_DocumentObject>(_added);
            int start = _edits.Count;
            try
            {
                // Later snapshots may re-number aliases; bind them to their actual live GUIDs.
                // Patch-local IDs from an earlier repair must not shadow the new snapshot.
                _nodes.Clear();
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (CanvasNodeSnapshot node in snapshot.Nodes)
                {
                    if (node == null || string.IsNullOrWhiteSpace(node.Id) || !ids.Add(node.Id) || node.InstanceGuid == Guid.Empty)
                        throw new ArgumentException("Snapshot contains an invalid or duplicate node.", nameof(snapshot));
                    _nodes[node.Id] = node.InstanceGuid;
                }

                foreach (GraphPatchOp op in patch.Ops)
                {
                    if (op == null) throw new ArgumentException("Patch contains a null operation.", nameof(patch));
                    ApplyOperation(op, resolver);
                }
                RefreshAdded();
                RefreshDocument(Document);
            }
            catch (Exception failure)
            {
                try
                {
                    // The current operation is journalled BEFORE its first mutation, including
                    // port resolution (which can expand a variable-parameter component).
                    for (int i = _edits.Count - 1; i >= start; i--)
                        _edits[i].Restore(Document, false);
                    _edits.RemoveRange(start, _edits.Count - start);
                    _nodes.Clear();
                    foreach (var item in oldNodes) _nodes.Add(item.Key, item.Value);
                    _added.Clear();
                    foreach (var item in oldAdded) _added.Add(item.Key, item.Value);
                    RefreshAdded();
                    RefreshDocument(Document);
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException("Patch failed and rollback could not finish; reject the pending turn before continuing.", failure, rollbackFailure);
                }
                throw;
            }
            finally { _busy = false; }
        }

        /// <summary>
        /// Add a first-generation graph, including its optional script, without publishing undo.
        /// The caller must first pass GraphValidator.Validate (including script safety and the
        /// snapshot's external selection records). Failure restores this call's entry state;
        /// the host must Reject to discard earlier successful operations in the same turn.
        /// </summary>
        public void ApplyGraph(GraphSpec spec, CanvasSnapshot snapshot, Func<string, IGH_ObjectProxy?> resolver)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            if (spec.Nodes == null || spec.Wires == null)
                throw new ArgumentException("Graph nodes and wires must not be null.", nameof(spec));
            if (snapshot.Nodes == null) throw new ArgumentException("Snapshot nodes must not be null.", nameof(snapshot));
            Enter();
            var oldNodes = new Dictionary<string, Guid>(_nodes, StringComparer.Ordinal);
            var oldAdded = new Dictionary<Guid, IGH_DocumentObject>(_added);
            int start = _edits.Count;
            try
            {
                _nodes.Clear();
                var ids = new HashSet<string>(StringComparer.Ordinal);
                var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (CanvasNodeSnapshot node in snapshot.Nodes)
                {
                    if (node == null || string.IsNullOrWhiteSpace(node.Id) || !ids.Add(node.Id) || node.InstanceGuid == Guid.Empty)
                        throw new ArgumentException("Snapshot contains an invalid or duplicate node.", nameof(snapshot));
                    if (!node.IsSelected) continue;
                    // Keep graph-local IDs separate from snapshot IDs (both can be 'n1').
                    // Resolve selection aliases through the captured GUID, never live selection.
                    string alias = "$" + node.Id;
                    _nodes.Add(alias, node.InstanceGuid);
                    aliases.Add(alias, alias);
                    if (aliases.Count == 1) aliases.Add("$selected", alias);
                }

                foreach (NodeSpec node in spec.Nodes)
                {
                    if (node == null) throw new ArgumentException("Graph contains a null node.", nameof(spec));
                    ApplyOperation(new GraphPatchOp { Op = "add_node", Node = node }, resolver);
                }

                if (spec.Script != null)
                {
                    if (_nodes.ContainsKey(GraphValidator.ScriptNodeId))
                        throw new ArgumentException("The script node ID is already in use.", nameof(spec));
                    float x = spec.Nodes.Count > 0 ? spec.Nodes.Max(n => n.X) + 220f : 380f;
                    float y = spec.Nodes.Count > 0 ? spec.Nodes.Average(n => n.Y) : 200f;
                    var beforeSpawn = new HashSet<Guid>(Document.Objects.Select(o => o.InstanceGuid));
                    IGH_DocumentObject scriptObj;
                    try
                    {
                        // Spawn inserts directly, with no explicit undo record. Its warnings
                        // are advisory; this void API cannot report injection/port fallbacks.
                        scriptObj = PythonScriptSpawner.Spawn(Document, spec.Script, new PointF(x, y), out _);
                    }
                    finally
                    {
                        // Also journal an insertion if Spawn throws after AddObject (e.g.
                        // during layout). No solution runs between insertion and this capture.
                        // Capture every addition before sealing any, so capture failures still
                        // leave all inserted objects in the rollback journal.
                        var inserted = new List<Edit>();
                        foreach (IGH_DocumentObject added in Document.Objects.Where(o => !beforeSpawn.Contains(o.InstanceGuid)).ToList())
                        {
                            var edit = new Edit(Document, added, false, true);
                            _edits.Add(edit);
                            _added.Add(added.InstanceGuid, added);
                            inserted.Add(edit);
                        }
                        foreach (Edit edit in inserted) edit.Seal(Document);
                    }
                    if (scriptObj.InstanceGuid == Guid.Empty || beforeSpawn.Contains(scriptObj.InstanceGuid) ||
                        !ReferenceEquals(Document.FindObject(scriptObj.InstanceGuid, false), scriptObj))
                        throw new InvalidOperationException("Script spawning did not add a new, uniquely identified object.");
                    // Unlike composing Apply calls, this scope retains every new GUID,
                    // including 'script', for wiring without mutating the caller's snapshot.
                    _nodes.Add(GraphValidator.ScriptNodeId, scriptObj.InstanceGuid);
                }

                foreach (WireSpec wire in spec.Wires)
                {
                    if (wire == null) throw new ArgumentException("Graph contains a null wire.", nameof(spec));
                    var mapped = new WireSpec
                    {
                        FromNode = aliases.TryGetValue(wire.FromNode, out string? source) ? source : wire.FromNode,
                        FromParam = wire.FromParam,
                        ToNode = wire.ToNode,
                        ToParam = wire.ToParam,
                        TargetMapping = wire.TargetMapping,
                        Simplify = wire.Simplify,
                        Reverse = wire.Reverse
                    };
                    ApplyOperation(new GraphPatchOp { Op = "wire", Wire = mapped }, resolver);
                }
                RefreshAdded();
                RefreshDocument(Document);
            }
            catch (Exception failure)
            {
                try
                {
                    for (int i = _edits.Count - 1; i >= start; i--)
                        _edits[i].Restore(Document, false);
                    _edits.RemoveRange(start, _edits.Count - start);
                    _nodes.Clear();
                    foreach (var item in oldNodes) _nodes.Add(item.Key, item.Value);
                    _added.Clear();
                    foreach (var item in oldAdded) _added.Add(item.Key, item.Value);
                    RefreshAdded();
                    RefreshDocument(Document);
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException("Graph failed and rollback could not finish; reject the pending turn before continuing.", failure, rollbackFailure);
                }
                throw;
            }
            finally { _busy = false; }
        }

        /// <summary>
        /// Journal input mutations on the supplied existing nodes, including serialized
        /// persistent input data. The action must only mutate those nodes. Failure restores
        /// this call's entry state; successful changes remain pending until Accept or Reject.
        /// </summary>
        public void MutateInputs(IReadOnlyList<Guid> nodeIds, Action mutation)
        {
            if (nodeIds == null) throw new ArgumentNullException(nameof(nodeIds));
            if (mutation == null) throw new ArgumentNullException(nameof(mutation));
            Enter();
            int start = _edits.Count;
            try
            {
                // Capture every node before invoking the action, not one at a time around
                // mutations. Settings serialization includes component input persistent data.
                foreach (Guid id in nodeIds.Distinct())
                {
                    IGH_DocumentObject? obj = Document.FindObject(id, false);
                    if (obj != null) _edits.Add(new Edit(Document, obj, true, true));
                }

                mutation();
                for (int i = start; i < _edits.Count; i++) _edits[i].Seal(Document);
                RefreshAdded();
                RefreshDocument(Document);
            }
            catch (Exception failure)
            {
                try
                {
                    for (int i = _edits.Count - 1; i >= start; i--)
                        _edits[i].Restore(Document, false);
                    _edits.RemoveRange(start, _edits.Count - start);
                    RefreshAdded();
                    RefreshDocument(Document);
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException("Input mutation failed and rollback could not finish; reject the pending turn before continuing.", failure, rollbackFailure);
                }
                throw;
            }
            finally { _busy = false; }
        }

        /// <summary>Keep the visible preview and publish its entire journal as one undo step.</summary>
        public void Accept()
        {
            if (_completion == Completion.Accepted) return;
            Enter();
            try
            {
                if (_edits.Count > 0)
                {
                    // Built-in GenericObjectAction snapshots at construction time, and includes
                    // boundary wires. Constructing it here would record the preview, not the
                    // pre-turn state. A custom action replays our pre-captured sparse mementos.
                    var record = new GH_UndoRecord("GH Copilot: Chat Turn");
                    record.AddAction(new TurnUndoAction(Document, _edits.ToArray()));
                    Document.UndoServer.PushUndoRecord(record);
                    UndoRecordCount = 1;
                }
                _completion = Completion.Accepted;
                _edits.Clear();
            }
            finally { _busy = false; }
        }

        /// <summary>Restore this turn only, without invoking PerformUndo or publishing a record.</summary>
        public void Reject()
        {
            if (_completion == Completion.Rejected) return;
            Enter();
            try
            {
                Replay(Document, _edits, false);
                _completion = Completion.Rejected;
                _edits.Clear();
                RefreshAdded();
                RefreshDocument(Document);
            }
            finally { _busy = false; }
        }

        private void Enter()
        {
            if (Thread.CurrentThread.ManagedThreadId != _threadId)
                throw new InvalidOperationException("Chat-turn mutations must run on the creating Rhino UI thread.");
            if (_completion != Completion.Pending)
                throw new InvalidOperationException("The chat turn has already been completed.");
            if (_busy || Document.SolutionState == GH_ProcessStep.Process)
                throw new InvalidOperationException("Cannot mutate a chat turn during a solution or another transaction operation.");
            _busy = true;
        }

        private void ApplyOperation(GraphPatchOp op, Func<string, IGH_ObjectProxy?> resolver)
        {
            if (op.Op == "add_node")
            {
                NodeSpec node = op.Node ?? throw new ArgumentException("add_node requires node.");
                if (string.IsNullOrWhiteSpace(node.Id) || string.IsNullOrWhiteSpace(node.Ref) || _nodes.ContainsKey(node.Id))
                    throw new ArgumentException("New node IDs must be unique and references must not be blank.");
                IGH_ObjectProxy proxy = resolver(node.Ref)
                    ?? throw new InvalidOperationException($"Could not resolve object proxy '{node.Ref}'.");
                IGH_DocumentObject instance = proxy.CreateInstance()
                    ?? throw new InvalidOperationException($"Proxy '{node.Ref}' returned no instance.");
                if (instance.InstanceGuid == Guid.Empty || Document.FindObject(instance.InstanceGuid, false) != null)
                    throw new InvalidOperationException("The proxy did not create a new, uniquely identified object.");
                var edit = new Edit(Document, instance, false, true);
                _edits.Add(edit);
                CanvasSpawner.ConfigureObject(instance, node);
                WireConnector.ApplyNodeInputModifiers(instance, node.Settings);
                if (instance.Attributes == null) instance.CreateAttributes();
                if (instance.Attributes == null) throw new InvalidOperationException("The new object has no attributes.");
                instance.Attributes.Pivot = new PointF(node.X, node.Y);
                if (!Document.AddObject(instance, false)) throw new InvalidOperationException("Could not add the new object.");
                instance.Attributes.ExpireLayout();
                instance.Attributes.PerformLayout();
                _nodes.Add(node.Id, instance.InstanceGuid);
                _added.Add(instance.InstanceGuid, instance);
                edit.Seal(Document);
                return;
            }

            if (op.Op == "set_param" || op.Op == "delete_node")
            {
                IGH_DocumentObject obj = FindNode(op.NodeId);
                var edit = new Edit(Document, obj, true, true);
                _edits.Add(edit);
                if (op.Op == "set_param")
                {
                    NodeSettings settings = op.Settings ?? throw new ArgumentException("set_param requires settings.");
                    ApplySettings(obj, settings);
                    ApplyInputModifierReplacements(obj, settings);
                    obj.Attributes?.ExpireLayout();
                }
                else
                {
                    if (!Document.RemoveObject(obj, false)) throw new InvalidOperationException("Could not delete the object.");
                    // Explicitly remove membership as SDK/event handlers can differ by version.
                    foreach (GH_Group group in Document.Objects.OfType<GH_Group>())
                        if (group.ObjectIDs.Contains(obj.InstanceGuid))
                        {
                            group.RemoveObject(obj.InstanceGuid);
                            group.ExpireCaches();
                        }
                }
                edit.Seal(Document);
                return;
            }

            if (op.Op == "wire" || op.Op == "unwire")
            {
                WireSpec wire = op.Wire ?? throw new ArgumentException("Wire operation requires wire.");
                IGH_DocumentObject source = FindNode(wire.FromNode);
                IGH_DocumentObject target = FindNode(wire.ToNode);
                var edit = new Edit(Document, target, true, false);
                _edits.Add(edit);
                IGH_Param output = WireConnector.ResolveOutputParam(source, wire.FromParam, out string? outputError)
                    ?? throw new InvalidOperationException(outputError ?? "Output port not found.");
                IGH_Param input = WireConnector.ResolveInputParam(target, wire.ToParam, out string? inputError)
                    ?? throw new InvalidOperationException(inputError ?? "Input port not found.");
                bool exists = input.Sources.Any(p => p.InstanceGuid == output.InstanceGuid);
                if (op.Op == "unwire")
                {
                    if (!exists) throw new InvalidOperationException("The wire to remove no longer exists.");
                    input.RemoveSource(output);
                }
                else
                {
                    if (exists) throw new InvalidOperationException("The wire already exists on the live canvas.");
                    if (!WireConnector.TryConnect(source, wire.FromParam, target, wire.ToParam, out string? error,
                            wire.TargetMapping, wire.Simplify, wire.Reverse))
                        throw new InvalidOperationException(error ?? "Could not connect the wire.");
                }
                edit.Seal(Document);
                return;
            }
            throw new ArgumentException($"Unknown graph patch operation '{op.Op}'.");
        }

        private IGH_DocumentObject FindNode(string? id)
        {
            if (id == null || !_nodes.TryGetValue(id, out Guid guid))
                throw new InvalidOperationException($"Unknown node '{id}'.");
            return Document.FindObject(guid, false)
                ?? throw new InvalidOperationException($"Node '{id}' is no longer on the canvas.");
        }

        private static void ApplySettings(IGH_DocumentObject obj, NodeSettings change)
        {
            if (!(obj is GH_NumberSlider slider))
            {
                CanvasSpawner.ConfigureObject(obj, new NodeSpec { Settings = change });
                return;
            }

            // Creation defaults must never leak into a patch. Work with live decimals to
            // avoid rounding existing values through double, and preserve Even/Odd accuracy
            // unless the patch actually supplies Decimals.
            decimal min = change.Min.HasValue ? (decimal)change.Min.Value : slider.Slider.Minimum;
            decimal max = change.Max.HasValue ? (decimal)change.Max.Value : slider.Slider.Maximum;
            decimal value = change.Value.HasValue ? (decimal)change.Value.Value : slider.CurrentValue;
            if (min >= max || value < min || value > max)
                throw new InvalidOperationException("Slider settings conflict with its live range/value; revalidate the patch.");
            if (change.Decimals.HasValue)
            {
                slider.Slider.Type = change.Decimals.Value == 0
                    ? Grasshopper.GUI.Base.GH_SliderAccuracy.Integer
                    : Grasshopper.GUI.Base.GH_SliderAccuracy.Float;
                slider.Slider.DecimalPlaces = change.Decimals.Value;
            }
            // Expand before contracting so a shifted interval is never temporarily inverted.
            if (change.Max.HasValue && max > slider.Slider.Maximum) slider.Slider.Maximum = max;
            if (change.Min.HasValue) slider.Slider.Minimum = min;
            if (change.Max.HasValue) slider.Slider.Maximum = max;
            if (change.Value.HasValue || change.Min.HasValue || change.Max.HasValue || change.Decimals.HasValue)
                slider.SetSliderValue(value);
            if (change.Nickname != null) slider.NickName = change.Nickname;
        }

        private static void ApplyInputModifierReplacements(IGH_DocumentObject obj, NodeSettings settings)
        {
            // Resolve every selector before enumerating inputs: resolution can expand variable
            // components. The caller journals settings and input flags before any resolution.
            HashSet<IGH_Param>? Resolve(List<string>? selectors)
            {
                if (selectors == null) return null;
                var result = new HashSet<IGH_Param>();
                foreach (string selector in selectors)
                    result.Add(WireConnector.ResolveInputParam(obj, selector, out string? error)
                        ?? throw new InvalidOperationException(error ?? "Input port not found."));
                return result;
            }

            HashSet<IGH_Param>? flatten = Resolve(settings.FlattenInputs);
            HashSet<IGH_Param>? graft = Resolve(settings.GraftInputs);
            HashSet<IGH_Param>? simplify = Resolve(settings.SimplifyInputs);
            HashSet<IGH_Param>? reverse = Resolve(settings.ReverseInputs);

            foreach (IGH_Param input in Inputs(obj))
            {
                // Flatten and Graft share one mapping. Clear only supplied mappings before
                // applying either replacement, preserving an omitted mapping on other ports.
                if ((flatten != null && input.DataMapping == GH_DataMapping.Flatten) ||
                    (graft != null && input.DataMapping == GH_DataMapping.Graft))
                    input.DataMapping = GH_DataMapping.None;
                if (flatten != null && flatten.Contains(input)) input.DataMapping = GH_DataMapping.Flatten;
                if (graft != null && graft.Contains(input)) input.DataMapping = GH_DataMapping.Graft;
                if (simplify != null) input.Simplify = simplify.Contains(input);
                if (reverse != null) input.Reverse = reverse.Contains(input);
            }
        }

        private void RefreshAdded()
        {
            _addedNodeIds.Clear();
            foreach (var item in _added)
                if (ReferenceEquals(Document.FindObject(item.Key, false), item.Value)) _addedNodeIds.Add(item.Key);
        }

        private static IEnumerable<IGH_Param> Inputs(IGH_DocumentObject obj)
        {
            if (obj is IGH_Component component) return component.Params.Input;
            if (obj is IGH_Param param) return new[] { param };
            return Enumerable.Empty<IGH_Param>();
        }

        private static IEnumerable<IGH_Param> Outputs(IGH_DocumentObject obj)
        {
            if (obj is IGH_Component component) return component.Params.Output;
            if (obj is IGH_Param param) return new[] { param };
            return Enumerable.Empty<IGH_Param>();
        }

        private static void RefreshDocument(GH_Document doc)
        {
            // Expiration is scoped to changed objects/recipients in Edit, not the definition.
            doc.NewSolution(false);
            Instances.ActiveCanvas?.Invalidate();
        }

        private static void Replay(GH_Document doc, IList<Edit> edits, bool forward)
        {
            var completed = new List<Edit>();
            try
            {
                for (int n = 0; n < edits.Count; n++)
                {
                    Edit edit = edits[forward ? n : edits.Count - 1 - n];
                    completed.Add(edit);
                    edit.Restore(doc, forward);
                }
            }
            catch (Exception failure)
            {
                try
                {
                    for (int n = completed.Count - 1; n >= 0; n--) completed[n].Restore(doc, !forward);
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException("Chat-turn replay and its rollback failed.", failure, rollbackFailure);
                }
                throw;
            }
        }

        private enum Completion { Pending, Accepted, Rejected }

        private sealed class TurnUndoAction : GH_UndoAction
        {
            private readonly GH_Document _document;
            private readonly Edit[] _edits;

            public TurnUndoAction(GH_Document document, Edit[] edits)
            {
                _document = document;
                _edits = edits;
            }

            public override bool ExpiresSolution => true;
            public override bool ExpiresDisplay => true;

            protected override void Internal_Undo(GH_Document doc) => Run(doc, false);
            protected override void Internal_Redo(GH_Document doc) => Run(doc, true);

            private void Run(GH_Document doc, bool forward)
            {
                if (!ReferenceEquals(doc, _document)) throw new InvalidOperationException("Undo belongs to another document.");
                Replay(doc, _edits, forward);
                // GH_UndoServer handles solution/display refresh after the complete record.
            }
        }

        /// <summary>One operation's sparse before/after image, including boundary recipients.</summary>
        private sealed class Edit
        {
            private readonly IGH_DocumentObject _object;
            private readonly Guid _id;
            private readonly bool _restoreSettings;
            private readonly Image _before;
            private Image? _after;

            public Edit(GH_Document doc, IGH_DocumentObject obj, bool exists, bool restoreSettings)
            {
                _object = obj;
                _id = obj.InstanceGuid;
                _restoreSettings = restoreSettings;
                _before = Image.Capture(doc, obj, exists, null);
            }

            public void Seal(GH_Document doc)
            {
                _after = Image.Capture(doc, _object, ReferenceEquals(doc.FindObject(_id, false), _object), _before);
                Expire(doc);
            }

            public void Restore(GH_Document doc, bool forward)
            {
                // A failed operation may not have reached Seal. Capture its partially mutated
                // state now so the very same delta restoration can undo it.
                if (_after == null)
                    _after = Image.Capture(doc, _object, ReferenceEquals(doc.FindObject(_id, false), _object), _before);
                Image desired = forward ? _after : _before;
                Image other = forward ? _before : _after;
                IGH_DocumentObject? live = doc.FindObject(_id, false);
                if (live != null && !ReferenceEquals(live, _object))
                    throw new InvalidOperationException("A different object has taken a transaction object's GUID.");

                if (!desired.Exists)
                {
                    if (live != null && !doc.RemoveObject(live, false))
                        throw new InvalidOperationException("Could not remove a transaction object during restoration.");
                }
                else if (live == null || _restoreSettings || desired.Inputs.Count != other.Inputs.Count)
                {
                    // Read can recreate component parameters. Preserve live boundary topology
                    // independently of serialized Source GUIDs, and re-resolve by GUID below.
                    List<InputImage> current = live == null ? new List<InputImage>() : Image.CaptureInputs(_object);
                    PointF? pivot = live?.Attributes?.Pivot;
                    bool? selected = live?.Attributes?.Selected;
                    if (desired.Chunk == null || !_object.Read(desired.Chunk))
                        throw new InvalidOperationException($"Could not restore '{_object.Name}'.");
                    if (_object.InstanceGuid != _id) throw new InvalidOperationException("Object deserialization changed its InstanceGuid.");
                    if (_object.Attributes == null) _object.CreateAttributes();
                    if (pivot.HasValue && _object.Attributes != null) _object.Attributes.Pivot = pivot.Value;
                    if (selected.HasValue && _object.Attributes != null) _object.Attributes.Selected = selected.Value;
                    if (live == null && !doc.AddObject(_object, false, desired.Index))
                        throw new InvalidOperationException("Could not restore a deleted object.");
                    foreach (IGH_Param input in Inputs(_object)) input.RemoveAllSources();
                    foreach (InputImage input in current) input.RestoreExactSources(doc);
                    _object.Attributes?.ExpireLayout();
                }

                // First all objects/parameters exist, then restore sources in their original
                // order. Only edges changed by this operation are added/removed.
                foreach (InputImage input in desired.Inputs)
                {
                    InputImage? previous = other.Inputs.FirstOrDefault(p => p.Id == input.Id);
                    input.RestoreDelta(doc, previous);
                }
                foreach (InputImage input in other.Inputs)
                    if (!desired.Inputs.Any(p => p.Id == input.Id)) InputImage.RestoreMissing(doc, input);

                foreach (Guid groupId in desired.Groups.Except(other.Groups))
                    if (doc.FindObject(groupId, false) is GH_Group group && doc.FindObject(_id, false) != null)
                    {
                        group.AddObject(_id);
                        group.ExpireCaches();
                    }
                foreach (Guid groupId in other.Groups.Except(desired.Groups))
                    if (doc.FindObject(groupId, false) is GH_Group group)
                    {
                        group.RemoveObject(_id);
                        group.ExpireCaches();
                    }
                Expire(doc);
            }

            private void Expire(GH_Document doc)
            {
                if (doc.FindObject(_id, false) is IGH_ActiveObject active) active.ExpireSolution(false);
                foreach (InputImage input in _before.Inputs.Concat(_after?.Inputs ?? Enumerable.Empty<InputImage>()))
                    doc.FindParameter(input.Id)?.ExpireSolution(false);
            }
        }

        private sealed class Image
        {
            public bool Exists;
            public int Index;
            public GH_Chunk? Chunk;
            public List<InputImage> Inputs = new List<InputImage>();
            public List<Guid> Groups = new List<Guid>();

            public static Image Capture(GH_Document doc, IGH_DocumentObject obj, bool exists, Image? before)
            {
                var image = new Image { Exists = exists, Index = doc.Objects.IndexOf(obj) };
                if (exists)
                {
                    var chunk = new GH_LooseChunk("ChatTurnObject");
                    if (!obj.Write(chunk)) throw new InvalidOperationException($"Could not capture '{obj.Name}'.");
                    image.Chunk = chunk;
                    image.Inputs = CaptureInputs(obj);
                    image.Groups = doc.Objects.OfType<GH_Group>()
                        .Where(g => g.ObjectIDs.Contains(obj.InstanceGuid)).Select(g => g.InstanceGuid).ToList();
                }
                // Even after deletion, capture the same external recipients as before.
                if (before != null)
                    foreach (InputImage input in before.Inputs)
                    {
                        if (image.Inputs.Any(p => p.Id == input.Id)) continue;
                        IGH_Param? live = doc.FindParameter(input.Id);
                        if (live != null) image.Inputs.Add(new InputImage(live));
                    }
                return image;
            }

            public static List<InputImage> CaptureInputs(IGH_DocumentObject obj)
            {
                return ChatTurnTransaction.Inputs(obj)
                    .Concat(Outputs(obj).SelectMany(p => p.Recipients))
                    .GroupBy(p => p.InstanceGuid).Select(g => new InputImage(g.First())).ToList();
            }
        }

        private sealed class InputImage
        {
            public readonly Guid Id;
            private readonly Guid[] _sources;
            private readonly GH_DataMapping _mapping;
            private readonly bool _simplify;
            private readonly bool _reverse;

            public InputImage(IGH_Param param)
            {
                Id = param.InstanceGuid;
                _sources = param.Sources.Select(p => p.InstanceGuid).ToArray();
                _mapping = param.DataMapping;
                _simplify = param.Simplify;
                _reverse = param.Reverse;
            }

            public void RestoreExactSources(GH_Document doc)
            {
                IGH_Param? target = doc.FindParameter(Id);
                if (target == null) return;
                target.RemoveAllSources();
                foreach (Guid id in _sources)
                    if (doc.FindParameter(id) is IGH_Param source) target.AddSource(source);
            }

            public void RestoreDelta(GH_Document doc, InputImage? other)
            {
                IGH_Param? target = doc.FindParameter(Id);
                if (target == null) return; // A user may independently have deleted a neighbor.
                Guid[] previous = other?._sources ?? Array.Empty<Guid>();
                foreach (Guid id in previous.Except(_sources)) target.RemoveSource(id);
                for (int i = 0; i < _sources.Length; i++)
                {
                    Guid id = _sources[i];
                    if (target.Sources.Any(p => p.InstanceGuid == id)) continue;
                    // Unchanged edges on a live neighbor may have been removed by the user.
                    // Do not resurrect them; newly restored objects need all of their sources.
                    if (previous.Contains(id) && other != null) continue;
                    if (doc.FindParameter(id) is IGH_Param source)
                    {
                        int index = target.Sources.Count;
                        // Insert before the next surviving original source, or after the last
                        // preceding source. Preserve relative order of unrelated user wires.
                        for (int j = i - 1; j >= 0; j--)
                        {
                            int preceding = target.Sources.ToList().FindIndex(p => p.InstanceGuid == _sources[j]);
                            if (preceding >= 0) { index = preceding + 1; break; }
                        }
                        for (int j = i + 1; j < _sources.Length; j++)
                        {
                            int next = target.Sources.ToList().FindIndex(p => p.InstanceGuid == _sources[j]);
                            if (next >= 0) { index = next; break; }
                        }
                        target.AddSource(source, index);
                    }
                }
                if (other == null || _mapping != other._mapping) target.DataMapping = _mapping;
                if (other == null || _simplify != other._simplify) target.Simplify = _simplify;
                if (other == null || _reverse != other._reverse) target.Reverse = _reverse;
                target.ExpireSolution(false);
            }

            public static void RestoreMissing(GH_Document doc, InputImage other)
            {
                IGH_Param? target = doc.FindParameter(other.Id);
                if (target == null) return;
                foreach (Guid id in other._sources) target.RemoveSource(id);
                target.ExpireSolution(false);
            }
        }
    }
}