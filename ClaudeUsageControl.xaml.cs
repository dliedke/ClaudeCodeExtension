/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: WebView2-backed control that hosts claude.ai/settings/usage with a thin toolbar
 *          (refresh, auto-refresh, open-in-browser, sign-out) and broadcasts scraped usage
 *          data so the inline bars in the main panel can stay in sync.
 *
 * *******************************************************************************************************************/

using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ClaudeCodeVS
{
    /// <summary>
    /// User control that embeds claude.ai/settings/usage in a WebView2.
    /// Trims everything outside the "plan usage limits" section via injected
    /// JS so the user only sees the relevant bars. Also posts the scraped
    /// values to <see cref="UsageDataReceived"/> for the inline mini-bars.
    /// </summary>
    public partial class ClaudeUsageControl : UserControl
    {
        public const string UsageUrl = "https://claude.ai/settings/usage";
        public const string WebView2DownloadUrl = "https://developer.microsoft.com/en-us/microsoft-edge/webview2/";

        private static readonly string SharedCookiePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeCodeExtension", "shared_cookies.json");

        private DispatcherTimer _autoRefreshTimer;
        private bool _initialized;
        private bool _suppressComboEvent;
        private DateTime _lastRedirectAttemptUtc = DateTime.MinValue;
        private DateTime _lastCookieSaveUtc = DateTime.MinValue;

        /// <summary>
        /// Fires when a usage snapshot is successfully scraped from the page.
        /// </summary>
        public event EventHandler<UsageSnapshot> UsageDataReceived;

        /// <summary>
        /// Fires when the auto-refresh combo box value changes. Hosts persist
        /// the new value to settings.
        /// </summary>
        public event EventHandler<int> AutoRefreshChanged;

        public ClaudeUsageControl()
        {
            InitializeComponent();
            this.Loaded += OnLoaded;
        }

#pragma warning disable VSTHRD100 // async void Loaded handler is required by WPF
        private async void OnLoaded(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_initialized) return;
            _initialized = true;
            try { await InitializeWebViewAsync(); }
            catch (Exception ex) { Debug.WriteLine("ClaudeUsageControl.OnLoaded failed: " + ex); }
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                // WebView2 exclusively locks its user data folder — two VS processes sharing
                // the same folder causes the second to throw during environment creation.
                // Use a per-process folder so multiple VS instances coexist without conflict.
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClaudeCodeExtension", "WebView2_" + pid);
                Directory.CreateDirectory(userDataFolder);
                CleanupStaleWebView2Folders();

                var env = await ClaudeUsageWebViewEnvironment.GetOrCreateAsync(userDataFolder);
                await WebView.EnsureCoreWebView2Async(env);

                // Re-focus after Ctrl+Scroll zoom so WebView2 re-establishes cursor tracking.
                // Without this the mouse cursor disappears until the user clicks again.
                WebView.ZoomFactorChanged += (s, e) =>
                {
#pragma warning disable VSTHRD001, VSTHRD110
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { WebView?.Focus(); } catch { }
                    }), System.Windows.Threading.DispatcherPriority.Background);
#pragma warning restore VSTHRD001, VSTHRD110
                };

                // Import cookies saved by another VS instance so the user stays logged in.
                await LoadSharedCookiesAsync();

                WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                WebView.CoreWebView2.SourceChanged += OnSourceChanged;
                WebView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;

                await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BuildInjectedScript(trim: true));

                WebView.CoreWebView2.Navigate(UsageUrl);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ClaudeUsageControl: WebView2 init failed: " + ex);
                ShowError("WebView2 runtime is required to display the Claude usage page. " +
                          "Click below to install it, then reopen this window.");
            }
        }

        /// <summary>
        /// Builds the JS injected on every navigation. Two responsibilities:
        /// (1) trim the page so only the usage section is visible,
        /// (2) extract the usage values and post them back via webview.postMessage.
        /// Selectors rely on stable ARIA attributes rather than Tailwind class names.
        /// </summary>
        public static string BuildInjectedScript(bool trim)
        {
            string trimFlag = trim ? "true" : "false";
            // Two responsibilities: (1) trim the page so only the usage section
            // is visible, (2) extract usage values and post via webview.postMessage.
            //
            // Trim is done via CSS injection rather than mutating DOM structure.
            // The previous approach (walk up from section, hide siblings at each
            // ancestor level) breaks React re-renders: when navigation puts a
            // new tree under <body> after login, the old display:none stylings
            // don't apply but the DOM has shifted, leaving a blank page.
            // CSS selectors targeting common chrome (nav/header/sidebar) survive
            // re-renders cleanly.
            //
            // No MutationObserver: React re-renders fire it constantly and the
            // resulting postMessage flood saturates host->renderer IPC, making
            // clicks feel laggy or get dropped. Lightweight setInterval polling
            // is enough for usage numbers that change every few minutes.
            return @"
(function(){
  const TRIM = " + trimFlag + @";
  let styleInjected = false;
  let lastJson = '';
  function findSection(){
    const bar = document.querySelector('[role=\""progressbar\""][aria-valuenow]');
    if (!bar) return null;
    return bar.closest('section') || bar.parentElement;
  }
  // Drives the page-isolation strategy: instead of fighting Tailwind's
  // grid/flex/max-w cascade to expand a deeply-nested progress bar row,
  // we identify the smallest content container that holds the progress
  // bars (the `<div tabindex=\""-1\"" class=\""outline-none\"">` wrapper that
  // sits directly above `<div class=\""pb-8\""><section>...`), walk up to
  // body marking every ancestor as `data-claude-usage-path`, and mark
  // every sibling along the way as `data-claude-usage-hide`. The CSS
  // below then collapses the path elements to a plain block layout at
  // 100% width and hides the rest of the page entirely. React re-renders
  // are tolerated because tick() re-applies the data attributes on every
  // pass, so newly-mounted siblings get re-marked the next cycle.
  function findIsolationTarget(){
    let node = document.querySelector('div[tabindex=\""-1\""].outline-none');
    if (node && node.querySelector('[role=\""progressbar\""]')) return node;
    const bar = document.querySelector('[role=\""progressbar\""][aria-valuenow]');
    if (!bar) return null;
    let n = bar.parentElement;
    while (n && n !== document.body) {
      if (n.tagName === 'DIV' && n.getAttribute('tabindex') === '-1') return n;
      n = n.parentElement;
    }
    const section = bar.closest('section');
    return section ? section.parentElement : bar.parentElement;
  }
  function injectTrimStyle(){
    if (styleInjected) return;
    const style = document.createElement('style');
    style.id = '__claude_usage_trim_css__';
    style.textContent =
      // Always-hidden chrome — survives every React re-render because the
      // selectors target tag names / class fragments rather than specific
      // node identities.
      'nav, header, aside, footer { display: none !important; }' +
      '[class*=\""sidebar\""], [class*=\""Sidebar\""] { display: none !important; }' +
      '[data-testid*=\""nav\""], [data-testid*=\""sidebar\""] { display: none !important; }' +
      // Anything we've explicitly marked as hidden via data attribute.
      '[data-claude-usage-hide=\""1\""] { display: none !important; }' +
      // Body / root: full viewport, comfortable padding, no scroll lock.
      // Padding lives on <body> rather than on the path elements because
      // the path rule below sets `padding: 0 !important` to neutralize
      // Tailwind's `px-4 md:px-8 lg:px-8` etc. on intermediate ancestors.
      'html, body { max-width: none !important; width: 100% !important; min-width: 100% !important; margin: 0 !important; overflow-x: hidden !important; }' +
      'body { overflow-y: auto !important; padding: 16px 20px !important; box-sizing: border-box !important; }' +
      // Force an explicit cursor on the page. WebView2 hosted in WPF only
      // renders its own mouse cursor while the page declares one — the
      // claude.ai body class set leaves cursor at auto, which the
      // WebView2 surface translates to no-cursor-at-all until the user
      // clicks and the WebView2 control gains focus. Setting cursor
      // default on html/body ensures a visible cursor from the moment
      // the tool window opens; element-level cursor pointer etc. on
      // links/buttons (Tailwind cursor-pointer) still applies on top.
      'html, body { cursor: default !important; }' +
      // Path elements (every ancestor between body and the target wrapper):
      // collapse to a plain block at 100% width. `display: block` neutralizes
      // any grid/flex/grid-cols layout so the previously-allocated 220px
      // settings-nav column disappears once the nav is hidden.
      '[data-claude-usage-path=\""1\""] {' +
      '  display: block !important;' +
      '  width: 100% !important;' +
      '  max-width: none !important;' +
      '  min-width: 0 !important;' +
      '  margin: 0 !important;' +
      '  padding: 0 !important;' +
      '  box-sizing: border-box !important;' +
      '  grid-template-columns: unset !important;' +
      '  grid-template-rows: unset !important;' +
      '  flex: 1 1 auto !important;' +
      '  overflow: visible !important;' +
      '  height: auto !important;' +
      '  min-height: 0 !important;' +
      '}' +
      // Target wrapper: same baseline as the path so its content reaches
      // the full panel width.
      '[data-claude-usage-keep=\""1\""] {' +
      '  display: block !important;' +
      '  width: 100% !important;' +
      '  max-width: none !important;' +
      '  margin: 0 !important;' +
      '  padding: 0 !important;' +
      '  box-sizing: border-box !important;' +
      '}' +
      // Inside the kept content, strip every max-width cap (Tailwind
      // `max-w-*` arbitrary values, inline styles, etc.) and force flex
      // rows to stay on a single line so the 13rem label and the
      // `flex-1` bar column share one row instead of wrapping.
      '[data-claude-usage-keep=\""1\""] *, [data-claude-usage-keep=\""1\""] {' +
      '  max-width: none !important;' +
      '  box-sizing: border-box !important;' +
      '}' +
      '[data-claude-usage-keep=\""1\""] [class*=\""max-w\""], [data-claude-usage-keep=\""1\""] [style*=\""max-width\""] {' +
      '  max-width: none !important;' +
      '}' +
      '[data-claude-usage-keep=\""1\""] .flex, [data-claude-usage-keep=\""1\""] [class*=\""flex-row\""] {' +
      '  flex-wrap: nowrap !important;' +
      '}' +
      // Bar element fills its column; clear any residual min-width clamp
      // and fixed flex-basis the page might have stamped previously.
      '[role=\""progressbar\""] { width: 100% !important; min-width: 0 !important; flex: 1 1 auto !important; }';
    (document.head || document.documentElement).appendChild(style);
    styleInjected = true;
  }
  let scrolledOnce = false;
  function isolatePath(target){
    if (!target) return;
    target.setAttribute('data-claude-usage-keep', '1');
    let node = target;
    let depth = 0;
    while (node && node !== document.body && depth < 30) {
      const parent = node.parentElement;
      if (!parent) break;
      // Mark intermediate ancestors as `path` so the CSS collapses them
      // to `display: block` at 100% width with zero padding/margin. We
      // deliberately do NOT mark <body> itself — body keeps its own
      // padding/margin styling from the rule above so the bars and
      // labels have breathing room from the WebView2 panel edges.
      if (parent !== document.body) {
        parent.setAttribute('data-claude-usage-path', '1');
      }
      // Hide every sibling on this level except STYLE/SCRIPT and anything
      // we've already marked as part of the path or as the target. This
      // runs even when `parent === document.body` so any hidden body
      // children (Intercom widgets, notification regions, etc.) don't
      // bleed into the visible area.
      for (const sibling of parent.children) {
        if (sibling === node) continue;
        const tag = sibling.tagName;
        if (tag === 'STYLE' || tag === 'SCRIPT') continue;
        if (sibling.hasAttribute('data-claude-usage-keep')) continue;
        if (sibling.hasAttribute('data-claude-usage-path')) continue;
        sibling.setAttribute('data-claude-usage-hide', '1');
      }
      node = parent;
      depth++;
    }
  }
  function clearStaleInlineWidths(target){
    // Old builds of this script stamped inline width/min-width/flex on
    // the bar's ancestors. Those overrides survive a navigation because
    // the underlying React tree is the same instance, so on re-entry we
    // wipe them inside the kept subtree to give the new CSS a clean slate.
    if (!target) return;
    const divs = target.querySelectorAll('div');
    for (const d of divs) {
      if (!d.style) continue;
      if (d.getAttribute && d.getAttribute('role') === 'progressbar') continue;
      // Skip children of progressbar containers — they hold the inline fill width (e.g. 18%).
      if (d.closest && d.closest('[role=\""progressbar\""]')) continue;
      d.style.width = '';
      d.style.minWidth = '';
      d.style.maxWidth = '';
      d.style.flex = '';
      d.style.flexBasis = '';
    }
    const bars = target.querySelectorAll('[role=\""progressbar\""]');
    for (const bar of bars) {
      if (bar.style) {
        bar.style.width = '100%';
        bar.style.maxWidth = 'none';
        bar.style.minWidth = '0';
        bar.style.flex = '1 1 auto';
      }
    }
  }
  function trimPage(section){
    injectTrimStyle();
    const target = findIsolationTarget() || (section && section.parentElement);
    if (target) {
      isolatePath(target);
      clearStaleInlineWidths(target);
      if (!scrolledOnce) {
        try { window.scrollTo({ top: 0, behavior: 'instant' }); } catch (e) {}
        scrolledOnce = true;
      }
    }
  }
  // Walks up from a progress bar to find the sibling column that holds the
  // label and reset text. Page layout has the row container with two flex
  // children: label column + bar column. The label column is the first
  // sibling that has a `.text-primary` element and does not contain the bar.
  function findLabelColumn(bar){
    let row = bar.parentElement;
    for (let depth = 0; depth < 10 && row && row !== document.body; depth++) {
      for (const child of row.children) {
        if (child === bar || child.contains(bar)) continue;
        if (child.querySelector && child.querySelector('.text-primary')) return child;
      }
      row = row.parentElement;
    }
    return null;
  }
  function readLabelAndReset(labelColumn){
    if (!labelColumn) return { label: '', reset: '' };
    const primary = labelColumn.querySelector('.text-primary');
    const label = primary ? (primary.textContent || '').trim() : '';
    let reset = '';
    const secondaries = labelColumn.querySelectorAll('.text-secondary, .text-footnote, .text-neutral-500');
    for (const s of secondaries) {
      const t = (s.textContent || '').trim();
      if (t && t !== label) { reset = t; break; }
    }
    return { label: label, reset: reset };
  }
  // Reads the displayed `X% used` text near the bar — used for extra usage
  // which can exceed 100% (aria-valuenow caps at 100, display shows actual).
  function readUsedPercent(bar){
    let n = bar.parentElement;
    for (let d = 0; d < 5 && n; d++) {
      const txt = (n.textContent || '');
      const m = txt.match(/(\d+)\s*%\s*used/i);
      if (m) return parseInt(m[1], 10);
      n = n.parentElement;
    }
    return null;
  }
  function extract(){
    try {
      // Page now splits bars across multiple <section> elements
      // (Plan usage limits, Weekly limits, Additional features, Extra usage)
      // and uses <span>/<div> for labels rather than <p>. Query bars
      // document-wide; identify session/weekly by label text. The
      // `[data-testid=extra-usage-section]` element is now an empty hidden
      // marker `<span>` — walk up to its containing <section> to find the
      // actual extra-usage bar and to filter that bar from the main rows.
      const extraMarker = document.querySelector('[data-testid=extra-usage-section]');
      const extraContainer = extraMarker ? (extraMarker.closest('section') || extraMarker.parentElement) : null;
      const allBars = document.querySelectorAll('[role=\""progressbar\""][aria-valuenow]');
      if (!allBars.length) return null;
      const rows = [];
      for (const bar of allBars) {
        if (extraContainer && extraContainer.contains(bar)) continue;
        const lc = findLabelColumn(bar);
        const li = readLabelAndReset(lc);
        rows.push({
          label: li.label,
          reset: li.reset,
          pct: parseInt(bar.getAttribute('aria-valuenow') || '0', 10)
        });
      }
      if (!rows.length) return null;
      function pick(predicate){
        for (const r of rows) if (predicate(r)) return r;
        return null;
      }
      const sessionRow = pick(r => /session/i.test(r.label)) || rows[0];
      const weeklyRow =
        pick(r => /^all models$/i.test(r.label)) ||
        pick(r => /weekly|all models/i.test(r.label)) ||
        pick(r => r !== sessionRow);
      if (!sessionRow || !weeklyRow) return null;
      const result = {
        SessionLabel: sessionRow.label,
        SessionReset: sessionRow.reset,
        SessionPercent: sessionRow.pct,
        WeeklyLabel: weeklyRow.label,
        WeeklyReset: weeklyRow.reset,
        WeeklyPercent: weeklyRow.pct,
        HasExtraUsage: false,
        ExtraUsageSpent: '',
        ExtraUsageReset: '',
        ExtraUsagePercent: 0
      };
      if (extraContainer) {
        const extraBar = extraContainer.querySelector('[role=\""progressbar\""][aria-valuenow]');
        if (extraBar) {
          const lc = findLabelColumn(extraBar);
          const li = readLabelAndReset(lc);
          if (li.label) {
            const usedPct = readUsedPercent(extraBar);
            result.HasExtraUsage = true;
            result.ExtraUsageSpent = li.label;
            result.ExtraUsageReset = li.reset;
            result.ExtraUsagePercent = usedPct != null ? usedPct
              : parseInt(extraBar.getAttribute('aria-valuenow') || '0', 10);
          }
        }
      }
      return result;
    } catch (e) { return null; }
  }
  function postSnapshot(){
    const data = extract();
    if (!data) return;
    const json = JSON.stringify(data);
    if (json === lastJson) return;
    lastJson = json;
    if (window.chrome && window.chrome.webview) {
      try { window.chrome.webview.postMessage(json); } catch (e) {}
    }
  }
  function tick(){
    const section = findSection();
    if (TRIM && !window.__claudeSuppressTrim && section) trimPage(section);
    postSnapshot();
  }
  tick();
  setTimeout(tick, 500);
  setTimeout(tick, 1500);
  setTimeout(tick, 3500);
  setInterval(tick, 7000);
  // Re-expand widths when the tool window gets resized — page containers
  // can hold stale inline widths from the initial render.
  window.addEventListener('resize', function(){ tick(); });
})();
";
        }

        private bool _firstNavigationCompleted;
        private readonly TaskCompletionSource<bool> _firstNavTcs = new TaskCompletionSource<bool>();
        private bool _needsReloadOnShow;
        private bool _backgroundInitMode;

        /// <summary>
        /// True once OnLoaded has started WebView2 initialization.
        /// Used by the host to avoid a redundant show-hide when the scraper is already running.
        /// </summary>
        public bool IsWebViewInitialized => _initialized;

        /// <summary>
        /// Returns a Task that completes when the first page navigation finishes (or timeoutMs elapses).
        /// Used by the host to know when it is safe to hide the frame after a background-init show.
        /// </summary>
        public Task WaitForFirstNavigationAsync(int timeoutMs = 15000)
            => Task.WhenAny(_firstNavTcs.Task, Task.Delay(timeoutMs));

        /// <summary>
        /// Set true before a background-init show so OnWindowBecameVisible skips Focus() and
        /// does not steal keyboard focus from the active VS editor.
        /// </summary>
        public void SetBackgroundInitMode(bool value) => _backgroundInitMode = value;

        /// <summary>
        /// Marks that the next explicit show should trigger a Navigate to recover any
        /// black-page rendering surface left by being hidden mid-initialization.
        /// </summary>
        public void MarkNeedsReloadOnShow() => _needsReloadOnShow = true;

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _firstNavTcs.TrySetResult(true);
            if (LoadingText != null) LoadingText.Visibility = Visibility.Collapsed;
            UpdateStatus();
            TryRedirectToUsage();

            // WebView2 hosted in WPF doesn't render its mouse cursor until
            // the control gains focus for the first time. Without this the
            // cursor stays invisible while hovering the tool window until
            // the user clicks somewhere inside, which feels broken.
            // Only prime when actually visible AND not in background-init mode.
            // Background-init shows the frame briefly then hides it; calling
            // Focus() there hands keyboard focus to the WebView2 HWND, which is
            // then hidden — VS can't recover that focus automatically, causing
            // the mouse cursor to vanish in the main IDE window. OnWindowBecameVisible
            // handles the cursor prime for the explicit-open case instead.
            if (!_firstNavigationCompleted && IsVisible && !_backgroundInitMode)
            {
                _firstNavigationCompleted = true;
                try { WebView?.Focus(); }
                catch (Exception ex) { Debug.WriteLine("ClaudeUsageControl: initial Focus() failed: " + ex); }
            }
        }

        /// <summary>
        /// claude.ai is a Next.js SPA — after OAuth login it pushes
        /// state (history.pushState) to /new without doing a full page load,
        /// so NavigationCompleted never fires. SourceChanged catches those
        /// SPA route transitions.
        /// </summary>
        private void OnSourceChanged(object sender, CoreWebView2SourceChangedEventArgs e)
        {
            TryRedirectToUsage();
        }

        /// <summary>
        /// After login, claude.ai bounces the user to a post-auth landing
        /// (/new, /chats, /projects, /recents) instead of the page we asked
        /// for. Detect those specific landings and re-navigate to
        /// /settings/usage. We whitelist the post-auth paths rather than
        /// blacklist /login because the unauthenticated home page (root /)
        /// is also a valid resting state when the user has signed out — a
        /// blacklist there would cause an infinite loop /settings/usage → /
        /// → /settings/usage → ... A 5s debounce catches double-fires from
        /// SPA pushState + NavigationCompleted on the same route change.
        /// </summary>
        private void TryRedirectToUsage()
        {
            try
            {
                var core = WebView?.CoreWebView2;
                if (core == null) return;
                if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var uri)) return;
                if (!uri.Host.Equals("claude.ai", StringComparison.OrdinalIgnoreCase)) return;

                string path = uri.AbsolutePath ?? "/";
                bool isPostAuthLanding =
                    path.Equals("/new", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("/chats", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/chat/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/projects", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/recents", StringComparison.OrdinalIgnoreCase);
                if (!isPostAuthLanding) return;

                var now = DateTime.UtcNow;
                if ((now - _lastRedirectAttemptUtc).TotalSeconds < 5) return;
                _lastRedirectAttemptUtc = now;

                core.Navigate(UsageUrl);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ClaudeUsageControl: redirect to usage failed: " + ex);
            }
        }

        private void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            string uri = e.Uri ?? "";
            bool isHelpLink =
                uri.StartsWith("https://support.claude.com/", StringComparison.OrdinalIgnoreCase) ||
                uri.StartsWith("https://support.anthropic.com/", StringComparison.OrdinalIgnoreCase) ||
                uri.StartsWith("https://docs.anthropic.com/", StringComparison.OrdinalIgnoreCase);

            if (isHelpLink)
            {
                try
                {
                    e.Handled = true;
                    Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                }
                catch { }
                return;
            }

            // Leave e.Handled = false for everything else (Google/Apple OAuth,
            // any other window.open). WebView2's default behavior is to open a
            // real popup browser window itself, which gives the OAuth flow
            // correct window.opener / postMessage / shared cookies / working
            // window.close() — the things that break when we try to manage the
            // popup ourselves with a separate WebView2 instance.
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(json)) return;
                var snap = JsonConvert.DeserializeObject<UsageSnapshot>(json);
                if (snap == null) return;
                UsageDataReceived?.Invoke(this, snap);
                UpdateStatus();
                // Persist cookies so other VS instances can reuse this session (throttled).
                _ = SaveSharedCookiesAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ClaudeUsageControl: scrape parse failed: " + ex);
            }
        }

        private void UpdateStatus()
        {
            if (StatusText == null) return;
            StatusText.Text = "Last checked: " + DateTime.Now.ToString("HH:mm:ss");
        }

        public void ApplyAutoRefreshSeconds(int seconds)
        {
            _suppressComboEvent = true;
            try
            {
                int idx = 0;
                if (seconds >= 300) idx = 4;
                else if (seconds >= 120) idx = 3;
                else if (seconds >= 60) idx = 2;
                else if (seconds >= 30) idx = 1;
                if (AutoRefreshCombo != null) AutoRefreshCombo.SelectedIndex = idx;
            }
            finally { _suppressComboEvent = false; }
            RestartAutoRefreshTimer(seconds);
        }

        private void RestartAutoRefreshTimer(int seconds)
        {
            _autoRefreshTimer?.Stop();
            _autoRefreshTimer = null;
            if (seconds <= 0) return;
            _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _autoRefreshTimer.Tick += (s, e) => Reload();
            _autoRefreshTimer.Start();
        }

        public void Reload()
        {
            try { WebView?.CoreWebView2?.Reload(); } catch { }
        }

        /// <summary>
        /// Called by the host tool window each time it becomes visible.
        /// - Skips everything during background-init show-hide (no focus theft).
        /// - Re-navigates to recover a black WebView2 surface if marked during background init.
        /// - Primes the cursor so it renders without requiring a click.
        /// </summary>
        public void OnWindowBecameVisible()
        {
            if (_backgroundInitMode) return; // startup show-hide — do not steal focus

            _firstNavigationCompleted = true; // suppress duplicate Focus() from OnNavigationCompleted

            if (_needsReloadOnShow)
            {
                _needsReloadOnShow = false;
                // Navigate rather than Reload to guarantee the rendering surface is rebuilt
                // after being hidden mid-initialization (which can leave a black WebView2).
                try { WebView?.CoreWebView2?.Navigate(UsageUrl); } catch { }
            }

            try { WebView?.Focus(); } catch { }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => Reload();

        private void AutoRefreshCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressComboEvent) return;
            var item = AutoRefreshCombo?.SelectedItem as ComboBoxItem;
            if (item?.Tag is string tag && int.TryParse(tag, out int seconds))
            {
                RestartAutoRefreshTimer(seconds);
                AutoRefreshChanged?.Invoke(this, seconds);
            }
        }

        private void OpenInBrowserButton_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(UsageUrl) { UseShellExecute = true }); } catch { }
        }

        /// <summary>
        /// Reveals the claude.ai native account switcher menu inside the embedded
        /// WebView. The trim CSS hides the avatar button by default (it lives in
        /// nav/header/sidebar), so this:
        ///   1. Removes the trim style and clears trim-related data attributes
        ///   2. Stops the tick() from re-applying the trim by setting a flag
        ///   3. Clicks the user avatar to open the org/account picker
        /// After the user picks an account, the page navigates to the new
        /// org context. When the user wants the focused usage view back,
        /// they can press Refresh — the next NavigationCompleted re-runs the
        /// injected script which re-trims if TRIM=true.
        /// </summary>
        private void SwitchAccountButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var core = WebView?.CoreWebView2;
                if (core == null) return;
                string js = @"
(function(){
  window.__claudeSuppressTrim = true;
  var s = document.getElementById('__claude_usage_trim_css__');
  if (s) s.remove();
  document.querySelectorAll('[data-claude-usage-hide]').forEach(function(el){ el.removeAttribute('data-claude-usage-hide'); });
  document.querySelectorAll('[data-claude-usage-path]').forEach(function(el){ el.removeAttribute('data-claude-usage-path'); });
  document.querySelectorAll('[data-claude-usage-keep]').forEach(function(el){ el.removeAttribute('data-claude-usage-keep'); });
  // Try several selectors for the avatar / user menu trigger.
  var selectors = [
    'button[aria-label*=""profile menu"" i]',
    'button[aria-label*=""user menu"" i]',
    'button[aria-label*=""account menu"" i]',
    'button[aria-label*=""account"" i]',
    'button[data-testid*=""user-menu"" i]',
    'button[data-testid*=""account-menu"" i]',
    'button[data-testid*=""profile"" i]',
    '[data-testid=""user-menu-button""]'
  ];
  for (var i = 0; i < selectors.length; i++) {
    var el = document.querySelector(selectors[i]);
    if (el) { el.click(); return true; }
  }
  return false;
})();";
#pragma warning disable VSTHRD110 // ExecuteScriptAsync fire-and-forget is intentional
                _ = core.ExecuteScriptAsync(js);
#pragma warning restore VSTHRD110
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ClaudeUsageControl: SwitchAccountButton_Click failed: " + ex);
            }
        }

#pragma warning disable VSTHRD100 // async void Click handler is required by WPF
        private async void SignOutButton_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            try
            {
                var cm = WebView?.CoreWebView2?.CookieManager;
                if (cm == null) return;
                var cookies = await cm.GetCookiesAsync("https://claude.ai");
                foreach (var c in cookies) cm.DeleteCookie(c);
                cookies = await cm.GetCookiesAsync("https://anthropic.com");
                foreach (var c in cookies) cm.DeleteCookie(c);
                try { if (File.Exists(SharedCookiePath)) File.Delete(SharedCookiePath); } catch { }
                Reload();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ClaudeUsageControl: sign out failed: " + ex);
            }
        }

        private void InstallWebView2Button_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(WebView2DownloadUrl) { UseShellExecute = true }); } catch { }
        }

        private void ShowError(string message)
        {
            if (LoadingText != null) LoadingText.Visibility = Visibility.Collapsed;
            if (ErrorPanel != null) ErrorPanel.Visibility = Visibility.Visible;
            if (ErrorText != null) ErrorText.Text = message;
            if (WebView != null) WebView.Visibility = Visibility.Collapsed;
        }

        private async Task LoadSharedCookiesAsync()
        {
            try
            {
                if (!File.Exists(SharedCookiePath)) return;
                string json = File.ReadAllText(SharedCookiePath);
                var dtos = JsonConvert.DeserializeObject<List<CookieDto>>(json);
                if (dtos == null || dtos.Count == 0) return;
                var cm = WebView?.CoreWebView2?.CookieManager;
                if (cm == null) return;
                foreach (var dto in dtos)
                {
                    try
                    {
                        if (dto.Expires != DateTime.MinValue && dto.Expires < DateTime.UtcNow) continue;
                        var cookie = cm.CreateCookie(dto.Name, dto.Value, dto.Domain, dto.Path);
                        cookie.Expires = dto.Expires;
                        cookie.IsHttpOnly = dto.IsHttpOnly;
                        cookie.IsSecure = dto.IsSecure;
                        cookie.SameSite = (CoreWebView2CookieSameSiteKind)dto.SameSite;
                        cm.AddOrUpdateCookie(cookie);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ClaudeUsageControl: LoadSharedCookiesAsync failed: " + ex);
            }
        }

        private async Task SaveSharedCookiesAsync()
        {
            if ((DateTime.UtcNow - _lastCookieSaveUtc).TotalSeconds < 60) return;
            _lastCookieSaveUtc = DateTime.UtcNow;
            try
            {
                var cm = WebView?.CoreWebView2?.CookieManager;
                if (cm == null) return;
                var all = new List<CoreWebView2Cookie>();
                foreach (var domain in new[] { "https://claude.ai", "https://anthropic.com" })
                    all.AddRange(await cm.GetCookiesAsync(domain));
                var dtos = all.Select(c => new CookieDto
                {
                    Name = c.Name,
                    Value = c.Value,
                    Domain = c.Domain,
                    Path = c.Path,
                    Expires = c.Expires,
                    IsHttpOnly = c.IsHttpOnly,
                    IsSecure = c.IsSecure,
                    SameSite = (int)c.SameSite
                }).ToList();
                File.WriteAllText(SharedCookiePath, JsonConvert.SerializeObject(dtos));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ClaudeUsageControl: SaveSharedCookiesAsync failed: " + ex);
            }
        }

        private class CookieDto
        {
            public string Name { get; set; }
            public string Value { get; set; }
            public string Domain { get; set; }
            public string Path { get; set; }
            public DateTime Expires { get; set; }
            public bool IsHttpOnly { get; set; }
            public bool IsSecure { get; set; }
            public int SameSite { get; set; }
        }

        private static void CleanupStaleWebView2Folders()
        {
            try
            {
                var baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClaudeCodeExtension");
                if (!Directory.Exists(baseDir)) return;
                foreach (var dir in Directory.GetDirectories(baseDir, "WebView2_*"))
                {
                    var pidStr = Path.GetFileName(dir).Substring("WebView2_".Length);
                    if (!int.TryParse(pidStr, out int pid)) continue;
                    try { System.Diagnostics.Process.GetProcessById(pid); }
                    catch (ArgumentException)
                    {
                        try { Directory.Delete(dir, recursive: true); } catch { }
                    }
                }
            }
            catch { }
        }

        public void Cleanup()
        {
            try
            {
                _autoRefreshTimer?.Stop();
                _autoRefreshTimer = null;
                if (WebView?.CoreWebView2 != null)
                {
                    WebView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                    WebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                    WebView.CoreWebView2.SourceChanged -= OnSourceChanged;
                    WebView.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
                }
                WebView?.Dispose();
            }
            catch { }
        }
    }

    /// <summary>
    /// Shared <see cref="CoreWebView2Environment"/> so the visible tool window
    /// and the hidden inline-bars scraper can share cookies (single sign-on)
    /// while running in the same process.
    /// </summary>
    internal static class ClaudeUsageWebViewEnvironment
    {
        private static CoreWebView2Environment _env;
        private static readonly object _lock = new object();
        private static Task<CoreWebView2Environment> _pending;

        public static Task<CoreWebView2Environment> GetOrCreateAsync(string userDataFolder)
        {
            lock (_lock)
            {
                if (_env != null) return Task.FromResult(_env);
                if (_pending != null) return _pending;
                _pending = CreateAsync(userDataFolder);
                return _pending;
            }
        }

        private static async Task<CoreWebView2Environment> CreateAsync(string userDataFolder)
        {
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
            lock (_lock) { _env = env; _pending = null; }
            return env;
        }
    }
}
