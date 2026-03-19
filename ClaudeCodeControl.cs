/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Main user control for the Claude Code extension for VS.NET
 *          Core functionality and orchestration of partial classes
 *
 * *******************************************************************************************************************/

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS
{
    /// <summary>
    /// Main user control for Claude Code extension
    /// Implements terminal embedding, AI provider management, and user interaction
    /// </summary>
    /// <remarks>
    /// This class is split into multiple partial classes for better organization:
    /// - ClaudeCodeControl.cs: Core initialization and orchestration (this file)
    /// - ClaudeCodeControl.Cleanup.cs: Resource cleanup and temp file management
    /// - ClaudeCodeControl.ImageHandling.cs: Image attachment and paste functionality
    /// - ClaudeCodeControl.Interop.cs: Win32 API declarations and structures
    /// - ClaudeCodeControl.ProviderManagement.cs: AI provider detection and switching
    /// - ClaudeCodeControl.Settings.cs: Settings persistence and management
    /// - ClaudeCodeControl.Terminal.cs: Terminal initialization and embedding
    /// - ClaudeCodeControl.TerminalIO.cs: Terminal communication and I/O
    /// - ClaudeCodeControl.Theme.cs: Visual Studio theme integration
    /// - ClaudeCodeControl.UserInput.cs: Keyboard and button input handling
    /// - ClaudeCodeControl.Workspace.cs: Solution and workspace directory management
    /// </remarks>
    public partial class ClaudeCodeControl : UserControl, IDisposable
    {
        #region Fields

        /// <summary>
        /// Reference to the parent tool window
        /// </summary>
        private ClaudeCodeToolWindow _toolWindow;

        /// <summary>
        /// Flag to track if the control has been initialized (prevents multiple initializations)
        /// </summary>
        private bool _hasInitialized = false;

        /// <summary>
        /// Prevents overlapping fire-and-forget startup initialization passes.
        /// </summary>
        private bool _startupInitializationScheduled = false;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the ClaudeCodeControl class
        /// Sets up event handlers and initializes temporary directories
        /// </summary>
        public ClaudeCodeControl()
        {
            // Initialize XAML components
            InitializeComponent();

            // Initialize temporary directory for image storage
            InitializeTempDirectory();

            // Set up solution events for workspace changes
            SetupSolutionEvents();

            // Set up theme change detection
            SetupThemeChangeEvents();

            // Wire up lifecycle events
            Loaded += ClaudeCodeControl_Loaded;
            Unloaded += ClaudeCodeControl_Unloaded;
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Sets the parent tool window reference
        /// </summary>
        /// <param name="toolWindow">The parent tool window instance</param>
        public void SetToolWindow(ClaudeCodeToolWindow toolWindow)
        {
            _toolWindow = toolWindow;
            _toolWindow.FrameShow += OnToolWindowFrameShow;
        }

        /// <summary>
        /// Handles the Loaded event - initializes settings and terminal
        /// </summary>
        private void ClaudeCodeControl_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                // Loaded runs on the UI thread, so apply settings directly.
                LoadSettings();
                ApplyLoadedSettings();

                // Only initialize terminal once - prevent re-initialization on tab switches
                if (_hasInitialized)
                {
                    _isInitializing = false;
                    return;
                }

                _hasInitialized = true;
                _isInitializing = false;

                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                bool solutionAlreadyOpen = dte?.Solution?.FullName != null && !string.IsNullOrEmpty(dte.Solution.FullName);
                ScheduleStartupInitialization(solutionAlreadyOpen);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during control load: {ex.Message}");
            }
        }

        /// <summary>
        /// Schedules the first workspace/terminal initialization after the tool window has finished painting.
        /// </summary>
        /// <param name="solutionAlreadyOpen">Whether Visual Studio already has a solution open</param>
        private void ScheduleStartupInitialization(bool solutionAlreadyOpen)
        {
            if (_startupInitializationScheduled)
            {
                return;
            }

            _startupInitializationScheduled = true;

#pragma warning disable VSSDK007 // fire-and-forget is intentional to keep tool window startup responsive
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    await Task.Delay(350);

                    if (solutionAlreadyOpen)
                    {
                        // First load should avoid the heavier forced diff reset path.
                        await OnWorkspaceDirectoryChangedAsync(false);
                    }
                    else
                    {
                        await UpdateViewChangesButtonVisibilityAsync();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error during scheduled startup initialization: {ex.Message}");
                }
                finally
                {
                    _startupInitializationScheduled = false;
                }
            });
#pragma warning restore VSSDK007
        }

        #endregion
    }
}
