// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: DialerPage.cs
// Project: SIM7600G-H Console Dialer (C# .NET 8)
// ----------------------------------------------------------------------------
// PURPOSE
//   Implements an interactive console-based dialer interface that lets the
//   user enter phone numbers, initiate outgoing calls, answer incoming calls,
//   and hang up active calls. It connects to the underlying telephony logic
//   through the PhoneController class.
//
// UI STRUCTURE
//   The page displays:
//     • Key bindings and usage hints.
//     • Current incoming call information (if any).
//     • A dial entry field where users type digits and symbols.
//     • A footer showing modem readiness and port information.
//
// BEHAVIOR
//   • ENTER → Initiates an outgoing call using the typed number.
//   • A     → Answers an incoming call (if one is active).
//   • H     → Hangs up an active call.
//   • F2    → Clears the dial buffer and resets hint text.
//   • ESC   → Returns to the main menu.
//
// DESIGN DETAILS
//   • The dial string is built dynamically using a StringBuilder buffer so the
//     user can type, delete, and modify digits interactively.
//   • Actions like dialing or hanging up are run asynchronously through
//     UiTaskRunner to prevent the UI from freezing while waiting for the modem.
//   • The page interacts with the shared AppSession for modem status updates
//     and with the ControllerLocator for retrieving the current PhoneController.
//
// DEPENDENCIES
//   • PageBase: Provides SafeWrite(), DrawTitleRow(), and Header() helpers.
//   • AppSession: Provides modem state, port info, and shared StatusHub logging.
//   • ControllerLocator.Phone: Global reference to the active PhoneController.
//   • UiTaskRunner: Runs background async tasks with visible progress messages.
// ============================================================================

using Sim7600Console.UI;
using System;
using System.Text;

namespace Sim7600Console.UI.Pages
{
    /// <summary>
    /// Console page that provides an interactive numeric dial pad for making,
    /// answering, and hanging up voice calls through the PhoneController.
    /// </summary>
    public sealed class DialerPage : PageBase
    {
        // --------------------------------------------------------------------
        // Fields
        // --------------------------------------------------------------------

        /// <summary>
        /// Stores the digits and allowed symbols typed by the user for dialing.
        /// Acts like a dynamic text buffer for the number entry line.
        /// </summary>
        private readonly StringBuilder _numberBuffer = new();

        /// <summary>
        /// Whether to show the user instruction hint below the dial entry line.
        /// Automatically hides once the user starts typing a number.
        /// </summary>
        private bool _showHint = true;

        // --------------------------------------------------------------------
        // Construction
        // --------------------------------------------------------------------

        /// <summary>
        /// Initializes the dialer page, logs its activation to the status hub.
        /// </summary>
        public DialerPage(ConsoleUi ui, AppSession session) : base(ui, session)
        {
            Session.Status.Add("Dialer Menu opened.");
        }

        // --------------------------------------------------------------------
        // Rendering
        // --------------------------------------------------------------------

        /// <summary>
        /// Draws the full dialer page, including:
        ///   - Control instructions
        ///   - Incoming call information
        ///   - Dial entry buffer
        ///   - Hint and modem status line
        /// </summary>
        public override void Draw(int width, int height)
        {
            // Page title at top
            DrawTitleRow(width, Header("Dialing Menu"));

            int row = 2;
            SafeWrite(0, row++, "ESC = Back | ENTER = Dial | A = Answer | H = Hang Up | F2 = Clear");
            SafeWrite(0, row++, "");

            // Display current inbound call state (caller ID if known)
            var inbound = string.IsNullOrEmpty(Session.IncomingCallerId)
                ? "No incoming call."
                : $"Incoming: {Session.IncomingCallerId}  (Press 'A' to answer or 'H' to reject)";
            SafeWrite(0, row++, inbound);
            SafeWrite(0, row++, "");

            // Dial input field (truncate to visible width to prevent wrapping)
            var entry = "Dial: " + _numberBuffer.ToString();
            if (entry.Length > width) entry = entry[^width..];
            SafeWrite(0, row++, entry);

            // Show hint for first-time user or after clearing
            if (_showHint)
                SafeWrite(0, row++, "Type digits (0-9, +, *, #). Press ENTER to start the call.");

            SafeWrite(0, row++, "");
        }

        // --------------------------------------------------------------------
        // Key Handling
        // --------------------------------------------------------------------

        /// <summary>
        /// Processes user key inputs for dialing and call control.
        /// Returns true if the key was handled, along with a PageNav result.
        /// </summary>
        public override bool HandleKey(ConsoleKeyInfo key, out PageNav nav)
        {
            nav = PageNav.None;

            // ESC → Return to main menu
            if (key.Key == ConsoleKey.Escape)
            {
                nav = PageNav.Pop;
                return true;
            }

            // Try to obtain the current PhoneController (may be null if not initialized)
            var pc = ControllerLocator.Phone;

            // If user attempts a call-related action without a controller, log it
            if (pc == null &&
                (key.Key == ConsoleKey.Enter || key.Key == ConsoleKey.A || key.Key == ConsoleKey.H))
            {
                Session.Status.Add("Dialer: PhoneController not available (initialize ports first).");
                nav = PageNav.Redraw;
                return true;
            }

            // ENTER → Place a call using the dial buffer contents
            if (key.Key == ConsoleKey.Enter)
            {
                var number = _numberBuffer.ToString().Trim();
                if (number.Length == 0)
                {
                    Session.Status.Add("Dialer: No number entered.");
                    return true;
                }

                // Run dialing asynchronously with a visible log entry
                UiTaskRunner.Run(Session.Status, $"Dialing {number}…", async () =>
                {
                    await pc!.DialAsync(number);
                });

                _showHint = false;
                nav = PageNav.Redraw;
                return true;
            }

            // A → Answer incoming call
            if (key.Key == ConsoleKey.A)
            {
                UiTaskRunner.Run(Session.Status, "Answering…", async () =>
                    await pc!.AnswerAsync());
                nav = PageNav.Redraw;
                return true;
            }

            // H → Hang up active call
            if (key.Key == ConsoleKey.H)
            {
                UiTaskRunner.Run(Session.Status, "Hanging up…", async () =>
                    await pc!.HangUpAsync());
                nav = PageNav.Redraw;
                return true;
            }

            // F2 → Clear dial buffer (reset input field)
            if (key.Key == ConsoleKey.F2)
            {
                _numberBuffer.Clear();
                _showHint = true;
                nav = PageNav.Redraw;
                return true;
            }

            // Add allowed dialing characters: digits, plus, star, hash
            char c = key.KeyChar;
            if (char.IsDigit(c) || c == '+' || c == '*' || c == '#')
            {
                _numberBuffer.Append(c);
                _showHint = false;
                nav = PageNav.Redraw;
                return true;
            }

            // BACKSPACE → Delete last typed character (simple editing)
            if (key.Key == ConsoleKey.Backspace && _numberBuffer.Length > 0)
            {
                _numberBuffer.Remove(_numberBuffer.Length - 1, 1);
                nav = PageNav.Redraw;
                return true;
            }

            // Key not handled by this page
            return false;
        }
    }
}
