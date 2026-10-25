/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Image attachment thumbnails and hover previews, shared by the composer and the chat transcript
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.Shell;
using static ClaudeCodeVS.UI.Loc;

namespace ClaudeCodeVS.UI
{
    /// <summary>
    /// Decoding and hover preview of image attachments. Used by the composer's attachment chips and
    /// cards and by the thumbnails in a sent prompt's chat bubble.
    /// </summary>
    public static class ImagePreview
    {
        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tiff", ".webp" };

        /// <summary>Longest side the hover preview is decoded at.</summary>
        private const int PreviewDecodeWidth = 1200;

        /// <summary>Hover time before the preview opens. WPF's default follows the system and can be long.</summary>
        private const int PreviewShowDelayMs = 300;

        public static bool IsImageFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            return ImageExtensions.Contains(extension);
        }

        /// <summary>
        /// Decodes an image file for preview use, downscaling to <paramref name="maxWidth"/> only
        /// when it is actually wider than that — DecodePixelWidth also upscales, which produces a
        /// soft, interpolated bitmap for anything smaller. OnLoad caching is required so the file is
        /// not left locked: attachments live in the session temp folder that cleanup deletes later.
        /// Frozen, so it may be decoded on a background thread.
        /// </summary>
        public static BitmapImage LoadBitmap(string path, int maxWidth)
        {
            try
            {
                int naturalWidth = GetPixelWidth(path);

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
                Debug.WriteLine($"ImagePreview.LoadBitmap error for '{path}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Reads an image's pixel width from its header without decoding the pixels. Returns 0 when
        /// the format can't be read, which callers treat as "decode at natural size".
        /// </summary>
        public static int GetPixelWidth(string path)
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
                Debug.WriteLine($"ImagePreview.GetPixelWidth error for '{path}': {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Gives <paramref name="target"/> — the whole chip or card, not just the picture inside it, so
        /// hovering its border or the empty band around a wide screenshot also works — a large preview
        /// of the image as its tooltip.
        /// <para>
        /// The preview is decoded on a background thread as soon as the mouse enters, so it is usually
        /// ready by the time the tooltip opens; decoding it inside ToolTipOpening blocked the UI thread
        /// and made the first preview of a large screenshot slow. The tooltip opens after a fixed short
        /// delay and stays up while the mouse is over the target, instead of WPF's 5-second timeout
        /// after which it often would not come back until the mouse left and re-entered.
        /// </para>
        /// </summary>
        public static void AttachPreviewToolTip(FrameworkElement target, string path)
        {
            var preview = new Image
            {
                MaxWidth = 520,
                MaxHeight = 400,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly
            };
            RenderOptions.SetBitmapScalingMode(preview, BitmapScalingMode.HighQuality);

            bool loadStarted = false;
            bool loadFailed = false;

            void StartLoad()
            {
                if (loadStarted) return;
                loadStarted = true;

#pragma warning disable VSSDK007, VSTHRD110 // Fire and forget: FileAndForget is the handler
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    BitmapImage full = await Task.Run(() => LoadBitmap(path, PreviewDecodeWidth));
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    if (full == null)
                    {
                        loadFailed = true;
                        if (target.ToolTip is ToolTip open) open.IsOpen = false;
                        return;
                    }

                    preview.Source = full;
                }).FileAndForget("claudecode/imagepreview/load");
#pragma warning restore VSSDK007, VSTHRD110
            }

            target.ToolTip = new ToolTip { Content = preview };
            ToolTipService.SetInitialShowDelay(target, PreviewShowDelayMs);
            ToolTipService.SetBetweenShowDelay(target, 0);
            ToolTipService.SetShowDuration(target, int.MaxValue);
            ToolTipService.SetPlacement(target, System.Windows.Controls.Primitives.PlacementMode.Mouse);

            target.MouseEnter += (s, e) => StartLoad();
            target.ToolTipOpening += (s, e) =>
            {
                StartLoad();
                if (loadFailed)
                {
                    e.Handled = true; // nothing to show; don't pop an empty tooltip
                }
            };
        }

        /// <summary>Opens a file with its default program, as clicking an attachment does.</summary>
        public static void OpenFile(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(L("Error opening file: {0}", ex.Message), L("Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    /// <summary>
    /// Attached property that fills a panel with the thumbnails of a sent prompt's attachments:
    /// whole pictures (no crop, no file name), a large preview on hover and the file opened on click.
    /// A file that is gone or is not an image gets a small chip with its name instead.
    /// </summary>
    public static class ChatAttachmentStrip
    {
        /// <summary>Largest thumbnail box in the chat bubble; the picture keeps its proportions inside it.</summary>
        private const double ThumbnailMaxWidth = 200;
        private const double ThumbnailMaxHeight = 130;

        public static readonly DependencyProperty FilesProperty = DependencyProperty.RegisterAttached(
            "Files",
            typeof(IReadOnlyList<string>),
            typeof(ChatAttachmentStrip),
            new PropertyMetadata(null, OnFilesChanged));

        public static IReadOnlyList<string> GetFiles(DependencyObject element)
        {
            return (IReadOnlyList<string>)element.GetValue(FilesProperty);
        }

        public static void SetFiles(DependencyObject element, IReadOnlyList<string> value)
        {
            element.SetValue(FilesProperty, value);
        }

        private static void OnFilesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is Panel panel)) return;

            panel.Children.Clear();
            var files = e.NewValue as IReadOnlyList<string>;
            panel.Visibility = files != null && files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (files == null) return;

            foreach (string path in files)
            {
                FrameworkElement item = CreateThumbnail(path) ?? CreateFileChip(path);
                panel.Children.Add(item);
            }
        }

        private static FrameworkElement CreateThumbnail(string path)
        {
            try
            {
                if (!ImagePreview.IsImageFile(path) || !File.Exists(path)) return null;

                // Decoded at 2x the display width so it stays crisp on a 200% DPI monitor.
                BitmapImage bitmap = ImagePreview.LoadBitmap(path, (int)ThumbnailMaxWidth * 2);
                if (bitmap == null) return null;

                var image = new Image
                {
                    Source = bitmap,
                    MaxWidth = ThumbnailMaxWidth,
                    MaxHeight = ThumbnailMaxHeight,
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

                var frame = new Border
                {
                    Child = image,
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(2),
                    Margin = new Thickness(0, 0, 6, 6),
                    Cursor = Cursors.Hand,
                    Background = Brushes.Transparent,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                };
                frame.SetResourceReference(Border.BorderBrushProperty, Microsoft.VisualStudio.Shell.VsBrushes.ToolWindowBorderKey);
                frame.MouseLeftButtonUp += (s, e) => ImagePreview.OpenFile(path);
                ImagePreview.AttachPreviewToolTip(frame, path);
                return frame;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ChatAttachmentStrip thumbnail error for '{path}': {ex.Message}");
                return null;
            }
        }

        private static FrameworkElement CreateFileChip(string path)
        {
            bool exists = File.Exists(path);
            var chip = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 6, 6),
                Background = Brushes.Transparent,
                Cursor = exists ? Cursors.Hand : null,
                ToolTip = path,
                Child = new TextBlock
                {
                    Text = "📎 " + Path.GetFileName(path),
                    Opacity = exists ? 1.0 : 0.6
                }
            };
            chip.SetResourceReference(Border.BorderBrushProperty, Microsoft.VisualStudio.Shell.VsBrushes.ToolWindowBorderKey);
            if (exists)
            {
                chip.MouseLeftButtonUp += (s, e) => ImagePreview.OpenFile(path);
            }

            return chip;
        }
    }
}
