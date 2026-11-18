using System.Collections.Generic;
using System.Text.Json;
using GhCopilot.Core.Graph;
using Xunit;

namespace GhCopilot.Tests
{
    public sealed class GraphSpecTests
    {
        [Fact]
        public void GraphSpec_JsonRoundTrip_PreservesNodesSettingsWiresAndExplanation()
        {
            var original = new GraphSpec
            {
                Explanation = "Adds two sliders and outputs the sum to a panel.",
                Nodes = new List<NodeSpec>
                {
                    new NodeSpec
                    {
                        Id = "n1",
                        Ref = "c001",
                        X = 120.5f,
                        Y = 240.25f,
                        Settings = new NodeSettings
                        {
                            Min = 0.0,
                            Max = 50.0,
                            Value = 12.5,
                            Decimals = 1,
                            Nickname = "Radius"
                        }
                    },
                    new NodeSpec
                    {
                        Id = "n2",
                        Ref = "c002",
                        X = 420f,
                        Y = 240f,
                        Settings = new NodeSettings
                        {
                            Text = "Initial text",
                            Nickname = "Display"
                        }
                    }
                },
                Wires = new List<WireSpec>
                {
                    new WireSpec
                    {
                        FromNode = "n1",
                        FromParam = string.Empty,
                        ToNode = "n2",
                        ToParam = "0"
                    }
                },
                Script = null
            };

            string json = JsonSerializer.Serialize(original);
            Assert.Contains("\"fromNode\":\"n1\"", json);
            Assert.Contains("\"decimals\":1", json);

            GraphSpec? roundTripped = JsonSerializer.Deserialize<GraphSpec>(json);
            Assert.NotNull(roundTripped);
            Assert.Equal(original.Explanation, roundTripped!.Explanation);
            Assert.Null(roundTripped.Script);

            Assert.Equal(2, roundTripped.Nodes.Count);
            Assert.Equal("n1", roundTripped.Nodes[0].Id);
            Assert.Equal("c001", roundTripped.Nodes[0].Ref);
            Assert.Equal(120.5f, roundTripped.Nodes[0].X);
            Assert.Equal(240.25f, roundTripped.Nodes[0].Y);
            Assert.NotNull(roundTripped.Nodes[0].Settings);
            Assert.Equal(12.5, roundTripped.Nodes[0].Settings!.Value);
            Assert.Equal(1, roundTripped.Nodes[0].Settings!.Decimals);
            Assert.Equal("Radius", roundTripped.Nodes[0].Settings!.Nickname);

            Assert.Equal("Initial text", roundTripped.Nodes[1].Settings!.Text);

            Assert.Single(roundTripped.Wires);
            Assert.Equal("n1", roundTripped.Wires[0].FromNode);
            Assert.Equal("n2", roundTripped.Wires[0].ToNode);
            Assert.Equal("0", roundTripped.Wires[0].ToParam);
        }

        [Fact]
        public void GraphSpec_WithScriptBlock_DeserializesExpectedShape()
        {
            const string json = """
            {
              "nodes": [],
              "wires": [],
              "script": {
                "code": "a = x * 2",
                "inputs": [
                  { "name": "x", "type": "float", "access": "item" }
                ],
                "outputs": [
                  { "name": "a", "type": "float" }
                ]
              },
              "explanation": "Doubles input x."
            }
            """;

            GraphSpec? spec = JsonSerializer.Deserialize<GraphSpec>(json);

            Assert.NotNull(spec);
            Assert.Empty(spec!.Nodes);
            Assert.Empty(spec.Wires);
            Assert.NotNull(spec.Script);
            Assert.Equal("a = x * 2", spec.Script!.Code);
            Assert.Single(spec.Script.Inputs);
            Assert.Equal("x", spec.Script.Inputs[0].Name);
            Assert.Equal("float", spec.Script.Inputs[0].Type);
            Assert.Equal("item", spec.Script.Inputs[0].Access);
            Assert.Single(spec.Script.Outputs);
            Assert.Equal("a", spec.Script.Outputs[0].Name);
            Assert.Equal("Doubles input x.", spec.Explanation);
        }
    }
}
