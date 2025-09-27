using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

namespace ClaudeCodeVS
{
    [Guid("87654321-4321-4321-4321-987654321cba")]
    public class ClaudeCodeToolWindow : ToolWindowPane
    {
        public ClaudeCodeToolWindow() : base(null)
        {
            this.Caption = "Claude Code Assistant";
            this.Content = new ClaudeCodeControl();
        }
    }
}