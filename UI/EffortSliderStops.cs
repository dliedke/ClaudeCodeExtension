/* ***********************
 * Application: ClaudeCodeExtension
 * Autor:  Daniel Carvalho Liedke / Claude Code
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 * Purpose: Stop dots and the top-stop colour of the effort pill slider (EffortSliderStyle). The stop
 *          count depends on the agent — Claude's effort has seven stops since Auto came back (v280.0),
 *          Codex's reasoning seven as well — so the template only reserves the dot row and this fills
 *          it: one dot under every stop the thumb can sit on, and a flag on the top stop that turns the
 *          fill purple. The template used to draw six fixed dots and colour stop 5, which was right for
 *          neither agent once Auto was on the slider.
 * ***********************/

using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ClaudeCodeVS.UI
{
    internal static class EffortSliderStops
    {
        /// <summary>Value of <see cref="FrameworkElement.Tag"/> while the thumb is on the top stop.</summary>
        public const string TopStopTag = "Top";

        private const double ThumbWidth = 16;
        private const double DotSize = 4;

        private static readonly Brush DotBrush = CreateDotBrush();

        /// <summary>Keeps the slider's dots and top-stop flag in step with its range, size and value.</summary>
        public static void Attach(Slider slider)
        {
            if (slider == null)
            {
                return;
            }

            slider.Loaded += (s, e) => Update(slider);
            slider.SizeChanged += (s, e) => Update(slider);
            slider.ValueChanged += (s, e) => UpdateTopStop(slider);
            DependencyPropertyDescriptor.FromProperty(RangeBase.MaximumProperty, typeof(Slider))
                .AddValueChanged(slider, (s, e) => Update(slider));
            Update(slider);
        }

        /// <summary>
        /// Offset and spacing that put one dot centre under each stop: the thumb's centre moves from
        /// half a thumb width in from the left edge to the same distance from the right, so a row of
        /// equal cells as wide as the stop spacing is shifted left by half a cell (a negative margin
        /// when the stops are wider apart than the thumb). Pure, for the tests.
        /// </summary>
        internal static void ComputeDotRow(double trackWidth, int stops, out double cellWidth, out double leftMargin)
        {
            if (stops < 2 || trackWidth <= ThumbWidth)
            {
                cellWidth = Math.Max(0, trackWidth);
                leftMargin = 0;
                return;
            }

            cellWidth = (trackWidth - ThumbWidth) / (stops - 1);
            leftMargin = ThumbWidth / 2 - cellWidth / 2;
        }

        private static void Update(Slider slider)
        {
            UpdateTopStop(slider);

            if (!(slider.Template?.FindName("PART_StopDots", slider) is StackPanel row))
            {
                return;
            }

            int stops = (int)Math.Round(slider.Maximum - slider.Minimum) + 1;
            double width = slider.ActualWidth > 0 ? slider.ActualWidth : slider.Width;
            if (double.IsNaN(width) || stops < 1)
            {
                return;
            }

            ComputeDotRow(width, stops, out double cellWidth, out double leftMargin);
            row.Margin = new Thickness(leftMargin, 0, 0, 0);

            if (row.Children.Count != stops)
            {
                row.Children.Clear();
                for (int i = 0; i < stops; i++)
                {
                    // One cell per stop, the dot centred in it.
                    var cell = new Grid();
                    cell.Children.Add(new Ellipse
                    {
                        Width = DotSize,
                        Height = DotSize,
                        Fill = DotBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    row.Children.Add(cell);
                }
            }

            foreach (UIElement child in row.Children)
            {
                if (child is FrameworkElement cell)
                {
                    cell.Width = cellWidth;
                }
            }
        }

        private static void UpdateTopStop(Slider slider)
        {
            bool top = slider.Maximum > slider.Minimum && slider.Value >= slider.Maximum - 0.001;
            slider.Tag = top ? TopStopTag : null;
        }

        private static Brush CreateDotBrush()
        {
            var brush = new SolidColorBrush(Color.FromArgb(0x80, 0x60, 0x60, 0x60));
            brush.Freeze();
            return brush;
        }
    }
}
