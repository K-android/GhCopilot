using System;
using System.Collections.Generic;

namespace GhCopilot.Core.Graph
{
    /// <summary>Separates missing or invalid user data from repairable runtime failures.</summary>
    public static class RuntimeRepairPolicy
    {
        public static bool ShouldRepair(
            IReadOnlyList<string> missingInputs,
            IReadOnlyList<string> invalidInputs,
            IReadOnlyList<string> runtimeMessages)
        {
            if (missingInputs is null) throw new ArgumentNullException(nameof(missingInputs));
            if (invalidInputs is null) throw new ArgumentNullException(nameof(invalidInputs));
            if (runtimeMessages is null) throw new ArgumentNullException(nameof(runtimeMessages));

            if (missingInputs.Count != 0 || invalidInputs.Count != 0) return false;

            foreach (string message in runtimeMessages)
            {
                if (message != null && message.StartsWith("[Error]", StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }
}