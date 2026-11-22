using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using GhCopilot.Core.Rhino;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace GhCopilot.Rhino.Host
{
    internal sealed class RhinoExecutionResult
    {
        public int CreatedCount { get; set; }
        public int ModifiedCount { get; set; }
        public int DeletedCount { get; set; }
        public bool ViewportUpdated { get; set; }
        public List<string> AppliedNotes { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public bool MadeDocumentChanges => CreatedCount > 0 || ModifiedCount > 0 || DeletedCount > 0;
    }

    /// <summary>
    /// Executes validated <see cref="RhinoActionPlan"/> operations directly against <see cref="RhinoDoc.ActiveDoc"/>
    /// inside a single native Rhino Undo record.
    /// </summary>
    internal sealed class RhinoActionExecutor
    {
        private readonly List<Guid> _lastCreatedIds = new List<Guid>();

        public bool CanUndoLastTurn { get; private set; }

        public IReadOnlyList<string> DescribeLastCreated(RhinoDoc? doc)
        {
            var descriptions = new List<string>();
            if (doc is null)
            {
                return descriptions;
            }

            _lastCreatedIds.RemoveAll(id =>
            {
                RhinoObject? obj = doc.Objects.FindId(id);
                return obj is null || obj.IsDeleted;
            });

            foreach (Guid id in _lastCreatedIds.Take(20))
            {
                RhinoObject? obj = doc.Objects.FindId(id);
                if (obj is null || obj.IsDeleted) continue;

                Layer? layer = doc.Layers?.FindIndex(obj.Attributes.LayerIndex);
                string layerName = layer != null ? (string.IsNullOrWhiteSpace(layer.FullPath) ? layer.Name : layer.FullPath) : "Default";
                string label = string.IsNullOrWhiteSpace(obj.Name) ? obj.ObjectType.ToString() : $"{obj.ObjectType} \"{obj.Name}\"";
                descriptions.Add($"{label} (layer={layerName})");
            }

            return descriptions;
        }

        public void ResetTracking()
        {
            _lastCreatedIds.Clear();
            CanUndoLastTurn = false;
        }

        public bool UndoLastTurn(RhinoDoc doc)
        {
            if (doc is null)
            {
                return false;
            }

            bool undone = doc.Undo();
            if (undone)
            {
                CanUndoLastTurn = false;
                doc.Views?.Redraw();
            }

            return undone;
        }

        public RhinoExecutionResult SpawnDemoScene(RhinoDoc doc)
        {
            var demoPlan = new RhinoActionPlan
            {
                Explanation = "Created a multi-object architectural pedestal and colonnade demo scene on layer 'GH Copilot Demo'.",
                Ops = new List<RhinoActionOp>
                {
                    new RhinoActionOp { Op = "create_layer", Layer = "GH Copilot Demo", Color = new[] { 56, 189, 248 }, Visible = true },
                    new RhinoActionOp { Op = "add_box", Origin = new[] { 0.0, 0.0, 0.0 }, Width = 24.0, Depth = 24.0, Height = 3.0, Centered = false, Layer = "GH Copilot Demo", Name = "Plinth", Color = new[] { 52, 211, 153 } },
                    new RhinoActionOp { Op = "add_sphere", Center = new[] { 12.0, 12.0, 8.0 }, Radius = 5.0, Layer = "GH Copilot Demo", Name = "Orb", Color = new[] { 56, 189, 248 } },
                    new RhinoActionOp { Op = "add_cylinder", Origin = new[] { 3.0, 3.0, 3.0 }, Radius = 1.2, Height = 12.0, Capped = true, Layer = "GH Copilot Demo", Name = "Column_SW", Color = new[] { 245, 158, 11 } },
                    new RhinoActionOp { Op = "add_cylinder", Origin = new[] { 21.0, 3.0, 3.0 }, Radius = 1.2, Height = 12.0, Capped = true, Layer = "GH Copilot Demo", Name = "Column_SE", Color = new[] { 245, 158, 11 } },
                    new RhinoActionOp { Op = "add_cylinder", Origin = new[] { 3.0, 21.0, 3.0 }, Radius = 1.2, Height = 12.0, Capped = true, Layer = "GH Copilot Demo", Name = "Column_NW", Color = new[] { 245, 158, 11 } },
                    new RhinoActionOp { Op = "add_cylinder", Origin = new[] { 21.0, 21.0, 3.0 }, Radius = 1.2, Height = 12.0, Capped = true, Layer = "GH Copilot Demo", Name = "Column_NE", Color = new[] { 245, 158, 11 } },
                    new RhinoActionOp { Op = "set_display_mode", DisplayMode = "Shaded" },
                    new RhinoActionOp { Op = "zoom", Scope = "extents" }
                }
            };

            return Execute(doc, demoPlan);
        }

        public RhinoExecutionResult Execute(RhinoDoc doc, RhinoActionPlan plan)
        {
            if (doc is null) throw new ArgumentNullException(nameof(doc));
            if (plan is null) throw new ArgumentNullException(nameof(plan));

            var result = new RhinoExecutionResult();
            var turnCreatedIds = new List<Guid>();

            uint undoSerial = doc.BeginUndoRecord("GH Copilot: Direct Rhino Action");
            try
            {
                foreach (RhinoActionOp op in plan.Ops)
                {
                    ExecuteOp(doc, op, turnCreatedIds, result);
                }

                if (turnCreatedIds.Count > 0)
                {
                    _lastCreatedIds.Clear();
                    _lastCreatedIds.AddRange(turnCreatedIds);
                }

                if (result.MadeDocumentChanges && undoSerial > 0)
                {
                    CanUndoLastTurn = true;
                }
            }
            finally
            {
                if (undoSerial > 0)
                {
                    doc.EndUndoRecord(undoSerial);
                }
                doc.Views?.Redraw();
            }

            return result;
        }

        private void ExecuteOp(RhinoDoc doc, RhinoActionOp op, List<Guid> turnCreatedIds, RhinoExecutionResult result)
        {
            string name = (op.Op ?? string.Empty).Trim().ToLowerInvariant();
            switch (name)
            {
                case "add_point":
                {
                    ObjectAttributes attrs = BuildAttributes(doc, op, result);
                    Guid id = doc.Objects.AddPoint(ToPoint3d(op.Point), attrs);
                    RecordCreated(id, turnCreatedIds, result, "Added point");
                    break;
                }

                case "add_line":
                {
                    ObjectAttributes attrs = BuildAttributes(doc, op, result);
                    Guid id = doc.Objects.AddLine(ToPoint3d(op.Start), ToPoint3d(op.End), attrs);
                    RecordCreated(id, turnCreatedIds, result, "Added line");
                    break;
                }

                case "add_circle":
                {
                    ObjectAttributes attrs = BuildAttributes(doc, op, result);
                    Point3d center = ToPoint3d(op.Center ?? op.Origin);
                    Plane plane = BuildPlane(center, op.Normal);
                    var circle = new Circle(plane, op.Radius!.Value);
                    Guid id = doc.Objects.AddCircle(circle, attrs);
                    RecordCreated(id, turnCreatedIds, result, "Added circle");
                    break;
                }

                case "add_rectangle":
                {
                    ObjectAttributes attrs = BuildAttributes(doc, op, result);
                    Point3d origin = ToPoint3d(op.Origin ?? op.Center);
                    Plane plane = BuildPlane(origin, op.Normal);
                    double w = op.Width!.Value;
                    double d = (op.Depth ?? op.Height)!.Value;
                    bool centered = op.Centered == true;
                    Interval xInt = centered ? new Interval(-w * 0.5, w * 0.5) : new Interval(0, w);
                    Interval yInt = centered ? new Interval(-d * 0.5, d * 0.5) : new Interval(0, d);
                    var rect = new Rectangle3d(plane, xInt, yInt);
                    Guid id = doc.Objects.AddPolyline(rect.ToPolyline(), attrs);
                    RecordCreated(id, turnCreatedIds, result, "Added rectangle");
                    break;
                }

                case "add_polyline":
                {
                    ObjectAttributes attrs = BuildAttributes(doc, op, result);
                    var pts = new List<Point3d>(op.Points!.Count + 1);
                    foreach (double[] raw in op.Points)
                    {
                        pts.Add(ToPoint3d(raw));
                    }

                    bool closed = op.Closed == true;
                    if (closed && pts.Count >= 3 && pts[0].DistanceTo(pts[pts.Count - 1]) > doc.ModelAbsoluteTolerance)
                    {
                        pts.Add(pts[0]);
                    }

                    Guid id;
                    if (op.Smooth == true && pts.Count >= 3)
                    {
                        Curve smoothCurve = Curve.CreateInterpolatedCurve(pts, 3);
                        id = smoothCurve != null
                            ? doc.Objects.AddCurve(smoothCurve, attrs)
                            : doc.Objects.AddPolyline(pts, attrs);
                    }
                    else
                    {
                        id = doc.Objects.AddPolyline(pts, attrs);
                    }

                    RecordCreated(id, turnCreatedIds, result, "Added polyline/curve");
                    break;
                }

                case "add_box":
                {
                    ObjectAttributes attrs = BuildAttributes(doc, op, result);
                    Point3d origin = ToPoint3d(op.Origin ?? op.Center);
                    double w = op.Width!.Value;
                    double d = op.Depth!.Value;
                    double h = op.Height!.Value;
                    bool centered = op.Centered == true;
                    Interval ix = centered ? new Interval(-w * 0.5, w * 0.5) : new Interval(0, w);
                    Interval iy = centered ? new Interval(-d * 0.5, d * 0.5) : new Interval(0, d);
                    Interval iz = centered ? new Interval(-h * 0.5, h * 0.5) : new Interval(0, h);
                    Plane plane = Plane.WorldXY;
                    plane.Origin = origin;
                    var box = new Box(plane, ix, iy, iz);
                    Guid id = doc.Objects.AddBox(box, attrs);
                    RecordCreated(id, turnCreatedIds, result, "Added box");
                    break;
                }

                case "add_sphere":
                {
                    ObjectAttributes attrs = BuildAttributes(doc, op, result);
                    Point3d center = ToPoint3d(op.Center ?? op.Origin);
                    var sphere = new Sphere(center, op.Radius!.Value);
                    Guid id = doc.Objects.AddSphere(sphere, attrs);
                    RecordCreated(id, turnCreatedIds, result, "Added sphere");
                    break;
                }

                case "add_cylinder":
                {
                    ObjectAttributes attrs = BuildAttributes(doc, op, result);
                    Point3d baseCenter = ToPoint3d(op.Origin ?? op.Center);
                    Plane plane = BuildPlane(baseCenter, op.Normal);
                    var circle = new Circle(plane, op.Radius!.Value);
                    var cylinder = new Cylinder(circle, op.Height!.Value);
                    bool capped = op.Capped != false;
                    Brep? brep = Brep.CreateFromCylinder(cylinder, capped, capped);
                    if (brep != null)
                    {
                        Guid id = doc.Objects.AddBrep(brep, attrs);
                        RecordCreated(id, turnCreatedIds, result, "Added cylinder");
                    }
                    else
                    {
                        result.Warnings.Add("Failed to build cylinder Brep from parameters.");
                    }
                    break;
                }

                case "add_cone":
                {
                    ObjectAttributes attrs = BuildAttributes(doc, op, result);
                    Point3d baseCenter = ToPoint3d(op.Origin ?? op.Center);
                    double r = op.Radius!.Value;
                    double h = op.Height!.Value;
                    Vector3d axisDir = op.Normal != null ? ToVector3d(op.Normal) : Vector3d.ZAxis;
                    axisDir.Unitize();
                    // RhinoCommon Cone(plane, height, radius) puts the apex at plane.Origin and the base at height * plane.ZAxis.
                    var apexPlane = new Plane(baseCenter + axisDir * h, axisDir);
                    var cone = new Cone(apexPlane, -h, r);
                    bool capped = op.Capped != false;
                    Brep? brep = Brep.CreateFromCone(cone, capped);
                    if (brep != null)
                    {
                        Guid id = doc.Objects.AddBrep(brep, attrs);
                        RecordCreated(id, turnCreatedIds, result, "Added cone");
                    }
                    else
                    {
                        result.Warnings.Add("Failed to build cone Brep from parameters.");
                    }
                    break;
                }

                case "extrude":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, defaultToAllWhenShow: false);
                    int extruded = 0;
                    bool capped = op.Capped != false;
                    double h = op.Height!.Value;
                    ObjectAttributes? customAttrs = HasAttributeOverride(op) ? BuildAttributes(doc, op, result) : null;

                    foreach (RhinoObject obj in targets)
                    {
                        if (obj.Geometry is not Curve crv) continue;

                        double directedHeight = h;
                        if (crv.TryGetPlane(out Plane crvPlane) && crvPlane.ZAxis.Z < -0.5)
                        {
                            directedHeight = -h;
                        }

                        ObjectAttributes attrs = customAttrs != null ? customAttrs.Duplicate() : obj.Attributes.Duplicate();
                        Extrusion? extrusion = Extrusion.Create(crv, directedHeight, capped);
                        if (extrusion != null)
                        {
                            Guid id = doc.Objects.AddExtrusion(extrusion, attrs);
                            if (id != Guid.Empty)
                            {
                                turnCreatedIds.Add(id);
                                result.CreatedCount++;
                                extruded++;
                            }
                            continue;
                        }

                        Surface? surface = Surface.CreateExtrusion(crv, new Vector3d(0, 0, h));
                        if (surface != null)
                        {
                            Guid id = doc.Objects.AddSurface(surface, attrs);
                            if (id != Guid.Empty)
                            {
                                turnCreatedIds.Add(id);
                                result.CreatedCount++;
                                extruded++;
                            }
                        }
                    }

                    if (extruded == 0) result.Warnings.Add("No valid curves found to extrude.");
                    else result.AppliedNotes.Add($"Extruded {extruded} curve(s)");
                    break;
                }

                case "loft":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, defaultToAllWhenShow: false);
                    var curves = new List<Curve>();
                    foreach (RhinoObject obj in targets)
                    {
                        if (obj.Geometry is Curve c) curves.Add(c);
                    }

                    if (curves.Count < 2)
                    {
                        result.Warnings.Add("Loft requires at least 2 target curves.");
                        break;
                    }

                    LoftType loftType = ParseLoftType(op.LoftType);
                    Brep[]? breps = Brep.CreateFromLoft(curves, Point3d.Unset, Point3d.Unset, loftType, op.Closed == true);
                    if (breps is null || breps.Length == 0)
                    {
                        result.Warnings.Add("Rhino Loft could not create surface from the selected curves.");
                        break;
                    }

                    ObjectAttributes attrs = BuildAttributes(doc, op, result);
                    int created = 0;
                    foreach (Brep b in breps)
                    {
                        Guid id = doc.Objects.AddBrep(b, attrs);
                        if (id != Guid.Empty)
                        {
                            turnCreatedIds.Add(id);
                            result.CreatedCount++;
                            created++;
                        }
                    }
                    result.AppliedNotes.Add($"Created {created} lofted surface/Brep(s)");
                    break;
                }

                case "offset_curve":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, defaultToAllWhenShow: false);
                    int created = 0;
                    double dist = op.Distance!.Value;
                    Plane offsetPlane = BuildPlane(Point3d.Origin, op.Normal);
                    ObjectAttributes? customAttrs = HasAttributeOverride(op) ? BuildAttributes(doc, op, result) : null;

                    foreach (RhinoObject obj in targets)
                    {
                        if (obj.Geometry is not Curve crv) continue;
                        Curve[]? offsets = crv.Offset(offsetPlane, dist, doc.ModelAbsoluteTolerance, CurveOffsetCornerStyle.Sharp);
                        if (offsets is null) continue;

                        ObjectAttributes attrs = customAttrs != null ? customAttrs.Duplicate() : obj.Attributes.Duplicate();
                        foreach (Curve offsetCurve in offsets)
                        {
                            Guid id = doc.Objects.AddCurve(offsetCurve, attrs);
                            if (id != Guid.Empty)
                            {
                                turnCreatedIds.Add(id);
                                result.CreatedCount++;
                                created++;
                            }
                        }
                    }

                    if (created == 0) result.Warnings.Add("No valid curves found to offset.");
                    else result.AppliedNotes.Add($"Offset {created} curve(s)");
                    break;
                }

                case "move":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, defaultToAllWhenShow: false);
                    Transform xform = Transform.Translation(ToVector3d(op.Vector));
                    ApplyTransform(doc, targets, xform, deleteOriginal: true, turnCreatedIds, result, "Moved");
                    break;
                }

                case "copy":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, defaultToAllWhenShow: false);
                    Vector3d delta = op.Vector != null ? ToVector3d(op.Vector) : Vector3d.Zero;
                    Transform xform = Transform.Translation(delta);
                    ApplyTransform(doc, targets, xform, deleteOriginal: false, turnCreatedIds, result, "Copied");
                    break;
                }

                case "rotate":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, defaultToAllWhenShow: false);
                    Point3d pivot = op.Center != null || op.Origin != null
                        ? ToPoint3d(op.Center ?? op.Origin)
                        : ComputeAnchor(targets);
                    Vector3d axis = op.Axis != null ? ToVector3d(op.Axis) : Vector3d.ZAxis;
                    double radians = op.AngleDegrees!.Value * (Math.PI / 180.0);
                    Transform xform = Transform.Rotation(radians, axis, pivot);
                    ApplyTransform(doc, targets, xform, deleteOriginal: true, turnCreatedIds, result, "Rotated");
                    break;
                }

                case "scale":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, defaultToAllWhenShow: false);
                    Point3d pivot = op.Center != null || op.Origin != null
                        ? ToPoint3d(op.Center ?? op.Origin)
                        : ComputeAnchor(targets);
                    Transform xform;
                    if (op.Factors != null && op.Factors.Length == 3)
                    {
                        Plane scalePlane = Plane.WorldXY;
                        scalePlane.Origin = pivot;
                        xform = Transform.Scale(scalePlane, op.Factors[0], op.Factors[1], op.Factors[2]);
                    }
                    else
                    {
                        xform = Transform.Scale(pivot, op.Factor!.Value);
                    }
                    ApplyTransform(doc, targets, xform, deleteOriginal: true, turnCreatedIds, result, "Scaled");
                    break;
                }

                case "array_linear":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, defaultToAllWhenShow: false);
                    if (targets.Count == 0)
                    {
                        result.Warnings.Add("No target objects matched for linear array.");
                        break;
                    }

                    Vector3d step = ToVector3d(op.Vector);
                    int count = op.Count!.Value;
                    int created = 0;
                    for (int i = 1; i < count; i++)
                    {
                        Transform xform = Transform.Translation(step * i);
                        foreach (RhinoObject obj in targets)
                        {
                            Guid newId = doc.Objects.Transform(obj, xform, deleteOriginal: false);
                            if (newId != Guid.Empty)
                            {
                                turnCreatedIds.Add(newId);
                                result.CreatedCount++;
                                created++;
                            }
                        }
                    }
                    result.AppliedNotes.Add($"Created linear array ({created} new instances)");
                    break;
                }

                case "array_polar":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, defaultToAllWhenShow: false);
                    if (targets.Count == 0)
                    {
                        result.Warnings.Add("No target objects matched for polar array.");
                        break;
                    }

                    Point3d center = op.Center != null || op.Origin != null ? ToPoint3d(op.Center ?? op.Origin) : Point3d.Origin;
                    Vector3d axis = op.Axis != null ? ToVector3d(op.Axis) : Vector3d.ZAxis;
                    int count = op.Count!.Value;
                    double totalDeg = op.TotalAngleDegrees ?? 360.0;
                    double stepDeg = Math.Abs(Math.Abs(totalDeg) - 360.0) < 1e-6
                        ? totalDeg / count
                        : totalDeg / (count - 1);

                    int created = 0;
                    for (int i = 1; i < count; i++)
                    {
                        double rad = (stepDeg * i) * (Math.PI / 180.0);
                        Transform xform = Transform.Rotation(rad, axis, center);
                        foreach (RhinoObject obj in targets)
                        {
                            Guid newId = doc.Objects.Transform(obj, xform, deleteOriginal: false);
                            if (newId != Guid.Empty)
                            {
                                turnCreatedIds.Add(newId);
                                result.CreatedCount++;
                                created++;
                            }
                        }
                    }
                    result.AppliedNotes.Add($"Created polar array ({created} new instances)");
                    break;
                }

                case "delete":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, defaultToAllWhenShow: false);
                    int deleted = 0;
                    foreach (RhinoObject obj in targets)
                    {
                        if (doc.Objects.Delete(obj, quiet: true))
                        {
                            deleted++;
                        }
                    }
                    result.DeletedCount += deleted;
                    if (deleted == 0) result.Warnings.Add("No matching objects found to delete.");
                    else result.AppliedNotes.Add($"Deleted {deleted} object(s)");
                    break;
                }

                case "create_layer":
                {
                    int idx = EnsureLayer(doc, op.Layer!.Trim(), op.Color, op.Visible);
                    if (idx >= 0)
                    {
                        result.ModifiedCount++;
                        result.AppliedNotes.Add($"Ensured layer '{op.Layer.Trim()}'");
                    }
                    else
                    {
                        result.Warnings.Add($"Could not create or update layer '{op.Layer}'.");
                    }
                    break;
                }

                case "set_layer":
                {
                    int layerIdx = EnsureLayer(doc, op.Layer!.Trim(), null, null);
                    if (layerIdx < 0)
                    {
                        result.Warnings.Add($"Could not resolve layer '{op.Layer}'.");
                        break;
                    }

                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, defaultToAllWhenShow: false);
                    int updated = 0;
                    foreach (RhinoObject obj in targets)
                    {
                        ObjectAttributes attrs = obj.Attributes.Duplicate();
                        attrs.LayerIndex = layerIdx;
                        if (doc.Objects.ModifyAttributes(obj, attrs, quiet: true))
                        {
                            updated++;
                        }
                    }
                    result.ModifiedCount += updated;
                    if (updated == 0) result.Warnings.Add($"No target objects found to move to layer '{op.Layer}'.");
                    else result.AppliedNotes.Add($"Moved {updated} object(s) to layer '{op.Layer.Trim()}'");
                    break;
                }

                case "set_color":
                {
                    int[] rgb = op.Color!;
                    Color color = Color.FromArgb(ClampByte(rgb[0]), ClampByte(rgb[1]), ClampByte(rgb[2]));
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, defaultToAllWhenShow: false);
                    int updated = 0;
                    foreach (RhinoObject obj in targets)
                    {
                        ObjectAttributes attrs = obj.Attributes.Duplicate();
                        attrs.ColorSource = ObjectColorSource.ColorFromObject;
                        attrs.ObjectColor = color;
                        if (doc.Objects.ModifyAttributes(obj, attrs, quiet: true))
                        {
                            updated++;
                        }
                    }
                    result.ModifiedCount += updated;
                    if (updated == 0) result.Warnings.Add("No target objects found to recolor.");
                    else result.AppliedNotes.Add($"Colored {updated} object(s)");
                    break;
                }

                case "rename":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, defaultToAllWhenShow: false);
                    string newName = op.Name!.Trim();
                    int updated = 0;
                    foreach (RhinoObject obj in targets)
                    {
                        ObjectAttributes attrs = obj.Attributes.Duplicate();
                        attrs.Name = newName;
                        if (doc.Objects.ModifyAttributes(obj, attrs, quiet: true))
                        {
                            updated++;
                        }
                    }
                    result.ModifiedCount += updated;
                    if (updated == 0) result.Warnings.Add("No target objects found to rename.");
                    else result.AppliedNotes.Add($"Renamed {updated} object(s) to '{newName}'");
                    break;
                }

                case "select":
                {
                    if (op.ClearFirst != false)
                    {
                        doc.Objects.UnselectAll();
                    }

                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, defaultToAllWhenShow: false);
                    var ids = targets.Select(o => o.Id).ToList();
                    int selected = ids.Count > 0 ? doc.Objects.Select(ids) : 0;
                    result.AppliedNotes.Add($"Selected {selected} object(s)");
                    break;
                }

                case "hide":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, defaultToAllWhenShow: false);
                    int hidden = 0;
                    foreach (RhinoObject obj in targets)
                    {
                        if (doc.Objects.Hide(obj, ignoreLayerMode: true))
                        {
                            hidden++;
                        }
                    }
                    result.ModifiedCount += hidden;
                    if (hidden == 0) result.Warnings.Add("No visible target objects found to hide.");
                    else result.AppliedNotes.Add($"Hid {hidden} object(s)");
                    break;
                }

                case "show":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, defaultToAllWhenShow: true);
                    int shown = 0;
                    foreach (RhinoObject obj in targets)
                    {
                        if (obj.IsHidden && doc.Objects.Show(obj, ignoreLayerMode: true))
                        {
                            shown++;
                        }
                    }
                    result.ModifiedCount += shown;
                    result.AppliedNotes.Add($"Showed {shown} hidden object(s)");
                    break;
                }

                case "set_display_mode":
                {
                    RhinoView? activeView = doc.Views?.ActiveView;
                    if (activeView?.ActiveViewport is null)
                    {
                        result.Warnings.Add("No active Rhino viewport available.");
                        break;
                    }

                    string requested = op.DisplayMode!.Trim();
                    DisplayModeDescription? mode = DisplayModeDescription.FindByName(requested);
                    if (mode is null)
                    {
                        DisplayModeDescription[]? allModes = DisplayModeDescription.GetDisplayModes();
                        if (allModes != null)
                        {
                            mode = allModes.FirstOrDefault(m =>
                                string.Equals(m.EnglishName, requested, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(m.LocalName, requested, StringComparison.OrdinalIgnoreCase));
                        }
                    }

                    if (mode is null)
                    {
                        result.Warnings.Add($"Display mode '{requested}' was not found in Rhino.");
                        break;
                    }

                    activeView.ActiveViewport.DisplayMode = mode;
                    result.ViewportUpdated = true;
                    result.AppliedNotes.Add($"Switched display mode to '{mode.EnglishName}'");
                    break;
                }

                case "zoom":
                {
                    RhinoView? activeView = doc.Views?.ActiveView;
                    if (activeView?.ActiveViewport is null)
                    {
                        result.Warnings.Add("No active Rhino viewport available.");
                        break;
                    }

                    bool zoomSelected = string.Equals(op.Scope?.Trim(), "selected", StringComparison.OrdinalIgnoreCase);
                    bool ok = zoomSelected
                        ? activeView.ActiveViewport.ZoomExtentsSelected()
                        : activeView.ActiveViewport.ZoomExtents();
                    if (ok)
                    {
                        result.ViewportUpdated = true;
                        result.AppliedNotes.Add(zoomSelected ? "Zoomed to selection" : "Zoomed extents");
                    }
                    else
                    {
                        result.Warnings.Add(zoomSelected ? "No selected objects to zoom to." : "Viewport contains nothing to zoom.");
                    }
                    break;
                }

                case "set_view":
                {
                    string requested = op.ViewName!.Trim();
                    RhinoView? targetView = doc.Views?.Find(requested, false);
                    if (targetView != null)
                    {
                        doc.Views!.ActiveView = targetView;
                        result.ViewportUpdated = true;
                        result.AppliedNotes.Add($"Activated viewport '{targetView.ActiveViewport.Name}'");
                        break;
                    }

                    RhinoView? activeView = doc.Views?.ActiveView;
                    if (activeView?.ActiveViewport != null &&
                        Enum.TryParse(requested.Replace(" ", string.Empty), ignoreCase: true, out DefinedViewportProjection proj) &&
                        proj != DefinedViewportProjection.None)
                    {
                        activeView.ActiveViewport.SetProjection(proj, requested, updateConstructionPlane: true);
                        result.ViewportUpdated = true;
                        result.AppliedNotes.Add($"Set active viewport projection to '{proj}'");
                    }
                    else
                    {
                        result.Warnings.Add($"Viewport '{requested}' was not found.");
                    }
                    break;
                }

                case "set_camera":
                {
                    RhinoView? activeView = doc.Views?.ActiveView;
                    if (activeView?.ActiveViewport is null)
                    {
                        result.Warnings.Add("No active Rhino viewport available.");
                        break;
                    }

                    Point3d loc = ToPoint3d(op.CameraLocation);
                    Point3d tgt = ToPoint3d(op.CameraTarget);
                    activeView.ActiveViewport.SetCameraLocations(tgt, loc);
                    result.ViewportUpdated = true;
                    result.AppliedNotes.Add("Updated viewport camera");
                    break;
                }
            }
        }

        private void RecordCreated(Guid id, List<Guid> turnCreatedIds, RhinoExecutionResult result, string note)
        {
            if (id != Guid.Empty)
            {
                turnCreatedIds.Add(id);
                result.CreatedCount++;
                result.AppliedNotes.Add(note);
            }
            else
            {
                result.Warnings.Add($"RhinoDoc failed operation: {note}.");
            }
        }

        private void ApplyTransform(
            RhinoDoc doc,
            List<RhinoObject> targets,
            Transform xform,
            bool deleteOriginal,
            List<Guid> turnCreatedIds,
            RhinoExecutionResult result,
            string verb)
        {
            if (targets.Count == 0)
            {
                result.Warnings.Add($"No target objects matched for '{verb.ToLowerInvariant()}'.");
                return;
            }

            int affected = 0;
            var replacementIds = new List<Guid>(targets.Count);
            foreach (RhinoObject obj in targets)
            {
                Guid oldId = obj.Id;
                Guid newId = doc.Objects.Transform(obj, xform, deleteOriginal: deleteOriginal);
                if (newId != Guid.Empty)
                {
                    affected++;
                    replacementIds.Add(newId);
                    if (deleteOriginal)
                    {
                        int idx = turnCreatedIds.IndexOf(oldId);
                        if (idx >= 0) turnCreatedIds[idx] = newId;
                    }
                    else
                    {
                        turnCreatedIds.Add(newId);
                    }
                }
            }

            if (deleteOriginal) result.ModifiedCount += affected;
            else
            {
                result.CreatedCount += affected;
                if (replacementIds.Count > 0)
                {
                    _lastCreatedIds.Clear();
                    _lastCreatedIds.AddRange(replacementIds);
                }
            }

            result.AppliedNotes.Add($"{verb} {affected} object(s)");
        }

        private List<RhinoObject> ResolveTargets(
            RhinoDoc doc,
            string? rawTarget,
            List<Guid> turnCreatedIds,
            bool defaultToAllWhenShow)
        {
            string target = string.IsNullOrWhiteSpace(rawTarget)
                ? string.Empty
                : rawTarget!.Trim();

            if (string.IsNullOrEmpty(target))
            {
                if (defaultToAllWhenShow)
                {
                    target = "all";
                }
                else
                {
                    List<RhinoObject> selected = CollectSelected(doc);
                    if (selected.Count > 0) return selected;
                    if (turnCreatedIds.Count > 0 || _lastCreatedIds.Count > 0) target = "last_created";
                    else return selected;
                }
            }

            if (string.Equals(target, "selected", StringComparison.OrdinalIgnoreCase))
            {
                return CollectSelected(doc);
            }

            if (string.Equals(target, "last_created", StringComparison.OrdinalIgnoreCase))
            {
                IEnumerable<Guid> source = turnCreatedIds.Count > 0 ? turnCreatedIds : _lastCreatedIds;
                var list = new List<RhinoObject>();
                foreach (Guid id in source)
                {
                    RhinoObject? obj = doc.Objects.FindId(id);
                    if (obj != null && !obj.IsDeleted)
                    {
                        list.Add(obj);
                    }
                }
                return list;
            }

            var settings = new ObjectEnumeratorSettings
            {
                NormalObjects = true,
                HiddenObjects = defaultToAllWhenShow,
                DeletedObjects = false,
                LockedObjects = false
            };

            if (string.Equals(target, "all", StringComparison.OrdinalIgnoreCase))
            {
                return doc.Objects.GetObjectList(settings)?.ToList() ?? new List<RhinoObject>();
            }

            if (target.StartsWith("layer:", StringComparison.OrdinalIgnoreCase))
            {
                string layerName = target.Substring("layer:".Length).Trim();
                int idx = doc.Layers.FindByFullPath(layerName, -1);
                if (idx < 0)
                {
                    Layer? shortMatch = doc.Layers.FindName(layerName);
                    if (shortMatch != null && !shortMatch.IsDeleted) idx = shortMatch.Index;
                }
                if (idx < 0) return new List<RhinoObject>();
                settings.LayerIndexFilter = idx;
                return doc.Objects.GetObjectList(settings)?.ToList() ?? new List<RhinoObject>();
            }

            if (target.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
            {
                string objName = target.Substring("name:".Length).Trim();
                settings.NameFilter = objName;
                return doc.Objects.GetObjectList(settings)?.ToList() ?? new List<RhinoObject>();
            }

            if (target.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
            {
                string typeSuffix = target.Substring("type:".Length).Trim().ToLowerInvariant();
                settings.ObjectTypeFilter = MapObjectTypeFilter(typeSuffix);
                return doc.Objects.GetObjectList(settings)?.ToList() ?? new List<RhinoObject>();
            }

            return new List<RhinoObject>();
        }

        private static List<RhinoObject> CollectSelected(RhinoDoc doc)
        {
            var result = new List<RhinoObject>();
            IEnumerable<RhinoObject>? selected = doc.Objects.GetSelectedObjects(includeLights: false, includeGrips: false);
            if (selected is null) return result;
            foreach (RhinoObject obj in selected)
            {
                if (obj != null && !obj.IsDeleted)
                {
                    result.Add(obj);
                }
            }
            return result;
        }

        private static ObjectType MapObjectTypeFilter(string typeSuffix)
        {
            switch (typeSuffix)
            {
                case "point": return ObjectType.Point;
                case "curve": return ObjectType.Curve;
                case "brep":
                case "polysurface":
                case "solid": return ObjectType.Brep;
                case "extrusion": return ObjectType.Extrusion;
                case "surface": return ObjectType.Surface;
                case "mesh": return ObjectType.Mesh;
                case "subd": return ObjectType.SubD;
                default: return ObjectType.AnyObject;
            }
        }

        private ObjectAttributes BuildAttributes(RhinoDoc doc, RhinoActionOp op, RhinoExecutionResult result)
        {
            ObjectAttributes attrs = doc.CreateDefaultAttributes();
            if (!string.IsNullOrWhiteSpace(op.Layer))
            {
                int layerIdx = EnsureLayer(doc, op.Layer!.Trim(), null, null);
                if (layerIdx >= 0)
                {
                    attrs.LayerIndex = layerIdx;
                }
                else
                {
                    result.Warnings.Add($"Could not resolve or create layer '{op.Layer}'.");
                }
            }

            if (!string.IsNullOrWhiteSpace(op.Name))
            {
                attrs.Name = op.Name!.Trim();
            }

            if (op.Color != null && op.Color.Length == 3)
            {
                attrs.ColorSource = ObjectColorSource.ColorFromObject;
                attrs.ObjectColor = Color.FromArgb(ClampByte(op.Color[0]), ClampByte(op.Color[1]), ClampByte(op.Color[2]));
            }

            return attrs;
        }

        private static bool HasAttributeOverride(RhinoActionOp op) =>
            !string.IsNullOrWhiteSpace(op.Layer) ||
            !string.IsNullOrWhiteSpace(op.Name) ||
            (op.Color != null && op.Color.Length == 3);

        private static int EnsureLayer(RhinoDoc doc, string layerPath, int[]? colorRgb, bool? visible)
        {
            string[] segments = layerPath.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return -1;
            }

            Guid parentId = Guid.Empty;
            string currentPath = string.Empty;
            int resolvedIndex = -1;

            for (int i = 0; i < segments.Length; i++)
            {
                string seg = segments[i].Trim();
                if (string.IsNullOrEmpty(seg)) return -1;

                bool isLast = i == segments.Length - 1;
                currentPath = string.IsNullOrEmpty(currentPath) ? seg : currentPath + "::" + seg;
                int idx = doc.Layers.FindByFullPath(currentPath, -1);

                if (idx < 0)
                {
                    var layer = new Layer { Name = seg };
                    if (parentId != Guid.Empty)
                    {
                        layer.ParentLayerId = parentId;
                    }

                    if (isLast)
                    {
                        if (colorRgb != null && colorRgb.Length == 3)
                        {
                            layer.Color = Color.FromArgb(ClampByte(colorRgb[0]), ClampByte(colorRgb[1]), ClampByte(colorRgb[2]));
                        }
                        if (visible.HasValue)
                        {
                            layer.IsVisible = visible.Value;
                        }
                    }

                    idx = doc.Layers.Add(layer);
                    if (idx < 0) return -1;
                }
                else if (isLast && (colorRgb != null || visible.HasValue))
                {
                    Layer existing = doc.Layers.FindIndex(idx);
                    if (existing != null)
                    {
                        var updated = new Layer();
                        updated.CopyAttributesFrom(existing);
                        if (colorRgb != null && colorRgb.Length == 3)
                        {
                            updated.Color = Color.FromArgb(ClampByte(colorRgb[0]), ClampByte(colorRgb[1]), ClampByte(colorRgb[2]));
                        }
                        if (visible.HasValue)
                        {
                            updated.IsVisible = visible.Value;
                        }
                        doc.Layers.Modify(updated, idx, quiet: true);
                    }
                }

                Layer resolvedLayer = doc.Layers.FindIndex(idx);
                parentId = resolvedLayer != null ? resolvedLayer.Id : Guid.Empty;
                resolvedIndex = idx;
            }

            return resolvedIndex;
        }

        private static Point3d ToPoint3d(double[]? coords) =>
            coords != null && coords.Length == 3 ? new Point3d(coords[0], coords[1], coords[2]) : Point3d.Origin;

        private static Vector3d ToVector3d(double[]? coords) =>
            coords != null && coords.Length == 3 ? new Vector3d(coords[0], coords[1], coords[2]) : Vector3d.ZAxis;

        private static Plane BuildPlane(Point3d origin, double[]? normalCoords)
        {
            if (normalCoords != null && normalCoords.Length == 3)
            {
                var normal = new Vector3d(normalCoords[0], normalCoords[1], normalCoords[2]);
                if (!normal.IsZero)
                {
                    return new Plane(origin, normal);
                }
            }

            Plane p = Plane.WorldXY;
            p.Origin = origin;
            return p;
        }

        private static Point3d ComputeAnchor(List<RhinoObject> targets)
        {
            BoundingBox bounds = BoundingBox.Unset;
            foreach (RhinoObject obj in targets)
            {
                if (obj.Geometry is null) continue;
                BoundingBox box = obj.Geometry.GetBoundingBox(false);
                if (box.IsValid)
                {
                    bounds = bounds.IsValid ? BoundingBox.Union(bounds, box) : box;
                }
            }
            return bounds.IsValid ? bounds.Center : Point3d.Origin;
        }

        private static LoftType ParseLoftType(string? loftType)
        {
            switch ((loftType ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "loose": return LoftType.Loose;
                case "tight": return LoftType.Tight;
                case "straight": return LoftType.Straight;
                case "uniform": return LoftType.Uniform;
                default: return LoftType.Normal;
            }
        }

        private static int ClampByte(int value) => value < 0 ? 0 : (value > 255 ? 255 : value);
    }
}
