/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Unit tests for the Devin account usage client (credentials parsing, response mapping, report text, HTTP errors)
 *
 * *******************************************************************************************************************/

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVS.Agents;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeExtension.Tests
{
    [TestClass]
    public class DevinUsageTests
    {
        private const string Credentials =
            "windsurf_api_key = \"sk-test-key\"\r\n" +
            "api_server_url = \"https://server.codeium.com\"\r\n" +
            "devin_webapp_host = \"https://app.devin.ai\"\r\n";

        // Shape measured against the live service for an Enterprise ACU plan: no daily/weekly quota,
        // acuConsumed as a double, planInfo at the top level next to userStatus.
        private const string EnterpriseResponse =
            "{\"userStatus\":{\"planStatus\":{\"acuConsumed\":10.7434985,\"acuLimit\":150,\"availablePromptCredits\":-1}}," +
            "\"planInfo\":{\"planName\":\"Cognition Platform (Enterprise)\",\"billingStrategy\":\"BILLING_STRATEGY_ACU\"}}";

        [TestMethod]
        public void ParsesTheKeyAndServerOutOfTheCredentialsFile()
        {
            string key;
            Uri server;

            Assert.IsTrue(DevinUsageClient.TryParseCredentials(Credentials, out key, out server));
            Assert.AreEqual("sk-test-key", key);
            Assert.AreEqual("https://server.codeium.com/", server.AbsoluteUri);
        }

        [TestMethod]
        public void RefusesAServerThatIsNotHttps()
        {
            string key;
            Uri server;
            string toml = "windsurf_api_key = \"k\"\napi_server_url = \"http://server.codeium.com\"\n";

            Assert.IsFalse(DevinUsageClient.TryParseCredentials(toml, out key, out server));
            Assert.IsNull(server);
        }

        [TestMethod]
        public void RefusesCredentialsMissingTheKeyOrTheServer()
        {
            string key;
            Uri server;

            Assert.IsFalse(DevinUsageClient.TryParseCredentials("api_server_url = \"https://x.example\"", out key, out server));
            Assert.IsFalse(DevinUsageClient.TryParseCredentials("windsurf_api_key = \"k\"", out key, out server));
            Assert.IsFalse(DevinUsageClient.TryParseCredentials("", out key, out server));
            Assert.IsFalse(DevinUsageClient.TryParseCredentials(null, out key, out server));
        }

        [TestMethod]
        public void ExtractsTheVersionFromTheCliBanner()
        {
            Assert.AreEqual("3000.11.3", DevinUsageClient.ExtractVersion("devin 3000.11.3 (9c803229faa4)"));
            Assert.IsNull(DevinUsageClient.ExtractVersion("no version here"));
            Assert.IsNull(DevinUsageClient.ExtractVersion(null));
        }

        [TestMethod]
        public void MapsAnEnterpriseAcuResponse()
        {
            DevinUsageInfo info = DevinUsageClient.ParseUserStatus(EnterpriseResponse);

            Assert.AreEqual("Cognition Platform (Enterprise)", info.PlanName);
            Assert.AreEqual(10.7434985, info.AcuConsumed.Value, 1e-9);
            Assert.AreEqual(150, info.AcuLimit.Value, 1e-9);
            Assert.IsNull(info.DailyRemainingPercent);
            Assert.IsNull(info.WeeklyRemainingPercent);
            Assert.IsTrue(info.HasUsageData);
        }

        [TestMethod]
        public void ReadsNumbersThatArriveAsStrings()
        {
            DevinUsageInfo info = DevinUsageClient.ParseUserStatus(
                "{\"userStatus\":{\"planStatus\":{\"dailyQuotaRemainingPercent\":\"40\",\"dailyQuotaResetAtUnix\":\"1790000000\"}}}");

            Assert.AreEqual(40, info.DailyRemainingPercent.Value, 1e-9);
            Assert.AreEqual(1790000000, info.DailyResetUnix.Value, 1e-9);
        }

        [TestMethod]
        public void RejectsAResponseWithoutPlanStatus()
        {
            Assert.ThrowsException<InvalidOperationException>(() => DevinUsageClient.ParseUserStatus("{\"userStatus\":{}}"));
            Assert.ThrowsException<InvalidOperationException>(() => DevinUsageClient.ParseUserStatus("not json"));
        }

        [TestMethod]
        public void FormatsTheCycleLineLikeTheCli()
        {
            string report = DevinUsageClient.FormatReport(
                DevinUsageClient.ParseUserStatus(EnterpriseResponse), DateTime.UtcNow);

            Assert.AreEqual(
                "📊 Devin usage — Cognition Platform (Enterprise)\nCycle: 10.74 of 150.00 ACUs · 7% used",
                report);
        }

        [TestMethod]
        public void FormatsQuotaLinesWithTimeToReset()
        {
            DateTime now = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
            long reset = new DateTimeOffset(now.AddHours(5).AddMinutes(12)).ToUnixTimeSeconds();
            var info = new DevinUsageInfo { DailyRemainingPercent = 80, DailyResetUnix = reset, WeeklyRemainingPercent = 12.5 };

            string report = DevinUsageClient.FormatReport(info, now);

            StringAssert.Contains(report, "Daily: 80% remaining, resets in 5h12m");
            StringAssert.Contains(report, "Weekly: 12.5% remaining");
            Assert.IsFalse(report.Contains("Cycle"));
        }

        [TestMethod]
        public void SaysSoWhenTheAccountReportsNothing()
        {
            string report = DevinUsageClient.FormatReport(new DevinUsageInfo(), DateTime.UtcNow);

            StringAssert.Contains(report, "did not report any usage figures");
        }

        [TestMethod]
        public void FormatsResetsAcrossDaysHoursAndMinutes()
        {
            DateTime now = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
            Func<TimeSpan, string> fmt = delta =>
                DevinUsageClient.FormatReset(new DateTimeOffset(now + delta).ToUnixTimeSeconds(), now);

            Assert.AreEqual("2d3h", fmt(TimeSpan.FromHours(51)));
            Assert.AreEqual("1h5m", fmt(TimeSpan.FromMinutes(65)));
            Assert.AreEqual("40m", fmt(TimeSpan.FromMinutes(40)));
            Assert.AreEqual("0m", fmt(TimeSpan.FromMinutes(-5)));
            Assert.AreEqual(string.Empty, DevinUsageClient.FormatReset(null, now));
        }

        [TestMethod]
        public async Task SendsTheKeyAndNumericVersionToTheUserStatusEndpointAsync()
        {
            var handler = new StubHandler(HttpStatusCode.OK, EnterpriseResponse);

            DevinUsageInfo info = await DevinUsageClient.FetchAsync(
                Credentials, "devin 3000.11.3 (abc)", CancellationToken.None, handler);

            Assert.AreEqual(10.7434985, info.AcuConsumed.Value, 1e-9);
            Assert.AreEqual(
                "https://server.codeium.com/exa.seat_management_pb.SeatManagementService/GetUserStatus",
                handler.RequestUri);
            StringAssert.Contains(handler.Body, "\"apiKey\":\"sk-test-key\"");
            StringAssert.Contains(handler.Body, "\"ideVersion\":\"3000.11.3\"");
        }

        [TestMethod]
        public async Task FallsBackToANumericVersionWhenTheCliReportsNoneAsync()
        {
            var handler = new StubHandler(HttpStatusCode.OK, EnterpriseResponse);

            await DevinUsageClient.FetchAsync(Credentials, null, CancellationToken.None, handler);

            // The service answers 400/500 to an empty or non-numeric version.
            StringAssert.Contains(handler.Body, "\"ideVersion\":\"" + DevinUsageClient.FallbackCliVersion + "\"");
        }

        [TestMethod]
        public async Task ExplainsARejectedLoginAsync()
        {
            var handler = new StubHandler(HttpStatusCode.Unauthorized, "{}");

            InvalidOperationException error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => DevinUsageClient.FetchAsync(Credentials, "3000.11.3", CancellationToken.None, handler));

            StringAssert.Contains(error.Message, "Sign in with Devin again");
        }

        [TestMethod]
        public async Task ReportsOtherServerErrorsByStatusWithoutLeakingTheKeyAsync()
        {
            var handler = new StubHandler(HttpStatusCode.InternalServerError, "{}");

            InvalidOperationException error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => DevinUsageClient.FetchAsync(Credentials, "3000.11.3", CancellationToken.None, handler));

            StringAssert.Contains(error.Message, "HTTP 500");
            Assert.IsFalse(error.Message.Contains("sk-test-key"));
        }

        [TestMethod]
        public async Task RejectsUnreadableCredentialsBeforeAnyRequestAsync()
        {
            var handler = new StubHandler(HttpStatusCode.OK, EnterpriseResponse);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => DevinUsageClient.FetchAsync("garbage", "3000.11.3", CancellationToken.None, handler));

            Assert.IsNull(handler.RequestUri);
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            private readonly string _content;

            public StubHandler(HttpStatusCode status, string content)
            {
                _status = status;
                _content = content;
            }

            public string RequestUri { get; private set; }

            public string Body { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestUri = request.RequestUri.AbsoluteUri;
                Body = request.Content == null ? null : await request.Content.ReadAsStringAsync().ConfigureAwait(false);
                return new HttpResponseMessage(_status) { Content = new StringContent(_content) };
            }
        }
    }
}
