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
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        /// chosen values. The dialog is organized into tabs (Behavior, Layout,
        /// Terminal, Theme, Usage). Restart-requiring changes (terminal type,
        /// theme) trigger a single terminal restart at the end if needed.
        /// </summary>
        private async System.Threading.Tasks.Task ShowConsolidatedSettingsDialogAsync()
        {
            GetThemeBrushes(out Brush themeBg, out Brush themeFg);
            ResourceDictionary comboRes = BuildThemedComboResources(themeBg, themeFg);
            ResourceDictionary tabRes = BuildThemedTabResources(themeBg, themeFg);

            // Snapshot the current values so we can detect what changed on OK.
            bool origSendWithEnter            = _settings.SendWithEnter;
            bool origSendWithCtrlEnter        = _settings.SendWithCtrlEnter;
            bool origSendLargeAsFile          = _settings.SendLargePromptsAsFile;
            bool origDisableClipboardSend     = _settings.DisableClipboardSend;
            bool origSendSelectionRefOnly     = _settings.SendSelectionReferenceOnly;
            bool origAutoSendBuildErrors      = _settings.AutoSendBuildErrorsToAgent;
            bool origAutoOpenChanges          = _settings.AutoOpenChangesOnPrompt;
            bool origInvertLayout             = _settings.InvertLayout;
            LayoutOrientation origOrientation = _settings.SelectedLayoutOrientation;
            bool origDisableAutoZoom          = _settings.DisableStartupAutoZoom;
            bool origHidePromptPanel          = _settings.HidePromptPanel;
            TerminalType origTerminalType     = _settings.SelectedTerminalType;
            string origConsoleFont            = string.IsNullOrWhiteSpace(_settings.ConsoleFontFaceName)
                                                ? "Cascadia Mono" : _settings.ConsoleFontFaceName;
            ThemePreference origThemePref     = _settings.SelectedThemePreference;
            int  origCustomColorArgb          = _settings.CustomThemeColorArgb;
            bool origSkipThemePrompt          = _settings.SkipThemeRestartPrompt;
            bool origShowInlineBars           = _settings.ShowInlineUsageBars;
            int  origAutoRefresh              = _settings.UsageAutoRefreshSeconds;
            int  origFontSize                 = (int)Math.Round(PromptTextBox?.FontSize ?? 12.0);
            if (origFontSize < 8) origFontSize = 12;
            if (origFontSize > 24) origFontSize = 24;
            var origVisibleToolbarButtons = new List<ToolbarButton>(
                _settings.VisibleToolbarButtons ?? new List<ToolbarButton>());
            var origToolbarOrder = GetEffectiveToolbarOrder();

            var dialog = new Window
            {
                Title = "Claude Code Extension - Settings",
                Width = 620,
                Height = 660,
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

            var tabs = new TabControl { Background = themeBg, BorderBrush = themeFg };
            if (tabRes["tabControl"] is Style tabCtrlStyle) tabs.Style = tabCtrlStyle;
            Grid.SetRow(tabs, 0);
            rootGrid.Children.Add(tabs);

            // Helper: build a scrollable tab page and return its content stack.
            StackPanel AddTab(string header)
            {
                var pageStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(12) };
                var pageScroll = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = pageStack
                };
                tabs.Items.Add(new TabItem { Header = header, Content = pageScroll });
                return pageStack;
            }

            // ========================= Behavior tab =========================
            var behaviorStack = AddTab("Behavior");

            behaviorStack.Children.Add(MakeSectionHeader("Send prompt with", themeFg));

            var sendEnterRadio = MakeRadioButton(
                "Enter — sends the prompt (Shift+Enter / Ctrl+Enter insert a newline)",
                origSendWithEnter, themeFg, "sendKey");
            var sendCtrlEnterRadio = MakeRadioButton(
                "Ctrl+Enter — sends the prompt (Enter inserts a newline)",
                !origSendWithEnter && origSendWithCtrlEnter, themeFg, "sendKey");
            var sendButtonRadio = MakeRadioButton(
                "Button only — Enter inserts a newline, click Send to submit",
                !origSendWithEnter && !origSendWithCtrlEnter, themeFg, "sendKey");
            sendCtrlEnterRadio.ToolTip =
                "Avoids accidentally sending an incomplete prompt with a stray Enter tap, while keeping a keyboard send shortcut (Ctrl+Enter).";
            behaviorStack.Children.Add(sendEnterRadio);
            behaviorStack.Children.Add(sendCtrlEnterRadio);
            behaviorStack.Children.Add(sendButtonRadio);

            behaviorStack.Children.Add(MakeSectionHeader("Prompt sending", themeFg));

            var largeAsFileCheck = MakeCheckBox(
                "Send large prompts as file",
                "When enabled, prompts above ~1 KB are saved to a temp file and only the file path is sent. Avoids paste truncation of large content.",
                origSendLargeAsFile, themeFg);
            behaviorStack.Children.Add(largeAsFileCheck);

            var disableClipboardCheck = MakeCheckBox(
                "Disable clipboard (type prompts instead of pasting)",
                "When enabled, the clipboard is never used to send a prompt. The prompt is saved to a temp file and only a short file reference is typed into the terminal via simulated keystrokes. Use this if another app (clipboard manager, Remote Desktop, security tool) holds the clipboard and breaks normal paste-based sending.\n\nAvailable only with the Command Prompt terminal type — Windows Terminal does not accept the simulated keystrokes this uses.",
                origDisableClipboardSend, themeFg);
            behaviorStack.Children.Add(disableClipboardCheck);

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
            behaviorStack.Children.Add(disableClipboardWtHint);

            var sendSelectionRefOnlyCheck = MakeCheckBox(
                "Send selection as reference only (no code)",
                "When enabled, \"Send Selection\" only inserts the file path and line numbers (e.g. \"File: foo.cs (lines 10-15)\"), without the selected code. The AI agent reads the file directly.",
                origSendSelectionRefOnly, themeFg);
            behaviorStack.Children.Add(sendSelectionRefOnlyCheck);

            // Auto-open Changes only applies inside git repos, but we keep the
            // checkbox visible so users can pre-toggle the setting before
            // opening a git-tracked solution. The label hints at that.
            var autoOpenCheck = MakeCheckBox(
                "Auto-open Changes on Send",
                "Automatically open the Changes view, expand files, and enable auto-scroll when a prompt is sent. Only applies when the project is in a git repository.",
                origAutoOpenChanges, themeFg);
            behaviorStack.Children.Add(autoOpenCheck);

            behaviorStack.Children.Add(MakeSectionHeader("Build errors", themeFg));

            var autoSendBuildErrorsCheck = MakeCheckBox(
                "Auto-send build errors to the agent",
                "When a Visual Studio build finishes with errors, automatically send the errors (and warnings, for context) to the active code agent so it can fix them. Only sends when an agent terminal is running and the build actually has errors.",
                origAutoSendBuildErrors, themeFg);
            behaviorStack.Children.Add(autoSendBuildErrorsCheck);

            // Prompt font size
            behaviorStack.Children.Add(MakeSectionHeader("Prompt font size", themeFg));
            behaviorStack.Children.Add(new TextBlock
            {
                Text = "Font size of the prompt input box (also adjustable with Ctrl+Scroll).",
                FontSize = 11,
                Opacity = 0.7,
                Foreground = themeFg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 0, 0, 4)
            });
            var fontSizeCombo = MakeThemedComboBox(comboRes, themeFg);
            fontSizeCombo.Width = 90;
            fontSizeCombo.HorizontalAlignment = HorizontalAlignment.Left;
            fontSizeCombo.Margin = new Thickness(4, 0, 0, 4);
            for (int pt = 8; pt <= 24; pt++)
            {
                var item = new ComboBoxItem { Content = pt + " pt", Tag = pt };
                if (comboRes["cbi"] is Style cbiStyle) item.Style = cbiStyle;
                if (pt == origFontSize) item.IsSelected = true;
                fontSizeCombo.Items.Add(item);
            }
            behaviorStack.Children.Add(fontSizeCombo);

            behaviorStack.Children.Add(MakeSectionHeader("On Agent Finish", themeFg));
            behaviorStack.Children.Add(new TextBlock
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
                Height = 32,
                MinWidth = 160,
                Padding = new Thickness(18, 0, 18, 0),
                Margin = new Thickness(4, 0, 0, 4)
            };
            Style afButtonStyle = GetDialogButtonStyle();
            if (afButtonStyle != null) afOpenButton.Style = afButtonStyle;
            else { afOpenButton.Background = themeBg; afOpenButton.Foreground = themeFg; afOpenButton.BorderBrush = themeFg; }
#pragma warning disable VSTHRD110
            afOpenButton.Click += (s, ea) => _ = ShowAgentFinishSettingsDialogAsync();
#pragma warning restore VSTHRD110
            behaviorStack.Children.Add(afOpenButton);

            // Under Windows Terminal the watcher attaches to the ConPTY console client resolved at
            // launch and reads the real screen buffer (UI Automation is only a fallback). The hint
            // below notes the WT support. Shown/hidden live by SyncAgentFinishAvailability().
            var afWtHint = new TextBlock
            {
                Text = "Windows Terminal is supported — detection reads the terminal's console buffer (with a UI Automation fallback) and may be slightly less reliable than Command Prompt.",
                FontSize = 11,
                Opacity = 0.7,
                Foreground = themeFg,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(4, 2, 0, 4)
            };
            behaviorStack.Children.Add(afWtHint);

            // ========================= Layout tab =========================
            var layoutStack = AddTab("Layout");

            layoutStack.Children.Add(MakeSectionHeader("Prompt panel position", themeFg));
            layoutStack.Children.Add(new TextBlock
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
            layoutStack.Children.Add(topRadio);
            layoutStack.Children.Add(bottomRadio);
            layoutStack.Children.Add(leftRadio);
            layoutStack.Children.Add(rightRadio);

            layoutStack.Children.Add(MakeSectionHeader("Prompt input box", themeFg));
            var hidePromptPanelCheck = MakeCheckBox(
                "Hide prompt input box (terminal only)",
                "Collapses the multi-line prompt text box so the terminal fills the space it occupied. " +
                "The controls row (Send/Attach, Restart, Model, and this ⚙ menu), file chips, and usage bars " +
                "stay visible so you can turn it back on here or from the ⚙ menu.",
                origHidePromptPanel, themeFg);
            layoutStack.Children.Add(hidePromptPanelCheck);

            layoutStack.Children.Add(MakeSectionHeader("Terminal zoom", themeFg));
            var disableAutoZoomCheck = MakeCheckBox(
                "Disable Auto Zoom on Startup",
                "Skip the automatic terminal zoom-out and saved zoom-delta replay performed after each terminal start. Manual Ctrl+Scroll zoom still works.",
                origDisableAutoZoom, themeFg);
            layoutStack.Children.Add(disableAutoZoomCheck);

            // ========================= Terminal tab =========================
            var terminalStack = AddTab("Terminal");

            terminalStack.Children.Add(MakeSectionHeader("Terminal type", themeFg));
            var cmdRadio = MakeRadioButton("Command Prompt (default)",
                origTerminalType == TerminalType.CommandPrompt, themeFg, "terminalType");
            var wtRadio = MakeRadioButton("Windows Terminal (better emoji/unicode support)",
                origTerminalType == TerminalType.WindowsTerminal, themeFg, "terminalType");
            terminalStack.Children.Add(cmdRadio);
            terminalStack.Children.Add(wtRadio);
            terminalStack.Children.Add(new TextBlock
            {
                Text = "Note: Windows Terminal must be installed (winget install Microsoft.WindowsTerminal).",
                FontSize = 11,
                Opacity = 0.7,
                Foreground = themeFg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20, 2, 0, 0)
            });

            // ---- Console font (searchable picker with live preview) ----
            terminalStack.Children.Add(MakeSectionHeader("Console font", themeFg));
            terminalStack.Children.Add(new TextBlock
            {
                Text = "Font applied to the embedded terminal (Command Prompt and Windows Terminal). " +
                       "Defaults to Cascadia Mono. Only monospaced fonts are listed — consoles draw text on a " +
                       "fixed cell grid, so a proportional font comes out jumbled. On systems whose scripts " +
                       "Cascadia Mono can't render (Chinese, Japanese, Korean, etc.), pick a monospaced font " +
                       "that covers your script — for example MS Gothic or NSimSun. Search by name; the list " +
                       "and preview render in each font.",
                FontSize = 11,
                Opacity = 0.7,
                Foreground = themeFg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 0, 0, 4)
            });

            // chosenFont holds the picker's current value, read on OK. The search box filters the list;
            // selecting an item updates chosenFont and the live preview below.
            string chosenFont = origConsoleFont;

            var fontSearchBox = new TextBox
            {
                Width = 300,
                Height = 24,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = themeBg,
                Foreground = themeFg,
                BorderBrush = themeFg,
                Margin = new Thickness(4, 0, 0, 4)
            };
            terminalStack.Children.Add(fontSearchBox);

            var fontList = new ListBox
            {
                Width = 300,
                MaxHeight = 150,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = themeBg,
                Foreground = themeFg,
                BorderBrush = themeFg,
                Margin = new Thickness(4, 0, 0, 4)
            };
            terminalStack.Children.Add(fontList);

            var showAllFontsCheck = MakeCheckBox(
                "Show all fonts (including proportional ones)",
                "Proportional fonts are hidden by default because the terminal renders them jumbled. " +
                "Check this only if you need a face the monospace filter misses.",
                !IsMonospaceFont(origConsoleFont), themeFg);
            terminalStack.Children.Add(showAllFontsCheck);

            var fontPreview = new TextBlock
            {
                Text = "AaBbCc 0123  你好世界  こんにちは  안녕하세요",
                FontSize = 16,
                Foreground = themeFg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 2, 0, 4)
            };
            terminalStack.Children.Add(fontPreview);

            // Shown only while a proportional face is selected: that is exactly the state that produces the
            // jumbled terminal text of issue #105, so say so where the user can still change their mind.
            var fontWarning = new TextBlock
            {
                Text = "⚠ This font is not monospaced. The terminal will render its text jumbled, with gaps " +
                       "after narrow letters and overlapping wide ones.",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0x9B, 0x2B)),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(4, 0, 0, 4)
            };
            terminalStack.Children.Add(fontWarning);

            // Populate with installed font families (sorted, distinct). Each item renders its own name in
            // its own face so the dropdown doubles as a preview; Segoe UI is a fallback for name glyphs.
            var installedFonts = System.Windows.Media.Fonts.SystemFontFamilies
                .Select(f => f.Source)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();

            void ApplyPreviewFont(string face)
            {
                try { fontPreview.FontFamily = new FontFamily(face + ", Segoe UI"); }
                catch { fontPreview.FontFamily = new FontFamily("Segoe UI"); }
                fontWarning.Visibility = IsMonospaceFont(face) ? Visibility.Collapsed : Visibility.Visible;
            }

            foreach (string face in installedFonts)
            {
                bool isMono = IsMonospaceFont(face);
                var item = new ListBoxItem
                {
                    Content = new TextBlock
                    {
                        Text = isMono ? face : face + "  ⚠",
                        FontFamily = new FontFamily(face + ", Segoe UI")
                    },
                    Tag = face,
                    Foreground = themeFg,
                    Background = Brushes.Transparent,
                    ToolTip = isMono ? null : "Not monospaced — the terminal will render this font jumbled."
                };
                fontList.Items.Add(item);
                if (string.Equals(face, chosenFont, StringComparison.OrdinalIgnoreCase))
                {
                    item.IsSelected = true;
                }
            }
            ApplyPreviewFont(chosenFont);

            fontList.SelectionChanged += (s, e) =>
            {
                if (fontList.SelectedItem is ListBoxItem li && li.Tag is string face)
                {
                    chosenFont = face;
                    ApplyPreviewFont(face);
                }
            };

            // Live filter: an item shows when its name contains the search text (case-insensitive) AND it is
            // either monospaced, explicitly allowed by the "show all" toggle, or the currently selected face
            // (which must stay visible so a proportional font already in the settings can be seen and changed).
            void RefreshFontFilter()
            {
                string q = fontSearchBox.Text?.Trim() ?? string.Empty;
                bool showAll = showAllFontsCheck.IsChecked == true;
                foreach (ListBoxItem li in fontList.Items)
                {
                    string face = li.Tag as string;
                    bool matchesSearch = q.Length == 0 ||
                        face?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                    bool allowed = showAll || IsMonospaceFont(face) ||
                        string.Equals(face, chosenFont, StringComparison.OrdinalIgnoreCase);
                    li.Visibility = matchesSearch && allowed ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            fontSearchBox.TextChanged += (s, e) => RefreshFontFilter();
            showAllFontsCheck.Checked += (s, e) => RefreshFontFilter();
            showAllFontsCheck.Unchecked += (s, e) => RefreshFontFilter();
            RefreshFontFilter();

            // Scroll the current selection into view once the dialog is laid out.
            fontList.Loaded += (s, e) =>
            {
                if (fontList.SelectedItem != null) fontList.ScrollIntoView(fontList.SelectedItem);
            };

            // "Disable clipboard" relies on simulated keystrokes that only conhost (Command Prompt)
            // accepts, so the toggle is enabled only while Command Prompt is selected. Keep it in sync
            // with the terminal-type radios live, and uncheck it when switching to Windows Terminal so
            // an unavailable setting can't be saved as enabled. (The checkbox lives on the Behavior tab.)
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

            // "On Agent Finish" works under Windows Terminal (ConPTY console-buffer read, UIA
            // fallback), so the config button stays enabled for both terminal types. Under Windows
            // Terminal an informational hint is shown. Kept in sync with the terminal-type radios live.
            void SyncAgentFinishAvailability()
            {
                bool cmdSelected = cmdRadio.IsChecked == true;
                afOpenButton.IsEnabled = true;
                afOpenButton.Opacity = 1.0;
                afWtHint.Visibility = cmdSelected ? Visibility.Collapsed : Visibility.Visible;
            }
            cmdRadio.Checked += (s, e) => SyncAgentFinishAvailability();
            wtRadio.Checked += (s, e) => SyncAgentFinishAvailability();
            SyncAgentFinishAvailability();

            // ========================= Theme tab =========================
            var themeStack = AddTab("Theme");

            themeStack.Children.Add(MakeSectionHeader("Theme", themeFg));
            var autoRadio = MakeRadioButton("Automatic (follow Visual Studio theme)",
                origThemePref == ThemePreference.Automatic, themeFg, "themePref");
            var darkRadio = MakeRadioButton("Dark",
                origThemePref == ThemePreference.Dark, themeFg, "themePref");
            var lightRadio = MakeRadioButton("Light",
                origThemePref == ThemePreference.Light, themeFg, "themePref");
            var customRadio = MakeRadioButton("Custom background color",
                origThemePref == ThemePreference.Custom, themeFg, "themePref");
            themeStack.Children.Add(autoRadio);
            themeStack.Children.Add(darkRadio);
            themeStack.Children.Add(lightRadio);
            themeStack.Children.Add(customRadio);

            // Custom color row: hex text box + live swatch + "Pick..." button.
            // Initialized from the saved custom color (#RRGGBB).
            int initialCustomArgb = origCustomColorArgb == 0 ? unchecked((int)0xFFF4ECFF) : origCustomColorArgb;
            var initialCustom = System.Drawing.Color.FromArgb(initialCustomArgb);

            var customRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(24, 2, 0, 4)
            };

            var swatch = new Border
            {
                Width = 26,
                Height = 22,
                BorderThickness = new Thickness(1),
                BorderBrush = themeFg,
                Background = new SolidColorBrush(Color.FromRgb(initialCustom.R, initialCustom.G, initialCustom.B)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var hexBox = new TextBox
            {
                Text = $"#{initialCustom.R:X2}{initialCustom.G:X2}{initialCustom.B:X2}",
                Width = 90,
                Height = 24,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = themeBg,
                Foreground = themeFg,
                BorderBrush = themeFg,
                VerticalAlignment = VerticalAlignment.Center
            };

            var pickButton = new Button
            {
                Content = "Pick...",
                Height = 24,
                MinWidth = 64,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(10, 0, 10, 0)
            };
            if (GetDialogButtonStyle() is Style pbStyle) pickButton.Style = pbStyle;

            // Try to parse the hex box (#RGB or #RRGGBB). Returns null when invalid.
            System.Drawing.Color? ParseHex(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return null;
                s = s.Trim().TrimStart('#');
                if (s.Length == 3)
                    s = $"{s[0]}{s[0]}{s[1]}{s[1]}{s[2]}{s[2]}";
                if (s.Length != 6) return null;
                if (!int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int rgb))
                    return null;
                return System.Drawing.Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
            }

            void UpdateSwatchFromHex()
            {
                var c = ParseHex(hexBox.Text);
                if (c.HasValue)
                    swatch.Background = new SolidColorBrush(Color.FromRgb(c.Value.R, c.Value.G, c.Value.B));
            }
            hexBox.TextChanged += (s, ea) => UpdateSwatchFromHex();

            pickButton.Click += (s, ea) =>
            {
                var current = ParseHex(hexBox.Text) ?? initialCustom;
                using (var cd = new System.Windows.Forms.ColorDialog
                {
                    FullOpen = true,
                    Color = current
                })
                {
                    if (cd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        hexBox.Text = $"#{cd.Color.R:X2}{cd.Color.G:X2}{cd.Color.B:X2}";
                        customRadio.IsChecked = true;
                    }
                }
            };

            customRow.Children.Add(swatch);
            customRow.Children.Add(hexBox);
            customRow.Children.Add(pickButton);
            themeStack.Children.Add(customRow);

            var skipPromptCheck = MakeCheckBox(
                "Don't ask to restart the AI agent when the theme changes",
                "Suppresses the \"Theme changed. Restart the AI code agent?\" pop-up. Useful when Visual Studio automatically switches themes (for example, the debugging theme triggered by F5).",
                origSkipThemePrompt, themeFg);
            skipPromptCheck.Margin = new Thickness(4, 10, 0, 0);
            themeStack.Children.Add(skipPromptCheck);

            // ========================= Usage tab =========================
            var usageStack = AddTab("Usage");

            usageStack.Children.Add(MakeSectionHeader("Usage bars", themeFg));
            var showBarsCheck = MakeCheckBox(
                "Show inline usage bars",
                "Show the mini session/weekly usage bars in the prompt panel. Only applies when a Claude Code provider is active.",
                origShowInlineBars, themeFg);
            usageStack.Children.Add(showBarsCheck);

            usageStack.Children.Add(MakeSectionHeader("Auto-refresh", themeFg));
            usageStack.Children.Add(new TextBlock
            {
                Text = "How often to refresh usage data in the background. \"Off\" refreshes only when the usage window is open or refreshed manually.",
                FontSize = 11,
                Opacity = 0.7,
                Foreground = themeFg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 0, 0, 4)
            });
            var autoRefreshCombo = MakeThemedComboBox(comboRes, themeFg);
            autoRefreshCombo.Width = 110;
            autoRefreshCombo.HorizontalAlignment = HorizontalAlignment.Left;
            autoRefreshCombo.Margin = new Thickness(4, 0, 0, 4);
            (string, int)[] refreshOpts = { ("Off", 0), ("30 sec", 30), ("1 min", 60), ("2 min", 120) };
            foreach (var (label, secs) in refreshOpts)
            {
                var item = new ComboBoxItem { Content = label, Tag = secs };
                if (comboRes["cbi"] is Style cbiStyle) item.Style = cbiStyle;
                if (secs == origAutoRefresh) item.IsSelected = true;
                autoRefreshCombo.Items.Add(item);
            }
            if (autoRefreshCombo.SelectedItem == null && autoRefreshCombo.Items.Count > 0)
                autoRefreshCombo.SelectedIndex = 0;
            usageStack.Children.Add(autoRefreshCombo);

            // ========================= Toolbar tab =========================
            var toolbarStack = AddTab("Toolbar");
            var toolbarTab = BuildToolbarButtonsTabContent(toolbarStack, themeFg);
            var toolbarButtonChecks = toolbarTab.Checks;
            var toolbarRowsPanel = toolbarTab.RowsPanel;

            // ========================= CLI Paths tab (last) =========================
            var cliPathsStack = AddTab("CLI Paths");
            var cliPathEditors = BuildCliPathsTabContent(cliPathsStack, themeBg, themeFg);

            // ---- Button row ----
            var buttonPanel = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            buttonPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttonPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttonPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetRow(buttonPanel, 1);

            Style buttonStyle = GetDialogButtonStyle();

            var resetButton = new Button
            {
                Content = "Reset to Defaults",
                Height = 32,
                MinWidth = 140,
                Padding = new Thickness(18, 0, 18, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            Grid.SetColumn(resetButton, 0);

            var okCancelPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(okCancelPanel, 2);

            var okButton = new Button
            {
                Content = "OK",
                Width = 90,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 90,
                Height = 32,
                IsCancel = true
            };
            if (buttonStyle != null)
            {
                okButton.Style = buttonStyle;
                cancelButton.Style = buttonStyle;
                resetButton.Style = buttonStyle;
            }
            else
            {
                okButton.Background = themeBg; okButton.Foreground = themeFg; okButton.BorderBrush = themeFg;
                cancelButton.Background = themeBg; cancelButton.Foreground = themeFg; cancelButton.BorderBrush = themeFg;
                resetButton.Background = themeBg; resetButton.Foreground = themeFg; resetButton.BorderBrush = themeFg;
            }
            okButton.Click += (s, ea) =>
            {
                // Warn (and keep the dialog open) if a native CLI path doesn't exist on disk.
                if (!ConfirmCliPathsBeforeClose(cliPathEditors)) return;
                dialog.DialogResult = true;
            };

            // Reset to Defaults: restore every control on this dialog to its default value.
            // Nothing is persisted until the user confirms with OK.
            void SelectComboByTag(ComboBox combo, int tagValue)
            {
                foreach (var obj in combo.Items)
                {
                    if (obj is ComboBoxItem ci && ci.Tag is int t && t == tagValue)
                    {
                        ci.IsSelected = true;
                        return;
                    }
                }
            }
            resetButton.Click += (s, ea) =>
            {
                if (MessageBox.Show(
                        "Reset all settings shown here to their defaults?",
                        "Reset to Defaults",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;

                sendEnterRadio.IsChecked = true;          // Send with Enter
                largeAsFileCheck.IsChecked = false;
                disableClipboardCheck.IsChecked = false;
                sendSelectionRefOnlyCheck.IsChecked = false;
                autoOpenCheck.IsChecked = false;
                SelectComboByTag(fontSizeCombo, 12);
                topRadio.IsChecked = true;                // Top layout
                disableAutoZoomCheck.IsChecked = false;
                cmdRadio.IsChecked = true;                // Command Prompt
                autoRadio.IsChecked = true;               // Automatic theme
                hexBox.Text = "#F4ECFF";                  // default custom color
                skipPromptCheck.IsChecked = false;
                showBarsCheck.IsChecked = true;
                SelectComboByTag(autoRefreshCombo, 0);    // Off

                // CLI Paths tab: default is no custom path (use detection) for every provider.
                foreach (var tb in cliPathEditors.Values)
                    tb.Text = "";

                // Toolbar tab: default promotes only Restart to a one-click button, in default order.
                foreach (var kv in toolbarButtonChecks)
                    kv.Value.IsChecked = kv.Key == ToolbarButton.RestartAgent;
                for (int i = DefaultToolbarButtonOrder.Length - 1; i >= 0; i--)
                {
                    var id = DefaultToolbarButtonOrder[i];
                    var row = toolbarRowsPanel.Children.OfType<Border>()
                        .FirstOrDefault(b => b.Tag is ToolbarButton tb && tb == id);
                    if (row != null)
                    {
                        toolbarRowsPanel.Children.Remove(row);
                        toolbarRowsPanel.Children.Insert(0, row);
                    }
                }
            };

            okCancelPanel.Children.Add(okButton);
            okCancelPanel.Children.Add(cancelButton);
            buttonPanel.Children.Add(resetButton);
            buttonPanel.Children.Add(okCancelPanel);
            rootGrid.Children.Add(buttonPanel);

            dialog.Content = rootGrid;

            if (dialog.ShowDialog() != true)
            {
                // Cancel - no changes applied
                return;
            }

            // ---- Collect new values ----
            bool newSendWithEnter     = sendEnterRadio.IsChecked == true;
            bool newSendWithCtrlEnter = sendCtrlEnterRadio.IsChecked == true;
            bool newSendLargeAsFile = largeAsFileCheck.IsChecked == true;
            bool newDisableClipboardSend = disableClipboardCheck.IsChecked == true;
            bool newSendSelectionRefOnly = sendSelectionRefOnlyCheck.IsChecked == true;
            bool newAutoSendBuildErrors = autoSendBuildErrorsCheck.IsChecked == true;
            bool newAutoOpenChanges = autoOpenCheck.IsChecked == true;
            int newFontSize = (fontSizeCombo.SelectedItem as ComboBoxItem)?.Tag is int fs ? fs : origFontSize;
            // Map the selected position back to orientation + invert.
            bool newVertical = leftRadio.IsChecked == true || rightRadio.IsChecked == true;
            bool newInvertLayout = bottomRadio.IsChecked == true || rightRadio.IsChecked == true;
            LayoutOrientation newOrientation = newVertical
                ? LayoutOrientation.Vertical
                : LayoutOrientation.Horizontal;
            bool newDisableAutoZoom = disableAutoZoomCheck.IsChecked == true;
            bool newHidePromptPanel = hidePromptPanelCheck.IsChecked == true;
            TerminalType newTerminalType = wtRadio.IsChecked == true
                ? TerminalType.WindowsTerminal
                : TerminalType.CommandPrompt;
            string newConsoleFont = string.IsNullOrWhiteSpace(chosenFont)
                ? "Cascadia Mono" : chosenFont.Trim();
            ThemePreference newThemePref =
                darkRadio.IsChecked   == true ? ThemePreference.Dark   :
                lightRadio.IsChecked  == true ? ThemePreference.Light  :
                customRadio.IsChecked == true ? ThemePreference.Custom :
                                                ThemePreference.Automatic;

            // Parse the custom hex; fall back to the original color if invalid.
            int newCustomColorArgb = origCustomColorArgb == 0 ? unchecked((int)0xFFF4ECFF) : origCustomColorArgb;
            {
                var parsed = ParseHex(hexBox.Text);
                if (parsed.HasValue)
                    newCustomColorArgb = parsed.Value.ToArgb();
                else if (newThemePref == ThemePreference.Custom)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    MessageBox.Show(
                        "The custom background color is not a valid hex value (use #RRGGBB, e.g. #F4ECFF).\n\n" +
                        "Keeping the previous color.",
                        "Invalid Color",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            bool newSkipThemePrompt = skipPromptCheck.IsChecked == true;
            bool newShowInlineBars = showBarsCheck.IsChecked == true;
            int newAutoRefresh = (autoRefreshCombo.SelectedItem as ComboBoxItem)?.Tag is int ar ? ar : origAutoRefresh;
            var newToolbarOrder = ReadToolbarRowOrder(toolbarRowsPanel);
            var newVisibleToolbarButtons = newToolbarOrder
                .Where(b => toolbarButtonChecks.TryGetValue(b, out var c) && c.IsChecked == true)
                .ToList();

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
            _settings.SendWithCtrlEnter       = newSendWithCtrlEnter;
            _settings.SendLargePromptsAsFile  = newSendLargeAsFile;
            _settings.DisableClipboardSend    = newDisableClipboardSend;
            _settings.SendSelectionReferenceOnly = newSendSelectionRefOnly;
            _settings.AutoSendBuildErrorsToAgent = newAutoSendBuildErrors;
            _settings.AutoOpenChangesOnPrompt = newAutoOpenChanges;
            _settings.InvertLayout            = newInvertLayout;
            _settings.SelectedLayoutOrientation = newOrientation;
            _settings.DisableStartupAutoZoom  = newDisableAutoZoom;
            _settings.HidePromptPanel         = newHidePromptPanel;
            _settings.SelectedTerminalType    = newTerminalType;
            _settings.ConsoleFontFaceName     = newConsoleFont;
            _settings.SelectedThemePreference = newThemePref;
            _settings.CustomThemeColorArgb    = newCustomColorArgb;
            _settings.SkipThemeRestartPrompt  = newSkipThemePrompt;
            _settings.ShowInlineUsageBars     = newShowInlineBars;
            _settings.UsageAutoRefreshSeconds = newAutoRefresh;
            _settings.PromptFontSize          = newFontSize;
            _settings.VisibleToolbarButtons   = newVisibleToolbarButtons;
            _settings.ToolbarButtonOrder      = newToolbarOrder;

            // Apply order changes to the live controls, then swap features between buttons and the
            // ☰ Tools dropdown.
            bool toolbarOrderChanged = !newToolbarOrder.SequenceEqual(origToolbarOrder);
            bool toolbarVisibleChanged = !newVisibleToolbarButtons.OrderBy(b => b).SequenceEqual(origVisibleToolbarButtons.OrderBy(b => b));
            if (toolbarOrderChanged)
            {
                ReorderToolbarControls();
            }
            if (toolbarOrderChanged || toolbarVisibleChanged)
            {
                RefreshToolbarLayout();
            }

            // Custom CLI executable paths (CLI Paths tab). Mutates _settings.CustomExecutablePaths
            // and returns the providers whose path actually changed.
            var changedCliProviders = ApplyCliPathChanges(cliPathEditors);
            bool cliPathsChanged = changedCliProviders.Count > 0;
            // Only the active provider's path change warrants relaunching the terminal.
            bool activeCliPathChanged = changedCliProviders.Contains(_settings.SelectedProvider);

            // On Agent Finish is configured in its own dialog (opened by the button above),
            // which persists its own changes; nothing to apply here.

            // Send button visibility tied to SendWithEnter
            SendPromptButton.Visibility = _settings.SendWithEnter
                ? Visibility.Collapsed
                : Visibility.Visible;

            // Apply prompt font size immediately
            if (PromptTextBox != null) PromptTextBox.FontSize = newFontSize;

            // Layout change (position and/or orientation)
            if (newInvertLayout != origInvertLayout || newOrientation != origOrientation)
            {
                ApplyLayoutSettingsChange();
            }

            // Hide/show the prompt input box
            if (newHidePromptPanel != origHidePromptPanel)
            {
                ApplyPromptPanelHiddenState();
            }

            // Theme change: re-paint panel and inline bars immediately.
            // A custom-color edit (same Custom preference, different color) also counts.
            bool themeChanged = newThemePref != origThemePref
                || (newThemePref == ThemePreference.Custom && newCustomColorArgb != origCustomColorArgb);
            if (themeChanged)
            {
                UpdateTerminalTheme();
                UpdateInlineUsageBarColors();
            }

            // Usage settings change: refresh inline bars visibility and auto-refresh cadence
            if (newShowInlineBars != origShowInlineBars || newAutoRefresh != origAutoRefresh)
            {
                try
                {
                    UpdateInlineUsagePanelVisibility();
                    if (_usageToolWindow?.IsWindowVisible != true)
                        StartUsageBackgroundRefreshTimer();
                    _usageToolWindow?.UsageControl?.ApplyAutoRefreshSeconds(newAutoRefresh);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error applying usage settings change: {ex.Message}");
                }
            }

            SaveSettings();

            // A CLI path change alters detection results — drop the availability cache so the
            // next check (and menu state) reflects the override, and relaunch the active provider.
            if (cliPathsChanged)
            {
                ClearProviderCache();
            }

            // ---- Restart-requiring changes ----
            bool terminalTypeChanged = newTerminalType != origTerminalType;
            // A console font change takes effect only when the terminal next launches (conhost reads the
            // registry at creation; Windows Terminal is launched with the dedicated -p profile), so relaunch.
            bool consoleFontChanged = newConsoleFont != origConsoleFont;
            bool needsRestart = terminalTypeChanged || activeCliPathChanged || consoleFontChanged;

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

        /// <summary>Display label + tooltip for each configurable toolbar feature.</summary>
        private static (string Label, string Tip) ToolbarFeatureMeta(ToolbarButton id)
        {
            switch (id)
            {
                case ToolbarButton.UpdateAgent: return ("🔄️  Update Code Agent", "Update the active code agent's CLI to the latest version.");
                case ToolbarButton.DetachTerminal: return ("⧉  Detach / Attach Terminal", "Move the terminal to a separate tab and back.");
                case ToolbarButton.RestartAgent: return ("♻️  Restart Code Agent", "Restart the active code agent.");
                case ToolbarButton.ViewChanges: return ("📄  View Changes", "Open the Changes (diff) view. Shown only inside a git repository.");
                case ToolbarButton.SessionHistory: return ("📜  Session History", "Resume a previous session. Claude Code providers only.");
                case ToolbarButton.ShowUsage: return ("📊  Show Usage", "Toggle the usage window. Claude / Devin providers only.");
                case ToolbarButton.SetWorkingDirectory: return ("📁  Set Working Directory", "Set a custom working directory for the agent.");
                default: return (id.ToString(), string.Empty);
            }
        }

        /// <summary>
        /// Builds the "Toolbar" settings tab: a drag-and-drop reorderable list of the configurable
        /// features, each with a checkbox that promotes it to a one-click toolbar button. The row
        /// order drives both the toolbar button order and the order inside the ☰ Tools dropdown.
        /// Returns the rows panel (each row's Tag is its <see cref="ToolbarButton"/>) plus the
        /// checkboxes keyed by feature, for collection on OK. See RefreshToolbarLayout /
        /// ReorderToolbarControls.
        /// </summary>
        private (StackPanel RowsPanel, Dictionary<ToolbarButton, CheckBox> Checks) BuildToolbarButtonsTabContent(StackPanel stack, Brush themeFg)
        {
            stack.Children.Add(MakeSectionHeader("One-click toolbar buttons", themeFg));
            stack.Children.Add(new TextBlock
            {
                Text = "Checked features appear as one-click buttons in the toolbar; unchecked features live in the " +
                       "☰ Tools dropdown (which hides when every feature is a button). Drag rows by the ⠿ handle to " +
                       "reorder them — the order applies to both the buttons and the dropdown. Some features only " +
                       "appear when they apply to the current agent or workspace.",
                FontSize = 11,
                Opacity = 0.7,
                Foreground = themeFg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 0, 0, 6)
            });

            var current = _settings.VisibleToolbarButtons ?? new List<ToolbarButton>();
            // Transparent background so the whole panel (incl. gaps) is a hit-testable drop target.
            var rowsPanel = new StackPanel { Orientation = Orientation.Vertical, Background = Brushes.Transparent, AllowDrop = true };
            stack.Children.Add(rowsPanel);

            // Shared drag state across all rows.
            Border draggedRow = null;
            Point dragStart = default;
            RowDragAdorner adorner = null;
            AdornerLayer adornerLayer = null;
            var lineBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0x7E, 0xFF));
            lineBrush.Freeze();

            // Computes the insertion index for a drop at point p (relative to rowsPanel) and the Y of
            // the insertion line (top of the row it would land before, or the bottom of the last row).
            double ComputeInsertion(Point p, out int index)
            {
                index = rowsPanel.Children.Count;
                double lineY = 0;
                for (int i = 0; i < rowsPanel.Children.Count; i++)
                {
                    if (!(rowsPanel.Children[i] is FrameworkElement el)) continue;
                    double top = el.TranslatePoint(new Point(0, 0), rowsPanel).Y;
                    double h = el.ActualHeight;
                    if (p.Y < top + h / 2) { index = i; return top; }
                    lineY = top + h;
                }
                return lineY;
            }

            rowsPanel.DragOver += (s, ea) =>
            {
                ea.Effects = DragDropEffects.Move;
                ea.Handled = true;
                if (adorner == null) return;
                Point p = ea.GetPosition(rowsPanel);
                double lineY = ComputeInsertion(p, out _);
                adorner.Update(p, lineY);
            };
            rowsPanel.Drop += (s, ea) =>
            {
                if (draggedRow == null) return;
                Point p = ea.GetPosition(rowsPanel);
                ComputeInsertion(p, out int index);
                UIElement target = index < rowsPanel.Children.Count ? rowsPanel.Children[index] : null;
                rowsPanel.Children.Remove(draggedRow);
                int insertAt = target != null ? rowsPanel.Children.IndexOf(target) : rowsPanel.Children.Count;
                if (insertAt < 0) insertAt = rowsPanel.Children.Count;
                rowsPanel.Children.Insert(insertAt, draggedRow);
                ea.Handled = true;
            };

            var checks = new Dictionary<ToolbarButton, CheckBox>();
            foreach (var id in GetEffectiveToolbarOrder())
            {
                var meta = ToolbarFeatureMeta(id);
                var cb = MakeCheckBox(meta.Label, meta.Tip, current.Contains(id), themeFg);
                cb.VerticalAlignment = VerticalAlignment.Center;
                checks[id] = cb;

                var grip = new TextBlock
                {
                    Text = "⠿",
                    FontSize = 14,
                    Opacity = 0.55,
                    Foreground = themeFg,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 8, 0),
                    Cursor = Cursors.SizeAll,
                    ToolTip = "Drag to reorder"
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(grip, 0);
                Grid.SetColumn(cb, 1);
                grid.Children.Add(grip);
                grid.Children.Add(cb);

                // Transparent background so the whole row is hit-testable; events bubble to the panel.
                // No SizeAll cursor here — only the ⠿ grip shows the move cursor and starts a drag, so
                // the checkbox/label use the regular pointer.
                var row = new Border
                {
                    Tag = id,
                    Child = grid,
                    Background = Brushes.Transparent,
                    Padding = new Thickness(2, 4, 2, 4),
                    Margin = new Thickness(0, 1, 0, 1)
                };

                Border thisRow = row; // capture per-iteration

                // Drag starts on the grip only. Capture the mouse on press so we keep getting moves even
                // if the pointer slips off the grip before the drag threshold is reached.
                grip.PreviewMouseLeftButtonDown += (s, ea) =>
                {
                    dragStart = ea.GetPosition(rowsPanel);
                    grip.CaptureMouse();
                };
                grip.PreviewMouseLeftButtonUp += (s, ea) =>
                {
                    if (grip.IsMouseCaptured) grip.ReleaseMouseCapture();
                };
                grip.PreviewMouseMove += (s, ea) =>
                {
                    if (ea.LeftButton != MouseButtonState.Pressed || draggedRow != null || !grip.IsMouseCaptured) return;
                    Point pos = ea.GetPosition(rowsPanel);
                    if (Math.Abs(pos.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                        Math.Abs(pos.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                        return;

                    grip.ReleaseMouseCapture();
                    draggedRow = thisRow;

                    // Ghost = a static snapshot of the row taken before it is dimmed, so the floating
                    // image stays solid while the in-place row fades to show it's being moved.
                    Brush ghost = SnapshotBrush(thisRow);
                    adornerLayer = AdornerLayer.GetAdornerLayer(rowsPanel);
                    if (adornerLayer != null && ghost != null)
                    {
                        adorner = new RowDragAdorner(rowsPanel, ghost, thisRow.RenderSize, rowsPanel.ActualWidth, lineBrush);
                        adornerLayer.Add(adorner);
                    }
                    thisRow.Opacity = 0.4;

                    try { DragDrop.DoDragDrop(thisRow, thisRow, DragDropEffects.Move); }
                    finally
                    {
                        thisRow.Opacity = 1.0;
                        if (adorner != null && adornerLayer != null) adornerLayer.Remove(adorner);
                        adorner = null;
                        draggedRow = null;
                    }
                };

                rowsPanel.Children.Add(row);
            }

            return (rowsPanel, checks);
        }

        /// <summary>Renders a UIElement to a static image brush (used as the drag ghost).</summary>
        private static Brush SnapshotBrush(FrameworkElement element)
        {
            int w = (int)Math.Ceiling(element.ActualWidth);
            int h = (int)Math.Ceiling(element.ActualHeight);
            if (w <= 0 || h <= 0) return null;
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(element);
            rtb.Freeze();
            var brush = new ImageBrush(rtb) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// Adorner drawn over the Toolbar tab's rows while a row is being dragged: a translucent ghost
        /// of the row following the cursor plus a blue insertion line showing where it will land.
        /// Hit-test-invisible so it never intercepts the drop.
        /// </summary>
        private sealed class RowDragAdorner : Adorner
        {
            private readonly Brush _ghost;
            private readonly Size _ghostSize;
            private readonly Pen _linePen;
            private readonly double _width;
            private Point _mouse;
            private double _insertionY = double.NaN;

            public RowDragAdorner(UIElement adorned, Brush ghost, Size ghostSize, double width, Brush lineBrush)
                : base(adorned)
            {
                IsHitTestVisible = false;
                _ghost = ghost;
                _ghostSize = ghostSize;
                _width = width > 0 ? width : ghostSize.Width;
                _linePen = new Pen(lineBrush, 2);
                _linePen.Freeze();
            }

            public void Update(Point mouse, double insertionY)
            {
                _mouse = mouse;
                _insertionY = insertionY;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext dc)
            {
                if (!double.IsNaN(_insertionY))
                {
                    dc.DrawLine(_linePen, new Point(0, _insertionY), new Point(_width, _insertionY));
                    dc.DrawEllipse(_linePen.Brush, null, new Point(3, _insertionY), 3, 3);
                }

                if (_ghost != null && _ghostSize.Width > 0 && _ghostSize.Height > 0)
                {
                    var rect = new Rect(new Point(_mouse.X + 12, _mouse.Y + 4), _ghostSize);
                    dc.PushOpacity(0.75);
                    dc.DrawRectangle(_ghost, null, rect);
                    dc.Pop();
                }
            }
        }

        /// <summary>Reads the current feature order from the Toolbar tab's rows (top to bottom).</summary>
        private static List<ToolbarButton> ReadToolbarRowOrder(StackPanel rowsPanel)
        {
            return rowsPanel.Children.OfType<Border>()
                .Where(b => b.Tag is ToolbarButton)
                .Select(b => (ToolbarButton)b.Tag)
                .ToList();
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
        /// Face name -> monospaced verdict, so the font picker measures each installed family's glyphs only
        /// once per VS session instead of on every Settings dialog open.
        /// </summary>
        private static readonly Dictionary<string, bool> _monospaceFontCache =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// True when the probe characters of <paramref name="face"/> all share the same advance width, i.e.
        /// the family is monospaced. Consoles place every glyph on a fixed cell grid, so a proportional face
        /// renders as jumbled text — gaps after narrow letters, overlap on wide ones (issue #105). Faces whose
        /// glyph metrics can't be read are reported as monospaced, so an unreadable font is never flagged
        /// wrongly; the worst case is that it stays listed as it was before the filter existed.
        /// </summary>
        private static bool IsMonospaceFont(string face)
        {
            if (string.IsNullOrWhiteSpace(face)) return true;
            if (_monospaceFontCache.TryGetValue(face, out bool cached)) return cached;

            bool isMono = true;
            try
            {
                var typeface = new Typeface(new FontFamily(face), FontStyles.Normal,
                                            FontWeights.Normal, FontStretches.Normal);
                if (typeface.TryGetGlyphTypeface(out GlyphTypeface glyphs))
                {
                    double reference = -1;
                    foreach (char probe in new[] { 'i', 'l', 'M', 'W', '0', 'x' })
                    {
                        if (!glyphs.CharacterToGlyphMap.TryGetValue(probe, out ushort glyphIndex)) continue;
                        if (!glyphs.AdvanceWidths.TryGetValue(glyphIndex, out double width) || width <= 0) continue;
                        if (reference < 0) { reference = width; continue; }
                        if (Math.Abs(width - reference) > 0.01) { isMono = false; break; }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"IsMonospaceFont({face}): {ex.Message}");
            }

            _monospaceFontCache[face] = isMono;
            return isMono;
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

        /// <summary>
        /// Creates a ComboBox pre-wired with the flat themed templates from
        /// <see cref="BuildThemedComboResources"/>. Items still need the "cbi" style applied
        /// individually (the host code does this when adding ComboBoxItems).
        /// </summary>
        private ComboBox MakeThemedComboBox(ResourceDictionary comboRes, Brush fg)
        {
            var cb = new ComboBox { Foreground = fg, Height = 26 };
            if (comboRes?["cb"] is Style cbStyle) cb.Style = cbStyle;
            return cb;
        }

        /// <summary>
        /// Parses flat TabControl + TabItem templates with the VS theme colors injected,
        /// returning a dictionary with the "tabControl" and "tabItem" styles. A standalone
        /// dialog doesn't inherit VS's themed tab styling and the default templates paint
        /// their own system selection/hover, so we replace the templates outright. The selected
        /// tab blends into the content area (same background) while unselected tabs use a derived
        /// shade (via <see cref="ComputeAtHoverBrush"/>) so they stay readable in dark and light themes.
        /// </summary>
        private ResourceDictionary BuildThemedTabResources(Brush bg, Brush fg)
        {
            string bgHex = ((bg as SolidColorBrush)?.Color ?? Colors.Black).ToString();
            string fgHex = ((fg as SolidColorBrush)?.Color ?? Colors.White).ToString();
            string shadeHex = ((ComputeAtHoverBrush(bg) as SolidColorBrush)?.Color ?? Colors.Gray).ToString();

            string xaml = TabControlTemplateXaml
                .Replace("__BG__", bgHex)
                .Replace("__FG__", fgHex)
                .Replace("__SHADE__", shadeHex)
                .Replace("__BORDER__", shadeHex);

            return (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml);
        }

        // Flat TabControl / TabItem templates. Single-quoted attributes so the whole thing can
        // live in a verbatim C# string without escaping. Color tokens are substituted at runtime.
        private const string TabControlTemplateXaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style x:Key='tabItem' TargetType='TabItem'>
    <Setter Property='Foreground' Value='__FG__'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='TabItem'>
          <Border x:Name='bd' Background='__SHADE__' BorderBrush='__BORDER__'
                  BorderThickness='1,1,1,0' Margin='0,0,3,0' Padding='12,6' SnapsToDevicePixels='True'>
            <ContentPresenter ContentSource='Header' TextElement.Foreground='__FG__'
                              HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='__BG__'/>
            </Trigger>
            <Trigger Property='IsSelected' Value='True'>
              <Setter TargetName='bd' Property='Background' Value='__BG__'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key='tabControl' TargetType='TabControl'>
    <Setter Property='ItemContainerStyle' Value='{StaticResource tabItem}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='TabControl'>
          <Grid>
            <Grid.RowDefinitions>
              <RowDefinition Height='Auto'/>
              <RowDefinition Height='*'/>
            </Grid.RowDefinitions>
            <TabPanel Grid.Row='0' IsItemsHost='True' Panel.ZIndex='1' Margin='0,0,0,-1'/>
            <Border Grid.Row='1' Background='__BG__' BorderBrush='__BORDER__' BorderThickness='1' SnapsToDevicePixels='True'>
              <ContentPresenter ContentSource='SelectedContent'/>
            </Border>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";

        #endregion
    }
}
