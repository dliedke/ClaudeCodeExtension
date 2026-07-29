/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
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

        /// <summary>
        /// Edge length of the square thumbnail on an image attachment chip. The chip height and the
        /// decode resolution both follow from it, so this is the only value to change when tuning.
        /// </summary>
        private const int AttachmentThumbnailSize = 32;

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
                if (ClipboardRetrySync(() => Clipboard.ContainsText()))
                {
                    return false;
                }

                BitmapSource image = null;

                // Try different clipboard formats for images
                if (ClipboardRetrySync(() => Clipboard.ContainsImage()))
                {
                    image = ClipboardRetrySync(() => Clipboard.GetImage());
                }
                else if (ClipboardRetrySync(() => Clipboard.ContainsData(DataFormats.Bitmap)))
                {
                    var bitmapData = ClipboardRetrySync(() => Clipboard.GetData(DataFormats.Bitmap));
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
                else if (ClipboardRetrySync(() => Clipboard.ContainsData("PNG")))
                {
                    var pngData = ClipboardRetrySync(() => Clipboard.GetData("PNG")) as MemoryStream;
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

        private void AttachDropdownButton_Click(object sender, RoutedEventArgs e)
        {
            if (AttachDropdownButton?.ContextMenu != null)
            {
                AttachDropdownButton.ContextMenu.PlacementTarget = AttachDropdownButton;
                AttachDropdownButton.ContextMenu.IsOpen = true;
            }
        }

        private void AttachFileMenuItem_Click(object sender, RoutedEventArgs e)
            => ImageDropBorder_Click(sender, null);

        /// <summary>
        /// Handles click on file drop border to open file selection dialog
        /// </summary>
        private void ImageDropBorder_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
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

        /// <summary>
        /// PreviewDragOver handler for the prompt textbox — accepts dropped files and shows
        /// the Copy cursor only when the payload actually contains file paths. Marked Handled
        /// so the textbox's default text-drop behavior doesn't override it.
        /// </summary>
        private void PromptTextBox_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        /// <summary>
        /// PreviewDrop handler for the prompt textbox — adds dropped files to the attachment
        /// list using the same path as the 📎 toolbar button. Directories are skipped (Claude
        /// can't attach folders directly); duplicates are filtered out.
        /// </summary>
        private void PromptTextBox_PreviewDrop(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    return;
                }

                var dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (dropped == null || dropped.Length == 0) return;

                bool any = false;
                foreach (string path in dropped)
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    if (Directory.Exists(path)) continue; // skip folders
                    if (!File.Exists(path)) continue;
                    if (attachedImagePaths.Contains(path)) continue;

                    attachedImagePaths.Add(path);
                    any = true;
                }

                if (any)
                {
                    UpdateImageDropDisplay();
                }

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PromptTextBox_PreviewDrop error: {ex.Message}");
                MessageBox.Show($"Error attaching dropped files: {ex.Message}",
                    "Drag & Drop", MessageBoxButton.OK, MessageBoxImage.Error);
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
                    AttachedImagesPanel.Children.Add(CreateAttachmentChip(path));
                }
            }

            // The chat tab has its own attachment strip showing the same list.
            UpdateComposerAttachmentChips();
        }

        /// <summary>
        /// Builds one attachment chip (file name + remove button). A fresh instance is created for
        /// every host because a WPF element can only live in one visual tree, and the panel and the
        /// chat composer both show the same attachments.
        /// </summary>
        private Border CreateAttachmentChip(string path)
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

            // Image attachments get a small square thumbnail plus a bigger preview on hover.
            // The chip grows to fit it, since ChipBorder's own Height is sized for text alone.
            var thumbnail = CreateAttachmentThumbnail(path);
            if (thumbnail != null)
            {
                chip.Height = AttachmentThumbnailSize + 8;
                sp.Children.Add(thumbnail);
            }

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

            return chip;
        }

        /// <summary>
        /// Builds the small square thumbnail shown at the left of an image chip, with a larger
        /// preview as its tooltip. Returns null for non-image files or anything that fails to
        /// decode (a broken file must never stop the chip itself from appearing).
        /// </summary>
        private FrameworkElement CreateAttachmentThumbnail(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !IsImageFile(path) || !File.Exists(path))
                {
                    return null;
                }

                // Decoded at 2x the display size so the chip stays crisp on a 200% DPI monitor.
                var bitmap = LoadThumbnailBitmap(path, AttachmentThumbnailSize * 2);
                if (bitmap == null) return null;

                // UniformToFill inside a fixed square crops instead of letterboxing, so wide
                // screenshots still show recognizable content at chip size.
                var thumb = new System.Windows.Shapes.Rectangle
                {
                    Width = AttachmentThumbnailSize,
                    Height = AttachmentThumbnailSize,
                    RadiusX = 3,
                    RadiusY = 3,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Fill = new System.Windows.Media.ImageBrush(bitmap)
                    {
                        Stretch = System.Windows.Media.Stretch.UniformToFill
                    }
                };
                System.Windows.Media.RenderOptions.SetBitmapScalingMode(
                    thumb, System.Windows.Media.BitmapScalingMode.HighQuality);

                // The preview is decoded on first hover, at its own resolution: reusing the 256 px
                // chip bitmap is what made it blurry, since WPF then upscaled it to preview size.
                var preview = new Image
                {
                    MaxWidth = 520,
                    MaxHeight = 400,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly
                };
                System.Windows.Media.RenderOptions.SetBitmapScalingMode(
                    preview, System.Windows.Media.BitmapScalingMode.HighQuality);

                thumb.ToolTip = preview;
                thumb.ToolTipOpening += (s, e) =>
                {
                    if (preview.Source != null) return;

                    var full = LoadThumbnailBitmap(path, 1200);
                    if (full == null)
                    {
                        e.Handled = true; // nothing to show; don't pop an empty tooltip
                        return;
                    }
                    preview.Source = full;
                };

                return thumb;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CreateAttachmentThumbnail error for '{path}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Decodes an image file for preview use, downscaling to <paramref name="maxWidth"/> only
        /// when it is actually wider than that — DecodePixelWidth also upscales, which produces a
        /// soft, interpolated bitmap for anything smaller. OnLoad caching is required so the file is
        /// not left locked: attachments live in the session temp folder that cleanup deletes later.
        /// </summary>
        private BitmapImage LoadThumbnailBitmap(string path, int maxWidth)
        {
            try
            {
                int naturalWidth = GetImagePixelWidth(path);

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (maxWidth > 0 && naturalWidth > maxWidth)
                {
                    bitmap.DecodePixelWidth = maxWidth;
                }
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadThumbnailBitmap error for '{path}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Reads an image's pixel width from its header without decoding the pixels. Returns 0 when
        /// the format can't be read, which callers treat as "decode at natural size".
        /// </summary>
        private int GetImagePixelWidth(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var decoder = BitmapDecoder.Create(
                        stream,
                        BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                        BitmapCacheOption.None);

                    return decoder.Frames.Count > 0 ? decoder.Frames[0].PixelWidth : 0;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetImagePixelWidth error for '{path}': {ex.Message}");
                return 0;
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
        /// Restores file attachments from a history entry, adding only files that still exist on disk
        /// </summary>
        /// <param name="filePaths">The file paths to restore</param>
        private void RestoreFilesFromHistory(List<string> filePaths)
        {
            attachedImagePaths.Clear();

            if (filePaths != null)
            {
                foreach (string path in filePaths)
                {
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        attachedImagePaths.Add(path);
                    }
                }
            }

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
