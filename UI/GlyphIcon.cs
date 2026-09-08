/* ***********************
 * Application: ClaudeCodeExtension
 * Autor:  Daniel Carvalho Liedke / Claude Code
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 * Purpose: Optical centering for the icon-only toolbar buttons (📝 / ↻ / ⚙ / ...). WPF centers a
 *          button's content by its text *layout* box, but an emoji or symbol glyph does not sit in
 *          the middle of that box — its ink hangs low against the baseline — so the glyphs render
 *          off-centre and look cropped against the button's bottom edge. This measures the glyph's
 *          real ink box and hands the template a translation that puts the ink in the middle.
 * ***********************/

using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClaudeCodeVS.UI
{
    /// <summary>
    /// Attached behavior for icon-only buttons: set <c>GlyphIcon.Center="True"</c> in the button's
    /// style, and bind the template's <c>ContentPresenter.RenderTransform</c> to
    /// <c>GlyphIcon.Offset</c>. Text content (anything with letters or digits, such as "Agent ▾") is
    /// left alone, so a style shared by icon and caption buttons can turn this on globally.
    /// <para>
    /// The offset is a render transform, not layout: nothing moves or resizes, so button widths, the
    /// composer's density tiers and the toolbar scrollers all measure exactly what they measured
    /// before.
    /// </para>
    /// </summary>
    public static class GlyphIcon
    {
        /// <summary>
        /// Largest correction applied, as a fraction of the font size. A glyph that would need more
        /// than this is either not an icon or was measured wrong; clamping keeps a bad measurement
        /// from throwing the button's content out of its own box.
        /// </summary>
        private const double MaxOffsetEm = 0.35;

        /// <summary>
        /// Ink boxes larger than this many em are rejected outright — real icon glyphs are about
        /// 1 em, and anything far past that means the content was not the single glyph we assumed.
        /// </summary>
        private const double MaxInkEm = 2.5;

        #region Attached properties

        /// <summary>Turns the behavior on for a button (or any other content control).</summary>
        public static readonly DependencyProperty CenterProperty =
            DependencyProperty.RegisterAttached(
                "Center",
                typeof(bool),
                typeof(GlyphIcon),
                new PropertyMetadata(false, OnCenterChanged));

        public static void SetCenter(DependencyObject element, bool value)
        {
            if (element == null) throw new ArgumentNullException(nameof(element));
            element.SetValue(CenterProperty, value);
        }

        public static bool GetCenter(DependencyObject element)
        {
            if (element == null) throw new ArgumentNullException(nameof(element));
            return (bool)element.GetValue(CenterProperty);
        }

        /// <summary>
        /// Computed translation for the content, written by this class and read by the control
        /// template. <see cref="Transform.Identity"/> whenever the content is not a single glyph.
        /// </summary>
        public static readonly DependencyProperty OffsetProperty =
            DependencyProperty.RegisterAttached(
                "Offset",
                typeof(Transform),
                typeof(GlyphIcon),
                new PropertyMetadata(Transform.Identity));

        public static void SetOffset(DependencyObject element, Transform value)
        {
            if (element == null) throw new ArgumentNullException(nameof(element));
            element.SetValue(OffsetProperty, value);
        }

        public static Transform GetOffset(DependencyObject element)
        {
            if (element == null) throw new ArgumentNullException(nameof(element));
            return (Transform)element.GetValue(OffsetProperty);
        }

        #endregion

        #region Wiring

        private static void OnCenterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as ContentControl;
            if (control == null) return;

            // Plain CLR events on the control itself: no DependencyPropertyDescriptor, which would
            // root every button it is attached to (the composer rebuilds its mirrored toolbar buttons
            // on each refresh, so those would never be collected).
            control.Loaded -= OnControlLoaded;
            control.SizeChanged -= OnControlSizeChanged;

            if (e.NewValue is bool && (bool)e.NewValue)
            {
                control.Loaded += OnControlLoaded;
                control.SizeChanged += OnControlSizeChanged;
                Apply(control);
            }
            else
            {
                SetOffset(control, Transform.Identity);
            }
        }

        private static void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            Apply(sender as ContentControl);
        }

        /// <summary>
        /// A different glyph or font size changes the button's measured width, so this doubles as the
        /// "content changed" hook. The offset itself never resizes anything, so this cannot loop.
        /// </summary>
        private static void OnControlSizeChanged(object sender, SizeChangedEventArgs e)
        {
            Apply(sender as ContentControl);
        }

        #endregion

        /// <summary>
        /// Measures the control's glyph and stores the centering translation. Any failure leaves the
        /// content exactly where WPF put it.
        /// </summary>
        private static void Apply(ContentControl control)
        {
            if (control == null) return;

            Transform offset = Transform.Identity;

            try
            {
                string glyph = control.Content as string;
                double fontSize = control.FontSize;

                if (IsIconGlyph(glyph) && fontSize > 0 && !double.IsNaN(fontSize) && !double.IsInfinity(fontSize))
                {
                    double pixelsPerDip = 1.0;
                    try { pixelsPerDip = VisualTreeHelper.GetDpi(control).PixelsPerDip; }
                    catch (Exception) { /* not connected to a source yet — 1.0 only affects hinting */ }

                    var typeface = new Typeface(control.FontFamily, control.FontStyle, control.FontWeight, control.FontStretch);
                    var text = new FormattedText(
                        glyph.Trim(),
                        CultureInfo.CurrentUICulture,
                        control.FlowDirection,
                        typeface,
                        fontSize,
                        Brushes.Black,
                        pixelsPerDip);

                    // BuildGeometry places the text with the origin at the top-left of the layout box,
                    // so these bounds are directly comparable to [0..Width] x [0..Height].
                    Geometry ink = text.BuildGeometry(new Point(0, 0));
                    Rect inkBounds = ink != null ? ink.Bounds : Rect.Empty;

                    double dx, dy;
                    if (TryComputeOffset(inkBounds, text.Width, text.Height, fontSize, out dx, out dy)
                        && (dx != 0 || dy != 0))
                    {
                        var translate = new TranslateTransform(dx, dy);
                        translate.Freeze();
                        offset = translate;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("GlyphIcon.Apply failed: " + ex.Message);
                offset = Transform.Identity;
            }

            SetOffset(control, offset);
        }

        /// <summary>
        /// True for content that is a single icon glyph — one or two code points, no letters, digits
        /// or embedded spaces. Captions such as "Agent ▾" or "Skip ▾" are excluded so a shared style
        /// can enable the behavior for every button that uses it.
        /// </summary>
        public static bool IsIconGlyph(string content)
        {
            if (string.IsNullOrEmpty(content)) return false;

            string glyph = content.Trim();

            // Two surrogate pairs plus variation selectors is already more than any single icon needs.
            if (glyph.Length == 0 || glyph.Length > 4) return false;

            foreach (char c in glyph)
            {
                // Only judge the BMP range that holds Latin text; symbol and emoji code points above
                // it are exactly what this behavior is for.
                if (c < 0x2000 && (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))) return false;
            }

            return true;
        }

        /// <summary>
        /// Translation that moves the glyph's ink box to the middle of its layout box. Pure: takes the
        /// measurements rather than doing them, so it is unit-testable without a WPF text stack.
        /// </summary>
        /// <param name="ink">Ink bounds of the glyph, relative to the top-left of the layout box.</param>
        /// <param name="layoutWidth">Advance width WPF gives the content.</param>
        /// <param name="layoutHeight">Line height WPF gives the content.</param>
        /// <param name="fontSize">Font size the glyph was measured at, used for the sanity limits.</param>
        /// <returns>False when the measurements are unusable, in which case no offset should be applied.</returns>
        public static bool TryComputeOffset(
            Rect ink,
            double layoutWidth,
            double layoutHeight,
            double fontSize,
            out double dx,
            out double dy)
        {
            dx = 0;
            dy = 0;

            if (!IsUsable(fontSize) || !IsUsable(layoutWidth) || !IsUsable(layoutHeight)) return false;
            if (ink.IsEmpty || !IsUsable(ink.Width) || !IsUsable(ink.Height)) return false;
            if (double.IsNaN(ink.Left) || double.IsInfinity(ink.Left)) return false;
            if (double.IsNaN(ink.Top) || double.IsInfinity(ink.Top)) return false;

            double maxInk = fontSize * MaxInkEm;
            if (ink.Width > maxInk || ink.Height > maxInk) return false;

            double limit = fontSize * MaxOffsetEm;

            // Rounded to whole device-independent pixels: a fractional render transform would blur the
            // glyph, and the leftover half pixel is not something the eye picks up on a 13pt icon.
            dx = Math.Round(Clamp((layoutWidth / 2.0) - (ink.Left + (ink.Width / 2.0)), -limit, limit));
            dy = Math.Round(Clamp((layoutHeight / 2.0) - (ink.Top + (ink.Height / 2.0)), -limit, limit));

            return true;
        }

        private static bool IsUsable(double value)
        {
            return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
