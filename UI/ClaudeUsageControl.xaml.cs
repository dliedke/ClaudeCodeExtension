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
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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
        private const string SharedCookieEntropy = "ClaudeCodeExtension.SharedCookies.v1";

        private static readonly string SharedCookiePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeCodeExtension", "shared_cookies.json");

        private DispatcherTimer _autoRefreshTimer;
        private int _autoRefreshSeconds;
        private bool _isHostVisible;

        /// <summary>
        /// The live WebView2 instance, built fresh by <see cref="InitializeWebViewAsync"/> and
        /// re-parented into <c>WebViewHost</c> every time. Never the same object across an
        /// init cycle: a WebView2 element whose HwndHost has been torn out of the visual tree
        /// (as happens when the tool window frame is hidden — see issue #131) cannot be revived
        /// by calling EnsureCoreWebView2Async on it again, so a dead one is discarded rather than
        /// reused.
        /// </summary>
        private Microsoft.Web.WebView2.Wpf.WebView2 WebView;

        /// <summary>Non-null while an init is in flight, so concurrent callers await the same build.</summary>
        private Task _pendingWebViewInit;

        /// <summary>
        /// Hidden top-level window that gives the background scraper a parent HWND of its own.
        /// WebView2 needs a real, rendered window to attach to, and the tool window frame stops
        /// providing one the moment it is hidden (issue #131) — which used to force a
        /// show-hide cycle of the tab on every single background refresh (issue #133).
        /// Parenting the scraper here instead keeps it alive independently of the frame, so the
        /// inline bars refresh with a plain <see cref="Reload"/> and the tab is only ever shown
        /// when the user asks for it.
        /// </summary>
        private Window _offscreenHost;
        private ContentControl _offscreenHostContent;

        /// <summary>True while the live WebView2 hangs in <see cref="_offscreenHost"/> rather than in the tool window.</summary>
        private bool _hostedOffscreen;

        private bool _suppressAutoRefreshEvent;
        private DateTime _lastRedirectAttemptUtc = DateTime.MinValue;
        private DateTime _lastCookieSaveUtc = DateTime.MinValue;
        private DateTime _lastUrlBlockClickUtc = DateTime.MinValue;

        /// <summary>Pending delayed navigate scheduled by <see cref="TryRedirectToUsage"/>.</summary>
        private DispatcherTimer _redirectDebounceTimer;

        /// <summary>
        /// Set once the embedded page has visited a sign-in / sign-out path (the user pressed
        /// "Log out" in the native claude.ai menu that Switch Account reveals). It turns the
        /// otherwise-ignored root path into a post-auth landing, so signing back in as a
        /// different user drops us straight back on the usage view — see
        /// <see cref="TryRedirectToUsage"/>. Cleared the moment that redirect fires, which is
        /// what keeps a genuinely signed-out session from bouncing root → usage → root forever.
        /// </summary>
        private bool _sawSignedOutPage;

        /// <summary>
        /// Fires when a usage snapshot is successfully scraped from the page.
        /// </summary>
        public event EventHandler<UsageSnapshot> UsageDataReceived;

        /// <summary>
        /// Fires when the auto-refresh checkbox value changes. Hosts persist
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
            try { await EnsureAliveAsync(); }
            catch (Exception ex) { Debug.WriteLine("ClaudeUsageControl.OnLoaded failed: " + ex); }
        }

        /// <summary>
        /// Builds a live WebView2 if there isn't one already, parented either into the tool
        /// window (<paramref name="offscreen"/> = false) or into the hidden off-screen host
        /// (true, for background scraping while the tab stays closed). An instance that is alive
        /// but parented in the wrong place is rebuilt — HwndHost cannot be reparented.
        ///
        /// Returns true when a fresh instance was built. A fresh instance navigates to the usage
        /// page on its own, so callers know they do not need an extra <see cref="Reload"/>.
        ///
        /// Safe to call from multiple places (Loaded, the host's show/refresh paths): concurrent
        /// callers await the in-flight build instead of racing two
        /// <see cref="InitializeWebViewAsync"/> runs.
        /// </summary>
        public async Task<bool> EnsureAliveAsync(bool offscreen = false)
        {
            var pending = _pendingWebViewInit;
            if (pending != null)
            {
#pragma warning disable VSTHRD003 // _pendingWebViewInit is this control's own build task, started on the UI thread; no cross-context deadlock
                await pending;
#pragma warning restore VSTHRD003
            }

            // Alive and already parented where the caller needs it — nothing to build.
            if (WebView?.CoreWebView2 != null && _hostedOffscreen == offscreen) return false;

            var build = InitializeWebViewAsync(offscreen);
            _pendingWebViewInit = build;
            try
            {
                await build;
            }
            finally
            {
                // Cleared here rather than inside InitializeWebViewAsync: its own finally would
                // run before the assignment above if the method ever completed synchronously,
                // leaving a permanently completed task that makes every later call a no-op.
                if (ReferenceEquals(_pendingWebViewInit, build)) _pendingWebViewInit = null;
            }
            return true;
        }

        private async Task InitializeWebViewAsync(bool offscreen)
        {
            try
            {
                // A WebView2 whose HwndHost has been torn out of the visual tree (the tool
                // window frame hiding does this — see issue #131) cannot be revived by calling
                // EnsureCoreWebView2Async on it again, so any leftover instance is discarded
                // and a fresh one takes its place.
                DisposeWebViewInstance();
                _redirectDebounceTimer?.Stop();

                WebView = new Microsoft.Web.WebView2.Wpf.WebView2
                {
                    Focusable = true,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                };

                _hostedOffscreen = offscreen;
                if (offscreen)
                {
                    EnsureOffscreenHost();
                    if (_offscreenHostContent != null) _offscreenHostContent.Content = WebView;
                }
                else if (WebViewHost != null)
                {
                    WebViewHost.Content = WebView;
                }

                _firstNavigationCompleted = false;
                _needsReloadOnShow = false;
                _firstNavTcs = new TaskCompletionSource<bool>();

                if (LoadingText != null) LoadingText.Visibility = Visibility.Visible;
                if (ErrorPanel != null) ErrorPanel.Visibility = Visibility.Collapsed;
                WebView.Visibility = Visibility.Visible;

                // Use a single fixed user-data folder so the full WebView2 profile
                // (cookies, localStorage, IndexedDB) survives a Visual Studio restart —
                // devenv.exe gets a new PID every launch, so the old per-PID folder
                // started each session logged-out and stuck on the cookie banner (issue #62).
                // A per-PID folder is still used as a fallback for the rare case where a
                // second VS process can't share the locked folder (see GetOrCreateAsync).
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                var baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClaudeCodeExtension");
                var userDataFolder = Path.Combine(baseDir, "WebView2");
                var fallbackFolder = Path.Combine(baseDir, "WebView2_" + pid);
                Directory.CreateDirectory(userDataFolder);
                CleanupStaleWebView2Folders();

                var env = await ClaudeUsageWebViewEnvironment.GetOrCreateAsync(userDataFolder, fallbackFolder);
                try
                {
                    await EnsureCoreWebView2WithTimeoutAsync(env);
                }
                catch (TimeoutException)
                {
                    // Host window never rendered — a per-PID environment would not help.
                    throw;
                }
                catch (Exception ex)
                {
                    // GetOrCreateAsync's own fallback only guards environment *creation* —
                    // it can succeed even though another VS instance already owns the shared
                    // folder's browser process. The failure actually shows up here, when this
                    // control tries to attach to it (COMException 0x8007139F, "the group or
                    // resource is not in the correct state to perform the requested
                    // operation"). Retry with a dedicated per-PID environment/folder so this
                    // instance still works (its session won't persist, but shared_cookies.json
                    // restores login on the next launch that gets the shared folder).
                    Debug.WriteLine("ClaudeUsage: EnsureCoreWebView2Async failed on shared env, retrying with per-PID fallback: " + ex);
                    Directory.CreateDirectory(fallbackFolder);
                    var fallbackEnv = await CoreWebView2Environment.CreateAsync(null, fallbackFolder, null);
                    await EnsureCoreWebView2WithTimeoutAsync(fallbackEnv);
                }

                // Re-focus after Ctrl+Scroll zoom so WebView2 re-establishes cursor tracking.
                // Without this the mouse cursor disappears until the user clicks again.
                // Guarded against background-init / hidden state: WebView2 re-applies its
                // persisted zoom factor on every Reload(), so this event also fires during
                // hidden background scrapes. Calling Focus() there pulls keyboard focus into
                // the tool window, which makes VS activate the Claude Usage tab unexpectedly.
                WebView.ZoomFactorChanged += (s, e) =>
                {
                    if (SuppressFocus) return;
#pragma warning disable VSTHRD001, VSTHRD110
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (SuppressFocus) return;
                            WebView?.Focus();
                        }
                        catch { }
                    }), System.Windows.Threading.DispatcherPriority.Background);
#pragma warning restore VSTHRD001, VSTHRD110
                };

                // Import cookies saved by another VS instance so the user stays logged in.
                await LoadSharedCookiesAsync();

                WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                WebView.CoreWebView2.SourceChanged += OnSourceChanged;
                WebView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;

                // Disable the HTTP cache for this dedicated scraper profile so Reload()/Navigate()
                // (manual Refresh click, the in-page auto-refresh timer, and the background
                // off-screen refresh) always pull a live response instead of a validator-matched
                // 304 from claude.ai's own API calls — those are what actually carry the usage
                // percentages, and a plain reload only guarantees a fresh *document*, not fresh
                // fetch() responses underneath it (issue #111: refresh looked like a no-op,
                // panel kept showing minutes/hours-old numbers even right after clicking Refresh).
                try
                {
                    await WebView.CoreWebView2.CallDevToolsProtocolMethodAsync(
                        "Network.setCacheDisabled", "{\"cacheDisabled\":true}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("ClaudeUsageControl: Network.setCacheDisabled failed: " + ex);
                }

                // claude.ai's client detection reads the "Microsoft Edge WebView2" brand
                // that Chromium adds to Sec-Ch-Ua / Sec-Ch-Ua-Full-Version-List for every
                // request when running hosted (not present in a standalone Edge browser),
                // and treats the page as blocked instead of serving normal content. Strip
                // that brand from outgoing requests so the page is scraped exactly like a
                // regular browser would see it.
                WebView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                WebView.CoreWebView2.WebResourceRequested += OnWebResourceRequested;

                // Same brand also leaks to page JS via navigator.userAgentData; patch it
                // out before any other script runs so client-side checks see a normal browser too.
                await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BuildUserAgentDataPatchScript());
                await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BuildInjectedScript(trim: true));

                WebView.CoreWebView2.Navigate(UsageUrl);
            }
            catch (TimeoutException tex)
            {
                // Not a runtime problem, so no error panel: the host window simply never
                // rendered (see EnsureCoreWebView2WithTimeoutAsync). Drop the half-built
                // instance so the next EnsureAliveAsync — the off-screen background scrape, or
                // an explicit tab open once Visual Studio does render it — starts clean.
                Debug.WriteLine("ClaudeUsageControl: WebView2 init timed out: " + tex.Message);
                DisposeWebViewInstance();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ClaudeUsageControl: WebView2 init failed: " + ex);
                ShowError("WebView2 runtime is required to display the Claude usage page. " +
                          "Click below to install it, then reopen this window.\n\n" +
                          "Details: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Bounds <c>EnsureCoreWebView2Async</c>, which only completes once the control's host
        /// window actually renders. Building into a tool window tab that Visual Studio restored
        /// but never made the active tab (the usual state right after startup) therefore left the
        /// await pending forever — and because every caller of <see cref="EnsureAliveAsync"/>
        /// waits on that same in-flight build task, the background scraper could never build its
        /// own off-screen instance either, so the inline usage bars stayed on the cached snapshot
        /// until the user clicked the Claude Usage tab.
        /// </summary>
        private async Task EnsureCoreWebView2WithTimeoutAsync(CoreWebView2Environment environment, int timeoutMs = 30000)
        {
            var init = WebView.EnsureCoreWebView2Async(environment);
#pragma warning disable VSTHRD003 // init is this control's own WebView2 build task; no cross-context deadlock
            if (await Task.WhenAny(init, Task.Delay(timeoutMs)) != init)
            {
                throw new TimeoutException(
                    "WebView2 initialization did not complete within " + timeoutMs + "ms (host window never rendered).");
            }

            await init;
#pragma warning restore VSTHRD003
        }

        /// <summary>
        /// Creates (once) the hidden window that parents the WebView2 while the tool window is
        /// closed. Positioned far off-screen and shown with ShowActivated = false, so it never
        /// becomes visible, never takes focus, and — being owned by the VS main window and kept
        /// out of the taskbar — never shows up in Alt+Tab either.
        ///
        /// Sized like a normal desktop viewport on purpose: claude.ai renders a narrower layout
        /// below its breakpoints, and the scraper reads the desktop DOM.
        /// </summary>
        private void EnsureOffscreenHost()
        {
            if (_offscreenHost != null) return;

            _offscreenHostContent = new ContentControl
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            _offscreenHost = new Window
            {
                Title = "Claude Usage background scraper",
                Width = 1024,
                Height = 768,
                Left = -32000,
                Top = -32000,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Focusable = false,
                Content = _offscreenHostContent
            };

            // Owned by the VS main window so it can never end up in front of the IDE and dies
            // with it. Must be set before Show(), otherwise the HWND is created without an owner.
            try
            {
                IntPtr owner = Process.GetCurrentProcess().MainWindowHandle;
                if (owner != IntPtr.Zero)
                {
                    new WindowInteropHelper(_offscreenHost).Owner = owner;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ClaudeUsageControl: could not own the off-screen host: " + ex.Message);
            }

            // If the window ever goes away on its own (owner destroyed, VS tearing down), forget
            // it — otherwise the next build would hand the WebView2 a dead host and hang waiting
            // for a parent HWND that no longer exists.
            _offscreenHost.Closed += (s, e) =>
            {
                _offscreenHost = null;
                _offscreenHostContent = null;
            };

            _offscreenHost.Show();
        }

        private void CloseOffscreenHost()
        {
            try
            {
                if (_offscreenHostContent != null) _offscreenHostContent.Content = null;
                _offscreenHost?.Close();
            }
            catch { }
            finally
            {
                _offscreenHost = null;
                _offscreenHostContent = null;
            }
        }

        /// <summary>
        /// Unhooks and disposes whatever WebView2 instance is currently hosted, if any. Called
        /// before building a fresh one and from <see cref="Cleanup"/>.
        /// </summary>
        private void DisposeWebViewInstance()
        {
            try
            {
                if (WebView?.CoreWebView2 != null)
                {
                    WebView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                    WebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                    WebView.CoreWebView2.SourceChanged -= OnSourceChanged;
                    WebView.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
                    WebView.CoreWebView2.WebResourceRequested -= OnWebResourceRequested;
                }
                WebView?.Dispose();
            }
            catch { }
            finally
            {
                WebView = null;
                _hostedOffscreen = false;
                if (WebViewHost != null) WebViewHost.Content = null;
                if (_offscreenHostContent != null) _offscreenHostContent.Content = null;
            }
        }

        /// <summary>
        /// Strips the "Microsoft Edge WebView2" brand that Chromium appends to
        /// Sec-Ch-Ua / Sec-Ch-Ua-Full-Version-List when running hosted in WebView2.
        /// claude.ai uses that brand to identify (and block) the embedded browser;
        /// removing it makes every request look like it came from standalone Edge.
        /// </summary>
        private void OnWebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                var headers = e.Request.Headers;
                RewriteClientHintsHeader(headers, "Sec-Ch-Ua");
                RewriteClientHintsHeader(headers, "Sec-Ch-Ua-Full-Version-List");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ClaudeUsageControl: OnWebResourceRequested failed: " + ex);
            }
        }

        private static void RewriteClientHintsHeader(CoreWebView2HttpRequestHeaders headers, string headerName)
        {
            if (!headers.Contains(headerName)) return;
            string value = headers.GetHeader(headerName);
            if (string.IsNullOrEmpty(value) || value.IndexOf("WebView2", StringComparison.OrdinalIgnoreCase) < 0) return;

            string cleaned = string.Join(", ", value
                .Split(',')
                .Select(p => p.Trim())
                .Where(p => p.IndexOf("WebView2", StringComparison.OrdinalIgnoreCase) < 0));
            headers.SetHeader(headerName, cleaned);
        }

        /// <summary>
        /// Patches navigator.userAgentData so the "Microsoft Edge WebView2" brand
        /// (added to brands/fullVersionList and getHighEntropyValues() results only
        /// when hosted in WebView2) is invisible to page JS, mirroring the header
        /// rewrite done for network requests in <see cref="OnWebResourceRequested"/>.
        /// </summary>
        private static string BuildUserAgentDataPatchScript()
        {
            return @"
(function(){
  try {
    var uad = navigator.userAgentData;
    if (!uad) return;
    function strip(list){ return list ? list.filter(function(b){ return (b.brand || '').indexOf('WebView2') === -1; }) : list; }
    var brands = strip(uad.brands);
    if (brands) Object.defineProperty(uad, 'brands', { get: function(){ return brands; }, configurable: true });
    var fullVersionList = strip(uad.fullVersionList);
    if (fullVersionList) Object.defineProperty(uad, 'fullVersionList', { get: function(){ return fullVersionList; }, configurable: true });
    var origGetHighEntropyValues = uad.getHighEntropyValues ? uad.getHighEntropyValues.bind(uad) : null;
    if (origGetHighEntropyValues) {
      uad.getHighEntropyValues = function(hints){
        return origGetHighEntropyValues(hints).then(function(result){
          if (result.brands) result.brands = strip(result.brands);
          if (result.fullVersionList) result.fullVersionList = strip(result.fullVersionList);
          return result;
        });
      };
    }
  } catch (e) {}
})();
";
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
  // The usage bars used to be [role=progressbar]; claude.ai switched them to
  // [role=meter] (data-cds=Meter). Match both so a future revert doesn't
  // break us, and so older cached pages still parse.
  const BAR_SEL = '[role=\""meter\""][aria-valuenow], [role=\""progressbar\""][aria-valuenow]';
  const BAR_ROLE_SEL = '[role=\""meter\""], [role=\""progressbar\""]';
  function isBarRole(el){
    const r = el && el.getAttribute && el.getAttribute('role');
    return r === 'meter' || r === 'progressbar';
  }
  function findSection(){
    const bar = document.querySelector(BAR_SEL);
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
    if (node && node.querySelector(BAR_ROLE_SEL)) return node;
    const bar = document.querySelector(BAR_SEL);
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
      // The page's own internal scroll pane (and every ancestor above it,
      // up to <body>) — deliberately left out of the `path` collapse above.
      // The real claude.ai settings dialog scrolls via a flex-column chain
      // (h-screen/flex-1/min-h-0 wrappers around an `overflow-y: auto`
      // pane); forcing `display: block` + `height: auto` + `overflow:
      // visible` on any link in that chain (as the path rule does) collapses
      // the pane to its content height and silently deletes the native
      // scrollbar, leaving anything past the fold (e.g. the usage-credits
      // spend/balance rows) unreachable. Only strip width caps and the
      // sidebar's grid column here so the pane still spans the full panel
      // width — its own display/height/overflow-y stay exactly as claude.ai
      // authored them, so the browser keeps producing a real scrollbar.
      '[data-claude-usage-scrollbox=\""1\""] {' +
      '  width: 100% !important;' +
      '  max-width: none !important;' +
      '  min-width: 0 !important;' +
      '  margin: 0 !important;' +
      '  box-sizing: border-box !important;' +
      '  grid-template-columns: unset !important;' +
      '  grid-template-rows: unset !important;' +
      '}' +
      // Target wrapper: only widen it — do NOT touch display/height/overflow.
      // `findIsolationTarget()` sometimes resolves to a `tabindex=-1` dialog
      // shell rather than the small `div.pb-8` content block, and that shell
      // commonly uses `display:flex; flex-direction:column` to stack a
      // header above an internally-scrolling body (`flex-1 min-h-0
      // overflow-y-auto`). Forcing `display:block` here (as an earlier build
      // did) collapses that flex-column, so the scrollable child loses its
      // bounded height and stops scrolling entirely — the shell's own
      // (untouched) `overflow:hidden` then just clips the excess with no
      // scrollbar. Leaving display/height/overflow alone preserves whatever
      // native scroll mechanism the target already had.
      '[data-claude-usage-keep=\""1\""] {' +
      '  width: 100% !important;' +
      '  max-width: none !important;' +
      '  margin: 0 !important;' +
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
      '[role=\""progressbar\""], [role=\""meter\""] { width: 100% !important; min-width: 0 !important; flex: 1 1 auto !important; }';
    (document.head || document.documentElement).appendChild(style);
    styleInjected = true;
  }
  let scrolledOnce = false;
  function isolatePath(target){
    if (!target) return;
    target.setAttribute('data-claude-usage-keep', '1');
    let node = target;
    let depth = 0;
    // Once we walk past the real scroll pane (see below), every further
    // ancestor is part of its height-bounding chain and must get the same
    // hands-off (`scrollbox`) treatment instead of the destructive `path`
    // collapse — otherwise a level just above the pane forcing `display:
    // block` would strip the `flex-1`/`min-h-0` sizing the pane relies on
    // to stay bounded, breaking its scroll all the same.
    let pastScrollbox = false;
    while (node && node !== document.body && depth < 30) {
      const parent = node.parentElement;
      if (!parent) break;
      // Mark intermediate ancestors as `path` so the CSS collapses them
      // to `display: block` at 100% width with zero padding/margin. We
      // deliberately do NOT mark <body> itself — body keeps its own
      // padding/margin styling from the rule above so the bars and
      // labels have breathing room from the WebView2 panel edges.
      if (parent !== document.body) {
        if (parent.hasAttribute('data-claude-usage-scrollbox')) {
          pastScrollbox = true;
        } else if (!parent.hasAttribute('data-claude-usage-path')) {
          const isNativeScrollPane = !pastScrollbox &&
            (window.getComputedStyle(parent).overflowY || '').match(/^(auto|scroll)$/);
          if (isNativeScrollPane || pastScrollbox) {
            parent.setAttribute('data-claude-usage-scrollbox', '1');
            pastScrollbox = true;
          } else {
            parent.setAttribute('data-claude-usage-path', '1');
          }
        }
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
        if (sibling.hasAttribute('data-claude-usage-scrollbox')) continue;
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
      if (isBarRole(d)) continue;
      // Skip children of bar containers — they hold the inline fill width (e.g. 18%).
      if (d.closest && d.closest(BAR_ROLE_SEL)) continue;
      d.style.width = '';
      d.style.minWidth = '';
      d.style.maxWidth = '';
      d.style.flex = '';
      d.style.flexBasis = '';
    }
    const bars = target.querySelectorAll(BAR_ROLE_SEL);
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
  // Language-independent label: the bar carries aria-labelledby pointing at
  // the label element, so we don't rely on class names or English text.
  function readBarLabelAndReset(bar){
    const lc = findLabelColumn(bar);
    const li = readLabelAndReset(lc);
    if (!li.label) {
      const id = bar.getAttribute('aria-labelledby');
      if (id) {
        const el = document.getElementById(id);
        if (el) li.label = (el.textContent || '').trim();
      }
    }
    return li;
  }
  // Reads the displayed `X% used` text near the bar — used for extra usage
  // which can exceed 100% (aria-valuenow caps at 100, display shows actual).
  function readUsedPercent(bar){
    // Prefer aria-valuetext (\""63% usado\"" / \""63% used\"") — language-independent
    // and can exceed 100 for extra usage where aria-valuenow caps at 100.
    const vt = bar.getAttribute('aria-valuetext') || '';
    let m = vt.match(/(\d+)\s*%/);
    if (m) return parseInt(m[1], 10);
    let n = bar.parentElement;
    for (let d = 0; d < 5 && n; d++) {
      const txt = (n.textContent || '');
      m = txt.match(/(\d+)\s*%/);
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
      const allBars = document.querySelectorAll(BAR_SEL);
      if (!allBars.length) return null;
      const rows = [];
      for (const bar of allBars) {
        if (extraContainer && extraContainer.contains(bar)) continue;
        const li = readBarLabelAndReset(bar);
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
      // Identify by label when possible (multi-language keywords), but fall
      // back to document order — session is always first, the aggregate
      // \""all models\"" weekly bar always precedes the per-model bars (Fable,
      // etc.). Order-based fallback keeps working when the account language
      // isn't one we listed. Per-model weekly bars must never be picked as
      // the weekly row, so weekly falls back to \""first row after session\"".
      const sessionRow = pick(r => /session|sess[aã]o/i.test(r.label)) || rows[0];
      const weeklyRow =
        pick(r => /^all models$|todos os modelos|tous les mod|alle modelle|todos los modelos/i.test(r.label)) ||
        pick(r => /weekly|semanal|semaine|w[oö]chent/i.test(r.label)) ||
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
        const extraBar = extraContainer.querySelector(BAR_SEL);
        if (extraBar) {
          const li = readBarLabelAndReset(extraBar);
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
        private TaskCompletionSource<bool> _firstNavTcs = new TaskCompletionSource<bool>();
        private bool _needsReloadOnShow;
        private bool _backgroundInitMode;

        /// <summary>
        /// True while there is a live CoreWebView2 to talk to. A real liveness check rather than
        /// a sticky "did we ever start initializing" flag — the WebView2 can die (frame teardown
        /// while hidden — issue #131) long after that first init, and callers need to know when
        /// that happened so they call <see cref="EnsureAliveAsync"/> instead of reloading a dead
        /// instance.
        /// </summary>
        public bool IsWebViewInitialized => WebView?.CoreWebView2 != null;

        /// <summary>
        /// True when the live instance (if any) is parented into the hidden off-screen host
        /// rather than into the tool window frame. Lets the host reload an in-frame instance
        /// for a background scrape instead of tearing it out and rebuilding it off-screen,
        /// which would blank the tab it is rendering in.
        /// </summary>
        public bool IsHostedOffscreen => _hostedOffscreen;

        /// <summary>
        /// True whenever priming <see cref="System.Windows.UIElement.Focus"/> on the WebView2
        /// would be wrong: during a background-init show-hide, while the instance lives in the
        /// off-screen host (focusing that would pull keyboard focus into an invisible window),
        /// and while this control simply isn't on screen.
        /// </summary>
        private bool SuppressFocus => _backgroundInitMode || _hostedOffscreen || !IsVisible;

        /// <summary>
        /// Returns a Task that completes when the first page navigation finishes (or timeoutMs elapses).
        /// Used by the host to know when it is safe to hide the frame after a background-init show.
        /// </summary>
#pragma warning disable VSTHRD003 // _firstNavTcs is completed by this control's own navigation event on the UI thread; no cross-context deadlock
        public Task WaitForFirstNavigationAsync(int timeoutMs = 15000)
            => Task.WhenAny(_firstNavTcs.Task, Task.Delay(timeoutMs));
#pragma warning restore VSTHRD003

        /// <summary>
        /// Set true before a background-init show so OnWindowBecameVisible skips Focus() and
        /// does not steal keyboard focus from the active VS editor.
        /// </summary>
        public void SetBackgroundInitMode(bool value) => _backgroundInitMode = value;

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _firstNavTcs.TrySetResult(true);
            if (LoadingText != null) LoadingText.Visibility = Visibility.Collapsed;
            UpdateStatus();
            if (TryHandleUrlBlock()) return;
            TryRestoreTrim();
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
            if (!_firstNavigationCompleted && !SuppressFocus)
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
            if (TryHandleUrlBlock()) return;
            TryRestoreTrim();
            TryRedirectToUsage();
        }

        /// <summary>
        /// Some corporate proxies (e.g. iboss at 160.79.104.10:6080) intercept the
        /// usage page with an interstitial that has a single Continue submit
        /// button. Detect the block URL and click the button so navigation
        /// resumes back to /settings/usage automatically.
        ///
        /// Runs in two modes — both work while the tool window is hidden because
        /// CoreWebView2 keeps processing navigation, DOM construction and JS even
        /// without a visible rendering surface:
        ///  1. Polls for the submit button with multiple selector fallbacks (some
        ///     interstitials render late or use &lt;button&gt; instead of input).
        ///  2. Selector is broadened beyond input[name=ok] so unrelated variants
        ///     (iboss/Forcepoint/Zscaler/etc. all use slightly different markup)
        ///     also get clicked.
        ///
        /// Throttled to one click attempt per 3 s so a redirect storm can't spam clicks.
        /// </summary>
        private bool TryHandleUrlBlock()
        {
            try
            {
                var core = WebView?.CoreWebView2;
                if (core == null) return false;
                if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var uri)) return false;
                if (!uri.AbsolutePath.EndsWith("/urlblock.php", StringComparison.OrdinalIgnoreCase)) return false;

                var now = DateTime.UtcNow;
                if ((now - _lastUrlBlockClickUtc).TotalSeconds < 3) return true;
                _lastUrlBlockClickUtc = now;

                // Poll up to ~5s for the Continue/OK button — the interstitial form may not be
                // in the DOM at NavigationCompleted time, and runs even when the tool window is
                // hidden (CoreWebView2 keeps processing JS in the background).
                string js = @"
(function(){
  var attempts = 0;
  var maxAttempts = 25; // 25 * 200ms = 5s
  function tryClick(){
    attempts++;
    var btn =
      document.querySelector('input[type=submit][name=ok]') ||
      document.querySelector('form input[type=submit]') ||
      document.querySelector('form button[type=submit]') ||
      document.querySelector('input[type=submit]') ||
      document.querySelector('button[type=submit]') ||
      document.querySelector('button[name=ok]') ||
      document.querySelector('a[href*=continue i]');
    if (btn) {
      try { btn.click(); return true; } catch(e) {}
    }
    var form = document.querySelector('form');
    if (form) {
      try { form.submit(); return true; } catch(e) {}
    }
    if (attempts < maxAttempts) {
      setTimeout(tryClick, 200);
    }
    return false;
  }
  tryClick();
})();";
#pragma warning disable VSTHRD110
                _ = core.ExecuteScriptAsync(js);
#pragma warning restore VSTHRD110
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ClaudeUsageControl: TryHandleUrlBlock failed: " + ex);
                return false;
            }
        }

        /// <summary>
        /// After login (including a Switch Account re-login), claude.ai bounces the user to a
        /// post-auth landing (/new, /chats, /projects, /recents) instead of the page we asked
        /// for. Detect those specific landings and re-navigate to /settings/usage. We whitelist
        /// the post-auth paths rather than blacklist /login because the unauthenticated home page
        /// (root /) is also a valid resting state when the user has signed out — a blacklist
        /// there would cause an infinite loop /settings/usage → / → /settings/usage → ...
        /// A 5s debounce (<see cref="_lastRedirectAttemptUtc"/>) catches double-fires from SPA
        /// pushState + NavigationCompleted on the same route change.
        ///
        /// The actual navigate is delayed a short beat (<see cref="_redirectDebounceTimer"/>)
        /// rather than fired immediately: claude.ai's own "Log out" flow briefly passes through
        /// one of these same landing paths before continuing on to /login, and an immediate
        /// Navigate() there aborts that in-flight sign-out request, making Log out look like it
        /// just "refreshed" back to the usage view instead of signing out. Re-checking the URL
        /// hasn't moved on by the time the delay elapses tells the two cases apart without needing
        /// to know anything about what the page's own JS is doing.
        /// </summary>
        private void TryRedirectToUsage()
        {
            try
            {
                var core = WebView?.CoreWebView2;
                if (core == null) return;
                if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var uri)) return;
                if (!uri.Host.Equals("claude.ai", StringComparison.OrdinalIgnoreCase))
                {
                    _redirectDebounceTimer?.Stop();
                    return;
                }

                string path = uri.AbsolutePath ?? "/";

                // Remember that the user went through a manual sign-out / sign-in. claude.ai
                // sends a fresh login to the root path about as often as it sends it to /new,
                // and root is normally off-limits here (it doubles as the signed-out home page).
                if (path.StartsWith("/login", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/logout", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/magic-link", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/auth", StringComparison.OrdinalIgnoreCase))
                {
                    _sawSignedOutPage = true;
                    _redirectDebounceTimer?.Stop();
                    return;
                }

                bool isPostAuthLanding =
                    path.Equals("/new", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("/chats", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/chat/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/projects", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/recents", StringComparison.OrdinalIgnoreCase);

                // Root only counts as a landing right after a sign-out we witnessed, and even
                // then only once the session cookie proves the new sign-in actually completed.
                bool isReLoginRootLanding = !isPostAuthLanding && _sawSignedOutPage && path.Equals("/", StringComparison.Ordinal);

                if (!isPostAuthLanding && !isReLoginRootLanding)
                {
                    // Moved on to somewhere else (e.g. claude.ai finished signing out and landed
                    // on /login or /) before the pending redirect fired — cancel it.
                    _redirectDebounceTimer?.Stop();
                    return;
                }

                var now = DateTime.UtcNow;
                if ((now - _lastRedirectAttemptUtc).TotalSeconds < 5) return;

                _redirectDebounceTimer?.Stop();
                _redirectDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                _redirectDebounceTimer.Tick += (s, e) =>
                {
                    _redirectDebounceTimer.Stop();
#pragma warning disable VSTHRD110 // fire-and-forget: timer tick cannot await; failures are logged inside
                    _ = RedirectToUsageIfSettledAsync(path, requireSessionCookie: isReLoginRootLanding);
#pragma warning restore VSTHRD110
                };
                _redirectDebounceTimer.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ClaudeUsageControl: redirect to usage failed: " + ex);
            }
        }

        /// <summary>
        /// Second half of <see cref="TryRedirectToUsage"/>, run after the settle delay.
        /// Navigates back to the usage view only when the page is still resting on the same
        /// landing path it was on when the timer was armed — a mid-flight hop through the
        /// sign-out flow will have moved on by now — and, for the root landing, only when a
        /// claude.ai session cookie confirms the user is actually signed in again.
        /// </summary>
        private async Task RedirectToUsageIfSettledAsync(string landingPath, bool requireSessionCookie)
        {
            try
            {
                var core = WebView?.CoreWebView2;
                if (core == null) return;
                if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var uri)) return;
                if (!string.Equals(uri.AbsolutePath, landingPath, StringComparison.OrdinalIgnoreCase)) return;

                if (requireSessionCookie && !await HasClaudeSessionCookieAsync()) return;

                // Re-check the URL: awaiting the cookie read gave the page another chance to move.
                core = WebView?.CoreWebView2;
                if (core == null) return;
                if (!Uri.TryCreate(core.Source, UriKind.Absolute, out uri)) return;
                if (!string.Equals(uri.AbsolutePath, landingPath, StringComparison.OrdinalIgnoreCase)) return;

                // One shot per sign-out: if this navigate lands back where it came from (session
                // gone after all), nothing re-arms the root landing and we stop rather than loop.
                _sawSignedOutPage = false;
                _lastRedirectAttemptUtc = DateTime.UtcNow;
                core.Navigate(UsageUrl);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ClaudeUsageControl: delayed redirect to usage failed: " + ex);
            }
        }

        /// <summary>
        /// True when the WebView holds a non-empty claude.ai session cookie, i.e. the user has
        /// finished signing in. Used to tell "logged back in and landed on the home page" apart
        /// from "sitting on the signed-out home page".
        /// </summary>
        private async Task<bool> HasClaudeSessionCookieAsync()
        {
            try
            {
                var cm = WebView?.CoreWebView2?.CookieManager;
                if (cm == null) return false;
                var cookies = await cm.GetCookiesAsync("https://claude.ai");
                foreach (var c in cookies)
                {
                    if (c.Name != null &&
                        c.Name.IndexOf("sessionKey", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        !string.IsNullOrEmpty(c.Value))
                    {
                        return true;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ClaudeUsageControl: session cookie probe failed: " + ex);
                return false;
            }
        }

        /// <summary>
        /// Switch Account sets <c>window.__claudeSuppressTrim</c> so the native claude.ai chrome
        /// — and with it the account menu — stays visible. The flag lives on the document, so a
        /// full page load clears it by itself, but an SPA route change back to the usage view does
        /// not. Clearing it whenever we are on the usage page again brings the focused, trimmed
        /// bars back instead of leaving the raw claude.ai layout in the panel.
        /// </summary>
        private void TryRestoreTrim()
        {
            try
            {
                var core = WebView?.CoreWebView2;
                if (core == null) return;
                if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var uri)) return;
                if (!uri.Host.Equals("claude.ai", StringComparison.OrdinalIgnoreCase)) return;
                if (!(uri.AbsolutePath ?? "").StartsWith("/settings/usage", StringComparison.OrdinalIgnoreCase)) return;
#pragma warning disable VSTHRD110 // ExecuteScriptAsync fire-and-forget is intentional
                _ = core.ExecuteScriptAsync("window.__claudeSuppressTrim = false;");
#pragma warning restore VSTHRD110
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ClaudeUsageControl: restore trim failed: " + ex);
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
                if (!IsAllowedUsageMessageSource(e.Source))
                {
                    Debug.WriteLine("ClaudeUsageControl: ignored WebView message from " + e.Source);
                    return;
                }

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

        private static bool IsAllowedUsageMessageSource(string source)
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out Uri uri))
            {
                return false;
            }

            return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(uri.Host, "claude.ai", StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateStatus()
        {
            if (StatusText == null) return;
            StatusText.Text = "Last checked: " + DateTime.Now.ToString("HH:mm:ss");
        }

        public void ApplyAutoRefreshSeconds(int seconds)
        {
            // Only Off/1m exist now; any legacy setting holding some other value
            // (from an older release's JSON) is treated as 1m.
            int normalized = seconds <= 0 ? 0 : Math.Max(60, seconds);
            _autoRefreshSeconds = normalized;
            _suppressAutoRefreshEvent = true;
            try
            {
                if (AutoRefreshCheck != null) AutoRefreshCheck.IsChecked = normalized > 0;
            }
            finally { _suppressAutoRefreshEvent = false; }
            RestartAutoRefreshTimer(_isHostVisible ? normalized : 0);
        }

        /// <summary>
        /// Keeps the page-owned timer limited to an active tool-window tab. A deactivated tab can
        /// lose its WebView2 rendering host, so the parent control takes over with its off-screen
        /// scraper until Visual Studio activates this tab again.
        /// </summary>
        public void SetHostVisibility(bool visible)
        {
            if (_isHostVisible == visible) return;
            _isHostVisible = visible;
            RestartAutoRefreshTimer(visible ? _autoRefreshSeconds : 0);
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

        /// <summary>
        /// Reloads the live page. Returns false when there is nothing alive to reload — the
        /// caller should treat that as a signal to rebuild via <see cref="EnsureAliveAsync"/>
        /// instead of assuming the reload silently did its job (issue #131).
        /// </summary>
        public bool Reload()
        {
            try
            {
                var core = WebView?.CoreWebView2;
                if (core == null) return false;
                core.Reload();
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Called by the host tool window each time it becomes visible.
        /// - Skips everything during background-init show-hide (no focus theft).
        /// - Rebuilds the WebView2 if it died while hidden (issue #131) or if it is currently
        ///   parented into the off-screen scraper host, instead of leaving a blank panel.
        /// - Re-navigates to recover a black WebView2 surface if marked during background init.
        /// - Primes the cursor so it renders without requiring a click.
        /// </summary>
        public void OnWindowBecameVisible()
        {
            if (_backgroundInitMode) return; // startup show-hide — do not steal focus

            _firstNavigationCompleted = true; // suppress duplicate Focus() from OnNavigationCompleted

            if (WebView?.CoreWebView2 == null || _hostedOffscreen)
            {
#pragma warning disable VSTHRD110 // fire-and-forget: OnWindowBecameVisible is a synchronous VS callback
                _ = ReviveOnShowAsync();
#pragma warning restore VSTHRD110
                return;
            }

            if (_needsReloadOnShow)
            {
                _needsReloadOnShow = false;
                // Navigate rather than Reload to guarantee the rendering surface is rebuilt
                // after being hidden mid-initialization (which can leave a black WebView2).
                try { WebView?.CoreWebView2?.Navigate(UsageUrl); } catch { }
            }

            try { WebView?.Focus(); } catch { }
        }

        /// <summary>
        /// Builds the tool window's own WebView2 when the user explicitly opens the tab — either
        /// because the previous one died while hidden (issue #131) or because the live one is the
        /// background scraper sitting in the off-screen host, which cannot be reparented.
        /// </summary>
        private async Task ReviveOnShowAsync()
        {
            try
            {
                await EnsureAliveAsync(offscreen: false);
                try { WebView?.Focus(); } catch { }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ClaudeUsageControl: revive on show failed: " + ex);
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            // Refresh is the documented way back to the focused usage view after Switch Account
            // reveals the native claude.ai menu — Reload() alone would just reload whatever page
            // the account switch left us on (e.g. /new), not bring the usage view back, so
            // explicitly navigate there whenever we're not on it already.
            var core = WebView?.CoreWebView2;
            if (core != null &&
                Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) &&
                !uri.AbsolutePath.StartsWith("/settings/usage", StringComparison.OrdinalIgnoreCase))
            {
                _redirectDebounceTimer?.Stop();
                core.Navigate(UsageUrl);
                return;
            }

            // Reload() silently no-ops when the live instance died while the tab sat hidden
            // (issue #131) — fall back to a full rebuild instead of leaving Refresh looking like
            // it did nothing.
            if (!Reload())
            {
#pragma warning disable VSTHRD110 // fire-and-forget: click handler cannot be async void-awaited here
                _ = ReviveOnShowAsync();
#pragma warning restore VSTHRD110
            }
        }

        private void AutoRefreshCheck_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_suppressAutoRefreshEvent) return;
            int seconds = AutoRefreshCheck?.IsChecked == true ? 60 : 0;
            _autoRefreshSeconds = seconds;
            RestartAutoRefreshTimer(_isHostVisible ? seconds : 0);
            AutoRefreshChanged?.Invoke(this, seconds);
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
        /// After the user picks an account, the page navigates to the new org context —
        /// <see cref="TryRedirectToUsage"/> notices the settled post-auth landing and brings the
        /// focused usage view back on its own; pressing Refresh does the same immediately.
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

        public async Task SignOutAsync()
        {
            try
            {
                _redirectDebounceTimer?.Stop();
                try { if (File.Exists(SharedCookiePath)) File.Delete(SharedCookiePath); } catch { }

                var cm = WebView?.CoreWebView2?.CookieManager;
                if (cm != null)
                {
                    var cookies = await cm.GetCookiesAsync("https://claude.ai");
                    foreach (var c in cookies) cm.DeleteCookie(c);
                    cookies = await cm.GetCookiesAsync("https://anthropic.com");
                    foreach (var c in cookies) cm.DeleteCookie(c);
                    Reload();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ClaudeUsageControl: sign out failed: " + ex);
            }
        }

#pragma warning disable VSTHRD100 // async void Click handler is required by WPF
        private async void SignOutButton_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await SignOutAsync();
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
                string stored = File.ReadAllText(SharedCookiePath);
                bool loadedProtectedPayload = TryUnprotectSharedCookieJson(stored, out string json);
                if (!loadedProtectedPayload)
                {
                    json = stored;
                }

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

                if (!loadedProtectedPayload)
                {
                    await SaveSharedCookiesAsync(force: true);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ClaudeUsageControl: LoadSharedCookiesAsync failed: " + ex);
            }
        }

        private async Task SaveSharedCookiesAsync(bool force = false)
        {
            if (!force && (DateTime.UtcNow - _lastCookieSaveUtc).TotalSeconds < 60) return;
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
                Directory.CreateDirectory(Path.GetDirectoryName(SharedCookiePath));
                string cookieJson = JsonConvert.SerializeObject(dtos);
                File.WriteAllText(SharedCookiePath, ProtectSharedCookieJson(cookieJson));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ClaudeUsageControl: SaveSharedCookiesAsync failed: " + ex);
            }
        }

        private static bool TryUnprotectSharedCookieJson(string stored, out string json)
        {
            json = null;
            try
            {
                var payload = JsonConvert.DeserializeObject<ProtectedCookieStore>(stored);
                if (payload == null || string.IsNullOrEmpty(payload.ProtectedData))
                {
                    return false;
                }

                byte[] protectedBytes = Convert.FromBase64String(payload.ProtectedData);
                byte[] entropy = Encoding.UTF8.GetBytes(SharedCookieEntropy);
                byte[] unprotectedBytes = ProtectedData.Unprotect(
                    protectedBytes, entropy, DataProtectionScope.CurrentUser);
                json = Encoding.UTF8.GetString(unprotectedBytes);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ClaudeUsageControl: protected cookie payload read failed: " + ex.Message);
                return false;
            }
        }

        private static string ProtectSharedCookieJson(string cookieJson)
        {
            byte[] cookieBytes = Encoding.UTF8.GetBytes(cookieJson ?? string.Empty);
            byte[] entropy = Encoding.UTF8.GetBytes(SharedCookieEntropy);
            byte[] protectedBytes = ProtectedData.Protect(
                cookieBytes, entropy, DataProtectionScope.CurrentUser);

            return JsonConvert.SerializeObject(new ProtectedCookieStore
            {
                Version = 1,
                ProtectedData = Convert.ToBase64String(protectedBytes)
            });
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

        private class ProtectedCookieStore
        {
            public int Version { get; set; }
            public string ProtectedData { get; set; }
        }

        // Reclaims legacy per-PID profile folders left by older versions (and by the
        // multi-instance fallback). The fixed "WebView2" folder does not match the
        // "WebView2_*" glob, so the persistent profile is never touched here (issue #62).
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
                _redirectDebounceTimer?.Stop();
                _redirectDebounceTimer = null;
                _isHostVisible = false;
                DisposeWebViewInstance();
                CloseOffscreenHost();
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

        public static Task<CoreWebView2Environment> GetOrCreateAsync(string userDataFolder, string fallbackFolder = null)
        {
            lock (_lock)
            {
                if (_env != null) return Task.FromResult(_env);
                if (_pending != null) return _pending;
                _pending = CreateAsync(userDataFolder, fallbackFolder);
                return _pending;
            }
        }

        private static async Task<CoreWebView2Environment> CreateAsync(string userDataFolder, string fallbackFolder)
        {
            try
            {
                CoreWebView2Environment env;
                try
                {
                    env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
                }
                catch (Exception ex) when (!string.IsNullOrEmpty(fallbackFolder))
                {
                    // Another VS process holds an exclusive lock on the shared folder.
                    // Fall back to a per-process folder so this instance still works
                    // (its session won't persist, but shared_cookies.json restores login
                    // on the next launch that gets the shared folder).
                    Debug.WriteLine("ClaudeUsage: shared WebView2 folder unavailable, using per-PID fallback: " + ex);
                    Directory.CreateDirectory(fallbackFolder);
                    env = await CoreWebView2Environment.CreateAsync(null, fallbackFolder, null);
                }
                lock (_lock) { _env = env; _pending = null; }
                return env;
            }
            catch
            {
                lock (_lock) { _pending = null; }
                throw;
            }
        }
    }
}
