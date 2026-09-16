/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Signals that a native session could not launch its CLI because the executable does not exist
 *
 * *******************************************************************************************************************/

using System;

namespace ClaudeCodeVS.Agents
{
    /// <summary>
    /// Thrown when the agent's executable is missing — not installed, or a custom CLI path that points
    /// nowhere. <see cref="Exception.Message"/> is already user-facing (what is missing and how to fix
    /// it), so the chat shows it as-is instead of prefixing it with a generic failure.
    /// </summary>
    public class AgentCliNotFoundException : Exception
    {
        public AgentCliNotFoundException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
