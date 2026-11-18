using System;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Rhino;

namespace GhCopilot.Gh.Canvas
{
    /// <summary>
    /// Marshals Grasshopper canvas and document mutations onto the Rhino UI thread and never during a running solution.
    /// </summary>
    public static class UiThread
    {
        /// <summary>
        /// Executes an action on Rhino's UI thread.
        /// </summary>
        public static void InvokeOnUi(Action action)
        {
            if (action is null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            RhinoApp.InvokeOnUiThread(action);
        }

        /// <summary>
        /// Safely runs a Grasshopper document mutation on the UI thread when no solution is actively executing.
        /// </summary>
        public static void MutateActiveDocument(Action<GH_Document> mutation, Action<string>? onError = null)
        {
            if (mutation is null)
            {
                throw new ArgumentNullException(nameof(mutation));
            }

            InvokeOnUi(() =>
            {
                GH_Document? doc = Instances.ActiveCanvas?.Document;
                if (doc is null)
                {
                    onError?.Invoke("No active Grasshopper document is open.");
                    return;
                }

                if (doc.SolutionState == GH_ProcessStep.PreProcess || doc.SolutionState == GH_ProcessStep.Process)
                {
                    doc.ScheduleSolution(5, scheduledDoc =>
                    {
                        if (scheduledDoc != null)
                        {
                            mutation(scheduledDoc);
                        }
                    });
                    return;
                }

                mutation(doc);
            });
        }

        /// <summary>
        /// Runs a Grasshopper document mutation on the UI thread (deferring with ScheduleSolution if mid-solution)
        /// and awaits its result asynchronously.
        /// </summary>
        public static Task<T> MutateActiveDocumentAsync<T>(Func<GH_Document, T> mutation)
        {
            if (mutation is null)
            {
                throw new ArgumentNullException(nameof(mutation));
            }

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            InvokeOnUi(() =>
            {
                try
                {
                    GH_Document? doc = Instances.ActiveCanvas?.Document;
                    if (doc is null)
                    {
                        tcs.TrySetException(new InvalidOperationException("No active Grasshopper document is open."));
                        return;
                    }

                    if (doc.SolutionState == GH_ProcessStep.PreProcess || doc.SolutionState == GH_ProcessStep.Process)
                    {
                        doc.ScheduleSolution(5, scheduledDoc =>
                        {
                            try
                            {
                                if (scheduledDoc is null)
                                {
                                    tcs.TrySetException(new InvalidOperationException("Scheduled Grasshopper document was null."));
                                    return;
                                }

                                tcs.TrySetResult(mutation(scheduledDoc));
                            }
                            catch (Exception ex)
                            {
                                tcs.TrySetException(ex);
                            }
                        });
                        return;
                    }

                    tcs.TrySetResult(mutation(doc));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            return tcs.Task;
        }
    }
}
