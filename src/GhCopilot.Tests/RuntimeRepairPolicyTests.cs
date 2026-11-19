using System;
using GhCopilot.Core.Graph;
using Xunit;

namespace GhCopilot.Tests
{
    public sealed class RuntimeRepairPolicyTests
    {
        [Theory]
        [InlineData(false, false, false, false)]
        [InlineData(false, false, true, true)]
        [InlineData(false, true, false, false)]
        [InlineData(false, true, true, false)]
        [InlineData(true, false, false, false)]
        [InlineData(true, false, true, false)]
        [InlineData(true, true, false, false)]
        [InlineData(true, true, true, false)]
        public void ShouldRepair_RequiresValidCompleteInputsAndRuntimeError(
            bool missing, bool invalid, bool error, bool expected)
        {
            Assert.Equal(expected, RuntimeRepairPolicy.ShouldRepair(
                missing ? new[] { "Curve is empty." } : Array.Empty<string>(),
                invalid ? new[] { "Curve is invalid." } : Array.Empty<string>(),
                error ? new[] { "[Error] Operation failed." } : Array.Empty<string>()));
        }

        [Theory]
        [InlineData("[Error]", true)]
        [InlineData("[Error] Component: Operation failed.", true)]
        [InlineData("[Warning] Operation failed.", false)]
        [InlineData("[Remark] Operation failed.", false)]
        [InlineData("Component: [Error] Operation failed.", false)]
        [InlineData(" [Error] Operation failed.", false)]
        [InlineData("[error] Operation failed.", false)]
        [InlineData("[Errors] Operation failed.", false)]
        [InlineData("Error: Operation failed.", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void ShouldRepair_RecognizesOnlyExactErrorPrefix(string? message, bool expected)
        {
            Assert.Equal(expected, RuntimeRepairPolicy.ShouldRepair(
                Array.Empty<string>(), Array.Empty<string>(), new[] { message! }));
        }

        [Fact]
        public void ShouldRepair_FindsErrorAmongWarningsAndDoesNotMutateInputs()
        {
            string[] messages = { "[Warning] Warning.", "[Error] Failure.", "[Warning] Other warning." };
            string[] original = (string[])messages.Clone();

            Assert.True(RuntimeRepairPolicy.ShouldRepair(Array.Empty<string>(), Array.Empty<string>(), messages));
            Assert.Equal(original, messages);
        }

        [Theory]
        [InlineData(0, "missingInputs")]
        [InlineData(1, "invalidInputs")]
        [InlineData(2, "runtimeMessages")]
        public void ShouldRepair_RejectsNullLists(int nullArgument, string parameterName)
        {
            string[] empty = Array.Empty<string>();
            var exception = Assert.Throws<ArgumentNullException>(() => RuntimeRepairPolicy.ShouldRepair(
                nullArgument == 0 ? null! : empty,
                nullArgument == 1 ? null! : empty,
                nullArgument == 2 ? null! : empty));

            Assert.Equal(parameterName, exception.ParamName);
        }
    }
}