using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GhCopilot.Core.Graph;
using GhCopilot.Core.Host;

namespace GhCopilot.Core.Llm
{
    /// <summary>
    /// Builds prompts and mode-routing rules for Direct Rhino 8 Mode (no Grasshopper required).
    /// </summary>
    public static class RhinoPromptBuilder
    {
        private static readonly Regex GrasshopperIntentRegex = new Regex(
            @"\b(?:grasshopper|\.gh|ghx|slider|sliders|panel|component|components|node|nodes|wire|wires|canvas|parametric|attractor|voronoi|delaunay|ghpython|python\s*script|cluster|recipe|flatten|graft|data\s*tree|definition|plugin|plugins|revit|rhino\.?\s*inside|rir|bim|directshape|toposolid|adaptive\s*component|curtain\s*wall|mullion|mullions|workset|worksets|category\s*picker|family\s*type|element\s*parameter|element\s*type|structural\s+(?:column|framing|beam|wall|floor)|lunchbox|pufferfish|weaverbird|kangaroo|karamba|ladybug|honeybee|elefront|human|dendro|galapagos|wallacei|anemone|metahopper|hops|cyclops|modelur|raven|(?:add|query|inspect|create|place|build)\s+(?:a\s+|the\s+|revit\s+)?(?:wall|walls|floor|floors|roof|roofs|ceiling|ceilings|column|columns|beam|beams|level|levels|grid|grids|element|elements|family|families|parameter|parameters|room|rooms|schedule|schedules|sheet|sheets))\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool HasExplicitGrasshopperIntent(string? prompt)
        {
            return !string.IsNullOrWhiteSpace(prompt) && GrasshopperIntentRegex.IsMatch(prompt!);
        }

        private static readonly Regex RhinoDirectIntentRegex = new Regex(
            @"\b(?:display\s*mode|shaded|rendered|wireframe|ghosted|arctic|raytraced|monochrome|zoom\s*(?:extents|selected|all)|camera|viewport|perspective\s*view|top\s*view|front\s*view|right\s*view|left\s*view|layer|layers|hide|unhide|show\s*(?:all|hidden|layer)|select\s*(?:all|curves|breps|meshes|points|objects|layer)|rename|color|colour|paint|without\s+grasshopper|directly\s+in\s+rhino|in\s+rhino\s+directly)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex GuidText = new Regex(
            @"(?i)(?<![0-9a-f])(?:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{32})(?![0-9a-f])",
            RegexOptions.Compiled);

        public static CopilotWorkMode ResolveEffectiveMode(
            CopilotWorkMode selectedMode,
            string? prompt,
            bool isGrasshopperOpen,
            bool useRhinoSelection)
        {
            if (selectedMode == CopilotWorkMode.RhinoDirect)
            {
                return CopilotWorkMode.RhinoDirect;
            }

            if (selectedMode == CopilotWorkMode.Grasshopper)
            {
                return CopilotWorkMode.Grasshopper;
            }

            if (useRhinoSelection)
            {
                return CopilotWorkMode.Grasshopper;
            }

            string text = prompt ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (GrasshopperIntentRegex.IsMatch(text))
                {
                    return CopilotWorkMode.Grasshopper;
                }

                if (RhinoDirectIntentRegex.IsMatch(text))
                {
                    return CopilotWorkMode.RhinoDirect;
                }
            }

            return isGrasshopperOpen ? CopilotWorkMode.Grasshopper : CopilotWorkMode.RhinoDirect;
        }

        public static string BuildSystem()
        {
            return """
                You are GH Copilot running in Direct Rhino 8 Mode (no Grasshopper required).
                Return ONLY a RhinoActionPlan JSON object matching the supplied JSON schema: { "ops": [...], "explanation": "..." } (up to 250 operations per turn).

                SUPPORTED OPERATIONS (use exact 'op' names and omit unused properties):
                - Curves & 2D/3D Profiles:
                  * add_point: point [x,y,z], optional layer, name, color [r,g,b]
                  * add_line: start [x,y,z], end [x,y,z], optional layer, name, color
                  * add_arc: EITHER 3-point (start [x,y,z], point [x,y,z], end [x,y,z]) OR (center [x,y,z], radius >0, angleDegrees, optional startAngleDegrees, normal [x,y,z]), optional layer, name, color
                  * add_circle: center [x,y,z] (default [0,0,0]), radius (>0), optional normal [x,y,z], layer, name, color
                  * add_ellipse: center [x,y,z] (default [0,0,0]), radiusX (>0), radiusY (>0), optional normal [x,y,z], layer, name, color
                  * add_polygon: center [x,y,z] (default [0,0,0]), radius (>0), sides (3..128), optional inscribed (default true), normal [x,y,z], layer, name, color
                  * add_rectangle: origin [x,y,z] (default [0,0,0]), width (>0), depth (>0), optional centered (bool), normal, layer, name, color
                  * add_polyline: points [[x,y,z],...], optional closed (bool), smooth (bool, interpolated NURBS curve!), layer, name, color
                  * add_helix: origin [x,y,z] (default [0,0,0]), radius (>0), optional radius2 (>=0 for tapered/conical spiral), height (non-zero), turns (>0), axis [x,y,z], layer, name, color
                  * offset_curve: distance (non-zero), optional target, normal, layer, name, color
                - Solids, Surfaces, Sweeps, Shells & Booleans:
                  * add_box: origin [x,y,z] (default [0,0,0]), width (>0), depth (>0), height (>0), optional centered (bool; default false uses origin as min corner), layer, name, color
                  * add_sphere: center [x,y,z] (default [0,0,0]), radius (>0), optional layer, name, color
                  * add_cylinder: origin [x,y,z] (base center), radius (>0), height (>0), optional normal, capped (default true), layer, name, color
                  * add_cone: origin [x,y,z] (base center), radius (>0), height (>0), optional normal, capped (default true), layer, name, color
                  * add_torus: center [x,y,z] (default [0,0,0]), radius (major >0), tubeRadius (minor >0), optional normal, layer, name, color
                  * extrude: height (non-zero), optional target (default 'last_created'), capped (default true), layer, name, color
                  * loft: optional target (2+ curves, e.g. 'name:Prof1|Prof2|Prof3' or 'turn_created'), loftType ('normal'|'loose'|'tight'|'straight'|'uniform'), closed (bool), capped (bool), layer, name, color
                  * revolve: optional origin [x,y,z] (axis point, default [0,0,0]), axis [x,y,z] (default [0,0,1]), angleDegrees (default 360), capped (default true), target, layer, name, color
                  * sweep1: optional railTarget (e.g. 'name:Rail'), target (cross-section curve(s); if railTarget omitted and target has 2+ curves, first is rail), closed (bool), capped (default true), layer, name, color
                  * pipe: radius (>0), optional radius2 (>0 for tapered pipe), capMode ('flat'|'round'|'none', default 'flat'), target, layer, name, color
                  * planar_srf: optional target (closed planar curve(s) — supports outer boundary + inner hole curves together via 'name:Outer|Hole1|Hole2'!), layer, name, color
                  * cap_planar_holes: optional target (open Breps)
                  * offset_surface: distance (non-zero shell thickness), optional solid (default true), target, layer, name, color
                  * fillet_edges: radius (>0), optional chamfer (bool, default false), target, layer, name, color
                  * boolean_union: optional target (2+ overlapping solids, e.g. 'name:PartA|PartB' or 'layer:Massing'), keepCutters (bool), layer, name, color
                  * boolean_difference: target (base solid(s), e.g. 'name:Wall'), subtractTarget (cutter solid(s), e.g. 'last_created' or 'name:Opening'), optional keepCutters (default false), layer, name, color
                  * boolean_intersection: target (first solid(s)), subtractTarget (second solid(s)), optional keepCutters (default false), layer, name, color
                  * contour_slice: distance (>0 spacing), optional start [x,y,z], end [x,y,z] (defaults to target Z-bounds), extrudeThickness (>0 extrudes each contour slice into a solid slab/rib!), target, layer, name, color
                - Transforms, Space Morphs & Arrays:
                  * move: vector [dx,dy,dz], optional target
                  * rotate: angleDegrees, optional center [x,y,z], axis [x,y,z] (default [0,0,1]), target
                  * scale: factor (>0) OR factors [sx,sy,sz], optional center [x,y,z], target
                  * mirror: optional origin [x,y,z] (default [0,0,0]), normal [x,y,z] (default [1,0,0]), copy (default true), target
                  * twist: angleDegrees (non-zero), optional start [x,y,z], end [x,y,z] (defaults to target vertical axis), target
                  * taper: startRadius (>0), endRadius (>=0), optional start [x,y,z], end [x,y,z] (defaults to target vertical axis), flat (bool), target
                  * copy: optional vector [dx,dy,dz] (default [0,0,0]), target
                  * array_linear: count (2..500), vector [dx,dy,dz] step per item, optional target
                  * array_polar: count (2..500), optional totalAngleDegrees (default 360), center [x,y,z], axis [x,y,z], target
                  * delete: optional target (default 'selected')
                - Layers, Colors, Naming, Selection & Viewport:
                  * create_layer: layer (supports 'Parent::Child'), optional color [r,g,b], visible (bool)
                  * set_layer: layer, optional target
                  * set_color: color [r,g,b] (0..255), optional target
                  * rename: name, optional target
                  * select: target (required), optional clearFirst (default true)
                  * hide: optional target (default 'selected')
                  * show: optional target (default 'all')
                  * set_display_mode: displayMode ('Shaded'|'Rendered'|'Wireframe'|'Ghosted'|'Arctic'|'Pen'|'Technical'|'Monochrome')
                  * zoom: scope ('extents'|'selected', default 'extents')
                  * set_view: viewName ('Perspective'|'Top'|'Front'|'Right'|'Left'|'Back'|'Bottom'|'TwoPointPerspective')
                  * set_camera: cameraLocation [x,y,z], cameraTarget [x,y,z]

                RULES:
                1. Target selectors: 'selected', 'last_created' (objects created by the immediately preceding creation op), 'turn_created' (all objects created so far in this turn), 'all', 'layer:<layerName>', 'name:<name>' (supports pipe-separated 'name:A|B|C'), or 'type:<point|curve|brep|extrusion|surface|mesh|polysurface|solid|subd>'.
                2. Name important intermediate profiles/solids (e.g. name='TowerCore', name='VoidCutter', name='Section1') so boolean_difference, loft, sweep1, planar_srf, twist, taper, pipe, and contour_slice compose complex multi-part architecture cleanly!
                3. Use realistic dimensions appropriate to ModelUnits shown in RHINO VIEWPORT & SCENE CONTEXT (e.g., mm vs meters vs inches), organize parts onto named sublayers with distinct colors, and add zoom {'scope':'extents'} when framing new scenes.
                4. NEVER wrap JSON in markdown code fences. If no Rhino edit is needed or the user is asking a question, return {"ops":[],"explanation":"..."}.
                """;
        }

        public static string BuildConversationalSystem()
        {
            return """
                You are GH Copilot running in Direct Rhino 8 Mode (no Grasshopper required).
                The user asked a conversational question or read-only scene inspection question.
                Reply concisely in plain text without returning JSON or code fences.
                Use RHINO VIEWPORT & SCENE CONTEXT when present to answer accurately about viewport camera, display mode, units, visible layers, selection, and object counts/bounds.
                """;
        }

        public static string BuildExplainSystem()
        {
            return """
                You are GH Copilot running in Direct Rhino 8 Mode.
                Inspect RHINO VIEWPORT & SCENE CONTEXT (and any follow-up user question) and summarize clearly:
                1. Active document units, viewport name, projection, display mode, and camera.
                2. Object types, counts, selection, bounds, and visible layers.
                3. Any direct advice or answer to the user's question.
                """;
        }

        public static string BuildPayload(
            ChatSession session,
            RhinoViewportSnapshot? viewport,
            string newUser,
            IReadOnlyList<string>? lastCreatedSummary = null)
        {
            if (session is null) throw new ArgumentNullException(nameof(session));
            if (string.IsNullOrWhiteSpace(newUser)) throw new ArgumentException("User prompt must not be empty.", nameof(newUser));

            var sb = new StringBuilder();
            sb.AppendLine("SESSION SUMMARY:");
            sb.AppendLine(string.IsNullOrEmpty(session.Summary) ? "(none)" : RedactGuids(session.Summary));
            sb.AppendLine("RECENT MESSAGES (role-labelled conversation data):");
            foreach (ChatMessage message in session.Messages)
            {
                sb.AppendLine(JsonSerializer.Serialize(new { role = message.Role, content = RedactGuids(message.Content) }));
            }

            if (viewport != null && viewport.HasActiveDocument)
            {
                sb.AppendLine("RHINO VIEWPORT & SCENE CONTEXT:");
                sb.AppendLine(RedactGuids(viewport.FormatForPrompt()));
            }
            else
            {
                sb.AppendLine("RHINO VIEWPORT & SCENE CONTEXT:");
                sb.AppendLine("No active Rhino document.");
            }

            if (lastCreatedSummary != null && lastCreatedSummary.Count > 0)
            {
                sb.AppendLine("LAST CREATED OBJECTS IN THIS SESSION (targetable via target='last_created'):");
                foreach (string item in lastCreatedSummary)
                {
                    sb.AppendLine("- " + RedactGuids(item));
                }
            }

            if (!string.IsNullOrWhiteSpace(session.PendingPlanSummary))
            {
                sb.AppendLine("APPROVED / PENDING PLAN CONTEXT:");
                sb.AppendLine(RedactGuids(session.PendingPlanSummary));
            }

            if (!string.IsNullOrWhiteSpace(session.LastViewportVerification))
            {
                sb.AppendLine("LAST VIEWPORT VERIFICATION:");
                sb.AppendLine(RedactGuids(session.LastViewportVerification));
            }

            sb.AppendLine("NEW USER MESSAGE:");
            sb.Append(newUser.Trim());
            return sb.ToString();
        }

        public static string BuildValidationRepairPrompt(
            string basePayload,
            string previousRawResponse,
            IReadOnlyList<string> errors)
        {
            if (basePayload is null) throw new ArgumentNullException(nameof(basePayload));
            if (errors is null) throw new ArgumentNullException(nameof(errors));

            var sb = new StringBuilder();
            sb.AppendLine(basePayload);
            sb.AppendLine();
            sb.AppendLine("YOUR PREVIOUS RhinoActionPlan JSON FAILED VALIDATION. Fix ONLY the issues listed below and return valid RhinoActionPlan JSON.");
            foreach (string err in errors)
            {
                sb.Append("- ");
                sb.AppendLine(err);
            }
            sb.AppendLine("PREVIOUS INVALID OUTPUT:");
            sb.Append(string.IsNullOrWhiteSpace(previousRawResponse) ? "(empty)" : RedactGuids(previousRawResponse.Trim()));
            return sb.ToString();
        }

        private static string RedactGuids(string text) => GuidText.Replace(text, "[guid omitted]");
    }
}
