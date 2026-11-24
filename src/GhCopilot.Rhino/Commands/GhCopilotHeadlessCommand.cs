using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GhCopilot.Core.Host;
using GhCopilot.Rhino.Host;
using Rhino;
using Rhino.Commands;
using Rhino.Input;

namespace GhCopilot.Rhino.Commands
{
    /// <summary>
    /// Headless Rhino command entry point (<c>GhCopilotRun</c>) that runs a prompt and prints JSON output.
    /// Works in both standalone Rhino 8 and Rhino.Inside.Revit.
    /// </summary>
    public sealed class GhCopilotHeadlessCommand : Command
    {
        public GhCopilotHeadlessCommand()
        {
            Instance = this;
        }

        public static GhCopilotHeadlessCommand? Instance { get; private set; }

        public override string EnglishName => "GhCopilotRun";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            string prompt = string.Empty;
            Result getResult = RhinoGet.GetString("Prompt for GH Copilot", false, ref prompt);
            if (getResult != Result.Success || string.IsNullOrWhiteSpace(prompt))
            {
                return getResult == Result.Cancel ? Result.Cancel : Result.Failure;
            }

            var request = new HeadlessPromptRequest
            {
                Prompt = prompt.Trim(),
                Mode = "auto",
                SkipPlanFirst = true,
                CaptureViewport = false
            };

            Task<HeadlessPromptResponse> task = Task.Run(() => HeadlessBridgeServer.ExecuteRequestAsync(request, CancellationToken.None));
            while (!task.IsCompleted)
            {
                RhinoApp.Wait();
                Thread.Sleep(20);
            }

            HeadlessPromptResponse response = task.GetAwaiter().GetResult();
            string json = JsonSerializer.Serialize(response);
            RhinoApp.WriteLine(json);

            return string.Equals(response.Status, "ok", StringComparison.OrdinalIgnoreCase)
                ? Result.Success
                : Result.Failure;
        }
    }
}
