using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace GhCopilot.Gh.Canvas
{
    /// <summary>Geometry-free input diagnostics for a solved Grasshopper document.</summary>
    public sealed class ChatInputState
    {
        public List<string> MissingInputs { get; } = new List<string>();
        public List<string> InvalidInputs { get; } = new List<string>();
        public List<string> RuntimeMessages { get; } = new List<string>();
    }

    public static class ChatInputInspector
    {
        /// <summary>
        /// Reads the current solution without solving, changing selection, or accessing Rhino geometry.
        /// Call on the Grasshopper UI thread after the document has solved.
        /// </summary>
        public static ChatInputState Inspect(GH_Document doc, IReadOnlyList<Guid> nodeIds)
        {
            if (doc is null) throw new ArgumentNullException(nameof(doc));
            if (nodeIds is null) throw new ArgumentNullException(nameof(nodeIds));

            var state = new ChatInputState();
            var selected = new HashSet<Guid>(nodeIds);
            var visited = new HashSet<Guid>();
            foreach (Guid id in nodeIds)
            {
                if (!visited.Add(id)) continue;
                IGH_DocumentObject obj = doc.FindObject(id, false);
                if (obj == null)
                {
                    state.MissingInputs.Add($"Node {id}: not found in the document.");
                    continue;
                }

                if (obj is IGH_ActiveObject active) AppendMessages(active, Label(obj), state);
                if (obj is IGH_Component component)
                {
                    foreach (IGH_Param input in component.Params.Input)
                    {
                        string label = $"{Label(obj)} / {input.Name}";
                        InspectParameter(input, label, !input.Optional, selected, state);
                        AppendMessages(input, label, state);
                    }
                    // Output failures are runtime diagnostics, not missing/invalid user inputs.
                    foreach (IGH_Param output in component.Params.Output)
                        AppendMessages(output, $"{Label(obj)} / output {output.Name}", state);
                }
                else if (obj is IGH_Param parameter)
                {
                    bool requiresData = !parameter.Optional && !(parameter is GH_NumberSlider) && !(parameter is GH_Panel);
                    InspectParameter(parameter, Label(obj), requiresData, selected, state);
                }
            }

            return state;
        }

        private static void InspectParameter(IGH_Param parameter, string label, bool required,
            HashSet<Guid> selected, ChatInputState state)
        {
            // A wire is not evidence that a required input actually received data.
            if (required && parameter.VolatileDataCount == 0)
                state.MissingInputs.Add($"{label}: required input has no data (including connected sources).");

            if (HasInvalidData(parameter))
                state.InvalidInputs.Add($"{label}: contains invalid or unreadable input data.");

            // Failed casts can leave the destination empty while invalid goo remains at the
            // external boundary. Do not classify selected components' outputs in isolation.
            var visited = new HashSet<Guid>();
            foreach (IGH_Param source in parameter.Sources)
                InspectExternalSource(source, label, selected, visited, state);
        }

        private static void InspectExternalSource(IGH_Param source, string label, HashSet<Guid> selected,
            HashSet<Guid> visited, ChatInputState state)
        {
            if (!visited.Add(source.InstanceGuid)) return;
            IGH_DocumentObject owner = source.Attributes?.GetTopLevel?.DocObject ?? source;
            if (selected.Contains(owner.InstanceGuid)) return;

            if (HasInvalidData(source))
                state.InvalidInputs.Add($"{label}: external source {Label(owner)} / {source.Name} contains invalid or unreadable data.");

            // Only follow pass-through parameters, never unrelated inputs of external components.
            if (owner is IGH_Param)
                foreach (IGH_Param upstream in source.Sources)
                    InspectExternalSource(upstream, label, selected, visited, state);
        }

        private static bool HasInvalidData(IGH_Param parameter)
        {
            try
            {
                foreach (IGH_Goo goo in parameter.VolatileData.AllData(true))
                    if (!IsValid(goo)) return true;

                // AllData(true) deliberately skips nulls, but null slots are not valid input.
                foreach (IGH_Goo goo in parameter.VolatileData.AllData(false))
                    if (goo == null) return true;
                return false;
            }
            catch (Exception)
            {
                // Third-party goo may throw from validity checks; fail closed without
                // dereferencing Rhino GeometryBase.IsValid or serializing any geometry.
                return true;
            }
        }

        private static bool IsValid(IGH_Goo? goo)
        {
            try { return goo != null && goo.IsValid; }
            catch (Exception) { return false; }
        }

        private static string Label(IGH_DocumentObject obj) => $"{obj.Name} ({obj.InstanceGuid})";

        private static void AppendMessages(IGH_ActiveObject active, string label, ChatInputState state)
        {
            foreach (string message in active.RuntimeMessages(GH_RuntimeMessageLevel.Error))
                state.RuntimeMessages.Add($"[Error] {label}: {message}");
            foreach (string message in active.RuntimeMessages(GH_RuntimeMessageLevel.Warning))
                state.RuntimeMessages.Add($"[Warning] {label}: {message}");
        }

        /// <summary>
        /// Explicit, local-only binding of the existing Rhino selection. The host MUST obtain
        /// user consent before calling this method on the UI thread. Never called by Inspect.
        /// Returns binding errors, not geometry; does not select objects or start a solution.
        /// </summary>
        public static List<string> BindRhinoSelection(GH_Document doc, IReadOnlyList<Guid> nodeIds)
        {
            if (doc is null) throw new ArgumentNullException(nameof(doc));
            if (nodeIds is null) throw new ArgumentNullException(nameof(nodeIds));

            var errors = new List<string>();
            RhinoDoc rhinoDoc = RhinoDoc.ActiveDoc;
            if (rhinoDoc == null)
            {
                errors.Add("No active Rhino document; geometry selection cannot be bound.");
                return errors;
            }

            RhinoObject[] selection = rhinoDoc.Objects.GetSelectedObjects(false, false).ToArray();
            if (selection.Length == 0)
            {
                errors.Add("No Rhino objects are selected; select compatible geometry before binding.");
                return errors;
            }

            var visited = new HashSet<Guid>();
            foreach (Guid id in nodeIds)
            {
                if (!visited.Add(id)) continue;
                IGH_DocumentObject obj = doc.FindObject(id, false);
                if (obj == null)
                {
                    errors.Add($"Node {id}: not found in the document.");
                    continue;
                }

                if (obj is IGH_Component component)
                {
                    foreach (IGH_Param input in component.Params.Input)
                        if (!input.Optional && input.VolatileDataCount == 0)
                            BindParameter(input, $"{Label(obj)} / {input.Name}", selection, rhinoDoc, errors);
                }
                else if (obj is IGH_Param parameter && parameter.VolatileDataCount == 0 &&
                         !(parameter is GH_NumberSlider) && !(parameter is GH_Panel))
                {
                    BindParameter(parameter, Label(obj), selection, rhinoDoc, errors);
                }
            }

            return errors;
        }

        private static void BindParameter(IGH_Param parameter, string label, RhinoObject[] selection,
            RhinoDoc rhinoDoc, List<string> errors)
        {
            if (parameter.SourceCount != 0)
            {
                errors.Add($"{label}: connected input is empty; supply data at its source (existing wires were preserved).");
                return;
            }

            try
            {
                // Strongly typed overloads only: no reflection, generic object wrappers, or
                // permissive casts that might put the wrong goo type in a parameter.
                switch (parameter)
                {
                    case GH_PersistentParam<IGH_GeometricGoo> geometry:
                        BindTyped(geometry, label, selection, obj =>
                        {
                            using (var reference = new ObjRef(obj))
                            {
                                IGH_GeometricGoo goo = GH_Convert.ObjRefToGeometry(reference);
                                if (goo == null || goo.ReferenceID != obj.Id) return null;
                                return goo.LoadGeometry(rhinoDoc) && IsValid(goo) ? goo : null;
                            }
                        }, errors);
                        break;
                    case GH_PersistentParam<GH_Curve> curve:
                        BindTyped(curve, label, selection, obj => obj.Geometry is Curve
                            ? Referenced<GH_Curve>(obj, rhinoDoc) : null, errors);
                        break;
                    case GH_PersistentParam<GH_Point> point:
                        BindTyped(point, label, selection, obj => obj.Geometry is Point
                            ? Referenced<GH_Point>(obj, rhinoDoc) : null, errors);
                        break;
                    case GH_PersistentParam<GH_Brep> brep:
                        BindTyped(brep, label, selection, obj => obj.Geometry is Brep || obj.Geometry is Extrusion
                            ? Referenced<GH_Brep>(obj, rhinoDoc) : null, errors);
                        break;
                    case GH_PersistentParam<GH_Surface> surface:
                        BindTyped(surface, label, selection, obj => obj.Geometry is Surface ||
                            (obj.Geometry is Brep b && b.Faces.Count == 1)
                            ? Referenced<GH_Surface>(obj, rhinoDoc) : null, errors);
                        break;
                    case GH_PersistentParam<GH_Mesh> mesh:
                        BindTyped(mesh, label, selection, obj => obj.Geometry is Mesh
                            ? Referenced<GH_Mesh>(obj, rhinoDoc) : null, errors);
                        break;
                    default:
                        errors.Add($"{label}: this parameter does not support safe Rhino geometry reference binding.");
                        break;
                }
            }
            catch (Exception)
            {
                errors.Add($"{label}: geometry reference binding failed; check the parameter and Rhino selection.");
            }
        }

        private static T? Referenced<T>(RhinoObject obj, RhinoDoc doc) where T : class, IGH_GeometricGoo, new()
        {
            var goo = new T { ReferenceID = obj.Id };
            return goo.LoadGeometry(doc) && IsValid(goo) ? goo : null;
        }

        private static void BindTyped<T>(GH_PersistentParam<T> parameter, string label,
            RhinoObject[] selection, Func<RhinoObject, T?> convert, List<string> errors)
            where T : class, IGH_Goo
        {
            // Volatile data can be empty before a solve even when persistent data is present.
            if (parameter.PersistentDataCount != 0)
            {
                errors.Add($"{label}: existing persistent data was preserved; solve the document before binding.");
                return;
            }

            var values = new List<T>();
            foreach (RhinoObject obj in selection)
            {
                try
                {
                    T? goo = convert(obj);
                    if (IsValid(goo) && goo != null) values.Add(goo);
                }
                catch (Exception)
                {
                    // An unreadable object must not prevent other compatible objects binding.
                    errors.Add($"{label}: a selected Rhino object could not be read as compatible geometry.");
                }
            }

            if (values.Count == 0)
                errors.Add($"{label}: no valid selected Rhino geometry is compatible with this parameter.");
            else
                parameter.SetPersistentData((IEnumerable<T>)values);
        }
    }
}