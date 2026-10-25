/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: View models for the interactive cards (questions, plan approval, tool approval) in the native chat
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using ClaudeCodeVS.Agents;
using static ClaudeCodeVS.UI.Loc;

namespace ClaudeCodeVS.UI
{
    /// <summary>
    /// One selectable answer. Toggling <see cref="IsSelected"/> is what the option buttons bind to.
    /// </summary>
    public class ChatOptionViewModel : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string Label { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        /// <summary>True when the description is worth showing under the label.</summary>
        public bool HasDescription
        {
            get { return !string.IsNullOrWhiteSpace(Description); }
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                Raise(nameof(IsSelected));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// One question card: a header chip, the question, its options and a free-text "Other" box.
    /// </summary>
    public class ChatQuestionViewModel : INotifyPropertyChanged
    {
        private string _otherAnswer = string.Empty;

        public ChatQuestionViewModel()
        {
            Options = new ObservableCollection<ChatOptionViewModel>();
        }

        /// <summary>The exact question text. Echoed back as the answers-map key, so never reworded.</summary>
        public string Question { get; set; } = string.Empty;

        public string Header { get; set; } = string.Empty;

        public bool HasHeader
        {
            get { return !string.IsNullOrWhiteSpace(Header); }
        }

        public bool MultiSelect { get; set; }

        public ObservableCollection<ChatOptionViewModel> Options { get; }

        /// <summary>
        /// Free-text answer. Alone it is the answer; with options selected it is sent after them as
        /// additional info.
        /// </summary>
        public string OtherAnswer
        {
            get { return _otherAnswer; }
            set
            {
                string next = value ?? string.Empty;
                if (_otherAnswer == next) return;
                _otherAnswer = next;
                Raise(nameof(OtherAnswer));
                Raise(nameof(IsAnswered));
            }
        }

        public bool IsAnswered
        {
            get
            {
                return !string.IsNullOrWhiteSpace(_otherAnswer) || Options.Any(o => o.IsSelected);
            }
        }

        /// <summary>
        /// Placeholder of the free-text box: an "Other" answer while nothing is selected, additional
        /// info to the selected options once something is.
        /// </summary>
        public string OtherPlaceholder
        {
            get
            {
                string[] selected = SelectedLabels();
                return selected.Length == 0
                    ? L("Other — type your own answer…")
                    : L("Type additional info to {0}…", JoinWithAnd(selected));
            }
        }

        /// <summary>Joins labels as "A", "A and B" or "A, B and C".</summary>
        internal static string JoinWithAnd(IList<string> items)
        {
            if (items == null || items.Count == 0) return string.Empty;
            if (items.Count == 1) return items[0];

            return L("{0} and {1}", string.Join(", ", items.Take(items.Count - 1)), items[items.Count - 1]);
        }

        private string[] SelectedLabels()
        {
            return Options.Where(o => o.IsSelected).Select(o => o.Label).ToArray();
        }

        /// <summary>
        /// Selects one option. Single-select questions clear the others, and clicking the selected one
        /// again clears it — the same behaviour as the arrow-key picker in the CLI.
        /// </summary>
        public void Select(ChatOptionViewModel option)
        {
            if (option == null) return;

            if (MultiSelect)
            {
                option.IsSelected = !option.IsSelected;
            }
            else
            {
                bool turningOn = !option.IsSelected;
                foreach (ChatOptionViewModel candidate in Options)
                {
                    candidate.IsSelected = false;
                }
                option.IsSelected = turningOn;
            }

            Raise(nameof(IsAnswered));
            Raise(nameof(OtherPlaceholder));
        }

        /// <summary>
        /// The answer to send, or null when the question was left blank. Selected options and the
        /// free text are sent together when both are filled in.
        /// </summary>
        public string BuildAnswer()
        {
            string other = string.IsNullOrWhiteSpace(_otherAnswer) ? null : _otherAnswer.Trim();
            string[] selected = SelectedLabels();

            if (selected.Length == 0)
            {
                return other;
            }

            string chosen = string.Join(", ", selected);
            return other == null ? chosen : chosen + " — additional info: " + other;
        }

        /// <summary>Re-reads the translated placeholder after a chat language switch.</summary>
        internal void RefreshLanguage()
        {
            Raise(nameof(OtherPlaceholder));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// A transcript row the user has to act on: a set of questions, a plan awaiting approval, or a tool
    /// call awaiting a yes.
    /// <para>
    /// The row stays in the transcript after it is answered — it collapses to a one-line summary rather
    /// than vanishing, so scrolling back still shows what was decided.
    /// </para>
    /// </summary>
    public class ChatInteractionViewModel : ChatMessageViewModel
    {
        private readonly AgentInteractionRequest _request;
        private bool _isPending = true;
        private string _outcome = string.Empty;
        private string _planFeedback = string.Empty;

        public ChatInteractionViewModel(AgentInteractionRequest request)
            : base(ChatMessageKind.Interaction)
        {
            _request = request ?? throw new ArgumentNullException(nameof(request));

            Questions = new ObservableCollection<ChatQuestionViewModel>();

            if (request.Questions != null)
            {
                foreach (AgentQuestion question in request.Questions)
                {
                    var vm = new ChatQuestionViewModel
                    {
                        Question = question.Question,
                        Header = question.Header,
                        MultiSelect = question.MultiSelect
                    };

                    if (question.Options != null)
                    {
                        foreach (AgentQuestionOption option in question.Options)
                        {
                            vm.Options.Add(new ChatOptionViewModel
                            {
                                Label = option.Label,
                                Description = option.Description
                            });
                        }
                    }

                    Questions.Add(vm);
                }
            }

            ToolName = request.ToolName ?? string.Empty;
            ToolInputJson = request.ToolInputJson ?? string.Empty;

            // Same presenter the post-approval tool-call row uses, so an Edit/Write approval card shows
            // the colored diff up front instead of raw JSON that only turns into a diff after the click
            // (issue #160). Diff/HasDiff/ShowRawInput are inherited from the base tool-call row.
            ChatToolPresentation presentation = ChatToolPresenter.Describe(ToolName, ToolInputJson);
            SetDiff(presentation.Diff);
            ToolDescription = presentation.Description;
        }

        /// <summary>
        /// What the tool call says it is for (its <c>description</c> argument), shown in the approval
        /// card's title so "Allow PowerShell?" also says what the command will do. Empty when absent.
        /// </summary>
        public string ToolDescription { get; }

        /// <summary>Which of the three card shapes this row is. Distinct from the row kind in the base class.</summary>
        public AgentInteractionKind InteractionKind
        {
            get { return _request.Kind; }
        }

        public bool IsQuestion { get { return InteractionKind == AgentInteractionKind.Question; } }

        public bool IsPlanReview { get { return InteractionKind == AgentInteractionKind.PlanReview; } }

        public bool IsToolApproval { get { return InteractionKind == AgentInteractionKind.ToolApproval; } }

        /// <summary>
        /// True when this approval card can also offer "Allow for the rest of this session" — the
        /// session marks the request eligible and supplies the callback that remembers the tool.
        /// </summary>
        public bool CanAllowForSession
        {
            get { return IsToolApproval && _request.OnAllowForSession != null; }
        }

        /// <summary>
        /// True when this plan card can also be approved straight into skip-permissions (issue #163) —
        /// the session marks the request eligible and supplies the callback that keeps its own state.
        /// </summary>
        public bool CanApproveAndSkipPermissions
        {
            get { return IsPlanReview && _request.CanApproveAndSkipPermissions; }
        }

        public string AllowForSessionCaption
        {
            get
            {
                return string.IsNullOrEmpty(ToolName)
                    ? L("Allow for the rest of this session")
                    : L("Allow {0} for the rest of this session", ToolName);
            }
        }

        public ObservableCollection<ChatQuestionViewModel> Questions { get; }

        public string PlanText { get { return _request.PlanText; } }

        /// <summary>
        /// Title line, e.g. <c>Allow Bash - List changed files?</c> (the part after the dash is the tool
        /// call's description, when it sent one) or <c>Plan ready for review</c>.
        /// </summary>
        public string Title
        {
            get
            {
                switch (InteractionKind)
                {
                    case AgentInteractionKind.PlanReview: return L("Plan ready for review");
                    case AgentInteractionKind.Question: return L("The agent needs your input");
                    default:
                        string suffix = string.IsNullOrEmpty(ToolDescription) ? string.Empty : " - " + ToolDescription;

                        return string.IsNullOrEmpty(ToolName)
                            ? L("The agent needs permission") + suffix
                            : L("Allow {0}?", ToolName + suffix);
                }
            }
        }

        /// <summary>Caption of the accept button, which differs per card shape.</summary>
        public string SubmitCaption
        {
            get
            {
                switch (InteractionKind)
                {
                    case AgentInteractionKind.PlanReview: return L("Approve plan");
                    case AgentInteractionKind.Question: return L("Submit");
                    default: return L("Allow");
                }
            }
        }

        /// <summary>
        /// What the user wants changed in the plan, typed in the plan card (issue #221). Sent with
        /// "Keep planning" so the agent revises the plan instead of asking what to change.
        /// </summary>
        public string PlanFeedback
        {
            get { return _planFeedback; }
            set
            {
                if (_planFeedback == value) return;
                _planFeedback = value ?? string.Empty;
                RaisePropertyChanged(nameof(PlanFeedback));
                RaisePropertyChanged(nameof(RejectCaption));
            }
        }

        public string RejectCaption
        {
            get
            {
                switch (InteractionKind)
                {
                    case AgentInteractionKind.PlanReview:
                        return string.IsNullOrWhiteSpace(_planFeedback) ? L("Keep planning") : L("Send feedback");
                    case AgentInteractionKind.Question: return L("Skip");
                    default: return L("Deny");
                }
            }
        }

        /// <summary>False once answered; the card then shows <see cref="Outcome"/> instead of controls.</summary>
        public bool IsPending
        {
            get { return _isPending; }
            private set
            {
                if (_isPending == value) return;
                _isPending = value;
                RaisePropertyChanged(nameof(IsPending));
                RaisePropertyChanged(nameof(IsResolved));
            }
        }

        public bool IsResolved { get { return !_isPending; } }

        /// <summary>
        /// Re-reads the translated captions after a chat language switch. For a pending card: an answered
        /// one only shows its <see cref="Outcome"/>, which stays as it was written.
        /// </summary>
        public void RefreshLanguage()
        {
            RaisePropertyChanged(nameof(Title));
            RaisePropertyChanged(nameof(SubmitCaption));
            RaisePropertyChanged(nameof(RejectCaption));
            RaisePropertyChanged(nameof(AllowForSessionCaption));

            foreach (ChatQuestionViewModel question in Questions)
            {
                question.RefreshLanguage();
            }
        }

        /// <summary>True when the card was accepted rather than declined or abandoned.</summary>
        public bool WasAccepted { get; private set; }

        /// <summary>True when the plan was approved with the switch to skip-permissions.</summary>
        public bool WasApprovedAndSkippedPermissions { get; private set; }

        /// <summary>The model the plan was approved with, or null when the approval left the model alone.</summary>
        public ClaudeModel? ApprovedModel { get; private set; }

        /// <summary>What was decided, shown after the card is answered.</summary>
        public string Outcome
        {
            get { return _outcome; }
            private set
            {
                if (_outcome == value) return;
                _outcome = value ?? string.Empty;
                RaisePropertyChanged(nameof(Outcome));
            }
        }

        /// <summary>
        /// Answers the request. Questions send their answers map; plan and tool cards just allow.
        /// </summary>
        public void Submit()
        {
            if (!IsPending) return;

            if (IsQuestion)
            {
                var answers = new Dictionary<string, string>();
                foreach (ChatQuestionViewModel question in Questions)
                {
                    string answer = question.BuildAnswer();
                    if (!string.IsNullOrEmpty(answer))
                    {
                        answers[question.Question] = answer;
                    }
                }

                if (answers.Count == 0)
                {
                    // Nothing picked: leave the card open rather than telling the agent the user
                    // answered nothing, which just wastes a turn.
                    return;
                }

                _request.Allow(answers);
                Outcome = L("Answered: {0}", string.Join(" · ", answers.Values));
            }
            else
            {
                _request.Allow(null);
                Outcome = IsPlanReview ? L("Plan approved — proceeding.") : L("Allowed.");
            }

            WasAccepted = true;
            IsPending = false;
        }

        /// <summary>
        /// Allows this call and tells the session not to ask again for the same tool this run. Only
        /// meaningful when <see cref="CanAllowForSession"/> is true.
        /// </summary>
        public void AllowForSession()
        {
            if (!IsPending) return;

            _request.AllowForSession();
            Outcome = string.IsNullOrEmpty(ToolName)
                ? L("Allowed — won't ask again this session.")
                : L("Allowed — won't ask again for {0} this session.", ToolName);
            WasAccepted = true;
            IsPending = false;
        }

        /// <summary>
        /// Approves the plan, optionally carrying on without permission prompts (issue #163) and/or on a
        /// different model — the common "plan with Opus, build with Sonnet" flow. The card only records
        /// what was chosen; the control applies the model switch to the live session once it resolves.
        /// </summary>
        public void ApprovePlan(bool skipPermissions, ClaudeModel? model)
        {
            if (!IsPending || !IsPlanReview) return;

            bool skipping = skipPermissions && CanApproveAndSkipPermissions;

            if (skipping)
            {
                _request.AllowAndSkipPermissions();
            }
            else
            {
                _request.Allow(null);
            }

            var details = new List<string>();
            if (skipping) details.Add(L("without permission prompts"));
            if (model.HasValue) details.Add(L("on {0}", model.Value));

            Outcome = details.Count == 0
                ? L("Plan approved — proceeding.")
                : L("Plan approved — proceeding {0}.", string.Join(" ", details));

            WasAccepted = true;
            WasApprovedAndSkippedPermissions = skipping;
            ApprovedModel = model;
            IsPending = false;
        }

        public void Reject()
        {
            if (!IsPending) return;

            string feedback = IsPlanReview ? (_planFeedback ?? string.Empty).Trim() : string.Empty;

            _request.Deny(IsPlanReview
                ? BuildPlanRejectionMessage(feedback)
                : "The user declined this action.");

            Outcome = !IsPlanReview ? L("Declined.")
                : feedback.Length == 0 ? L("Plan rejected.")
                : L("Plan rejected — asked to change: ") + feedback;
            IsPending = false;
        }

        /// <summary>
        /// The reason sent back to the agent when the plan is rejected: with the user's feedback it asks
        /// for a revised plan, without it the agent asks what to change before doing any work.
        /// </summary>
        internal static string BuildPlanRejectionMessage(string feedback)
        {
            return string.IsNullOrWhiteSpace(feedback)
                ? "The user rejected the plan. Ask what to change before doing any work."
                : "The user rejected the plan and wants these changes:\n" + feedback.Trim() +
                  "\n\nRevise the plan accordingly and present it again for approval before doing any work.";
        }

        /// <summary>
        /// Closes the card without answering, for when the session is torn down under it. The session
        /// itself denies the underlying request; this only updates what the transcript shows.
        /// </summary>
        public void Abandon()
        {
            if (!IsPending) return;

            Outcome = L("Not answered — the session ended.");
            IsPending = false;
        }
    }
}
