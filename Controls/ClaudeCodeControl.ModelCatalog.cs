/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Discovers each provider's model list from its CLI and records the model the user picked
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using ClaudeCodeVS.Agents;

using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Catalog Sources

        /// <summary>How a provider's model list is obtained and how its output is read back.</summary>
        private class ModelCatalogSource
        {
            /// <summary>Command line appended to the provider's executable, e.g. "debug models".</summary>
            public string Arguments { get; set; } = string.Empty;

            /// <summary>Executable to run when the user configured no custom path.</summary>
            public string DefaultCommand { get; set; } = string.Empty;

            /// <summary>Runs inside WSL rather than on Windows.</summary>
            public bool UseWsl { get; set; }

            public Func<string, List<ModelOption>> Parse { get; set; }
        }

        /// <summary>
        /// The listing command per provider, measured against the real CLIs. Providers absent from
        /// this table either have no listing command (Reasonix, Devin — their lists live in the
        /// settings) or no extension-managed model list at all (Claude, which has a fixed menu).
        /// </summary>
        private static readonly Dictionary<AiProvider, ModelCatalogSource> ModelCatalogSources =
            new Dictionary<AiProvider, ModelCatalogSource>
            {
                [AiProvider.CodexNative] = new ModelCatalogSource
                {
                    DefaultCommand = "codex",
                    Arguments = "debug models",
                    Parse = ModelCatalogParsers.ParseCodexCatalog
                },
                [AiProvider.Codex] = new ModelCatalogSource
                {
                    DefaultCommand = "codex",
                    Arguments = "debug models",
                    UseWsl = true,
                    Parse = ModelCatalogParsers.ParseCodexCatalog
                },
                [AiProvider.CursorAgentNative] = new ModelCatalogSource
                {
                    DefaultCommand = "agent",
                    Arguments = "--list-models",
                    Parse = ModelCatalogParsers.ParseIdDashNameList
                },
                [AiProvider.CursorAgent] = new ModelCatalogSource
                {
                    DefaultCommand = "cursor-agent",
                    Arguments = "--list-models",
                    UseWsl = true,
                    Parse = ModelCatalogParsers.ParseIdDashNameList
                },
                [AiProvider.OpenCode] = new ModelCatalogSource
                {
                    DefaultCommand = "opencode",
                    Arguments = "models",
                    Parse = ModelCatalogParsers.ParsePlainList
                },
                [AiProvider.Pi] = new ModelCatalogSource
                {
                    DefaultCommand = "pi",
                    Arguments = "--list-models",
                    Parse = ModelCatalogParsers.ParsePiModelList
                },
                [AiProvider.Antigravity] = new ModelCatalogSource
                {
                    DefaultCommand = "agy",
                    Arguments = "models",
                    Parse = ModelCatalogParsers.ParsePlainList
                }
            };

        /// <summary>A cached list older than this is refreshed the next time the menu opens.</summary>
        private static readonly TimeSpan ModelCatalogTimeToLive = TimeSpan.FromHours(24);

        /// <summary>
        /// Listing a model can reach the network (Cursor authenticates, PI reads its provider
        /// registry), so the wait is generous — it happens off the UI thread and at most once a day.
        /// </summary>
        private const int ModelCatalogTimeoutMs = 30000;

        /// <summary>Fetches in flight, so two menu openings do not start the same CLI twice.</summary>
        private readonly Dictionary<AiProvider, Task<List<ModelOption>>> _modelCatalogFetches
            = new Dictionary<AiProvider, Task<List<ModelOption>>>();

        #endregion

        #region Catalog Access

        /// <summary>
        /// Whether the model menu offers a list for this provider. Claude has its own fixed menu and
        /// is deliberately excluded; everything else either lists its models on the command line or
        /// carries a user-editable list in the settings.
        /// </summary>
        private static bool ProviderHasModelCatalog(AiProvider? provider)
        {
            if (provider == null || IsClaudeProvider(provider)) return false;

            return ModelCatalogSources.ContainsKey(provider.Value)
                || IsDevinProvider(provider)
                || provider == AiProvider.Reasonix;
        }

        private static bool IsDevinProvider(AiProvider? provider)
        {
            return provider == AiProvider.Devin || provider == AiProvider.DevinNative;
        }

        /// <summary>
        /// The models to show right now, without starting a process: the settings list for the
        /// agents that have one, otherwise whatever the last CLI call cached. Empty means the
        /// caller should kick off <see cref="RefreshProviderModelsAsync"/> and show a placeholder.
        /// </summary>
        private List<ModelOption> GetCachedProviderModels(AiProvider provider)
        {
            if (IsDevinProvider(provider))
            {
                EnsureDevinModelDefaults();
                return ToModelOptions(_settings?.DevinModels);
            }

            if (provider == AiProvider.Reasonix)
            {
                EnsureReasonixModelDefaults();
                return ToModelOptions(_settings?.ReasonixModels);
            }

            ModelCatalogCache cache = GetModelCatalogCache(provider);

            return cache?.Models != null ? new List<ModelOption>(cache.Models) : new List<ModelOption>();
        }

        /// <summary>True when the cached list is missing or old enough to be worth re-reading.</summary>
        private bool ShouldRefreshProviderModels(AiProvider provider)
        {
            if (!ModelCatalogSources.ContainsKey(provider)) return false;

            ModelCatalogCache cache = GetModelCatalogCache(provider);
            if (cache?.Models == null || cache.Models.Count == 0) return true;

            return DateTime.UtcNow - cache.FetchedUtc > ModelCatalogTimeToLive;
        }

        private ModelCatalogCache GetModelCatalogCache(AiProvider provider)
        {
            if (_settings?.ModelCatalogs == null) return null;

            ModelCatalogCache cache;
            return _settings.ModelCatalogs.TryGetValue(provider.ToString(), out cache) ? cache : null;
        }

        /// <summary>
        /// Runs the provider's listing command and stores the result. Concurrent calls for the same
        /// provider share one process. Returns the cached list unchanged when the CLI is missing or
        /// prints nothing usable — a failed refresh must not empty a list that was working.
        /// </summary>
        private Task<List<ModelOption>> RefreshProviderModelsAsync(AiProvider provider)
        {
            ModelCatalogSource source;
            if (!ModelCatalogSources.TryGetValue(provider, out source))
            {
                return Task.FromResult(GetCachedProviderModels(provider));
            }

            lock (_modelCatalogFetches)
            {
                Task<List<ModelOption>> running;
                if (_modelCatalogFetches.TryGetValue(provider, out running) && !running.IsCompleted)
                {
                    return running;
                }

                Task<List<ModelOption>> fetch = FetchProviderModelsAsync(provider, source);
                _modelCatalogFetches[provider] = fetch;

                return fetch;
            }
        }

        private async Task<List<ModelOption>> FetchProviderModelsAsync(AiProvider provider, ModelCatalogSource source)
        {
            List<ModelOption> models = null;

            try
            {
                string output = await Task.Run(() => RunModelListingCommand(provider, source));
                if (!string.IsNullOrWhiteSpace(output))
                {
                    models = source.Parse(output);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Model catalog: listing {provider} failed: {ex.Message}");
            }

            if (models == null || models.Count == 0)
            {
                Debug.WriteLine($"Model catalog: {provider} returned no models; keeping the previous list.");
                return GetCachedProviderModels(provider);
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_settings != null)
            {
                if (_settings.ModelCatalogs == null)
                {
                    _settings.ModelCatalogs = new Dictionary<string, ModelCatalogCache>();
                }

                _settings.ModelCatalogs[provider.ToString()] = new ModelCatalogCache
                {
                    Models = models,
                    FetchedUtc = DateTime.UtcNow
                };

                // A model that disappeared from the CLI (renamed, retired) would otherwise stay
                // selected and be rejected at launch with no menu entry to correct it.
                DropSelectionMissingFromCatalog(provider, models);

                SaveSettings();
            }

            return models;
        }

        /// <summary>
        /// Runs the listing command and returns its stdout. Windows commands go through cmd.exe so a
        /// .cmd/.ps1 npm shim resolves the same way the terminal resolves it; WSL commands go through
        /// an interactive bash, without which an nvm-managed CLI is not on PATH.
        /// </summary>
        private string RunModelListingCommand(AiProvider provider, ModelCatalogSource source)
        {
            string executable = ResolveNativeProviderExecutable(provider, source.DefaultCommand);
            string commandLine = QuoteIfNeeded(executable) + " " + source.Arguments;

            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            if (source.UseWsl)
            {
                startInfo.FileName = "wsl.exe";
                startInfo.Arguments = "bash -ic \"" + commandLine.Replace("\"", "\\\"") + "\"";
            }
            else
            {
                startInfo.FileName = "cmd.exe";
                startInfo.Arguments = "/c " + commandLine;

                string freshPath = GetFreshPathFromRegistry();
                if (!string.IsNullOrWhiteSpace(freshPath))
                {
                    startInfo.EnvironmentVariables["PATH"] = freshPath;
                }
            }

            using (var process = new Process { StartInfo = startInfo })
            {
                var stdout = new StringBuilder();
                process.OutputDataReceived += (s, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };

                // stderr is drained but discarded: these CLIs write update notices and MCP banners
                // there, and a full pipe would block the process instead of letting it exit.
                process.ErrorDataReceived += (s, e) => { };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(ModelCatalogTimeoutMs))
                {
                    Debug.WriteLine($"Model catalog: {provider} listing timed out.");
                    try { ProcessTree.Kill(process.Id); } catch { }
                    return string.Empty;
                }

                return stdout.ToString();
            }
        }

        private static string QuoteIfNeeded(string executable)
        {
            string value = (executable ?? string.Empty).Trim();
            if (value.Length == 0) return value;
            if (value.StartsWith("\"", StringComparison.Ordinal)) return value;

            return value.IndexOf(' ') >= 0 ? "\"" + value + "\"" : value;
        }

        private static List<ModelOption> ToModelOptions(IEnumerable<string> names)
        {
            var models = new List<ModelOption>();
            if (names == null) return models;

            foreach (string name in names)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                models.Add(new ModelOption { Id = name.Trim(), Name = name.Trim() });
            }

            return models;
        }

        /// <summary>Ensures the Reasonix list exists, seeding it when an older settings file has none.</summary>
        private void EnsureReasonixModelDefaults()
        {
            if (_settings == null) return;

            if (_settings.ReasonixModels == null || _settings.ReasonixModels.Count == 0)
            {
                _settings.ReasonixModels = new List<string>
                {
                    "deepseek-v4-flash", "deepseek-v4-pro", "deepseek-chat", "deepseek-reasoner"
                };
            }
        }

        #endregion

        #region Selection

        /// <summary>
        /// The model id chosen for this provider, or empty for the CLI's own default. Devin is read
        /// from its own setting so the list it has always had keeps working.
        /// </summary>
        private string GetSelectedProviderModelId(AiProvider? provider)
        {
            if (provider == null || _settings == null) return string.Empty;

            if (IsDevinProvider(provider))
            {
                return _settings.SelectedDevinModel ?? string.Empty;
            }

            if (_settings.SelectedProviderModels == null) return string.Empty;

            string model;
            return _settings.SelectedProviderModels.TryGetValue(provider.Value.ToString(), out model)
                ? (model ?? string.Empty)
                : string.Empty;
        }

        /// <summary>Records the choice. Does not save — callers batch the save with their own updates.</summary>
        private void SetSelectedProviderModelId(AiProvider provider, string modelId)
        {
            if (_settings == null) return;

            if (IsDevinProvider(provider))
            {
                _settings.SelectedDevinModel = modelId ?? string.Empty;
                return;
            }

            if (_settings.SelectedProviderModels == null)
            {
                _settings.SelectedProviderModels = new Dictionary<string, string>();
            }

            _settings.SelectedProviderModels[provider.ToString()] = modelId ?? string.Empty;
        }

        /// <summary>
        /// Clears a selection the CLI no longer offers, so the next launch falls back to the agent's
        /// default rather than to a model it will reject.
        /// </summary>
        private void DropSelectionMissingFromCatalog(AiProvider provider, List<ModelOption> models)
        {
            string selected = GetSelectedProviderModelId(provider);
            if (string.IsNullOrWhiteSpace(selected)) return;

            foreach (ModelOption model in models)
            {
                if (string.Equals(model.Id, selected, StringComparison.OrdinalIgnoreCase)) return;
            }

            Debug.WriteLine($"Model catalog: {provider} no longer offers '{selected}'; falling back to its default.");
            SetSelectedProviderModelId(provider, string.Empty);
        }

        /// <summary>
        /// The caption for the selected model: the CLI's own display name when the catalog has one
        /// ("GPT-5.6-Sol" rather than "gpt-5.6-sol"), otherwise the id.
        /// </summary>
        private string GetSelectedProviderModelLabel(AiProvider? provider)
        {
            string selected = GetSelectedProviderModelId(provider);
            if (string.IsNullOrWhiteSpace(selected) || provider == null) return string.Empty;

            foreach (ModelOption model in GetCachedProviderModels(provider.Value))
            {
                if (string.Equals(model.Id, selected, StringComparison.OrdinalIgnoreCase))
                {
                    return model.DisplayName;
                }
            }

            return selected;
        }

        #endregion

        #region Applying The Selection

        /// <summary>
        /// The launch flag that starts the agent on the selected model, or an empty string when
        /// nothing is selected. Only for the CLIs that accept a model on their interactive command
        /// line — Devin and Reasonix are switched with a slash command inside their own TUI instead.
        /// </summary>
        private string GetModelLaunchFlag(AiProvider provider)
        {
            string model = GetSelectedProviderModelId(provider);
            if (string.IsNullOrWhiteSpace(model)) return string.Empty;

            switch (provider)
            {
                case AiProvider.Codex:
                case AiProvider.CodexNative:
                case AiProvider.OpenCode:
                    return " -m " + QuoteModelArgument(model);

                case AiProvider.CursorAgent:
                case AiProvider.CursorAgentNative:
                case AiProvider.Pi:
                case AiProvider.Antigravity:
                    return " --model " + QuoteModelArgument(model);

                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// The slash command that switches the model of a running TUI, or null when the agent has
        /// none. Devin takes a quoted display name; Reasonix takes a bare id.
        /// </summary>
        private string GetLiveModelSwitchCommand(AiProvider provider, string modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId)) return null;

            if (IsDevinProvider(provider)) return "/model \"" + modelId + "\"";
            if (provider == AiProvider.Reasonix) return "/model " + modelId;

            return null;
        }

        private static string QuoteModelArgument(string model)
        {
            return model.IndexOf(' ') >= 0 ? "\"" + model + "\"" : model;
        }

        #endregion
    }
}
