// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: ConsoleUi.cs
// Project: SIM7600G-H Console Dialer (C# .NET 8)
// ----------------------------------------------------------------------------
// PURPOSE
//   Provides a simple, flicker-minimized console “page” framework with a
//   persistent status area at the bottom third of the screen. Pages can be
//   pushed/popped like a navigation stack. The UI redraws when either:
//     • navigation changes (Push / Pop / ReplaceTop),
//     • the current page requests it (PageNav.Redraw),
//     • or new status lines are appended (StatusHub.Version changes).
//
// FLICKER CONTROL
//   Console.Clear() and frequent cursor moves can produce flicker if executed
//   too often. This class:
//
//   1) Tracks StatusHub.Version so we only redraw when *new* status text exists.
//   2) Applies a minimal redraw interval (MinRedrawMs) to throttle bursts.
//   3) Draws everything while holding a _drawLock to avoid interleaved frames.
//      (This does not add parallelism; it simply protects from draw re-entry.)
//
// LAYOUT
//   ┌───────────────────────────────────────────────────────────────────┐
//   │                         Page Content Area                         │
//   │                           (top ~2/3rds)                           │
//   ├───────────────────────────────────────────────────────────────────┤ ← separator
//   │ Status — <InstanceId>                                            │
//   │ <most recent status lines scroll upward as new lines arrive>     │
//   │ ...                                                              │
//   └───────────────────────────────────────────────────────────────────┘
//
//   Notes:
//   • We also paint a *global header ribbon* with modem/call/SMS indicators on
//     row 1 (and soft-keys on row 2). This is drawn *after* the page so it
//     always sits on top without needing coordination from individual pages.
//   • Pages should not assume exclusive use of rows 1–2; those are reserved for
//     the global header/softkeys. Page content height passed to Draw(...) is
//     already computed to keep things non-overlapping.
//
// EXTENSIBILITY
//   • Implement IPage.Draw() to render any page. Use IPage.HandleKey() for input.
//   • Set IPage.WantsPassiveRefresh=true if your page wants periodic redraws
//     (e.g., a clock or live meters), otherwise leave it false to avoid extra work.
//   • Use StatusHub.Add(...) to push new lines into the status tail.
//
// THREADING
//   • All rendering is performed on the UI thread calling Run(). This class
//     does not spawn background threads.
//   • StatusHub can be updated from other threads; Version increments will
//     be observed on the next Run() loop iteration.
//
// RESIZE ROBUSTNESS
//   • All writes are defensive against concurrent console resizes. Cursor moves
//     are wrapped in try/catch; width/height are clamped to minimums (80×24).
//   • We clear/pad lines we write to prevent “ghost” characters after a shrink.
// ============================================================================

using System.Diagnostics;

namespace Sim7600Console.UI
{
    /// <summary>
    /// Console layout & page host that manages a navigation stack and a persistent
    /// status area. Redraws are throttled to reduce flicker and CPU churn.
    /// </summary>
    public sealed class ConsoleUi
    {
        // -------- Navigation state ------------------------------------------------------

        /// <summary>
        /// LIFO stack of pages. Topmost page is considered the “current” page.
        /// </summary>
        private readonly Stack<IPage> _navStack = new();

        /// <summary>
        /// Shared StatusHub for logging and for producing the status-area tail.
        /// </summary>
        private readonly StatusHub _status;

        /// <summary>
        /// Flag polled by Run() to know when the app should exit.
        /// </summary>
        private bool _exitRequested;

        // -------- Layout configuration --------------------------------------------------

        /// <summary>
        /// Fraction of the console height allocated to the status area (bottom third).
        /// Tuned for readability: tall enough for context, small enough for content.
        /// </summary>
        private const double StatusAreaRatio = 0.33;

        /// <summary>
        /// Ensures only one thread draws at a time (prevents interleaving artifacts).
        /// While we do not draw from multiple threads, this protects against
        /// accidental re-entrant calls (e.g., nested Redraw()).
        /// </summary>
        private readonly object _drawLock = new();

        // -------- Flicker / redraw throttling ------------------------------------------

        /// <summary>
        /// Tracks the last observed StatusHub.Version that we drew. If different,
        /// there are new status lines to paint (tail changed).
        /// </summary>
        private int _lastStatusVersion = -1;

        /// <summary>
        /// Simple wall-clock throttle for redraws. If the last redraw happened too
        /// recently, we skip until MinRedrawMs has elapsed. Helps avoid flicker
        /// when many short log lines arrive in quick succession.
        /// </summary>
        private readonly Stopwatch _throttle = new();

        /// <summary>
        /// The minimal interval (in ms) between redraws (≈12.5 FPS). Raising this
        /// lowers CPU use/flicker at the cost of UI responsiveness.
        /// </summary>
        private const int MinRedrawMs = 80;

        /// <summary>
        /// Epoch for blink timing. We use Environment.TickCount modulo a period
        /// in <see cref="DrawGlobalHeader"/> to flash the incoming-call background.
        /// </summary>
        private long _blinkEpoch = Environment.TickCount;

        // ------------------------------------------------------------------------------

        public ConsoleUi(StatusHub statusHub)
        {
            _status = statusHub ?? throw new ArgumentNullException(nameof(statusHub));
        }

        // ------------------------------------------------------------------------------

        /// <summary>
        /// Pushes a page onto the navigation stack and forces a redraw.
        /// </summary>
        public void Push(IPage page)
        {
            if (page == null) return;
            _navStack.Push(page);
            Redraw(force: true);
        }

        /// <summary>
        /// Pops the top page (if any) and forces a redraw. If no pages remain,
        /// the UI will show a “No page” message until an exit occurs.
        /// </summary>
        public void Pop()
        {
            if (_navStack.Count > 0)
                _navStack.Pop();
            Redraw(force: true);
        }

        /// <summary>
        /// Replaces the current top page with the provided one. Equivalent to Pop+Push.
        /// </summary>
        public void ReplaceTop(IPage page)
        {
            if (_navStack.Count > 0)
                _navStack.Pop();
            Push(page);
        }

        /// <summary>
        /// Returns the topmost page or null if the stack is empty.
        /// </summary>
        public IPage? Current => _navStack.Count > 0 ? _navStack.Peek() : null;

        /// <summary>
        /// Asks the outer loop (Run) to terminate on its next iteration.
        /// </summary>
        public void RequestExit() => _exitRequested = true;

        // ------------------------------------------------------------------------------

        /// <summary>
        /// Enters the main UI loop:
        ///  • Reads key input and delegates it to the current page (non-blocking).
        ///  • Redraws on navigation changes, requested redraws, or status updates.
        ///  • Sleeps briefly between iterations to keep CPU usage reasonable.
        ///
        /// Non-blocking key handling ensures that status updates and passive refresh
        /// signals are noticed even if the user doesn’t press keys.
        /// </summary>
        public void Run()
        {
            Console.Clear();
            _throttle.Start();
            Redraw(force: true);

            while (!_exitRequested)
            {
                // 1) Process key input if available. Do not block: allows redraws
                //    driven by status changes or passive refresh to proceed smoothly.
                if (Console.KeyAvailable)
                {
                    var keyInfo = Console.ReadKey(intercept: true);

                    // Try current page first; it can request Pop/Exit/Redraw.
                    if (Current?.HandleKey(keyInfo, out var nav) == true)
                    {
                        if (nav == PageNav.ExitApp) { RequestExit(); continue; }
                        if (nav == PageNav.Pop) { Pop(); continue; }
                        if (nav == PageNav.Redraw) { Redraw(force: true); continue; }
                        // Future: other nav actions (PushSubPage, Replace, etc.)
                    }
                    else
                    {
                        // --- Global call controls (work on ANY page) -------------------
                        // These shortcuts allow the operator to answer/reject/hang-up
                        // without navigating away from, say, the SMS page.
                        var session = TryGetSession();
                        var phone = ControllerLocator.Phone;

                        if (session != null && phone != null)
                        {
                            if (session.IsRinging && keyInfo.Key == ConsoleKey.A)
                            {
                                UiTaskRunner.Run(_status, "Answering…", async () => await phone.AnswerAsync());
                                Redraw(force: true);
                                continue;
                            }
                            if (session.IsRinging && keyInfo.Key == ConsoleKey.R)
                            {
                                UiTaskRunner.Run(_status, "Rejecting…", async () => await phone.HangUpAsync());
                                Redraw(force: true);
                                continue;
                            }
                            if (session.InCall && keyInfo.Key == ConsoleKey.H)
                            {
                                UiTaskRunner.Run(_status, "Hanging up…", async () => await phone.HangUpAsync());
                                Redraw(force: true);
                                continue;
                            }
                        }
                    }
                }

                // 2) Redraw if there is new status text or if the page opts into
                //    passive refresh (e.g., animations/timers).
                bool needsStatusRedraw = _status.Version != _lastStatusVersion;
                bool wantsPassive = Current?.WantsPassiveRefresh == true;

                if (needsStatusRedraw || wantsPassive)
                    Redraw(force: false);

                // 3) Be gentle with the CPU: a short sleep maintains responsiveness
                //    while avoiding a tight busy loop. 15–25ms works well for console apps.
                Thread.Sleep(20);
            }
        }

        // ------------------------------------------------------------------------------

        /// <summary>
        /// Back-compat convenience overload; calls Redraw(force:false).
        /// </summary>
        public void Redraw() => Redraw(force: false);

        /// <summary>
        /// Renders the full frame (page content + separator + status area).
        /// A minimal redraw interval and a draw lock reduce flicker and tearing.
        /// </summary>
        /// <param name="force">
        /// If true, bypasses the redraw throttle (used after navigation changes
        /// where immediate visual feedback is preferred).
        /// </param>
        public void Redraw(bool force)
        {
            // Throttle high-frequency redraw attempts unless explicitly forced.
            if (!force && _throttle.ElapsedMilliseconds < MinRedrawMs) return;
            _throttle.Restart();

            lock (_drawLock)
            {
                // Compute layout metrics. Clamp to sane minimums so pages can rely on
                // at least 80×24 for baseline rendering despite rapid window resizes.
                var width = Math.Max(80, Console.WindowWidth);
                var height = Math.Max(24, Console.WindowHeight);

                // Reset cursor/attributes and clear the frame to avoid artifacts.
                Console.SetCursorPosition(0, 0);
                Console.ResetColor();
                Console.Clear();

                // Determine how much vertical real estate belongs to the page vs. status.
                var page = Current;
                bool hideStatus = page?.HideStatusArea ?? false; // e.g., LogViewer wants full screen
                int statusHeight = hideStatus ? 0 : (int)Math.Max(6, Math.Round(height * StatusAreaRatio));
                int contentHeight = Math.Max(1, height - statusHeight);

                // Draw page content first; global header/softkeys are painted afterwards,
                // guaranteeing they remain visible across all pages.
                if (page != null)
                {
                    try { page.Draw(width, contentHeight); }
                    catch (Exception ex)
                    {
                        // Draw should never crash the UI; surface to status instead.
                        _status.Add($"[UI] Page draw error: {ex.Message}");
                    }
                }
                else
                {
                    WriteClamped(0, 0, width, contentHeight,
                        "No page available. Press ESC to exit.");
                }

                // Paint global header/softkeys (rows 1–2). We draw them *after* the page to
                // avoid duplication/overlap even if a page also prints top banners.
                try { DrawGlobalHeader(width); } catch { /* non-fatal adornment */ }

                // Draw bottom status area (tail log), unless current page asked to hide it.
                if (!(Current is PageBase pb && pb.HideStatusArea))
                {
                    var sep = new string('─', width);
                    WriteAt(0, contentHeight, sep);
                    DrawStatusArea(width, statusHeight);
                }
                else
                {
                    // If hidden, explicitly clear rows below the content to prevent stale lines
                    // when toggling between pages of different heights.
                    for (int y = contentHeight; y < height; y++)
                        WriteAt(0, y, new string(' ', width));
                }

                // Record the status version we rendered to detect future changes.
                _lastStatusVersion = _status.Version;
            }
        }

        /// <summary>
        /// Paints the status header and the tail of most recent status lines.
        /// </summary>
        private void DrawStatusArea(int width, int height)
        {
            // How many log lines can we show (minus the 1-line header)?
            var tail = _status.GetTail(height - 1);

            // Top of the status area (relative to the full window height).
            int top = Console.WindowHeight - height;

            // 1-line header with instance ID (useful when multiple builds are tested).
            WriteAt(0, top, PadRight($" Status — {InstanceInfo.InstanceId} ", width, ' '));

            // Draw the tail lines; we clamp each to width to prevent console wrap.
            int y = top + 1;
            foreach (var line in tail)
            {
                WriteClamped(0, y++, width, 1, line);
                if (y >= Console.WindowHeight) break; // Stay within bounds during resizes.
            }
        }

        /// <summary>
        /// Draws a one-line global ribbon with Modem/Incoming/SMS indicators (row 1),
        /// plus “softkey” hints (row 2) for answering/rejecting/hanging up.
        /// Includes a blinking background for ringing calls to attract attention.
        /// </summary>
        private void DrawGlobalHeader(int width)
        {
            var session = TryGetSession();
            string modem = session?.ModemReady ?? false ? "Ready" : "Not Ready";
            string incoming = string.IsNullOrWhiteSpace(session?.IncomingCallerId) ? "—" : session!.IncomingCallerId!;
            string sms = session?.NewSmsIndicator ?? false ? "NEW" : "—";

            // Compose segments; we’ll trim the middle if necessary to fit the window.
            string left = $" Modem: {modem} ";
            string mid = $" Incoming: {incoming} ";
            string right = $" SMS: {sms} ";

            string line = left + mid + right;
            if (line.Length > width)
            {
                // Keep left/right intact; compress middle segment first.
                int spare = width - (left.Length + right.Length);
                if (spare < 8) spare = 8; // leave some visibility for “Incoming”
                if (mid.Length > spare) mid = mid[..spare];
                line = left + mid + right;
                if (line.Length > width) line = line[..width]; // last resort
            }

            // Clear row 1 to avoid leftovers from previous frames, then write segments.
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.BackgroundColor = ConsoleColor.Black;
            WriteAt(0, 1, new string(' ', width));
            WriteAt(0, 1, left);

            // Middle segment may blink (DarkYellow background) while ringing.
            int midX = left.Length;
            bool blinkOn = (Environment.TickCount - _blinkEpoch) / 450 % 2 == 0; // ~2.2 Hz blink
            if ((session?.IsRinging ?? false) && blinkOn)
            {
                Console.BackgroundColor = ConsoleColor.DarkYellow;
                Console.ForegroundColor = ConsoleColor.Black;
            }
            else
            {
                Console.BackgroundColor = ConsoleColor.Black;
                Console.ForegroundColor = ConsoleColor.White;
            }
            WriteAt(midX, 1, mid);

            // Right segment (SMS indicator).
            int rightX = midX + mid.Length;
            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.Gray;
            WriteAt(rightX, 1, right);

            Console.ResetColor();

            // Softkeys (row 2), aligned right; non-destructive to page content area.
            DrawGlobalSoftkeys(width, session);
        }

        /// <summary>
        /// Renders context-dependent softkeys:
        ///   • Ringing: [A] Answer / [R] Reject
        ///   • In call: [H] Hang Up
        /// Drawn on row 2, right-aligned, to minimize collisions with page hints.
        /// </summary>
        private void DrawGlobalSoftkeys(int width, AppSession? session)
        {
            if (session == null) return;

            string keys =
                session.IsRinging ? " [A] Answer   [R] Reject " :
                session.InCall ? " [H] Hang Up " :
                                   string.Empty;

            if (string.IsNullOrEmpty(keys)) return;

            int x = Math.Max(0, width - keys.Length - 1);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.BackgroundColor = ConsoleColor.Black;
            WriteAt(x, 2, keys);
            Console.ResetColor();
        }

        // --------------------------------------------------------------------------
        // Console write helpers (defensive against small/fast-resizing windows)
        // --------------------------------------------------------------------------

        /// <summary>
        /// Writes text at an exact location (x,y). Safely no-ops if y is off-screen.
        /// Catches SetCursorPosition exceptions that can occur during rapid resizes.
        /// </summary>
        private static void WriteAt(int x, int y, string text)
        {
            if (y < 0 || y >= Console.WindowHeight) return;
            try
            {
                Console.SetCursorPosition(x, y);
                Console.Write(text);
            }
            catch
            {
                // Ignore drawing failures due to race with a console resize.
            }
        }

        /// <summary>
        /// Writes a single clamped and padded line at (x,y), then clears the remaining
        /// <paramref name="height"/> - 1 lines beneath it to ensure a clean rectangular
        /// area. This prevents “ghost” characters when the new content is shorter
        /// than the previous frame.
        /// </summary>
        private static void WriteClamped(int x, int y, int width, int height, string text)
        {
            if (height <= 0) return;
            if (y < 0 || y >= Console.WindowHeight) return;

            string line = text ?? string.Empty;

            // Clamp to visible width to avoid console auto-wrap artifacts.
            if (line.Length > width) line = line[..width];

            // Pad to full width to overwrite any leftover characters on that row.
            line = PadRight(line, width, ' ');
            WriteAt(x, y, line);

            // Clear the rest of the rectangular block if requested.
            for (int i = 1; i < height; i++)
                WriteAt(x, y + i, new string(' ', width));
        }

        /// <summary>
        /// Right-pads a string with the specified character up to totalWidth.
        /// Returns the original string if already wide enough.
        /// </summary>
        private static string PadRight(string s, int totalWidth, char c)
            => s.Length >= totalWidth ? s : s + new string(c, totalWidth - s.Length);

        /// <summary>
        /// Helper to retrieve the current AppSession from the active page without
        /// changing interface signatures. Returns null if the current page is not
        /// a PageBase derivative (e.g., during early initialization).
        /// </summary>
        private AppSession? TryGetSession()
            => (Current as PageBase)?.AppSession;
    }
}
