/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Liedke
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Liedke is strictly forbidden.
 *
 * Purpose: Image attachment, paste, and display functionality
 *
 * *******************************************************************************************************************/

using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Image Fields

        /// <summary>
        /// List of currently attached image file paths
        /// </summary>
        private readonly List<string> attachedImagePaths = new List<string>();

        /// <summary>
        /// Counter for naming pasted images sequentially
        /// </summary>
        private int imageCounter = 1;

        #endregion

        #region Image Paste and Attachment

        /// <summary>
        /// Attempts to paste an image from the clipboard
        /// </summary>
        /// <returns>True if an image was successfully pasted, false otherwise</returns>
        private bool TryPasteImage()
        {
            try
            {
                // Check if clipboard contains text first - if so, let normal text paste happen
                // This prevents Excel cells (which have both text and image formats) from pasting as images
                if (Clipboard.ContainsText())
                {
                    return false;
                }

                // Check file limit
                if (attachedImagePaths.Count >= 5)
                {
                    MessageBox.Show("Maximum of 5 files can be attached.", "File Limit", MessageBoxButton.OK, MessageBoxImage.Information);
                    return false;
                }

                BitmapSource image = null;

                // Try different clipboard formats for images
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
                                BitmapSizeOptions.FromEmptyOptions());
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
                        image = BitmapFrame.Create(pngData);
                    }
                }

                if (image != null)
                {
                    // Ensure temp directory exists
                    if (!Directory.Exists(tempImageDirectory))
                    {
                        Directory.CreateDirectory(tempImageDirectory);
                    }

                    // Generate filename and save image
                    string fileName = $"image_{imageCounter}.png";
                    imageCounter++;
                    string imagePath = Path.Combine(tempImageDirectory, fileName);

                    using (var fileStream = new FileStream(imagePath, FileMode.Create))
                    {
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(image));
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

        /// <summary>
        /// Handles click on file drop border to open file selection dialog
        /// </summary>
        private void ImageDropBorder_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                // Check file limit
                if (attachedImagePaths.Count >= 5)
                {
                    MessageBox.Show("Maximum of 5 files can be attached.", "File Limit", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Open file dialog for file selection with common data formats
                OpenFileDialog openFileDialog = new OpenFileDialog
                {
                    Filter = "All files (*.*)|*.*|" +
                             "Images (*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|" +
                             "Documents (*.pdf;*.doc;*.docx;*.txt;*.rtf)|*.pdf;*.doc;*.docx;*.txt;*.rtf|" +
                             "Spreadsheets (*.xls;*.xlsx;*.csv)|*.xls;*.xlsx;*.csv|" +
                             "Data (*.json;*.xml;*.yaml;*.yml)|*.json;*.xml;*.yaml;*.yml|" +
                             "Code (*.cs;*.py;*.js;*.java;*.cpp;*.h)|*.cs;*.py;*.js;*.java;*.cpp;*.h",
                    Multiselect = true
                };

                if (openFileDialog.ShowDialog() == true)
                {
                    int remainingSlots = 5 - attachedImagePaths.Count;
                    int filesAdded = 0;

                    foreach (string filename in openFileDialog.FileNames)
                    {
                        if (attachedImagePaths.Count >= 5)
                        {
                            MessageBox.Show($"Maximum of 5 files can be attached. Only the first {remainingSlots} selected file(s) were added.", "File Limit", MessageBoxButton.OK, MessageBoxImage.Information);
                            break;
                        }
                        attachedImagePaths.Add(filename);
                        filesAdded++;
                    }
                    UpdateImageDropDisplay();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error selecting files: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Image Display Management

        /// <summary>
        /// Updates the UI to display currently attached images as chips
        /// </summary>
        private void UpdateImageDropDisplay()
        {
            AttachedImagesPanel.Children.Clear();

            if (attachedImagePaths.Any())
            {
                foreach (var path in attachedImagePaths.ToList())
                {
                    // Create chip border
                    var chip = new Border
                    {
                        Style = (Style)FindResource("ChipBorder"),
                        Cursor = Cursors.Hand,
                        Tag = path
                    };

                    // Make chip clickable to open image
                    chip.MouseLeftButtonUp += (s, e) =>
                    {
                        // Don't open if clicking the remove button
                        if (e.OriginalSource is Button)
                            return;

                        var imagePath = (string)((Border)s).Tag;
                        try
                        {
                            Process.Start(new ProcessStartInfo(imagePath) { UseShellExecute = true });
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Error opening image: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    };

                    // Create chip content
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

                    // Filename text - truncate if too long
                    string fileName = Path.GetFileName(path);
                    string displayName = fileName.Length > 18 ? fileName.Substring(0, 15) + "..." : fileName;

                    var nameBlock = new TextBlock
                    {
                        Text = displayName,
                        VerticalAlignment = VerticalAlignment.Center,
                        MaxWidth = 120,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        ToolTip = fileName,
                        FontSize = 11
                    };
                    nameBlock.SetResourceReference(TextBlock.ForegroundProperty, Microsoft.VisualStudio.Shell.VsBrushes.ToolWindowTextKey);

                    // Remove button
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

        /// <summary>
        /// Clears all attached images
        /// </summary>
        private void ClearAttachedImages()
        {
            attachedImagePaths.Clear();
            UpdateImageDropDisplay();
        }

        /// <summary>
        /// Checks if a file path represents an image file
        /// </summary>
        /// <param name="filePath">The file path to check</param>
        /// <returns>True if the file is an image, false otherwise</returns>
        private bool IsImageFile(string filePath)
        {
            string[] imageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tiff", ".webp" };
            string extension = Path.GetExtension(filePath).ToLower();
            return imageExtensions.Contains(extension);
        }

        #endregion
    }
}
