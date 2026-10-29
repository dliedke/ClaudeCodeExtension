/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for the question card answer: selected options combined with the free-text "Other" box
 *
 * *******************************************************************************************************************/

using System.Collections.Generic;
using ClaudeCodeVS.Agents;
using ClaudeCodeVS.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class QuestionAnswerTests
    {
        private static ChatQuestionViewModel Question(bool multiSelect, params string[] labels)
        {
            var vm = new ChatQuestionViewModel { Question = "Q?", MultiSelect = multiSelect };
            foreach (string label in labels)
            {
                vm.Options.Add(new ChatOptionViewModel { Label = label });
            }
            return vm;
        }

        [TestMethod]
        public void JoinWithAndListsLikeASentence()
        {
            Assert.AreEqual("A", ChatQuestionViewModel.JoinWithAnd(new[] { "A" }));
            Assert.AreEqual("A and B", ChatQuestionViewModel.JoinWithAnd(new[] { "A", "B" }));
            Assert.AreEqual("A, B and C", ChatQuestionViewModel.JoinWithAnd(new[] { "A", "B", "C" }));
        }

        [TestMethod]
        public void PlaceholderNamesTheSelectedOptions()
        {
            ChatQuestionViewModel vm = Question(true, "Answer1", "Answer2", "Answer3");
            Assert.AreEqual("Other — type your own answer…", vm.OtherPlaceholder);

            vm.Select(vm.Options[0]);
            Assert.AreEqual("Type additional info to Answer1…", vm.OtherPlaceholder);

            vm.Select(vm.Options[2]);
            Assert.AreEqual("Type additional info to Answer1 and Answer3…", vm.OtherPlaceholder);
        }

        [TestMethod]
        public void SelectedOptionAndOtherTextAreSentTogether()
        {
            ChatQuestionViewModel vm = Question(false, "Answer1", "Answer2");
            vm.Select(vm.Options[1]);
            vm.OtherAnswer = "  but keep it short ";

            Assert.AreEqual("Answer2 — additional info: but keep it short", vm.BuildAnswer());
        }

        [TestMethod]
        public void EitherPartAloneIsSentAsIs()
        {
            ChatQuestionViewModel vm = Question(true, "Answer1", "Answer2");
            Assert.IsNull(vm.BuildAnswer());

            vm.OtherAnswer = "my own";
            Assert.AreEqual("my own", vm.BuildAnswer());

            vm.OtherAnswer = string.Empty;
            vm.Select(vm.Options[0]);
            vm.Select(vm.Options[1]);
            Assert.AreEqual("Answer1, Answer2", vm.BuildAnswer());
        }

        [TestMethod]
        public void CapturedAnswerKeepsChoiceAndNoteApart()
        {
            ChatQuestionViewModel vm = Question(false, "Answer1", "Answer2");
            vm.Select(vm.Options[1]);
            vm.OtherAnswer = "  but keep it short ";
            vm.CaptureAnswer();

            Assert.AreEqual("Answer2", vm.AnsweredChoice);
            Assert.AreEqual("but keep it short", vm.AnsweredNote);
            Assert.IsTrue(vm.HasAnsweredNote);
            Assert.IsFalse(vm.WasLeftBlank);
        }

        [TestMethod]
        public void CapturedFreeTextAloneIsTheChoice()
        {
            ChatQuestionViewModel vm = Question(true, "Answer1", "Answer2");
            vm.OtherAnswer = "my own";
            vm.CaptureAnswer();

            Assert.AreEqual("my own", vm.AnsweredChoice);
            Assert.IsFalse(vm.HasAnsweredNote);
            Assert.IsFalse(vm.WasLeftBlank);
        }

        [TestMethod]
        public void CapturedBlankQuestionSaysNoAnswer()
        {
            ChatQuestionViewModel vm = Question(false, "Answer1");
            vm.CaptureAnswer();

            Assert.AreEqual("No answer", vm.AnsweredChoice);
            Assert.IsFalse(vm.HasAnsweredNote);
            Assert.IsTrue(vm.WasLeftBlank);
        }

        private static ChatInteractionViewModel QuestionCard(int questionCount, string header = "")
        {
            var questions = new List<AgentQuestion>();
            for (int i = 0; i < questionCount; i++)
            {
                questions.Add(new AgentQuestion
                {
                    Question = "Q" + i + "?",
                    Header = header,
                    Options = new[] { new AgentQuestionOption { Label = "Yes" } }
                });
            }

            return new ChatInteractionViewModel(new AgentInteractionRequest((allow, reason, answers) => { })
            {
                Kind = AgentInteractionKind.Question,
                Questions = questions
            });
        }

        [TestMethod]
        public void ALoneQuestionsBadgeMovesBesideTheTitle()
        {
            ChatInteractionViewModel single = QuestionCard(1, "Fix kick");
            Assert.AreEqual("Fix kick", single.TitleBadge);
            Assert.IsTrue(single.HasTitleBadge);
            Assert.IsFalse(single.Questions[0].ShowHeaderBadge, "Shown once, in the title line.");

            ChatInteractionViewModel several = QuestionCard(2, "Fix kick");
            Assert.IsFalse(several.HasTitleBadge);
            Assert.IsTrue(several.Questions[0].ShowHeaderBadge, "Each question keeps its own badge.");

            ChatInteractionViewModel noHeader = QuestionCard(1);
            Assert.IsFalse(noHeader.HasTitleBadge);
            Assert.IsFalse(noHeader.Questions[0].ShowHeaderBadge);
        }

        [TestMethod]
        public void TitleNamesTheQuestionsOnceAnswered()
        {
            ChatInteractionViewModel single = QuestionCard(1);
            Assert.AreEqual("The agent needs your input", single.Title);
            single.Questions[0].Select(single.Questions[0].Options[0]);
            single.Submit();
            Assert.AreEqual("Question", single.Title);

            ChatInteractionViewModel several = QuestionCard(2);
            several.Questions[0].Select(several.Questions[0].Options[0]);
            several.Submit();
            Assert.AreEqual("Questions", several.Title);

            ChatInteractionViewModel skipped = QuestionCard(1);
            skipped.Reject();
            Assert.AreEqual("Question skipped", skipped.Title);
        }

        [TestMethod]
        public void RecommendedSuffixIsTranslatedForDisplayOnly()
        {
            try
            {
                Loc.SetLanguageFromJson("it", "{ \"(Recommended)\": \"(Scelta consigliata)\" }");
                ChatQuestionViewModel vm = Question(false, "Chiusura (Recommended)", "Apertura");

                Assert.AreEqual("Chiusura (Scelta consigliata)", vm.Options[0].DisplayLabel);
                Assert.AreEqual("Apertura", vm.Options[1].DisplayLabel);
                Assert.AreEqual("Chiusura (Scelta consigliata)", ChatOptionViewModel.LocalizeLabel("Chiusura (recommended) "));

                vm.Select(vm.Options[0]);
                Assert.AreEqual("Chiusura (Recommended)", vm.BuildAnswer(), "The agent must get its own label back.");
            }
            finally
            {
                Loc.SetLanguageFromJson(Loc.EnglishLanguage, null);
            }
        }
    }
}
