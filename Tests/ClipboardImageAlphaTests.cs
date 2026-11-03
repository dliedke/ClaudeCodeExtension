/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for the pasted-image alpha repair (issue #260, UI/ImagePreview.cs)
 *
 * *******************************************************************************************************************/

using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class ClipboardImageAlphaTests
    {
        private static BitmapSource Bgra(byte alpha)
        {
            // 2x1: one red pixel, one green pixel, both with the given alpha.
            byte[] pixels = { 0, 0, 255, alpha, 0, 255, 0, alpha };
            return BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        }

        [TestMethod]
        public void FullyTransparentImageBecomesOpaqueWithItsColorsKept()
        {
            BitmapSource result = ImagePreview.MakeOpaqueIfAlphaEmpty(Bgra(0));

            Assert.AreEqual(PixelFormats.Bgr32, result.Format);
            var pixels = new byte[8];
            result.CopyPixels(pixels, 8, 0);
            CollectionAssert.AreEqual(new byte[] { 0, 0, 255 }, new[] { pixels[0], pixels[1], pixels[2] });
            CollectionAssert.AreEqual(new byte[] { 0, 255, 0 }, new[] { pixels[4], pixels[5], pixels[6] });
        }

        [TestMethod]
        public void ImageWithRealAlphaIsLeftAlone()
        {
            BitmapSource source = Bgra(128);
            Assert.AreSame(source, ImagePreview.MakeOpaqueIfAlphaEmpty(source));
        }

        [TestMethod]
        public void ImageWithOnlySomeTransparentPixelsIsLeftAlone()
        {
            byte[] pixels = { 0, 0, 255, 0, 0, 255, 0, 255 };
            BitmapSource source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            Assert.AreSame(source, ImagePreview.MakeOpaqueIfAlphaEmpty(source));
        }

        [TestMethod]
        public void ImageWithoutAlphaChannelIsLeftAlone()
        {
            byte[] pixels = { 0, 0, 255, 0, 255, 0 };
            BitmapSource source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgr24, null, pixels, 6);
            Assert.AreSame(source, ImagePreview.MakeOpaqueIfAlphaEmpty(source));
            Assert.IsNull(ImagePreview.MakeOpaqueIfAlphaEmpty(null));
        }
    }
}
