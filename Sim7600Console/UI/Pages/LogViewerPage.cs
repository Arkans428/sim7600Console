// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: LogViewerPage.cs
// Description:
//   Live view into StatusHub with smooth scrollback. Follows the tail by
//   default (like `tail -f`); when you scroll up it “pins” to your position
//   until you scroll back down to the bottom.
//
//   Keys:
//     • ESC        : Back
//     • ↑ / ↓      : Scroll 1 line up/down (older/newer)
//     • PageUp     : Scroll one page up (older)
//     • PageDown   : Scroll one page down (newer)
//     • Home       : Jump to oldest available in-memory entry
//     • End        : Jump to newest (resume live follow)
//     • S          : Save a snapshot of the visible buffer to a text file
//                    (next to the EXE) e.g. sim7600-log-snapshot-2025-11-04_20-53-12.txt
//
// Notes:
//   - Uses StatusHub.GetTail(N) on each draw to avoid holding the lock longer
//     than necessary and to keep the UI responsive under heavy logging.
//   - Scrollback is bounded by StatusHub’s in-memory capacity (maxEntries).
//   - When _scrollOffset == 0 we’re “following” the tail (live).
//   - This page explicitly hides the global bottom Status Area so the full
//     console height is available for log content.
// ============================================================================

namespace Sim7600Console.UI.Pages
{
    /// <summary>
    /// Read-only viewer over <see cref="StatusHub"/> that supports
    /// live follow and manual scrollback without disrupting logging.
    /// </summary>
    public sealed class LogViewerPage : PageBase
    {
        /// <summary>
        /// Hide the shared Status Area (normally rendered by the UI chrome)
        /// to maximize vertical space for the log while this page is active.
        /// </summary>
        public override bool HideStatusArea => true;

        /// <summary>
        /// Number of recent lines to fetch from <see cref="StatusHub"/> each draw.
        /// Must be ≤ hub's maxEntries; higher values increase memory copy work.
        /// </summary>
        private const int FetchLimit = 1000;

        /// <summary>
        /// Current scroll position relative to the newest entry:
        ///   0 = show newest lines and follow live updates (tail).
        ///  >0 = pinned that many lines above the bottom (no auto-follow).
        /// </summary>
        private int _scrollOffset = 0;

        public LogViewerPage(ConsoleUi ui, AppSession session) : base(ui, session)
        {
            Session.Status.Add("Log Viewer opened. Use PgUp/PgDn/Home/End to navigate.");
        }

        /// <summary>
        /// Renders the page title, legend, and a window onto the most recent log
        /// entries, honoring the current <see cref="_scrollOffset"/>.
        /// </summary>
        public override void Draw(int width, int height)
        {
            DrawTitleRow(width, Header("Live Log Viewer"));

            int row = 2;
            SafeWrite(0, row++, "ESC = Back | ↑/↓ = Line | PgUp/PgDn = Page | Home/End = Oldest/Newest | S = Save snapshot");
            SafeWrite(0, row++, "");

            // Number of content rows available for log lines
            int visible = Math.Max(3, height - row);

            // Snapshot the tail once per draw to keep a consistent view while painting
            List<string> buf = Session.Status.GetTail(FetchLimit);
            int total = buf.Count;

            if (total == 0)
            {
                SafeWrite(0, row, "(No log entries yet)");
                return;
            }

            // Bound the scroll offset to the available buffer and current viewport
            int maxScroll = Math.Max(0, total - visible);
            if (_scrollOffset > maxScroll) _scrollOffset = maxScroll;
            if (_scrollOffset < 0) _scrollOffset = 0;

            // Compute window: offset==0 => newest bottom-aligned
            int start = Math.Max(0, total - visible - _scrollOffset);
            int endEx = Math.Min(total, start + visible);

            // Paint the current window, truncating lines to the console width
            for (int i = start; i < endEx; i++)
            {
                string line = buf[i];
                if (line.Length > width) line = line[..width]; // avoid console wrap/flicker
                SafeWrite(0, row++, line);
            }

            // Clear any remaining rows (short logs / resizes)
            for (; row < height; row++)
                SafeWrite(0, row, string.Empty);
        }

        /// <summary>
        /// Keyboard navigation handler. All operations adjust
        /// <see cref="_scrollOffset"/> then request a redraw.
        /// </summary>
        public override bool HandleKey(ConsoleKeyInfo key, out PageNav nav)
        {
            nav = PageNav.None;

            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    nav = PageNav.Pop; // return to previous page
                    return true;

                // One-line navigation: up = older, down = newer
                case ConsoleKey.UpArrow:
                    _scrollOffset++;                 // move one line away from tail
                    Ui.Redraw();
                    return true;

                case ConsoleKey.DownArrow:
                    _scrollOffset = Math.Max(0, _scrollOffset - 1);
                    Ui.Redraw();
                    return true;

                // Page navigation: uses a dynamic page size (see AdjustPage)
                case ConsoleKey.PageUp:
                    AdjustPage(delta: +1);           // older by one page
                    Ui.Redraw();
                    return true;

                case ConsoleKey.PageDown:
                    AdjustPage(delta: -1);           // newer by one page
                    Ui.Redraw();
                    return true;

                case ConsoleKey.Home:
                    // Jump to the oldest line available in the current snapshot
                    {
                        int maxScroll = Math.Max(0, Session.Status.GetTail(FetchLimit).Count - 1);
                        _scrollOffset = maxScroll;
                        Ui.Redraw();
                        return true;
                    }

                case ConsoleKey.End:
                    // Return to live-following state
                    _scrollOffset = 0;
                    Ui.Redraw();
                    return true;

                case ConsoleKey.S:
                    // Persist the current (latest FetchLimit) lines to disk
                    SaveSnapshot();
                    Ui.Redraw();
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Adjusts the scroll offset by approximately one visible "page".
        /// The exact number of lines depends on the current console height.
        /// </summary>
        /// <param name="delta">
        /// +1 to move one page older; -1 to move one page newer.
        /// </param>
        private void AdjustPage(int delta)
        {
            // Page size approximates the current available content height.
            // Using Console.WindowHeight keeps it adaptive to resizes; final
            // bounds and clamping are enforced in Draw().
            int page = Math.Max(5, Console.WindowHeight - 6);
            _scrollOffset = Math.Max(0, _scrollOffset + (delta > 0 ? page : -page));
        }

        /// <summary>
        /// Saves the latest <see cref="FetchLimit"/> lines from the StatusHub
        /// into a timestamped text file alongside the executable. Any error is
        /// logged to the Status Area instead of throwing.
        /// </summary>
        private void SaveSnapshot()
        {
            try
            {
                var list = Session.Status.GetTail(FetchLimit);
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string file = $"sim7600-log-snapshot-{stamp}.txt";
                File.WriteAllLines(file, list);
                Session.Status.Add($"[Log] Snapshot saved: {file}");
            }
            catch (Exception ex)
            {
                // Non-fatal: the viewer must remain usable even if disk is read-only/full
                Session.Status.Add($"[Log] Snapshot failed: {ex.Message}");
            }
        }
    }
}
