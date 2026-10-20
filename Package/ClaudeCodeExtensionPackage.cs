/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Main package class for the Claude Code extension for VS.NET
 *
 * *******************************************************************************************************************/

using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio;
using Newtonsoft.Json;
using System;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeExtension
{
    /// <summary>
    /// This is the class that implements the package exposed by this assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The minimum requirement for a class to be considered a valid package for Visual Studio
    /// is to implement the IVsPackage interface and register itself with the shell.
    /// This package uses the helper classes defined inside the Managed Package Framework (MPF)
    /// to do it: it derives from the Package class that provides the implementation of the
    /// IVsPackage interface and uses the registration attributes defined in the framework to
    /// register itself and its components with the shell. These attributes tell the pkgdef creation
    /// utility what data to put into .pkgdef file.
    /// </para>
    /// <para>
    /// To get loaded into VS, the package must be referred by &lt;Asset Type="Microsoft.VisualStudio.VsPackage" ...&gt; in .vsixmanifest file.
    /// </para>
    /// </remarks>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideToolWindow(typeof(ClaudeCodeVS.ClaudeCodeToolWindow))]
    [ProvideToolWindow(typeof(ClaudeCodeVS.DiffViewerToolWindow), Transient = true)]
    [ProvideToolWindow(typeof(ClaudeCodeVS.DetachedTerminalToolWindow), Transient = true)]
    [ProvideToolWindow(typeof(ClaudeCodeVS.ClaudeUsageToolWindow), Transient = true)]
    // MDI style docks the chat in the central document area, next to the open files, instead of the
    // narrow tool-window strip the other panes use.
    [ProvideToolWindow(typeof(ClaudeCodeVS.NativeChatToolWindow), Style = VsDockStyle.MDI, Transient = true)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(ClaudeCodeExtensionPackage.PackageGuidString)]
    public sealed class ClaudeCodeExtensionPackage : AsyncPackage
    {
        /// <summary>
        /// ClaudeCodeExtensionPackage GUID string.
        /// </summary>
        public const string PackageGuidString = "3fa29425-3add-418f-82f6-0c9b7419b2ca";

        /// <summary>
        /// Command set GUID and command IDs.
        /// </summary>
        public static readonly Guid CommandSet = new Guid("11111111-2222-3333-4444-555555555555");
        public const int ClaudeCodeToolWindowCommandId = 0x0100;
        public const int EditorSendSelectionCommandId = 0x0201;
        public const int ShowNativeChatCommandId = 0x0101;

        private const string ConfigurationFileName = "claudecode-settings.json";
        private static readonly string ConfigurationPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeCodeExtension",
            ConfigurationFileName);

        private ClaudeCodeVS.ClaudeUsageToolWindow _autoReopenedUsageWindow;

        #region Package Members

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to monitor for initialization cancellation, which can occur when VS is shutting down.</param>
        /// <param name="progress">A provider for progress updates.</param>
        /// <returns>A task representing the async work of package initialization, or an already completed task if there is none. Do not return null from this method.</returns>
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            // When initialized asynchronously, the current thread may be a background thread at this point.
            // Do any initialization that requires the UI thread after switching to the UI thread.
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // Add command handler for the tool window
            var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService != null)
            {
                var menuCommandID = new CommandID(CommandSet, ClaudeCodeToolWindowCommandId);
                var menuItem = new MenuCommand(this.ShowToolWindow, menuCommandID);
                commandService.AddCommand(menuItem);

                // View > Other Windows > Claude Code Chat: the always-available way back to the chat
                // (issue #168), and a command the user can bind a keyboard shortcut to.
                var showChatCmdId = new CommandID(CommandSet, ShowNativeChatCommandId);
                commandService.AddCommand(new MenuCommand(this.ShowNativeChat, showChatCmdId));

                // Add command handler for "Send Selection to Claude Code" editor context menu
                var editorCmdId = new CommandID(CommandSet, EditorSendSelectionCommandId);
                var editorMenuItem = new OleMenuCommand(this.OnEditorSendSelection, editorCmdId);
                editorMenuItem.BeforeQueryStatus += OnEditorSendSelectionQueryStatus;
                commandService.AddCommand(editorMenuItem);
            }

            ScheduleUsageWindowRestore(cancellationToken);
            SchedulePanelBackgroundLoad(cancellationToken);
        }

        /// <summary>
        /// How long after package load to wait before loading a panel restored behind another tab,
        /// so Visual Studio has finished restoring the window layout.
        /// </summary>
        private const int PanelBackgroundLoadDelayMs = 1500;

        /// <summary>How long to wait for the panel's control to load once its frame was brought forward.</summary>
        private const int PanelLoadTimeoutMs = 5000;

        /// <summary>
        /// Visual Studio restores the panel at startup but does not create its content while it sits
        /// behind another tab in its dock group (or collapsed as an auto-hide tab). Everything the
        /// extension does at startup — launching the agent and opening the native chat tab — runs when
        /// that content loads, so until the user clicked the panel's tab nothing appeared (issue #204).
        /// </summary>
        private void SchedulePanelBackgroundLoad(CancellationToken cancellationToken)
        {
#pragma warning disable VSSDK007 // Fire-and-forget is intentional; startup restore should not block package load
            _ = JoinableTaskFactory.RunAsync(async delegate
            {
                try
                {
                    await Task.Delay(PanelBackgroundLoadDelayMs, cancellationToken);
                    await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                    await LoadPanelRestoredOffScreenAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Error loading the Claude Code panel in the background: " + ex);
                }
            });
#pragma warning restore VSSDK007
        }

        /// <summary>
        /// Loads the panel when it is open in the layout but off screen: brings its frame forward just
        /// long enough for its content to load, then puts back every tab it displaced and returns
        /// focus to the window that had it. A panel the user closed stays closed, and one already on
        /// screen loads on its own.
        /// </summary>
        private async Task LoadPanelRestoredOffScreenAsync(CancellationToken cancellationToken)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if ((FindToolWindow(typeof(ClaudeCodeVS.ClaudeCodeToolWindow), 0, false) as ClaudeCodeVS.ClaudeCodeToolWindow)?.HasInitialized == true)
            {
                return;
            }

            var shell = await GetServiceAsync(typeof(SVsUIShell)) as IVsUIShell;
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (shell == null)
            {
                return;
            }

            // Frame only: finds the frame VS restored from the layout without creating the pane, and
            // finds nothing when the panel was closed.
            Guid panelSlot = typeof(ClaudeCodeVS.ClaudeCodeToolWindow).GUID;
            if (shell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fFrameOnly, ref panelSlot, out IVsWindowFrame panelFrame) != VSConstants.S_OK ||
                panelFrame == null ||
                panelFrame.IsVisible() != VSConstants.S_OK ||
                IsFrameOnScreen(panelFrame))
            {
                return;
            }

            var onScreenBefore = GetFramesOnScreen(shell);
            IVsWindowFrame activeFrame = await GetActiveWindowFrameAsync(cancellationToken);
            bool focusMoved = false;

            // ShowNoActivate keeps focus where it is; Show is the fallback for a tab group that does
            // not switch tabs without activation.
            panelFrame.ShowNoActivate();
            if (!IsFrameOnScreen(panelFrame))
            {
                panelFrame.Show();
                focusMoved = true;
            }

            for (int waited = 0; waited < PanelLoadTimeoutMs; waited += 100)
            {
                if ((FindToolWindow(typeof(ClaudeCodeVS.ClaudeCodeToolWindow), 0, false) as ClaudeCodeVS.ClaudeCodeToolWindow)?.HasInitialized == true)
                {
                    break;
                }

                await Task.Delay(100, cancellationToken);
                await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            }

            // Put back the tab the panel pushed behind in its group.
            foreach (var frame in onScreenBefore)
            {
                if (frame == panelFrame || IsFrameOnScreen(frame))
                {
                    continue;
                }

                frame.ShowNoActivate();
                if (!IsFrameOnScreen(frame))
                {
                    frame.Show();
                    focusMoved = true;
                }
            }

            if (focusMoved && activeFrame != null && activeFrame != panelFrame)
            {
                activeFrame.Show();
            }
        }

        private static bool IsFrameOnScreen(IVsWindowFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                return frame.IsOnScreen(out int onScreen) == VSConstants.S_OK && onScreen != 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("IsFrameOnScreen failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Every tool window and document frame currently on screen.</summary>
        private static System.Collections.Generic.List<IVsWindowFrame> GetFramesOnScreen(IVsUIShell shell)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var frames = new System.Collections.Generic.List<IVsWindowFrame>();

            try
            {
                if (shell.GetToolWindowEnum(out IEnumWindowFrames toolWindows) == VSConstants.S_OK)
                {
                    AddFramesOnScreen(toolWindows, frames);
                }

                if (shell.GetDocumentWindowEnum(out IEnumWindowFrames documents) == VSConstants.S_OK)
                {
                    AddFramesOnScreen(documents, frames);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("GetFramesOnScreen failed: " + ex.Message);
            }

            return frames;
        }

        private static void AddFramesOnScreen(IEnumWindowFrames enumerator, System.Collections.Generic.List<IVsWindowFrame> frames)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (enumerator == null)
            {
                return;
            }

            var batch = new IVsWindowFrame[1];
            while (enumerator.Next(1, batch, out uint fetched) == VSConstants.S_OK && fetched == 1)
            {
                if (batch[0] != null && IsFrameOnScreen(batch[0]))
                {
                    frames.Add(batch[0]);
                }
            }
        }

        /// <summary>The window frame that has focus right now (an editor, usually), or null.</summary>
        private async System.Threading.Tasks.Task<IVsWindowFrame> GetActiveWindowFrameAsync(CancellationToken cancellationToken)
        {
            try
            {
                var monitorSelection = await GetServiceAsync(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
                await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

                if (monitorSelection != null &&
                    monitorSelection.GetCurrentElementValue((uint)VSConstants.VSSELELEMID.SEID_WindowFrame, out object value) == VSConstants.S_OK)
                {
                    return value as IVsWindowFrame;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("GetActiveWindowFrameAsync failed: " + ex.Message);
            }

            return null;
        }

        private void ScheduleUsageWindowRestore(CancellationToken cancellationToken)
        {
            var settings = LoadSettingsForStartup();
            if (settings?.UsageWindowOpened != true)
            {
                return;
            }

#pragma warning disable VSSDK007 // Fire-and-forget is intentional; startup restore should not block package load
            _ = JoinableTaskFactory.RunAsync(async delegate
            {
                try
                {
                    await Task.Delay(750, cancellationToken);
                    await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

                    _autoReopenedUsageWindow = FindToolWindow(
                        typeof(ClaudeCodeVS.ClaudeUsageToolWindow),
                        0,
                        true) as ClaudeCodeVS.ClaudeUsageToolWindow;

                    if (_autoReopenedUsageWindow?.Frame == null)
                    {
                        return;
                    }

                    if (_autoReopenedUsageWindow.UsageControl != null)
                    {
                        _autoReopenedUsageWindow.UsageControl.ApplyAutoRefreshSeconds(settings.UsageAutoRefreshSeconds);
                    }

                    _autoReopenedUsageWindow.ClosedByUser -= OnAutoReopenedUsageWindowClosed;
                    _autoReopenedUsageWindow.ClosedByUser += OnAutoReopenedUsageWindowClosed;

                    var frame = (IVsWindowFrame)_autoReopenedUsageWindow.Frame;
                    ErrorHandler.ThrowOnFailure(frame.Show());
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Error restoring Claude usage window: " + ex);
                }
            });
#pragma warning restore VSSDK007
        }

        private void OnAutoReopenedUsageWindowClosed(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var settings = LoadSettingsForStartup();
                if (settings == null || !settings.UsageWindowOpened)
                {
                    return;
                }

                settings.UsageWindowOpened = false;
                SaveSettingsFromPackage(settings);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error saving Claude usage close state: " + ex);
            }
        }

        private static ClaudeCodeVS.ClaudeCodeSettings LoadSettingsForStartup()
        {
            try
            {
                if (!File.Exists(ConfigurationPath))
                {
                    return null;
                }

                string json = File.ReadAllText(ConfigurationPath);
                var settings = JsonConvert.DeserializeObject<ClaudeCodeVS.ClaudeCodeSettings>(json);
                if (settings == null)
                {
                    return null;
                }

                if (!Enum.IsDefined(typeof(ClaudeCodeVS.AiProvider), settings.SelectedProvider))
                {
                    settings.SelectedProvider = ClaudeCodeVS.AiProvider.ClaudeCode;
                }

                return settings;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error loading Claude usage startup settings: " + ex);
                return null;
            }
        }

        private static void SaveSettingsFromPackage(ClaudeCodeVS.ClaudeCodeSettings settings)
        {
            try
            {
                string directory = Path.GetDirectoryName(ConfigurationPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(ConfigurationPath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error saving Claude usage startup settings: " + ex);
            }
        }

        /// <summary>
        /// Checks if there is a text selection in the active editor to enable/disable the context menu item.
        /// </summary>
        private void OnEditorSendSelectionQueryStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var cmd = (OleMenuCommand)sender;
            try
            {
                var dte = GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                var sel = dte?.ActiveDocument?.Selection as EnvDTE.TextSelection;
                bool hasSelection = sel != null && !string.IsNullOrEmpty(sel.Text);
                cmd.Visible = true;
                cmd.Enabled = hasSelection;
            }
            catch
            {
                cmd.Visible = true;
                cmd.Enabled = false;
            }
        }

        /// <summary>
        /// Handles the "Send Selection to Claude Code" editor context menu command.
        /// Extracts the selected text, file path, and line numbers, then inserts into the prompt.
        /// </summary>
        private void OnEditorSendSelection(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var dte = GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                var sel = dte?.ActiveDocument?.Selection as EnvDTE.TextSelection;
                if (sel == null || string.IsNullOrEmpty(sel.Text))
                    return;

                string code = sel.Text;
                string filePath = dte.ActiveDocument.FullName;
                int startLine = sel.TopLine;
                int endLine = sel.BottomLine;

                // Ensure tool window is visible
                ToolWindowPane window = FindToolWindow(typeof(ClaudeCodeVS.ClaudeCodeToolWindow), 0, true);
                if (window?.Frame == null)
                    return;

                IVsWindowFrame windowFrame = (IVsWindowFrame)window.Frame;
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(windowFrame.Show());

                // Insert snippet into the prompt
                var toolWindow = window as ClaudeCodeVS.ClaudeCodeToolWindow;
                toolWindow?.InsertCodeSnippet(code, filePath, startLine, endLine);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error sending selection to Claude Code: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows the tool window when the menu item is clicked.
        /// </summary>
        private void ShowToolWindow(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Get the instance of the tool window
            ToolWindowPane window = FindToolWindow(typeof(ClaudeCodeVS.ClaudeCodeToolWindow), 0, true);
            if ((null == window) || (null == window.Frame))
            {
                throw new NotSupportedException("Cannot create tool window");
            }

            IVsWindowFrame windowFrame = (IVsWindowFrame)window.Frame;
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(windowFrame.Show());
        }

        /// <summary>
        /// Brings the native-mode chat back on screen from wherever it went — a hidden or lost tab, or
        /// docked in the panel. With native mode off (or the panel never opened) it shows the panel.
        /// </summary>
        private void ShowNativeChat(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var window = FindToolWindow(typeof(ClaudeCodeVS.ClaudeCodeToolWindow), 0, true) as ClaudeCodeVS.ClaudeCodeToolWindow;
            if (window?.Frame == null)
            {
                throw new NotSupportedException("Cannot create tool window");
            }

            window.ShowChat();
        }

        #endregion
    }
}
