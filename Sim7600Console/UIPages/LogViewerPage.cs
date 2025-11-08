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
//     • ↑ / ↓      : Scroll 1 line up/down
//     • PageUp     : Scroll one page up
//     • PageDown   : Scroll one page down
//     • Home       : Jump to oldest available in-memory entry
//     • End        : Jump to newest (resume live follow)
//     • S          : Save a snapshot of the visible buffer to a text file
//                    (next to the EXE) e.g. sim7600-log-snapshot-2025-11-04_20-53-12.txt
//
// Notes:
//   - Uses StatusHub.GetTail(N) to avoid locking for the entire history.
//   - Scrollback is bounded by StatusHub’s in-memory capacity (maxEntries).
//   - When _scrollOffset == 0 we’re “following” the tail (live).
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace Sim7600Console.UIPages
{
    public sealed class LogViewerPage : PageBase
    {
        // Hide bottom live status so full screen can be used for log content
        public override bool HideStatusArea => true;

        // How many lines to fetch from StatusHub each draw. Should be
        // <= StatusHub’s maxEntries (Program sets 400 by default).
        private const int FetchLimit = 1000;

        // 0 means bottom (live tail). Positive values mean “that many lines above the bottom”.
        private int _scrollOffset = 0;

        public LogViewerPage(ConsoleUi ui, AppSession session) : base(ui, session)
        {
            Session.Status.Add("Log Viewer opened. Use PgUp/PgDn/Home/End to navigate.");
        }

        public override void Draw(int width, int height)
        {
            DrawTitleRow(width, Header("Live Log Viewer"));

            int row = 2;
            SafeWrite(0, row++, "ESC = Back | ↑/↓ = Line | PgUp/PgDn = Page | Home/End = Oldest/Newest | S = Save snapshot");
            SafeWrite(0, row++, "");

            // Space available for log lines
            int visible = Math.Max(3, height - row);

            // Take a tail snapshot
            List<string> buf = Session.Status.GetTail(FetchLimit);
            int total = buf.Count;

            if (total == 0)
            {
                SafeWrite(0, row, "(No log entries yet)");
                return;
            }

            // Clamp scroll offset against current buffer and visible height
            int maxScroll = Math.Max(0, total - visible);
            if (_scrollOffset > maxScroll) _scrollOffset = maxScroll;
            if (_scrollOffset < 0) _scrollOffset = 0;

            // Compute the first index to draw so that offset=0 shows the newest lines
            int start = Math.Max(0, total - visible - _scrollOffset);
            int endEx = Math.Min(total, start + visible);

            // Render the current window
            for (int i = start; i < endEx; i++)
            {
                string line = buf[i];
                if (line.Length > width) line = line[..width];
                SafeWrite(0, row++, line);
            }

            // If we didn’t fill the area (very short logs), clear the rest
            for (; row < height; row++)
                SafeWrite(0, row, string.Empty);
        }

        public override bool HandleKey(ConsoleKeyInfo key, out PageNav nav)
        {
            nav = PageNav.None;

            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    nav = PageNav.Pop; // back to previous page
                    return true;

                case ConsoleKey.UpArrow:
                    _scrollOffset++;             // one line older
                    Ui.Redraw();
                    return true;

                case ConsoleKey.DownArrow:
                    _scrollOffset = Math.Max(0, _scrollOffset - 1); // one line newer
                    Ui.Redraw();
                    return true;

                case ConsoleKey.PageUp:
                    AdjustPage(delta: +1);
                    Ui.Redraw();
                    return true;

                case ConsoleKey.PageDown:
                    AdjustPage(delta: -1);
                    Ui.Redraw();
                    return true;

                case ConsoleKey.Home:
                    // jump to oldest available in the current in-memory ring
                    {
                        int maxScroll = Math.Max(0, Session.Status.GetTail(FetchLimit).Count - 1);
                        _scrollOffset = maxScroll;
                        Ui.Redraw();
                        return true;
                    }

                case ConsoleKey.End:
                    _scrollOffset = 0; // resume live follow
                    Ui.Redraw();
                    return true;

                case ConsoleKey.S:
                    SaveSnapshot();
                    Ui.Redraw();
                    return true;

                default:
                    return false;
            }
        }

        private void AdjustPage(int delta)
        {
            // Page size is “how many content rows we have”
            // We don’t have height here; approximate ~20 lines (safe default) and overdraw will be clamped in Draw().
            // Minor imperfection: page size varies with window height, but this keeps logic simple and robust.
            int page = Math.Max(5, Console.WindowHeight - 6);
            _scrollOffset = Math.Max(0, _scrollOffset + (delta > 0 ? page : -page));
        }

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
                Session.Status.Add($"[Log] Snapshot failed: {ex.Message}");
            }
        }
    }
}
