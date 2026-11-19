using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GhCopilot.Core.Graph;
using GhCopilot.Core.Index;
using Xunit;

namespace GhCopilot.Tests
{
    public sealed class GraphPatchTests
    {
        private static ParamRecord Port(string name, string type = "Number", string nickname = "") =>
            new ParamRecord { Name = name, NickName = nickname, TypeName = type };

        private static List<ComponentRecord> Shortlist() => new List<ComponentRecord>
        {
            new ComponentRecord { ShortId = "slider", Name = "Number Slider", Outputs = new List<ParamRecord> { Port("Number", nickname: "N") } },
            new ComponentRecord { ShortId = "panel", Name = "Panel", Inputs = new List<ParamRecord> { Port("Text", "Text") }, Outputs = new List<ParamRecord> { Port("Text", "Text") } },
            new ComponentRecord { ShortId = "add", Name = "Addition", Inputs = new List<ParamRecord> { Port("A"), Port("B") }, Outputs = new List<ParamRecord> { Port("Result", nickname: "R") } }
        };

        private static CanvasSnapshot Snapshot() => new CanvasSnapshot
        {
            Nodes = new List<CanvasNodeSnapshot>
            {
                new CanvasNodeSnapshot { Id = "s1", Name = "Number Slider", ShortId = "unindexed",
                    Settings = new NodeSettings { Min = 0, Max = 10, Value = 5, Decimals = 2 },
                    Outputs = new List<ParamRecord> { Port("Number", nickname: "N") } },
                new CanvasNodeSnapshot { Id = "s2", Name = "Addition", Inputs = new List<ParamRecord> { Port("A"), Port("B") },
                    Outputs = new List<ParamRecord> { Port("Result", nickname: "R") } }
            },
            Wires = new List<CanvasWireSnapshot>
            {
                new CanvasWireSnapshot { FromNode = "s1", FromParam = "", ToNode = "s2", ToParam = "A" }
            }
        };

        private static GraphPatchOp Add(string id = "n1", string reference = "add", NodeSettings? settings = null) =>
            new GraphPatchOp { Op = "add_node", Node = new NodeSpec { Id = id, Ref = reference, X = 100, Y = 200, Settings = settings } };

        private static GraphPatchOp Set(string id, NodeSettings settings) => new GraphPatchOp { Op = "set_param", NodeId = id, Settings = settings };

        private static GraphPatchOp Wire(string op = "wire", string from = "s1", string output = "N", string to = "s2", string input = "B") =>
            new GraphPatchOp { Op = op, Wire = new WireSpec { FromNode = from, FromParam = output, ToNode = to, ToParam = input } };

        private static PatchValidationResult Validate(params GraphPatchOp[] ops) =>
            GraphPatchValidator.Validate(new GraphPatch { Ops = ops.ToList() }, Snapshot(), Shortlist());

        private static void Invalid(PatchValidationResult result, string message)
        {
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.IndexOf(message, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void SequentialEdits_SupportAllOperations_WithoutMutatingInputs()
        {
            CanvasSnapshot snapshot = Snapshot();
            var patch = new GraphPatch
            {
                Explanation = "Insert an addition",
                Ops = new List<GraphPatchOp>
                {
                    Add(), Set("n1", new NodeSettings { Nickname = "Sum", FlattenInputs = new List<string> { "A" } }),
                    Wire(to: "n1", input: "A"), Wire("unwire", input: "A"),
                    new GraphPatchOp { Op = "delete_node", NodeId = "s2" },
                    Set("s1", new NodeSettings { Max = 20 }), Set("s1", new NodeSettings { Value = 15 })
                }
            };
            List<ComponentRecord> shortlist = Shortlist();
            string before = JsonSerializer.Serialize(new { snapshot, patch, shortlist });

            PatchValidationResult result = GraphPatchValidator.Validate(patch, snapshot, shortlist);

            Assert.True(result.IsValid, string.Join("; ", result.Errors));
            Assert.Equal(before, JsonSerializer.Serialize(new { snapshot, patch, shortlist }));
            Assert.True(GraphPatchValidator.Validate(patch, snapshot, shortlist).IsValid);
        }

        [Fact]
        public void ExistingUnindexedNodes_RequireNoShortlist()
        {
            var patch = new GraphPatch { Ops = new List<GraphPatchOp> { Set("s1", new NodeSettings { Value = 8 }), Wire() } };
            Assert.True(GraphPatchValidator.Validate(patch, Snapshot(), Array.Empty<ComponentRecord>()).IsValid);
        }

        [Fact]
        public void EmptyPatchAndDeletingEntireGraph_AreValid()
        {
            Assert.True(Validate().IsValid);
            Assert.True(Validate(new GraphPatchOp { Op = "delete_node", NodeId = "s1" },
                new GraphPatchOp { Op = "delete_node", NodeId = "s2" }).IsValid);
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData(" n1")]
        [InlineData("s1")]
        [InlineData(null)]
        public void InvalidOrCollidingIds_AreRejected(string? id) => Invalid(Validate(Add(id!)), "id");

        [Fact]
        public void AddedAndDeletedIds_CannotBeReused()
        {
            Invalid(Validate(Add(), Add()), "unique");
            Invalid(Validate(new GraphPatchOp { Op = "delete_node", NodeId = "s1" }, Add("s1")), "unique");
        }

        [Fact]
        public void UnknownRefsAndUnknownOperations_AreRejected()
        {
            Invalid(Validate(Add(reference: "missing")), "ref");
            Invalid(Validate(new GraphPatchOp { Op = "move" }), "operation");
        }

        [Fact]
        public void Coordinates_ReuseGraphValidatorLimits()
        {
            foreach (float coordinate in new[] { float.NaN, float.PositiveInfinity, GraphValidator.MaxCoordinateMagnitude + 1 })
            {
                GraphPatchOp op = Add();
                op.Node!.X = coordinate;
                Invalid(Validate(op), "coordinates");
            }
        }

        [Fact]
        public void ReferencesMustExistAtThatPointInSequence()
        {
            Invalid(Validate(Set("n1", new NodeSettings { Nickname = "Sum" }), Add()), "unknown node");
            Invalid(Validate(Wire(to: "n1", input: "A"), Add()), "unknown node");
            Invalid(Validate(new GraphPatchOp { Op = "delete_node", NodeId = "s1" }, Wire()), "unknown node");
            Invalid(Validate(new GraphPatchOp { Op = "delete_node", NodeId = "missing" }), "unknown node");
        }

        [Fact]
        public void DeleteRemovesIncidentWires()
        {
            Invalid(Validate(new GraphPatchOp { Op = "delete_node", NodeId = "s1" }, Wire("unwire", input: "A")), "unknown node");
            Assert.True(Validate(new GraphPatchOp { Op = "delete_node", NodeId = "s2" }, Add("n1"), Wire(to: "n1", input: "A")).IsValid);
        }

        [Fact]
        public void SliderSettings_MergeAcrossOperations_AndRejectOutOfRangeValues()
        {
            Assert.True(Validate(Set("s1", new NodeSettings { Max = 20 }), Set("s1", new NodeSettings { Value = 15 })).IsValid);
            Invalid(Validate(Set("s1", new NodeSettings { Value = 15 }), Set("s1", new NodeSettings { Max = 20 })), "within");
            Invalid(Validate(Set("s1", new NodeSettings { Max = 4 })), "within");
            Invalid(Validate(Set("s1", new NodeSettings { Min = 10 })), "less than");
            Invalid(Validate(Set("s1", new NodeSettings { Min = 11 })), "less than");
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(13)]
        public void InvalidSliderDecimals_AreRejected(int decimals) =>
            Invalid(Validate(Set("s1", new NodeSettings { Decimals = decimals })), "decimals");

        [Fact]
        public void NonFiniteAndOverflowingSliderNumbers_AreRejected()
        {
            foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue })
            {
                Invalid(Validate(Set("s1", new NodeSettings { Value = value })), "finite");
                Invalid(Validate(Set("s1", new NodeSettings { Min = value })), "finite");
                Invalid(Validate(Set("s1", new NodeSettings { Max = value })), "finite");
            }
        }

        [Fact]
        public void AddedSlidersUsePersistentDefaults()
        {
            Assert.True(Validate(Add(reference: "slider"), Set("n1", new NodeSettings { Value = 50 })).IsValid);
            Invalid(Validate(Add(reference: "slider"), Set("n1", new NodeSettings { Min = 10 })), "within");
            Invalid(Validate(Add(reference: "slider", settings: new NodeSettings { Min = 3, Max = 2 })), "less than");
        }

        [Fact]
        public void SettingsAreRestrictedToTheirNodeTypes()
        {
            Invalid(Validate(Set("s2", new NodeSettings { Value = 2 })), "only on Number Slider");
            Invalid(Validate(Set("s1", new NodeSettings { Text = "hello" })), "only on Panel");
            Invalid(Validate(Add(settings: new NodeSettings { Min = 0 })), "only on Number Slider");
            Assert.True(Validate(Add(reference: "panel", settings: new NodeSettings { Text = "", Nickname = "Display" })).IsValid);
            Assert.True(Validate(Set("s2", new NodeSettings { Nickname = "Sum" })).IsValid);
            Invalid(Validate(Set("s2", new NodeSettings { Nickname = " " })), "blank");
            Invalid(Validate(Set("s2", new NodeSettings())), "at least one");
        }

        [Fact]
        public void InputModifiers_ValidatePortsAndConflicts_AndAllowClearing()
        {
            Assert.True(Validate(Set("s2", new NodeSettings { FlattenInputs = new List<string> { "A" },
                SimplifyInputs = new List<string> { "0" }, ReverseInputs = new List<string> { "B" } })).IsValid);
            Invalid(Validate(Set("s1", new NodeSettings { FlattenInputs = new List<string> { "Number" } })), "unknown input");
            Invalid(Validate(Set("s2", new NodeSettings { GraftInputs = new List<string> { "Result" } })), "unknown input");
            Invalid(Validate(Set("s2", new NodeSettings { ReverseInputs = new List<string> { "A", "0" } })), "duplicate");
            Invalid(Validate(Set("s2", new NodeSettings { FlattenInputs = new List<string> { "A" } }),
                Set("s2", new NodeSettings { GraftInputs = new List<string> { "0" } })), "both");
            Assert.True(Validate(Set("s2", new NodeSettings { FlattenInputs = new List<string> { "A" } }),
                Set("s2", new NodeSettings { FlattenInputs = new List<string>(), GraftInputs = new List<string> { "A" } })).IsValid);
        }

        [Fact]
        public void DuplicateAndMissingWires_UseCanonicalPortIdentity()
        {
            Invalid(Validate(Wire(input: "0")), "already exists");
            Invalid(Validate(Wire("unwire")), "does not exist");
            Assert.True(Validate(Wire("unwire", input: "0"), Wire(input: "A")).IsValid);
            Invalid(Validate(Wire("unwire", input: "A"), Wire("unwire", input: "0")), "does not exist");
        }

        [Theory]
        [InlineData("missing", "B")]
        [InlineData("N", "Result")]
        [InlineData("N", "")]
        [InlineData("N", "A[999999999999999999999999999]")]
        [InlineData("N", "99")]
        public void InvalidOrWrongDirectionPorts_AreRejected(string output, string input) =>
            Invalid(Validate(Wire(output: output, input: input)), "parameter");

        [Fact]
        public void DuplicateNameOccurrences_AndAliases_AreResolved()
        {
            CanvasSnapshot snapshot = Snapshot();
            snapshot.Nodes[1].Inputs = new List<ParamRecord> { Port("Value", nickname: "V"), Port("Value", nickname: "V") };
            snapshot.Wires.Clear();
            var patch = new GraphPatch { Ops = new List<GraphPatchOp> { Wire(input: "V[1]"), Wire("unwire", input: "1") } };
            Assert.True(GraphPatchValidator.Validate(patch, snapshot, Shortlist()).IsValid);
        }

        [Fact]
        public void WireTypes_UseExistingCompatibilityRules()
        {
            CanvasSnapshot snapshot = Snapshot();
            snapshot.Nodes[0].Outputs[0].TypeName = "Circle";
            var patch = new GraphPatch { Ops = new List<GraphPatchOp> { Wire() } };
            Invalid(GraphPatchValidator.Validate(patch, snapshot, Shortlist()), "incompatible");
            foreach (string target in new[] { "Curve", "Geometry", "Generic", "Text" })
            {
                snapshot.Nodes[1].Inputs[1].TypeName = target;
                Assert.True(GraphPatchValidator.Validate(patch, snapshot, Shortlist()).IsValid);
            }
            patch.Ops = new List<GraphPatchOp> { Wire("unwire", input: "A") };
            Assert.True(GraphPatchValidator.Validate(patch, snapshot, Shortlist()).IsValid);
        }

        [Fact]
        public void CyclesAreRejected_ButCanBeAvoidedByUnwiringFirst()
        {
            Invalid(Validate(Wire(from: "s2", output: "R", input: "B")), "cycle");
            Invalid(Validate(Add(), Wire(from: "s2", output: "R", to: "n1", input: "A"),
                Wire(from: "n1", output: "R")), "cycle");
            Assert.True(Validate(Add(), Wire(from: "s2", output: "R", to: "n1", input: "A"),
                Wire("unwire", from: "s2", output: "R", to: "n1", input: "A"), Wire(from: "n1", output: "R")).IsValid);
        }

        [Fact]
        public void MappingAndExtraneousPayloads_AreRejected()
        {
            GraphPatchOp wire = Wire();
            wire.Wire!.TargetMapping = "invalid";
            Invalid(Validate(wire), "targetMapping");
            GraphPatchOp add = Add();
            add.NodeId = "n1";
            Invalid(Validate(add), "only node");
            Invalid(Validate(new GraphPatchOp { Op = "delete_node", NodeId = "s1", Settings = new NodeSettings() }), "only nodeId");
            Invalid(Validate(new GraphPatchOp { Op = "set_param", NodeId = "s1" }), "settings");
            Invalid(Validate(new GraphPatchOp { Op = "wire" }), "only wire");
        }

        [Fact]
        public void NullClrContracts_AreInvalidRatherThanThrowing()
        {
            Invalid(GraphPatchValidator.Validate(null!, Snapshot(), Shortlist()), "null");
            Invalid(GraphPatchValidator.Validate(new GraphPatch(), null!, Shortlist()), "null");
            Invalid(GraphPatchValidator.Validate(new GraphPatch(), Snapshot(), null!), "null");
            Invalid(GraphPatchValidator.Validate(new GraphPatch { Ops = null! }, Snapshot(), Shortlist()), "null");
            Invalid(GraphPatchValidator.Validate(new GraphPatch { Explanation = null! }, Snapshot(), Shortlist()), "null");
            Invalid(Validate(new GraphPatchOp[] { null! }), "null");
            Invalid(Validate(new GraphPatchOp { Op = null! }), "null");
            GraphPatchOp wire = Wire();
            wire.Wire!.FromParam = null!;
            Invalid(Validate(wire), "null");
            Invalid(Validate(Set("s2", new NodeSettings { ReverseInputs = new List<string> { null! } })), "null");
        }

        [Theory]
        [InlineData("null")]
        [InlineData("{}")]
        [InlineData("{\"ops\":null,\"explanation\":\"\"}")]
        [InlineData("{\"ops\":[null],\"explanation\":\"\"}")]
        [InlineData("{\"ops\":[],\"explanation\":null}")]
        [InlineData("{\"ops\":[{\"op\":null}],\"explanation\":\"\"}")]
        [InlineData("{\"ops\":[{\"op\":\"add_node\",\"node\":{\"id\":\"n1\",\"ref\":\"add\",\"x\":0}}],\"explanation\":\"\"}")]
        [InlineData("{\"ops\":[{\"op\":\"set_param\",\"nodeId\":\"s1\",\"settings\":{\"value\":null,\"nickname\":\"a\"}}],\"explanation\":\"\"}")]
        [InlineData("{\"ops\":[{\"op\":\"wire\",\"wire\":{\"fromNode\":\"s1\",\"fromParam\":null,\"toNode\":\"s2\",\"toParam\":\"A\"}}],\"explanation\":\"\"}")]
        [InlineData("{\"ops\":[],\"explanation\":\"\",\"unexpected\":1}")]
        [InlineData("{\"ops\":[],\"ops\":[],\"explanation\":\"\"}")]
        public void MalformedModelJson_IsRejected(string json) =>
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<GraphPatch>(json));

        [Fact]
        public void JsonRoundTrip_UsesContractNames_AndOmitsUnusedPayloads()
        {
            var patch = new GraphPatch { Ops = new List<GraphPatchOp> { Add(), Set("n1", new NodeSettings { Nickname = "Sum" }), Wire(to: "n1", input: "A") }, Explanation = "Plan" };
            string json = JsonSerializer.Serialize(patch);
            Assert.Contains("\"nodeId\"", json);
            Assert.DoesNotContain(":null", json);
            GraphPatch? restored = JsonSerializer.Deserialize<GraphPatch>(json);
            Assert.NotNull(restored);
            Assert.Equal("Plan", restored!.Explanation);
            Assert.True(GraphPatchValidator.Validate(restored, Snapshot(), Shortlist()).IsValid);
        }

        [Fact]
        public void Schema_DescribesAllOperationsAndSharedPayloads()
        {
            JsonElement schema = GraphPatchSchema.Build();
            Assert.Equal("object", schema.GetProperty("type").GetString());
            Assert.Equal(new[] { "ops", "explanation" }, schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
            JsonElement op = schema.GetProperty("properties").GetProperty("ops").GetProperty("items");
            JsonElement properties = op.GetProperty("properties");
            Assert.Equal(new[] { "add_node", "delete_node", "set_param", "wire", "unwire" }, properties.GetProperty("op").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal(new[] { "op", "nodeId", "node", "settings", "wire" }, properties.EnumerateObject().Select(p => p.Name));
            Assert.True(properties.GetProperty("settings").GetProperty("properties").TryGetProperty("reverseInputs", out _));
            Assert.Equal(4, properties.GetProperty("wire").GetProperty("required").GetArrayLength());
            Assert.False(op.GetProperty("additionalProperties").GetBoolean());
        }
    }
}