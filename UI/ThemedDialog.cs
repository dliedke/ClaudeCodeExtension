/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Gives one of the extension's WPF dialogs the look of a Visual Studio dialog: a title bar drawn in
 *          the IDE theme instead of the white Windows one, VS-themed scrollbars and controls, and the
 *          rounded button / search box styles of ThemedDialogStyles.xaml.
 *
 * *******************************************************************************************************************/

using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS.UI
{
    internal static class ThemedDialog
    {
        private const double TitleBarHeight = 32;

        private static readonly Uri StylesUri =
            new Uri("/ClaudeCodeExtension;component/UI/ThemedDialogStyles.xaml", UriKind.Relative);

        /// <summary>
        /// Loads the dialog styles into <paramref name="window"/>'s resources (so <see cref="Style"/> can
        /// find them while the content is being built) and turns on VS's themed default styles.
        /// Call right after creating the window, before building its content.
        /// </summary>
        public static void PrepareResources(Window window)
        {
            try
            {
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = StylesUri });
                ThemedDialogStyleLoader.SetUseDefaultThemedDialogStyles(window, true);

                // Thin dark scrollbars like the editor's, instead of the light system ones.
                if (Application.Current?.TryFindResource(VsResourceKeys.ScrollBarStyleKey) is Style scrollBar)
                {
                    window.Resources[typeof(ScrollBar)] = scrollBar;
                }
                if (Application.Current?.TryFindResource(VsResourceKeys.ScrollViewerStyleKey) is Style scrollViewer)
                {
                    window.Resources[typeof(ScrollViewer)] = scrollViewer;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ThemedDialog.PrepareResources: {ex.Message}");
            }
        }

        /// <summary>One of the styles of ThemedDialogStyles.xaml (null when not loaded).</summary>
        public static Style GetStyle(Window window, string key) => window.TryFindResource(key) as Style;

        /// <summary>
        /// Replaces the Windows title bar with one drawn in the IDE theme: the window's title on the left,
        /// ✕ on the right, dragging and (for resizable windows) resizing and double-click maximize kept by
        /// <see cref="WindowChrome"/>. Call once the content is set.
        /// </summary>
        public static void ApplyChrome(Window window)
        {
            try
            {
                var content = window.Content as UIElement;
                window.Content = null;

                bool resizable = window.ResizeMode == ResizeMode.CanResize || window.ResizeMode == ResizeMode.CanResizeWithGrip;
                WindowChrome.SetWindowChrome(window, new WindowChrome
                {
                    CaptionHeight = TitleBarHeight,
                    ResizeBorderThickness = resizable ? new Thickness(5) : new Thickness(0),
                    GlassFrameThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(0),
                    UseAeroCaptionButtons = false
                });

                var title = new TextBlock
                {
                    Text = window.Title,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(14, 0, 8, 0)
                };
                title.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.MainWindowActiveCaptionTextBrushKey);

                var close = new Button
                {
                    Content = "", // ChromeClose
                    Style = GetStyle(window, "ThemedDialogCaptionButtonStyle"),
                    ToolTip = Loc.L("Close")
                };
                WindowChrome.SetIsHitTestVisibleInChrome(close, true);
                close.Click += (s, e) => window.Close();

                var titleBar = new DockPanel { Height = TitleBarHeight, LastChildFill = true };
                DockPanel.SetDock(close, Dock.Right);
                titleBar.Children.Add(close);
                titleBar.Children.Add(title);

                var layout = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(titleBar, Dock.Top);
                layout.Children.Add(titleBar);
                if (content != null)
                {
                    layout.Children.Add(content);
                }

                // The 1px frame Windows no longer draws; a maximized window would also overhang the
                // screen by the resize border, so it gets that much padding back.
                var frame = new Border { BorderThickness = new Thickness(1), Child = layout };
                frame.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
                window.StateChanged += (s, e) =>
                    frame.Padding = window.WindowState == WindowState.Maximized ? new Thickness(6) : new Thickness(0);

                window.Content = frame;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ThemedDialog.ApplyChrome: {ex.Message}");
            }
        }

        /// <summary>A rounded frame around a list, in the theme's border color.</summary>
        public static Border ListFrame(UIElement child)
        {
            var border = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                Child = child
            };
            border.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            return border;
        }
    }
}
