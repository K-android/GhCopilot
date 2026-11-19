using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using GhCopilot.Core;
using GhCopilot.Core.Host;

namespace GhCopilot.UI
{
    /// <summary>
    /// Chat transcript and explicit per-request selection consent, with legacy bridge fallback.
    /// </summary>
    internal sealed class PromptTab : Panel
    {
        private readonly PreviewBar _previewBar;
        private readonly TextArea _promptBox;
        private readonly Button _sendButton;
        private readonly Button _explainButton;
        private readonly Button _fixButton;
        private readonly Button _saveRecipeButton;
        private readonly Button _cancelButton;
        private readonly Button _demoButton;
        private readonly Button _newChatButton;
        private readonly CheckBox _useRhinoSelection;
        private readonly ProgressBar _progressBar;
        private readonly Label _statusLabel;
        private readonly Label _tokenEstimateLabel;
        private readonly TextArea _transcriptBox;
        private readonly TextArea _warningsBox;
        private CancellationTokenSource? _activeCts;

        public PromptTab(PreviewBar previewBar)
        {
            _previewBar = previewBar ?? throw new ArgumentNullException(nameof(previewBar));

            _promptBox = new TextArea
            {
                Text = "circle with a radius slider",
                Height = 80,
                Wrap = true
            };

            _sendButton = new Button { Text = "Send" };
            _sendButton.Click += OnSendClicked;

            _explainButton = new Button { Text = "Explain Selection" };
            _explainButton.Click += OnExplainClicked;

            _fixButton = new Button { Text = "Fix Selection" };
            _fixButton.Click += OnFixClicked;

            _saveRecipeButton = new Button { Text = "Save Recipe" };
            _saveRecipeButton.Click += OnSaveRecipeClicked;

            _cancelButton = new Button { Text = "Cancel", Enabled = false };
            _cancelButton.Click += OnCancelClicked;

            _demoButton = new Button { Text = "Spawn Demo (No LLM)" };
            _demoButton.Click += OnDemoClicked;

            _newChatButton = new Button { Text = "New Chat" };
            _newChatButton.Click += OnNewChatClicked;
            _useRhinoSelection = new CheckBox { Text = "Use Rhino Selection", Checked = false };
            _tokenEstimateLabel = new Label { Text = "Approx. input tokens: — (not billing usage)" };

            _progressBar = new ProgressBar
            {
                Indeterminate = true,
                Visible = false
            };

            _statusLabel = new Label
            {
                Text = "Ready."
            };
            _transcriptBox = new TextArea
            {
                ReadOnly = true,
                Wrap = true,
                Height = 220
            };

            _warningsBox = new TextArea
            {
                ReadOnly = true,
                Wrap = true,
                Height = 75
            };

            var layout = new DynamicLayout
            {
                Padding = new Padding(8),
                Spacing = new Size(6, 6)
            };

            layout.BeginVertical();
            layout.AddRow(new Label { Text = "Chat:" }, _newChatButton);
            layout.Add(_transcriptBox, yscale: true);
            layout.Add(new Label { Text = "Prompt (or optional hint for Explain / Fix):" });
            layout.Add(_promptBox);
            layout.Add(_useRhinoSelection);
            layout.AddRow(_sendButton, _cancelButton, _demoButton);
            layout.AddRow(_explainButton, _fixButton, _saveRecipeButton);
            layout.Add(_progressBar);
            layout.Add(_statusLabel);
            layout.Add(_tokenEstimateLabel);
            layout.Add(new Label { Text = "Validator Warnings / Runtime Messages:" });
            layout.Add(_warningsBox);
            layout.EndVertical();
            Content = layout;

            if (CopilotHostRegistry.GrasshopperBridge is IChatHostBridge chat)
            {
                if (!string.IsNullOrEmpty(chat.Session.Summary)) AppendTranscript("Context", chat.Session.Summary);
                foreach (var message in chat.Session.Messages) AppendTranscript(message.Role, message.Content);
            }
            _previewBar.PreviewActionCompleted += (_, status) =>
            {
                _statusLabel.Text = status;
                AppendTranscript("Assistant", status);
                UpdateActionState();
            };
            _previewBar.PreviewStateChanged += (_, __) => UpdateActionState();
            UpdateActionState();
        }

        private bool CanStartAction()
        {
            if (_activeCts != null) return false;
            if (CopilotHostRegistry.GrasshopperBridge?.HasActivePreview == true)
            {
                _statusLabel.Text = "Accept or reject the pending preview before continuing.";
                UpdateActionState();
                return false;
            }
            return true;
        }

        private Task RunBridgeActionAsync(string prompt, ChatAction action, string busyMessage)
        {
            if (!CanStartAction()) return Task.CompletedTask;
            IGrasshopperHostBridge? bridge = CopilotHostRegistry.GrasshopperBridge;
            if (bridge is null)
            {
                const string missing = "Open Grasshopper first to initialize the .gha bridge.";
                _statusLabel.Text = missing;
                AppendTranscript("Assistant", missing);
                MessageBox.Show(this, missing, PluginConstants.ProductName, MessageBoxType.Warning);
                return Task.CompletedTask;
            }

            bool useRhinoSelection = _useRhinoSelection.Checked == true;
            if (useRhinoSelection && !(bridge is IChatHostBridge))
            {
                _statusLabel.Text = "This host does not support explicit Rhino selection. Uncheck Use Rhino Selection to continue.";
                return Task.CompletedTask;
            }
            return ExecuteAsync(bridge, prompt, action, useRhinoSelection, busyMessage);
        }

        private async Task ExecuteAsync(
            IGrasshopperHostBridge bridge,
            string prompt,
            ChatAction action,
            bool useRhinoSelection,
            string busyMessage)
        {
            var cts = new CancellationTokenSource();
            _activeCts = cts;
            IChatHostBridge? chat = bridge as IChatHostBridge;
            Action<int> onEstimate = estimate => Application.Instance.AsyncInvoke(() =>
            {
                if (ReferenceEquals(_activeCts, cts)) ShowTokenEstimate(estimate);
            });
            if (chat != null) chat.TokenEstimateChanged += onEstimate;
            SetRunningState(isRunning: true, statusText: busyMessage);
            _tokenEstimateLabel.Text = "Approx. input tokens: estimating… (not billing usage)";
            AppendTranscript("User", (action == ChatAction.Message ? "" : "[" + action + "] ")
                + prompt + (useRhinoSelection ? " [Use Rhino Selection]" : ""));
            _warningsBox.Text = string.Empty;

            CopilotGenerationResponse? response = null;
            string? failure = null;
            try
            {
                if (chat != null)
                {
                    response = await chat.SendChatAsync(prompt, action, useRhinoSelection, cts.Token);
                }
                else
                {
                    switch (action)
                    {
                        case ChatAction.ExplainSelection:
                            response = await bridge.ExplainSelectionAsync(prompt, cts.Token);
                            break;
                        case ChatAction.FixSelection:
                            response = await bridge.FixSelectionAsync(prompt, cts.Token);
                            break;
                        case ChatAction.SaveRecipe:
                            string name = prompt.Length > 48 ? prompt.Substring(0, 48).Trim() : prompt;
                            string result = bridge.SaveSelectionAsRecipe(name, prompt);
                            response = new CopilotGenerationResponse { IsSuccess = true, StatusLine = result, Explanation = result };
                            break;
                        default:
                            response = await bridge.RunPromptAsync(prompt, cts.Token);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                failure = "Request cancelled.";
            }
            catch (Exception ex)
            {
                failure = $"Error: {ex.Message}";
            }
            finally
            {
                if (chat != null) chat.TokenEstimateChanged -= onEstimate;
                Application.Instance.AsyncInvoke(() =>
                {
                    try
                    {
                        if (response != null)
                        {
                            _statusLabel.Text = response.StatusLine;
                            string answer = string.IsNullOrWhiteSpace(response.Explanation) ? response.StatusLine : response.Explanation;
                            AppendTranscript("Assistant", (response.IsSuccess ? "" : "[Error] ") + answer);
                            var combinedMessages = new List<string>();
                            foreach (string warning in response.ValidatorWarnings) combinedMessages.Add("[Validator] " + warning);
                            combinedMessages.AddRange(response.RuntimeMessages);
                            _warningsBox.Text = combinedMessages.Count > 0 ? string.Join(Environment.NewLine, combinedMessages) : "None.";
                            if (chat != null) ShowTokenEstimate(response.EstimatedInputTokens);
                            else _tokenEstimateLabel.Text = "Approx. input tokens: unavailable on legacy host (not billing usage)";
                        }
                        else
                        {
                            _statusLabel.Text = failure ?? "No response returned.";
                            AppendTranscript("Assistant", _statusLabel.Text);
                            _tokenEstimateLabel.Text += " — request did not complete";
                        }
                    }
                    finally
                    {
                        _useRhinoSelection.Checked = false;
                        _activeCts = null;
                        cts.Dispose();
                        SetRunningState(isRunning: false);
                    }
                });
            }
        }

        private async void OnSendClicked(object? sender, EventArgs e)
        {
            string prompt = _promptBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(prompt))
            {
                _statusLabel.Text = "Enter a prompt first.";
                return;
            }

            await RunBridgeActionAsync(
                prompt, ChatAction.Message, "Retrieving components and calling Gemini...");
        }

        private async void OnExplainClicked(object? sender, EventArgs e)
        {
            string hint = _promptBox.Text?.Trim() ?? string.Empty;
            await RunBridgeActionAsync(
                hint, ChatAction.ExplainSelection, "Serializing selection and asking Gemini to explain...");
        }

        private async void OnFixClicked(object? sender, EventArgs e)
        {
            string hint = _promptBox.Text?.Trim() ?? string.Empty;
            await RunBridgeActionAsync(
                hint, ChatAction.FixSelection, "Serializing selection and generating fix preview...");
        }

        private void OnCancelClicked(object? sender, EventArgs e)
        {
            _activeCts?.Cancel();
        }

        private void OnDemoClicked(object? sender, EventArgs e)
        {
            if (!CanStartAction()) return;
            IGrasshopperHostBridge? bridge = CopilotHostRegistry.GrasshopperBridge;
            if (bridge is null)
            {
                const string missing = "Open Grasshopper first to initialize the .gha bridge.";
                _statusLabel.Text = missing;
                MessageBox.Show(this, missing, PluginConstants.ProductName, MessageBoxType.Warning);
                return;
            }

            AppendTranscript("User", "Spawn Demo (No LLM)");
            try
            {
                string status = bridge.SpawnDemoGraph();
                _statusLabel.Text = status;
                AppendTranscript("Assistant", status + Environment.NewLine + "Slider(12.5) + Slider(7.5) -> Addition -> Panel.");
                _warningsBox.Text = "None.";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Error: " + ex.Message;
                AppendTranscript("Assistant", _statusLabel.Text);
            }
            finally { _previewBar.RefreshState(); }
        }

        private async void OnSaveRecipeClicked(object? sender, EventArgs e)
        {
            string text = _promptBox.Text?.Trim() ?? string.Empty;
            await RunBridgeActionAsync(text, ChatAction.SaveRecipe, "Saving selected graph as a recipe...");
        }

        private void OnNewChatClicked(object? sender, EventArgs e)
        {
            if (!CanStartAction()) return;
            try
            {
                (CopilotHostRegistry.GrasshopperBridge as IChatHostBridge)?.NewChat();
                _transcriptBox.Text = string.Empty;
                _warningsBox.Text = string.Empty;
                _promptBox.Text = string.Empty;
                _useRhinoSelection.Checked = false;
                _tokenEstimateLabel.Text = "Approx. input tokens: — (not billing usage)";
                _statusLabel.Text = "New chat ready. Existing canvas objects are unchanged.";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Error: " + ex.Message;
                AppendTranscript("Assistant", _statusLabel.Text);
            }
        }

        private void AppendTranscript(string role, string content)
        {
            string previous = _transcriptBox.Text ?? string.Empty;
            _transcriptBox.Text = previous + (previous.Length == 0 ? "" : Environment.NewLine + Environment.NewLine)
                + role + ":" + Environment.NewLine + content;
            _transcriptBox.CaretIndex = _transcriptBox.Text.Length;
        }

        private void ShowTokenEstimate(int estimate)
        {
            _tokenEstimateLabel.Text = $"Approx. input tokens: {Math.Max(0, estimate):N0} (not billing usage)";
        }

        private void UpdateActionState()
        {
            bool running = _activeCts != null;
            bool canAct = !running && CopilotHostRegistry.GrasshopperBridge?.HasActivePreview != true;
            _sendButton.Enabled = canAct;
            _explainButton.Enabled = canAct;
            _fixButton.Enabled = canAct;
            _saveRecipeButton.Enabled = canAct;
            _demoButton.Enabled = canAct;
            _newChatButton.Enabled = canAct;
            _useRhinoSelection.Enabled = canAct;
            _promptBox.Enabled = !running;
        }

        private void SetRunningState(bool isRunning, string? statusText = null)
        {
            UpdateActionState();
            _previewBar.SetBusy(isRunning);
            _cancelButton.Enabled = isRunning;
            _progressBar.Visible = isRunning;
            if (statusText != null)
            {
                _statusLabel.Text = statusText;
            }
        }
    }
}
