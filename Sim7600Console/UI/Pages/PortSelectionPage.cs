// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: PortSelectionPage.cs
// Project: SIM7600G-H Console Dialer (C# .NET 8)
// ----------------------------------------------------------------------------
// PURPOSE
//   Interactive “wizard” page to bind the application to two serial ports:
//
//     Step 1/3 — Select AT Port
//     Step 2/3 — Select Audio Port
//     Step 3/3 — Confirm & Initialize
//
//   The page is intentionally *paged* instead of scrolly text. Only the
//   currently ACTIVE list is scrollable; the other step shows a single
//   “Selected: …” summary line. The confirmation block is *pinned* to the
//   bottom of the page’s content area so it never scrolls off-screen.
//
// UX & KEY BINDINGS
//   • ESC            → Exit application
//   • F5             → Rescan COM ports
//   • 1              → Jump to Step 1 (AT selection)
//   • 2              → Jump to Step 2 (Audio selection)
//   • ↑ / ↓          → Move the cursor *in the active list only*
//   • ENTER (Step 1) → Select highlighted AT port and advance to Step 2
//   • ENTER (Step 2) → Select highlighted Audio port and advance to Confirm
//   • ENTER (Confirm)→ Initialize the PhoneController and navigate to Main Menu
//
// LAYOUT & FLICKER
//   ConsoleUi handles throttled redraws for flicker-free updates. This page
//   draws within the “content” region; the status area is below and is owned
//   by ConsoleUi. We leave the last few rows for the confirm block.
//
// THREADING
//   The final initialization (creating PhoneController and opening ports)
//   is performed asynchronously via UiTaskRunner so the UI remains responsive
//   and the status area keeps streaming logs in real time.
//
// NOTES
//   • COM enumeration is via SerialPort.GetPortNames() and sorted for stability.
//   • Cursor always remains in bounds (ClampCursorToPorts).
//   • We log all important actions to StatusHub for operator traceability.
// ============================================================================

using System.IO.Ports;

namespace Sim7600Console.UI.Pages
{
    /// <summary>
    /// Port selection wizard that cleanly guides the user through selecting
    /// the AT and Audio serial ports and then confirming to initialize the
    /// modem + audio layers.
    /// </summary>
    public sealed class PortSelectionPage : PageBase
    {
        // Snapshot of available COM ports (refreshed on load and on F5).
        private string[] _ports = Array.Empty<string>();

        // Current cursor into _ports for the active selection step only.
        private int _cursor = 0;

        // Tracks which part of the 3-step flow the page is currently on.
        private SelectionPhase _phase = SelectionPhase.ChooseAt;

        // The three wizard phases in order.
        private enum SelectionPhase { ChooseAt, ChooseAudio, Confirm }

        public PortSelectionPage(ConsoleUi ui, AppSession session) : base(ui, session)
        {
            // Initial device inventory; user can press F5 to rescan.
            RefreshPorts();

            // First-time operator guidance appears in the Status area.
            Session.Status.Add("PortSelection: Choose your AT and Audio COM ports (ENTER to select).");
        }

        /// <summary>
        /// Draws the complete page content area (without the status log).
        /// The Confirm block is "pinned" at the bottom of the available content
        /// height by reserving rows in advance, so it is always visible.
        /// </summary>
        public override void Draw(int width, int height)
        {
            // Centered page header with instance ID (handled by helper).
            DrawTitleRow(width, Header("Port Selection"));

            int y = 2;

            // Global control legend.
            SafeWrite(0, y++, "ESC = Exit | F5 = Rescan | 1 = AT step | 2 = Audio step | ENTER = Select/Confirm");
            SafeWrite(0, y++, "");

            // We reserve space for the confirmation block so it does not scroll away.
            // Keep 3 lines free (title + message), computed against current content height.
            int confirmBlockRows = 3;
            int usableRows = Math.Max(5, height - (y + confirmBlockRows)); // rows left for the active list

            // -------------------- Step 1: AT selection --------------------
            SafeWrite(0, y++, "Step 1/3 — Select AT Port:");

            if (_phase == SelectionPhase.ChooseAt)
            {
                // Active list is scrollable and paged. We pass the current selection
                // so we can tag the already-selected item with “ [Selected] ” if any.
                y = DrawActivePortList(y, width, usableRows, Session.AtComPort);
            }
            else
            {
                // If we are not actively choosing AT, show only a one-line summary.
                string atLine = Session.AtComPort != null ? $"Selected: {Session.AtComPort}" : "(not selected)";
                SafeWrite(0, y++, "  " + atLine);
            }

            SafeWrite(0, y++, ""); // spacer between steps

            // ------------------- Step 2: Audio selection -------------------
            SafeWrite(0, y++, "Step 2/3 — Select Audio Port:");

            if (_phase == SelectionPhase.ChooseAudio)
            {
                // Active list is scrollable and paged for the audio step.
                y = DrawActivePortList(y, width, usableRows, Session.AudioComPort);
            }
            else
            {
                // If we are not actively choosing Audio, show only a one-line summary.
                string aLine = Session.AudioComPort != null ? $"Selected: {Session.AudioComPort}" : "(not selected)";
                SafeWrite(0, y++, "  " + aLine);
            }

            // Ensure the confirm block stays at the bottom of the content region.
            if (y < height - confirmBlockRows) y = height - confirmBlockRows;

            // ------------------------- Step 3: Confirm -------------------------
            SafeWrite(0, y++, "Step 3/3 — Confirm:");
            string confirm = Session.AtComPort != null && Session.AudioComPort != null
                ? $"Press ENTER to continue  (AT={Session.AtComPort}, Audio={Session.AudioComPort})"
                : $"Select both ports to continue…";
            SafeWrite(0, y++, confirm);
        }

        /// <summary>
        /// Handles navigation keys and commit actions. Only the active list reacts
        /// to the arrow keys; ENTER commits the selection for the current phase
        /// or runs initialization on the Confirm step.
        /// </summary>
        public override bool HandleKey(ConsoleKeyInfo key, out PageNav nav)
        {
            nav = PageNav.None;

            switch (key.Key)
            {
                // Re-enumerate COM ports; keep cursor in bounds.
                case ConsoleKey.F5:
                    RefreshPorts();
                    Session.Status.Add("PortSelection: Rescanned serial ports.");
                    nav = PageNav.Redraw;
                    return true;

                // Exit to OS — this is the initial page; safe to end here.
                case ConsoleKey.Escape:
                    nav = PageNav.ExitApp;
                    return true;

                // Jump directly to a step for convenience.
                case ConsoleKey.D1:
                    _phase = SelectionPhase.ChooseAt;
                    ClampCursorToPorts();
                    nav = PageNav.Redraw;
                    return true;

                case ConsoleKey.D2:
                    _phase = SelectionPhase.ChooseAudio;
                    ClampCursorToPorts();
                    nav = PageNav.Redraw;
                    return true;

                // Move the cursor only when a list is active (ChooseAt/ChooseAudio).
                case ConsoleKey.UpArrow:
                    if (_phase != SelectionPhase.Confirm)
                    {
                        _cursor = Math.Max(0, _cursor - 1);
                        nav = PageNav.Redraw;
                        return true;
                    }
                    return false;

                case ConsoleKey.DownArrow:
                    if (_phase != SelectionPhase.Confirm)
                    {
                        _cursor = Math.Min(Math.Max(0, _ports.Length - 1), _cursor + 1);
                        nav = PageNav.Redraw;
                        return true;
                    }
                    return false;

                // ENTER either selects the highlighted item (Step 1/2) or
                // performs the initialization (Confirm step).
                case ConsoleKey.Enter:
                    return OnEnter(out nav);

                default:
                    return false;
            }
        }

        /// <summary>
        /// Centralized handler for ENTER across the three phases:
        ///   • ChooseAt    → capture selected AT port, advance to ChooseAudio
        ///   • ChooseAudio → capture selected Audio port, advance to Confirm
        ///   • Confirm     → if both selected, initialize controller async
        /// </summary>
        private bool OnEnter(out PageNav nav)
        {
            nav = PageNav.None;

            // --- Step 1: Picking the AT port ---
            if (_phase == SelectionPhase.ChooseAt)
            {
                if (_ports.Length == 0)
                {
                    Session.Status.Add("No COM ports found. Connect the modem and press F5.");
                    return true;
                }

                // Pick the current cursor line; Math.Clamp guards against races
                // where ports list changes between draw and keypress.
                string current = _ports[Math.Clamp(_cursor, 0, Math.Max(0, _ports.Length - 1))];
                Session.AtComPort = current;
                Session.Status.Add($"Selected AT port: {current}");

                // Advance wizard.
                _phase = SelectionPhase.ChooseAudio;
                ClampCursorToPorts();
                nav = PageNav.Redraw;
                return true;
            }

            // --- Step 2: Picking the Audio port ---
            if (_phase == SelectionPhase.ChooseAudio)
            {
                if (_ports.Length == 0)
                {
                    Session.Status.Add("No COM ports found. Connect the modem and press F5.");
                    return true;
                }

                string current = _ports[Math.Clamp(_cursor, 0, Math.Max(0, _ports.Length - 1))];
                Session.AudioComPort = current;
                Session.Status.Add($"Selected Audio port: {current}");

                // Advance to confirmation.
                _phase = SelectionPhase.Confirm;
                nav = PageNav.Redraw;
                return true;
            }

            // --- Step 3: Confirm & Initialize ---
            if (Session.AtComPort == null || Session.AudioComPort == null)
            {
                Session.Status.Add("Please select both AT and Audio ports first (1 and 2).");
                return true;
            }

            // Perform the potentially slow I/O work off the UI thread.
            UiTaskRunner.Run(
                Session.Status,
                startMsg: $"[Init] Bringing modem up on AT={Session.AtComPort}, Audio={Session.AudioComPort}…",
                work: async () =>
                {
                    // Create the controller and publish it for other pages to use.
                    var controller = new PhoneController(Session, Session.AtComPort!, Session.AudioComPort!);
                    ControllerLocator.Register(controller);

                    // Apply a safety timeout so we fail fast if a device is mis-bound.
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    await controller.InitializeAsync(cts.Token).ConfigureAwait(false);

                    // Success signal for operator.
                    Session.Status.Add("[Init] ✅ Initialization successful.");

                    // When a Next page is wired (MainMenuPage), replace this selection page.
                    if (Next != null) Ui.ReplaceTop(Next);

                    // Ask the UI to refresh right away to show the updated state.
                    Ui.Redraw();
                },
                successMsg: null // already logging a success line just above
            );

            nav = PageNav.Redraw;
            return true;
        }

        /// <summary>
        /// Re-enumerates COM ports and keeps the cursor within valid bounds.
        /// Called on page construction and when the user presses F5.
        /// </summary>
        private void RefreshPorts()
        {
            _ports = SerialPort.GetPortNames()
                               .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                               .ToArray();
            ClampCursorToPorts();
        }

        /// <summary>
        /// Ensures the cursor index is valid for the current _ports list.
        /// This avoids out-of-range reads if ports appear/disappear between frames.
        /// </summary>
        private void ClampCursorToPorts()
        {
            _cursor = Math.Min(_cursor, Math.Max(0, _ports.Length - 1));
            if (_cursor < 0) _cursor = 0;
        }

        /// <summary>
        /// Draws the ACTIVE port list within a “window” (page) so it never
        /// overlaps the confirmation block. The currently highlighted entry
        /// shows a “▶” marker; previously chosen port displays “ [Selected] ”.
        /// </summary>
        /// <param name="startRow">Row to start drawing the list.</param>
        /// <param name="width">Console width for clamping long lines.</param>
        /// <param name="maxRows">Maximum list rows allowed (content budget).</param>
        /// <param name="selectedPort">Previously selected port for this step (may be null).</param>
        /// <returns>The next row index after the rendered list.</returns>
        private int DrawActivePortList(int startRow, int width, int maxRows, string? selectedPort)
        {
            if (_ports.Length == 0)
            {
                // Friendly hint when nothing is found.
                SafeWrite(0, startRow++, "  (No COM ports found)");
                return startRow;
            }

            // Compute a “window” around the cursor so the selection stays visible
            // and scrolling is intuitive. We ensure at least 3 rows are shown.
            int windowSize = Math.Max(3, maxRows);
            int first = Math.Clamp(_cursor - windowSize / 2, 0, Math.Max(0, _ports.Length - windowSize));
            int lastExclusive = Math.Min(_ports.Length, first + windowSize);

            for (int i = first; i < lastExclusive; i++)
            {
                bool isCursor = i == _cursor;
                string mark = isCursor ? "▶" : " ";
                string sel = _ports[i] == selectedPort ? " [Selected]" : "";
                string line = $"{mark} {_ports[i]}{sel}";

                // Clamp line width to avoid accidental wrapping on narrow terminals.
                if (line.Length > width) line = line[..width];

                SafeWrite(0, startRow++, line);
            }

            // Ellipses help indicate that additional items exist off-screen.
            if (first > 0)
                SafeWrite(0, startRow++, "  …");
            if (lastExclusive < _ports.Length)
                SafeWrite(0, startRow++, "  …");

            return startRow;
        }
    }
}
