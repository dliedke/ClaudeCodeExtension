/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Settings management for Claude Code extension
 *
 * *******************************************************************************************************************/

using System;
using System.IO;
using System.Diagnostics;
using System.Windows;
using Newtonsoft.Json;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Settings Fields

        /// <summary>
        /// Configuration file name (normal Visual Studio instance)
        /// </summary>
        private const string ConfigurationFileName = "claudecode-settings.json";

        /// <summary>
        /// Full path to the configuration file.
        /// When Visual Studio runs as an experimental instance (devenv /rootsuffix Exp — e.g. an
        /// F5 debug session of this extension), the file name is suffixed with the root suffix
        /// ("claudecode-settings.Exp.json"). Both instances otherwise share the single
        /// %LocalAppData% path, so without this the F5 build and the installed extension would
        /// read/write the same file and overwrite each other's settings (notably the terminal
        /// type, which is not in VolatileSettingsFields).
        /// </summary>
        private static readonly string ConfigurationPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeCodeExtension",
            GetConfigurationFileName());

        /// <summary>
        /// Returns the settings file name, suffixed with the Visual Studio root suffix when
        /// running under an experimental instance (devenv launched with "/rootsuffix &lt;name&gt;",
        /// which is how F5 starts this extension). This isolates the F5 debug build's settings from
        /// the installed extension running in the normal VS instance. Falls back to the plain file
        /// name for a normal instance or on any error.
        /// </summary>
        private static string GetConfigurationFileName()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                {
                    string a = args[i];
                    if (!string.IsNullOrEmpty(a) && (a[0] == '/' || a[0] == '-')
                        && string.Equals(a.Substring(1), "rootsuffix", StringComparison.OrdinalIgnoreCase))
                    {
                        string suffix = args[i + 1];
                        if (!string.IsNullOrWhiteSpace(suffix))
                        {
                            // Keep only letters/digits so the suffix is a safe file-name fragment.
                            var sb = new System.Text.StringBuilder(suffix.Length);
                            foreach (char c in suffix)
                            {
                                if (char.IsLetterOrDigit(c)) sb.Append(c);
                            }
                            if (sb.Length > 0)
                            {
                                return $"claudecode-settings.{sb}.json";
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetConfigurationFileName error: {ex.Message}");
            }
            return ConfigurationFileName;
        }

        /// <summary>
        /// Current settings instance
        /// </summary>
        private ClaudeCodeSettings _settings;

        /// <summary>
        /// Flag to prevent saving settings during initialization
        /// </summary>
        private bool _isInitializing = true;

        /// <summary>
        /// Set to true during Dispose/CleanupResources so the final SaveSettings
        /// call persists volatile per-instance fields (provider, model, effort).
        /// During normal operation these fields are excluded from disk writes so
        /// that multiple VS instances do not overwrite each other's selections.
        /// </summary>
        private bool _isShuttingDown = false;

        /// <summary>
        /// Settings properties that represent per-instance state (which provider
        /// and model the user chose in THIS VS window). During normal operation
        /// SaveSettings preserves whatever the disk file already has for these
        /// fields, avoiding cross-instance overwrites. On shutdown the current
        /// values are persisted so the next single-instance launch picks them up.
        /// </summary>
        private static readonly string[] VolatileSettingsFields = new[]
        {
            nameof(ClaudeCodeSettings.SelectedProvider),
            nameof(ClaudeCodeSettings.SelectedClaudeModel),
            nameof(ClaudeCodeSettings.SelectedDevinModel),
            nameof(ClaudeCodeSettings.SelectedEffortLevel)
        };

        #endregion

        #region Settings Management

        /// <summary>
        /// Loads settings from the configuration file
        /// </summary>
        private void LoadSettings()
        {
            try
            {
                if (File.Exists(ConfigurationPath))
                {
                    var json = File.ReadAllText(ConfigurationPath);

                    // ObjectCreationHandling.Replace: properties seeded with a non-empty
                    // default list (e.g. DevinModels, VisibleProviders) must be REPLACED by
                    // the saved values, not appended to. With the default (Auto) handling,
                    // Newtonsoft adds the deserialized items on top of the initializer items,
                    // so the list grows by the seed size on every load — which made the Devin
                    // model menu show the model list repeated over and over.
                    var loadSettings = new JsonSerializerSettings
                    {
                        ObjectCreationHandling = ObjectCreationHandling.Replace
                    };
                    _settings = JsonConvert.DeserializeObject<ClaudeCodeSettings>(json, loadSettings) ?? new ClaudeCodeSettings();

                    // Heal settings files already corrupted by the append-on-load bug above:
                    // collapse accumulated duplicate entries while preserving order.
                    DedupePreservingOrder(_settings.DevinModels);
                    DedupePreservingOrder(_settings.VisibleProviders);
                    DedupePreservingOrder(_settings.VisibleToolbarButtons);

                    // Retired providers (e.g. QwenCode ordinal 6) deserialize into
                    // a numeric value that is no longer a declared enum member.
                    // Fall back to Claude Code so the extension still launches.
                    if (!Enum.IsDefined(typeof(AiProvider), _settings.SelectedProvider))
                    {
                        _settings.SelectedProvider = AiProvider.ClaudeCode;
                    }

                    // Seed the durable effort baseline from disk. Max/Ultracode are
                    // session-only and should never have been persisted; if an older
                    // config still carries one, fall back to High so the slider starts
                    // from a durable level rather than re-entering a transient mode.
                    _lastPersistableEffortLevel = IsSessionOnlyEffort(_settings.SelectedEffortLevel)
                        ? EffortLevel.High
                        : _settings.SelectedEffortLevel;
                    if (IsSessionOnlyEffort(_settings.SelectedEffortLevel))
                    {
                        _settings.SelectedEffortLevel = EffortLevel.High;
                    }
                }
                else
                {
                    _settings = new ClaudeCodeSettings();

                    // Seed a starter custom command for fresh installs only — existing
                    // settings files are never touched, so a user's own list is never
                    // silently appended to on upgrade.
                    _settings.CustomCommands.Add(new CustomCommand
                    {
                        Name = "Commit & Push",
                        Command = "Commit and push the changes"
                    });

                    // Save the default settings to create the file
                    SaveDefaultSettings();
                }

                if (_settings.SplitterPosition > 0)
                {
                    SetSplitterPosition(_settings.SplitterPosition);

                    // Re-apply after first layout pass completes — during Loaded the
                    // control may not have its final size yet, so the pixel value can
                    // be overridden by WPF layout recalculation
                    double savedPos = _settings.SplitterPosition;
#pragma warning disable VSTHRD001, VSTHRD110
                    _ = Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Loaded,
                        new Action(() => SetSplitterPosition(savedPos)));
#pragma warning restore VSTHRD001, VSTHRD110
                }

                // Only apply if user has explicitly set a font size (0 = use VS default)
                if (_settings.PromptFontSize >= 8)
                {
                    PromptTextBox.FontSize = _settings.PromptFontSize;
                }

                // Restore the color the agent was last launched with so theme-
                // change prompts work correctly across VS restarts. The value
                // is overwritten the next time the embedded terminal launches.
                if (_settings.LastAgentTerminalColorArgb != 0)
                {
                    _terminalAgentColor = System.Drawing.Color.FromArgb(_settings.LastAgentTerminalColorArgb);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading settings: {ex.Message}");
                _settings = new ClaudeCodeSettings();
            }
        }

        /// <summary>
        /// Removes duplicate entries from a list in place, keeping the first occurrence of each
        /// value. Used to repair settings files that accumulated repeated entries from the
        /// earlier append-on-deserialize bug (see LoadSettings).
        /// </summary>
        private static void DedupePreservingOrder<T>(System.Collections.Generic.List<T> list)
        {
            if (list == null || list.Count < 2) return;

            var seen = new System.Collections.Generic.HashSet<T>();
            int writeIndex = 0;
            for (int readIndex = 0; readIndex < list.Count; readIndex++)
            {
                if (seen.Add(list[readIndex]))
                {
                    list[writeIndex++] = list[readIndex];
                }
            }
            if (writeIndex < list.Count)
            {
                list.RemoveRange(writeIndex, list.Count - writeIndex);
            }
        }

        /// <summary>
        /// Saves default settings to create the initial configuration file
        /// </summary>
        private void SaveDefaultSettings()
        {
            try
            {
                var directory = Path.GetDirectoryName(ConfigurationPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonConvert.SerializeObject(_settings, Formatting.Indented);
                File.WriteAllText(ConfigurationPath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving default settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves current settings to the configuration file
        /// </summary>
        private void SaveSettings()
        {
            try
            {
                // Don't save settings during initialization to prevent overwriting with default values
                if (_isInitializing)
                {
                    return;
                }

                if (_settings == null)
                    _settings = new ClaudeCodeSettings();

                // Only update splitter position if we can get a valid value (not 0.0)
                // Skip when terminal is detached because the grid layout is collapsed
                // and FindSplitterPosition would return the full control height
                if (!_isTerminalDetached)
                {
                    var splitterPosition = FindSplitterPosition();
                    if (splitterPosition.HasValue && splitterPosition.Value > 0)
                    {
                        _settings.SplitterPosition = splitterPosition.Value;
                    }
                }

                // Save to file
                var directory = Path.GetDirectoryName(ConfigurationPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // During normal operation, preserve volatile per-instance fields
                // (provider, model, effort) from the disk file so that multiple VS
                // instances do not overwrite each other's selections.  On shutdown
                // (_isShuttingDown) we write everything so the next launch picks up
                // this instance's choices.
                var toSave = Newtonsoft.Json.Linq.JObject.FromObject(_settings);

                if (!_isShuttingDown)
                {
                    try
                    {
                        if (File.Exists(ConfigurationPath))
                        {
                            var diskJson = File.ReadAllText(ConfigurationPath);
                            var diskObj = Newtonsoft.Json.Linq.JObject.Parse(diskJson);

                            foreach (var field in VolatileSettingsFields)
                            {
                                if (diskObj.TryGetValue(field, out var diskValue))
                                {
                                    toSave[field] = diskValue;
                                }
                            }
                        }
                    }
                    catch (Exception diskEx)
                    {
                        // If the disk file is unreadable or corrupt, just save
                        // everything — better than losing all settings.
                        Debug.WriteLine($"Could not read disk settings for merge: {diskEx.Message}");
                    }
                }

                // Max and Ultracode are session-only. Whenever the live level is one of
                // them, persist the last durable level instead so the next VS launch does
                // not re-enter a transient effort mode. This matters on shutdown, where
                // volatile fields above are NOT preserved from disk and the in-memory
                // (possibly session-only) value would otherwise be written.
                if (IsSessionOnlyEffort(_settings.SelectedEffortLevel))
                {
                    toSave[nameof(ClaudeCodeSettings.SelectedEffortLevel)] =
                        (int)_lastPersistableEffortLevel;
                }

                var json = toSave.ToString(Formatting.Indented);
                File.WriteAllText(ConfigurationPath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving settings: {ex.Message}");
            }
        }

        #endregion

        #region Splitter Position Management

        /// <summary>
        /// True when the MainGrid is configured for a side-by-side (Vertical)
        /// split — i.e. it has been rebuilt with three columns instead of the
        /// default three rows. The grid configuration is the source of truth so
        /// the splitter math stays correct even before settings are applied.
        /// </summary>
        private bool LayoutGridIsVertical => MainGrid != null && MainGrid.ColumnDefinitions.Count >= 3;

        /// <summary>
        /// Finds the current splitter position in pixels — the size of the first
        /// slot (top row for a horizontal split, left column for a vertical one).
        /// </summary>
        /// <returns>The pixel size of the first slot, or null if unable to determine</returns>
        private double? FindSplitterPosition()
        {
            try
            {
                var grid = MainGrid;
                if (grid == null)
                {
                    return null;
                }

                if (LayoutGridIsVertical)
                {
                    if (grid.ColumnDefinitions.Count >= 3 && this.ActualWidth > 0)
                    {
                        double leftWidth = grid.ColumnDefinitions[0].ActualWidth;
                        if (leftWidth > 0)
                        {
                            return leftWidth;
                        }
                    }
                    return null;
                }

                if (grid.RowDefinitions.Count >= 3 && this.ActualHeight > 0)
                {
                    // ActualHeight is always the real rendered pixel height,
                    // regardless of whether the row uses Star or Pixel GridLength
                    double topHeight = grid.RowDefinitions[0].ActualHeight;
                    if (topHeight > 0)
                    {
                        return topHeight;
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error finding splitter position: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Sets the splitter position to the specified pixel height, clamped so
        /// the splitter and the opposite row always remain visible.
        /// </summary>
        /// <param name="position">The desired pixel size for the first slot
        /// (top row for a horizontal split, left column for a vertical one)</param>
        private void SetSplitterPosition(double position)
        {
            try
            {
                var grid = MainGrid;
                if (grid == null || position <= 0)
                {
                    return;
                }

                if (LayoutGridIsVertical)
                {
                    if (grid.ColumnDefinitions.Count >= 3)
                    {
                        // Refresh the live MaxWidth constraint before applying
                        UpdateSplitterBoundaries();

                        double clamped = ClampSplitterPosition(position);

                        // Set absolute width for the left column, keep the right
                        // column as star to fill remaining space.
                        grid.ColumnDefinitions[0].Width = new GridLength(clamped, GridUnitType.Pixel);
                        grid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
                    }
                    return;
                }

                if (grid.RowDefinitions.Count >= 3)
                {
                    // Refresh the live MaxHeight constraint before applying
                    UpdateSplitterBoundaries();

                    double clamped = ClampSplitterPosition(position);

                    // Set absolute height for the top row
                    grid.RowDefinitions[0].Height = new GridLength(clamped, GridUnitType.Pixel);
                    // Keep the bottom row as star to fill remaining space
                    grid.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error setting splitter position: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the top row's MaxHeight so WPF (and the GridSplitter during
        /// drag) cannot let the top row grow large enough to push the splitter
        /// or the bottom row out of the visible area.
        /// Returns the maximum allowed top-row height, or null when the control
        /// hasn't been measured yet.
        /// </summary>
        private double? UpdateSplitterBoundaries()
        {
            try
            {
                var grid = MainGrid;
                if (grid == null)
                {
                    return null;
                }

                if (LayoutGridIsVertical)
                {
                    if (grid.ColumnDefinitions.Count < 3)
                    {
                        return null;
                    }

                    double controlWidth = this.ActualWidth;
                    if (controlWidth <= 0)
                    {
                        return null;
                    }

                    double splitterWidth = grid.ColumnDefinitions[1].ActualWidth;
                    if (splitterWidth <= 0)
                    {
                        splitterWidth = 4;
                    }

                    double otherMinWidth = grid.ColumnDefinitions[2].MinWidth;
                    double leftMinWidth = grid.ColumnDefinitions[0].MinWidth;

                    double maxAllowedW = controlWidth - splitterWidth - otherMinWidth;
                    if (maxAllowedW < leftMinWidth)
                    {
                        maxAllowedW = leftMinWidth;
                    }

                    // Apply as MaxWidth so WPF enforces it even during a live drag
                    grid.ColumnDefinitions[0].MaxWidth = maxAllowedW;

                    var leftCol = grid.ColumnDefinitions[0];
                    if (leftCol.Width.GridUnitType == GridUnitType.Pixel &&
                        leftCol.Width.Value > maxAllowedW)
                    {
                        leftCol.Width = new GridLength(maxAllowedW, GridUnitType.Pixel);
                    }

                    return maxAllowedW;
                }

                if (grid.RowDefinitions.Count < 3)
                {
                    return null;
                }

                double controlHeight = this.ActualHeight;
                if (controlHeight <= 0)
                {
                    return null;
                }

                double splitterHeight = grid.RowDefinitions[1].ActualHeight;
                if (splitterHeight <= 0)
                {
                    splitterHeight = 4;
                }

                double otherMinHeight = grid.RowDefinitions[2].MinHeight;
                double topMinHeight = grid.RowDefinitions[0].MinHeight;

                double maxAllowed = controlHeight - splitterHeight - otherMinHeight;
                if (maxAllowed < topMinHeight)
                {
                    maxAllowed = topMinHeight;
                }

                // Apply as MaxHeight so WPF enforces it even during a live drag
                grid.RowDefinitions[0].MaxHeight = maxAllowed;

                // If the current explicit Pixel height already exceeds the new cap,
                // snap it back into range now — MaxHeight alone caps rendered size
                // but leaves RowDefinition.Height.Value stale, which later save/load
                // passes would then persist as the out-of-range value.
                var topRow = grid.RowDefinitions[0];
                if (topRow.Height.GridUnitType == GridUnitType.Pixel &&
                    topRow.Height.Value > maxAllowed)
                {
                    topRow.Height = new GridLength(maxAllowed, GridUnitType.Pixel);
                }

                return maxAllowed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating splitter boundaries: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Clamps a desired top-row pixel height so the splitter row and the
        /// bottom row (respecting its MinHeight) always remain visible within
        /// the control's current rendered height.
        /// Returns the original value unchanged when the control has not been
        /// measured yet (ActualHeight == 0).
        /// </summary>
        private double ClampSplitterPosition(double position)
        {
            try
            {
                var grid = MainGrid;
                if (grid == null)
                {
                    return position;
                }

                if (LayoutGridIsVertical)
                {
                    if (grid.ColumnDefinitions.Count < 3)
                    {
                        return position;
                    }

                    double controlWidth = this.ActualWidth;
                    if (controlWidth <= 0)
                    {
                        return position;
                    }

                    double splitterWidth = grid.ColumnDefinitions[1].ActualWidth;
                    if (splitterWidth <= 0)
                    {
                        splitterWidth = 4;
                    }

                    double otherMinWidth = grid.ColumnDefinitions[2].MinWidth;
                    double leftMinWidth = grid.ColumnDefinitions[0].MinWidth;

                    double maxAllowedW = controlWidth - splitterWidth - otherMinWidth;
                    if (maxAllowedW < leftMinWidth)
                    {
                        maxAllowedW = leftMinWidth;
                    }

                    if (position > maxAllowedW)
                    {
                        return maxAllowedW;
                    }
                    if (position < leftMinWidth)
                    {
                        return leftMinWidth;
                    }
                    return position;
                }

                if (grid.RowDefinitions.Count < 3)
                {
                    return position;
                }

                double controlHeight = this.ActualHeight;
                if (controlHeight <= 0)
                {
                    return position;
                }

                double splitterHeight = grid.RowDefinitions[1].ActualHeight;
                if (splitterHeight <= 0)
                {
                    splitterHeight = 4;
                }

                double otherMinHeight = grid.RowDefinitions[2].MinHeight;
                double topMinHeight = grid.RowDefinitions[0].MinHeight;

                double maxAllowed = controlHeight - splitterHeight - otherMinHeight;
                if (maxAllowed < topMinHeight)
                {
                    maxAllowed = topMinHeight;
                }

                if (position > maxAllowed)
                {
                    return maxAllowed;
                }
                if (position < topMinHeight)
                {
                    return topMinHeight;
                }
                return position;
            }
            catch
            {
                return position;
            }
        }

        /// <summary>
        /// Saves the splitter position after WPF has applied the final layout.
        /// This is required because GridSplitter uses a preview adorner while dragging,
        /// so the row ActualHeight can still be stale inside DragCompleted.
        /// </summary>
        private void SaveSplitterPositionAfterLayout()
        {
#pragma warning disable VSTHRD001, VSTHRD110
            _ = Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() =>
                {
                    MainGrid?.UpdateLayout();

                    var splitterPosition = FindSplitterPosition();
                    if (splitterPosition.HasValue && splitterPosition.Value > 0)
                    {
                        if (_settings == null)
                        {
                            _settings = new ClaudeCodeSettings();
                        }

                        _settings.SplitterPosition = splitterPosition.Value;
                    }

                    SaveSettings();
                }));
#pragma warning restore VSTHRD001, VSTHRD110
        }

        /// <summary>
        /// Handles splitter drag completed event to save the new position
        /// </summary>
        private void MainGridSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            SaveSplitterPositionAfterLayout();
        }

        /// <summary>
        /// Live drag of the grip on the bottom edge of the prompt box. Grows or
        /// shrinks the prompt area by moving the same prompt/terminal split the
        /// main splitter controls — the controls/chips/usage rows below the box
        /// are fixed height, so the top row tracks the box height 1:1. Only the
        /// default top/bottom layout enables the grip (see
        /// <see cref="SetPromptResizeGripVisible"/>).
        /// </summary>
        private void PromptResizeGrip_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            try
            {
                if (LayoutGridIsVertical || _settings?.InvertLayout == true)
                {
                    return;
                }

                var grid = MainGrid;
                if (grid == null || grid.RowDefinitions.Count < 3)
                {
                    return;
                }

                double current = grid.RowDefinitions[0].ActualHeight;
                if (current <= 0)
                {
                    return;
                }

                double target = current + e.VerticalChange;

                // The main split allows collapsing the prompt to 0 (to hide the
                // panel), but the grip should never shrink the box below a usable
                // size. Floor at the box's MinHeight plus the fixed controls/chips/
                // usage rows below it (the part of the section that isn't the box).
                double minTopRow = GetMinPromptTopRowHeight();
                if (target < minTopRow)
                {
                    target = minTopRow;
                }

                SetSplitterPosition(target);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error resizing prompt area via grip: {ex.Message}");
            }
        }

        /// <summary>
        /// Persists the prompt area size after a grip drag, reusing the same
        /// deferred-save path as the main splitter.
        /// </summary>
        private void PromptResizeGrip_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            SaveSplitterPositionAfterLayout();
        }

        /// <summary>
        /// Minimum height for the prompt section's top row when resized via the
        /// grip: the box's MinHeight (so the input stays usable) plus the fixed
        /// rows below it (controls, file chips, inline usage). Computed from live
        /// sizes so it adapts when the usage bars or chips are shown/hidden.
        /// </summary>
        private double GetMinPromptTopRowHeight()
        {
            try
            {
                double sectionHeight = PromptSectionGrid?.ActualHeight ?? 0;
                double boxHeight = PromptGroupBox?.ActualHeight ?? 0;

                // Everything in the section that isn't the prompt box (these rows
                // are Auto-sized, so their height is constant as the box resizes).
                double fixedBelow = sectionHeight - boxHeight;
                if (fixedBelow < 0)
                {
                    fixedBelow = 0;
                }

                double boxMin = 80;
                if (PromptSectionGrid != null && PromptSectionGrid.RowDefinitions.Count > 0)
                {
                    double declared = PromptSectionGrid.RowDefinitions[0].MinHeight;
                    if (declared > 0)
                    {
                        boxMin = declared;
                    }
                }

                return fixedBelow + boxMin;
            }
            catch
            {
                return 80;
            }
        }

        /// <summary>
        /// Shows the prompt-box resize grip only in the default top/bottom layout,
        /// where dragging the box edge meaningfully trades space with the terminal
        /// below it. Hidden for inverted (prompt on bottom) and side-by-side
        /// layouts, where the box edge isn't adjacent to the terminal boundary.
        /// </summary>
        private void SetPromptResizeGripVisible(bool visible)
        {
            if (PromptResizeGrip != null)
            {
                PromptResizeGrip.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Handles control size changes to re-apply the top-row MaxHeight cap
        /// and re-clamp the splitter position. Prevents the top row from
        /// overflowing when the tool window is shrunk or its content is grown
        /// after a larger splitter position was saved.
        /// </summary>
        private void ClaudeCodeControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            try
            {
                // A vertical split is constrained by width; a horizontal one by height.
                bool relevantChange = LayoutGridIsVertical ? e.WidthChanged : e.HeightChanged;
                if (!relevantChange || _isTerminalDetached)
                {
                    return;
                }

                // Re-applies both the Max size cap and any needed snap-back
                UpdateSplitterBoundaries();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error clamping splitter on size change: {ex.Message}");
            }
        }

        #endregion

        #region Settings Application

        /// <summary>
        /// Applies loaded settings to the UI elements
        /// </summary>
        private void ApplyLoadedSettings()
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            // Apply forced theme if configured (before layout so colors are right)
            ApplyForcedThemeResources();

            // Apply layout inversion if enabled
            ApplyLayout();

            // Reflect Send-with-Enter setting on the Send button visibility
            if (_settings != null)
            {
                SendPromptButton.Visibility = _settings.SendWithEnter ? Visibility.Collapsed : Visibility.Visible;
            }

            // Update provider selection and title
            UpdateProviderSelection();

            // Apply the user's configured order to the toolbar feature buttons + Tools dropdown.
            ReorderToolbarControls();

            // Apply visible-providers filter to the agent menu (default shows
            // only Claude Code; user-configured providers and the active one
            // also appear).
            ApplyProviderMenuVisibility();

            // Update model selection
            UpdateModelSelection();

            // Update effort selection
            UpdateEffortSelection();

            // Color the inline usage bars to match the current theme
            UpdateInlineUsageBarColors();

            // Show the custom-commands toolbar button when entries are configured
            RefreshCustomCommandsButton();

            // Show the session-history button only for Claude Code providers
            RefreshSessionHistoryButton();
        }

        #endregion

        #region Layout

        /// <summary>
        /// Applies the current layout: a Horizontal (top/bottom) or Vertical
        /// (left/right) split between the prompt panel and the terminal, with the
        /// two swapped when <see cref="ClaudeCodeSettings.InvertLayout"/> is set.
        /// Combined, the four results place the prompt panel on the Top, Bottom,
        /// Left, or Right.
        /// </summary>
        private void ApplyLayout()
        {
            try
            {
                bool invert = _settings?.InvertLayout == true;
                bool vertical = _settings?.SelectedLayoutOrientation == LayoutOrientation.Vertical;

                // Rebuild the MainGrid as rows or columns (only when it differs),
                // then orient the splitter to match.
                ConfigureMainGridForOrientation(vertical);
                ConfigureSplitterForOrientation(vertical);

                if (vertical)
                {
                    ApplyVerticalLayout(invert);
                }
                else
                {
                    ApplyHorizontalLayout(invert);
                }

                // The first slot's Min size just changed; refresh the Max cap so
                // the splitter can't be pushed out of view in the new layout.
                UpdateSplitterBoundaries();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error applying layout: {ex.Message}");
            }
        }

        /// <summary>
        /// Lays out the prompt panel and terminal stacked vertically (top/bottom).
        /// When inverted, the terminal is on top and the prompt on the bottom.
        /// </summary>
        private void ApplyHorizontalLayout(bool invert)
        {
            // All three children share the single column in a row-based grid.
            System.Windows.Controls.Grid.SetColumn(PromptSectionGrid, 0);
            System.Windows.Controls.Grid.SetColumn(TerminalGroupBox, 0);
            System.Windows.Controls.Grid.SetColumn(MainGridSplitter, 0);
            System.Windows.Controls.Grid.SetRow(MainGridSplitter, 1);

            // MinHeight 0 on both panel rows so the splitter can be dragged all
            // the way to either end, collapsing either panel.
            MainGrid.RowDefinitions[0].MinHeight = 0;
            MainGrid.RowDefinitions[2].MinHeight = 0;

            if (invert)
            {
                // Terminal on top (row 0), prompt on bottom (row 2)
                System.Windows.Controls.Grid.SetRow(PromptSectionGrid, 2);
                System.Windows.Controls.Grid.SetRow(TerminalGroupBox, 0);

                TerminalGroupBox.Margin = new Thickness(6, 6, 6, 0);
                PromptSectionGrid.Margin = new Thickness(6, 6, 6, 6);

                // Hide terminal GroupBox header (redundant with tool window title)
                ShowTerminalHeader(false);

                // Box edge isn't adjacent to the terminal here — hide the grip
                SetPromptResizeGripVisible(false);

                // Buttons+chips near the splitter, prompt box below
                ApplyPromptSectionInvertedOrder();
            }
            else
            {
                // Default: Prompt on top (row 0), terminal on bottom (row 2)
                System.Windows.Controls.Grid.SetRow(PromptSectionGrid, 0);
                System.Windows.Controls.Grid.SetRow(TerminalGroupBox, 2);

                PromptSectionGrid.Margin = new Thickness(6, 6, 6, 0);
                TerminalGroupBox.Margin = new Thickness(6);

                ShowTerminalHeader(true);

                // Prompt box sits directly above the terminal — enable the grip
                SetPromptResizeGripVisible(true);

                ApplyPromptSectionDefaultOrder();
            }
        }

        /// <summary>
        /// Lays out the prompt panel and terminal side by side (left/right).
        /// When inverted, the terminal is on the left and the prompt on the right
        /// (the most common request: prompt panel docked on the right-hand side).
        /// </summary>
        private void ApplyVerticalLayout(bool invert)
        {
            // All three children share the single row in a column-based grid.
            System.Windows.Controls.Grid.SetRow(PromptSectionGrid, 0);
            System.Windows.Controls.Grid.SetRow(TerminalGroupBox, 0);
            System.Windows.Controls.Grid.SetRow(MainGridSplitter, 0);
            System.Windows.Controls.Grid.SetColumn(MainGridSplitter, 1);

            // MinWidth 0 on both panel columns so the splitter can be dragged all
            // the way to either edge, collapsing either panel.
            MainGrid.ColumnDefinitions[0].MinWidth = 0;
            MainGrid.ColumnDefinitions[2].MinWidth = 0;

            int promptColumn = invert ? 2 : 0;
            int terminalColumn = invert ? 0 : 2;
            System.Windows.Controls.Grid.SetColumn(PromptSectionGrid, promptColumn);
            System.Windows.Controls.Grid.SetColumn(TerminalGroupBox, terminalColumn);

            // Even margins on both panels around the 4px splitter.
            PromptSectionGrid.Margin = new Thickness(6);
            TerminalGroupBox.Margin = new Thickness(6);

            // Side by side, the terminal header is not redundant — keep it visible.
            ShowTerminalHeader(true);

            // Box edge isn't adjacent to the terminal boundary here — hide the grip
            SetPromptResizeGripVisible(false);

            // Prompt box on top of its panel, controls below (the natural order).
            ApplyPromptSectionDefaultOrder();
        }

        /// <summary>
        /// Rebuilds the MainGrid definitions for the requested orientation, but
        /// only when the grid is not already configured that way (so repeated
        /// ApplyLayout calls don't wipe the live splitter sizes). Horizontal uses
        /// three rows (prompt / splitter / terminal); Vertical uses three columns.
        /// </summary>
        private void ConfigureMainGridForOrientation(bool vertical)
        {
            if (vertical)
            {
                if (MainGrid.ColumnDefinitions.Count >= 3)
                {
                    return; // already column-based
                }

                MainGrid.RowDefinitions.Clear();
                MainGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
                {
                    Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
                });

                MainGrid.ColumnDefinitions.Clear();
                MainGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
                {
                    Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star),
                    MinWidth = 0
                });
                MainGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
                {
                    Width = new System.Windows.GridLength(4)
                });
                MainGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
                {
                    Width = new System.Windows.GridLength(2, System.Windows.GridUnitType.Star),
                    MinWidth = 0
                });
            }
            else
            {
                if (MainGrid.ColumnDefinitions.Count == 0 && MainGrid.RowDefinitions.Count >= 3)
                {
                    return; // already row-based (the XAML default)
                }

                MainGrid.ColumnDefinitions.Clear();
                MainGrid.RowDefinitions.Clear();
                MainGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
                {
                    Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star),
                    MinHeight = 0
                });
                MainGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
                {
                    Height = new System.Windows.GridLength(4)
                });
                MainGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
                {
                    Height = new System.Windows.GridLength(2, System.Windows.GridUnitType.Star),
                    MinHeight = 0
                });
            }
        }

        /// <summary>
        /// Orients the GridSplitter so it resizes rows (a horizontal bar the user
        /// drags up/down) or columns (a vertical bar dragged left/right).
        /// </summary>
        private void ConfigureSplitterForOrientation(bool vertical)
        {
            if (vertical)
            {
                MainGridSplitter.Width = 4;
                MainGridSplitter.Height = double.NaN;
                MainGridSplitter.ResizeDirection = System.Windows.Controls.GridResizeDirection.Columns;
                MainGridSplitter.HorizontalAlignment = HorizontalAlignment.Center;
                MainGridSplitter.VerticalAlignment = VerticalAlignment.Stretch;
                MainGridSplitter.Cursor = System.Windows.Input.Cursors.SizeWE;
            }
            else
            {
                MainGridSplitter.Height = 4;
                MainGridSplitter.Width = double.NaN;
                MainGridSplitter.ResizeDirection = System.Windows.Controls.GridResizeDirection.Rows;
                MainGridSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
                MainGridSplitter.VerticalAlignment = VerticalAlignment.Center;
                MainGridSplitter.Cursor = System.Windows.Input.Cursors.SizeNS;
            }
        }

        /// <summary>
        /// Prompt section in its natural order: prompt box on top, then the
        /// controls row, file chips, and inline usage bars below.
        /// </summary>
        private void ApplyPromptSectionDefaultOrder()
        {
            System.Windows.Controls.Grid.SetRow(PromptGroupBox, 0);
            System.Windows.Controls.Grid.SetRow(ControlsRow, 1);
            System.Windows.Controls.Grid.SetRow(CheckboxRow, 2);
            PromptSectionGrid.RowDefinitions[0].Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
            PromptSectionGrid.RowDefinitions[0].MinHeight = 80;
            PromptSectionGrid.RowDefinitions[2].Height = new System.Windows.GridLength(0, System.Windows.GridUnitType.Auto);
            PromptSectionGrid.RowDefinitions[2].MinHeight = 0;

            CheckboxRow.Margin = new Thickness(0, 4, 0, 2);
            ControlsRow.Margin = new Thickness(0, 4, 0, 0);
            PromptGroupBox.Margin = new Thickness(0, 0, 0, 2);
        }

        /// <summary>
        /// Prompt section reordered for the inverted horizontal layout: the
        /// buttons and file chips sit at the top (next to the splitter) and the
        /// prompt box fills the space below.
        /// </summary>
        private void ApplyPromptSectionInvertedOrder()
        {
            System.Windows.Controls.Grid.SetRow(CheckboxRow, 0);
            System.Windows.Controls.Grid.SetRow(ControlsRow, 1);
            System.Windows.Controls.Grid.SetRow(PromptGroupBox, 2);
            PromptSectionGrid.RowDefinitions[0].Height = new System.Windows.GridLength(0, System.Windows.GridUnitType.Auto);
            PromptSectionGrid.RowDefinitions[0].MinHeight = 0;
            PromptSectionGrid.RowDefinitions[2].Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
            PromptSectionGrid.RowDefinitions[2].MinHeight = 80;

            CheckboxRow.Margin = new Thickness(0, 0, 0, 4);
            ControlsRow.Margin = new Thickness(0, 0, 0, 4);
            PromptGroupBox.Margin = new Thickness(0, 2, 0, 0);
        }

        /// <summary>
        /// Shows or hides the terminal GroupBox header. Hidden only when the
        /// terminal is on top (inverted horizontal layout), where the header is
        /// redundant with the tool window title.
        /// </summary>
        private void ShowTerminalHeader(bool show)
        {
            if (show)
            {
                TerminalGroupBox.Header = new System.Windows.Controls.TextBlock
                {
                    Text = GetCurrentProviderName(),
                    Opacity = 0.93
                };
            }
            else
            {
                TerminalGroupBox.Header = null;
            }
        }

        /// <summary>
        /// Applies the user's layout choice (orientation and/or invert) and
        /// re-balances the splitter so the layout looks natural after the change,
        /// giving the terminal the larger share. Called from the consolidated
        /// Settings dialog when the prompt panel position is changed.
        /// </summary>
        internal void ApplyLayoutSettingsChange()
        {
            if (_settings == null) return;

            // Reconfigure the grid for the new orientation/invert first.
            ApplyLayout();

            bool invert = _settings.InvertLayout;

            // Reset to proportional sizing so the layout looks natural after the
            // change. The terminal slot gets the larger (2*) share; the prompt the
            // smaller (1*). The terminal is the first slot when inverted.
            if (LayoutGridIsVertical)
            {
                MainGrid.ColumnDefinitions[0].Width = new System.Windows.GridLength(invert ? 2 : 1, System.Windows.GridUnitType.Star);
                MainGrid.ColumnDefinitions[2].Width = new System.Windows.GridLength(invert ? 1 : 2, System.Windows.GridUnitType.Star);
            }
            else
            {
                MainGrid.RowDefinitions[0].Height = new System.Windows.GridLength(invert ? 2 : 1, System.Windows.GridUnitType.Star);
                MainGrid.RowDefinitions[2].Height = new System.Windows.GridLength(invert ? 1 : 2, System.Windows.GridUnitType.Star);
            }
        }

        #endregion
    }
}
