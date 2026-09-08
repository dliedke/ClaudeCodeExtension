/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for the optical centering of icon-only toolbar buttons (UI/GlyphIcon.cs)
 *
 * *******************************************************************************************************************/

using System.Windows;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class GlyphIconTests
    {
        [TestMethod]
        public void EmojiSittingLowIsLiftedToTheMiddle()
        {
            // Measurements taken from the toolbar's 📝 at 13pt: the line box is 17.3 tall, the ink is
            // 12 tall and starts 4.7 down, so its middle sits ~2 below the box's own middle.
            double dx, dy;
            bool ok = GlyphIcon.TryComputeOffset(new Rect(1.5, 4.7, 12.0, 12.0), 15.0, 17.3, 13.0, out dx, out dy);

            Assert.IsTrue(ok);
            Assert.AreEqual(-2.0, dy, 0.001, "the glyph should be pulled up, off the bottom edge");
            Assert.AreEqual(0.0, dx, 0.001, "ink already centered in the advance width stays put");
        }

        [TestMethod]
        public void GlyphAlreadyCenteredGetsNoOffset()
        {
            double dx, dy;
            bool ok = GlyphIcon.TryComputeOffset(new Rect(2.0, 3.0, 12.0, 12.0), 16.0, 18.0, 13.0, out dx, out dy);

            Assert.IsTrue(ok);
            Assert.AreEqual(0.0, dx, 0.001);
            Assert.AreEqual(0.0, dy, 0.001);
        }

        [TestMethod]
        public void OffsetIsClampedToASaneFractionOfTheFontSize()
        {
            // A measurement this far off is not believable; the clamp keeps a bad one from throwing the
            // glyph out of its own button instead of nudging it.
            double dx, dy;
            bool ok = GlyphIcon.TryComputeOffset(new Rect(0.0, 14.0, 3.0, 3.0), 16.0, 18.0, 13.0, out dx, out dy);

            Assert.IsTrue(ok);
            Assert.IsTrue(dy >= -5.0 && dy <= 5.0, "expected the 0.35em clamp, got " + dy);
        }

        [TestMethod]
        public void UnusableMeasurementsAreRejected()
        {
            double dx, dy;

            Assert.IsFalse(GlyphIcon.TryComputeOffset(Rect.Empty, 16.0, 18.0, 13.0, out dx, out dy), "empty ink");
            Assert.IsFalse(GlyphIcon.TryComputeOffset(new Rect(0, 0, 12, 12), 0.0, 18.0, 13.0, out dx, out dy), "no advance width");
            Assert.IsFalse(GlyphIcon.TryComputeOffset(new Rect(0, 0, 12, 12), 16.0, 18.0, 0.0, out dx, out dy), "no font size");

            // Far taller than an em: whatever was measured, it was not a single icon glyph.
            Assert.IsFalse(GlyphIcon.TryComputeOffset(new Rect(0, 0, 12, 60), 16.0, 18.0, 13.0, out dx, out dy), "implausible ink");
        }

        [TestMethod]
        public void OnlyIconGlyphsAreCentered()
        {
            Assert.IsTrue(GlyphIcon.IsIconGlyph("📝"));
            Assert.IsTrue(GlyphIcon.IsIconGlyph("♻️"));
            Assert.IsTrue(GlyphIcon.IsIconGlyph("🛠️"));
            Assert.IsTrue(GlyphIcon.IsIconGlyph("↻"));
            Assert.IsTrue(GlyphIcon.IsIconGlyph("✚"));
            Assert.IsTrue(GlyphIcon.IsIconGlyph("⚙"));
            Assert.IsTrue(GlyphIcon.IsIconGlyph("☰"));
            Assert.IsTrue(GlyphIcon.IsIconGlyph("▶"));

            // Captions keep WPF's own centering: shifting them by their ink would break their
            // alignment with the neighboring selectors.
            Assert.IsFalse(GlyphIcon.IsIconGlyph("Agent ▾"));
            Assert.IsFalse(GlyphIcon.IsIconGlyph("Skip ▾"));
            Assert.IsFalse(GlyphIcon.IsIconGlyph("5"));
            Assert.IsFalse(GlyphIcon.IsIconGlyph(""));
            Assert.IsFalse(GlyphIcon.IsIconGlyph(null));
        }
    }
}
