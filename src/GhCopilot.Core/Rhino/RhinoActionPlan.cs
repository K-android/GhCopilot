using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using GhCopilot.Core.Host;

namespace GhCopilot.Core.Rhino
{
    /// <summary>
    /// Structured direct Rhino 8 scene-manipulation plan returned by an LLM when working
    /// directly in the Rhino viewport without Grasshopper.
    /// </summary>
    public sealed class RhinoActionPlan : IActionPlan
    {
        private static readonly HashSet<string> ReadOnlyOps = new HashSet<string>(
            new[] { "select", "zoom", "set_view", "set_camera", "set_display_mode" },
            StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("ops")]
        public List<RhinoActionOp> Ops { get; set; } = new List<RhinoActionOp>();

        [JsonPropertyName("explanation")]
        public string Explanation { get; set; } = string.Empty;

        [JsonIgnore]
        public int OperationCount => Ops?.Count ?? 0;

        [JsonIgnore]
        public bool HasWriteOperations
        {
            get
            {
                if (Ops is null || Ops.Count == 0)
                {
                    return false;
                }

                foreach (RhinoActionOp op in Ops)
                {
                    if (op is null)
                    {
                        continue;
                    }

                    string name = (op.Op ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(name) && !ReadOnlyOps.Contains(name))
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    /// <summary>
    /// Represents a single validated Rhino document, geometry, attribute, or viewport operation.
    /// Unused fields are omitted / left null.
    /// </summary>
    public sealed class RhinoActionOp
    {
        [JsonPropertyName("op")]
        public string Op { get; set; } = string.Empty;

        [JsonPropertyName("target")]
        public string? Target { get; set; }

        [JsonPropertyName("railTarget")]
        public string? RailTarget { get; set; }

        [JsonPropertyName("subtractTarget")]
        public string? SubtractTarget { get; set; }

        [JsonPropertyName("layer")]
        public string? Layer { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("loftType")]
        public string? LoftType { get; set; }

        [JsonPropertyName("capMode")]
        public string? CapMode { get; set; }

        [JsonPropertyName("displayMode")]
        public string? DisplayMode { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }

        [JsonPropertyName("viewName")]
        public string? ViewName { get; set; }

        [JsonPropertyName("point")]
        public double[]? Point { get; set; }

        [JsonPropertyName("start")]
        public double[]? Start { get; set; }

        [JsonPropertyName("end")]
        public double[]? End { get; set; }

        [JsonPropertyName("center")]
        public double[]? Center { get; set; }

        [JsonPropertyName("origin")]
        public double[]? Origin { get; set; }

        [JsonPropertyName("normal")]
        public double[]? Normal { get; set; }

        [JsonPropertyName("vector")]
        public double[]? Vector { get; set; }

        [JsonPropertyName("axis")]
        public double[]? Axis { get; set; }

        [JsonPropertyName("cameraLocation")]
        public double[]? CameraLocation { get; set; }

        [JsonPropertyName("cameraTarget")]
        public double[]? CameraTarget { get; set; }

        [JsonPropertyName("factors")]
        public double[]? Factors { get; set; }

        [JsonPropertyName("points")]
        public List<double[]>? Points { get; set; }

        [JsonPropertyName("color")]
        public int[]? Color { get; set; }

        [JsonPropertyName("width")]
        public double? Width { get; set; }

        [JsonPropertyName("depth")]
        public double? Depth { get; set; }

        [JsonPropertyName("height")]
        public double? Height { get; set; }

        [JsonPropertyName("radius")]
        public double? Radius { get; set; }

        [JsonPropertyName("radius2")]
        public double? Radius2 { get; set; }

        [JsonPropertyName("tubeRadius")]
        public double? TubeRadius { get; set; }

        [JsonPropertyName("radiusX")]
        public double? RadiusX { get; set; }

        [JsonPropertyName("radiusY")]
        public double? RadiusY { get; set; }

        [JsonPropertyName("startRadius")]
        public double? StartRadius { get; set; }

        [JsonPropertyName("endRadius")]
        public double? EndRadius { get; set; }

        [JsonPropertyName("distance")]
        public double? Distance { get; set; }

        [JsonPropertyName("extrudeThickness")]
        public double? ExtrudeThickness { get; set; }

        [JsonPropertyName("angleDegrees")]
        public double? AngleDegrees { get; set; }

        [JsonPropertyName("startAngleDegrees")]
        public double? StartAngleDegrees { get; set; }

        [JsonPropertyName("totalAngleDegrees")]
        public double? TotalAngleDegrees { get; set; }

        [JsonPropertyName("turns")]
        public double? Turns { get; set; }

        [JsonPropertyName("factor")]
        public double? Factor { get; set; }

        [JsonPropertyName("count")]
        public int? Count { get; set; }

        [JsonPropertyName("sides")]
        public int? Sides { get; set; }

        [JsonPropertyName("closed")]
        public bool? Closed { get; set; }

        [JsonPropertyName("smooth")]
        public bool? Smooth { get; set; }

        [JsonPropertyName("centered")]
        public bool? Centered { get; set; }

        [JsonPropertyName("capped")]
        public bool? Capped { get; set; }

        [JsonPropertyName("inscribed")]
        public bool? Inscribed { get; set; }

        [JsonPropertyName("solid")]
        public bool? Solid { get; set; }

        [JsonPropertyName("chamfer")]
        public bool? Chamfer { get; set; }

        [JsonPropertyName("flat")]
        public bool? Flat { get; set; }

        [JsonPropertyName("copy")]
        public bool? Copy { get; set; }

        [JsonPropertyName("keepCutters")]
        public bool? KeepCutters { get; set; }

        [JsonPropertyName("visible")]
        public bool? Visible { get; set; }

        [JsonPropertyName("clearFirst")]
        public bool? ClearFirst { get; set; }
    }

    public sealed class RhinoActionValidationResult
    {
        public bool IsValid => Errors.Count == 0;
        public List<string> Errors { get; } = new List<string>();

        public ActionPlanValidationResult ToHostResult()
        {
            var hostResult = new ActionPlanValidationResult();
            hostResult.Errors.AddRange(Errors);
            return hostResult;
        }
    }

    /// <summary>
    /// Validates direct Rhino action plans for finite geometry values, safe array bounds,
    /// valid selectors, and required operation parameters prior to RhinoDoc mutation.
    /// </summary>
    public static class RhinoActionPlanValidator
    {
        public const int MaxOpsPerTurn = 250;
        public const int MaxArrayCount = 500;
        public const int MaxPolylinePoints = 500;

        public static readonly IReadOnlyCollection<string> SupportedOpNames = new[]
        {
            "add_point",
            "add_line",
            "add_arc",
            "add_circle",
            "add_ellipse",
            "add_polygon",
            "add_rectangle",
            "add_polyline",
            "add_helix",
            "add_box",
            "add_sphere",
            "add_cylinder",
            "add_cone",
            "add_torus",
            "extrude",
            "loft",
            "revolve",
            "sweep1",
            "pipe",
            "planar_srf",
            "cap_planar_holes",
            "offset_curve",
            "offset_surface",
            "fillet_edges",
            "boolean_union",
            "boolean_difference",
            "boolean_intersection",
            "contour_slice",
            "move",
            "rotate",
            "scale",
            "mirror",
            "twist",
            "taper",
            "copy",
            "array_linear",
            "array_polar",
            "delete",
            "create_layer",
            "set_layer",
            "set_color",
            "rename",
            "select",
            "hide",
            "show",
            "set_display_mode",
            "zoom",
            "set_view",
            "set_camera"
        };

        private static readonly HashSet<string> SupportedOps = new HashSet<string>(
            SupportedOpNames,
            StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> ValidTargetTypes = new HashSet<string>(
            new[] { "point", "curve", "brep", "extrusion", "surface", "mesh", "polysurface", "solid", "subd" },
            StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> ValidLoftTypes = new HashSet<string>(
            new[] { "normal", "loose", "tight", "straight", "uniform" },
            StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> ValidCapModes = new HashSet<string>(
            new[] { "flat", "round", "none" },
            StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> ValidZoomScopes = new HashSet<string>(
            new[] { "extents", "selected" },
            StringComparer.OrdinalIgnoreCase);

        public static RhinoActionValidationResult Validate(RhinoActionPlan? plan)
        {
            var result = new RhinoActionValidationResult();
            if (plan is null)
            {
                result.Errors.Add("Action plan must not be null.");
                return result;
            }

            if (plan.Ops is null)
            {
                result.Errors.Add("Action plan 'ops' array must not be null.");
                return result;
            }

            if (plan.Ops.Count > MaxOpsPerTurn)
            {
                result.Errors.Add($"Action plan has {plan.Ops.Count} ops, exceeding the safety limit of {MaxOpsPerTurn} ops per turn.");
                return result;
            }

            for (int i = 0; i < plan.Ops.Count; i++)
            {
                ValidateOp(i, plan.Ops[i], result.Errors);
            }

            return result;
        }

        private static void ValidateOp(int index, RhinoActionOp? op, List<string> errors)
        {
            string prefix = $"ops[{index}]";
            if (op is null)
            {
                errors.Add($"{prefix}: Operation entry must not be null.");
                return;
            }

            string opName = (op.Op ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(opName) || !SupportedOps.Contains(opName))
            {
                errors.Add($"{prefix}: Unsupported or missing op '{op.Op}'.");
                return;
            }

            string normalizedOp = opName.ToLowerInvariant();
            prefix = $"ops[{index}] ({normalizedOp})";

            // Generic field checks whenever populated
            ValidatePoint3DIfPresent(op.Point, $"{prefix}.point", errors);
            ValidatePoint3DIfPresent(op.Start, $"{prefix}.start", errors);
            ValidatePoint3DIfPresent(op.End, $"{prefix}.end", errors);
            ValidatePoint3DIfPresent(op.Center, $"{prefix}.center", errors);
            ValidatePoint3DIfPresent(op.Origin, $"{prefix}.origin", errors);
            ValidateVector3DIfPresent(op.Normal, $"{prefix}.normal", requireNonZero: true, errors);
            ValidateVector3DIfPresent(op.Vector, $"{prefix}.vector", requireNonZero: normalizedOp != "copy", errors);
            ValidateVector3DIfPresent(op.Axis, $"{prefix}.axis", requireNonZero: true, errors);
            ValidatePoint3DIfPresent(op.CameraLocation, $"{prefix}.cameraLocation", errors);
            ValidatePoint3DIfPresent(op.CameraTarget, $"{prefix}.cameraTarget", errors);

            if (op.Points != null)
            {
                for (int p = 0; p < op.Points.Count; p++)
                {
                    ValidatePoint3DIfPresent(op.Points[p], $"{prefix}.points[{p}]", errors);
                    if (op.Points[p] is null)
                    {
                        errors.Add($"{prefix}.points[{p}]: Point must not be null.");
                    }
                }
            }

            ValidateColorIfPresent(op.Color, $"{prefix}.color", errors);
            ValidateTargetIfPresent(op.Target, $"{prefix}.target", errors);
            ValidateTargetIfPresent(op.RailTarget, $"{prefix}.railTarget", errors);
            ValidateTargetIfPresent(op.SubtractTarget, $"{prefix}.subtractTarget", errors);

            if (!string.IsNullOrWhiteSpace(op.LoftType) && !ValidLoftTypes.Contains(op.LoftType!.Trim()))
            {
                errors.Add($"{prefix}.loftType: Unsupported loftType '{op.LoftType}'. Use normal, loose, tight, straight, or uniform.");
            }

            if (!string.IsNullOrWhiteSpace(op.CapMode) && !ValidCapModes.Contains(op.CapMode!.Trim()))
            {
                errors.Add($"{prefix}.capMode: Unsupported capMode '{op.CapMode}'. Use flat, round, or none.");
            }

            if (!string.IsNullOrWhiteSpace(op.Scope) && !ValidZoomScopes.Contains(op.Scope!.Trim()))
            {
                errors.Add($"{prefix}.scope: Unsupported zoom scope '{op.Scope}'. Use 'extents' or 'selected'.");
            }

            if (op.Radius.HasValue && !IsPositiveFinite(op.Radius.Value))
            {
                errors.Add($"{prefix}.radius: Radius must be a positive finite number.");
            }

            if (op.Radius2.HasValue)
            {
                bool allowZero = normalizedOp == "add_helix";
                if (allowZero ? (!IsFinite(op.Radius2.Value) || op.Radius2.Value < 0.0) : !IsPositiveFinite(op.Radius2.Value))
                {
                    errors.Add($"{prefix}.radius2: Radius2 must be finite and {(allowZero ? ">= 0" : "> 0")}.");
                }
            }

            if (op.TubeRadius.HasValue && !IsPositiveFinite(op.TubeRadius.Value))
            {
                errors.Add($"{prefix}.tubeRadius: TubeRadius must be a positive finite number.");
            }

            if (op.RadiusX.HasValue && !IsPositiveFinite(op.RadiusX.Value))
            {
                errors.Add($"{prefix}.radiusX: RadiusX must be a positive finite number.");
            }

            if (op.RadiusY.HasValue && !IsPositiveFinite(op.RadiusY.Value))
            {
                errors.Add($"{prefix}.radiusY: RadiusY must be a positive finite number.");
            }

            if (op.StartRadius.HasValue && !IsPositiveFinite(op.StartRadius.Value))
            {
                errors.Add($"{prefix}.startRadius: StartRadius must be a positive finite number.");
            }

            if (op.EndRadius.HasValue && (!IsFinite(op.EndRadius.Value) || op.EndRadius.Value < 0.0))
            {
                errors.Add($"{prefix}.endRadius: EndRadius must be a non-negative finite number.");
            }

            if (op.ExtrudeThickness.HasValue && !IsPositiveFinite(op.ExtrudeThickness.Value))
            {
                errors.Add($"{prefix}.extrudeThickness: ExtrudeThickness must be a positive finite number.");
            }

            if (op.Turns.HasValue && !IsPositiveFinite(op.Turns.Value))
            {
                errors.Add($"{prefix}.turns: Turns must be a positive finite number.");
            }

            if (op.StartAngleDegrees.HasValue && !IsFinite(op.StartAngleDegrees.Value))
            {
                errors.Add($"{prefix}.startAngleDegrees: StartAngleDegrees must be finite.");
            }

            if (op.Width.HasValue && !IsPositiveFinite(op.Width.Value))
            {
                errors.Add($"{prefix}.width: Width must be a positive finite number.");
            }

            if (op.Depth.HasValue && !IsPositiveFinite(op.Depth.Value))
            {
                errors.Add($"{prefix}.depth: Depth must be a positive finite number.");
            }

            if (op.Height.HasValue)
            {
                if (normalizedOp == "extrude" || normalizedOp == "add_helix")
                {
                    if (!IsNonZeroFinite(op.Height.Value))
                    {
                        errors.Add($"{prefix}.height: Height must be a non-zero finite number.");
                    }
                }
                else if (!IsPositiveFinite(op.Height.Value))
                {
                    errors.Add($"{prefix}.height: Height must be a positive finite number.");
                }
            }

            if (op.Distance.HasValue && !IsNonZeroFinite(op.Distance.Value))
            {
                errors.Add($"{prefix}.distance: Distance must be a non-zero finite number.");
            }

            if (op.AngleDegrees.HasValue && !IsFinite(op.AngleDegrees.Value))
            {
                errors.Add($"{prefix}.angleDegrees: Angle must be finite.");
            }

            if (op.TotalAngleDegrees.HasValue && !IsFinite(op.TotalAngleDegrees.Value))
            {
                errors.Add($"{prefix}.totalAngleDegrees: Total angle must be finite.");
            }

            if (op.Factor.HasValue && !IsPositiveFinite(op.Factor.Value))
            {
                errors.Add($"{prefix}.factor: Scale factor must be a positive finite number.");
            }

            if (op.Factors != null)
            {
                if (op.Factors.Length != 3 || !op.Factors.All(IsPositiveFinite))
                {
                    errors.Add($"{prefix}.factors: Non-uniform scale factors must contain 3 positive finite numbers [sx, sy, sz].");
                }
            }

            // Operation-specific required parameters
            switch (normalizedOp)
            {
                case "add_point":
                    if (op.Point is null)
                    {
                        errors.Add($"{prefix}: 'point' [x, y, z] is required.");
                    }
                    break;

                case "add_line":
                    if (op.Start is null || op.End is null)
                    {
                        errors.Add($"{prefix}: Both 'start' and 'end' [x, y, z] points are required.");
                    }
                    else if (IsValidVector3(op.Start) && IsValidVector3(op.End) && AreClose(op.Start, op.End))
                    {
                        errors.Add($"{prefix}: 'start' and 'end' must not be identical.");
                    }
                    break;

                case "add_arc":
                {
                    bool hasThreePoints = op.Start != null && op.Point != null && op.End != null;
                    bool hasCenterRadiusSweep = op.Radius.HasValue && op.AngleDegrees.HasValue && IsNonZeroFinite(op.AngleDegrees.Value);
                    if (!hasThreePoints && !hasCenterRadiusSweep)
                    {
                        errors.Add($"{prefix}: Provide either 3-point ('start', 'point', 'end') or ('radius' > 0 and non-zero 'angleDegrees').");
                    }
                    break;
                }

                case "add_circle":
                case "add_sphere":
                case "pipe":
                case "fillet_edges":
                    if (!op.Radius.HasValue)
                    {
                        errors.Add($"{prefix}: 'radius' (> 0) is required.");
                    }
                    break;

                case "add_ellipse":
                    if ((!op.RadiusX.HasValue && !op.Width.HasValue) ||
                        (!op.RadiusY.HasValue && !op.Depth.HasValue && !op.Height.HasValue))
                    {
                        errors.Add($"{prefix}: Positive 'radiusX' (or 'width') and 'radiusY' (or 'depth') are required.");
                    }
                    break;

                case "add_polygon":
                {
                    int polySides = op.Sides ?? op.Count ?? 0;
                    if (!op.Radius.HasValue || polySides < 3 || polySides > 128)
                    {
                        errors.Add($"{prefix}: Positive 'radius' and 'sides' (or 'count', 3..128) are required.");
                    }
                    break;
                }

                case "add_torus":
                    if (!op.Radius.HasValue || !op.TubeRadius.HasValue)
                    {
                        errors.Add($"{prefix}: Positive 'radius' (major) and 'tubeRadius' (minor) are required.");
                    }
                    break;

                case "add_helix":
                    if (!op.Radius.HasValue || !op.Height.HasValue || !op.Turns.HasValue)
                    {
                        errors.Add($"{prefix}: Positive 'radius', non-zero 'height', and positive 'turns' are required.");
                    }
                    break;

                case "revolve":
                    if (op.AngleDegrees.HasValue && !IsNonZeroFinite(op.AngleDegrees.Value))
                    {
                        errors.Add($"{prefix}: 'angleDegrees' must be non-zero when specified.");
                    }
                    break;

                case "offset_surface":
                    if (!op.Distance.HasValue)
                    {
                        errors.Add($"{prefix}: Non-zero 'distance' (shell thickness) is required for offset_surface.");
                    }
                    break;

                case "boolean_difference":
                case "boolean_intersection":
                    if (string.IsNullOrWhiteSpace(op.SubtractTarget))
                    {
                        errors.Add($"{prefix}: 'subtractTarget' selector (e.g. 'last_created', 'name:Cutter', 'layer:Cutters') is required.");
                    }
                    break;

                case "twist":
                    if (!op.AngleDegrees.HasValue || !IsNonZeroFinite(op.AngleDegrees.Value))
                    {
                        errors.Add($"{prefix}: Non-zero 'angleDegrees' is required for twist.");
                    }
                    break;

                case "taper":
                    if (!op.StartRadius.HasValue || !op.EndRadius.HasValue)
                    {
                        errors.Add($"{prefix}: Positive 'startRadius' and non-negative 'endRadius' are required for taper.");
                    }
                    break;

                case "contour_slice":
                    if (!op.Distance.HasValue || op.Distance.Value <= 0.0)
                    {
                        errors.Add($"{prefix}: Positive 'distance' (> 0) spacing between contour slices is required.");
                    }
                    break;

                case "add_rectangle":
                    if (!op.Width.HasValue || (!op.Depth.HasValue && !op.Height.HasValue))
                    {
                        errors.Add($"{prefix}: Positive 'width' and 'depth' (or 'height') are required.");
                    }
                    break;

                case "add_polyline":
                    if (op.Points is null || op.Points.Count < 2 || op.Points.Count > MaxPolylinePoints)
                    {
                        errors.Add($"{prefix}: 'points' must contain between 2 and {MaxPolylinePoints} 3D points.");
                    }
                    break;

                case "add_box":
                    if (!op.Width.HasValue || !op.Depth.HasValue || !op.Height.HasValue)
                    {
                        errors.Add($"{prefix}: Positive 'width', 'depth', and 'height' are required.");
                    }
                    break;

                case "add_cylinder":
                case "add_cone":
                    if (!op.Radius.HasValue || !op.Height.HasValue)
                    {
                        errors.Add($"{prefix}: Positive 'radius' and 'height' are required.");
                    }
                    break;

                case "extrude":
                    if (!op.Height.HasValue)
                    {
                        errors.Add($"{prefix}: Non-zero 'height' is required for extrude.");
                    }
                    break;

                case "offset_curve":
                    if (!op.Distance.HasValue)
                    {
                        errors.Add($"{prefix}: Non-zero 'distance' is required for offset_curve.");
                    }
                    break;

                case "move":
                    if (op.Vector is null)
                    {
                        errors.Add($"{prefix}: Non-zero 'vector' [dx, dy, dz] is required.");
                    }
                    break;

                case "rotate":
                    if (!op.AngleDegrees.HasValue)
                    {
                        errors.Add($"{prefix}: 'angleDegrees' is required.");
                    }
                    break;

                case "scale":
                    if (!op.Factor.HasValue && op.Factors is null)
                    {
                        errors.Add($"{prefix}: Either 'factor' or 'factors' [sx, sy, sz] is required.");
                    }
                    break;

                case "array_linear":
                    if (!op.Count.HasValue || op.Count.Value < 2 || op.Count.Value > MaxArrayCount)
                    {
                        errors.Add($"{prefix}: 'count' must be between 2 and {MaxArrayCount}.");
                    }
                    if (op.Vector is null)
                    {
                        errors.Add($"{prefix}: Non-zero step 'vector' [dx, dy, dz] is required.");
                    }
                    break;

                case "array_polar":
                    if (!op.Count.HasValue || op.Count.Value < 2 || op.Count.Value > MaxArrayCount)
                    {
                        errors.Add($"{prefix}: 'count' must be between 2 and {MaxArrayCount}.");
                    }
                    break;

                case "create_layer":
                case "set_layer":
                    if (string.IsNullOrWhiteSpace(op.Layer))
                    {
                        errors.Add($"{prefix}: Non-empty 'layer' name or path is required.");
                    }
                    break;

                case "set_color":
                    if (op.Color is null)
                    {
                        errors.Add($"{prefix}: 'color' [r, g, b] (0..255) is required.");
                    }
                    break;

                case "rename":
                    if (string.IsNullOrWhiteSpace(op.Name))
                    {
                        errors.Add($"{prefix}: Non-empty 'name' is required.");
                    }
                    break;

                case "select":
                    if (string.IsNullOrWhiteSpace(op.Target))
                    {
                        errors.Add($"{prefix}: 'target' selector is required for select.");
                    }
                    break;

                case "set_display_mode":
                    if (string.IsNullOrWhiteSpace(op.DisplayMode))
                    {
                        errors.Add($"{prefix}: 'displayMode' (e.g. Shaded, Rendered, Wireframe, Ghosted, Arctic) is required.");
                    }
                    break;

                case "set_view":
                    if (string.IsNullOrWhiteSpace(op.ViewName))
                    {
                        errors.Add($"{prefix}: 'viewName' (e.g. Perspective, Top, Front, Right) is required.");
                    }
                    break;

                case "set_camera":
                    if (op.CameraLocation is null || op.CameraTarget is null)
                    {
                        errors.Add($"{prefix}: Both 'cameraLocation' and 'cameraTarget' [x, y, z] are required.");
                    }
                    else if (IsValidVector3(op.CameraLocation) && IsValidVector3(op.CameraTarget) && AreClose(op.CameraLocation, op.CameraTarget))
                    {
                        errors.Add($"{prefix}: 'cameraLocation' and 'cameraTarget' must not be identical.");
                    }
                    break;
            }
        }

        private static void ValidatePoint3DIfPresent(double[]? values, string fieldName, List<string> errors)
        {
            if (values is null) return;
            if (!IsValidVector3(values))
            {
                errors.Add($"{fieldName}: Expected [x, y, z] array of 3 finite numbers.");
            }
        }

        private static void ValidateVector3DIfPresent(double[]? values, string fieldName, bool requireNonZero, List<string> errors)
        {
            if (values is null) return;
            if (!IsValidVector3(values))
            {
                errors.Add($"{fieldName}: Expected [x, y, z] vector of 3 finite numbers.");
                return;
            }

            if (requireNonZero)
            {
                double lenSq = values[0] * values[0] + values[1] * values[1] + values[2] * values[2];
                if (lenSq <= 1e-12)
                {
                    errors.Add($"{fieldName}: Vector must not be zero length.");
                }
            }
        }

        private static void ValidateColorIfPresent(int[]? rgb, string fieldName, List<string> errors)
        {
            if (rgb is null) return;
            if (rgb.Length != 3 || rgb[0] < 0 || rgb[0] > 255 || rgb[1] < 0 || rgb[1] > 255 || rgb[2] < 0 || rgb[2] > 255)
            {
                errors.Add($"{fieldName}: RGB color must contain 3 integers between 0 and 255.");
            }
        }

        private static void ValidateTargetIfPresent(string? target, string fieldName, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(target)) return;
            string t = target!.Trim();
            if (string.Equals(t, "selected", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "last_created", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "turn_created", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "all", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (t.StartsWith("layer:", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(t.Substring("layer:".Length)))
                {
                    errors.Add($"{fieldName}: 'layer:' selector requires a non-empty layer name.");
                }
                return;
            }

            if (t.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
            {
                string rawNames = t.Substring("name:".Length);
                if (string.IsNullOrWhiteSpace(rawNames) ||
                    rawNames.Split(new[] { '|', ',' }, StringSplitOptions.RemoveEmptyEntries).All(string.IsNullOrWhiteSpace))
                {
                    errors.Add($"{fieldName}: 'name:' selector requires at least one non-empty object name.");
                }
                return;
            }

            if (t.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
            {
                string typeSuffix = t.Substring("type:".Length).Trim();
                if (!ValidTargetTypes.Contains(typeSuffix))
                {
                    errors.Add($"{fieldName}: Unsupported 'type:' selector '{typeSuffix}'. Valid types: point, curve, brep, extrusion, surface, mesh, polysurface, solid, subd.");
                }
                return;
            }

            errors.Add($"{fieldName}: Invalid target '{target}'. Use 'selected', 'last_created', 'turn_created', 'all', 'layer:<name>', 'name:<name>', or 'type:<type>'.");
        }

        private static bool IsValidVector3(double[]? v)
        {
            return v != null && v.Length == 3 && IsFinite(v[0]) && IsFinite(v[1]) && IsFinite(v[2]);
        }

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        private static bool IsPositiveFinite(double v) => IsFinite(v) && v > 0.0;

        private static bool IsNonZeroFinite(double v) => IsFinite(v) && Math.Abs(v) > 1e-9;

        private static bool AreClose(double[] a, double[] b)
        {
            double dx = a[0] - b[0];
            double dy = a[1] - b[1];
            double dz = a[2] - b[2];
            return (dx * dx + dy * dy + dz * dz) <= 1e-12;
        }
    }

    /// <summary>
    /// Provider-compatible flat JSON Schema describing <see cref="RhinoActionPlan"/>.
    /// </summary>
    public static class RhinoActionSchema
    {
        public static JsonElement Build()
        {
            object text = new { type = "string" };
            object number = new { type = "number" };
            object integer = new { type = "integer" };
            object boolean = new { type = "boolean" };
            object vec3 = new { type = "array", items = number };
            object rgb3 = new { type = "array", items = integer };

            var schema = new
            {
                type = "object",
                properties = new
                {
                    ops = new
                    {
                        type = "array",
                        description = "Ordered list of direct Rhino 8 operations to execute in one undoable transaction. Return [] when answering a question or when no Rhino edits are requested.",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                op = new
                                {
                                    type = "string",
                                    @enum = RhinoActionPlanValidator.SupportedOpNames
                                },
                                target = new { type = "string", description = "Selector: 'selected', 'last_created', 'turn_created', 'all', 'layer:<name>', 'name:<name>' (supports 'name:A|B'), or 'type:<point|curve|brep|extrusion|surface|mesh|polysurface|solid|subd>'." },
                                railTarget = text,
                                subtractTarget = text,
                                layer = text,
                                name = text,
                                loftType = new { type = "string", @enum = new[] { "normal", "loose", "tight", "straight", "uniform" } },
                                capMode = new { type = "string", @enum = new[] { "flat", "round", "none" } },
                                displayMode = text,
                                scope = new { type = "string", @enum = new[] { "extents", "selected" } },
                                viewName = text,
                                point = vec3,
                                start = vec3,
                                end = vec3,
                                center = vec3,
                                origin = vec3,
                                normal = vec3,
                                vector = vec3,
                                axis = vec3,
                                cameraLocation = vec3,
                                cameraTarget = vec3,
                                factors = vec3,
                                points = new { type = "array", items = vec3 },
                                color = rgb3,
                                width = number,
                                depth = number,
                                height = number,
                                radius = number,
                                radius2 = number,
                                tubeRadius = number,
                                radiusX = number,
                                radiusY = number,
                                startRadius = number,
                                endRadius = number,
                                distance = number,
                                extrudeThickness = number,
                                angleDegrees = number,
                                startAngleDegrees = number,
                                totalAngleDegrees = number,
                                turns = number,
                                factor = number,
                                count = integer,
                                sides = integer,
                                closed = boolean,
                                smooth = boolean,
                                centered = boolean,
                                capped = boolean,
                                inscribed = boolean,
                                solid = boolean,
                                chamfer = boolean,
                                flat = boolean,
                                copy = boolean,
                                keepCutters = boolean,
                                visible = boolean,
                                clearFirst = boolean
                            },
                            required = new[] { "op" }
                        }
                    },
                    explanation = new
                    {
                        type = "string",
                        description = "Concise summary of the Rhino changes made, or your conversational answer if ops is empty."
                    }
                },
                required = new[] { "ops", "explanation" }
            };

            return JsonSerializer.SerializeToElement(schema);
        }
    }
}
