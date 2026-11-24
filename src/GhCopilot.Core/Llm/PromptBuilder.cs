using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using GhCopilot.Core.Graph;
using GhCopilot.Core.Index;

namespace GhCopilot.Core.Llm
{
    /// <summary>
    /// Builds compact system instructions, shortlist context blocks, recipe few-shots, and targeted repair prompts.
    /// </summary>
    public static class PromptBuilder
    {
        public static string BuildSystemInstruction()
        {
            return """
                You are GH Copilot, an expert Rhino 8 and Grasshopper computational design assistant.
                Your task is to generate a valid Grasshopper definition graph as JSON matching the required schema.

                Strict rules:
                1. Every node's "ref" MUST be one of the short IDs from SHORTLIST COMPONENTS. Never invent GUIDs or unlisted component IDs. You may create rich multi-stage definitions up to 200 nodes.
                2. COMPONENT-FIRST PRIORITY: Prefer [COMPANY] components/clusters first, then existing native Grasshopper components, installed third-party plugin components (e.g., LunchBox, Pufferfish, Weaverbird, Human, Elefront, Kangaroo, Karamba, etc.), and Rhino.Inside.Revit ("Revit>*") components from SHORTLIST COMPONENTS whenever they match the request.
                3. Every wire connects an OUTPUT parameter (fromParam) of fromNode to an INPUT parameter (toParam) of toNode.
                4. SLIDERS, PANEL INPUTS & SELECTION INPUTS:
                   - For Number Sliders ("Number Slider"), always provide settings { "min": ..., "max": ..., "value": ..., "decimals": ..., "nickname": "..." } (decimals: 0 for integers, 1-2 for floats) and wire out with fromParam: "" or "Number".
                   - For Panel inputs or displays ("Panel"), set settings { "text": "...", "nickname": "..." }. Panels can act as inputs wired into components (fromParam: "") or outputs receiving data (toParam: "").
                   - When asked to place selection inputs or reference Rhino geometry (curves, breps, surfaces, meshes, points, geometry), place standalone parameter nodes from Params>Geometry in SHORTLIST COMPONENTS ("Geometry", "Curve", "Brep", "Surface", "Mesh", "Point") with clear nicknames at the left input column and wire their output (fromParam: "") into downstream components.
                5. Never create cycles or wire into an output / out of an input.
                6. Use exact parameter names shown in "in:" and "out:". For standalone parameter nodes (Number Slider, Panel, Boolean Toggle, Curve, Brep, Point, Surface, Mesh, Geometry), use "" (empty string) or the shown parameter name.
                7. For data-tree operations, you may set pin modifiers directly on wires ("targetMapping": "flatten"|"graft", "simplify": true, "reverse": true) or via node settings ("flattenInputs", "graftInputs", "simplifyInputs", "reverseInputs") instead of cluttering the canvas with extra tree nodes.
                8. If SELECTED CANVAS OUTPUTS AVAILABLE FOR WIRING are shown and the user asks to operate on their selection, wire fromNode "$selected" (or "$s1", "$s2", ...) into your new nodes.
                9. Arrange nodes cleanly left-to-right: inputs around x=150, advancing +200 per stage; vertical spacing +90 per parallel node.
                10. AVOID UNNECESSARY PYTHON SCRIPTS: Solve tasks by wiring real Grasshopper, plugin, and Revit components from SHORTLIST COMPONENTS rather than creating a Python script. Only populate "script" when explicitly requested by the user or if no combination of shortlisted components can compute custom recursive math (using only import Rhino.Geometry as rg and import math; never os, sys, subprocess, network, file I/O, or eval/exec).
                """;
        }

        public static string BuildUserPrompt(
            string userPrompt,
            IReadOnlyList<ComponentRecord> shortlist,
            IReadOnlyList<RecipeRecord>? matchingRecipes = null,
            CanvasSnapshot? selectedContext = null)
        {
            if (string.IsNullOrWhiteSpace(userPrompt))
            {
                throw new ArgumentException("User prompt must not be empty.", nameof(userPrompt));
            }

            if (shortlist is null)
            {
                throw new ArgumentNullException(nameof(shortlist));
            }

            var sb = new StringBuilder();
            sb.AppendLine("USER REQUEST:");
            sb.AppendLine(userPrompt.Trim());

            if (selectedContext != null && selectedContext.SelectedInstanceGuids.Count > 0)
            {
                string wireHeader = selectedContext.FormatSelectedOutputsForWiring();
                if (!string.IsNullOrWhiteSpace(wireHeader))
                {
                    sb.AppendLine();
                    sb.AppendLine(wireHeader);
                }
            }

            if (matchingRecipes != null && matchingRecipes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("MATCHING SAVED RECIPES (REUSE/ADAPT WHEN APPROPRIATE):");
                foreach (RecipeRecord recipe in matchingRecipes)
                {
                    sb.AppendLine($"- Recipe '{recipe.Name}': {recipe.Description}");
                    sb.AppendLine($"  Pattern: {JsonSerializer.Serialize(recipe.Spec)}");
                }
            }

            sb.AppendLine();
            AppendShortlist(sb, shortlist);
            return sb.ToString().TrimEnd();
        }

        public static string BuildValidatorRepairPrompt(
            string userPrompt,
            IReadOnlyList<ComponentRecord> shortlist,
            string failedResponse,
            IReadOnlyList<string> errors)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Your previous JSON output failed validation. Fix ONLY the failing issues and return a valid JSON GraphSpec.");
            sb.AppendLine();
            sb.AppendLine("VALIDATION ERRORS:");
            foreach (string err in errors)
            {
                sb.Append("- ");
                sb.AppendLine(err);
            }

            sb.AppendLine();
            sb.AppendLine("ORIGINAL USER REQUEST:");
            sb.AppendLine(userPrompt.Trim());
            sb.AppendLine();
            sb.AppendLine("PREVIOUS OUTPUT:");
            sb.AppendLine(string.IsNullOrWhiteSpace(failedResponse) ? "(empty)" : failedResponse.Trim());
            sb.AppendLine();
            AppendShortlist(sb, shortlist);
            return sb.ToString().TrimEnd();
        }

        public static string BuildScriptFallbackPrompt(
            string userPrompt,
            IReadOnlyList<ComponentRecord> shortlist,
            string failedResponse,
            IReadOnlyList<string> errors)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Native component wiring failed validation twice. Solve the request using the Python 3 'script' fallback block instead.");
            sb.AppendLine("Return nodes: [] and wires: [] (or optionally Number Sliders wired to node 'script'), and populate 'script' with clean Python 3 code using ONLY Rhino.Geometry (import Rhino.Geometry as rg) and math.");
            sb.AppendLine("Assign final geometry/values to output variable names listed in script.outputs, and provide sensible defaults inside the script when inputs are None.");
            sb.AppendLine();
            sb.AppendLine("PREVIOUS ERRORS:");
            foreach (string err in errors)
            {
                sb.Append("- ");
                sb.AppendLine(err);
            }

            sb.AppendLine();
            sb.AppendLine("ORIGINAL USER REQUEST:");
            sb.AppendLine(userPrompt.Trim());
            sb.AppendLine();
            sb.AppendLine("PREVIOUS OUTPUT:");
            sb.AppendLine(string.IsNullOrWhiteSpace(failedResponse) ? "(empty)" : failedResponse.Trim());
            sb.AppendLine();
            AppendShortlist(sb, shortlist);
            return sb.ToString().TrimEnd();
        }

        public static string BuildRuntimeRepairPrompt(
            string userPrompt,
            IReadOnlyList<ComponentRecord> shortlist,
            GraphSpec previousSpec,
            IReadOnlyList<string> runtimeMessages)
        {
            var sb = new StringBuilder();
            sb.AppendLine("The Grasshopper definition was spawned and solved, but produced runtime error/warning messages.");
            sb.AppendLine("Return an updated JSON GraphSpec that fixes these runtime issues.");
            sb.AppendLine();
            sb.AppendLine("GRASSHOPPER RUNTIME MESSAGES:");
            foreach (string msg in runtimeMessages)
            {
                sb.Append("- ");
                sb.AppendLine(msg);
            }

            sb.AppendLine();
            sb.AppendLine("ORIGINAL USER REQUEST:");
            sb.AppendLine(userPrompt.Trim());
            sb.AppendLine();
            sb.AppendLine("PREVIOUS GRAPH JSON:");
            sb.AppendLine(System.Text.Json.JsonSerializer.Serialize(previousSpec));
            sb.AppendLine();
            AppendShortlist(sb, shortlist);
            return sb.ToString().TrimEnd();
        }

        public static string BuildExplainSystemInstruction()
        {
            return """
                You are GH Copilot, an expert Rhino 8 and Grasshopper computational design assistant.
                Explain the selected Grasshopper definition clearly and concisely:
                1. Summarize what the subgraph computes end-to-end.
                2. Describe key inputs (sliders/panels) and outputs.
                3. If any runtime Error/Warning messages are present, explain why they happen and how to fix them.
                """;
        }

        public static string BuildExplainSelectionPrompt(
            CanvasSnapshot snapshot,
            string? userHint,
            bool includeNonSelectedContext)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            var sb = new StringBuilder();
            sb.AppendLine("EXPLAIN THIS GRASSHOPPER SUBGRAPH:");
            if (!string.IsNullOrWhiteSpace(userHint))
            {
                sb.AppendLine($"USER QUESTION: {userHint!.Trim()}");
            }
            sb.AppendLine();
            sb.AppendLine(snapshot.FormatForPrompt(includeNonSelectedContext));
            return sb.ToString().TrimEnd();
        }

        public static string BuildFixSelectionPrompt(
            CanvasSnapshot snapshot,
            IReadOnlyList<ComponentRecord> shortlist,
            string? userHint,
            bool includeNonSelectedContext)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (shortlist is null)
            {
                throw new ArgumentNullException(nameof(shortlist));
            }

            var sb = new StringBuilder();
            sb.AppendLine("Fix the selected Grasshopper subgraph so it solves cleanly without runtime errors or bad wiring.");
            sb.AppendLine("Return a complete replacement GraphSpec JSON for the selected nodes positioned near their original coordinates.");
            if (!string.IsNullOrWhiteSpace(userHint))
            {
                sb.AppendLine($"USER REQUEST / HINT: {userHint!.Trim()}");
            }

            sb.AppendLine();
            sb.AppendLine("CURRENT CANVAS SELECTION:");
            sb.AppendLine(snapshot.FormatForPrompt(includeNonSelectedContext));
            sb.AppendLine();
            AppendShortlist(sb, shortlist);
            return sb.ToString().TrimEnd();
        }

        private static void AppendShortlist(StringBuilder sb, IReadOnlyList<ComponentRecord> shortlist)
        {
            sb.AppendLine("SHORTLIST COMPONENTS:");
            foreach (ComponentRecord record in shortlist)
            {
                sb.AppendLine(record.FormatPromptContextLine());
            }
        }
    }
}
