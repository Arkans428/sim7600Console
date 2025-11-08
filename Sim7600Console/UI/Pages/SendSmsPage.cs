// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: SendSmsPage.cs
// Description:
//   Provides an interactive UI page for composing and sending SMS messages.
//   Users can toggle between recipient ("To") and message ("Body") fields,
//   send with Ctrl+Enter, or exit with ESC. Input fields are handled via
//   StringBuilder buffers to simplify incremental editing.
//
//   New in this revision:
//     • Overloaded constructor allows pre-filling "To" and an optional body seed.
//     • When pre-filled, focus automatically starts on Body so the user can
//       immediately type a reply without additional key presses.
// ============================================================================

using System.Text;

namespace Sim7600Console.UI.Pages
{
    /// <summary>
    /// Console UI page that enables composing a new SMS message.
    /// This page is responsible only for collecting user input and
    /// delegating the actual send operation to <see cref="PhoneController"/>.
    /// </summary>
    public sealed class SendSmsPage : PageBase
    {
        // --- Input field state ---

        /// <summary>
        /// Destination phone number buffer (e.g., "+15551234567").
        /// Uses StringBuilder to efficiently support character-by-character edits.
        /// </summary>
        private readonly StringBuilder _to = new();

        /// <summary>
        /// Message body buffer (text-mode SMS).
        /// </summary>
        private readonly StringBuilder _body = new();

        /// <summary>
        /// Tracks which field the user is currently editing.
        /// true  = editing "To" (recipient)
        /// false = editing "Body" (message text)
        /// </summary>
        private bool _editingTo = true;

        /// <summary>
        /// Default constructor for a fresh blank message composition.
        /// </summary>
        public SendSmsPage(ConsoleUi ui, AppSession session) : base(ui, session)
        {
            Session.Status.Add("Compose SMS page opened.");
        }

        /// <summary>
        /// Overload that pre-fills the "To" field (and optionally the body).
        /// Used for reply or forward workflows.
        /// 
        /// Parameters:
        ///  - toPrefill : Recipient number to seed in the "To" field.
        ///  - bodySeed  : Optional initial message text (e.g., "Re: ... ").
        /// 
        /// The focus automatically starts on the Body field so the operator
        /// can immediately start typing the reply.
        /// </summary>
        public SendSmsPage(ConsoleUi ui, AppSession session, string toPrefill, string? bodySeed = null)
            : base(ui, session)
        {
            if (!string.IsNullOrWhiteSpace(toPrefill))
                _to.Append(toPrefill.Trim());

            if (!string.IsNullOrEmpty(bodySeed))
                _body.Append(bodySeed);

            _editingTo = false; // Jump focus to Body for immediate typing

            Session.Status.Add($"Compose SMS page opened (prefilled To={toPrefill}).");
        }

        /// <summary>
        /// Renders the UI for composing the SMS message.
        /// Displays both fields ("To" and "Body") and highlights the active field
        /// with a "> " prefix. Provides a small command legend above for navigation.
        /// </summary>
        public override void Draw(int width, int height)
        {
            DrawTitleRow(width, Header("Compose SMS"));

            int row = 2;
            SafeWrite(0, row++, "ESC = Back | TAB = Swap To/Body | CTRL+ENTER = Send");
            SafeWrite(0, row++, "");

            // Show editable fields; prefix indicates which one is active.
            SafeWrite(0, row++, (_editingTo ? "> " : "  ") + $"To:   {_to}");
            SafeWrite(0, row++, (_editingTo ? "  " : "> ") + $"Body: {_body}");
        }

        /// <summary>
        /// Handles all key input events: field switching, character entry,
        /// deletion, and triggering of send or exit actions.
        /// </summary>
        public override bool HandleKey(ConsoleKeyInfo key, out PageNav nav)
        {
            nav = PageNav.None;

            // --- Navigation: ESC exits without sending ---
            if (key.Key == ConsoleKey.Escape)
            {
                nav = PageNav.Pop; // Return to previous page
                return true;
            }

            // --- Field switching: toggle between To and Body using TAB ---
            if (key.Key == ConsoleKey.Tab)
            {
                _editingTo = !_editingTo;
                nav = PageNav.Redraw;
                return true;
            }

            // --- Send action: Ctrl+Enter ---
            // ENTER alone does nothing (reserved for possible future newline support in Body)
            if (key.Key == ConsoleKey.Enter &&
                (key.Modifiers & ConsoleModifiers.Control) == ConsoleModifiers.Control)
            {
                // Fire-and-forget: actual async execution handled safely
                _ = SendAsync();
                nav = PageNav.Redraw;
                return true;
            }

            // --- Text editing section ---
            // Target the current active field (To or Body)
            var target = _editingTo ? _to : _body;

            // Handle Backspace: remove last character if present
            if (key.Key == ConsoleKey.Backspace)
            {
                if (target.Length > 0)
                    target.Remove(target.Length - 1, 1);

                nav = PageNav.Redraw;
                return true;
            }

            // Append any printable (non-control) character into the active buffer
            char c = key.KeyChar;
            if (!char.IsControl(c))
            {
                target.Append(c);
                nav = PageNav.Redraw;
                return true;
            }

            return false; // Unhandled key — ignored
        }

        /// <summary>
        /// Validates user input and attempts to send the composed SMS
        /// through <see cref="PhoneController.SendSmsAsync"/>. Includes
        /// complete safety checks for modem state and controller presence.
        /// On success, automatically returns to the SMS list page if it
        /// was the previous active page.
        /// </summary>
        private async Task SendAsync()
        {
            var number = _to.ToString().Trim();
            var text = _body.ToString();

            // --- Validation ---
            if (string.IsNullOrWhiteSpace(number))
            {
                Session.Status.Add("Compose: Number is empty.");
                return;
            }

            if (!Session.ModemReady)
            {
                Session.Status.Add("Compose: Modem not ready.");
                return;
            }

            // Get shared PhoneController instance
            var pc = ControllerLocator.Phone;
            if (pc == null)
            {
                Session.Status.Add("Compose: PhoneController not available.");
                return;
            }

            try
            {
                // Attempt to send SMS (async call to modem layer)
                await pc.SendSmsAsync(number, text);

                Session.Status.Add($"Compose: SMS sent to {number}.");

                // UX enhancement: Return to message list if user came from it
                if (Previous is SmsMenuPage)
                {
                    Ui.Pop(); // Go back one page
                }
            }
            catch (Exception ex)
            {
                // Any modem or serial error is reported in the Status area
                Session.Status.Add($"Compose: Send failed: {ex.Message}");
            }
            finally
            {
                // Force a redraw to clear inputs or refresh page state
                Ui.Redraw();
            }
        }
    }
}
