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
            @"\b(?:grasshopper|\.gh|ghx|slider|sliders|panel|component|components|wire|wires|canvas|parametric|attractor|voronoi|delaunay|ghpython|python\s*script|cluster|recipe|flatten|graft|data\s*tree|definition)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
                Return ONLY a RhinoActionPlan JSON object matching the supplied JSON schema: { "ops": [...], "explanation": "..." }.

                SUPPORTED OPERATIONS (use exact 'op' names and omit unused properties):
                - Create geometry:
                  * add_point: point [x,y,z], optional layer, name, color [r,g,b]
                  * add_line: start [x,y,z], end [x,y,z], optional layer, name, color
                  * add_circle: center [x,y,z] (default [0,0,0]), radius (>0), optional normal [x,y,z], layer, name, color
                  * add_rectangle: origin [x,y,z] (default [0,0,0]), width (>0), depth (>0, or height >0), optional centered (bool), normal, layer, name, color
                  * add_polyline: points [[x,y,z],...], optional closed (bool), smooth (bool), layer, name, color
                  * add_box: origin [x,y,z] (default [0,0,0]), width (>0), depth (>0), height (>0), optional centered (bool; default false uses origin as min corner [0..w,0..d,0..h], centered:true centers box at origin), layer, name, color
                  * add_sphere: center [x,y,z] (default [0,0,0]), radius (>0), optional layer, name, color
                  * add_cylinder: origin [x,y,z] (base center, default [0,0,0]), radius (>0), height (>0), optional capped (default true), layer, name, color
                  * add_cone: origin [x,y,z] (base center, default [0,0,0]), radius (>0), height (>0), optional capped (default true), layer, name, color
                  * extrude: height (non-zero), optional target (default 'selected' or 'last_created'), capped (default true), layer, name, color
                  * loft: optional target (2+ curves, default 'selected' or 'last_created'), loftType ('normal'|'loose'|'tight'|'straight'|'uniform'), closed (bool), layer, name, color
                  * offset_curve: distance (non-zero), optional target, normal, layer, name, color
                - Transform / duplicate / delete existing objects:
                  * move: vector [dx,dy,dz], optional target
                  * rotate: angleDegrees, optional center [x,y,z], axis [x,y,z] (default [0,0,1]), target
                  * scale: factor (>0) OR factors [sx,sy,sz], optional center [x,y,z], target
                  * copy: optional vector [dx,dy,dz] (default [0,0,0]), target
                  * array_linear: count (2..200), vector [dx,dy,dz] step per item, optional target
                  * array_polar: count (2..200), optional totalAngleDegrees (default 360), center [x,y,z], axis [x,y,z], target
                  * delete: optional target (default 'selected')
                - Layers, colors, naming, selection & visibility:
                  * create_layer: layer (string, supports 'Parent::Child'), optional color [r,g,b], visible (bool)
                  * set_layer: layer (string, created automatically if needed), optional target
                  * set_color: color [r,g,b] (0..255), optional target
                  * rename: name (string), optional target
                  * select: target (required), optional clearFirst (default true)
                  * hide: optional target (default 'selected')
                  * show: optional target (default 'all')
                - Viewport & camera:
                  * set_display_mode: displayMode ('Shaded'|'Rendered'|'Wireframe'|'Ghosted'|'Arctic'|'Pen'|'Technical'|'Monochrome')
                  * zoom: scope ('extents'|'selected', default 'extents')
                  * set_view: viewName ('Perspective'|'Top'|'Front'|'Right'|'Left'|'Back'|'Bottom'|'TwoPointPerspective')
                  * set_camera: cameraLocation [x,y,z], cameraTarget [x,y,z]

                RULES:
                1. Target selectors: 'selected', 'last_created', 'all', 'layer:<layerName>', 'name:<objectName>', or 'type:<point|curve|brep|extrusion|surface|mesh|polysurface|solid|subd>'.
                2. Objects created earlier in the SAME plan immediately become 'last_created', so you can create geometry then move/array/color/layer it cleanly in one plan.
                3. Use realistic dimensions appropriate to ModelUnits shown in RHINO VIEWPORT & SCENE CONTEXT (e.g., mm vs meters vs inches).
                4. When creating multiple objects cleanly in a fresh scene, consider adding zoom {'scope':'extents'} at the end if helpful.
                5. NEVER wrap JSON in markdown code fences. If no Rhino edit is needed or the user is asking a question, return {"ops":[],"explanation":"..."}.
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
