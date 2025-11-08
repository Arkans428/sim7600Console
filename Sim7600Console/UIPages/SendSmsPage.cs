// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: SendSmsPage.cs
// Description:
//   Compose-and-send page for SMS. Supports toggling between "To" and "Body"
//   fields (TAB), sending with CTRL+ENTER, and backing out with ESC.
//   The actual sending is delegated to PhoneController.
//
//   New in this revision:
//     • Overloaded constructor allows pre-filling "To" and an optional body seed.
//     • When pre-filled, focus starts on Body so the user can type immediately.
// ============================================================================

using System;
using System.Text;
using System.Threading.Tasks;

namespace Sim7600Console.UIPages
{
    public sealed class SendSmsPage : PageBase
    {
        // Destination phone number buffer (e.g., +15551234567)
        private readonly StringBuilder _to = new();

        // Message body buffer (text-mode SMS)
        private readonly StringBuilder _body = new();

        // Which field the user is editing: true -> To, false -> Body
        private bool _editingTo = true;

        public SendSmsPage(ConsoleUi ui, AppSession session) : base(ui, session)
        {
            Session.Status.Add("Compose SMS page opened.");
        }

        /// <summary>
        /// Prefill-friendly overload:
        ///   - toPrefill: number to seed into the "To" field
        ///   - bodySeed : optional initial text for the body (e.g., "Re: ... ")
        /// Focus is set to Body so the operator can start typing the reply instantly.
        /// </summary>
        public SendSmsPage(ConsoleUi ui, AppSession session, string toPrefill, string? bodySeed = null) : base(ui, session)
        {
            if (!string.IsNullOrWhiteSpace(toPrefill)) _to.Append(toPrefill.Trim());
            if (!string.IsNullOrEmpty(bodySeed)) _body.Append(bodySeed);
            _editingTo = false; // jump focus to Body
            Session.Status.Add($"Compose SMS page opened (prefilled To={toPrefill}).");
        }

        /// <summary>
        /// Renders the compose screen: legend, "To" field, "Body" field, and a small
        /// readiness summary. The active field gets a "> " prefix to aid visibility.
        /// </summary>
        public override void Draw(int width, int height)
        {
            DrawTitleRow(width, Header("Compose SMS"));

            int row = 2;
            SafeWrite(0, row++, "ESC = Back | TAB = Swap To/Body | CTRL+ENTER = Send");
            SafeWrite(0, row++, "");

            SafeWrite(0, row++, (_editingTo ? "> " : "  ") + $"To:   {_to}");
            SafeWrite(0, row++, (_editingTo ? "  " : "> ") + $"Body: {_body}");
        }

        /// <summary>
        /// Handles editing for both fields, swapping fields, sending, and back navigation.
        /// </summary>
        public override bool HandleKey(ConsoleKeyInfo key, out PageNav nav)
        {
            nav = PageNav.None;

            // Leave compose page without sending
            if (key.Key == ConsoleKey.Escape) { nav = PageNav.Pop; return true; }

            // Toggle between "To" and "Body" input fields
            if (key.Key == ConsoleKey.Tab)
            {
                _editingTo = !_editingTo;
                nav = PageNav.Redraw;
                return true;
            }

            // Send with Ctrl+Enter so ENTER remains free for newlines (if later desired)
            if (key.Key == ConsoleKey.Enter && (key.Modifiers & ConsoleModifiers.Control) == ConsoleModifiers.Control)
            {
                _ = SendAsync(); // fire-and-forget (includes status logging and redraw)
                nav = PageNav.Redraw;
                return true;
            }

            // Edit the active buffer
            var target = _editingTo ? _to : _body;

            if (key.Key == ConsoleKey.Backspace)
            {
                if (target.Length > 0) target.Remove(target.Length - 1, 1);
                nav = PageNav.Redraw;
                return true;
            }

            // Append any printable (non-control) character into the active field.
            char c = key.KeyChar;
            if (!char.IsControl(c))
            {
                target.Append(c);
                nav = PageNav.Redraw;
                return true;
            }

            return false; // Unhandled key
        }

        /// <summary>
        /// Validates fields and sends the SMS using PhoneController. On success,
        /// returns to the list page (if that was the previous page).
        /// </summary>
        private async Task SendAsync()
        {
            var number = _to.ToString().Trim();
            var text = _body.ToString();

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

            var pc = ControllerLocator.Phone;
            if (pc == null)
            {
                Session.Status.Add("Compose: PhoneController not available.");
                return;
            }

            try
            {
                await pc.SendSmsAsync(number, text);
                Session.Status.Add($"Compose: SMS sent to {number}.");

                // Optional UX: Return to SMS list after sending successfully.
                if (Previous is SmsMenuPage)
                {
                    Ui.Pop(); // go back to list
                }
            }
            catch (Exception ex)
            {
                Session.Status.Add($"Compose: Send failed: {ex.Message}");
            }
            finally
            {
                Ui.Redraw(); // Ensure the page content reflects any changes
            }
        }
    }
}
