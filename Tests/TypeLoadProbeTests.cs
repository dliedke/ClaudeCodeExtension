/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Proves the ClaudeCodeControl type can be loaded outside a running Visual Studio, which is
 *          the precondition for every helper unit test in this project.
 *
 * *******************************************************************************************************************/

using ClaudeCodeVS;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    /// <summary>
    /// The helpers under test are static, so no <see cref="System.Windows.Controls.UserControl"/>
    /// instance is ever constructed. Touching any of them still runs the type's static constructor,
    /// which initializes the static fields of *all* the partial classes — including
    /// <c>_themeResourceKeys</c> in ClaudeCodeControl.Theme.cs, built from VS SDK
    /// <c>VsBrushes</c> members. If this test fails with a TypeInitializationException, the fix is
    /// to move the tested helpers into plain static classes instead of widening this project's
    /// dependency surface further.
    /// </summary>
    [TestClass]
    public class TypeLoadProbeTests
    {
        [TestMethod]
        public void ClaudeCodeControl_StaticHelpersAreReachableWithoutVisualStudio()
        {
            string encoded = ClaudeCodeControl.EncodeClaudeProjectPath(@"C:\GitLab\ClaudeCodeExtension");

            Assert.AreEqual("C--GitLab-ClaudeCodeExtension", encoded);
        }
    }
}
