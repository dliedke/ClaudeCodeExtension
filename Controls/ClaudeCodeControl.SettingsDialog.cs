/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Consolidated Settings dialog. Groups the previously scattered toggles
 *          (Send with Enter, Send large prompts as file, Auto-open Changes,
 *          Invert Layout, Disable Auto Zoom, Terminal Type, Theme, plus the
 *          new "skip theme restart prompt" opt-out) under a single screen
 *          accessible from the ⚙ menu's "Settings..." entry.
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
        #region Settings Dialog Entry Point

        /// <summary>
        /// Handles the "Settings..." menu item click. Opens the consolidated
        /// settings dialog and applies any changes the user confirmed.
        /// </summary>
#pragma warning disable VSTHRD100 // async void is acceptable for event handlers
        private async void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_settings == null) _settings = new ClaudeCodeSettings();

            try
            {
                await ShowConsolidatedSettingsDialogAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error opening Settings dialog: {ex.Message}");
                MessageBox.Show($"Error opening Settings dialog: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Settings Dialog UI

        /// <summary>
        /// Builds and shows the consolidated settings dialog, then applies the
        /// chosen values. Restart-requiring changes (terminal type, theme)
        /// trigger a single terminal restart at the end if needed.
        /// </summary>
        private async System.Threading.Tasks.Task ShowConsolidatedSettingsDialogAsync()
        {
            GetThemeBrushes(out Brush themeBg, out Brush themeFg);

            // Snapshot the current values so we can detect what changed on OK.
            bool origSendWithEnter            = _settings.SendWithEnter;
            bool origSendLargeAsFile          = _settings.SendLargePromptsAsFile;
            bool origDisableClipboardSend     = _settings.DisableClipboardSend;
            bool origAutoOpenChanges          = _settings.AutoOpenChangesOnPrompt;
            bool origInvertLayout             = _settings.InvertLayout;
            LayoutOrientation origOrientation = _settings.SelectedLayoutOrientation;
            bool origDisableAutoZoom          = _settings.DisableStartupAutoZoom;
            TerminalType origTerminalType     = _settings.SelectedTerminalType;
            ThemePreference origThemePref     = _settings.SelectedThemePreference;
            bool origSkipThemePrompt          = _settings.SkipThemeRestartPrompt;
            bool origDisableBringToFront    = _settings.DisableBringToForeground;

            var dialog = new Window
            {
                Title = "Claude Code Extension - Settings",
                Width = 520,
                Height = 680,
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

            // ---- Behavior section ----
            stack.Children.Add(MakeSectionHeader("Behavior", themeFg));

            var sendEnterCheck = MakeCheckBox(
                "Send with Enter",
                "When enabled, Enter sends the prompt (Shift+Enter / Ctrl+Enter for newlines). When disabled, Enter inserts a newline and the Send button appears.",
                origSendWithEnter, themeFg);
            stack.Children.Add(sendEnterCheck);

            var largeAsFileCheck = MakeCheckBox(
                "Send large prompts as file",
                "When enabled, prompts above ~1 KB are saved to a temp file and only the file path is sent. Avoids paste truncation of large content.",
                origSendLargeAsFile, themeFg);
            stack.Children.Add(largeAsFileCheck);

            var disableClipboardCheck = MakeCheckBox(
                "Disable clipboard (type prompts instead of pasting)",
                "When enabled, the clipboard is never used to send a prompt. The prompt is saved to a temp file and only a short file reference is typed into the terminal via simulated keystrokes. Use this if another app (clipboard manager, Remote Desktop, security tool) holds the clipboard and breaks normal paste-based sending.\n\nAvailable only with the Command Prompt terminal type — Windows Terminal does not accept the simulated keystrokes this uses.",
                origDisableClipboardSend, themeFg);
            stack.Children.Add(disableClipboardCheck);

            // Hint shown only while Windows Terminal is selected, explaining why the toggle is greyed out.
            var disableClipboardWtHint = new TextBlock
            {
                Text = "Not available with Windows Terminal (works only with Command Prompt).",
                FontSize = 11,
                Opacity = 0.7,
                Foreground = themeFg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20, 0, 0, 0)
            };
            stack.Children.Add(disableClipboardWtHint);

            // Auto-open Changes only applies inside git repos, but we keep the
            // checkbox visible so users can pre-toggle the setting before
            // opening a git-tracked solution. The label hints at that.
            var autoOpenCheck = MakeCheckBox(
                "Auto-open Changes on Send",
                "Automatically open the Changes view, expand files, and enable auto-scroll when a prompt is sent. Only applies when the project is in a git repository.",
                origAutoOpenChanges, themeFg);
            stack.Children.Add(autoOpenCheck);

            var disableBringToFrontCheck = MakeCheckBox(
                "Don't bring Visual Studio to the foreground on terminal click",
                "When enabled, clicking the embedded terminal no longer pulls the entire Visual Studio window to the front. Useful when you overlap multiple VS instances or other apps and want to interact with the terminal without rearranging your window layout.",
                origDisableBringToFront, themeFg);
            stack.Children.Add(disableBringToFrontCheck);

            // ---- On Agent Finish section ----
            stack.Children.Add(MakeSectionHeader("On Agent Finish", themeFg));

            stack.Children.Add(new TextBlock
            {
                Text = "Notify and optionally run an action when the agent finishes. Supports global defaults plus per-solution overrides.",
                FontSize = 11,
                Opacity = 0.7,
                Foreground = themeFg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 0, 0, 6)
            });

            var afOpenButton = new Button
            {
                Content = "On Agent Finish…",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(4, 0, 0, 4)
            };
            Style afButtonStyle = GetDialogButtonStyle();
            if (afButtonStyle != null) afOpenButton.Style = afButtonStyle;
            else { afOpenButton.Background = themeBg; afOpenButton.Foreground = themeFg; afOpenButton.BorderBrush = themeFg; }
#pragma warning disable VSTHRD110
            afOpenButton.Click += (s, ea) => _ = ShowAgentFinishSettingsDialogAsync();
#pragma warning restore VSTHRD110
            stack.Children.Add(afOpenButton);

            // ---- Layout section ----
            stack.Children.Add(MakeSectionHeader("Layout", themeFg));

            stack.Children.Add(new TextBlock
            {
                Text = "Where the prompt panel (input box and usage bars) is docked relative to the terminal.",
                FontSize = 11,
                Opacity = 0.7,
                Foreground = themeFg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 0, 0, 4)
            });

            // Map the current orientation + invert to one of four positions.
            bool origVertical = origOrientation == LayoutOrientation.Vertical;
            var topRadio = MakeRadioButton("Top (default) — prompt above, terminal below",
                !origVertical && !origInvertLayout, themeFg, "promptPosition");
            var bottomRadio = MakeRadioButton("Bottom — terminal above, prompt below",
                !origVertical && origInvertLayout, themeFg, "promptPosition");
            var leftRadio = MakeRadioButton("Left — prompt on the left, terminal on the right",
                origVertical && !origInvertLayout, themeFg, "promptPosition");
            var rightRadio = MakeRadioButton("Right — terminal on the left, prompt on the right",
                origVertical && origInvertLayout, themeFg, "promptPosition");
            stack.Children.Add(topRadio);
            stack.Children.Add(bottomRadio);
            stack.Children.Add(leftRadio);
            stack.Children.Add(rightRadio);

            var disableAutoZoomCheck = MakeCheckBox(
                "Disable Auto Zoom on Startup",
                "Skip the automatic terminal zoom-out and saved zoom-delta replay performed after each terminal start. Manual Ctrl+Scroll zoom still works.",
                origDisableAutoZoom, themeFg);
            stack.Children.Add(disableAutoZoomCheck);

            // ---- Terminal Type section ----
            stack.Children.Add(MakeSectionHeader("Terminal Type", themeFg));

            var cmdRadio = MakeRadioButton("Command Prompt (default)",
                origTerminalType == TerminalType.CommandPrompt, themeFg, "terminalType");
            var wtRadio = MakeRadioButton("Windows Terminal (better emoji/unicode support)",
                origTerminalType == TerminalType.WindowsTerminal, themeFg, "terminalType");
            stack.Children.Add(cmdRadio);
            stack.Children.Add(wtRadio);
            stack.Children.Add(new TextBlock
            {
                Text = "Note: Windows Terminal must be installed (winget install Microsoft.WindowsTerminal).",
                FontSize = 11,
                Opacity = 0.7,
                Foreground = themeFg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20, 2, 0, 0)
            });

            // "Disable clipboard" relies on simulated keystrokes that only conhost (Command Prompt)
            // accepts, so the toggle is enabled only while Command Prompt is selected. Keep it in sync
            // with the terminal-type radios live, and uncheck it when switching to Windows Terminal so
            // an unavailable setting can't be saved as enabled.
            void SyncDisableClipboardAvailability()
            {
                bool cmdSelected = cmdRadio.IsChecked == true;
                disableClipboardCheck.IsEnabled = cmdSelected;
                disableClipboardCheck.Opacity = cmdSelected ? 1.0 : 0.5;
                disableClipboardWtHint.Visibility = cmdSelected ? Visibility.Collapsed : Visibility.Visible;
                if (!cmdSelected)
                {
                    disableClipboardCheck.IsChecked = false;
                }
            }
            cmdRadio.Checked += (s, e) => SyncDisableClipboardAvailability();
            wtRadio.Checked += (s, e) => SyncDisableClipboardAvailability();
            SyncDisableClipboardAvailability();

            // ---- Theme section ----
            stack.Children.Add(MakeSectionHeader("Theme", themeFg));

            var autoRadio = MakeRadioButton("Automatic (follow Visual Studio theme)",
                origThemePref == ThemePreference.Automatic, themeFg, "themePref");
            var darkRadio = MakeRadioButton("Dark",
                origThemePref == ThemePreference.Dark, themeFg, "themePref");
            var lightRadio = MakeRadioButton("Light",
                origThemePref == ThemePreference.Light, themeFg, "themePref");
            stack.Children.Add(autoRadio);
            stack.Children.Add(darkRadio);
            stack.Children.Add(lightRadio);

            var skipPromptCheck = MakeCheckBox(
                "Don't ask to restart the AI agent when the theme changes",
                "Suppresses the \"Theme changed. Restart the AI code agent?\" pop-up. Useful when Visual Studio automatically switches themes (for example, the debugging theme triggered by F5).",
                origSkipThemePrompt, themeFg);
            skipPromptCheck.Margin = new Thickness(20, 6, 0, 0);
            stack.Children.Add(skipPromptCheck);

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
                // Cancel - no changes applied
                return;
            }

            // ---- Collect new values ----
            bool newSendWithEnter   = sendEnterCheck.IsChecked == true;
            bool newSendLargeAsFile = largeAsFileCheck.IsChecked == true;
            bool newDisableClipboardSend = disableClipboardCheck.IsChecked == true;
            bool newAutoOpenChanges = autoOpenCheck.IsChecked == true;
            bool newDisableBringToFront = disableBringToFrontCheck.IsChecked == true;
            // Map the selected position back to orientation + invert.
            bool newVertical = leftRadio.IsChecked == true || rightRadio.IsChecked == true;
            bool newInvertLayout = bottomRadio.IsChecked == true || rightRadio.IsChecked == true;
            LayoutOrientation newOrientation = newVertical
                ? LayoutOrientation.Vertical
                : LayoutOrientation.Horizontal;
            bool newDisableAutoZoom = disableAutoZoomCheck.IsChecked == true;
            TerminalType newTerminalType = wtRadio.IsChecked == true
                ? TerminalType.WindowsTerminal
                : TerminalType.CommandPrompt;
            ThemePreference newThemePref =
                darkRadio.IsChecked  == true ? ThemePreference.Dark  :
                lightRadio.IsChecked == true ? ThemePreference.Light :
                                               ThemePreference.Automatic;
            bool newSkipThemePrompt = skipPromptCheck.IsChecked == true;

            // ---- Validate Windows Terminal availability before persisting ----
            if (newTerminalType == TerminalType.WindowsTerminal &&
                newTerminalType != origTerminalType)
            {
                bool wtAvailable = await IsWindowsTerminalAvailableAsync();
                if (!wtAvailable)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    MessageBox.Show(
                        "Windows Terminal (wt.exe) was not found in PATH.\n\n" +
                        "To install, open Command Prompt as Administrator and run:\n\n" +
                        "    winget install --id Microsoft.WindowsTerminal -e\n\n" +
                        "After installing, restart Visual Studio and try again.\n\n" +
                        "Reverting Terminal Type to Command Prompt.",
                        "Windows Terminal Not Found",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    newTerminalType = TerminalType.CommandPrompt;
                }
            }

            // ---- Apply settings ----
            _settings.SendWithEnter           = newSendWithEnter;
            _settings.SendLargePromptsAsFile  = newSendLargeAsFile;
            _settings.DisableClipboardSend    = newDisableClipboardSend;
            _settings.AutoOpenChangesOnPrompt = newAutoOpenChanges;
            _settings.DisableBringToForeground = newDisableBringToFront;
            _settings.InvertLayout            = newInvertLayout;
            _settings.SelectedLayoutOrientation = newOrientation;
            _settings.DisableStartupAutoZoom  = newDisableAutoZoom;
            _settings.SelectedTerminalType    = newTerminalType;
            _settings.SelectedThemePreference = newThemePref;
            _settings.SkipThemeRestartPrompt  = newSkipThemePrompt;

            // On Agent Finish is configured in its own dialog (opened by the button above),
            // which persists its own changes; nothing to apply here.

            // Send button visibility tied to SendWithEnter
            SendPromptButton.Visibility = _settings.SendWithEnter
                ? Visibility.Collapsed
                : Visibility.Visible;

            // Layout change (position and/or orientation)
            if (newInvertLayout != origInvertLayout || newOrientation != origOrientation)
            {
                ApplyLayoutSettingsChange();
            }

            // Theme change: re-paint panel and inline bars immediately
            bool themeChanged = newThemePref != origThemePref;
            if (themeChanged)
            {
                UpdateTerminalTheme();
                UpdateInlineUsageBarColors();
            }

            SaveSettings();

            // ---- Restart-requiring changes ----
            bool terminalTypeChanged = newTerminalType != origTerminalType;
            bool needsRestart = terminalTypeChanged;

            // For theme changes, ask the user (respecting the skip-prompt opt-out
            // and the same "agent color already matches" short-circuit used elsewhere)
            if (themeChanged && !needsRestart)
            {
                bool terminalRunning = terminalHandle != IntPtr.Zero && IsWindow(terminalHandle);
                bool colorAlreadyMatches = terminalPanel != null
                    && _terminalAgentColor != System.Drawing.Color.Empty
                    && terminalPanel.BackColor == _terminalAgentColor;

                if (terminalRunning && !colorAlreadyMatches && !newSkipThemePrompt)
                {
                    var result = MessageBox.Show(
                        "Theme preference changed. Restart the AI code agent to apply the new terminal colors?",
                        "Theme Changed",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    if (result == MessageBoxResult.Yes)
                    {
                        needsRestart = true;
                    }
                }
            }

            if (needsRestart)
            {
                try
                {
                    await RestartTerminalWithSelectedProviderAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error restarting terminal after settings change: {ex.Message}");
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    MessageBox.Show($"Failed to restart terminal: {ex.Message}",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        #endregion

        #region Settings Dialog Helpers

        private static TextBlock MakeSectionHeader(string text, Brush fg)
        {
            return new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Foreground = fg,
                Margin = new Thickness(0, 12, 0, 6)
            };
        }

        private static CheckBox MakeCheckBox(string label, string tooltip, bool isChecked, Brush fg)
        {
            return new CheckBox
            {
                Content = label,
                IsChecked = isChecked,
                Foreground = fg,
                Margin = new Thickness(4, 4, 0, 4),
                ToolTip = tooltip
            };
        }

        private static RadioButton MakeRadioButton(string label, bool isChecked, Brush fg, string groupName)
        {
            return new RadioButton
            {
                Content = label,
                IsChecked = isChecked,
                GroupName = groupName,
                Foreground = fg,
                Margin = new Thickness(4, 3, 0, 3)
            };
        }

        /// <summary>
        /// Parses the custom flat ComboBox + ComboBoxItem templates with the VS theme colors
        /// injected, returning a dictionary with the "cb" (ComboBox) and "cbi" (ComboBoxItem)
        /// styles. A standalone dialog doesn't inherit VS's themed ComboBox styling and the
        /// default templates paint their own system selection/hover, so we replace the templates
        /// outright. The hover/selection background is derived from the theme background (via
        /// <see cref="ComputeAtHoverBrush"/>) so it stays readable in dark and light themes.
        /// </summary>
        private ResourceDictionary BuildThemedComboResources(Brush bg, Brush fg)
        {
            string bgHex = ((bg as SolidColorBrush)?.Color ?? Colors.Black).ToString();
            string fgHex = ((fg as SolidColorBrush)?.Color ?? Colors.White).ToString();
            string hoverHex = ((ComputeAtHoverBrush(bg) as SolidColorBrush)?.Color ?? Colors.Gray).ToString();

            string xaml = ComboBoxTemplateXaml
                .Replace("__BG__", bgHex)
                .Replace("__FG__", fgHex)
                .Replace("__HOVER__", hoverHex)
                .Replace("__BORDER__", hoverHex);

            return (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml);
        }

        // Flat ComboBox / ComboBoxItem templates. Single-quoted attributes so the whole thing can
        // live in a verbatim C# string without escaping. Color tokens are substituted at runtime.
        private const string ComboBoxTemplateXaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style x:Key='cbi' TargetType='ComboBoxItem'>
    <Setter Property='Foreground' Value='__FG__'/>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Padding' Value='6,3'/>
    <Setter Property='HorizontalContentAlignment' Value='Left'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ComboBoxItem'>
          <Border x:Name='bd' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>
            <ContentPresenter/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='__HOVER__'/>
            </Trigger>
            <Trigger Property='IsSelected' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='__HOVER__'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key='cb' TargetType='ComboBox'>
    <Setter Property='SnapsToDevicePixels' Value='True'/>
    <Setter Property='ItemContainerStyle' Value='{StaticResource cbi}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ComboBox'>
          <Grid>
            <ToggleButton x:Name='ToggleButton' Focusable='False' ClickMode='Press'
                IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>
              <ToggleButton.Template>
                <ControlTemplate TargetType='ToggleButton'>
                  <Border Background='__BG__' BorderBrush='__BORDER__' BorderThickness='1' SnapsToDevicePixels='True'>
                    <Grid>
                      <Grid.ColumnDefinitions>
                        <ColumnDefinition Width='*'/>
                        <ColumnDefinition Width='20'/>
                      </Grid.ColumnDefinitions>
                      <Path Grid.Column='1' HorizontalAlignment='Center' VerticalAlignment='Center'
                            Data='M0,0 L8,0 L4,5 Z' Fill='__FG__'/>
                    </Grid>
                  </Border>
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter IsHitTestVisible='False'
                Content='{TemplateBinding SelectionBoxItem}'
                ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'
                Margin='8,3,24,3' VerticalAlignment='Center' HorizontalAlignment='Left'
                TextElement.Foreground='__FG__'/>
            <Popup x:Name='PART_Popup' AllowsTransparency='True' Focusable='False'
                Placement='Bottom' PopupAnimation='Slide'
                IsOpen='{TemplateBinding IsDropDownOpen}'>
              <Border Background='__BG__' BorderBrush='__BORDER__' BorderThickness='1'
                  MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}'
                  SnapsToDevicePixels='True'>
                <ScrollViewer MaxHeight='320'>
                  <ItemsPresenter/>
                </ScrollViewer>
              </Border>
            </Popup>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";

        #endregion
    }
}
