using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using GhCopilot.Core.Rhino;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Morphs;

namespace GhCopilot.Rhino.Host
{
    internal sealed class RhinoExecutionResult
    {
        public int CreatedCount { get; set; }
        public int ModifiedCount { get; set; }
        public int DeletedCount { get; set; }
        public bool ViewportUpdated { get; set; }
        public List<Guid> CreatedIds { get; } = new List<Guid>();
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
            var latestOpIds = new List<Guid>();

            uint undoSerial = doc.BeginUndoRecord("GH Copilot: Direct Rhino Action");
            try
            {
                foreach (RhinoActionOp op in plan.Ops)
                {
                    int beforeCount = turnCreatedIds.Count;
                    ExecuteOp(doc, op, turnCreatedIds, latestOpIds, result);
                    if (turnCreatedIds.Count > beforeCount)
                    {
                        latestOpIds.Clear();
                        for (int i = beforeCount; i < turnCreatedIds.Count; i++)
                        {
                            latestOpIds.Add(turnCreatedIds[i]);
                        }
                    }
                }

                turnCreatedIds.RemoveAll(id =>
                {
                    RhinoObject? obj = doc.Objects.FindId(id);
                    return obj is null || obj.IsDeleted;
                });

                if (turnCreatedIds.Count > 0)
                {
                    result.CreatedIds.AddRange(turnCreatedIds);
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

        private void ExecuteOp(
            RhinoDoc doc,
            RhinoActionOp op,
            List<Guid> turnCreatedIds,
            List<Guid> latestOpIds,
            RhinoExecutionResult result)
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

                case "add_arc":
                {
                    ObjectAttributes attrs = BuildAttributes(doc, op, result);
                    Arc arc;
                    if (op.Start != null && op.Point != null && op.End != null)
                    {
                        arc = new Arc(ToPoint3d(op.Start), ToPoint3d(op.Point), ToPoint3d(op.End));
                    }
                    else
                    {
                        Point3d center = ToPoint3d(op.Center ?? op.Origin);
                        Plane plane = BuildPlane(center, op.Normal);
                        double startRad = (op.StartAngleDegrees ?? 0.0) * (Math.PI / 180.0);
                        if (Math.Abs(startRad) > 1e-9)
                        {
                            plane.Rotate(startRad, plane.ZAxis, plane.Origin);
                        }
                        double sweepRad = op.AngleDegrees!.Value * (Math.PI / 180.0);
                        arc = new Arc(plane, op.Radius!.Value, sweepRad);
                    }

                    if (arc.IsValid)
                    {
                        Guid id = doc.Objects.AddArc(arc, attrs);
                        RecordCreated(id, turnCreatedIds, result, "Added arc");
                    }
                    else
                    {
                        result.Warnings.Add("Could not construct a valid arc from the supplied points/angles.");
                    }
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

                case "add_ellipse":
                {
                    ObjectAttributes attrs = BuildAttributes(doc, op, result);
                    Point3d center = ToPoint3d(op.Center ?? op.Origin);
                    Plane plane = BuildPlane(center, op.Normal);
                    double rx = op.RadiusX ?? (op.Width!.Value * 0.5);
                    double ry = op.RadiusY ?? ((op.Depth ?? op.Height)!.Value * 0.5);
                    var ellipse = new Ellipse(plane, rx, ry);
                    NurbsCurve? crv = ellipse.ToNurbsCurve();
                    if (crv != null)
                    {
                        Guid id = doc.Objects.AddCurve(crv, attrs);
                        RecordCreated(id, turnCreatedIds, result, "Added ellipse");
                    }
                    else
                    {
                        result.Warnings.Add("Failed to build ellipse curve.");
                    }
                    break;
                }

                case "add_polygon":
                {
                    ObjectAttributes attrs = BuildAttributes(doc, op, result);
                    Point3d center = ToPoint3d(op.Center ?? op.Origin);
                    Plane plane = BuildPlane(center, op.Normal);
                    int sides = op.Sides ?? op.Count ?? 6;
                    double r = op.Radius!.Value;
                    if (op.Inscribed == false)
                    {
                        r /= Math.Cos(Math.PI / sides);
                    }

                    var pts = new List<Point3d>(sides + 1);
                    for (int i = 0; i < sides; i++)
                    {
                        double angle = (2.0 * Math.PI * i) / sides;
                        pts.Add(plane.PointAt(r * Math.Cos(angle), r * Math.Sin(angle)));
                    }
                    pts.Add(pts[0]);

                    Guid id = doc.Objects.AddPolyline(pts, attrs);
                    RecordCreated(id, turnCreatedIds, result, $"Added {sides}-sided polygon");
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

                case "add_helix":
                {
                    ObjectAttributes attrs = BuildAttributes(doc, op, result);
                    Point3d axisStart = ToPoint3d(op.Origin ?? op.Start ?? op.Center);
                    Vector3d axisDir = op.Axis != null
                        ? ToVector3d(op.Axis)
                        : (op.Normal != null ? ToVector3d(op.Normal) : Vector3d.ZAxis);
                    axisDir.Unitize();
                    double h = op.Height!.Value;
                    if (h < 0.0)
                    {
                        axisDir = -axisDir;
                        h = -h;
                    }
                    double turns = op.Turns!.Value;
                    double pitch = h / turns;
                    double r0 = op.Radius!.Value;
                    double r1 = op.Radius2 ?? r0;
                    var basePlane = new Plane(axisStart, axisDir);
                    Point3d radiusPt = basePlane.PointAt(Math.Max(r0, 1e-3), 0.0);
                    NurbsCurve? spiral = NurbsCurve.CreateSpiral(axisStart, axisDir, radiusPt, pitch, turns, r0, r1);
                    if (spiral != null)
                    {
                        Guid id = doc.Objects.AddCurve(spiral, attrs);
                        RecordCreated(id, turnCreatedIds, result, "Added helix/spiral curve");
                    }
                    else
                    {
                        result.Warnings.Add("Failed to build helix/spiral curve.");
                    }
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

                case "add_torus":
                {
                    ObjectAttributes attrs = BuildAttributes(doc, op, result);
                    Point3d center = ToPoint3d(op.Center ?? op.Origin);
                    Plane plane = BuildPlane(center, op.Normal);
                    var torus = new Torus(plane, op.Radius!.Value, op.TubeRadius!.Value);
                    Brep? brep = torus.ToNurbsSurface()?.ToBrep();
                    if (brep != null)
                    {
                        Guid id = doc.Objects.AddBrep(brep, attrs);
                        RecordCreated(id, turnCreatedIds, result, "Added torus");
                    }
                    else
                    {
                        result.Warnings.Add("Failed to build torus Brep from parameters.");
                    }
                    break;
                }

                case "extrude":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
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
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false, preferMultipleTurnCreated: true);
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
                    foreach (Brep rawBrep in breps)
                    {
                        Brep toAdd = rawBrep;
                        if (op.Capped == true)
                        {
                            Brep? cappedBrep = rawBrep.CapPlanarHoles(doc.ModelAbsoluteTolerance);
                            if (cappedBrep != null) toAdd = cappedBrep;
                        }
                        Guid id = doc.Objects.AddBrep(toAdd, attrs);
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

                case "revolve":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
                    Point3d axisOrigin = ToPoint3d(op.Origin ?? op.Center);
                    Vector3d axisDir = op.Axis != null ? ToVector3d(op.Axis) : Vector3d.ZAxis;
                    axisDir.Unitize();
                    var axisLine = new Line(axisOrigin, axisOrigin + axisDir * 10.0);
                    double sweepDeg = op.AngleDegrees ?? 360.0;
                    double endRad = sweepDeg * (Math.PI / 180.0);
                    bool capped = op.Capped != false;
                    ObjectAttributes? customAttrs = HasAttributeOverride(op) ? BuildAttributes(doc, op, result) : null;
                    int created = 0;

                    foreach (RhinoObject obj in targets)
                    {
                        if (obj.Geometry is not Curve crv) continue;
                        RevSurface? rev = RevSurface.Create(crv, axisLine, 0.0, endRad);
                        if (rev is null) continue;
                        Brep? brep = Brep.CreateFromRevSurface(rev, capped, capped);
                        if (brep is null) continue;
                        ObjectAttributes attrs = customAttrs != null ? customAttrs.Duplicate() : obj.Attributes.Duplicate();
                        Guid id = doc.Objects.AddBrep(brep, attrs);
                        if (id != Guid.Empty)
                        {
                            turnCreatedIds.Add(id);
                            result.CreatedCount++;
                            created++;
                        }
                    }

                    if (created == 0) result.Warnings.Add("No valid profile curves could be revolved.");
                    else result.AppliedNotes.Add($"Revolved {created} surface/solid(s)");
                    break;
                }

                case "sweep1":
                {
                    Curve? rail = null;
                    var shapes = new List<Curve>();
                    if (!string.IsNullOrWhiteSpace(op.RailTarget))
                    {
                        List<RhinoObject> railObjs = ResolveTargets(doc, op.RailTarget, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
                        rail = railObjs.Select(o => o.Geometry as Curve).FirstOrDefault(c => c != null);
                        List<RhinoObject> sectionObjs = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
                        foreach (RhinoObject so in sectionObjs)
                        {
                            if (so.Geometry is Curve c && !ReferenceEquals(c, rail)) shapes.Add(c);
                        }
                    }
                    else
                    {
                        List<RhinoObject> allObjs = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false, preferMultipleTurnCreated: true);
                        foreach (RhinoObject o in allObjs)
                        {
                            if (o.Geometry is Curve c)
                            {
                                if (rail is null) rail = c;
                                else shapes.Add(c);
                            }
                        }
                    }

                    if (rail is null || shapes.Count == 0)
                    {
                        result.Warnings.Add("Sweep1 requires 1 rail curve and at least 1 cross-section curve.");
                        break;
                    }

                    Brep[]? swept = Brep.CreateFromSweep(rail, shapes, op.Closed == true, doc.ModelAbsoluteTolerance);
                    if (swept is null || swept.Length == 0)
                    {
                        result.Warnings.Add("Sweep1 could not create a surface/Brep from the rail and section curves.");
                        break;
                    }

                    ObjectAttributes attrs = BuildAttributes(doc, op, result);
                    int created = 0;
                    foreach (Brep rawBrep in swept)
                    {
                        Brep toAdd = rawBrep;
                        if (op.Capped != false)
                        {
                            Brep? cappedBrep = rawBrep.CapPlanarHoles(doc.ModelAbsoluteTolerance);
                            if (cappedBrep != null) toAdd = cappedBrep;
                        }
                        Guid id = doc.Objects.AddBrep(toAdd, attrs);
                        if (id != Guid.Empty)
                        {
                            turnCreatedIds.Add(id);
                            result.CreatedCount++;
                            created++;
                        }
                    }
                    result.AppliedNotes.Add($"Swept {created} Brep(s)");
                    break;
                }

                case "pipe":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
                    PipeCapMode cap = ParsePipeCapMode(op.CapMode);
                    double r1 = op.Radius!.Value;
                    double absTol = doc.ModelAbsoluteTolerance;
                    double angTol = doc.ModelAngleToleranceRadians;
                    ObjectAttributes? customAttrs = HasAttributeOverride(op) ? BuildAttributes(doc, op, result) : null;
                    int created = 0;

                    foreach (RhinoObject obj in targets)
                    {
                        if (obj.Geometry is not Curve crv) continue;
                        Curve railCopy = crv.DuplicateCurve();
                        railCopy.Domain = new Interval(0.0, 1.0);
                        Brep[]? pipes = op.Radius2.HasValue
                            ? Brep.CreatePipe(railCopy, new[] { 0.0, 1.0 }, new[] { r1, op.Radius2.Value }, false, cap, true, absTol, angTol)
                            : Brep.CreatePipe(railCopy, r1, false, cap, true, absTol, angTol);
                        if (pipes is null) continue;

                        ObjectAttributes attrs = customAttrs != null ? customAttrs.Duplicate() : obj.Attributes.Duplicate();
                        foreach (Brep p in pipes)
                        {
                            Guid id = doc.Objects.AddBrep(p, attrs);
                            if (id != Guid.Empty)
                            {
                                turnCreatedIds.Add(id);
                                result.CreatedCount++;
                                created++;
                            }
                        }
                    }

                    if (created == 0) result.Warnings.Add("No valid curves found to pipe.");
                    else result.AppliedNotes.Add($"Piped {created} tubular Brep(s)");
                    break;
                }

                case "planar_srf":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false, preferMultipleTurnCreated: true);
                    var curves = targets.Select(o => o.Geometry as Curve).Where(c => c != null).Cast<Curve>().ToList();
                    if (curves.Count == 0)
                    {
                        result.Warnings.Add("planar_srf requires at least 1 closed planar curve.");
                        break;
                    }

                    Brep[]? planar = Brep.CreatePlanarBreps(curves, doc.ModelAbsoluteTolerance);
                    if (planar is null || planar.Length == 0)
                    {
                        result.Warnings.Add("Curves were not closed/coplanar enough to build planar surfaces.");
                        break;
                    }

                    ObjectAttributes attrs = BuildAttributes(doc, op, result);
                    int created = 0;
                    foreach (Brep b in planar)
                    {
                        Guid id = doc.Objects.AddBrep(b, attrs);
                        if (id != Guid.Empty)
                        {
                            turnCreatedIds.Add(id);
                            result.CreatedCount++;
                            created++;
                        }
                    }
                    result.AppliedNotes.Add($"Created {created} planar surface(s)");
                    break;
                }

                case "cap_planar_holes":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
                    int cappedCount = 0;
                    foreach (RhinoObject obj in targets)
                    {
                        Brep? brep = ToBrepFromObject(obj);
                        if (brep is null) continue;
                        Brep? capped = brep.CapPlanarHoles(doc.ModelAbsoluteTolerance);
                        if (capped != null && doc.Objects.Replace(obj.Id, capped))
                        {
                            cappedCount++;
                        }
                    }
                    result.ModifiedCount += cappedCount;
                    if (cappedCount == 0) result.Warnings.Add("No open planar holes could be capped on target Breps.");
                    else result.AppliedNotes.Add($"Capped planar holes on {cappedCount} Brep(s)");
                    break;
                }

                case "offset_curve":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
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

                case "offset_surface":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
                    double dist = op.Distance!.Value;
                    bool solid = op.Solid != false;
                    ObjectAttributes? customAttrs = HasAttributeOverride(op) ? BuildAttributes(doc, op, result) : null;
                    int created = 0;

                    foreach (RhinoObject obj in targets)
                    {
                        Brep? brep = ToBrepFromObject(obj);
                        if (brep is null) continue;
                        Brep[]? offsets = Brep.CreateOffsetBrep(brep, dist, solid, extend: false, doc.ModelAbsoluteTolerance, out _, out _);
                        if (offsets is null) continue;

                        ObjectAttributes attrs = customAttrs != null ? customAttrs.Duplicate() : obj.Attributes.Duplicate();
                        foreach (Brep off in offsets)
                        {
                            Guid id = doc.Objects.AddBrep(off, attrs);
                            if (id != Guid.Empty)
                            {
                                turnCreatedIds.Add(id);
                                result.CreatedCount++;
                                created++;
                            }
                        }
                    }

                    if (created == 0) result.Warnings.Add("No valid surfaces/Breps found to offset into shells.");
                    else result.AppliedNotes.Add($"Offset {created} shell/surface Brep(s)");
                    break;
                }

                case "fillet_edges":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
                    double r = op.Radius!.Value;
                    BlendType blend = op.Chamfer == true ? BlendType.Chamfer : BlendType.Fillet;
                    ObjectAttributes? customAttrs = HasAttributeOverride(op) ? BuildAttributes(doc, op, result) : null;
                    int created = 0;

                    foreach (RhinoObject obj in targets)
                    {
                        Brep? brep = ToBrepFromObject(obj);
                        if (brep is null || brep.Edges.Count == 0) continue;
                        int edgeCount = brep.Edges.Count;
                        int[] indices = Enumerable.Range(0, edgeCount).ToArray();
                        double[] radii = Enumerable.Repeat(r, edgeCount).ToArray();
                        Brep[]? filleted = Brep.CreateFilletEdges(brep, indices, radii, radii, blend, RailType.RollingBall, doc.ModelAbsoluteTolerance);
                        if (filleted is null || filleted.Length == 0) continue;

                        ObjectAttributes attrs = customAttrs != null ? customAttrs.Duplicate() : obj.Attributes.Duplicate();
                        Guid oldId = obj.Id;
                        doc.Objects.Delete(obj, quiet: true);
                        turnCreatedIds.Remove(oldId);
                        result.DeletedCount++;

                        foreach (Brep fb in filleted)
                        {
                            Guid id = doc.Objects.AddBrep(fb, attrs);
                            if (id != Guid.Empty)
                            {
                                turnCreatedIds.Add(id);
                                result.CreatedCount++;
                                created++;
                            }
                        }
                    }

                    if (created == 0) result.Warnings.Add("Fillet/chamfer could not be applied at the requested radius.");
                    else result.AppliedNotes.Add($"Applied {(op.Chamfer == true ? "chamfer" : "fillet")} to {created} solid(s)");
                    break;
                }

                case "boolean_union":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false, preferMultipleTurnCreated: true);
                    var pairs = new List<(RhinoObject Obj, Brep Solid)>();
                    foreach (RhinoObject obj in targets)
                    {
                        Brep? b = ToBrepFromObject(obj);
                        if (b != null) pairs.Add((obj, b));
                    }

                    if (pairs.Count < 2)
                    {
                        result.Warnings.Add("boolean_union requires at least 2 target solids/Breps.");
                        break;
                    }

                    Brep[]? unioned = Brep.CreateBooleanUnion(pairs.Select(p => p.Solid), doc.ModelAbsoluteTolerance);
                    if (unioned is null || unioned.Length == 0)
                    {
                        result.Warnings.Add("Boolean union did not find intersecting solid volumes.");
                        break;
                    }

                    ObjectAttributes attrs = HasAttributeOverride(op) ? BuildAttributes(doc, op, result) : pairs[0].Obj.Attributes.Duplicate();
                    if (op.KeepCutters != true)
                    {
                        foreach ((RhinoObject srcObj, _) in pairs)
                        {
                            if (doc.Objects.Delete(srcObj, quiet: true))
                            {
                                turnCreatedIds.Remove(srcObj.Id);
                                result.DeletedCount++;
                            }
                        }
                    }

                    int created = 0;
                    foreach (Brep ub in unioned)
                    {
                        Guid id = doc.Objects.AddBrep(ub, attrs.Duplicate());
                        if (id != Guid.Empty)
                        {
                            turnCreatedIds.Add(id);
                            result.CreatedCount++;
                            created++;
                        }
                    }
                    result.AppliedNotes.Add($"Boolean-unioned into {created} solid(s)");
                    break;
                }

                case "boolean_difference":
                case "boolean_intersection":
                {
                    bool isDiff = name == "boolean_difference";
                    List<RhinoObject> baseObjs = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
                    List<RhinoObject> cutterObjs = ResolveTargets(doc, op.SubtractTarget, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
                    var cutterIdSet = new HashSet<Guid>(cutterObjs.Select(o => o.Id));
                    baseObjs.RemoveAll(o => cutterIdSet.Contains(o.Id));

                    List<Brep> baseBreps = baseObjs.Select(ToBrepFromObject).Where(b => b != null).Cast<Brep>().ToList();
                    List<Brep> cutterBreps = cutterObjs.Select(ToBrepFromObject).Where(b => b != null).Cast<Brep>().ToList();
                    if (baseBreps.Count == 0 || cutterBreps.Count == 0)
                    {
                        result.Warnings.Add($"{name} requires valid base solid(s) in 'target' and cutter solid(s) in 'subtractTarget'.");
                        break;
                    }

                    Brep[]? boolResult = isDiff
                        ? Brep.CreateBooleanDifference(baseBreps, cutterBreps, doc.ModelAbsoluteTolerance)
                        : Brep.CreateBooleanIntersection(baseBreps, cutterBreps, doc.ModelAbsoluteTolerance);

                    if (boolResult is null || boolResult.Length == 0)
                    {
                        result.Warnings.Add($"{name} produced an empty result (verify solids overlap cleanly).");
                        break;
                    }

                    ObjectAttributes attrs = HasAttributeOverride(op) ? BuildAttributes(doc, op, result) : baseObjs[0].Attributes.Duplicate();
                    foreach (RhinoObject bObj in baseObjs)
                    {
                        if (doc.Objects.Delete(bObj, quiet: true))
                        {
                            turnCreatedIds.Remove(bObj.Id);
                            result.DeletedCount++;
                        }
                    }
                    if (op.KeepCutters != true)
                    {
                        foreach (RhinoObject cObj in cutterObjs)
                        {
                            if (doc.Objects.Delete(cObj, quiet: true))
                            {
                                turnCreatedIds.Remove(cObj.Id);
                                result.DeletedCount++;
                            }
                        }
                    }

                    int created = 0;
                    foreach (Brep rb in boolResult)
                    {
                        Guid id = doc.Objects.AddBrep(rb, attrs.Duplicate());
                        if (id != Guid.Empty)
                        {
                            turnCreatedIds.Add(id);
                            result.CreatedCount++;
                            created++;
                        }
                    }
                    result.AppliedNotes.Add($"{(isDiff ? "Boolean difference" : "Boolean intersection")} created {created} solid(s)");
                    break;
                }

                case "contour_slice":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
                    BoundingBox bounds = ComputeBoundingBox(targets);
                    if (!bounds.IsValid)
                    {
                        result.Warnings.Add("No valid geometry found for contour_slice.");
                        break;
                    }

                    Point3d cStart = op.Start != null ? ToPoint3d(op.Start) : new Point3d(bounds.Center.X, bounds.Center.Y, bounds.Min.Z);
                    Point3d cEnd = op.End != null ? ToPoint3d(op.End) : new Point3d(bounds.Center.X, bounds.Center.Y, bounds.Max.Z);
                    Vector3d sliceDir = cEnd - cStart;
                    if (sliceDir.Length <= 1e-6)
                    {
                        sliceDir = Vector3d.ZAxis;
                        cEnd = cStart + sliceDir * Math.Max(1.0, bounds.Max.Z - bounds.Min.Z);
                    }
                    sliceDir.Unitize();

                    ObjectAttributes attrs = BuildAttributes(doc, op, result);
                    int created = 0;
                    double spacing = op.Distance!.Value;
                    double? slabThickness = op.ExtrudeThickness;

                    foreach (RhinoObject obj in targets)
                    {
                        Brep? brep = ToBrepFromObject(obj);
                        if (brep is null) continue;
                        Curve[]? contours = Brep.CreateContourCurves(brep, cStart, cEnd, spacing);
                        if (contours is null) continue;

                        foreach (Curve contour in contours)
                        {
                            if (slabThickness.HasValue && slabThickness.Value > 0.0)
                            {
                                Extrusion? slab = Extrusion.Create(contour, slabThickness.Value, cap: true);
                                if (slab != null)
                                {
                                    Guid slabId = doc.Objects.AddExtrusion(slab, attrs.Duplicate());
                                    if (slabId != Guid.Empty)
                                    {
                                        turnCreatedIds.Add(slabId);
                                        result.CreatedCount++;
                                        created++;
                                    }
                                    continue;
                                }
                            }

                            Guid id = doc.Objects.AddCurve(contour, attrs.Duplicate());
                            if (id != Guid.Empty)
                            {
                                turnCreatedIds.Add(id);
                                result.CreatedCount++;
                                created++;
                            }
                        }
                    }

                    if (created == 0) result.Warnings.Add("contour_slice produced zero slices.");
                    else result.AppliedNotes.Add($"Created {created} contour {(slabThickness.HasValue ? "slab(s)" : "curve(s)")}");
                    break;
                }

                case "move":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
                    Transform xform = Transform.Translation(ToVector3d(op.Vector));
                    ApplyTransform(doc, targets, xform, deleteOriginal: true, turnCreatedIds, latestOpIds, result, "Moved");
                    break;
                }

                case "copy":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
                    Vector3d delta = op.Vector != null ? ToVector3d(op.Vector) : Vector3d.Zero;
                    Transform xform = Transform.Translation(delta);
                    ApplyTransform(doc, targets, xform, deleteOriginal: false, turnCreatedIds, latestOpIds, result, "Copied");
                    break;
                }

                case "rotate":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
                    Point3d pivot = op.Center != null || op.Origin != null
                        ? ToPoint3d(op.Center ?? op.Origin)
                        : ComputeAnchor(targets);
                    Vector3d axis = op.Axis != null ? ToVector3d(op.Axis) : Vector3d.ZAxis;
                    double radians = op.AngleDegrees!.Value * (Math.PI / 180.0);
                    Transform xform = Transform.Rotation(radians, axis, pivot);
                    ApplyTransform(doc, targets, xform, deleteOriginal: true, turnCreatedIds, latestOpIds, result, "Rotated");
                    break;
                }

                case "scale":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
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
                    ApplyTransform(doc, targets, xform, deleteOriginal: true, turnCreatedIds, latestOpIds, result, "Scaled");
                    break;
                }

                case "mirror":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
                    Point3d origin = ToPoint3d(op.Origin ?? op.Center);
                    Vector3d normal = op.Normal != null ? ToVector3d(op.Normal) : Vector3d.XAxis;
                    var mirrorPlane = new Plane(origin, normal);
                    Transform xform = Transform.Mirror(mirrorPlane);
                    bool copy = op.Copy != false;
                    ApplyTransform(doc, targets, xform, deleteOriginal: !copy, turnCreatedIds, latestOpIds, result, copy ? "Mirrored (copy)" : "Mirrored");
                    break;
                }

                case "twist":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
                    BoundingBox bounds = ComputeBoundingBox(targets);
                    if (!bounds.IsValid)
                    {
                        result.Warnings.Add("No target objects matched for twist.");
                        break;
                    }

                    Point3d axisStart = op.Start != null || op.Origin != null
                        ? ToPoint3d(op.Start ?? op.Origin)
                        : new Point3d(bounds.Center.X, bounds.Center.Y, bounds.Min.Z);
                    Point3d axisEnd = op.End != null
                        ? ToPoint3d(op.End)
                        : (op.Axis != null
                            ? axisStart + ToVector3d(op.Axis) * Math.Max(1.0, bounds.Max.Z - bounds.Min.Z)
                            : new Point3d(bounds.Center.X, bounds.Center.Y, Math.Max(bounds.Min.Z + 1.0, bounds.Max.Z)));

                    var morph = new TwistSpaceMorph
                    {
                        TwistAxis = new Line(axisStart, axisEnd),
                        TwistAngleRadians = op.AngleDegrees!.Value * (Math.PI / 180.0),
                        InfiniteTwist = false,
                        Tolerance = doc.ModelAbsoluteTolerance
                    };

                    ApplySpaceMorph(doc, targets, morph, turnCreatedIds, latestOpIds, result, "Twisted");
                    break;
                }

                case "taper":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
                    BoundingBox bounds = ComputeBoundingBox(targets);
                    if (!bounds.IsValid)
                    {
                        result.Warnings.Add("No target objects matched for taper.");
                        break;
                    }

                    Point3d axisStart = op.Start != null || op.Origin != null
                        ? ToPoint3d(op.Start ?? op.Origin)
                        : new Point3d(bounds.Center.X, bounds.Center.Y, bounds.Min.Z);
                    Point3d axisEnd = op.End != null
                        ? ToPoint3d(op.End)
                        : new Point3d(bounds.Center.X, bounds.Center.Y, Math.Max(bounds.Min.Z + 1.0, bounds.Max.Z));

                    var morph = new TaperSpaceMorph(
                        axisStart,
                        axisEnd,
                        op.StartRadius!.Value,
                        op.EndRadius!.Value,
                        op.Flat == true,
                        infiniteTaper: false)
                    {
                        Tolerance = doc.ModelAbsoluteTolerance
                    };

                    ApplySpaceMorph(doc, targets, morph, turnCreatedIds, latestOpIds, result, "Tapered");
                    break;
                }

                case "array_linear":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
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
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
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
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
                    int deleted = 0;
                    foreach (RhinoObject obj in targets)
                    {
                        Guid deadId = obj.Id;
                        if (doc.Objects.Delete(obj, quiet: true))
                        {
                            turnCreatedIds.Remove(deadId);
                            latestOpIds.Remove(deadId);
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

                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
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
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
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
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
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

                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
                    var ids = targets.Select(o => o.Id).ToList();
                    int selected = ids.Count > 0 ? doc.Objects.Select(ids) : 0;
                    result.AppliedNotes.Add($"Selected {selected} object(s)");
                    break;
                }

                case "hide":
                {
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: false);
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
                    List<RhinoObject> targets = ResolveTargets(doc, op.Target, turnCreatedIds, latestOpIds, defaultToAllWhenShow: true);
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
            List<Guid> latestOpIds,
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
                        int latestIdx = latestOpIds.IndexOf(oldId);
                        if (latestIdx >= 0) latestOpIds[latestIdx] = newId;
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

        private static void ApplySpaceMorph(
            RhinoDoc doc,
            List<RhinoObject> targets,
            SpaceMorph morph,
            List<Guid> turnCreatedIds,
            List<Guid> latestOpIds,
            RhinoExecutionResult result,
            string verb)
        {
            int morphed = 0;
            foreach (RhinoObject obj in targets)
            {
                if (obj.Geometry is null) continue;
                // Convert linear Extrusions to NURBS Breps before non-affine space morphing so twists/tapers deform smoothly.
                GeometryBase dup = (obj.Geometry is Extrusion ext ? (GeometryBase?)ext.ToBrep() : null)
                                   ?? obj.Geometry.Duplicate();
                if (!morph.Morph(dup)) continue;

                ObjectAttributes attrs = obj.Attributes.Duplicate();
                Guid oldId = obj.Id;
                if (dup is Brep morphedBrep)
                {
                    if (doc.Objects.Replace(oldId, morphedBrep))
                    {
                        morphed++;
                        continue;
                    }
                    if (doc.Objects.Delete(obj, quiet: true))
                    {
                        Guid newId = doc.Objects.AddBrep(morphedBrep, attrs);
                        if (newId != Guid.Empty)
                        {
                            int idx = turnCreatedIds.IndexOf(oldId);
                            if (idx >= 0) turnCreatedIds[idx] = newId;
                            int lIdx = latestOpIds.IndexOf(oldId);
                            if (lIdx >= 0) latestOpIds[lIdx] = newId;
                            morphed++;
                        }
                    }
                }
                else if (dup is Curve morphedCurve && doc.Objects.Replace(oldId, morphedCurve))
                {
                    morphed++;
                }
                else if (dup is Mesh morphedMesh && doc.Objects.Replace(oldId, morphedMesh))
                {
                    morphed++;
                }
            }

            result.ModifiedCount += morphed;
            if (morphed == 0) result.Warnings.Add($"{verb} could not deform the target geometry.");
            else result.AppliedNotes.Add($"{verb} {morphed} object(s)");
        }

        private List<RhinoObject> ResolveTargets(
            RhinoDoc doc,
            string? rawTarget,
            List<Guid> turnCreatedIds,
            List<Guid> latestOpIds,
            bool defaultToAllWhenShow,
            bool preferMultipleTurnCreated = false)
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
                    if (latestOpIds.Count > 0 || turnCreatedIds.Count > 0 || _lastCreatedIds.Count > 0) target = "last_created";
                    else return selected;
                }
            }

            if (string.Equals(target, "selected", StringComparison.OrdinalIgnoreCase))
            {
                return CollectSelected(doc);
            }

            if (string.Equals(target, "turn_created", StringComparison.OrdinalIgnoreCase))
            {
                IEnumerable<Guid> turnSource = turnCreatedIds.Count > 0 ? turnCreatedIds : _lastCreatedIds;
                return CollectLiveObjects(doc, turnSource);
            }

            if (string.Equals(target, "last_created", StringComparison.OrdinalIgnoreCase))
            {
                IEnumerable<Guid> source;
                if (preferMultipleTurnCreated && latestOpIds.Count < 2 && turnCreatedIds.Count >= 2)
                {
                    source = turnCreatedIds;
                }
                else if (latestOpIds.Count > 0)
                {
                    source = latestOpIds;
                }
                else
                {
                    source = turnCreatedIds.Count > 0 ? turnCreatedIds : _lastCreatedIds;
                }

                return CollectLiveObjects(doc, source);
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
                string rawNames = target.Substring("name:".Length);
                string[] parts = rawNames.Split(new[] { '|', ',' }, StringSplitOptions.RemoveEmptyEntries);
                var matched = new List<RhinoObject>();
                var seen = new HashSet<Guid>();
                foreach (string part in parts)
                {
                    string objName = part.Trim();
                    if (string.IsNullOrEmpty(objName)) continue;
                    settings.NameFilter = objName;
                    IEnumerable<RhinoObject>? found = doc.Objects.GetObjectList(settings);
                    if (found is null) continue;
                    foreach (RhinoObject o in found)
                    {
                        if (o != null && !o.IsDeleted && seen.Add(o.Id))
                        {
                            matched.Add(o);
                        }
                    }
                }
                return matched;
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
            BoundingBox bounds = ComputeBoundingBox(targets);
            return bounds.IsValid ? bounds.Center : Point3d.Origin;
        }

        private static BoundingBox ComputeBoundingBox(List<RhinoObject> targets)
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
            return bounds;
        }

        private static List<RhinoObject> CollectLiveObjects(RhinoDoc doc, IEnumerable<Guid> ids)
        {
            var list = new List<RhinoObject>();
            foreach (Guid id in ids)
            {
                RhinoObject? obj = doc.Objects.FindId(id);
                if (obj != null && !obj.IsDeleted)
                {
                    list.Add(obj);
                }
            }
            return list;
        }

        private static Brep? ToBrepFromObject(RhinoObject? obj)
        {
            if (obj?.Geometry is null) return null;
            if (obj.Geometry is Brep brep) return brep.DuplicateBrep();
            if (obj.Geometry is Extrusion ext) return ext.ToBrep();
            if (obj.Geometry is Surface srf) return srf.ToBrep();
            return null;
        }

        private static PipeCapMode ParsePipeCapMode(string? capMode)
        {
            switch ((capMode ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "round": return PipeCapMode.Round;
                case "none": return PipeCapMode.None;
                default: return PipeCapMode.Flat;
            }
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
