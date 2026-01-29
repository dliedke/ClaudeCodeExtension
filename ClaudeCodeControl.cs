/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Liedke
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Liedke is strictly forbidden.
 *
 * Purpose: Main user control for the Claude Code extension for VS.NET
 *          Core functionality and orchestration of partial classes
 *
 * *******************************************************************************************************************/

using System;
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
        }

        /// <summary>
        /// Handles the Loaded event - initializes settings and terminal
        /// </summary>
        private void ClaudeCodeControl_Loaded(object sender, RoutedEventArgs e)
        {
            // Load settings after UI is fully loaded
            LoadSettings();

            // Apply settings to UI every time we load (in case settings changed)
            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ApplyLoadedSettings();
            });

            // Only initialize terminal once - prevent re-initialization on tab switches
            if (_hasInitialized)
            {
                // Mark initialization as complete to allow settings saving
                _isInitializing = false;
                return;
            }

            // Set the flag to prevent multiple calls
            _hasInitialized = true;

            // Check if a solution is already open (e.g., VS restarted with solution)
            // If so, initialize terminal immediately as OnAfterOpenSolution won't fire
            ThreadHelper.JoinableTaskFactory.Run(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                bool solutionAlreadyOpen = dte?.Solution?.FullName != null && !string.IsNullOrEmpty(dte.Solution.FullName);

                if (solutionAlreadyOpen)
                {
                    await OnWorkspaceDirectoryChangedAsync(true);
                }
                else
                {
                }
            });

            // Mark initialization as complete to allow settings saving
            _isInitializing = false;
        }

        #endregion
    }
}
