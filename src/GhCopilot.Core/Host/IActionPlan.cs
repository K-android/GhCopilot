using System.Collections.Generic;

namespace GhCopilot.Core.Host
{
    /// <summary>
    /// Host-agnostic structured action plan contract implemented by CAD/BIM host action plans.
    /// </summary>
    public interface IActionPlan
    {
        string Explanation { get; }
        int OperationCount { get; }
        bool HasWriteOperations { get; }
    }

    public sealed class ActionPlanValidationResult
    {
        public bool IsValid => Errors.Count == 0;
        public List<string> Errors { get; } = new List<string>();
    }

    public sealed class ActionPlanExecutionResult
    {
        public bool IsSuccess { get; set; }
        public string Summary { get; set; } = string.Empty;
        public int AffectedCount { get; set; }
        public List<string> AffectedIds { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public List<string> Errors { get; } = new List<string>();
    }

    /// <summary>
    /// Host-agnostic execution contract (validate, execute, rollback) implemented by host adapters.
    /// </summary>
    public interface IActionPlanHost<in TPlan> where TPlan : IActionPlan
    {
        ActionPlanValidationResult Validate(TPlan plan);
        ActionPlanExecutionResult Execute(TPlan plan);
        bool Rollback();
    }
}
