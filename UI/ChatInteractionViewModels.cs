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

        /// <summary>Free-text answer, which wins over any selected option when it has content.</summary>
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
        }

        /// <summary>The answer to send, or null when the question was left blank.</summary>
        public string BuildAnswer()
        {
            if (!string.IsNullOrWhiteSpace(_otherAnswer))
            {
                return _otherAnswer.Trim();
            }

            string[] selected = Options.Where(o => o.IsSelected).Select(o => o.Label).ToArray();
            if (selected.Length == 0)
            {
                return null;
            }

            return string.Join(", ", selected);
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
        }

        /// <summary>Which of the three card shapes this row is. Distinct from the row kind in the base class.</summary>
        public AgentInteractionKind InteractionKind
        {
            get { return _request.Kind; }
        }

        public bool IsQuestion { get { return InteractionKind == AgentInteractionKind.Question; } }

        public bool IsPlanReview { get { return InteractionKind == AgentInteractionKind.PlanReview; } }

        public bool IsToolApproval { get { return InteractionKind == AgentInteractionKind.ToolApproval; } }

        public ObservableCollection<ChatQuestionViewModel> Questions { get; }

        public string PlanText { get { return _request.PlanText; } }

        /// <summary>Title line, e.g. <c>Allow Bash?</c> or <c>Plan ready for review</c>.</summary>
        public string Title
        {
            get
            {
                switch (InteractionKind)
                {
                    case AgentInteractionKind.PlanReview: return "Plan ready for review";
                    case AgentInteractionKind.Question: return "The agent needs your input";
                    default:
                        return string.IsNullOrEmpty(ToolName)
                            ? "The agent needs permission"
                            : "Allow " + ToolName + "?";
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
                    case AgentInteractionKind.PlanReview: return "Approve plan";
                    case AgentInteractionKind.Question: return "Submit";
                    default: return "Allow";
                }
            }
        }

        public string RejectCaption
        {
            get
            {
                switch (InteractionKind)
                {
                    case AgentInteractionKind.PlanReview: return "Keep planning";
                    case AgentInteractionKind.Question: return "Skip";
                    default: return "Deny";
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

        /// <summary>True when the card was accepted rather than declined or abandoned.</summary>
        public bool WasAccepted { get; private set; }

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
                Outcome = "Answered: " + string.Join(" · ", answers.Values);
            }
            else
            {
                _request.Allow(null);
                Outcome = IsPlanReview ? "Plan approved — proceeding." : "Allowed.";
            }

            WasAccepted = true;
            IsPending = false;
        }

        public void Reject()
        {
            if (!IsPending) return;

            _request.Deny(IsPlanReview
                ? "The user rejected the plan. Ask what to change before doing any work."
                : "The user declined this action.");

            Outcome = IsPlanReview ? "Plan rejected." : "Declined.";
            IsPending = false;
        }

        /// <summary>
        /// Closes the card without answering, for when the session is torn down under it. The session
        /// itself denies the underlying request; this only updates what the transcript shows.
        /// </summary>
        public void Abandon()
        {
            if (!IsPending) return;

            Outcome = "Not answered — the session ended.";
            IsPending = false;
        }
    }
}
