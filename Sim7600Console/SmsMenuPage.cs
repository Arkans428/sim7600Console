// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: SmsMenuPage.cs
// Description:
//   SMS hub page for listing and opening received messages, composing new ones,
//   and (new) deleting the currently selected message directly from the list.
//   Key bindings:
//     - ESC   : Back to Main Menu
//     - R     : Refresh message list from modem storage
//     - S     : Open "Compose SMS" page
//     - D/Del : Delete the selected message (with confirmation)
//     - ↑/↓   : Move cursor
//     - ENTER : Open selected message in read view
// ============================================================================

using System;

namespace Sim7600Console
{
    public sealed class SmsMenuPage : PageBase
    {
        // Cursor index within the currently displayed list.
        private int _cursor = 0;

        public SmsMenuPage(ConsoleUi ui, AppSession session) : base(ui, session)
        {
            Session.Status.Add("SMS Menu opened.");
        }

        /// <summary>
        /// Draws the SMS list with a pointer on the current item. Shows a short
        /// instruction legend at the top. If there are no items, prompt to refresh.
        /// </summary>
        public override void Draw(int width, int height)
        {
            DrawTitleRow(width, Header("SMS Menu"));

            int row = 2;
            SafeWrite(0, row++, "ESC = Back | R = Refresh | S = Send New | ENTER = Open | D/Del = Delete");
            SafeWrite(0, row++, "");


            // Empty state: encourage the operator to fetch from the modem
            if (Session.SmsList.Count == 0)
            {
                SafeWrite(0, row++, "No messages to display. Press 'R' to refresh.");
                return;
            }

            // Render the list within available space (clip to page height).
            SafeWrite(0, row++, "Received Messages:");
            int maxVisible = Math.Max(1, height - row - 1); // leave a little breathing room
            int start = Math.Max(0, Math.Min(_cursor - (maxVisible / 2), Math.Max(0, Session.SmsList.Count - maxVisible)));
            int end = Math.Min(Session.SmsList.Count, start + maxVisible);

            for (int i = start; i < end; i++)
            {
                var item = Session.SmsList[i];
                string marker = (i == _cursor) ? "▶" : " ";
                string line = $"{marker} [{item.Index}@{item.Storage}] {item.Timestamp}  From: {item.Sender}  {item.Preview}";

                if (line.Length > width) line = line[..width]; // prevent wrap
                SafeWrite(0, row++, line);
            }
        }

        /// <summary>
        /// Handles navigation, refresh, compose, open, and delete actions.
        /// </summary>
        public override bool HandleKey(ConsoleKeyInfo key, out PageNav nav)
        {
            nav = PageNav.None;

            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    nav = PageNav.Pop; // Back to main
                    return true;

                case ConsoleKey.R:
                    {
                        var pc = ControllerLocator.Phone;
                        if (pc == null)
                        {
                            Session.Status.Add("SMS: PhoneController not available. Initialize ports first.");
                            nav = PageNav.Redraw;
                            return true;
                        }

                        UiTaskRunner.Run(Session.Status, "Refreshing SMS list…", async () =>
                        {
                            await pc.RefreshSmsListAsync().ConfigureAwait(false);
                            Session.NewSmsIndicator = false; // Clear the NEW badge now that we refreshed
                            ClampCursor();
                            Ui.Redraw();
                        }, successMsg: "SMS list refreshed.");

                        nav = PageNav.Redraw;
                        return true;
                    }

                case ConsoleKey.S:
                    Ui.Push(new SendSmsPage(Ui, Session) { Previous = this });
                    nav = PageNav.Redraw;
                    return true;

                case ConsoleKey.UpArrow:
                    _cursor = Math.Max(0, _cursor - 1);
                    nav = PageNav.Redraw;
                    return true;

                case ConsoleKey.DownArrow:
                    _cursor = Math.Min(Math.Max(0, Session.SmsList.Count - 1), _cursor + 1);
                    nav = PageNav.Redraw;
                    return true;

                case ConsoleKey.Enter:
                    if (Session.SmsList.Count == 0) return true;
                    var item = Session.SmsList[Math.Max(0, Math.Min(Session.SmsList.Count - 1, _cursor))];
                    Ui.Push(new SmsReadPage(Ui, Session, item.Index) { Previous = this });
                    nav = PageNav.Redraw;
                    return true;

                // --- New: Delete selected message (D or Delete key) ---
                case ConsoleKey.D:
                case ConsoleKey.Delete:
                    {
                        if (Session.SmsList.Count == 0) return true;

                        var selected = Session.SmsList[Math.Max(0, Math.Min(Session.SmsList.Count - 1, _cursor))];
                        int index = selected.Index;
                        var storage = selected.Storage;

                        Ui.Push(new ConfirmDialogPage(Ui, Session, "Delete SMS", $"Delete SMS at index {index} in {storage}?", confirmed =>
                        {
                            if (!confirmed) return;
                            var pc = ControllerLocator.Phone;
                            if (pc == null) { Session.Status.Add("Delete: PhoneController not available."); return; }

                            UiTaskRunner.Run(Session.Status, $"Deleting SMS[{index}@{storage}]…", async () =>
                            {
                                await pc.DeleteSmsAsync(index, storage).ConfigureAwait(false);
                                await pc.RefreshSmsListAsync().ConfigureAwait(false);
                                ClampCursor();
                                Ui.Redraw();
                            }, successMsg: $"SMS[{index}@{storage}] deleted.");
                        })
                        { Previous = this });

                        nav = PageNav.Redraw;
                        return true;
                    }

                default:
                    return false; // Unhandled key
            }
        }

        /// <summary>
        /// Keeps the cursor within the valid range after list size changes.
        /// </summary>
        private void ClampCursor()
        {
            if (Session.SmsList.Count == 0) { _cursor = 0; return; }
            if (_cursor >= Session.SmsList.Count) _cursor = Session.SmsList.Count - 1;
            if (_cursor < 0) _cursor = 0;
        }
    }
}
