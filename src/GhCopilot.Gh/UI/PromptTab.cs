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
    /// Prompt input tab with Send, Explain Selection, Fix Selection, Cancel, progress/status indicator,
    /// model explanation, and validator warnings.
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
        private readonly ProgressBar _progressBar;
        private readonly Label _statusLabel;
        private readonly TextArea _explanationBox;
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

            _progressBar = new ProgressBar
            {
                Indeterminate = true,
                Visible = false
            };

            _statusLabel = new Label
            {
                Text = "Ready."
            };
            _previewBar.PreviewActionCompleted += (_, status) => _statusLabel.Text = status;

            _explanationBox = new TextArea
            {
                ReadOnly = true,
                Wrap = true,
                Height = 90
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
            layout.Add(new Label { Text = "Prompt (or optional hint for Explain / Fix):" });
            layout.Add(_promptBox);
            layout.AddRow(_sendButton, _cancelButton, _demoButton);
            layout.AddRow(_explainButton, _fixButton, _saveRecipeButton);
            layout.Add(_progressBar);
            layout.Add(_statusLabel);
            layout.Add(new Label { Text = "Explanation:" });
            layout.Add(_explanationBox);
            layout.Add(new Label { Text = "Validator Warnings / Runtime Messages:" });
            layout.Add(_warningsBox);
            layout.EndVertical();
            layout.Add(null);

            Content = layout;
        }

        private Task RunBridgeActionAsync(
            string busyMessage,
            Func<IGrasshopperHostBridge, CancellationToken, Task<CopilotGenerationResponse>> action)
        {
            IGrasshopperHostBridge? bridge = CopilotHostRegistry.GrasshopperBridge;
            if (bridge is null)
            {
                const string missing = "Open Grasshopper first to initialize the .gha bridge.";
                _statusLabel.Text = missing;
                MessageBox.Show(this, missing, PluginConstants.ProductName, MessageBoxType.Warning);
                return Task.CompletedTask;
            }

            return ExecuteAsync(bridge, busyMessage, action);
        }

        private async Task ExecuteAsync(
            IGrasshopperHostBridge bridge,
            string busyMessage,
            Func<IGrasshopperHostBridge, CancellationToken, Task<CopilotGenerationResponse>> action)
        {
            _activeCts?.Dispose();
            _activeCts = new CancellationTokenSource();

            SetRunningState(isRunning: true, statusText: busyMessage);
            _explanationBox.Text = string.Empty;
            _warningsBox.Text = string.Empty;

            try
            {
                CopilotGenerationResponse response = await action(bridge, _activeCts.Token);
                Application.Instance.AsyncInvoke(() =>
                {
                    _statusLabel.Text = response.StatusLine;
                    _explanationBox.Text = response.Explanation;

                    var combinedMessages = new List<string>();
                    foreach (string w in response.ValidatorWarnings)
                    {
                        combinedMessages.Add($"[Validator] {w}");
                    }
                    foreach (string r in response.RuntimeMessages)
                    {
                        combinedMessages.Add(r);
                    }

                    _warningsBox.Text = combinedMessages.Count > 0
                        ? string.Join(Environment.NewLine, combinedMessages)
                        : "None.";

                    _previewBar.RefreshState();
                });
            }
            catch (OperationCanceledException)
            {
                Application.Instance.AsyncInvoke(() =>
                {
                    _statusLabel.Text = "Request cancelled.";
                    _previewBar.RefreshState();
                });
            }
            catch (Exception ex)
            {
                Application.Instance.AsyncInvoke(() =>
                {
                    _statusLabel.Text = $"Error: {ex.Message}";
                    _previewBar.RefreshState();
                });
            }
            finally
            {
                Application.Instance.AsyncInvoke(() => SetRunningState(isRunning: false));
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
                "Retrieving components and calling Gemini...",
                (bridge, ct) => bridge.RunPromptAsync(prompt, ct));
        }

        private async void OnExplainClicked(object? sender, EventArgs e)
        {
            string? hint = _promptBox.Text?.Trim();
            await RunBridgeActionAsync(
                "Serializing selection and asking Gemini to explain...",
                (bridge, ct) => bridge.ExplainSelectionAsync(hint, ct));
        }

        private async void OnFixClicked(object? sender, EventArgs e)
        {
            string? hint = _promptBox.Text?.Trim();
            await RunBridgeActionAsync(
                "Serializing selection and generating fix preview...",
                (bridge, ct) => bridge.FixSelectionAsync(hint, ct));
        }

        private void OnCancelClicked(object? sender, EventArgs e)
        {
            _activeCts?.Cancel();
        }

        private void OnDemoClicked(object? sender, EventArgs e)
        {
            IGrasshopperHostBridge? bridge = CopilotHostRegistry.GrasshopperBridge;
            if (bridge is null)
            {
                const string missing = "Open Grasshopper first to initialize the .gha bridge.";
                _statusLabel.Text = missing;
                MessageBox.Show(this, missing, PluginConstants.ProductName, MessageBoxType.Warning);
                return;
            }

            string status = bridge.SpawnDemoGraph();
            _statusLabel.Text = status;
            _explanationBox.Text = "Phase 1 hardcoded demo: Slider(12.5) + Slider(7.5) -> Addition -> Panel.";
            _warningsBox.Text = "None.";
            _previewBar.RefreshState();
        }

        private void OnSaveRecipeClicked(object? sender, EventArgs e)
        {
            IGrasshopperHostBridge? bridge = CopilotHostRegistry.GrasshopperBridge;
            if (bridge is null)
            {
                const string missing = "Open Grasshopper first to initialize the .gha bridge.";
                _statusLabel.Text = missing;
                MessageBox.Show(this, missing, PluginConstants.ProductName, MessageBoxType.Warning);
                return;
            }

            string text = _promptBox.Text?.Trim() ?? string.Empty;
            string name = text.Length > 48 ? text.Substring(0, 48).Trim() : text;
            string result = bridge.SaveSelectionAsRecipe(name, text);
            _statusLabel.Text = result;
        }

        private void SetRunningState(bool isRunning, string? statusText = null)
        {
            _sendButton.Enabled = !isRunning;
            _explainButton.Enabled = !isRunning;
            _fixButton.Enabled = !isRunning;
            _saveRecipeButton.Enabled = !isRunning;
            _demoButton.Enabled = !isRunning;
            _cancelButton.Enabled = isRunning;
            _progressBar.Visible = isRunning;
            if (statusText != null)
            {
                _statusLabel.Text = statusText;
            }
        }
    }
}
