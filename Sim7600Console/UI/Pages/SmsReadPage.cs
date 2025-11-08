// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: SmsReadPage.cs
// Description:
//   Displays a single SMS message in detail (From, Timestamp, Body).
//   On open, it fetches the full body from the modem using PhoneController.
//   Keys:
//     • ESC : Back
//     • D   : Delete (with confirmation)
//     • R   : Reply to sender (opens SendSmsPage with "To" prefilled & focus on Body)
// ============================================================================

using Sim7600Console.SMS;

namespace Sim7600Console.UI.Pages
{
    public sealed class SmsReadPage : PageBase
    {
        private readonly int _index;           // Storage index of the SMS on the modem
        private string _sender = "";           // Sender (phone number)
        private string _timestamp = "";        // Timestamp as returned by the modem
        private string _body = "(loading…)";   // Body text (fetched asynchronously)
        private readonly SmsStorage _storage = SmsStorage.SM;

        public SmsReadPage(ConsoleUi ui, AppSession session, int index) : base(ui, session)
        {
            _index = index;

            // Best-effort preload from the list page's cached summary so the user
            // sees something quickly while the full body is fetched.
            var li = Session.SmsList.FirstOrDefault(it => it.Index == index);
            if (li != null)
            {
                _sender = li.Sender; _timestamp = li.Timestamp; _body = "(loading full body…)";
                _storage = li.Storage; // <--- remember storage
            }

            Session.Status.Add($"SMS: Opened message index {_index}.");

            // Acquire controller; if missing, we show a friendly message and stay in read-only state.
            var pc = ControllerLocator.Phone;
            if (pc == null)
            {
                _body = "(PhoneController not available — initialize ports first.)";
                return;
            }

            // Fire a background fetch to retrieve the full message body.
            UiTaskRunner.Run(Session.Status, $"Reading SMS[{_index}@{_storage}]…", async () =>
            {
                var msg = await pc.ReadSmsAsync(_index, _storage);
                _sender = msg.Sender; 
                _timestamp = msg.Timestamp; 
                _body = msg.Body;
                Ui.Redraw();
            });
        }

        /// <summary>
        /// Draws the message header and body with simple hard-wrap within page width.
        /// </summary>
        public override void Draw(int width, int height)
        {
            DrawTitleRow(width, Header($"SMS [{_index}]"));

            int row = 2;
            // Add the new 'R = Reply' affordance to the legend
            SafeWrite(0, row++, "ESC = Back | D = Delete | R = Reply");
            SafeWrite(0, row++, "");

            SafeWrite(0, row++, $"From:      {_sender}");
            SafeWrite(0, row++, $"Timestamp: {_timestamp}");
            SafeWrite(0, row++, "");
            SafeWrite(0, row++, "Message:");
            SafeWrite(0, row++, "");

            // Display the body in fixed-width chunks to avoid console wrapping
            var lines = Wrap(_body ?? string.Empty, width);
            int max = Math.Max(0, height - row - 1);
            for (int i = 0; i < lines.Count && i < max; i++)
                SafeWrite(0, row + i, lines[i]);
        }

        /// <summary>
        /// Handles back navigation, delete flow (with confirmation dialog), and reply.
        /// </summary>
        public override bool HandleKey(ConsoleKeyInfo key, out PageNav nav)
        {
            nav = PageNav.None;

            if (key.Key == ConsoleKey.Escape) { nav = PageNav.Pop; return true; }

            if (key.Key == ConsoleKey.D)
            {
                // Ask the user to confirm deletion of this SMS.
                Ui.Push(new ConfirmDialogPage(Ui, Session,
                    title: "Delete SMS",
                    message: $"Delete SMS at index {_index} in {_storage}?",
                    onResult: confirmed =>
                    {
                        if (!confirmed) return;
                        var pc = ControllerLocator.Phone;
                        if (pc == null) { Session.Status.Add("Delete: PhoneController not available."); return; }

                        UiTaskRunner.Run(Session.Status, $"Deleting SMS[{_index}@{_storage}]…", async () =>
                        {
                            await pc.DeleteSmsAsync(_index, _storage).ConfigureAwait(false);
                            Ui.Pop();    // back to list
                            Ui.Redraw();
                        }, successMsg: $"SMS[{_index}@{_storage}] deleted.");
                    })
                { Previous = this });

                nav = PageNav.Redraw;
                return true;
            }

            if (key.Key == ConsoleKey.R)
            {
                // Open the compose page with "To" prefilled to the original sender.
                // Focus should be on the Body field so the user can type immediately.
                var dest = string.IsNullOrWhiteSpace(_sender) ? "" : _sender;

                // Optional: seed a small header (remove if undesired).
                string seed = "";
                if (!string.IsNullOrWhiteSpace(_timestamp))
                    seed = $"(Re {_timestamp}): ";

                Ui.Push(new SendSmsPage(Ui, Session, toPrefill: dest, bodySeed: seed) { Previous = this });
                nav = PageNav.Redraw;
                return true;
            }

            return false; // Unhandled key
        }

        /// <summary>
        /// Simple fixed-width "wrap" of a string into chunks no longer than width.
        /// This is not word-wrapping; it's a safe way to avoid console auto-wrap.
        /// </summary>
        private static List<string> Wrap(string text, int width)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) { result.Add(""); return result; }

            int start = 0;
            while (start < text.Length)
            {
                int len = Math.Min(width, text.Length - start);
                result.Add(text.Substring(start, len));
                start += len;
            }
            return result;
        }
    }
}
