using System;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.GUI.Canvas;
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
        /// Returns the active Grasshopper document, automatically creating and attaching a new blank
        /// document onto the active canvas when Grasshopper has an initialized DocumentServer but no file open yet.
        /// Must be called on the Rhino UI thread.
        /// </summary>
        public static GH_Document? EnsureActiveDocument()
        {
            if (Instances.ActiveCanvas == null)
            {
                _ = Instances.DocumentEditor;
            }

            GH_Canvas? canvas = Instances.ActiveCanvas;
            if (canvas?.Document != null)
            {
                return canvas.Document;
            }

            GH_DocumentServer? server = Instances.DocumentServer;
            if (server == null)
            {
                return null;
            }

            GH_Document? doc = server.DocumentCount > 0 ? server[0] : server.AddNewDocument();
            if (doc != null && canvas != null && canvas.Document == null)
            {
                canvas.Document = doc;
                canvas.Refresh();
            }

            return canvas?.Document ?? doc;
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
                GH_Document? doc = EnsureActiveDocument();
                if (doc is null)
                {
                    onError?.Invoke("No active Grasshopper document is open (open Grasshopper first).");
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
                    GH_Document? doc = EnsureActiveDocument();
                    if (doc is null)
                    {
                        tcs.TrySetException(new InvalidOperationException("No active Grasshopper document is open (open Grasshopper first)."));
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
