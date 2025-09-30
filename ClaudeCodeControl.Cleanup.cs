/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Liedke
 *
 * Copyright © Daniel Liedke 2025
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Liedke is strictly forbidden.
 *
 * Purpose: Resource cleanup and temporary file management
 *
 * *******************************************************************************************************************/

using System;
using System.IO;
using System.Diagnostics;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Temporary Directory Fields

        /// <summary>
        /// Session-specific temporary directory for storing pasted images
        /// </summary>
        private string tempImageDirectory;

        #endregion

        #region Temporary Directory Initialization

        /// <summary>
        /// Initializes the temporary directory for storing pasted images
        /// Cleans up any existing ClaudeCodeVS temp directories first
        /// </summary>
        private void InitializeTempDirectory()
        {
            try
            {
                // Clear any existing ClaudeCodeVS temp directories
                CleanupClaudeCodeVSTempDirectories();

                // Create a session-level temp directory for storing pasted images before sending
                tempImageDirectory = Path.Combine(Path.GetTempPath(), "ClaudeCodeVS_Session", Guid.NewGuid().ToString());
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

        /// <summary>
        /// Cleans up all ClaudeCodeVS temporary directories from previous sessions
        /// </summary>
        private void CleanupClaudeCodeVSTempDirectories()
        {
            try
            {
                string tempPath = Path.GetTempPath();

                // Clean up old ClaudeCodeVS directories
                string claudeCodeVSPath = Path.Combine(tempPath, "ClaudeCodeVS");
                if (Directory.Exists(claudeCodeVSPath))
                {
                    Debug.WriteLine($"Cleaning up ClaudeCodeVS temp directory: {claudeCodeVSPath}");
                    Directory.Delete(claudeCodeVSPath, true);
                    Debug.WriteLine("ClaudeCodeVS temp directory cleanup completed");
                }

                // Clean up session directories
                string sessionPath = Path.Combine(tempPath, "ClaudeCodeVS_Session");
                if (Directory.Exists(sessionPath))
                {
                    Debug.WriteLine($"Cleaning up ClaudeCodeVS_Session temp directory: {sessionPath}");
                    Directory.Delete(sessionPath, true);
                    Debug.WriteLine("ClaudeCodeVS_Session temp directory cleanup completed");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error cleaning up ClaudeCodeVS temp directories: {ex.Message}");
                // Continue even if cleanup fails
            }
        }

        #endregion

        #region Unload and Cleanup

        /// <summary>
        /// Handles control unload event - cleans up resources and unregisters events
        /// </summary>
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

        /// <summary>
        /// Cleans up all resources including processes and temporary files
        /// </summary>
        private void CleanupResources()
        {
            try
            {
                // Kill and dispose terminal process
                if (cmdProcess != null && !cmdProcess.HasExited)
                {
                    cmdProcess.Kill();
                    cmdProcess.Dispose();
                    cmdProcess = null;
                }

                // Clean up temporary directory
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
                        // Try to at least delete files if directory deletion fails
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

                // Clear attached images list
                attachedImagePaths?.Clear();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during cleanup: {ex.Message}");
            }
        }

        /// <summary>
        /// Implements IDisposable - disposes of all managed resources
        /// </summary>
        public void Dispose()
        {
            CleanupResources();
        }

        #endregion
    }
}