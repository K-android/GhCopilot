using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GhCopilot.Core.Llm;
using GhCopilot.Core.Host;
using Rhino;

namespace GhCopilot.Rhino.Host
{
    /// <summary>
    /// Runs an opt-in local named-pipe listener so external tools (or Rhino command scripts)
    /// can trigger GH Copilot prompts headlessly in Rhino 8 or Rhino.Inside.Revit and receive structured JSON.
    /// </summary>
    internal sealed class HeadlessBridgeServer : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private readonly int _pid;
        private readonly string _pipeName;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task? _listenTask;
        private bool _disposed;

        public HeadlessBridgeServer()
        {
            using Process current = Process.GetCurrentProcess();
            _pid = current.Id;
            _pipeName = HeadlessEndpointDiscovery.BuildPipeName(_pid);
        }

        public string PipeName => _pipeName;

        public void Start()
        {
            if (_disposed || _listenTask != null)
            {
                return;
            }

            PublishEndpointInfo();
            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _cts.Cancel();
            }
            catch (Exception)
            {
                // Ignore cancel errors during shutdown.
            }

            HeadlessEndpointDiscovery.RemoveEndpoint(_pid);
            _cts.Dispose();
        }

        public static string DetectHostType()
        {
            try
            {
                using Process current = Process.GetCurrentProcess();
                if (current.ProcessName.IndexOf("revit", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "RhinoInsideRevit";
                }
            }
            catch (Exception)
            {
                // Fallback below.
            }

            return "Rhino8";
        }

        public static async Task<HeadlessPromptResponse> ExecuteRequestAsync(
            HeadlessPromptRequest? request,
            CancellationToken cancellationToken = default)
        {
            string host = DetectHostType();
            if (request is null || string.IsNullOrWhiteSpace(request.Prompt))
            {
                var emptyErr = new HeadlessPromptResponse
                {
                    Status = "error",
                    Summary = "Prompt must not be empty.",
                    Host = host
                };
                emptyErr.Errors.Add("Prompt must not be empty.");
                return emptyErr;
            }

            int timeoutSec = request.TimeoutSeconds > 0 ? Math.Max(15, Math.Min(600, request.TimeoutSeconds)) : 180;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

            try
            {
                CopilotWorkMode mode = ResolveRequestedMode(request.Mode, request.Prompt);

                if (mode == CopilotWorkMode.RhinoDirect)
                {
                    IRhinoHostBridge? rhinoBridge = CopilotHostRegistry.RhinoBridge;
                    if (rhinoBridge is null)
                    {
                        var missing = new HeadlessPromptResponse
                        {
                            Status = "error",
                            Summary = "Rhino Direct host bridge is not initialized.",
                            Host = host
                        };
                        missing.Errors.Add(missing.Summary);
                        return missing;
                    }

                    CopilotGenerationResponse resp = rhinoBridge is IHeadlessChatBridge headlessRhino
                        ? await headlessRhino.SendHeadlessChatAsync(
                            request.Prompt,
                            request.SkipPlanFirst,
                            request.CaptureViewport,
                            timeoutCts.Token).ConfigureAwait(false)
                        : await rhinoBridge.SendRhinoChatAsync(
                            request.Prompt,
                            explainOnly: false,
                            timeoutCts.Token).ConfigureAwait(false);

                    return MapResponse(resp, host);
                }

                IGrasshopperHostBridge? ghBridge = CopilotHostRegistry.GrasshopperBridge;
                if (ghBridge is null)
                {
                    ghBridge = await EnsureGrasshopperLoadedAsync(timeoutCts.Token).ConfigureAwait(false);
                }

                if (ghBridge is null)
                {
                    var missingGh = new HeadlessPromptResponse
                    {
                        Status = "error",
                        Summary = "Grasshopper host bridge is unavailable. Open Grasshopper or send mode='rhino'.",
                        Host = host
                    };
                    missingGh.Errors.Add(missingGh.Summary);
                    return missingGh;
                }

                CopilotGenerationResponse ghResp;
                if (ghBridge is IHeadlessChatBridge headlessGh)
                {
                    ghResp = await headlessGh.SendHeadlessChatAsync(
                        request.Prompt,
                        request.SkipPlanFirst,
                        request.CaptureViewport,
                        timeoutCts.Token).ConfigureAwait(false);
                }
                else if (ghBridge is IChatHostBridge chatGh)
                {
                    ghResp = await chatGh.SendChatAsync(
                        request.Prompt,
                        ChatAction.Message,
                        useRhinoSelection: false,
                        timeoutCts.Token).ConfigureAwait(false);
                }
                else
                {
                    ghResp = await ghBridge.RunPromptAsync(request.Prompt, timeoutCts.Token).ConfigureAwait(false);
                }

                return MapResponse(ghResp, host);
            }
            catch (OperationCanceledException)
            {
                var cancelled = new HeadlessPromptResponse
                {
                    Status = "error",
                    Summary = "Headless request timed out or was cancelled.",
                    Host = host
                };
                cancelled.Errors.Add(cancelled.Summary);
                return cancelled;
            }
            catch (Exception ex)
            {
                var failed = new HeadlessPromptResponse
                {
                    Status = "error",
                    Summary = ex.Message,
                    Host = host
                };
                failed.Errors.Add(ex.Message);
                return failed;
            }
        }

        private static CopilotWorkMode ResolveRequestedMode(string? requestedMode, string prompt)
        {
            string normalized = (requestedMode ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized == "rhino" || normalized == "rhinodirect")
            {
                return CopilotWorkMode.RhinoDirect;
            }

            if (normalized == "grasshopper" || normalized == "gh")
            {
                return CopilotWorkMode.Grasshopper;
            }

            bool isGhOpen = CopilotHostRegistry.GrasshopperBridge != null;
            return RhinoPromptBuilder.ResolveEffectiveMode(CopilotWorkMode.Auto, prompt, isGhOpen, useRhinoSelection: false);
        }

        private static async Task<IGrasshopperHostBridge?> EnsureGrasshopperLoadedAsync(CancellationToken cancellationToken)
        {
            if (CopilotHostRegistry.GrasshopperBridge != null)
            {
                return CopilotHostRegistry.GrasshopperBridge;
            }

            try
            {
                RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    try
                    {
                        RhinoApp.RunScript("_-Grasshopper _Window _Show _Enter", false);
                    }
                    catch (Exception)
                    {
                        // Ignore headless command errors.
                    }
                }));
            }
            catch (Exception)
            {
                return null;
            }

            for (int i = 0; i < 20; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (CopilotHostRegistry.GrasshopperBridge != null)
                {
                    return CopilotHostRegistry.GrasshopperBridge;
                }

                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }

            return CopilotHostRegistry.GrasshopperBridge;
        }

        private static HeadlessPromptResponse MapResponse(CopilotGenerationResponse resp, string host)
        {
            var result = new HeadlessPromptResponse
            {
                Status = resp.IsSuccess ? "ok" : "error",
                Summary = resp.StatusLine ?? string.Empty,
                Explanation = resp.Explanation ?? string.Empty,
                ObjectsCreated = resp.NodeCount,
                ObjectsModified = 0,
                PromptTokens = resp.PromptTokens,
                ResponseTokens = resp.ResponseTokens,
                DurationMs = resp.DurationMs,
                ModelId = resp.ModelId ?? string.Empty,
                Provider = resp.Provider ?? string.Empty,
                Host = host
            };

            if (!resp.IsSuccess && !string.IsNullOrWhiteSpace(resp.StatusLine))
            {
                result.Errors.Add(resp.StatusLine!);
            }

            foreach (string warn in resp.ValidatorWarnings)
            {
                if (!string.IsNullOrWhiteSpace(warn))
                {
                    result.Errors.Add(warn);
                }
            }

            foreach (string rt in resp.RuntimeMessages)
            {
                if (!string.IsNullOrWhiteSpace(rt))
                {
                    result.Warnings.Add(rt);
                }
            }

            return result;
        }

        private void PublishEndpointInfo()
        {
            try
            {
                string docName = string.Empty;
                try
                {
                    docName = RhinoDoc.ActiveDoc?.Name ?? string.Empty;
                }
                catch (Exception)
                {
                    // Ignore when no doc is initialized yet.
                }

                var info = new HeadlessEndpointInfo
                {
                    Pid = _pid,
                    PipeName = _pipeName,
                    Host = DetectHostType(),
                    RhinoVersion = typeof(RhinoApp).Assembly.GetName().Version?.ToString() ?? string.Empty,
                    DocumentName = docName,
                    StartedAtUtc = DateTime.UtcNow
                };

                HeadlessEndpointDiscovery.WriteEndpoint(info);
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Write("Headless endpoint registration warning.", ex);
            }
        }

        private async Task ListenLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await WaitForConnectionAsync(server, cancellationToken).ConfigureAwait(false);
                    await HandleClientAsync(server, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    StartupDiagnostics.Write("Headless pipe listener iteration error.", ex);
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    server?.Dispose();
                }
            }
        }

        private static Task WaitForConnectionAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
        {
#if NET5_0_OR_GREATER
            return server.WaitForConnectionAsync(cancellationToken);
#else
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration reg = cancellationToken.Register(() =>
            {
                try { server.Dispose(); } catch { }
                tcs.TrySetCanceled();
            });

            try
            {
                server.BeginWaitForConnection(ar =>
                {
                    try
                    {
                        server.EndWaitForConnection(ar);
                        tcs.TrySetResult(true);
                    }
                    catch (ObjectDisposedException)
                    {
                        tcs.TrySetCanceled();
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                    finally
                    {
                        reg.Dispose();
                    }
                }, null);
            }
            catch (Exception ex)
            {
                reg.Dispose();
                tcs.TrySetException(ex);
            }

            return tcs.Task;
#endif
        }

        private static async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(server, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            using var writer = new StreamWriter(server, Utf8NoBom, bufferSize: 4096, leaveOpen: true) { AutoFlush = true };

            string? requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
            HeadlessPromptResponse response;
            if (string.IsNullOrWhiteSpace(requestLine))
            {
                response = new HeadlessPromptResponse
                {
                    Status = "error",
                    Summary = "Empty headless request.",
                    Host = DetectHostType()
                };
                response.Errors.Add(response.Summary);
            }
            else
            {
                try
                {
                    HeadlessPromptRequest? request = JsonSerializer.Deserialize<HeadlessPromptRequest>(requestLine!, JsonOptions);
                    response = await ExecuteRequestAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    response = new HeadlessPromptResponse
                    {
                        Status = "error",
                        Summary = "Invalid JSON request: " + ex.Message,
                        Host = DetectHostType()
                    };
                    response.Errors.Add(response.Summary);
                }
            }

            string responseJson = JsonSerializer.Serialize(response);
            await writer.WriteLineAsync(responseJson).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
    }
}
