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
    }
}
