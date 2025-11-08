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
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Sim7600Console.UI
{
    /// <summary>
    /// Console layout & page host that manages a navigation stack and a persistent
    /// status area. Redraws are throttled to reduce flicker.
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
        /// </summary>
        private const double StatusAreaRatio = 0.33;

        /// <summary>
        /// Ensures only one thread draws at a time (prevents interleaving artifacts).
        /// </summary>
        private readonly object _drawLock = new();

        // -------- Flicker / redraw throttling ------------------------------------------

        /// <summary>
        /// Tracks the last observed StatusHub.Version that we drew. If different,
        /// there are new status lines to paint.
        /// </summary>
        private int _lastStatusVersion = -1;

        /// <summary>
        /// Simple wall-clock throttle for redraws. If the last redraw happened too
        /// recently, we skip until MinRedrawMs has elapsed.
        /// </summary>
        private readonly Stopwatch _throttle = new();

        /// <summary>
        /// The minimal interval (in ms) between redraws (≈12.5 FPS). Raising this
        /// value further reduces flicker at the cost of UI responsiveness.
        /// </summary>
        private const int MinRedrawMs = 80;

        private long _blinkEpoch = Environment.TickCount;

        // ------------------------------------------------------------------------------
        // Construction
        // ------------------------------------------------------------------------------

        public ConsoleUi(StatusHub statusHub)
        {
            _status = statusHub ?? throw new ArgumentNullException(nameof(statusHub));
        }

        // ------------------------------------------------------------------------------
        // Navigation
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
        // Main loop
        // ------------------------------------------------------------------------------

        /// <summary>
        /// Enters the main UI loop:
        ///  • Reads key input and delegates it to the current page.
        ///  • Redraws on navigation changes, requested redraws, or status updates.
        ///  • Sleeps briefly between iterations to keep CPU usage reasonable.
        /// </summary>
        public void Run()
        {
            Console.Clear();
            _throttle.Start();
            Redraw(force: true);

            while (!_exitRequested)
            {
                // 1) Process key input if available. We do not block so the loop can
                //    also detect status changes and passive page refreshes.
                if (Console.KeyAvailable)
                {
                    var keyInfo = Console.ReadKey(intercept: true);

                    // IPage.HandleKey returns true if it recognizes the key and sets a PageNav action.
                    if (Current?.HandleKey(keyInfo, out var nav) == true)
                    {
                        if (nav == PageNav.ExitApp) { RequestExit(); continue; }
                        if (nav == PageNav.Pop) { Pop(); continue; }
                        if (nav == PageNav.Redraw) { Redraw(force: true); continue; }
                        // Other PageNav actions can be added here (e.g., PageNav.PushSubPage)
                    }
                    else
                    {
                        // --- NEW: global call keys ---
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

                // 2) Determine if we need a redraw due to new status lines or a page that
                //    wants passive refresh (e.g., a clock).
                bool needsStatusRedraw = _status.Version != _lastStatusVersion;
                bool wantsPassive = Current?.WantsPassiveRefresh == true;

                if (needsStatusRedraw || wantsPassive)
                    Redraw(force: false);

                // 3) Be polite to the CPU. Lower values improve input feel at the cost
                //    of more wakeups. 15–25ms works well for console apps.
                Thread.Sleep(20);
            }
        }

        // ------------------------------------------------------------------------------
        // Redraw helpers
        // ------------------------------------------------------------------------------

        /// <summary>
        /// Back-compat convenience overload; calls Redraw(force:false).
        /// </summary>
        public void Redraw() => Redraw(force: false);

        /// <summary>
        /// Renders the full frame (page content + separator + status area).
        /// A minimal redraw interval and a draw lock reduce flicker and tearing.
        /// </summary>
        /// <param name="force">Ignore throttle if true (e.g., navigation).</param>
        public void Redraw(bool force)
        {
            // Throttle high-frequency redraw attempts unless explicitly forced.
            if (!force && _throttle.ElapsedMilliseconds < MinRedrawMs) return;
            _throttle.Restart();

            lock (_drawLock)
            {
                // Compute layout metrics. We clamp width/height to sane minimums so
                // pages can rely on at least 80x24 for baseline rendering.
                var width = Math.Max(80, Console.WindowWidth);
                var height = Math.Max(24, Console.WindowHeight);
                var statusHeight = (int)Math.Max(6, Math.Round(height * StatusAreaRatio));
                var contentHeight = height - statusHeight;

                // Reset cursor and clear the screen to avoid artifacts from prior frames.
                Console.SetCursorPosition(0, 0);
                Console.ResetColor();
                Console.Clear();

                // Draw the current page (or a fallback message if none exists).
                var page = Current;
                if (page != null)
                {
                    try { page.Draw(width, contentHeight); }
                    catch (Exception ex)
                    {
                        // Never crash the UI for a page draw error; log it to status instead.
                        _status.Add($"[UI] Page draw error: {ex.Message}");
                    }
                }
                else
                {
                    WriteClamped(0, 0, width, contentHeight,
                        "No page available. Press ESC to exit.");
                }

                // --- NEW: global “Modem / Incoming / SMS” ribbon at row 1 ---
                try { DrawGlobalHeader(width); } catch { /* non-fatal */ }

                // Draw the bottom separator and/or Status Area only if the current page allows it.
                if (!(Current is PageBase pb && pb.HideStatusArea))
                {
                    var sep = new string('─', width);
                    WriteAt(0, contentHeight, sep);
                    DrawStatusArea(width, statusHeight);
                }
                else
                {
                    // When hiding, clear the area below contentHeight to avoid ghost lines.
                    for (int y = contentHeight; y < height; y++)
                        WriteAt(0, y, new string(' ', width));
                }

                // Record the version we rendered so we can detect new status later.
                _lastStatusVersion = _status.Version;
            }
        }

        /// <summary>
        /// Paints the status header and the tail of most recent status lines.
        /// </summary>
        private void DrawStatusArea(int width, int height)
        {
            // How many lines of actual log text can we show (minus the header line)?
            var tail = _status.GetTail(height - 1);

            // Top row of the status area (accounting for full window height).
            int top = Console.WindowHeight - height;

            // Render a one-line header that includes the instance ID as a breadcrumb.
            WriteAt(0, top, PadRight($" Status — {InstanceInfo.InstanceId} ", width, ' '));

            // Render tail lines (newest usually at the bottom, depending on StatusHub).
            int y = top + 1;
            foreach (var line in tail)
            {
                WriteClamped(0, y++, width, 1, line);
                if (y >= Console.WindowHeight) break; // Stay in bounds if console was resized.
            }
        }

        private void DrawGlobalHeader(int width)
        {
            var session = TryGetSession();
            string modem = session?.ModemReady ?? false ? "Ready" : "Not Ready";
            string incoming = string.IsNullOrWhiteSpace(session?.IncomingCallerId) ? "—" : session!.IncomingCallerId!;
            string sms = session?.NewSmsIndicator ?? false ? "NEW" : "—";

            // Compose the left+middle+right segments
            string left = $" Modem: {modem} ";
            string mid = $" Incoming: {incoming} ";
            string right = $" SMS: {sms} ";

            // Fit them on one line, trimming middle if needed
            string line = left + mid + right;
            if (line.Length > width)
            {
                // Prefer trimming the middle segment to keep left/right visible
                int spare = width - (left.Length + right.Length);
                if (spare < 8) spare = 8;
                if (mid.Length > spare) mid = mid[..spare];
                line = left + mid + right;
                if (line.Length > width) line = line[..width];
            }

            // Write left part (normal)
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.BackgroundColor = ConsoleColor.Black;
            WriteAt(0, 1, new string(' ', width)); // clear row 1
            WriteAt(0, 1, left);

            // Write middle part with optional blinking background if ringing
            int midX = left.Length;
            bool blinkOn = (Environment.TickCount - _blinkEpoch) / 450 % 2 == 0;
            if ((session?.IsRinging ?? false) && blinkOn)
            {
                Console.BackgroundColor = ConsoleColor.DarkYellow; // blink bg
                Console.ForegroundColor = ConsoleColor.Black;
            }
            else
            {
                Console.BackgroundColor = ConsoleColor.Black;
                Console.ForegroundColor = ConsoleColor.White;
            }
            WriteAt(midX, 1, mid);

            // Write right part (normal)
            int rightX = midX + mid.Length;
            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.Gray;
            WriteAt(rightX, 1, right);

            Console.ResetColor();

            // Also show global softkeys on row 2 (without stealing page real estate)
            DrawGlobalSoftkeys(width, session);
        }

        private void DrawGlobalSoftkeys(int width, AppSession? session)
        {
            if (session == null) return;

            string keys =
                session.IsRinging ? " [A] Answer   [R] Reject " :
                session.InCall ? " [H] Hang Up " :
                                   string.Empty;

            if (string.IsNullOrEmpty(keys)) return;

            // Draw on row 2, aligned to right side so it rarely collides with page hints
            int x = Math.Max(0, width - keys.Length - 1);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.BackgroundColor = ConsoleColor.Black;
            WriteAt(x, 2, keys);
            Console.ResetColor();
        }


        // ------------------------------------------------------------------------------
        // Console write helpers (defensive against small/fast-resizing windows)
        // ------------------------------------------------------------------------------

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
                // Ignore drawing failures due to race with console resize.
            }
        }

        /// <summary>
        /// Writes a single clamped and padded line at (x,y), then clears the remaining
        /// <paramref name="height"/> - 1 lines beneath it to ensure a clean rectangular
        /// area. This is useful for page content rows and prevents “ghost” characters.
        /// </summary>
        private static void WriteClamped(int x, int y, int width, int height, string text)
        {
            if (height <= 0) return;
            if (y < 0 || y >= Console.WindowHeight) return;

            string line = text ?? string.Empty;

            // Clamp to the available width to avoid wrapping.
            if (line.Length > width) line = line[..width];

            // Pad to the full width to paint over leftovers from prior frames.
            line = PadRight(line, width, ' ');
            WriteAt(x, y, line);

            // Clear the remaining rows of this rectangular area.
            for (int i = 1; i < height; i++)
                WriteAt(x, y + i, new string(' ', width));
        }

        /// <summary>
        /// Right-pads a string with the specified character up to totalWidth.
        /// Returns the original string if already wide enough.
        /// </summary>
        private static string PadRight(string s, int totalWidth, char c)
            => s.Length >= totalWidth ? s : s + new string(c, totalWidth - s.Length);

        // Helper to get the AppSession from the active page without changing signatures
        private AppSession? TryGetSession()
            => (Current as PageBase)?.AppSession;

    }
}
