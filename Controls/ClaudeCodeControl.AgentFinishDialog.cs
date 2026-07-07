/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Dedicated "On Agent Finish" settings window. Opened from the consolidated
 *          Settings dialog via the "On Agent Finish..." button. Edits the global default
 *          config, plus an optional per-solution override keyed by solution name: when the
 *          "Use custom settings for this solution" box is checked, the fields edit (and on
 *          OK persist) a per-project AgentFinishConfig that takes precedence over the global
 *          default for that solution; unchecked, the fields edit the global default.
 *
 * *******************************************************************************************************************/

using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region On Agent Finish Dialog

        /// <summary>
        /// Canned texts offered by the follow-up field's "Preset" button (label, text sent to the
        /// agent). Picking one overwrites the field rather than appending, since a follow-up is a
        /// single message.
        /// </summary>
        private static readonly (string, string)[] FollowUpPresets =
        {
            ("Commit and push", "Commit and push the changes"),
            ("Commit and push (no AI credit)",
                "Commit and push code changes but keep only the human author and no references to AI model"),
        };

        /// <summary>
        /// Builds and shows the dedicated "On Agent Finish" settings window. Persists the
        /// global default and, when the per-solution override box is checked, the current
        /// solution's override on OK. Returns after the modal closes.
        /// </summary>
        private async System.Threading.Tasks.Task ShowAgentFinishSettingsDialogAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_settings == null) _settings = new ClaudeCodeSettings();
            if (_settings.AgentFinish == null) _settings.AgentFinish = new AgentFinishConfig();
            if (_settings.ProjectAgentFinish == null)
                _settings.ProjectAgentFinish = new System.Collections.Generic.Dictionary<string, AgentFinishConfig>(StringComparer.OrdinalIgnoreCase);

            GetThemeBrushes(out Brush themeBg, out Brush themeFg);

            string solutionName = GetCurrentSolutionName();
            bool hasSolution = !string.IsNullOrEmpty(solutionName);

            // Working copies the controls edit in memory; persisted on OK.
            AgentFinishConfig workingGlobal = CloneAgentFinish(_settings.AgentFinish);
            AgentFinishConfig workingProject =
                hasSolution && _settings.ProjectAgentFinish.TryGetValue(solutionName, out var existing) && existing != null
                    ? CloneAgentFinish(existing)
                    : null;

            // Start editing the project config when one already exists for this solution.
            bool editingProject = workingProject != null;

            var dialog = new Window
            {
                Title = "On Agent Finish",
                Width = 520,
                Height = 700,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = themeBg,
                Foreground = themeFg,
                ShowInTaskbar = false
            };
            try { dialog.Owner = Application.Current?.MainWindow; } catch { }

            var rootGrid = new Grid { Margin = new Thickness(14) };
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(scroll, 0);
            rootGrid.Children.Add(scroll);

            var stack = new StackPanel { Orientation = Orientation.Vertical };
            scroll.Content = stack;

            // ---- Per-solution override ----
            stack.Children.Add(MakeSectionHeader("Scope", themeFg));

            var projectCheck = MakeCheckBox(
                hasSolution
                    ? $"Use custom settings for this solution ({solutionName})"
                    : "Use custom settings for this solution",
                "When enabled, these settings apply only to the current solution and override the global defaults. When disabled, this solution uses the global defaults below.",
                editingProject, themeFg);
            projectCheck.IsEnabled = hasSolution;
            projectCheck.Opacity = hasSolution ? 1.0 : 0.5;
            stack.Children.Add(projectCheck);

            stack.Children.Add(new TextBlock
            {
                Text = hasSolution
                    ? "Unchecked: edit the global defaults used by every solution without its own settings."
                    : "Open a solution to configure per-solution settings. These fields edit the global defaults.",
                FontSize = 11,
                Opacity = 0.7,
                Foreground = themeFg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20, 0, 0, 4)
            });

            // ---- On Agent Finish fields ----
            stack.Children.Add(MakeSectionHeader("On Agent Finish", themeFg));

            var afEnabledCheck = MakeCheckBox(
                "Notify / run an action when the agent finishes",
                "When the agent stops working (the terminal goes idle), optionally play a sound, show a Visual Studio notification (with how long it took, plus token count for Claude Code), and run an action.",
                false, themeFg);
            stack.Children.Add(afEnabledCheck);

            stack.Children.Add(new TextBlock
            {
                Text = "Works with the Command Prompt terminal (not Windows Terminal). Detected by watching the terminal go idle, so it covers any agent.",
                FontSize = 11,
                Opacity = 0.7,
                Foreground = themeFg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20, 0, 0, 4)
            });

            var afSoundCheck = MakeCheckBox("Play a sound",
                "Play a system sound when the agent finishes.", false, themeFg);
            afSoundCheck.Margin = new Thickness(20, 4, 0, 4);
            stack.Children.Add(afSoundCheck);

            var afQuestionSoundCheck = MakeCheckBox("Play a different sound when the agent asks a question",
                "Play a distinct sound when the agent stops and waits for your answer (a yes/no confirmation or a selection menu) instead of finishing. Uses a different tone than the finish sound so you can tell them apart.",
                false, themeFg);
            afQuestionSoundCheck.Margin = new Thickness(20, 4, 0, 4);
            stack.Children.Add(afQuestionSoundCheck);

            var afToastCheck = MakeCheckBox("Show a notification",
                "Show a Visual Studio info bar with the turn's duration and token count when the agent finishes.",
                false, themeFg);
            afToastCheck.Margin = new Thickness(20, 4, 0, 4);
            stack.Children.Add(afToastCheck);

            stack.Children.Add(new TextBlock
            {
                Text = "Action:",
                Foreground = themeFg,
                Margin = new Thickness(20, 6, 0, 2)
            });

            var afActionCombo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                MinWidth = 360,
                MinHeight = 26,
                Margin = new Thickness(20, 0, 0, 4)
            };
            var afComboRes = BuildThemedComboResources(themeBg, themeFg);
            afActionCombo.Style = (Style)afComboRes["cb"];
            var afItemStyle = (Style)afComboRes["cbi"];

            Func<string, AgentFinishActionType, ComboBoxItem> mkActionItem = (txt, val) =>
                new ComboBoxItem { Content = txt, Tag = val, Style = afItemStyle };
            afActionCombo.Items.Add(mkActionItem("None (notify only)", AgentFinishActionType.None));
            afActionCombo.Items.Add(mkActionItem("Build solution", AgentFinishActionType.BuildSolution));
            afActionCombo.Items.Add(mkActionItem("Rebuild solution", AgentFinishActionType.RebuildSolution));
            afActionCombo.Items.Add(mkActionItem("Run (F5)", AgentFinishActionType.Run));
            afActionCombo.Items.Add(mkActionItem("Run without debugging (Ctrl+F5)", AgentFinishActionType.RunWithoutDebugging));
            afActionCombo.Items.Add(mkActionItem("Run all tests", AgentFinishActionType.RunTests));
            afActionCombo.Items.Add(mkActionItem("Run a script…", AgentFinishActionType.RunScript));
            afActionCombo.Items.Add(mkActionItem("Send a command to the agent", AgentFinishActionType.SendToAgent));
            stack.Children.Add(afActionCombo);

            stack.Children.Add(new TextBlock
            {
                Text = "Script path (for \"Run a script\") or command text (for \"Send a command\"):",
                Foreground = themeFg,
                Margin = new Thickness(20, 4, 0, 2)
            });
            var afScriptBox = new TextBox
            {
                Background = themeBg,
                Foreground = themeFg,
                BorderBrush = themeFg,
                Margin = new Thickness(20, 0, 0, 4)
            };
            stack.Children.Add(afScriptBox);

            var afAutoCloseScriptCheck = MakeCheckBox("Close script window when it finishes",
                "When enabled, script windows close automatically after the script exits. When disabled, they stay open so you can read the output.",
                false, themeFg);
            afAutoCloseScriptCheck.Margin = new Thickness(20, 4, 0, 4);
            stack.Children.Add(afAutoCloseScriptCheck);

            void SyncScriptActionOptions()
            {
                bool isRunScript = (afActionCombo.SelectedItem as ComboBoxItem)?.Tag is AgentFinishActionType act
                    && act == AgentFinishActionType.RunScript;
                afAutoCloseScriptCheck.IsEnabled = isRunScript;
                afAutoCloseScriptCheck.Opacity = isRunScript ? 1.0 : 0.5;
            }
            afActionCombo.SelectionChanged += (s, e) => SyncScriptActionOptions();

            var afCleanBeforeRunCheck = MakeCheckBox("Clean solution before running",
                "When enabled, Run and Run without debugging clean the solution before launching.",
                true, themeFg);
            afCleanBeforeRunCheck.Margin = new Thickness(20, 4, 0, 4);
            stack.Children.Add(afCleanBeforeRunCheck);

            var afRebuildBeforeRunCheck = MakeCheckBox("Rebuild solution before running",
                "When enabled, Run and Run without debugging rebuild the solution before launching. If the rebuild fails, launching is skipped.",
                true, themeFg);
            afRebuildBeforeRunCheck.Margin = new Thickness(20, 4, 0, 4);
            stack.Children.Add(afRebuildBeforeRunCheck);

            void SyncRunActionOptions()
            {
                bool isRunAction = (afActionCombo.SelectedItem as ComboBoxItem)?.Tag is AgentFinishActionType act
                    && (act == AgentFinishActionType.Run || act == AgentFinishActionType.RunWithoutDebugging);
                afCleanBeforeRunCheck.IsEnabled = isRunAction;
                afRebuildBeforeRunCheck.IsEnabled = isRunAction;
                afCleanBeforeRunCheck.Opacity = isRunAction ? 1.0 : 0.5;
                afRebuildBeforeRunCheck.Opacity = isRunAction ? 1.0 : 0.5;
            }
            afActionCombo.SelectionChanged += (s, e) => SyncRunActionOptions();

            stack.Children.Add(new TextBlock
            {
                Text = "Also send to agent after the action succeeds (optional):",
                Foreground = themeFg,
                Margin = new Thickness(20, 6, 0, 2)
            });
            var afFollowUpBox = new TextBox
            {
                Background = themeBg,
                Foreground = themeFg,
                BorderBrush = themeFg,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            var afFollowUpPresetButton = new Button
            {
                Content = "Preset ▾",
                Width = 90,
                Height = 24,
                Margin = new Thickness(6, 0, 0, 0)
            };
            Style afPresetButtonStyle = GetDialogButtonStyle();
            if (afPresetButtonStyle != null) afFollowUpPresetButton.Style = afPresetButtonStyle;
            else { afFollowUpPresetButton.Background = themeBg; afFollowUpPresetButton.Foreground = themeFg; afFollowUpPresetButton.BorderBrush = themeFg; }

            var afFollowUpPresetMenu = new ContextMenu();
            foreach (var preset in FollowUpPresets)
            {
                var item = new MenuItem { Header = preset.Item1 };
                item.Click += (s, ea) => { afFollowUpBox.Text = preset.Item2; };
                afFollowUpPresetMenu.Items.Add(item);
            }
            afFollowUpPresetButton.Click += (s, ea) =>
            {
                afFollowUpPresetMenu.PlacementTarget = afFollowUpPresetButton;
                afFollowUpPresetMenu.IsOpen = true;
            };

            var afFollowUpRow = new Grid { Margin = new Thickness(20, 0, 0, 4) };
            afFollowUpRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            afFollowUpRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(afFollowUpBox, 0);
            Grid.SetColumn(afFollowUpPresetButton, 1);
            afFollowUpRow.Children.Add(afFollowUpBox);
            afFollowUpRow.Children.Add(afFollowUpPresetButton);
            stack.Children.Add(afFollowUpRow);

            stack.Children.Add(new TextBlock
            {
                Text = "e.g. \"Commit and push the changes\", or pick a preset. Skipped if the action fails (build/rebuild errors) or is skipped. Not available for \"Send a command to the agent\", which already sends text.",
                FontSize = 11,
                Opacity = 0.7,
                Foreground = themeFg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20, 0, 0, 4)
            });

            void SyncFollowUpOptions()
            {
                bool canFollowUp = (afActionCombo.SelectedItem as ComboBoxItem)?.Tag is AgentFinishActionType act
                    && act != AgentFinishActionType.None && act != AgentFinishActionType.SendToAgent;
                afFollowUpBox.IsEnabled = canFollowUp;
                afFollowUpBox.Opacity = canFollowUp ? 1.0 : 0.5;
                afFollowUpPresetButton.IsEnabled = canFollowUp;
            }
            afActionCombo.SelectionChanged += (s, e) => SyncFollowUpOptions();

            var afConfirmCheck = MakeCheckBox("Ask before running the action",
                "When enabled, the action appears as a button on the notification and runs only when you click it. When disabled, the action runs automatically. Recommended on for scripts, run, and rebuild.",
                false, themeFg);
            afConfirmCheck.Margin = new Thickness(20, 4, 0, 4);
            stack.Children.Add(afConfirmCheck);

            var afReqChangesCheck = MakeCheckBox("Only run the action if files changed",
                "Skip the action when the agent did not modify any files (git working tree clean). Has no effect outside a git repository.",
                false, themeFg);
            afReqChangesCheck.Margin = new Thickness(20, 4, 0, 4);
            stack.Children.Add(afReqChangesCheck);

            var afIdlePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(20, 4, 0, 4)
            };
            afIdlePanel.Children.Add(new TextBlock
            {
                Text = "Idle seconds before \"finished\" is detected:",
                Foreground = themeFg,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            var afIdleBox = new TextBox
            {
                Width = 56,
                Background = themeBg,
                Foreground = themeFg,
                BorderBrush = themeFg,
                VerticalAlignment = VerticalAlignment.Center
            };
            afIdlePanel.Children.Add(afIdleBox);
            stack.Children.Add(afIdlePanel);

            // ---- Field <-> config helpers ----
            void WriteFrom(AgentFinishConfig cfg)
            {
                afEnabledCheck.IsChecked    = cfg.Enabled;
                afSoundCheck.IsChecked      = cfg.PlaySound;
                afQuestionSoundCheck.IsChecked = cfg.PlayQuestionSound;
                afToastCheck.IsChecked      = cfg.ShowToast;
                afConfirmCheck.IsChecked    = cfg.Confirm;
                afReqChangesCheck.IsChecked = cfg.RequireFileChanges;
                afAutoCloseScriptCheck.IsChecked = cfg.AutoCloseScript;
                afCleanBeforeRunCheck.IsChecked = cfg.CleanBeforeRun;
                afRebuildBeforeRunCheck.IsChecked = cfg.RebuildBeforeRun;
                afScriptBox.Text            = cfg.ScriptOrCommand ?? string.Empty;
                afFollowUpBox.Text          = cfg.FollowUpSendToAgent ?? string.Empty;
                afIdleBox.Text              = cfg.IdleSeconds.ToString();
                afActionCombo.SelectedItem  = null;
                foreach (ComboBoxItem it in afActionCombo.Items)
                {
                    if ((AgentFinishActionType)it.Tag == cfg.Action) { afActionCombo.SelectedItem = it; break; }
                }
                if (afActionCombo.SelectedItem == null) afActionCombo.SelectedIndex = 0;
                SyncScriptActionOptions();
                SyncRunActionOptions();
                SyncFollowUpOptions();
            }

            void ReadInto(AgentFinishConfig cfg)
            {
                cfg.Enabled            = afEnabledCheck.IsChecked == true;
                cfg.PlaySound          = afSoundCheck.IsChecked == true;
                cfg.PlayQuestionSound  = afQuestionSoundCheck.IsChecked == true;
                cfg.ShowToast          = afToastCheck.IsChecked == true;
                cfg.Confirm            = afConfirmCheck.IsChecked == true;
                cfg.RequireFileChanges = afReqChangesCheck.IsChecked == true;
                cfg.AutoCloseScript    = afAutoCloseScriptCheck.IsChecked == true;
                cfg.CleanBeforeRun     = afCleanBeforeRunCheck.IsChecked == true;
                cfg.RebuildBeforeRun   = afRebuildBeforeRunCheck.IsChecked == true;
                cfg.ScriptOrCommand    = afScriptBox.Text?.Trim() ?? string.Empty;
                cfg.FollowUpSendToAgent = afFollowUpBox.Text?.Trim() ?? string.Empty;
                if ((afActionCombo.SelectedItem as ComboBoxItem)?.Tag is AgentFinishActionType act)
                    cfg.Action = act;
                if (int.TryParse(afIdleBox.Text?.Trim(), out int idleVal))
                    cfg.IdleSeconds = Math.Max(2, Math.Min(120, idleVal));
            }

            // Initial population from whichever config is active.
            WriteFrom(editingProject ? workingProject : workingGlobal);

            // Toggle: persist the current control values into the config being edited,
            // switch the target, seeding a brand-new project config from the global one.
            projectCheck.Checked += (s, e) =>
            {
                ReadInto(workingGlobal);
                if (workingProject == null) workingProject = CloneAgentFinish(workingGlobal);
                editingProject = true;
                WriteFrom(workingProject);
            };
            projectCheck.Unchecked += (s, e) =>
            {
                if (workingProject != null) ReadInto(workingProject);
                editingProject = false;
                WriteFrom(workingGlobal);
            };

            // ---- Button row ----
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            Grid.SetRow(buttonPanel, 1);

            Style buttonStyle = GetDialogButtonStyle();

            var okButton = new Button
            {
                Content = "OK",
                Width = 80,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 80,
                Height = 28,
                IsCancel = true
            };
            if (buttonStyle != null)
            {
                okButton.Style = buttonStyle;
                cancelButton.Style = buttonStyle;
            }
            else
            {
                okButton.Background = themeBg; okButton.Foreground = themeFg; okButton.BorderBrush = themeFg;
                cancelButton.Background = themeBg; cancelButton.Foreground = themeFg; cancelButton.BorderBrush = themeFg;
            }
            okButton.Click += (s, ea) => dialog.DialogResult = true;
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            rootGrid.Children.Add(buttonPanel);

            dialog.Content = rootGrid;

            if (dialog.ShowDialog() != true)
            {
                return; // Cancel — nothing persisted
            }

            // ---- Persist ----
            // Flush current control values into the config currently being edited.
            if (editingProject)
            {
                if (workingProject == null) workingProject = CloneAgentFinish(workingGlobal);
                ReadInto(workingProject);
            }
            else
            {
                ReadInto(workingGlobal);
            }

            // Global default always written back (it may have been edited before toggling).
            _settings.AgentFinish = workingGlobal;

            if (projectCheck.IsChecked == true && hasSolution)
            {
                _settings.ProjectAgentFinish[solutionName] = workingProject ?? CloneAgentFinish(workingGlobal);
            }
            else if (hasSolution)
            {
                _settings.ProjectAgentFinish.Remove(solutionName);
            }

            SaveSettings();

            // If the agent is mid-turn, apply the just-saved settings to the running watch so they
            // take effect when this turn finishes rather than only on the next prompt.
            RefreshWatchedAgentFinishConfig();
        }

        /// <summary>
        /// Returns a deep copy of an <see cref="AgentFinishConfig"/> so the dialog can
        /// edit working copies without mutating the live settings until OK.
        /// </summary>
        private static AgentFinishConfig CloneAgentFinish(AgentFinishConfig src)
        {
            src = src ?? new AgentFinishConfig();
            return new AgentFinishConfig
            {
                Enabled            = src.Enabled,
                PlaySound          = src.PlaySound,
                PlayQuestionSound  = src.PlayQuestionSound,
                ShowToast          = src.ShowToast,
                IdleSeconds        = src.IdleSeconds,
                Action             = src.Action,
                ScriptOrCommand    = src.ScriptOrCommand,
                AutoCloseScript    = src.AutoCloseScript,
                CleanBeforeRun     = src.CleanBeforeRun,
                RebuildBeforeRun   = src.RebuildBeforeRun,
                RequireFileChanges = src.RequireFileChanges,
                Confirm            = src.Confirm,
                FollowUpSendToAgent = src.FollowUpSendToAgent
            };
        }

        #endregion
    }
}
