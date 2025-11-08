// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: SmsMenuPage.cs
// Description:
//   Central hub for managing SMS messages on the SIM7600G-H modem. Displays
//   a scrollable list of received messages (merged from ME and SM storage),
//   provides actions to refresh, compose, open, or delete messages, and
//   handles cursor-based navigation.
//
//   Key bindings:
//     - ESC   : Back to Main Menu
//     - R     : Refresh message list from modem storage
//     - S     : Open "Compose SMS" page
//     - D/Del : Delete the selected message (with confirmation)
//     - ↑/↓   : Move cursor up/down in list
//     - ENTER : Open selected message in read view
//
// Implementation Notes:
//   - The list display auto-adjusts to console height and centers the cursor
//     where possible to reduce vertical scrolling fatigue.
//   - All I/O operations (refresh, delete) are executed asynchronously via
//     UiTaskRunner to maintain UI responsiveness.
//   - After refresh or delete operations, ClampCursor() ensures cursor validity.
// ============================================================================

namespace Sim7600Console.UI.Pages
{
    /// <summary>
    /// Page providing a scrollable list of received SMS messages.
    /// Allows the user to refresh, open, compose, or delete messages.
    /// </summary>
    public sealed class SmsMenuPage : PageBase
    {
        /// <summary>
        /// Index of the currently selected message in the visible list.
        /// Used for navigation and determining which message is opened or deleted.
        /// </summary>
        private int _cursor = 0;

        /// <summary>
        /// Constructs the SMS menu and logs the page entry event.
        /// </summary>
        public SmsMenuPage(ConsoleUi ui, AppSession session) : base(ui, session)
        {
            Session.Status.Add("SMS Menu opened.");
        }

        /// <summary>
        /// Draws the SMS list interface, including the title, hotkey legend,
        /// and message list itself. If no messages are available, prompts the
        /// user to refresh.
        /// </summary>
        public override void Draw(int width, int height)
        {
            DrawTitleRow(width, Header("SMS Menu"));

            int row = 2;
            SafeWrite(0, row++, "ESC = Back | R = Refresh | S = Send New | ENTER = Open | D/Del = Delete");
            SafeWrite(0, row++, "");

            // --- Empty list state ---
            if (Session.SmsList.Count == 0)
            {
                SafeWrite(0, row++, "No messages to display. Press 'R' to refresh.");
                return;
            }

            // --- List header ---
            SafeWrite(0, row++, "Received Messages:");

            // Calculate how many lines can be shown within screen height.
            // Center cursor within viewport when possible.
            int maxVisible = Math.Max(1, height - row - 1);
            int start = Math.Max(0, Math.Min(_cursor - maxVisible / 2,
                            Math.Max(0, Session.SmsList.Count - maxVisible)));
            int end = Math.Min(Session.SmsList.Count, start + maxVisible);

            // --- Render each message line ---
            for (int i = start; i < end; i++)
            {
                var item = Session.SmsList[i];
                string marker = i == _cursor ? "▶" : " "; // highlight cursor position
                string line = $"{marker} [{item.Index}@{item.Storage}] {item.Timestamp}  From: {item.Sender}  {item.Preview}";

                if (line.Length > width) line = line[..width]; // clamp to console width
                SafeWrite(0, row++, line);
            }
        }

        /// <summary>
        /// Handles all keyboard input for this page: navigation, refresh, compose,
        /// open, and delete actions. Most actions trigger redraws or async tasks.
        /// </summary>
        public override bool HandleKey(ConsoleKeyInfo key, out PageNav nav)
        {
            nav = PageNav.None;

            switch (key.Key)
            {
                // --- Navigation / Back ---
                case ConsoleKey.Escape:
                    nav = PageNav.Pop;
                    return true;

                // --- Refresh: pulls message list from modem ---
                case ConsoleKey.R:
                    {
                        var pc = ControllerLocator.Phone;
                        if (pc == null)
                        {
                            Session.Status.Add("SMS: PhoneController not available. Initialize ports first.");
                            nav = PageNav.Redraw;
                            return true;
                        }

                        // Run refresh asynchronously (non-blocking)
                        UiTaskRunner.Run(Session.Status, "Refreshing SMS list…", async () =>
                        {
                            await pc.RefreshSmsListAsync().ConfigureAwait(false);
                            Session.NewSmsIndicator = false; // clear NEW badge
                            ClampCursor();
                            Ui.Redraw(); // redraw to show updated message list
                        }, successMsg: "SMS list refreshed.");

                        nav = PageNav.Redraw;
                        return true;
                    }

                // --- Compose: open new message page ---
                case ConsoleKey.S:
                    Ui.Push(new SendSmsPage(Ui, Session) { Previous = this });
                    nav = PageNav.Redraw;
                    return true;

                // --- Navigation: cursor up/down ---
                case ConsoleKey.UpArrow:
                    _cursor = Math.Max(0, _cursor - 1);
                    nav = PageNav.Redraw;
                    return true;

                case ConsoleKey.DownArrow:
                    _cursor = Math.Min(Math.Max(0, Session.SmsList.Count - 1), _cursor + 1);
                    nav = PageNav.Redraw;
                    return true;

                // --- Open message in read view ---
                case ConsoleKey.Enter:
                    if (Session.SmsList.Count == 0) return true;
                    var item = Session.SmsList[Math.Max(0, Math.Min(Session.SmsList.Count - 1, _cursor))];
                    Ui.Push(new SmsReadPage(Ui, Session, item.Index) { Previous = this });
                    nav = PageNav.Redraw;
                    return true;

                // --- Delete selected message (D or Delete key) ---
                case ConsoleKey.D:
                case ConsoleKey.Delete:
                    {
                        if (Session.SmsList.Count == 0) return true;

                        // Get selected message
                        var selected = Session.SmsList[Math.Max(0, Math.Min(Session.SmsList.Count - 1, _cursor))];
                        int index = selected.Index;
                        var storage = selected.Storage;

                        // Confirm deletion to prevent accidental data loss
                        Ui.Push(new ConfirmDialogPage(
                            Ui,
                            Session,
                            "Delete SMS",
                            $"Delete SMS at index {index} in {storage}?",
                            confirmed =>
                            {
                                if (!confirmed) return;

                                var pc = ControllerLocator.Phone;
                                if (pc == null)
                                {
                                    Session.Status.Add("Delete: PhoneController not available.");
                                    return;
                                }

                                // Execute async deletion and refresh
                                UiTaskRunner.Run(Session.Status, $"Deleting SMS[{index}@{storage}]…", async () =>
                                {
                                    await pc.DeleteSmsAsync(index, storage).ConfigureAwait(false);
                                    await pc.RefreshSmsListAsync().ConfigureAwait(false);
                                    ClampCursor();
                                    Ui.Redraw();
                                },
                                successMsg: $"SMS[{index}@{storage}] deleted.");
                            })
                        { Previous = this });

                        nav = PageNav.Redraw;
                        return true;
                    }

                // --- Unhandled key ---
                default:
                    return false;
            }
        }

        /// <summary>
        /// Ensures the cursor remains within a valid index range after list
        /// updates or deletions. Prevents out-of-range exceptions.
        /// </summary>
        private void ClampCursor()
        {
            if (Session.SmsList.Count == 0)
            {
                _cursor = 0;
                return;
            }

            if (_cursor >= Session.SmsList.Count)
                _cursor = Session.SmsList.Count - 1;

            if (_cursor < 0)
                _cursor = 0;
        }
    }
}
