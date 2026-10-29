/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Stop dots of the effort pill slider: one dot centred under every position the thumb can stop at, for
 *          any number of stops (Auto made Claude's effort seven stops in v280.0).
 *
 * *******************************************************************************************************************/

using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class EffortSliderStopsTests
    {
        private const double ThumbWidth = 16;

        [TestMethod]
        public void EveryDotSitsUnderAThumbStop()
        {
            foreach (int stops in new[] { 6, 7 })
            {
                const double width = 175;
                EffortSliderStops.ComputeDotRow(width, stops, out double cell, out double left);

                for (int i = 0; i < stops; i++)
                {
                    double dotCentre = left + cell * i + cell / 2;
                    double thumbCentre = ThumbWidth / 2 + i * (width - ThumbWidth) / (stops - 1);
                    Assert.AreEqual(thumbCentre, dotCentre, 0.001, $"stop {i} of {stops}");
                }
            }
        }

        [TestMethod]
        public void DegenerateSlidersDoNotThrow()
        {
            EffortSliderStops.ComputeDotRow(175, 1, out double cell, out double left);
            Assert.AreEqual(175, cell);
            Assert.AreEqual(0, left);

            EffortSliderStops.ComputeDotRow(10, 7, out cell, out left);
            Assert.AreEqual(0, left);
        }
    }
}
