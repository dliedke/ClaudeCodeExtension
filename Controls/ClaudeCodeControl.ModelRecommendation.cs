/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: "Recommend AI Model" toolbar feature (Claude Code only). Asks Opus at Extra High effort, in a
 *          separate one-shot CLI call, which model and effort suit the prompt the user has typed, shows the
 *          answer in a dialog where it can be adjusted, and applies the confirmed pair to the terminal,
 *          the chat or the parallel chat tab the prompt came from. Never sends the prompt itself.
 *
 * *******************************************************************************************************************/

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using ClaudeCodeVS.Agents;
using ClaudeCodeVS.UI;

using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Recommend AI Model

        /// <summary>
        /// Longest the advisor is given before the dialog gives up on it. Measured answers take 6–9 s;
        /// the margin covers a cold WSL distro and a slow network.
        /// </summary>
        private const int ModelRecommendationTimeoutMs = 120000;

        /// <summary>
        /// Pause between the <c>/model</c> and <c>/effort</c> commands typed into the terminal, so the
        /// TUI has finished applying the first before the second arrives.
        /// </summary>
        private const int TerminalCommandGapMs = 800;

        /// <summary>The model choices the dialog offers, cheapest first, matching the advisor's own list.</summary>
        private static readonly ClaudeModel[] RecommendableModels =
        {
            ClaudeModel.Haiku, ClaudeModel.Sonnet, ClaudeModel.Opus, ClaudeModel.Fable
        };

#pragma warning disable VSTHRD100 // Avoid async void methods - WPF event handler
        private async void RecommendModelButton_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            try
            {
                await RecommendModelAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RecommendModelButton_Click error: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads the prompt the user has typed, asks the advisor about it inside a dialog, and applies
        /// whatever pair the user confirms there. Cancelling the dialog changes nothing.
        /// </summary>
        private async Task RecommendModelAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_settings == null)
            {
                return;
            }

            string prompt = GetPromptForRecommendation(out NativeChatSessionState session);
            AiProvider? provider = session != null ? session.SelectedProvider : GetActiveOrSelectedProvider();

            if (!IsClaudeProvider(provider))
            {
                MessageBox.Show(
                    "Recommend AI Model works with Claude Code only.\n\nSwitch the agent to Claude Code or Claude Code (WSL) to use it.",
                    "Recommend AI Model",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(prompt))
            {
                MessageBox.Show(
                    "Type your prompt first, then click Recommend AI Model to get the model and effort that suit it.",
                    "Recommend AI Model",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            ClaudeModel currentModel = session?.SelectedClaudeModel ?? _settings.SelectedClaudeModel;
            EffortLevel currentEffort = session?.SelectedEffortLevel ?? _settings.SelectedEffortLevel;

            (ClaudeModel Model, EffortLevel Effort)? choice =
                ShowModelRecommendationDialog(provider.Value, prompt, currentModel, currentEffort);

            if (choice == null)
            {
                return;
            }

            await ApplyRecommendedModelAsync(session, choice.Value.Model, choice.Value.Effort);
        }

        /// <summary>
        /// The prompt the user is about to send, and the parallel chat tab it belongs to (null for the
        /// panel and its default chat). In native mode the focused tab's composer is checked first, then
        /// the default chat's composer when it is the live input; the panel's prompt box is the fallback
        /// in every mode. The tab whose composer the text came from is the one the result is applied to.
        /// </summary>
        private string GetPromptForRecommendation(out NativeChatSessionState session)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            session = null;

            if (IsNativeModeActive)
            {
                NativeChatSessionState focused = GetSession(ResolveFocusedNativeSessionId());
                string tabText = focused?.ChatTranscript?.ComposerText;
                if (!string.IsNullOrWhiteSpace(tabText))
                {
                    session = focused;
                    return tabText;
                }

                if (ChatTranscript != null && ChatTranscript.HasComposerInput
                    && !string.IsNullOrWhiteSpace(ChatTranscript.ComposerText))
                {
                    return ChatTranscript.ComposerText;
                }
            }

            return PromptTextBox?.Text;
        }

        /// <summary>
        /// Shows the dialog and starts the advisor as soon as it is on screen. The model and effort
        /// pickers open on the current pair and move to the recommended one when it arrives; if the
        /// advisor fails, the error replaces the status line and the pickers stay usable for a manual
        /// choice. Returns the confirmed pair, or null when the dialog was cancelled. Closing the dialog
        /// early kills the advisor process.
        /// </summary>
        private (ClaudeModel Model, EffortLevel Effort)? ShowModelRecommendationDialog(
            AiProvider provider, string prompt, ClaudeModel currentModel, EffortLevel currentEffort)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            GetThemeBrushes(out Brush themeBg, out Brush themeFg);
            ResourceDictionary comboRes = BuildThemedComboResources(themeBg, themeFg);

            // Fable needs usage credits — left out on a zero balance unless it is the current pick.
            bool offerFable = currentModel == ClaudeModel.Fable || IsFableModelOffered();
            ClaudeModel[] models = offerFable
                ? RecommendableModels
                : Array.FindAll(RecommendableModels, m => m != ClaudeModel.Fable);

            var dialog = new Window
            {
                Title = "Recommend AI Model",
                Width = 560,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = themeBg,
                Foreground = themeFg,
                ShowInTaskbar = false
            };
            try { dialog.Owner = Application.Current?.MainWindow; } catch { }

            var root = new StackPanel { Margin = new Thickness(14) };
            dialog.Content = root;

            root.Children.Add(new TextBlock
            {
                Text = "Prompt",
                FontWeight = FontWeights.SemiBold,
                Foreground = themeFg,
                Margin = new Thickness(0, 0, 0, 4)
            });

            root.Children.Add(new TextBox
            {
                Text = prompt.Trim(),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 96,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = themeBg,
                Foreground = themeFg,
                BorderBrush = themeFg,
                Opacity = 0.85,
                Padding = new Thickness(4),
                Margin = new Thickness(0, 0, 0, 12)
            });

            var status = new TextBlock
            {
                Text = $"Asking {GetClaudeModelDisplayName(ClaudeModel.Opus)} ({GetChatEffortLabel(EffortLevel.XHigh)}) which model and effort suit this prompt…",
                Foreground = themeFg,
                TextWrapping = TextWrapping.Wrap
            };
            root.Children.Add(status);

            var progress = new ProgressBar
            {
                IsIndeterminate = true,
                Height = 4,
                Margin = new Thickness(0, 6, 0, 0)
            };
            root.Children.Add(progress);

            var reason = new TextBlock
            {
                Foreground = themeFg,
                Opacity = 0.85,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
                Visibility = Visibility.Collapsed
            };
            root.Children.Add(reason);

            // ---- Model / effort pickers ----
            var pickers = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            pickers.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pickers.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pickers.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pickers.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(pickers);

            Func<string, int, TextBlock> makeLabel = (text, column) =>
            {
                var label = new TextBlock
                {
                    Text = text,
                    Foreground = themeFg,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(column == 0 ? 0 : 14, 0, 8, 0)
                };
                Grid.SetColumn(label, column);
                pickers.Children.Add(label);
                return label;
            };

            makeLabel("Model:", 0);
            var modelCombo = MakeThemedComboBox(comboRes, themeFg);
            Grid.SetColumn(modelCombo, 1);
            pickers.Children.Add(modelCombo);

            makeLabel("Effort:", 2);
            var effortCombo = MakeThemedComboBox(comboRes, themeFg);
            Grid.SetColumn(effortCombo, 3);
            pickers.Children.Add(effortCombo);

            foreach (ClaudeModel model in models)
            {
                var item = new ComboBoxItem { Content = GetClaudeModelDisplayName(model), Tag = model, Foreground = themeFg };
                if (comboRes["cbi"] is Style cbiStyle) item.Style = cbiStyle;
                modelCombo.Items.Add(item);
            }

            foreach (EffortLevel level in _effortSliderOrder)
            {
                var item = new ComboBoxItem { Content = GetChatEffortLabel(level), Tag = level, Foreground = themeFg };
                if (comboRes["cbi"] is Style cbiStyle) item.Style = cbiStyle;
                effortCombo.Items.Add(item);
            }

            Action<ClaudeModel, EffortLevel> selectPair = (model, effort) =>
            {
                // Opus Plan is not a choice here (it has no headless equivalent); Opus is its nearest.
                int modelIndex = Array.IndexOf(models, model == ClaudeModel.OpusPlan ? ClaudeModel.Opus : model);
                modelCombo.SelectedIndex = modelIndex >= 0 ? modelIndex : Array.IndexOf(models, ClaudeModel.Opus);
                effortCombo.SelectedIndex = EffortToSliderIndex(effort);
            };
            selectPair(currentModel, currentEffort);

            root.Children.Add(new TextBlock
            {
                Text = $"Currently: {GetClaudeModelDisplayName(currentModel)} · {GetChatEffortLabel(currentEffort)}",
                Foreground = themeFg,
                Opacity = 0.65,
                Margin = new Thickness(0, 8, 0, 0)
            });

            // ---- Buttons ----
            Style buttonStyle = GetDialogButtonStyle();
            Func<string, Button> makeButton = text =>
            {
                var button = new Button
                {
                    Content = text,
                    MinWidth = 96,
                    Height = 28,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                if (buttonStyle != null) button.Style = buttonStyle;
                else { button.Background = themeBg; button.Foreground = themeFg; button.BorderBrush = themeFg; }
                return button;
            };

            var applyButton = makeButton("Apply");
            applyButton.IsDefault = true;
            applyButton.IsEnabled = false;
            applyButton.ToolTip = "Switch to the model and effort selected above. The prompt is not sent.";

            var cancelButton = makeButton("Cancel");
            cancelButton.IsCancel = true;

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };
            buttons.Children.Add(applyButton);
            buttons.Children.Add(cancelButton);
            root.Children.Add(buttons);

            (ClaudeModel Model, EffortLevel Effort)? result = null;

            applyButton.Click += delegate
            {
                if (modelCombo.SelectedItem is ComboBoxItem modelItem && modelItem.Tag is ClaudeModel model
                    && effortCombo.SelectedItem is ComboBoxItem effortItem && effortItem.Tag is EffortLevel effort)
                {
                    result = (model, effort);
                }
                dialog.DialogResult = true;
            };

            var cts = new CancellationTokenSource();
            dialog.Closed += delegate { cts.Cancel(); };

            dialog.Loaded += delegate
            {
#pragma warning disable VSSDK007, VSTHRD110 // Fire and forget: FileAndForget is the handler
                ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                {
                    (ModelRecommendation recommendation, string error) =
                        await RequestModelRecommendationAsync(provider, prompt, cts.Token);

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    // Closed while the advisor was still thinking: nothing left to update.
                    if (cts.IsCancellationRequested)
                    {
                        return;
                    }

                    progress.Visibility = Visibility.Collapsed;
                    applyButton.IsEnabled = true;

                    if (recommendation != null
                        && TryMapRecommendation(recommendation, out ClaudeModel model, out EffortLevel effort))
                    {
                        // No usage credits: Fable is not on offer, so its nearest, Opus, is proposed.
                        bool fableUnavailable = model == ClaudeModel.Fable && !offerFable;
                        if (fableUnavailable)
                        {
                            model = ClaudeModel.Opus;
                        }

                        status.Text = $"Recommended: {GetClaudeModelDisplayName(model)} · {GetChatEffortLabel(effort)}";
                        status.FontWeight = FontWeights.SemiBold;

                        string reasonText = recommendation.Reason?.Trim() ?? string.Empty;
                        if (fableUnavailable)
                        {
                            reasonText = (reasonText + " Fable was recommended, but it needs usage credits and the balance is zero.").Trim();
                        }

                        if (!string.IsNullOrWhiteSpace(reasonText))
                        {
                            reason.Text = reasonText;
                            reason.Visibility = Visibility.Visible;
                        }

                        selectPair(model, effort);
                        applyButton.Focus();
                    }
                    else
                    {
                        status.Text = "No recommendation: " + (error ?? "the answer could not be read.")
                            + " You can still pick a model and effort yourself.";
                    }
                }).FileAndForget("claudecode/recommendmodel/request");
#pragma warning restore VSSDK007, VSTHRD110
            };

            dialog.ShowDialog();

            return result;
        }

        /// <summary>
        /// Runs the advisor: a one-shot <c>claude --print</c> in the workspace, prompt over stdin
        /// (nothing to escape, no length limit), answer read from the single JSON result line. Uses the
        /// same CLI the conversation does — the WSL one for Claude Code (WSL), the resolved Windows
        /// executable (custom path included) otherwise. Returns a recommendation or an error message;
        /// both are null when <paramref name="cancellationToken"/> fired first.
        /// </summary>
        private async Task<(ModelRecommendation Recommendation, string Error)> RequestModelRecommendationAsync(
            AiProvider provider, string prompt, CancellationToken cancellationToken)
        {
            string workspace = await GetWorkspaceDirectoryAsync();
            if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
            {
                workspace = Path.GetTempPath();
            }

            bool isWsl = provider == AiProvider.ClaudeCodeWSL;

            var options = new ClaudeSessionOptions
            {
                UseWsl = isWsl,
                ExecutablePath = isWsl ? "claude" : ResolveNativeClaudeExecutable(),
                WslWorkingDirectory = isWsl ? ConvertToWslPath(workspace) : string.Empty
            };

            var hostOptions = new JsonLineProcessOptions
            {
                FileName = ClaudeCommandBuilder.GetFileName(options),
                Arguments = ClaudeCommandBuilder.GetModelRecommendationArguments(options),
                WorkingDirectory = workspace
            };

            // A CLI installed after Visual Studio started is missing from the PATH we inherited.
            string freshPath = GetFreshPathFromRegistry();
            if (!string.IsNullOrWhiteSpace(freshPath))
            {
                hostOptions.EnvironmentOverrides["PATH"] = freshPath;
            }

            var output = new StringBuilder();
            var stderr = new StringBuilder();
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            var host = new JsonLineProcessHost(hostOptions);

            EventHandler<string> onLine = delegate (object s, string line) { lock (output) { output.AppendLine(line); } };
            EventHandler<string> onError = delegate (object s, string line) { lock (stderr) { stderr.AppendLine(line); } };
            EventHandler<int> onExited = delegate (object s, int code) { exited.TrySetResult(code); };

            host.LineReceived += onLine;
            host.ErrorLineReceived += onError;
            host.Exited += onExited;

            try
            {
                await host.StartAsync(cancellationToken);

                // EOF is what tells the CLI the prompt is complete.
                await host.WriteLineAsync(ModelRecommender.BuildRequest(prompt), cancellationToken);
                host.CloseInput();

                Task completed = await Task.WhenAny(exited.Task, Task.Delay(ModelRecommendationTimeoutMs, cancellationToken));
                if (cancellationToken.IsCancellationRequested)
                {
                    return (null, null);
                }

                if (completed != exited.Task)
                {
                    return (null, "Claude Code did not answer in time.");
                }

                await host.WaitForOutputDrainAsync(2000);

                string text;
                lock (output) { text = output.ToString(); }

                ModelRecommendation recommendation = ModelRecommender.ParseOutput(text, out string error);
                if (recommendation != null)
                {
                    return (recommendation, null);
                }

                // Nothing on stdout at all usually means the CLI failed to start (not installed in the
                // distro, bad custom path); stderr says why.
                string detail;
                lock (stderr) { detail = stderr.ToString().Trim(); }

                return (null, string.IsNullOrWhiteSpace(text) && detail.Length > 0 ? detail : error);
            }
            catch (OperationCanceledException)
            {
                return (null, null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Recommend AI Model failed: {ex}");
                return (null, ex.Message);
            }
            finally
            {
                host.LineReceived -= onLine;
                host.ErrorLineReceived -= onError;
                host.Exited -= onExited;

                try
                {
                    host.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Recommend AI Model: host dispose failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Applies the confirmed pair where the prompt came from. A parallel chat tab switches only its
        /// own session, unpersisted, like its composer pickers do. Otherwise the global selection is
        /// saved and then pushed to whatever is running: a native chat switches live (model, then effort)
        /// and is relaunched with resume only if the live switch is refused; the terminal gets the same
        /// <c>/model</c> and <c>/effort</c> commands the 🤖 menu types. With nothing running, the pair
        /// simply applies at the next launch.
        /// </summary>
        private async Task ApplyRecommendedModelAsync(NativeChatSessionState session, ClaudeModel model, EffortLevel effort)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string label = $"{GetClaudeModelDisplayName(model)} · {GetChatEffortLabel(effort)}";

            if (session != null)
            {
                bool tabModelChanged = session.SelectedClaudeModel != model;
                bool tabEffortChanged = session.SelectedEffortLevel != effort;
                if (!tabModelChanged && !tabEffortChanged)
                {
                    return;
                }

                session.SelectedClaudeModel = model;
                session.SelectedEffortLevel = effort;
                UpdateChatComposerState(session);

                bool tabLive = (!tabModelChanged || await TrySwitchClaudeModelAsync(session.AgentSession, MapClaudeModelArgument(model)))
                    && (!tabEffortChanged || await TrySwitchClaudeEffortAsync(session.AgentSession, MapEffortArgument(effort)));

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (tabLive)
                {
                    AddNativeMessageToSession(session, ChatMessageKind.Notice, $"💡 Switched to {label}.");
                }
                else
                {
                    await RelaunchSessionAsync(session, $"💡 Switched to {label}");
                }
                return;
            }

            if (_settings == null)
            {
                return;
            }

            bool modelChanged = _settings.SelectedClaudeModel != model;
            bool effortChanged = _settings.SelectedEffortLevel != effort;
            if (!modelChanged && !effortChanged)
            {
                return;
            }

            _settings.SelectedClaudeModel = model;
            _settings.SelectedEffortLevel = effort;
            if (!IsSessionOnlyEffort(effort))
            {
                _lastPersistableEffortLevel = effort;
            }

            UpdateModelSelection();
            UpdateEffortSelection();
            SaveSettings(modelChanged ? nameof(ClaudeCodeSettings.SelectedClaudeModel) : null);

            if (IsNativeModeActive)
            {
                UpdateChatComposerState();

                bool live = (!modelChanged || await TrySwitchClaudeModelAsync(MapClaudeModelArgument(model)))
                    && (!effortChanged || await TrySwitchClaudeEffortAsync(MapEffortArgument(effort)));

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (live)
                {
                    AddNativeMessage(ChatMessageKind.Notice, $"💡 Switched to {label}.");
                }
                else
                {
                    await RelaunchNativeSessionAsync($"💡 Switched to {label}");
                }
                return;
            }

            if (!IsClaudeProvider(_currentRunningProvider))
            {
                return;
            }

            // Model first: the effort that follows is then applied to the model it was chosen for.
            if (modelChanged)
            {
                await SendTextToTerminalAsync("/model " + MapClaudeModelArgument(model));
            }

            if (modelChanged && effortChanged)
            {
                await Task.Delay(TerminalCommandGapMs);
            }

            if (effortChanged)
            {
                await SendTextToTerminalAsync("/effort " + MapEffortArgument(effort));
            }
        }

        /// <summary>
        /// Turns the advisor's words into the extension's own model and effort values. False for
        /// anything outside <see cref="ModelRecommender.Models"/>/<see cref="ModelRecommender.Efforts"/>.
        /// </summary>
        public static bool TryMapRecommendation(ModelRecommendation recommendation, out ClaudeModel model, out EffortLevel effort)
        {
            model = ClaudeModel.Opus;
            effort = EffortLevel.High;

            if (recommendation == null)
            {
                return false;
            }

            switch (recommendation.Model)
            {
                case "haiku": model = ClaudeModel.Haiku; break;
                case "sonnet": model = ClaudeModel.Sonnet; break;
                case "opus": model = ClaudeModel.Opus; break;
                case "fable": model = ClaudeModel.Fable; break;
                default: return false;
            }

            switch (recommendation.Effort)
            {
                case "low": effort = EffortLevel.Low; break;
                case "medium": effort = EffortLevel.Medium; break;
                case "high": effort = EffortLevel.High; break;
                case "xhigh": effort = EffortLevel.XHigh; break;
                case "max": effort = EffortLevel.Max; break;
                default: return false;
            }

            return true;
        }

        private static string GetClaudeModelDisplayName(ClaudeModel model)
        {
            switch (model)
            {
                case ClaudeModel.Haiku: return "Haiku";
                case ClaudeModel.Sonnet: return "Sonnet";
                case ClaudeModel.Opus: return "Opus";
                case ClaudeModel.OpusPlan: return "Opus Plan";
                default: return "Fable";
            }
        }

        #endregion
    }
}
