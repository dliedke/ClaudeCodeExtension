/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Liedke
 *
 * Copyright © Daniel Liedke 2025
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Liedke is strictly forbidden.
 *
 * Purpose: Main user control for the Claude Code extension for VS.NET 2022
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace ClaudeCodeVS
{
    public enum AiProvider
    {
        ClaudeCode,
        Codex
    }

    public class ClaudeCodeSettings
    {
        public bool SendWithEnter { get; set; } = true;
        public double SplitterPosition { get; set; } = 236.0; // Default pixel height for first row
        public AiProvider SelectedProvider { get; set; } = AiProvider.ClaudeCode;
    }

    public partial class ClaudeCodeControl : UserControl, IDisposable
    {
        private System.Windows.Forms.Panel terminalPanel;
        private ClaudeCodeVS.ClaudeCodeToolWindow _toolWindow;
        private Process cmdProcess;
        private IntPtr terminalHandle;
        private readonly List<string> attachedImagePaths = new List<string>();
        private string tempImageDirectory;
        private ClaudeCodeSettings _settings;
        private bool _isInitializing = true;
        private IVsSolutionEvents solutionEvents;
        private uint solutionEventsCookie;
        private string _lastWorkspaceDirectory;
        private const string ConfigurationFileName = "claudecode-settings.json";
        private static readonly string ConfigurationPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeCodeExtension",
            ConfigurationFileName);
        private System.Drawing.Color _lastTerminalColor = System.Drawing.Color.Black;
        private DispatcherTimer _themeCheckTimer;
        private static bool _claudeNotificationShown = false;
        private static bool _codexNotificationShown = false;

        [DllImport("user32.dll")]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const int SW_SHOW = 5;
        private const int SW_HIDE = 0;

        private const int GWL_STYLE = -16;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_MINIMIZE = 0x20000000;
        private const int WS_MAXIMIZE = 0x01000000;
        private const int WS_SYSMENU = 0x00080000;

        public ClaudeCodeControl()
        {
            InitializeComponent();
            InitializeTempDirectory();
            SetupSolutionEvents();
            SetupThemeChangeEvents();

            Loaded += ClaudeCodeControl_Loaded;
            Unloaded += ClaudeCodeControl_Unloaded;
        }

        public void SetToolWindow(ClaudeCodeVS.ClaudeCodeToolWindow toolWindow)
        {
            _toolWindow = toolWindow;
        }

        private void InitializeTempDirectory()
        {
            try
            {
                tempImageDirectory = Path.Combine(Path.GetTempPath(), "ClaudeCodeVS", Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempImageDirectory);
                Debug.WriteLine($"Temp directory created: {tempImageDirectory}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error creating temp directory: {ex.Message}");
                // Fallback to a simpler path
                tempImageDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempImageDirectory);
                Debug.WriteLine($"Fallback temp directory created: {tempImageDirectory}");
            }
        }

        private void SetupSolutionEvents()
        {
            try
            {
                ThreadHelper.JoinableTaskFactory.Run(async delegate
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    var solution = Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
                    if (solution != null)
                    {
                        solutionEvents = new SolutionEventsHandler(this);
                        solution.AdviseSolutionEvents(solutionEvents, out solutionEventsCookie);
                        Debug.WriteLine("Solution events registered successfully");
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error setting up solution events: {ex.Message}");
            }
        }

        private void SetupThemeChangeEvents()
        {
            try
            {
                // Listen for when the control becomes visible to update theme and initialize terminal
                this.IsVisibleChanged += OnVisibilityChanged;

                // Set up a timer to periodically check for theme changes
                _themeCheckTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(2)
                };
                _themeCheckTimer.Tick += (s, e) => CheckAndUpdateTheme();
                _themeCheckTimer.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error setting up theme change events: {ex.Message}");
            }
        }

        private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (this.IsVisible)
            {
                UpdateTerminalTheme();

                // Initialize terminal only when control becomes visible
                // Terminal initialization is now handled in ClaudeCodeControl_Loaded
                // after settings are properly loaded
            }
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(ConfigurationPath))
                {
                    var json = File.ReadAllText(ConfigurationPath);
                    _settings = JsonConvert.DeserializeObject<ClaudeCodeSettings>(json) ?? new ClaudeCodeSettings();
                    Debug.WriteLine($"Loaded settings: SendWithEnter={_settings.SendWithEnter}, SplitterPosition={_settings.SplitterPosition}");
                }
                else
                {
                    _settings = new ClaudeCodeSettings();
                    Debug.WriteLine($"No settings file found, using defaults: SendWithEnter={_settings.SendWithEnter}, SplitterPosition={_settings.SplitterPosition}");

                    // Save the default settings to create the file
                    SaveDefaultSettings();
                }

                // Apply loaded settings to UI
                SendWithEnterCheckBox.IsChecked = _settings.SendWithEnter;
                Debug.WriteLine($"Set checkbox to: {_settings.SendWithEnter}");

                if (_settings.SplitterPosition > 0)
                {
                    SetSplitterPosition(_settings.SplitterPosition);
                    Debug.WriteLine($"Set splitter position to: {_settings.SplitterPosition}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading settings: {ex.Message}");
                _settings = new ClaudeCodeSettings();
            }
        }

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
                Debug.WriteLine($"Default settings created at: {ConfigurationPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving default settings: {ex.Message}");
            }
        }

        private void SaveSettings()
        {
            try
            {
                // Don't save settings during initialization to prevent overwriting with default values
                if (_isInitializing)
                {
                    Debug.WriteLine("Skipping save during initialization");
                    return;
                }

                if (_settings == null)
                    _settings = new ClaudeCodeSettings();

                // Update settings from UI
                _settings.SendWithEnter = SendWithEnterCheckBox.IsChecked == true;
                Debug.WriteLine($"Saving SendWithEnter: {_settings.SendWithEnter}");
                Debug.WriteLine($"Saving SelectedProvider: {_settings.SelectedProvider}");

                // Only update splitter position if we can get a valid value (not 0.0)
                var splitterPosition = FindSplitterPosition();
                if (splitterPosition.HasValue && splitterPosition.Value > 0)
                {
                    _settings.SplitterPosition = splitterPosition.Value;
                    Debug.WriteLine($"Saving splitter position: {_settings.SplitterPosition}");
                }
                else
                {
                    Debug.WriteLine($"Not saving splitter position, got: {splitterPosition}");
                }

                // Save to file
                var directory = Path.GetDirectoryName(ConfigurationPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonConvert.SerializeObject(_settings, Formatting.Indented);
                File.WriteAllText(ConfigurationPath, json);
                Debug.WriteLine($"Settings saved to: {ConfigurationPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving settings: {ex.Message}");
            }
        }

        private double? FindSplitterPosition()
        {
            try
            {
                var grid = MainGrid;
                if (grid?.RowDefinitions?.Count >= 3 && this.ActualHeight > 0)
                {
                    var topRow = grid.RowDefinitions[0];
                    var splitterRow = grid.RowDefinitions[1];
                    var bottomRow = grid.RowDefinitions[2];

                    // Calculate the actual height of the top row
                    double topHeight = 0;
                    if (topRow.Height.IsStar)
                    {
                        double totalStars = topRow.Height.Value + bottomRow.Height.Value;
                        topHeight = (topRow.Height.Value / totalStars) * (this.ActualHeight - splitterRow.Height.Value);
                    }
                    else if (topRow.Height.IsAbsolute)
                    {
                        topHeight = topRow.Height.Value;
                    }

                    // Return the actual pixel height for saving
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

        private void SetSplitterPosition(double position)
        {
            try
            {
                var grid = MainGrid;
                if (grid?.RowDefinitions?.Count >= 3 && position > 0)
                {
                    // Set absolute height for the top row
                    grid.RowDefinitions[0].Height = new GridLength(position, GridUnitType.Pixel);
                    // Keep the bottom row as star to fill remaining space
                    grid.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error setting splitter position: {ex.Message}");
            }
        }

        private void MainGridSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            SaveSettings();
        }

        private void ClaudeCodeControl_Loaded(object sender, RoutedEventArgs e)
        {
            // Load settings after UI is fully loaded
            LoadSettings();

            // Apply settings and initialize terminal after settings are loaded
            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ApplyLoadedSettings();
                await InitializeTerminalAsync();
            });

            // Mark initialization as complete to allow settings saving
            _isInitializing = false;
        }

        private void ApplyLoadedSettings()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Ensure the send button visibility matches the checkbox state
            if (SendWithEnterCheckBox.IsChecked == true)
            {
                SendPromptButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                SendPromptButton.Visibility = Visibility.Visible;
            }

            // Update provider selection and title
            UpdateProviderSelection();
        }

        private System.Drawing.Color GetTerminalBackgroundColor()
        {
            try
            {
                // Get the VS theme color for window background
                var brush = (System.Windows.Media.SolidColorBrush)FindResource(Microsoft.VisualStudio.Shell.VsBrushes.WindowKey);
                var wpfColor = brush.Color;

                // Convert WPF color to System.Drawing color
                return System.Drawing.Color.FromArgb(wpfColor.A, wpfColor.R, wpfColor.G, wpfColor.B);
            }
            catch
            {
                // Fallback to black if theme color cannot be retrieved
                return System.Drawing.Color.Black;
            }
        }


        private void UpdateTerminalTheme()
        {
            if (terminalPanel != null)
            {
                var newColor = GetTerminalBackgroundColor();
                if (terminalPanel.BackColor != newColor)
                {
                    terminalPanel.BackColor = newColor;
                    _lastTerminalColor = newColor;
                    Debug.WriteLine($"Terminal theme updated to: {newColor}");
                }
            }
        }

        private async Task<bool> IsClaudeCmdAvailableAsync()
        {
            try
            {
                // Check if claude.cmd is available in PATH using 'where' command
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c where claude.cmd",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    // Use async wait to avoid blocking UI thread
                    var completed = await Task.Run(() =>
                    {
                        return process.WaitForExit(3000); // 3 second timeout
                    });

                    if (!completed)
                    {
                        try { process.Kill(); } catch { }
                        Debug.WriteLine("Claude check timed out");
                        return false;
                    }

                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();

                    Debug.WriteLine($"Claude check - Exit code: {process.ExitCode}");
                    Debug.WriteLine($"Claude check - Output: {output}");
                    Debug.WriteLine($"Claude check - Error: {error}");

                    bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
                    Debug.WriteLine($"Claude availability result: {isAvailable}");

                    return isAvailable;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for claude.cmd: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> IsCodexCmdAvailableAsync()
        {
            try
            {
                // Check if codex.cmd is available in user's npm global directory
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string codexPath = Path.Combine(userProfile, "AppData", "Roaming", "npm", "codex.cmd");

                // First check the known path
                if (File.Exists(codexPath))
                {
                    Debug.WriteLine($"Codex found at: {codexPath}");
                    return true;
                }

                // Also check if codex.cmd is available in PATH using 'where' command
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c where codex.cmd",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    // Use async wait to avoid blocking UI thread
                    var completed = await Task.Run(() =>
                    {
                        return process.WaitForExit(3000); // 3 second timeout
                    });

                    if (!completed)
                    {
                        try { process.Kill(); } catch { }
                        Debug.WriteLine("Codex check timed out");
                        return false;
                    }

                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();

                    Debug.WriteLine($"Codex check - Exit code: {process.ExitCode}");
                    Debug.WriteLine($"Codex check - Output: {output}");
                    Debug.WriteLine($"Codex check - Error: {error}");

                    bool isAvailable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
                    Debug.WriteLine($"Codex availability result: {isAvailable}");

                    return isAvailable;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for codex.cmd: {ex.Message}");
                return false;
            }
        }

        private void ShowClaudeInstallationInstructions()
        {
            const string setupUrl = "https://docs.claude.com/en/docs/claude-code/setup";
            const string message = "Claude Code is not installed. A regular CMD terminal will be used instead.\n\n" +
                                   "To get the full Claude Code experience, you can install it with:\n" +
                                   "npm install -g @anthropic-ai/claude-code\n\n" +
                                   "Would you like to open the setup documentation for more details?";

            var result = MessageBox.Show(message, "Claude Code Installation",
                                       MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = setupUrl,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to open setup URL: {ex.Message}");
                    MessageBox.Show($"Please visit: {setupUrl}", "Setup URL",
                                  MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private void ShowCodexInstallationInstructions()
        {
            const string setupUrl = "https://developers.openai.com/codex/cli/";
            const string message = "Codex CLI is not installed. A regular CMD terminal will be used instead.\n\n" +
                                   "To get the full Codex experience, you can install it by following the instructions at:\n" +
                                   "https://developers.openai.com/codex/cli/\n\n" +
                                   "Would you like to open the setup documentation for more details?";

            var result = MessageBox.Show(message, "Codex CLI Installation",
                                       MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = setupUrl,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to open setup URL: {ex.Message}");
                    MessageBox.Show($"Please visit: {setupUrl}", "Setup URL",
                                  MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private void CheckAndUpdateTheme()
        {
            try
            {
                var currentColor = GetTerminalBackgroundColor();
                if (currentColor != _lastTerminalColor)
                {
                    UpdateTerminalTheme();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking theme: {ex.Message}");
            }
        }

        private async Task InitializeTerminalAsync()
        {
            try
            {
                // Determine which provider to use based on settings
                bool useCodex = _settings?.SelectedProvider == AiProvider.Codex;
                bool providerAvailable = false;

                Debug.WriteLine($"User selected provider: {(useCodex ? "Codex" : "Claude Code")}");

                if (useCodex)
                {
                    Debug.WriteLine("Checking Codex availability...");
                    providerAvailable = await IsCodexCmdAvailableAsync();
                    Debug.WriteLine($"Codex available: {providerAvailable}");
                }
                else
                {
                    Debug.WriteLine("Checking Claude Code availability...");
                    providerAvailable = await IsClaudeCmdAvailableAsync();
                    Debug.WriteLine($"Claude Code available: {providerAvailable}");
                }

                // Switch to main thread for UI operations
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Note: Installation instructions are now shown in the main logic below

                // Ensure TerminalHost is available
                if (TerminalHost == null)
                {
                    Debug.WriteLine("Error: TerminalHost is null");
                    return;
                }

                terminalPanel = new System.Windows.Forms.Panel
                {
                    Dock = System.Windows.Forms.DockStyle.Fill,
                    BackColor = GetTerminalBackgroundColor()
                };

                TerminalHost.Child = terminalPanel;

                if (terminalPanel?.Handle == IntPtr.Zero)
                {
                    Debug.WriteLine("Warning: terminalPanel handle not yet created, waiting...");
                    await Task.Delay(100);
                }

                terminalPanel.Resize += (s, e) => ResizeEmbeddedTerminal();

                await Task.Delay(500);

                // Start the selected provider if available, otherwise show message and use regular CMD
                if (useCodex)
                {
                    if (providerAvailable)
                    {
                        Debug.WriteLine("Starting Codex terminal...");
                        await StartEmbeddedTerminalAsync(false, true); // Codex
                        UpdateToolWindowTitle("Codex");
                    }
                    else
                    {
                        Debug.WriteLine("Codex not available, showing installation instructions...");
                        if (!_codexNotificationShown)
                        {
                            _codexNotificationShown = true;
                            ShowCodexInstallationInstructions();
                        }
                        await StartEmbeddedTerminalAsync(false, false); // Regular CMD
                    }
                }
                else
                {
                    if (providerAvailable)
                    {
                        Debug.WriteLine("Starting Claude Code terminal...");
                        await StartEmbeddedTerminalAsync(true, false); // Claude Code
                        UpdateToolWindowTitle("Claude Code");
                    }
                    else
                    {
                        Debug.WriteLine("Claude Code not available, showing installation instructions...");
                        if (!_claudeNotificationShown)
                        {
                            _claudeNotificationShown = true;
                            ShowClaudeInstallationInstructions();
                        }
                        await StartEmbeddedTerminalAsync(false, false); // Regular CMD
                    }
                }
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Debug.WriteLine($"Error in InitializeTerminalAsync: {ex.Message}");
                Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                MessageBox.Show($"Failed to initialize terminal: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task StartEmbeddedTerminalAsync(bool claudeAvailable = true, bool useCodex = false)
        {
            try
            {
                string workspaceDir = await GetWorkspaceDirectoryAsync();
                if (string.IsNullOrEmpty(workspaceDir))
                {
                    Debug.WriteLine("Warning: Workspace directory is null or empty");
                    workspaceDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                }

                _lastWorkspaceDirectory = workspaceDir;

                if (cmdProcess != null && !cmdProcess.HasExited)
                {
                    try
                    {
                        cmdProcess.Kill();
                        cmdProcess.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error disposing previous process: {ex.Message}");
                    }
                    cmdProcess = null;
                }

                string terminalCommand;
                if (useCodex)
                {
                    Debug.WriteLine($"Starting Codex in directory: {workspaceDir}");

                    // Try known Codex path first
                    string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    string codexPath = Path.Combine(userProfile, "AppData", "Roaming", "npm", "codex.cmd");

                    if (File.Exists(codexPath))
                    {
                        terminalCommand = $"/k cd /d \"{workspaceDir}\" && \"{codexPath}\"";
                    }
                    else
                    {
                        terminalCommand = $"/k cd /d \"{workspaceDir}\" && codex.cmd";
                    }
                }
                else if (claudeAvailable)
                {
                    Debug.WriteLine($"Starting Claude in directory: {workspaceDir}");
                    terminalCommand = "/k cd /d \"" + workspaceDir + "\" && claude.cmd";
                }
                else
                {
                    Debug.WriteLine($"Starting regular CMD in directory: {workspaceDir}");
                    terminalCommand = "/k cd /d \"" + workspaceDir + "\"";
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = terminalCommand,
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    WindowStyle = ProcessWindowStyle.Minimized,
                    WorkingDirectory = workspaceDir
                };

                await Task.Run(() =>
                {
                    try
                    {
                        cmdProcess = new Process { StartInfo = startInfo };
                        cmdProcess.Start();
                        Debug.WriteLine($"Process started with ID: {cmdProcess.Id}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error starting process: {ex.Message}");
                        throw;
                    }
                });

                if (cmdProcess == null)
                {
                    throw new InvalidOperationException("Failed to create terminal process");
                }

                var hwnd = FindMainWindowHandleByPid(cmdProcess.Id, timeoutMs: 7000, pollIntervalMs: 50);
                terminalHandle = hwnd;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
                {
                    if (terminalPanel?.Handle == null || terminalPanel.Handle == IntPtr.Zero)
                    {
                        Debug.WriteLine("Warning: terminalPanel.Handle is null or invalid");
                        return;
                    }

                    try
                    {
                        // Hide the window immediately to prevent blinking
                        ShowWindow(terminalHandle, SW_HIDE);

                        // Embed the window
                        SetParent(terminalHandle, terminalPanel.Handle);

                        // Remove window decorations
                        SetWindowLong(terminalHandle, GWL_STYLE,
                            GetWindowLong(terminalHandle, GWL_STYLE) & ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZE | WS_MAXIMIZE | WS_SYSMENU));

                        // Now show it in the embedded context
                        ShowWindow(terminalHandle, SW_SHOW);
                        ResizeEmbeddedTerminal();

                        Debug.WriteLine("Terminal embedded successfully");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error embedding terminal window: {ex.Message}");
                        throw;
                    }
                }
                else
                {
                    Debug.WriteLine("Could not find CMD window to embed. Terminal may not be available.");
                }
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                MessageBox.Show($"Failed to start embedded terminal: {ex.Message}",
                                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ResizeEmbeddedTerminal()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle) && terminalPanel != null)
            {
                SetWindowPos(terminalHandle, IntPtr.Zero, 0, 0,
                            terminalPanel.Width, terminalPanel.Height,
                            SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }

        private async Task<string> GetWorkspaceDirectoryAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte?.Solution?.FullName != null && !string.IsNullOrEmpty(dte.Solution.FullName))
                {
                    string solutionDir = Path.GetDirectoryName(dte.Solution.FullName);
                    if (Directory.Exists(solutionDir))
                    {
                        return solutionDir;
                    }
                }

                if (dte?.ActiveDocument?.ProjectItem?.ContainingProject?.FullName != null)
                {
                    string projectDir = Path.GetDirectoryName(dte.ActiveDocument.ProjectItem.ContainingProject.FullName);
                    if (Directory.Exists(projectDir))
                    {
                        return projectDir;
                    }
                }

                var solution = Package.GetGlobalService(typeof(Microsoft.VisualStudio.Shell.Interop.SVsSolution)) as Microsoft.VisualStudio.Shell.Interop.IVsSolution;
                if (solution != null)
                {
                    solution.GetSolutionInfo(out string solutionDir, out string solutionFile, out string userOptsFile);
                    if (!string.IsNullOrEmpty(solutionDir) && Directory.Exists(solutionDir))
                    {
                        return solutionDir;
                    }
                }

                string currentDir = Environment.CurrentDirectory;
                if (Directory.Exists(currentDir) &&
                    (Directory.GetFiles(currentDir, "*.sln").Length > 0 ||
                     Directory.GetFiles(currentDir, "*.csproj").Length > 0 ||
                     Directory.GetFiles(currentDir, "*.vbproj").Length > 0))
                {
                    return currentDir;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting workspace directory: {ex.Message}");
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        public async Task OnWorkspaceDirectoryChangedAsync()
        {
            try
            {
                string newWorkspaceDir = await GetWorkspaceDirectoryAsync();

                // Only restart if the directory actually changed
                if (_lastWorkspaceDirectory != newWorkspaceDir)
                {
                    Debug.WriteLine($"Workspace directory changed from '{_lastWorkspaceDirectory}' to '{newWorkspaceDir}'. Restarting terminal...");
                    _lastWorkspaceDirectory = newWorkspaceDir;

                    // Determine which provider to use based on settings
                    bool useCodex = _settings?.SelectedProvider == AiProvider.Codex;
                    bool claudeAvailable = false;
                    bool codexAvailable = false;

                    Debug.WriteLine($"User selected provider: {(useCodex ? "Codex" : "Claude Code")}");

                    if (useCodex)
                    {
                        Debug.WriteLine("Checking Codex availability for workspace change...");
                        codexAvailable = await IsCodexCmdAvailableAsync();
                        Debug.WriteLine($"Codex available: {codexAvailable}");
                    }
                    else
                    {
                        Debug.WriteLine("Checking Claude Code availability for workspace change...");
                        claudeAvailable = await IsClaudeCmdAvailableAsync();
                        Debug.WriteLine($"Claude available: {claudeAvailable}");
                    }

                    // Switch to main thread for UI operations
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    // Restart with the selected provider if available, otherwise show message and use regular CMD
                    if (useCodex)
                    {
                        if (codexAvailable)
                        {
                            Debug.WriteLine("Restarting Codex terminal in new directory...");
                            await StartEmbeddedTerminalAsync(false, true); // Codex
                        }
                        else
                        {
                            Debug.WriteLine("Codex not available, showing installation instructions...");
                            if (!_codexNotificationShown)
                            {
                                _codexNotificationShown = true;
                                ShowCodexInstallationInstructions();
                            }
                            await StartEmbeddedTerminalAsync(false, false); // Regular CMD
                        }
                    }
                    else
                    {
                        if (claudeAvailable)
                        {
                            Debug.WriteLine("Restarting Claude Code terminal in new directory...");
                            await StartEmbeddedTerminalAsync(true, false); // Claude Code
                        }
                        else
                        {
                            Debug.WriteLine("Claude Code not available, showing installation instructions...");
                            if (!_claudeNotificationShown)
                            {
                                _claudeNotificationShown = true;
                                ShowClaudeInstallationInstructions();
                            }
                            await StartEmbeddedTerminalAsync(false, false); // Regular CMD
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling workspace directory change: {ex.Message}");
            }
        }

        // ===== Send-with-Enter behavior =====

        private void PromptTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                bool sendWithEnter = SendWithEnterCheckBox.IsChecked == true;

                Debug.WriteLine($"Enter pressed - SendWithEnter: {sendWithEnter}, Modifiers: {Keyboard.Modifiers}");

                if (sendWithEnter)
                {
                    // When SendWithEnter is enabled:
                    // - Enter sends the prompt
                    // - Shift+Enter or Ctrl+Enter creates new line
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ||
                        (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                    {
                        // Allow newline insertion with modifiers
                        Debug.WriteLine("Allowing newline with modifier key");
                        return;
                    }
                    else
                    {
                        // Plain Enter sends the prompt
                        Debug.WriteLine("Sending prompt with Enter");
                        e.Handled = true; // Prevent default newline behavior
                        SendButton_Click(sender, null);
                    }
                }
                else
                {
                    // When SendWithEnter is disabled, let default behavior handle Enter (newlines)
                    Debug.WriteLine("SendWithEnter disabled - allowing newline");
                }
            }
        }

        private void PromptTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Handle SendWithEnter functionality in PreviewKeyDown to catch it before TextBox handles it
            if (e.Key == Key.Enter)
            {
                bool sendWithEnter = SendWithEnterCheckBox.IsChecked == true;

                Debug.WriteLine($"PreviewKeyDown Enter pressed - SendWithEnter: {sendWithEnter}, Modifiers: {Keyboard.Modifiers}");

                if (sendWithEnter)
                {
                    // When SendWithEnter is enabled:
                    // - Enter sends the prompt
                    // - Shift+Enter or Ctrl+Enter creates new line
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ||
                        (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                    {
                        // Allow newline insertion with modifiers
                        Debug.WriteLine("PreviewKeyDown: Allowing newline with modifier key");
                        return;
                    }
                    else
                    {
                        // Plain Enter sends the prompt
                        Debug.WriteLine("PreviewKeyDown: Sending prompt with Enter");
                        e.Handled = true; // Prevent default newline behavior
                        SendButton_Click(sender, null);
                        return;
                    }
                }
                // When SendWithEnter is disabled, let default behavior handle Enter (newlines)
            }

            // Preserve paste-image shortcut even with new behavior
            if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (TryPasteImage())
                {
                    e.Handled = true;
                }
            }
        }

        // Send-with-Enter toggle just controls the Send button visibility
        private void SendWithEnterCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            SendPromptButton.Visibility = Visibility.Collapsed;
            SaveSettings();
        }

        private void SendWithEnterCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            SendPromptButton.Visibility = Visibility.Visible;
            SaveSettings();
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string prompt = PromptTextBox.Text.Trim();
                if (string.IsNullOrEmpty(prompt))
                {
                    MessageBox.Show("Please enter a prompt.", "No Prompt", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                StringBuilder fullPrompt = new StringBuilder();

                if (attachedImagePaths.Any())
                {
                    fullPrompt.AppendLine("Images attached:");
                    foreach (string imagePath in attachedImagePaths)
                    {
                        try
                        {
                            string fileName = Path.GetFileName(imagePath);
                            string tempPath = Path.Combine(tempImageDirectory, fileName);

                            File.Copy(imagePath, tempPath, true);

                            fullPrompt.AppendLine($"  - {tempPath}");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error copying image {imagePath}: {ex.Message}");
                            fullPrompt.AppendLine($"  - {imagePath}");
                        }
                    }
                    fullPrompt.AppendLine();
                }

                fullPrompt.AppendLine(prompt);

                SendTextToTerminal(fullPrompt.ToString());

                PromptTextBox.Clear();
                ClearAttachedImages();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error sending prompt: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ===== Images (paste, add, chips) =====

        private bool TryPasteImage()
        {
            try
            {
                if (attachedImagePaths.Count >= 3)
                {
                    MessageBox.Show("Maximum of 3 images can be attached.", "Image Limit", MessageBoxButton.OK, MessageBoxImage.Information);
                    return false;
                }

                System.Windows.Media.Imaging.BitmapSource image = null;

                if (Clipboard.ContainsImage())
                {
                    image = Clipboard.GetImage();
                }
                else if (Clipboard.ContainsData(DataFormats.Bitmap))
                {
                    var bitmapData = Clipboard.GetData(DataFormats.Bitmap);
                    if (bitmapData is System.Drawing.Bitmap bitmap)
                    {
                        var handle = bitmap.GetHbitmap();
                        try
                        {
                            image = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                                handle, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                        }
                        finally
                        {
                            DeleteObject(handle);
                        }
                    }
                }
                else if (Clipboard.ContainsData("PNG"))
                {
                    var pngData = Clipboard.GetData("PNG") as MemoryStream;
                    if (pngData != null)
                    {
                        image = System.Windows.Media.Imaging.BitmapFrame.Create(pngData);
                    }
                }

                if (image != null)
                {
                    // Ensure temp directory exists
                    if (!Directory.Exists(tempImageDirectory))
                    {
                        Debug.WriteLine($"Temp directory missing, recreating: {tempImageDirectory}");
                        Directory.CreateDirectory(tempImageDirectory);
                    }

                    string fileName = $"pasted_image_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
                    string imagePath = Path.Combine(tempImageDirectory, fileName);

                    using (var fileStream = new FileStream(imagePath, FileMode.Create))
                    {
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                        encoder.Save(fileStream);
                    }

                    attachedImagePaths.Add(imagePath);
                    UpdateImageDropDisplay();

                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error pasting image: {ex.Message}");
                MessageBox.Show($"Error pasting image: {ex.Message}", "Paste Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return false;
        }

        private void ImageDropBorder_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (attachedImagePaths.Count >= 3)
                {
                    MessageBox.Show("Maximum of 3 images can be attached.", "Image Limit", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                OpenFileDialog openFileDialog = new OpenFileDialog
                {
                    Filter = "Image files (*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp|All files (*.*)|*.*",
                    Multiselect = true
                };

                if (openFileDialog.ShowDialog() == true)
                {
                    foreach (string filename in openFileDialog.FileNames)
                    {
                        if (attachedImagePaths.Count >= 3)
                        {
                            MessageBox.Show($"Maximum of 3 images can be attached. Only the first {3 - attachedImagePaths.Count} selected images will be added.", "Image Limit", MessageBoxButton.OK, MessageBoxImage.Information);
                            break;
                        }
                        attachedImagePaths.Add(filename);
                    }
                    UpdateImageDropDisplay();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error selecting files: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateImageDropDisplay()
        {
            AttachedImagesPanel.Children.Clear();

            if (attachedImagePaths.Any())
            {
                foreach (var path in attachedImagePaths.ToList())
                {
                    var chip = new Border { Style = (Style)FindResource("ChipBorder") };

                    var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    var nameBlock = new TextBlock
                    {
                        Text = System.IO.Path.GetFileName(path),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    nameBlock.SetResourceReference(TextBlock.ForegroundProperty, Microsoft.VisualStudio.Shell.VsBrushes.ToolWindowTextKey);
                    var removeBtn = new Button
                    {
                        Style = (Style)FindResource("ChipRemoveButton"),
                        Tag = path
                    };
                    removeBtn.Click += (s, e) =>
                    {
                        var p = (string)((Button)s).Tag;
                        attachedImagePaths.Remove(p);
                        UpdateImageDropDisplay();
                    };

                    sp.Children.Add(nameBlock);
                    sp.Children.Add(removeBtn);
                    chip.Child = sp;

                    AttachedImagesPanel.Children.Add(chip);
                }
            }
        }

        private void ClearAttachedImages()
        {
            attachedImagePaths.Clear();
            UpdateImageDropDisplay();
        }

        private bool IsImageFile(string filePath)
        {
            string[] imageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tiff", ".webp" };
            string extension = Path.GetExtension(filePath).ToLower();
            return imageExtensions.Contains(extension);
        }

        // ===== Terminal I/O =====

        private void SendTextToTerminal(string text)
        {
            try
            {
                if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
                {
                    Clipboard.SetText(text);

                    SetForegroundWindow(terminalHandle);
                    SetFocus(terminalHandle);

                    System.Threading.Thread.Sleep(200);

                    GetWindowRect(terminalHandle, out RECT rect);
                    int centerX = rect.Left + (rect.Right - rect.Left) / 2;
                    int centerY = rect.Top + (rect.Bottom - rect.Top) / 2;

                    SendRightClick(centerX, centerY);

                    System.Threading.Thread.Sleep(1000);
                    SendEnterKey();
                }
                else
                {
                    MessageBox.Show("Terminal is not available. Please restart the terminal.",
                                  "Terminal Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error sending text to terminal: {ex.Message}",
                              "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SendRightClick(int x, int y)
        {
            SetCursorPos(x, y);
            System.Threading.Thread.Sleep(50);
            mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(50);
            mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
        }

        private void SendEnterKey()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                // Check if we're using Codex
                bool isCodex = _settings?.SelectedProvider == AiProvider.Codex;

                if (isCodex)
                {
                    // For Codex, use KEYDOWN/KEYUP approach
                    SendEnterKeyDownUp();
                }
                else
                {
                    // For Claude Code, use single WM_CHAR
                    PostMessage(terminalHandle, WM_CHAR, new IntPtr(VK_RETURN), IntPtr.Zero);
                }
            }
        }

        private void SendEnterKeyDownUp()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                // Send KEYDOWN for Enter
                PostMessage(terminalHandle, WM_KEYDOWN, new IntPtr(VK_RETURN), IntPtr.Zero);
                System.Threading.Thread.Sleep(50);

                // Send KEYUP for Enter
                PostMessage(terminalHandle, WM_KEYUP, new IntPtr(VK_RETURN), IntPtr.Zero);
                System.Threading.Thread.Sleep(100);

                // Try a second time to ensure submission
                PostMessage(terminalHandle, WM_KEYDOWN, new IntPtr(VK_RETURN), IntPtr.Zero);
                System.Threading.Thread.Sleep(50);
                PostMessage(terminalHandle, WM_KEYUP, new IntPtr(VK_RETURN), IntPtr.Zero);
            }
        }



        private void ClaudeCodeControl_Unloaded(object sender, RoutedEventArgs e)
        {
            // Don't save during unload - settings should only be saved when user makes changes

            // Stop theme check timer
            if (_themeCheckTimer != null)
            {
                _themeCheckTimer.Stop();
                _themeCheckTimer = null;
            }

            // Unregister solution events
            if (solutionEventsCookie != 0)
            {
                ThreadHelper.JoinableTaskFactory.Run(async delegate
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    try
                    {
                        var solution = Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
                        solution?.UnadviseSolutionEvents(solutionEventsCookie);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error unregistering solution events: {ex.Message}");
                    }
                });
            }

            CleanupResources();
        }

        // ===== Provider Menu Handlers =====

        private void ClaudeCodeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;

            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
                bool claudeAvailable = await IsClaudeCmdAvailableAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Always update the selection regardless of availability
                _settings.SelectedProvider = AiProvider.ClaudeCode;
                UpdateProviderSelection();
                SaveSettings();

                if (!claudeAvailable)
                {
                    ShowClaudeInstallationInstructions();
                    await StartEmbeddedTerminalAsync(false, false); // Regular CMD
                }
                else
                {
                    await StartEmbeddedTerminalAsync(true, false); // Claude Code
                }
            });
        }

        private void CodexMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;

            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
                bool codexAvailable = await IsCodexCmdAvailableAsync();

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Always update the selection regardless of availability
                _settings.SelectedProvider = AiProvider.Codex;
                UpdateProviderSelection();
                SaveSettings();

                if (!codexAvailable)
                {
                    ShowCodexInstallationInstructions();
                    await StartEmbeddedTerminalAsync(false, false); // Regular CMD
                }
                else
                {
                    await StartEmbeddedTerminalAsync(false, true); // Codex
                }
            });
        }

        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            string aboutMessage = $"Claude Code Extension for Visual Studio\n\n" +
                                $"Version: {version}\n" +
                                $"Author: Daniel Liedke\n" +
                                $"Copyright © Daniel Liedke 2025\n\n" +
                                $"Provides seamless integration with Claude Code and Codex AI assistants directly within Visual Studio IDE.";

            MessageBox.Show(aboutMessage, "About Claude Code Extension",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void MenuDropdownButton_Click(object sender, RoutedEventArgs e)
        {
            // Show the context menu when the dropdown button is clicked
            var button = sender as Button;
            if (button?.ContextMenu != null)
            {
                button.ContextMenu.PlacementTarget = button;
                button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                button.ContextMenu.IsOpen = true;
            }
        }

        private void UpdateProviderSelection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_settings == null) return;

            // Update menu item checkmarks
            ClaudeCodeMenuItem.IsChecked = _settings.SelectedProvider == AiProvider.ClaudeCode;
            CodexMenuItem.IsChecked = _settings.SelectedProvider == AiProvider.Codex;

            // Update GroupBox header to show current provider
            string providerName = _settings.SelectedProvider == AiProvider.ClaudeCode ? "Claude Code" : "Codex";
            TerminalGroupBox.Header = providerName;

            // Update tool window title
            UpdateToolWindowTitle(providerName);
        }

        private void UpdateToolWindowTitle(string providerName)
        {
            try
            {
                ThreadHelper.JoinableTaskFactory.Run(async delegate
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    _toolWindow?.UpdateTitle(providerName);
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating tool window title: {ex.Message}");
            }
        }

        private void CleanupResources()
        {
            try
            {
                if (cmdProcess != null && !cmdProcess.HasExited)
                {
                    cmdProcess.Kill();
                    cmdProcess.Dispose();
                    cmdProcess = null;
                }

                if (Directory.Exists(tempImageDirectory))
                {
                    try
                    {
                        Directory.Delete(tempImageDirectory, true);
                        Debug.WriteLine($"Cleaned up temp directory: {tempImageDirectory}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error cleaning temp directory: {ex.Message}");
                        try
                        {
                            foreach (string file in Directory.GetFiles(tempImageDirectory))
                            {
                                File.Delete(file);
                            }
                        }
                        catch { }
                    }
                }

                attachedImagePaths?.Clear();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during cleanup: {ex.Message}");
            }
        }

        public void Dispose()
        {
            CleanupResources();
        }

        // ===== Win32 interop =====
        [DllImport("user32.dll")]
        private static extern bool SetFocus(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern void keybd_event(int bVk, int bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;

        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint WM_CHAR = 0x0102;
        private const int VK_RETURN = 0x0D;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)]
            public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private static IntPtr FindMainWindowHandleByPid(int targetPid, int timeoutMs = 5000, int pollIntervalMs = 50)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                IntPtr found = IntPtr.Zero;

                EnumWindows((hWnd, lParam) =>
                {
                    uint pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    if (pid == targetPid)
                    {
                        found = hWnd;
                        // Hide the window immediately to prevent any blinking
                        ShowWindow(hWnd, SW_HIDE);
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);

                if (found != IntPtr.Zero)
                    return found;

                System.Threading.Thread.Sleep(pollIntervalMs);
            }
            return IntPtr.Zero;
        }

#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void RestartTerminalButton_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100 // Avoid async void methods
        {
            try
            {
                // Determine which provider to use based on settings
                bool useCodex = _settings?.SelectedProvider == AiProvider.Codex;
                bool claudeAvailable = false;
                bool codexAvailable = false;

                if (useCodex)
                {
                    codexAvailable = await IsCodexCmdAvailableAsync();
                }
                else
                {
                    claudeAvailable = await IsClaudeCmdAvailableAsync();
                }

                await StartEmbeddedTerminalAsync(claudeAvailable, useCodex && codexAvailable);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in RestartTerminalButton_Click: {ex.Message}");
                MessageBox.Show($"Failed to restart terminal: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // Solution events handler to detect when solutions are opened/closed
    public class SolutionEventsHandler : IVsSolutionEvents
    {
        private readonly ClaudeCodeControl _control;

        public SolutionEventsHandler(ClaudeCodeControl control)
        {
            _control = control;
        }

        public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            Debug.WriteLine("Solution opened - checking if terminal needs to restart");
            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
                await _control.OnWorkspaceDirectoryChangedAsync();
            });
            return Microsoft.VisualStudio.VSConstants.S_OK;
        }

        public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded)
        {
            Debug.WriteLine("Project opened - checking if terminal needs to restart");
            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
                await _control.OnWorkspaceDirectoryChangedAsync();
            });
            return Microsoft.VisualStudio.VSConstants.S_OK;
        }

        // These methods are required by the interface but we don't need them
        public int OnAfterCloseSolution(object pUnkReserved) => Microsoft.VisualStudio.VSConstants.S_OK;
        public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => Microsoft.VisualStudio.VSConstants.S_OK;
        public int OnAfterUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => Microsoft.VisualStudio.VSConstants.S_OK;
        public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => Microsoft.VisualStudio.VSConstants.S_OK;
        public int OnBeforeCloseSolution(object pUnkReserved) => Microsoft.VisualStudio.VSConstants.S_OK;
        public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => Microsoft.VisualStudio.VSConstants.S_OK;
        public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => Microsoft.VisualStudio.VSConstants.S_OK;
        public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => Microsoft.VisualStudio.VSConstants.S_OK;
        public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => Microsoft.VisualStudio.VSConstants.S_OK;
    }
}