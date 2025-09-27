using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using Microsoft.Win32;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl : UserControl
    {
        private System.Windows.Forms.Panel terminalPanel;
        private Process cmdProcess;
        private IntPtr terminalHandle;
        private List<string> attachedImagePaths = new List<string>();
        private string tempImageDirectory;

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

        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        // Window style constants
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
            this.Loaded += ClaudeCodeControl_Loaded;
            this.Unloaded += ClaudeCodeControl_Unloaded;
        }

        private void ClaudeCodeControl_Loaded(object sender, RoutedEventArgs e)
        {
            // Only initialize terminal when the control is actually shown
            if (terminalPanel == null)
            {
                // Delay terminal initialization slightly to ensure VS services are ready
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    InitializeTerminal();
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }

        private void InitializeTempDirectory()
        {
            tempImageDirectory = Path.Combine(Path.GetTempPath(), "ClaudeCodeVS", Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempImageDirectory);
        }

        private void InitializeTerminal()
        {
            try
            {
                // Create a Windows Forms panel to host the terminal
                terminalPanel = new System.Windows.Forms.Panel
                {
                    Dock = System.Windows.Forms.DockStyle.Fill,
                    BackColor = System.Drawing.Color.Black
                };

                // Set the panel as the child of WindowsFormsHost
                TerminalHost.Child = terminalPanel;

                // Handle panel resize to resize embedded terminal
                terminalPanel.Resize += (s, e) => ResizeEmbeddedTerminal();

                // Start CMD with Claude after a brief delay to ensure the panel is ready
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(500);
                    StartEmbeddedTerminal();
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize terminal: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StartEmbeddedTerminal()
        {
            try
            {
                if (cmdProcess != null && !cmdProcess.HasExited)
                {
                    cmdProcess.Kill();
                    cmdProcess.Dispose();
                }

                // Get workspace directory
                string workspaceDir = GetWorkspaceDirectory();
                System.Diagnostics.Debug.WriteLine($"Starting Claude in directory: {workspaceDir}");

                // Start CMD process with Claude
                cmdProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/k cd /d \"" + workspaceDir + "\" && claude.cmd",
                        UseShellExecute = false,
                        CreateNoWindow = false,
                        WorkingDirectory = workspaceDir
                    }
                };

                cmdProcess.Start();

                // Wait a moment for the window to be created
                System.Threading.Thread.Sleep(1500);

                // Find and embed the CMD window
                terminalHandle = cmdProcess.MainWindowHandle;
                if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
                {
                    // Set the CMD window as a child of our panel
                    SetParent(terminalHandle, terminalPanel.Handle);

                    // Hide window borders and chrome
                    SetWindowLong(terminalHandle, GWL_STYLE,
                        GetWindowLong(terminalHandle, GWL_STYLE) & ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZE | WS_MAXIMIZE | WS_SYSMENU));

                    // Show the window and resize to fill the panel
                    ShowWindow(terminalHandle, SW_SHOW);
                    ResizeEmbeddedTerminal();
                }
                else
                {
                    MessageBox.Show("Could not find CMD window to embed. Make sure Claude Code is installed and accessible via 'claude.cmd'.",
                                  "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
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

        private string GetWorkspaceDirectory()
        {
            try
            {
                // Try multiple approaches to get the VS project directory

                // Method 1: Try to get from DTE service
                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte?.Solution?.FullName != null && !string.IsNullOrEmpty(dte.Solution.FullName))
                {
                    string solutionDir = Path.GetDirectoryName(dte.Solution.FullName);
                    if (Directory.Exists(solutionDir))
                    {
                        return solutionDir;
                    }
                }

                // Method 2: Try to get active project directory
                if (dte?.ActiveDocument?.ProjectItem?.ContainingProject?.FullName != null)
                {
                    string projectDir = Path.GetDirectoryName(dte.ActiveDocument.ProjectItem.ContainingProject.FullName);
                    if (Directory.Exists(projectDir))
                    {
                        return projectDir;
                    }
                }

                // Method 3: Try solution builder
                var solutionBuildManager = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(Microsoft.VisualStudio.Shell.Interop.SVsSolutionBuildManager));
                if (solutionBuildManager != null)
                {
                    var solution = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(Microsoft.VisualStudio.Shell.Interop.SVsSolution)) as Microsoft.VisualStudio.Shell.Interop.IVsSolution;
                    if (solution != null)
                    {
                        solution.GetSolutionInfo(out string solutionDir, out string solutionFile, out string userOptsFile);
                        if (!string.IsNullOrEmpty(solutionDir) && Directory.Exists(solutionDir))
                        {
                            return solutionDir;
                        }
                    }
                }

                // Method 4: Check current working directory if it looks like a project
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
                // Log error but don't crash
                System.Diagnostics.Debug.WriteLine($"Error getting workspace directory: {ex.Message}");
            }

            // Final fallback
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        // Button event handlers
        private void RestartTerminalButton_Click(object sender, RoutedEventArgs e)
        {
            StartEmbeddedTerminal();
        }


        // UI Event Handlers
        private void PromptTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Handle Ctrl+V for pasting images at preview level to catch it earlier
            if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (TryPasteImage())
                {
                    e.Handled = true; // Prevent normal text paste
                }
            }
        }

        private void PromptTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                {
                    // Let TextBox handle Shift+Enter as a real newline
                    return;
                }

                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    // Ctrl+Enter also insert newline
                    return;
                }

                // Plain Enter: Send prompt
                SendButton_Click(sender, null);
                e.Handled = true;
            }
        }

        private bool TryPasteImage()
        {
            try
            {
                System.Windows.Media.Imaging.BitmapSource image = null;

                // Try multiple clipboard formats
                if (Clipboard.ContainsImage())
                {
                    image = Clipboard.GetImage();
                    System.Diagnostics.Debug.WriteLine("Got image from ContainsImage/GetImage");
                }
                else if (Clipboard.ContainsData(DataFormats.Bitmap))
                {
                    var bitmapData = Clipboard.GetData(DataFormats.Bitmap);
                    System.Diagnostics.Debug.WriteLine($"Bitmap data type: {bitmapData?.GetType()}");

                    if (bitmapData is System.Drawing.Bitmap bitmap)
                    {
                        var handle = bitmap.GetHbitmap();
                        try
                        {
                            image = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                                handle, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                            System.Diagnostics.Debug.WriteLine("Got image from Bitmap conversion");
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
                        System.Diagnostics.Debug.WriteLine("Got image from PNG data");
                    }
                }
                else if (Clipboard.ContainsData("DeviceIndependentBitmap"))
                {
                    var dibData = Clipboard.GetData("DeviceIndependentBitmap");
                    System.Diagnostics.Debug.WriteLine($"DIB data type: {dibData?.GetType()}");
                    // Could add DIB parsing here if needed
                }

                if (image != null)
                {
                    // Create unique filename
                    string fileName = $"pasted_image_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
                    string imagePath = Path.Combine(tempImageDirectory, fileName);

                    // Save image to temp directory
                    using (var fileStream = new FileStream(imagePath, FileMode.Create))
                    {
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                        encoder.Save(fileStream);
                    }

                    // Add to attached images list
                    attachedImagePaths.Add(imagePath);
                    UpdateImageDropDisplay();

                    System.Diagnostics.Debug.WriteLine($"Successfully pasted and saved image to: {imagePath}");
                    return true;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("No image found in clipboard");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error pasting image: {ex.Message}");
                MessageBox.Show($"Error pasting image: {ex.Message}", "Paste Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return false;
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

                // Build the full prompt with images if any
                StringBuilder fullPrompt = new StringBuilder();

                // Copy images to temp directory and include their paths
                List<string> tempImagePaths = new List<string>();
                if (attachedImagePaths.Any())
                {
                    fullPrompt.AppendLine("Images attached:");
                    foreach (string imagePath in attachedImagePaths)
                    {
                        try
                        {
                            string fileName = Path.GetFileName(imagePath);
                            string tempPath = Path.Combine(tempImageDirectory, fileName);

                            // Copy image to temp directory
                            File.Copy(imagePath, tempPath, true);
                            tempImagePaths.Add(tempPath);

                            // Include full temp path in prompt for Claude Code
                            fullPrompt.AppendLine($"  - {tempPath}");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error copying image {imagePath}: {ex.Message}");
                            // Include original path if copy fails
                            fullPrompt.AppendLine($"  - {imagePath}");
                        }
                    }
                    fullPrompt.AppendLine();
                }

                fullPrompt.AppendLine(prompt);

                // Send to terminal by simulating keyboard input
                SendTextToTerminal(fullPrompt.ToString());

                // Always auto-clear after sending
                PromptTextBox.Clear();
                ClearAttachedImages();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error sending prompt: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        private void ImageDropBorder_Drop(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    foreach (string file in files)
                    {
                        if (IsImageFile(file))
                        {
                            attachedImagePaths.Add(file);
                        }
                    }
                    UpdateImageDropDisplay();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error handling dropped files: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ImageDropBorder_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void ImageDropBorder_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                OpenFileDialog openFileDialog = new OpenFileDialog
                {
                    Filter = "Image files (*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp|All files (*.*)|*.*",
                    Multiselect = true
                };

                if (openFileDialog.ShowDialog() == true)
                {
                    foreach (string filename in openFileDialog.FileNames)
                    {
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

        // Helper methods
        private void SendTextToTerminal(string text)
        {
            try
            {
                if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
                {
                    // Copy text to clipboard
                    Clipboard.SetText(text);

                    // Focus the terminal window
                    SetForegroundWindow(terminalHandle);
                    SetFocus(terminalHandle);

                    System.Threading.Thread.Sleep(200); // Wait for focus

                    // Get terminal window rectangle for click positioning
                    GetWindowRect(terminalHandle, out RECT rect);
                    int centerX = rect.Left + (rect.Right - rect.Left) / 2;
                    int centerY = rect.Top + (rect.Bottom - rect.Top) / 2;

                    // Send right-click to paste
                    SendRightClick(centerX, centerY);

                    // Wait a moment then send Enter (sometimes need double enter for submission)
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
            // Move cursor to the specified position
            SetCursorPos(x, y);

            // Wait a moment for cursor to position
            System.Threading.Thread.Sleep(50);

            // Send right mouse button down and up
            mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(50);
            mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
        }

        private void SendEnterKey()
        {
            if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
            {
                // Send Enter key directly to the terminal window
                PostMessage(terminalHandle, WM_KEYDOWN, new IntPtr(VK_RETURN), IntPtr.Zero);
                System.Threading.Thread.Sleep(50);
                PostMessage(terminalHandle, WM_CHAR, new IntPtr(VK_RETURN), IntPtr.Zero);
                System.Threading.Thread.Sleep(50);
                PostMessage(terminalHandle, WM_KEYUP, new IntPtr(VK_RETURN), IntPtr.Zero);
            }
        }

        private bool IsImageFile(string filePath)
        {
            string[] imageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tiff", ".webp" };
            string extension = Path.GetExtension(filePath).ToLower();
            return imageExtensions.Contains(extension);
        }

        private void UpdateImageDropDisplay()
        {
            if (attachedImagePaths.Any())
            {
                ImageDropText.Text = $"{attachedImagePaths.Count} image(s)";
            }
            else
            {
                ImageDropText.Text = "";
            }
        }

        private void ClearAttachedImages()
        {
            attachedImagePaths.Clear();
            UpdateImageDropDisplay();
        }

        // Additional Win32 APIs for sending input to terminal
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

        // Message constants
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

        // Cleanup when the control is unloaded
        private void ClaudeCodeControl_Unloaded(object sender, RoutedEventArgs e)
        {
            CleanupResources();
        }

        private void CleanupResources()
        {
            try
            {
                // Kill terminal process
                if (cmdProcess != null && !cmdProcess.HasExited)
                {
                    cmdProcess.Kill();
                    cmdProcess.Dispose();
                    cmdProcess = null;
                }

                // Cleanup temp directory and all images
                if (Directory.Exists(tempImageDirectory))
                {
                    try
                    {
                        Directory.Delete(tempImageDirectory, true);
                        System.Diagnostics.Debug.WriteLine($"Cleaned up temp directory: {tempImageDirectory}");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error cleaning temp directory: {ex.Message}");
                        // Try to delete individual files if directory delete fails
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

                // Clear image paths
                attachedImagePaths?.Clear();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error during cleanup: {ex.Message}");
            }
        }

        // Also implement IDisposable pattern for better cleanup
        public void Dispose()
        {
            CleanupResources();
        }
    }
}