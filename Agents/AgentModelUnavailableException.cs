/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Signals that a native session could not be started on the model the user picked
 *
 * *******************************************************************************************************************/

using System;

namespace ClaudeCodeVS.Agents
{
    /// <summary>
    /// Thrown from <c>IAgentSession.StartAsync</c> when the agent came up fine but cannot run on the
    /// model the user selected — it publishes a model picker and that model is not in it, or the
    /// protocol call that applies it was rejected.
    /// <para>
    /// A distinct type because the caller treats it differently from every other start failure: the
    /// agent itself is healthy, so retrying native mode would keep landing here, and the fix is a
    /// setting only the user can change. <c>ClaudeCodeControl</c> rolls back to the embedded terminal
    /// on this exception, where the 🤖 model menu is available (it is hidden in native mode, whose
    /// model picker lives in the chat composer instead) and where the CLI prints its own diagnosis of
    /// the bad model.
    /// </para>
    /// <para>
    /// Only raised when the agent actually offers a model picker. An agent that publishes none keeps
    /// its own default silently — nothing failed to load in that case, there is simply nothing to
    /// select.
    /// </para>
    /// </summary>
    public class AgentModelUnavailableException : Exception
    {
        public AgentModelUnavailableException(string agentDisplayName, string modelName, string message)
            : base(message)
        {
            AgentDisplayName = agentDisplayName ?? string.Empty;
            ModelName = modelName ?? string.Empty;
        }

        /// <summary>Name shown to the user for the agent that refused the model ("Devin").</summary>
        public string AgentDisplayName { get; }

        /// <summary>The model that could not be applied, as it was configured ("swe-1-6").</summary>
        public string ModelName { get; }
    }
}
