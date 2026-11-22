using System;
using System.Collections.Generic;
using System.Text.Json;
using GhCopilot.Core.Graph;
using GhCopilot.Core.Host;
using GhCopilot.Core.Llm;
using GhCopilot.Core.Rhino;
using Xunit;

namespace GhCopilot.Tests
{
    public sealed class RhinoActionTests
    {
        [Fact]
        public void RhinoActionSchema_BuildsValidObjectSchemaWithOpsAndExplanation()
        {
            JsonElement schema = RhinoActionSchema.Build();

            Assert.Equal(JsonValueKind.Object, schema.ValueKind);
            Assert.Equal("object", schema.GetProperty("type").GetString());

            JsonElement props = schema.GetProperty("properties");
            Assert.True(props.TryGetProperty("ops", out JsonElement opsProp));
            Assert.True(props.TryGetProperty("explanation", out JsonElement explanationProp));
            Assert.Equal("array", opsProp.GetProperty("type").GetString());
            Assert.Equal("string", explanationProp.GetProperty("type").GetString());

            JsonElement opEnum = opsProp.GetProperty("items").GetProperty("properties").GetProperty("op").GetProperty("enum");
            var enumValues = new List<string>();
            foreach (JsonElement item in opEnum.EnumerateArray())
            {
                enumValues.Add(item.GetString()!);
            }

            Assert.Equal(RhinoActionPlanValidator.SupportedOpNames.Count, enumValues.Count);
            Assert.Contains("add_box", enumValues);
            Assert.Contains("set_layer", enumValues);
            Assert.Contains("set_color", enumValues);
            Assert.Contains("set_display_mode", enumValues);
            Assert.Contains("array_linear", enumValues);
            Assert.Contains("zoom", enumValues);

            var required = new List<string>();
            foreach (JsonElement req in schema.GetProperty("required").EnumerateArray())
            {
                required.Add(req.GetString()!);
            }
            Assert.Contains("ops", required);
            Assert.Contains("explanation", required);
        }

        [Fact]
        public void Validator_AcceptsValidCreateTransformStyleAndViewportPlan()
        {
            var plan = new RhinoActionPlan
            {
                Explanation = "Create architectural bay with columns, array them, color red, and zoom shaded.",
                Ops = new List<RhinoActionOp>
                {
                    new RhinoActionOp { Op = "create_layer", Layer = "Architecture::Columns", Color = new[] { 16, 185, 129 }, Visible = true },
                    new RhinoActionOp { Op = "add_box", Origin = new[] { 0.0, 0.0, 0.0 }, Width = 20.0, Depth = 10.0, Height = 1.5, Layer = "Architecture", Name = "Plinth" },
                    new RhinoActionOp { Op = "add_cylinder", Origin = new[] { 2.0, 2.0, 1.5 }, Radius = 0.5, Height = 4.0, Capped = true, Layer = "Architecture::Columns" },
                    new RhinoActionOp { Op = "array_linear", Target = "last_created", Count = 4, Vector = new[] { 5.0, 0.0, 0.0 } },
                    new RhinoActionOp { Op = "set_color", Target = "layer:Architecture::Columns", Color = new[] { 220, 38, 38 } },
                    new RhinoActionOp { Op = "set_layer", Target = "selected", Layer = "Architecture::Modified" },
                    new RhinoActionOp { Op = "rotate", Target = "type:brep", AngleDegrees = 45.0, Center = new[] { 0.0, 0.0, 0.0 }, Axis = new[] { 0.0, 0.0, 1.0 } },
                    new RhinoActionOp { Op = "scale", Target = "name:Plinth", Factor = 1.25 },
                    new RhinoActionOp { Op = "set_display_mode", DisplayMode = "Shaded" },
                    new RhinoActionOp { Op = "set_view", ViewName = "Perspective" },
                    new RhinoActionOp { Op = "set_camera", CameraLocation = new[] { 40.0, -40.0, 30.0 }, CameraTarget = new[] { 10.0, 5.0, 3.0 } },
                    new RhinoActionOp { Op = "zoom", Scope = "extents" }
                }
            };

            RhinoActionValidationResult validation = RhinoActionPlanValidator.Validate(plan);

            Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
            Assert.Empty(validation.Errors);
        }

        [Fact]
        public void Validator_RejectsUnsupportedOp_NegativeRadius_NaNCoord_ExcessiveArrayCount_InvalidRgb_InvalidTarget_IdenticalCamera()
        {
            var plan = new RhinoActionPlan
            {
                Ops = new List<RhinoActionOp>
                {
                    new RhinoActionOp { Op = "nuke_everything" },
                    new RhinoActionOp { Op = "add_sphere", Center = new[] { double.NaN, 0.0, 0.0 }, Radius = -3.0 },
                    new RhinoActionOp { Op = "array_linear", Count = 500, Vector = new[] { 10.0, 0.0, 0.0 } },
                    new RhinoActionOp { Op = "set_color", Target = "bogus_selector", Color = new[] { 300, -1, 128 } },
                    new RhinoActionOp { Op = "set_camera", CameraLocation = new[] { 5.0, 5.0, 5.0 }, CameraTarget = new[] { 5.0, 5.0, 5.0 } }
                }
            };

            RhinoActionValidationResult validation = RhinoActionPlanValidator.Validate(plan);

            Assert.False(validation.IsValid);
            Assert.Contains(validation.Errors, e => e.Contains("Unsupported", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(validation.Errors, e => e.Contains("Radius", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(validation.Errors, e => e.Contains("finite", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(validation.Errors, e => e.Contains("200", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(validation.Errors, e => e.Contains("RGB", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(validation.Errors, e => e.Contains("Invalid target", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(validation.Errors, e => e.Contains("identical", StringComparison.OrdinalIgnoreCase));
        }

        [Theory]
        [InlineData(CopilotWorkMode.Auto, "create a box 10x10x10", false, false, CopilotWorkMode.RhinoDirect)]
        [InlineData(CopilotWorkMode.Auto, "create a circle with a radius slider", false, false, CopilotWorkMode.Grasshopper)]
        [InlineData(CopilotWorkMode.Auto, "create a box 10x10x10", true, false, CopilotWorkMode.Grasshopper)]
        [InlineData(CopilotWorkMode.Auto, "switch viewport to shaded and zoom extents", true, false, CopilotWorkMode.RhinoDirect)]
        [InlineData(CopilotWorkMode.Auto, "create layer Walls and paint selected red", true, false, CopilotWorkMode.RhinoDirect)]
        [InlineData(CopilotWorkMode.RhinoDirect, "create a circle with a radius slider", true, true, CopilotWorkMode.RhinoDirect)]
        [InlineData(CopilotWorkMode.Grasshopper, "switch viewport to shaded", false, false, CopilotWorkMode.Grasshopper)]
        [InlineData(CopilotWorkMode.Auto, "color it red", false, true, CopilotWorkMode.Grasshopper)]
        public void ResolveEffectiveMode_RoutesAutoRhinoAndGrasshopperCorrectly(
            CopilotWorkMode selected,
            string prompt,
            bool isGhOpen,
            bool useRhinoSelection,
            CopilotWorkMode expected)
        {
            CopilotWorkMode actual = RhinoPromptBuilder.ResolveEffectiveMode(selected, prompt, isGhOpen, useRhinoSelection);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void RhinoPromptBuilder_IncludesViewportAndRedactsGuids()
        {
            var session = new ChatSession();
            session.CompleteTurn("hi", "hello from aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            var viewport = new RhinoViewportSnapshot
            {
                HasActiveDocument = true,
                DocumentName = "TowerModel.3dm",
                ModelUnits = "Meters",
                ActiveViewName = "Perspective",
                DisplayMode = "Shaded",
                Projection = "Perspective",
                CameraLocation = "(35.00, -45.00, 25.00)",
                CameraTarget = "(0.00, 0.00, 10.00)",
                CameraDirection = "(-0.60, 0.70, -0.38)",
                TotalVisibleObjects = 4,
                SelectedObjectCount = 1,
                VisibleLayers = new List<string> { "Default (4)" }
            };

            string payload = RhinoPromptBuilder.BuildPayload(
                session,
                viewport,
                "move selected up 5m",
                new[] { "Extrusion \"Core\" (layer=Default)" });

            Assert.Contains("RHINO VIEWPORT & SCENE CONTEXT:", payload);
            Assert.Contains("TowerModel.3dm", payload);
            Assert.Contains("Units: Meters", payload);
            Assert.Contains("DisplayMode=Shaded", payload);
            Assert.Contains("LAST CREATED OBJECTS IN THIS SESSION", payload);
            Assert.Contains("Extrusion \"Core\"", payload);
            Assert.Contains("NEW USER MESSAGE:", payload);
            Assert.DoesNotContain("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", payload);
            Assert.Contains("[guid omitted]", payload);

            string sys = RhinoPromptBuilder.BuildSystem();
            Assert.Contains("add_box", sys);
            Assert.Contains("set_display_mode", sys);
            Assert.Contains("last_created", sys);
        }

        [Theory]
        [InlineData("zoom extents", false)]
        [InlineData("switch to shaded", false)]
        [InlineData("hide selected", false)]
        [InlineData("color selected red", false)]
        [InlineData("create layer Walls", false)]
        [InlineData("rename to Column", false)]
        [InlineData("show all hidden objects", false)]
        [InlineData("select all curves", false)]
        [InlineData("what is in my viewport?", true)]
        [InlineData("what layers are visible?", true)]
        [InlineData("how many objects are selected?", true)]
        public void ChatPromptBuilder_IsConversationalOnlyPrompt_DistinguishesRhinoCommandsFromQuestions(
            string prompt,
            bool expectedConversational)
        {
            Assert.Equal(expectedConversational, ChatPromptBuilder.IsConversationalOnlyPrompt(prompt));
        }
    }
}
