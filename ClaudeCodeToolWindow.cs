/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Tool window class for the Claude Code extension for VS.NET
 *
 * *******************************************************************************************************************/

using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

namespace ClaudeCodeVS
{
    [Guid("87654321-4321-4321-4321-987654321cba")]
    public class ClaudeCodeToolWindow : ToolWindowPane
    {
        private ClaudeCodeControl claudeCodeControl;

        public ClaudeCodeToolWindow() : base(null)
        {
            this.Caption = "Claude Code";
            claudeCodeControl = new ClaudeCodeControl();
            this.Content = claudeCodeControl;

            // Set up the reference so the control can update the title
            claudeCodeControl.SetToolWindow(this);
        }

        public void UpdateTitle(string providerName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.Caption = $"{providerName}";
        }
    }
}