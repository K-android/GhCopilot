using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GhCopilot.Core.Graph;
using GhCopilot.Core.Host;
using GhCopilot.Core.Index;
using GhCopilot.Core.Llm;
using GhCopilot.Core.Rhino;
using GhCopilot.Core.Settings;
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
                    new RhinoActionOp { Op = "array_linear", Count = 1000, Vector = new[] { 10.0, 0.0, 0.0 } },
                    new RhinoActionOp { Op = "set_color", Target = "bogus_selector", Color = new[] { 300, -1, 128 } },
                    new RhinoActionOp { Op = "set_camera", CameraLocation = new[] { 5.0, 5.0, 5.0 }, CameraTarget = new[] { 5.0, 5.0, 5.0 } }
                }
            };

            RhinoActionValidationResult validation = RhinoActionPlanValidator.Validate(plan);

            Assert.False(validation.IsValid);
            Assert.Contains(validation.Errors, e => e.Contains("Unsupported", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(validation.Errors, e => e.Contains("Radius", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(validation.Errors, e => e.Contains("finite", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(validation.Errors, e => e.Contains("500", StringComparison.OrdinalIgnoreCase));
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

        [Fact]
        public void AppSettings_DefaultTimeoutIs180_AndUpgradesLegacy45SecondSetting()
        {
            var fresh = new AppSettings();
            Assert.Equal(180, fresh.RequestTimeoutSeconds);
            Assert.True(fresh.PlanFirstMode);

            string tempDir = Path.Combine(Path.GetTempPath(), "GhCopilotTimeout_" + Guid.NewGuid().ToString("N"));
            string settingsPath = Path.Combine(tempDir, "settings.json");
            try
            {
                Directory.CreateDirectory(tempDir);
                File.WriteAllText(settingsPath, "{\"provider\":\"Gemini\",\"modelId\":\"gemini-2.5-flash\",\"requestTimeoutSeconds\":45}", Encoding.UTF8);
                AppSettings upgraded = AppSettings.Load(settingsPath);
                Assert.Equal(180, upgraded.RequestTimeoutSeconds);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void PlanFirst_DetectsNewRequests_Approvals_AndFollowUpAlterations()
        {
            var session = new ChatSession();

            Assert.True(ChatPromptBuilder.ShouldPlanBeforeBuilding("design a pavilion canopy with columns", session, planFirstEnabled: true));
            Assert.False(ChatPromptBuilder.ShouldPlanBeforeBuilding("design a pavilion canopy with columns", session, planFirstEnabled: false));
            Assert.False(ChatPromptBuilder.ShouldPlanBeforeBuilding("hi there", session, planFirstEnabled: true));

            Assert.True(ChatPromptBuilder.IsApprovalOrBuildConfirmation("yes"));
            Assert.True(ChatPromptBuilder.IsApprovalOrBuildConfirmation("looks good, build it"));
            Assert.True(ChatPromptBuilder.IsApprovalOrBuildConfirmation("[Approve & Build] Yes, approved — build the planned design now."));
            Assert.False(ChatPromptBuilder.IsApprovalOrBuildConfirmation("create a twisted tower"));

            session.PendingPlanSummary = "Step 1: create 20x20x10 plinth; Step 2: 4 columns";
            session.CompleteTurn("design a pavilion", session.PendingPlanSummary);
            Assert.False(ChatPromptBuilder.ShouldPlanBeforeBuilding("Approve & Build", session, planFirstEnabled: true));
            Assert.False(ChatPromptBuilder.ShouldPlanBeforeBuilding("yes", session, planFirstEnabled: true));

            string effective = ChatPromptBuilder.BuildEffectiveExecutionPrompt("yes", session);
            Assert.Contains("USER APPROVED THIS PLAN", effective);
            Assert.Contains("Step 1: create 20x20x10 plinth", effective);
            Assert.Contains("design a pavilion", effective);

            string retrieval = ChatPromptBuilder.BuildRetrievalQuery(session, "yes");
            Assert.Contains("plinth", retrieval);

            session.PendingPlanSummary = string.Empty;
            session.HasBuiltInCurrentSession = true;
            session.LastExecutedSummary = "Built pavilion plinth and 4 columns";
            Assert.True(ChatPromptBuilder.IsFollowUpAlterationPrompt("now make it taller and color it red"));
            Assert.False(ChatPromptBuilder.ShouldPlanBeforeBuilding("now make it taller and color it red", session, planFirstEnabled: true));

            string followUp = ChatPromptBuilder.BuildEffectiveExecutionPrompt("now make it taller", session);
            Assert.Contains("FOLLOW-UP ALTERATION TO EXISTING WORK", followUp);
            Assert.Contains("Built pavilion plinth", followUp);

            string planSystem = ChatPromptBuilder.BuildPlanAndClarifySystem(CopilotWorkMode.RhinoDirect);
            Assert.Contains("```diagram", planSystem);
        }

        [Fact]
        public void RhinoViewportSnapshot_BuildDiffSummary_ReportsBeforeAfterChanges_AndIgnoresImageInJson()
        {
            var before = new RhinoViewportSnapshot
            {
                HasActiveDocument = true,
                TotalVisibleObjects = 2,
                ActiveViewName = "Perspective",
                DisplayMode = "Wireframe",
                SceneBoundingBox = "(0.00, 0.00, 0.00)..(5.00, 5.00, 5.00)"
            };

            var after = new RhinoViewportSnapshot
            {
                HasActiveDocument = true,
                TotalVisibleObjects = 6,
                SelectedObjectCount = 1,
                ActiveViewName = "Perspective",
                DisplayMode = "Shaded",
                SceneBoundingBox = "(0.00, 0.00, 0.00)..(24.00, 24.00, 15.00)",
                ViewportImageBase64Png = "AAAA"
            };

            string diff = RhinoViewportSnapshot.BuildDiffSummary(before, after);
            Assert.Contains("6 visible object(s) (+4 new)", diff);
            Assert.Contains("Selected=1", diff);
            Assert.Contains("Perspective (Shaded)", diff);

            string json = JsonSerializer.Serialize(after);
            Assert.DoesNotContain("ViewportImageBase64Png", json);
            Assert.DoesNotContain("AAAA", json);
        }

        [Fact]
        public void GraphPatchValidator_AllowsTextUpdateOnPythonScriptNodes_AndRejectsUnsafeScriptCode()
        {
            var snapshot = new CanvasSnapshot
            {
                Nodes = new List<CanvasNodeSnapshot>
                {
                    new CanvasNodeSnapshot
                    {
                        Id = "s1",
                        Name = "Python 3 Script",
                        ShortId = "py3",
                        Outputs = new List<ParamRecord> { new ParamRecord { Name = "geo", NickName = "geo", TypeName = "Geometry" } }
                    }
                }
            };
            var shortlist = new List<ComponentRecord>
            {
                new ComponentRecord { ShortId = "py3", Name = "Python 3 Script" }
            };

            var validPatch = new GraphPatch
            {
                Explanation = "Double sphere radius",
                Ops = new List<GraphPatchOp>
                {
                    new GraphPatchOp
                    {
                        Op = "set_param",
                        NodeId = "s1",
                        Settings = new NodeSettings { Text = "import Rhino.Geometry as rg\ngeo = rg.Sphere(rg.Point3d.Origin, 10.0)" }
                    }
                }
            };
            Assert.True(GraphPatchValidator.Validate(validPatch, snapshot, shortlist).IsValid);

            var unsafePatch = new GraphPatch
            {
                Explanation = "Unsafe import",
                Ops = new List<GraphPatchOp>
                {
                    new GraphPatchOp
                    {
                        Op = "set_param",
                        NodeId = "s1",
                        Settings = new NodeSettings { Text = "import os\ngeo = None" }
                    }
                }
            };
            PatchValidationResult bad = GraphPatchValidator.Validate(unsafePatch, snapshot, shortlist);
            Assert.False(bad.IsValid);
            Assert.Contains(bad.Errors, e => e.Contains("Unsafe", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task GeminiClient_SupportsMultimodalImageBase64Attachment()
        {
            string? capturedBody = null;
            int callCount = 0;

            var handler = new StubHttpHandler(req =>
            {
                callCount++;
                capturedBody = req.Content != null ? req.Content.ReadAsStringAsync().Result : null;
                string responseJson = JsonSerializer.Serialize(new
                {
                    candidates = new[]
                    {
                        new { content = new { parts = new[] { new { text = "I see a shaded box in Perspective." } } } }
                    }
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };
            });

            using var http = new HttpClient(handler);
            using var client = new LlmClient(http);
            string reply = await client.GenerateTextAsync(
                "AIzaSy-TestKey-1234",
                "gemini-2.5-flash",
                "Inspect viewport.",
                "What do you see?",
                TimeSpan.FromSeconds(10),
                CancellationToken.None,
                "Gemini",
                null,
                "iVBORw0KGgo=");

            Assert.Equal(1, callCount);
            Assert.Equal("I see a shaded box in Perspective.", reply);
            Assert.NotNull(capturedBody);
            Assert.Contains("inline_data", capturedBody);
            Assert.Contains("image/png", capturedBody);
            Assert.Contains("iVBORw0KGgo=", capturedBody);
        }

        [Fact]
        public void ComponentRecord_FormatPromptContextLine_IncludesPluginLibraryNameAndRevitFlag()
        {
            var revitWall = new ComponentRecord
            {
                ShortId = "c0142",
                Name = "Add Wall (Curve)",
                Category = "Revit",
                SubCategory = "Wall",
                Source = "plugin",
                LibraryName = "RhinoInside.Revit.GH",
                Inputs = new List<ParamRecord> { new ParamRecord { Name = "Curve", TypeName = "Curve", Access = "item" } },
                Outputs = new List<ParamRecord> { new ParamRecord { Name = "Wall", TypeName = "Wall", Access = "item" } }
            };

            Assert.True(revitWall.IsRevitComponent);
            string revitLine = revitWall.FormatPromptContextLine();
            Assert.Contains("Revit>Wall [RhinoInside.Revit.GH]", revitLine);

            var lunchboxPanel = new ComponentRecord
            {
                ShortId = "c0210",
                Name = "Diamond Panels",
                Category = "LunchBox",
                SubCategory = "Panels",
                Source = "plugin",
                LibraryName = "LunchBox"
            };

            Assert.False(lunchboxPanel.IsRevitComponent);
            string lbLine = lunchboxPanel.FormatPromptContextLine();
            Assert.Contains("LunchBox>Panels", lbLine);
            Assert.DoesNotContain("[LunchBox]", lbLine);
        }

        [Fact]
        public void Retriever_PrioritizesRhinoInsideRevitAndThirdPartyPluginComponents_OnMatchingPrompts()
        {
            var corpus = new List<ComponentRecord>
            {
                new ComponentRecord { ShortId = "c0001", Name = "Number Slider", Category = "Params", SubCategory = "Input", Source = "native" },
                new ComponentRecord { ShortId = "c0002", Name = "Panel", Category = "Params", SubCategory = "Input", Source = "native" },
                new ComponentRecord { ShortId = "c0003", Name = "Circle", Category = "Curve", SubCategory = "Primitive", Source = "native" },
                new ComponentRecord { ShortId = "c0004", Name = "Extrude", Category = "Surface", SubCategory = "Freeform", Source = "native" },
                new ComponentRecord { ShortId = "c0005", Name = "Python 3 Script", Category = "Maths", SubCategory = "Script", Source = "native" },
                new ComponentRecord { ShortId = "c0100", Name = "Diamond Panels", Category = "LunchBox", SubCategory = "Panels", Source = "plugin", LibraryName = "LunchBox", Description = "Create a diamond paneling grid on a surface" },
                new ComponentRecord { ShortId = "c0101", Name = "Catmull-Clark Subdivision", Category = "Weaverbird", SubCategory = "Mesh", Source = "plugin", LibraryName = "Weaverbird", Description = "Subdivide a mesh using Catmull-Clark" },
                new ComponentRecord { ShortId = "c0200", Name = "Add Wall (Curve)", Category = "Revit", SubCategory = "Wall", Source = "plugin", LibraryName = "RhinoInside.Revit.GH", Description = "Given a curve, it adds a Wall element to the active Revit document" },
                new ComponentRecord { ShortId = "c0201", Name = "Add Floor", Category = "Revit", SubCategory = "Host", Source = "plugin", LibraryName = "RhinoInside.Revit.GH", Description = "Given its outline boundary, it adds a Floor element to the active Revit document" },
                new ComponentRecord { ShortId = "c0202", Name = "Query Levels", Category = "Revit", SubCategory = "Model", Source = "plugin", LibraryName = "RhinoInside.Revit.GH", Description = "Get Revit document levels" },
                new ComponentRecord { ShortId = "c0203", Name = "Add DirectShape Geometry", Category = "Revit", SubCategory = "DirectShape", Source = "plugin", LibraryName = "RhinoInside.Revit.GH", Description = "Add geometry as a DirectShape element in Revit" },
                new ComponentRecord { ShortId = "c0204", Name = "Element Parameter", Category = "Revit", SubCategory = "Parameter", Source = "plugin", LibraryName = "RhinoInside.Revit.GH", Description = "Get or set Revit element parameter values" }
            };

            IReadOnlyList<ComponentRecord> revitResults = Retriever.Retrieve(corpus, "create Revit walls from curves on Level 1", 8);
            Assert.Contains(revitResults, c => c.Name == "Add Wall (Curve)");
            Assert.Contains(revitResults, c => c.Name == "Query Levels");
            Assert.Equal("Add Wall (Curve)", revitResults[0].Name);

            IReadOnlyList<ComponentRecord> pluginResults = Retriever.Retrieve(corpus, "create diamond diagrid panels on a surface", 6);
            Assert.Equal("Diamond Panels", pluginResults[0].Name);

            IReadOnlyList<ComponentRecord> meshResults = Retriever.Retrieve(corpus, "smooth subdivide mesh catmull clark", 6);
            Assert.Equal("Catmull-Clark Subdivision", meshResults[0].Name);

            var store = new IndexStore(corpus, "test-key");
            IReadOnlyList<string> plugins = store.GetInstalledPluginNames();
            Assert.Contains("LunchBox", plugins);
            Assert.Contains("Weaverbird", plugins);
            Assert.Contains("Rhino.Inside.Revit (Revit)", plugins);
        }

        [Theory]
        [InlineData("create Revit walls along my curves on Level 1", false, CopilotWorkMode.Grasshopper)]
        [InlineData("query Revit elements and update their parameters", false, CopilotWorkMode.Grasshopper)]
        [InlineData("add DirectShape brep geometry in Rhino.Inside.Revit", false, CopilotWorkMode.Grasshopper)]
        [InlineData("panel this surface using LunchBox diamond panels", false, CopilotWorkMode.Grasshopper)]
        [InlineData("subdivide this mesh with Weaverbird", false, CopilotWorkMode.Grasshopper)]
        [InlineData("tween two curves using Pufferfish", false, CopilotWorkMode.Grasshopper)]
        [InlineData("create layer Walls and paint selected red", true, CopilotWorkMode.RhinoDirect)]
        public void ResolveEffectiveMode_RoutesRevitAndPluginPromptsToGrasshopperInAutoMode(
            string prompt,
            bool isGhOpen,
            CopilotWorkMode expected)
        {
            CopilotWorkMode actual = RhinoPromptBuilder.ResolveEffectiveMode(CopilotWorkMode.Auto, prompt, isGhOpen, useRhinoSelection: false);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void PlanFirstSystem_IncludesInstalledPluginsAndShortlist_AndEnforcesComponentFirstOverPython()
        {
            var candidates = new List<ComponentRecord>
            {
                new ComponentRecord { ShortId = "c0200", Name = "Add Wall (Curve)", Category = "Revit", SubCategory = "Wall", Source = "plugin", LibraryName = "RhinoInside.Revit.GH" },
                new ComponentRecord { ShortId = "c0100", Name = "Diamond Panels", Category = "LunchBox", SubCategory = "Panels", Source = "plugin", LibraryName = "LunchBox" }
            };
            var plugins = new[] { "LunchBox", "Rhino.Inside.Revit (Revit)" };

            string planSystem = ChatPromptBuilder.BuildPlanAndClarifySystem(CopilotWorkMode.Grasshopper, candidates, plugins);
            Assert.Contains("COMPONENT-FIRST RULE", planSystem);
            Assert.Contains("Do NOT propose writing a Python 3 script", planSystem);
            Assert.Contains("INSTALLED GRASSHOPPER PLUGINS / LIBRARIES: LunchBox, Rhino.Inside.Revit (Revit)", planSystem);
            Assert.Contains("AVAILABLE NATIVE / PLUGIN / REVIT COMPONENTS MATCHING THIS REQUEST:", planSystem);
            Assert.Contains("Add Wall (Curve)", planSystem);
            Assert.Contains("Diamond Panels", planSystem);

            string sysInstruction = PromptBuilder.BuildSystemInstruction();
            Assert.Contains("COMPONENT-FIRST PRIORITY", sysInstruction);
            Assert.Contains("AVOID UNNECESSARY PYTHON SCRIPTS", sysInstruction);
        }

        [Fact]
        public void SchemaBuilder_OmitsScriptBlock_UnlessIncludeScriptIsTrue()
        {
            var shortlist = new List<ComponentRecord>
            {
                new ComponentRecord { ShortId = "c001", Name = "Number Slider" },
                new ComponentRecord { ShortId = "c002", Name = "Panel" }
            };

            JsonElement nativeOnly = SchemaBuilder.BuildResponseJsonSchema(shortlist, includeScript: false);
            JsonElement propsNoScript = nativeOnly.GetProperty("properties");
            Assert.True(propsNoScript.TryGetProperty("nodes", out _));
            Assert.True(propsNoScript.TryGetProperty("wires", out _));
            Assert.False(propsNoScript.TryGetProperty("script", out _));

            JsonElement withScript = SchemaBuilder.BuildResponseJsonSchema(shortlist, includeScript: true);
            Assert.True(withScript.GetProperty("properties").TryGetProperty("script", out _));

            Assert.False(ChatPromptBuilder.ExplicitlyRequestsPythonScript("create a diagrid facade with sliders"));
            Assert.True(ChatPromptBuilder.ExplicitlyRequestsPythonScript("write a Python 3 script for a recursive fractal"));
        }

        [Fact]
        public void GraphPatch_SanitizeLlmPatchJson_CleansNullsEmptySettingsAndSliderBounds_AndSchemaIncludesShortlistEnum()
        {
            var shortlist = new List<ComponentRecord>
            {
                new ComponentRecord
                {
                    ShortId = "c_slider",
                    Name = "Number Slider",
                    IsStandaloneParam = true,
                    Outputs = new List<ParamRecord> { new ParamRecord { Name = "Number", NickName = "N", TypeName = "Number", Access = "item" } }
                },
                new ComponentRecord
                {
                    ShortId = "c_panel",
                    Name = "Panel",
                    IsStandaloneParam = true,
                    Inputs = new List<ParamRecord> { new ParamRecord { Name = "Panel", NickName = "Panel", TypeName = "Text", Access = "item" } },
                    Outputs = new List<ParamRecord> { new ParamRecord { Name = "Panel", NickName = "Panel", TypeName = "Text", Access = "item" } }
                },
                new ComponentRecord
                {
                    ShortId = "c_crv",
                    Name = "Curve",
                    Category = "Params",
                    SubCategory = "Geometry",
                    IsStandaloneParam = true,
                    Inputs = new List<ParamRecord> { new ParamRecord { Name = "Curve", NickName = "Crv", TypeName = "Curve", Access = "item" } },
                    Outputs = new List<ParamRecord> { new ParamRecord { Name = "Curve", NickName = "Crv", TypeName = "Curve", Access = "item" } }
                },
                new ComponentRecord
                {
                    ShortId = "c_circle",
                    Name = "Circle",
                    Inputs = new List<ParamRecord>
                    {
                        new ParamRecord { Name = "Plane", NickName = "P", TypeName = "Plane", Access = "item", Optional = true },
                        new ParamRecord { Name = "Radius", NickName = "R", TypeName = "Number", Access = "item" }
                    },
                    Outputs = new List<ParamRecord> { new ParamRecord { Name = "Circle", NickName = "C", TypeName = "Circle", Access = "item" } }
                }
            };

            JsonElement patchSchema = GraphPatchSchema.Build(shortlist);
            JsonElement refEnum = patchSchema.GetProperty("properties").GetProperty("ops").GetProperty("items")
                .GetProperty("properties").GetProperty("node").GetProperty("properties").GetProperty("ref").GetProperty("enum");
            Assert.Equal(4, refEnum.GetArrayLength());

            string noisyLlmJson = """
                {
                  "ops": [
                    {
                      "op": "add_node",
                      "nodeId": null,
                      "node": {
                        "id": "n1",
                        "ref": "c_slider",
                        "x": 120,
                        "y": 150,
                        "settings": { "value": 250, "text": null, "nickname": "Height" }
                      },
                      "settings": null,
                      "wire": null
                    },
                    {
                      "op": "add_node",
                      "node": {
                        "id": "n2",
                        "ref": "c_panel",
                        "x": 120,
                        "y": 240,
                        "settings": { "text": "18.5", "min": null, "nickname": "" }
                      }
                    },
                    {
                      "op": "add_node",
                      "node": {
                        "id": "n3",
                        "ref": "c_crv",
                        "x": 120,
                        "y": 330,
                        "settings": {}
                      }
                    },
                    {
                      "op": "add_node",
                      "node": {
                        "id": "n4",
                        "ref": "c_circle",
                        "x": 360,
                        "y": 240,
                        "settings": {}
                      }
                    },
                    {
                      "op": "wire",
                      "wire": { "fromNode": "n2", "fromParam": "Text", "toNode": "n4", "toParam": "Radius", "targetMapping": null }
                    }
                  ],
                  "explanation": "Added slider, panel input, curve selection input, and circle."
                }
                """;

            string sanitized = GraphPatch.SanitizeLlmPatchJson(noisyLlmJson);
            GraphPatch? parsed = JsonSerializer.Deserialize<GraphPatch>(sanitized);
            Assert.NotNull(parsed);
            PatchValidationResult validation = GraphPatchValidator.Validate(parsed!, new CanvasSnapshot(), shortlist);
            Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
        }

        [Fact]
        public void PanelInput_SelectionInputs_And200NodeLimit_ValidateAndRetrieveCleanly()
        {
            Assert.Equal(200, GraphValidator.MaxNodeCount);
            Assert.True(TypeCompat.AreCompatible("Text", "Number"));
            Assert.True(TypeCompat.AreCompatible("Text", "Integer"));
            Assert.True(TypeCompat.AreCompatible("Text", "Point"));
            Assert.True(TypeCompat.AreCompatible("Text", "Boolean"));
            Assert.True(TypeCompat.AreCompatible("Curve", "Surface"));
            Assert.True(ChatPromptBuilder.RequestsRhinoSelectionInPrompt("use my selected Rhino curve as input"));
            Assert.True(ChatPromptBuilder.RequestsRhinoSelectionInPrompt("place a brep input and a slider"));
            Assert.False(ChatPromptBuilder.ShouldPlanBeforeBuilding("place 2 sliders and a panel input", new ChatSession(), planFirstEnabled: true));

            // Build a large 110-component corpus where 95 facade/plugin items all match the query,
            // ensuring Number Slider, Panel, and Curve/Brep/Geometry selection params are STILL preserved!
            var corpus = new List<ComponentRecord>
            {
                new ComponentRecord { ShortId = "c0001", Name = "Number Slider", Category = "Params", SubCategory = "Input", IsStandaloneParam = true },
                new ComponentRecord { ShortId = "c0002", Name = "Panel", Category = "Params", SubCategory = "Input", IsStandaloneParam = true },
                new ComponentRecord { ShortId = "c0003", Name = "Geometry", Category = "Params", SubCategory = "Geometry", IsStandaloneParam = true },
                new ComponentRecord { ShortId = "c0004", Name = "Curve", Category = "Params", SubCategory = "Geometry", IsStandaloneParam = true },
                new ComponentRecord { ShortId = "c0005", Name = "Brep", Category = "Params", SubCategory = "Geometry", IsStandaloneParam = true }
            };
            for (int i = 0; i < 95; i++)
            {
                corpus.Add(new ComponentRecord
                {
                    ShortId = $"p{i:D4}",
                    Name = $"Diagrid Tower Truss Tool {i}",
                    Category = "PluginTools",
                    SubCategory = "Facade",
                    Source = "plugin",
                    LibraryName = "PluginTools",
                    Description = "Parametric tower diagrid facade structure"
                });
            }

            IReadOnlyList<ComponentRecord> retrieved = Retriever.Retrieve(corpus, "parametric tower diagrid facade structure", maxResults: 80);
            Assert.Equal(80, retrieved.Count);
            Assert.Contains(retrieved, c => c.Name == "Number Slider");
            Assert.Contains(retrieved, c => c.Name == "Panel");
            Assert.Contains(retrieved, c => c.Name == "Geometry" && c.IsStandaloneParam);
            Assert.Contains(retrieved, c => c.Name == "Curve" && c.IsStandaloneParam);
            Assert.Contains(retrieved, c => c.Name == "Brep" && c.IsStandaloneParam);
        }

        [Fact]
        public void RhinoActionPlanValidator_AcceptsAllAdvancedArchitectural3DOperations_AndSupports250Ops()
        {
            Assert.Equal(250, RhinoActionPlanValidator.MaxOpsPerTurn);

            var plan = new RhinoActionPlan
            {
                Explanation = "Twisted tapered tower with piped helix diagrid, revolved dome, boolean atrium cutout, and floor slabs.",
                Ops = new List<RhinoActionOp>
                {
                    new RhinoActionOp { Op = "add_polygon", Center = new[] { 0.0, 0.0, 0.0 }, Radius = 14.0, Sides = 8, Inscribed = true, Name = "BaseOctagon" },
                    new RhinoActionOp { Op = "extrude", Target = "name:BaseOctagon", Height = 60.0, Capped = true, Name = "TowerMass" },
                    new RhinoActionOp { Op = "twist", Target = "name:TowerMass", AngleDegrees = 75.0 },
                    new RhinoActionOp { Op = "taper", Target = "name:TowerMass", StartRadius = 14.0, EndRadius = 9.0 },
                    new RhinoActionOp { Op = "add_cylinder", Origin = new[] { 0.0, -14.0, 0.0 }, Radius = 5.0, Height = 12.0, Name = "AtriumCutter" },
                    new RhinoActionOp { Op = "boolean_difference", Target = "name:TowerMass", SubtractTarget = "name:AtriumCutter", Name = "CarvedTower" },
                    new RhinoActionOp { Op = "fillet_edges", Target = "name:CarvedTower", Radius = 0.25 },
                    new RhinoActionOp { Op = "contour_slice", Target = "name:CarvedTower", Distance = 4.0, ExtrudeThickness = 0.3, Layer = "Floors" },
                    new RhinoActionOp { Op = "add_helix", Origin = new[] { 0.0, 0.0, 0.0 }, Radius = 14.2, Radius2 = 9.2, Height = 60.0, Turns = 2.5, Name = "HelixRail" },
                    new RhinoActionOp { Op = "pipe", Target = "name:HelixRail", Radius = 0.35, Radius2 = 0.2, CapMode = "round", Layer = "Diagrid" },
                    new RhinoActionOp { Op = "mirror", Target = "last_created", Normal = new[] { 1.0, 0.0, 0.0 }, Copy = true },
                    new RhinoActionOp { Op = "add_arc", Center = new[] { 0.0, 0.0, 60.0 }, Radius = 9.0, AngleDegrees = 90.0, Normal = new[] { 0.0, 1.0, 0.0 }, Name = "DomeProfile" },
                    new RhinoActionOp { Op = "revolve", Target = "name:DomeProfile", Origin = new[] { 0.0, 0.0, 60.0 }, Axis = new[] { 0.0, 0.0, 1.0 }, AngleDegrees = 360.0, Capped = false, Name = "DomeSrf" },
                    new RhinoActionOp { Op = "offset_surface", Target = "name:DomeSrf", Distance = 0.25, Solid = true },
                    new RhinoActionOp { Op = "add_torus", Center = new[] { 0.0, 0.0, 60.0 }, Radius = 9.2, TubeRadius = 0.4 },
                    new RhinoActionOp { Op = "add_ellipse", Center = new[] { 30.0, 0.0, 0.0 }, RadiusX = 12.0, RadiusY = 7.0, Name = "PlazaOuter" },
                    new RhinoActionOp { Op = "add_circle", Center = new[] { 30.0, 0.0, 0.0 }, Radius = 3.0, Name = "PlazaPond" },
                    new RhinoActionOp { Op = "planar_srf", Target = "name:PlazaOuter|PlazaPond", Layer = "Landscape" },
                    new RhinoActionOp { Op = "zoom", Scope = "extents" }
                }
            };

            RhinoActionValidationResult validation = RhinoActionPlanValidator.Validate(plan);
            Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
        }

        private sealed class StubHttpHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

            public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            {
                _responder = responder;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(_responder(request));
        }
    }
}
