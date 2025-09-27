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
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace ClaudeCodeVS
{
    public class ClaudeCodeSettings
    {
        public bool SendWithEnter { get; set; } = true;
        public double SplitterPosition { get; set; } = 236.0; // Default pixel height for first row
    }

    public partial class ClaudeCodeControl : UserControl, IDisposable
    {
        private System.Windows.Forms.Panel terminalPanel;
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

            Loaded += ClaudeCodeControl_Loaded;
            Unloaded += ClaudeCodeControl_Unloaded;
        }

        private void InitializeTempDirectory()
        {
            tempImageDirectory = Path.Combine(Path.GetTempPath(), "ClaudeCodeVS", Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempImageDirectory);
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
            ApplyLoadedSettings();

            // Mark initialization as complete to allow settings saving
            _isInitializing = false;

            ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (terminalPanel == null)
                {
                    await InitializeTerminalAsync();
                }
            }).FileAndForget("ClaudeCodeExtension/InitializeTerminal");
        }

        private void ApplyLoadedSettings()
        {
            // Ensure the send button visibility matches the checkbox state
            if (SendWithEnterCheckBox.IsChecked == true)
            {
                SendPromptButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                SendPromptButton.Visibility = Visibility.Visible;
            }
        }

        private async Task InitializeTerminalAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                terminalPanel = new System.Windows.Forms.Panel
                {
                    Dock = System.Windows.Forms.DockStyle.Fill,
                    BackColor = System.Drawing.Color.Black
                };

                TerminalHost.Child = terminalPanel;

                terminalPanel.Resize += (s, e) => ResizeEmbeddedTerminal();

                await Task.Delay(500);
                await StartEmbeddedTerminalAsync();
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                MessageBox.Show($"Failed to initialize terminal: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task StartEmbeddedTerminalAsync()
        {
            string workspaceDir = await GetWorkspaceDirectoryAsync();
            _lastWorkspaceDirectory = workspaceDir;

            try
            {
                if (cmdProcess != null && !cmdProcess.HasExited)
                {
                    cmdProcess.Kill();
                    cmdProcess.Dispose();
                }

                Debug.WriteLine($"Starting Claude in directory: {workspaceDir}");

                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/k cd /d \"" + workspaceDir + "\" && claude.cmd",
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    WindowStyle = ProcessWindowStyle.Minimized,
                    WorkingDirectory = workspaceDir
                };

                await Task.Run(() =>
                {
                    cmdProcess = new Process { StartInfo = startInfo };
                    cmdProcess.Start();
                });

                var hwnd = FindMainWindowHandleByPid(cmdProcess.Id, timeoutMs: 7000, pollIntervalMs: 50);
                terminalHandle = hwnd;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (terminalHandle != IntPtr.Zero && IsWindow(terminalHandle))
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
                }
                else
                {
                    MessageBox.Show(
                        "Could not find CMD window to embed. Make sure Claude Code is installed and accessible via 'claude.cmd'.",
                        "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
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

                    // Restart the terminal in the new directory
                    await StartEmbeddedTerminalAsync();
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
                        Foreground = System.Windows.Media.Brushes.Gainsboro,
                        VerticalAlignment = VerticalAlignment.Center
                    };
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
                //PostMessage(terminalHandle, WM_KEYDOWN, new IntPtr(VK_RETURN), IntPtr.Zero);
                //System.Threading.Thread.Sleep(50);
                PostMessage(terminalHandle, WM_CHAR, new IntPtr(VK_RETURN), IntPtr.Zero);
                //System.Threading.Thread.Sleep(50);
                //PostMessage(terminalHandle, WM_KEYUP, new IntPtr(VK_RETURN), IntPtr.Zero);
            }
        }

        private void ClaudeCodeControl_Unloaded(object sender, RoutedEventArgs e)
        {
            // Save settings one final time before unloading
            if (!_isInitializing)
            {
                SaveSettings();
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
            await StartEmbeddedTerminalAsync();
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