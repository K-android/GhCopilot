using System;
using System.Collections.Generic;
using GhCopilot.Core.Graph;
using GhCopilot.Core.Index;
using Xunit;

namespace GhCopilot.Tests
{
    public sealed class GraphValidatorTests
    {
        private static Dictionary<string, ComponentRecord> CreateSampleShortlist()
        {
            var slider = new ComponentRecord
            {
                ShortId = "c001",
                ComponentGuid = Guid.NewGuid(),
                Name = "Number Slider",
                Category = "Params",
                SubCategory = "Input",
                IsStandaloneParam = true,
                IsHydrated = true,
                Outputs = new List<ParamRecord>
                {
                    new ParamRecord { Name = "Number", NickName = "N", TypeName = "Number", Access = "item" }
                }
            };

            var addition = new ComponentRecord
            {
                ShortId = "c002",
                ComponentGuid = Guid.NewGuid(),
                Name = "Addition",
                Category = "Maths",
                SubCategory = "Operators",
                IsHydrated = true,
                Inputs = new List<ParamRecord>
                {
                    new ParamRecord { Name = "A", NickName = "A", TypeName = "Number", Access = "item" },
                    new ParamRecord { Name = "B", NickName = "B", TypeName = "Number", Access = "item" }
                },
                Outputs = new List<ParamRecord>
                {
                    new ParamRecord { Name = "Result", NickName = "R", TypeName = "Number", Access = "item" }
                }
            };

            var panel = new ComponentRecord
            {
                ShortId = "c003",
                ComponentGuid = Guid.NewGuid(),
                Name = "Panel",
                Category = "Params",
                SubCategory = "Input",
                IsStandaloneParam = true,
                IsHydrated = true,
                Inputs = new List<ParamRecord>
                {
                    new ParamRecord { Name = "Panel", NickName = "Panel", TypeName = "Generic", Access = "list" }
                },
                Outputs = new List<ParamRecord>
                {
                    new ParamRecord { Name = "Panel", NickName = "Panel", TypeName = "Generic", Access = "list" }
                }
            };

            var circle = new ComponentRecord
            {
                ShortId = "c004",
                ComponentGuid = Guid.NewGuid(),
                Name = "Circle",
                Category = "Curve",
                SubCategory = "Primitive",
                IsHydrated = true,
                Inputs = new List<ParamRecord>
                {
                    new ParamRecord { Name = "Plane", NickName = "P", TypeName = "Plane", Access = "item" },
                    new ParamRecord { Name = "Radius", NickName = "R", TypeName = "Number", Access = "item" }
                },
                Outputs = new List<ParamRecord>
                {
                    new ParamRecord { Name = "Circle", NickName = "C", TypeName = "Circle", Access = "item" }
                }
            };

            var extrude = new ComponentRecord
            {
                ShortId = "c005",
                ComponentGuid = Guid.NewGuid(),
                Name = "Extrude",
                Category = "Surface",
                SubCategory = "Freeform",
                IsHydrated = true,
                Inputs = new List<ParamRecord>
                {
                    new ParamRecord { Name = "Base", NickName = "B", TypeName = "Geometry", Access = "item" },
                    new ParamRecord { Name = "Direction", NickName = "D", TypeName = "Vector", Access = "item" }
                },
                Outputs = new List<ParamRecord>
                {
                    new ParamRecord { Name = "Extrusion", NickName = "E", TypeName = "Brep", Access = "item" }
                }
            };

            var series = new ComponentRecord
            {
                ShortId = "c006",
                ComponentGuid = Guid.NewGuid(),
                Name = "Series",
                Category = "Sets",
                SubCategory = "Sequence",
                IsHydrated = true,
                Inputs = new List<ParamRecord>
                {
                    new ParamRecord { Name = "Start", NickName = "S", TypeName = "Number", Access = "item" },
                    new ParamRecord { Name = "Step", NickName = "N", TypeName = "Number", Access = "item" },
                    new ParamRecord { Name = "Count", NickName = "C", TypeName = "Integer", Access = "item" }
                },
                Outputs = new List<ParamRecord>
                {
                    new ParamRecord { Name = "Series", NickName = "S", TypeName = "Number", Access = "list" }
                }
            };

            return new Dictionary<string, ComponentRecord>(StringComparer.OrdinalIgnoreCase)
            {
                [slider.ShortId] = slider,
                [addition.ShortId] = addition,
                [panel.ShortId] = panel,
                [circle.ShortId] = circle,
                [extrude.ShortId] = extrude,
                [series.ShortId] = series
            };
        }

        [Fact]
        public void ValidGraph_PassesWithZeroErrors()
        {
            var shortlist = CreateSampleShortlist();
            var spec = new GraphSpec
            {
                Nodes = new List<NodeSpec>
                {
                    new NodeSpec { Id = "n1", Ref = "c001", X = 100, Y = 100 },
                    new NodeSpec { Id = "n2", Ref = "c001", X = 100, Y = 160 },
                    new NodeSpec { Id = "n3", Ref = "c002", X = 260, Y = 130 },
                    new NodeSpec { Id = "n4", Ref = "c004", X = 420, Y = 130 }
                },
                Wires = new List<WireSpec>
                {
                    new WireSpec { FromNode = "n1", FromParam = "", ToNode = "n3", ToParam = "A" },
                    new WireSpec { FromNode = "n2", FromParam = "", ToNode = "n3", ToParam = "B" },
                    new WireSpec { FromNode = "n3", FromParam = "Result", ToNode = "n4", ToParam = "Radius" }
                }
            };

            ValidationResult result = GraphValidator.Validate(spec, shortlist);

            Assert.True(result.IsValid);
            Assert.Empty(result.Errors);
            Assert.Empty(result.Warnings);
        }

        [Fact]
        public void UnknownRef_ReturnsUnknownComponentRefError()
        {
            var shortlist = CreateSampleShortlist();
            var spec = new GraphSpec
            {
                Nodes = new List<NodeSpec>
                {
                    new NodeSpec { Id = "n1", Ref = "c999", X = 100, Y = 100 }
                }
            };

            ValidationResult result = GraphValidator.Validate(spec, shortlist);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Code == ValidationErrorCode.UnknownComponentRef && e.NodeId == "n1");
        }

        [Fact]
        public void BadParamName_ReturnsUnknownParameterError()
        {
            var shortlist = CreateSampleShortlist();
            var spec = new GraphSpec
            {
                Nodes = new List<NodeSpec>
                {
                    new NodeSpec { Id = "n1", Ref = "c001", X = 100, Y = 100 },
                    new NodeSpec { Id = "n2", Ref = "c002", X = 250, Y = 100 }
                },
                Wires = new List<WireSpec>
                {
                    new WireSpec { FromNode = "n1", FromParam = "Number", ToNode = "n2", ToParam = "NonExistentInput" }
                }
            };

            ValidationResult result = GraphValidator.Validate(spec, shortlist);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Code == ValidationErrorCode.UnknownParameter);
        }

        [Fact]
        public void TypeMismatch_ReturnsTypeMismatchError_WhileGenericAndAllowListPass()
        {
            var shortlist = CreateSampleShortlist();

            // Incompatible: Circle -> Number (Addition.A)
            var badSpec = new GraphSpec
            {
                Nodes = new List<NodeSpec>
                {
                    new NodeSpec { Id = "n1", Ref = "c004", X = 100, Y = 100 },
                    new NodeSpec { Id = "n2", Ref = "c002", X = 260, Y = 100 }
                },
                Wires = new List<WireSpec>
                {
                    new WireSpec { FromNode = "n1", FromParam = "Circle", ToNode = "n2", ToParam = "A" }
                }
            };

            ValidationResult badResult = GraphValidator.Validate(badSpec, shortlist);
            Assert.False(badResult.IsValid);
            Assert.Contains(badResult.Errors, e => e.Code == ValidationErrorCode.TypeMismatch);

            // Allow-list + Generic: Circle -> Geometry (Extrude.Base), Extrusion (Brep) -> Generic (Panel)
            var goodSpec = new GraphSpec
            {
                Nodes = new List<NodeSpec>
                {
                    new NodeSpec { Id = "n1", Ref = "c004", X = 100, Y = 100 },
                    new NodeSpec { Id = "n2", Ref = "c005", X = 260, Y = 100 },
                    new NodeSpec { Id = "n3", Ref = "c003", X = 420, Y = 100 }
                },
                Wires = new List<WireSpec>
                {
                    new WireSpec { FromNode = "n1", FromParam = "Circle", ToNode = "n2", ToParam = "Base" },
                    new WireSpec { FromNode = "n2", FromParam = "Extrusion", ToNode = "n3", ToParam = "Panel" }
                }
            };

            ValidationResult goodResult = GraphValidator.Validate(goodSpec, shortlist);
            Assert.True(goodResult.IsValid);
            Assert.Empty(goodResult.Errors);
        }

        [Fact]
        public void DuplicateNodeIds_ReturnsDuplicateNodeIdError()
        {
            var shortlist = CreateSampleShortlist();
            var spec = new GraphSpec
            {
                Nodes = new List<NodeSpec>
                {
                    new NodeSpec { Id = "n1", Ref = "c001", X = 100, Y = 100 },
                    new NodeSpec { Id = "n1", Ref = "c002", X = 260, Y = 100 }
                }
            };

            ValidationResult result = GraphValidator.Validate(spec, shortlist);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Code == ValidationErrorCode.DuplicateNodeId);
        }

        [Fact]
        public void CycleInWires_ReturnsCycleDetectedError()
        {
            var shortlist = CreateSampleShortlist();
            var spec = new GraphSpec
            {
                Nodes = new List<NodeSpec>
                {
                    new NodeSpec { Id = "n1", Ref = "c002", X = 100, Y = 100 },
                    new NodeSpec { Id = "n2", Ref = "c002", X = 260, Y = 100 }
                },
                Wires = new List<WireSpec>
                {
                    new WireSpec { FromNode = "n1", FromParam = "Result", ToNode = "n2", ToParam = "A" },
                    new WireSpec { FromNode = "n2", FromParam = "Result", ToNode = "n1", ToParam = "A" }
                }
            };

            ValidationResult result = GraphValidator.Validate(spec, shortlist);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Code == ValidationErrorCode.CycleDetected);
        }

        [Fact]
        public void SelfLoopInWires_ReturnsCycleDetectedError()
        {
            var shortlist = CreateSampleShortlist();
            var spec = new GraphSpec
            {
                Nodes = new List<NodeSpec>
                {
                    new NodeSpec { Id = "n1", Ref = "c002", X = 100, Y = 100 }
                },
                Wires = new List<WireSpec>
                {
                    new WireSpec { FromNode = "n1", FromParam = "Result", ToNode = "n1", ToParam = "A" }
                }
            };

            ValidationResult result = GraphValidator.Validate(spec, shortlist);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Code == ValidationErrorCode.CycleDetected);
        }

        [Fact]
        public void WireDirectionErrors_OutOfInputOrIntoOutput_ReturnsWireDirectionError()
        {
            var shortlist = CreateSampleShortlist();
            var spec = new GraphSpec
            {
                Nodes = new List<NodeSpec>
                {
                    new NodeSpec { Id = "n1", Ref = "c002", X = 100, Y = 100 },
                    new NodeSpec { Id = "n2", Ref = "c001", X = 260, Y = 100 },
                    new NodeSpec { Id = "n3", Ref = "c002", X = 420, Y = 100 }
                },
                Wires = new List<WireSpec>
                {
                    // Wiring out of input 'A' into slider (which has 0 inputs) -> both directions invalid
                    new WireSpec { FromNode = "n1", FromParam = "A", ToNode = "n2", ToParam = "" },
                    // Wiring into output 'Result' on Addition
                    new WireSpec { FromNode = "n2", FromParam = "Number", ToNode = "n3", ToParam = "Result" }
                }
            };

            ValidationResult result = GraphValidator.Validate(spec, shortlist);

            Assert.False(result.IsValid);
            Assert.Equal(3, result.Errors.FindAll(e => e.Code == ValidationErrorCode.WireDirectionError).Count);
        }

        [Fact]
        public void AccessMismatch_ProducesWarningNotError()
        {
            var shortlist = CreateSampleShortlist();
            var spec = new GraphSpec
            {
                Nodes = new List<NodeSpec>
                {
                    new NodeSpec { Id = "n1", Ref = "c006", X = 100, Y = 100 },
                    new NodeSpec { Id = "n2", Ref = "c002", X = 260, Y = 100 }
                },
                Wires = new List<WireSpec>
                {
                    // Series outputs Number(list) -> Addition.A expects Number(item)
                    new WireSpec { FromNode = "n1", FromParam = "Series", ToNode = "n2", ToParam = "A" }
                }
            };

            ValidationResult result = GraphValidator.Validate(spec, shortlist);

            Assert.True(result.IsValid);
            Assert.Empty(result.Errors);
            Assert.Single(result.Warnings);
            Assert.Equal(ValidationErrorCode.AccessMismatch, result.Warnings[0].Code);
        }

        [Fact]
        public void TooManyNodesAndCoordinateOutOfRange_ReturnErrors()
        {
            var shortlist = CreateSampleShortlist();
            var spec = new GraphSpec();
            for (int i = 0; i < GraphValidator.MaxNodeCount + 1; i++)
            {
                spec.Nodes.Add(new NodeSpec
                {
                    Id = $"n{i}",
                    Ref = "c001",
                    X = i == 0 ? 50000f : i * 20f,
                    Y = 100f
                });
            }

            ValidationResult result = GraphValidator.Validate(spec, shortlist);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Code == ValidationErrorCode.TooManyNodes);
            Assert.Contains(result.Errors, e => e.Code == ValidationErrorCode.CoordinateOutOfRange);
        }
    }
}
