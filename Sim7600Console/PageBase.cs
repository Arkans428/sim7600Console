// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: PageBase.cs
// Project: SIM7600G-H Console Dialer (C# .NET 8)
// ----------------------------------------------------------------------------
// PURPOSE
//   Provides a shared foundation for all UI “pages” in the console interface.
//   Each page encapsulates a full-screen view with its own rendering (Draw)
//   and key handling (HandleKey) logic, supporting a stack-based navigation
//   model (Push / Pop / ReplaceTop).
//
//   PageBase offers:
//     • Common properties (Next, Previous)
//     • A consistent drawing header and safe text output helpers
//     • Default passive refresh behavior (disabled by default)
//     • A common structure for simple, testable console pages
//
// OVERVIEW
//   Pages are rendered by ConsoleUi, which keeps a navigation stack. Each
//   page fully redraws its content when requested and responds to key input.
//   For example:
//     - DialerPage handles number entry and call initiation.
//     - SmsMenuPage lists and reads SMS messages.
//     - AboutPage shows program info.
//
//   Each page can also indicate whether it needs periodic redraws even without
//   keypresses (WantsPassiveRefresh), though this is disabled by default to
//   reduce flicker.
//
// DESIGN NOTES
//   • Console coordinates are manually managed using SafeWrite() to prevent
//     exceptions during window resizes or cursor overruns.
//   • The DrawTitleRow() helper centers headers and adds consistent spacing.
//   • The “InstanceInfo.InstanceId” is appended to titles for build traceability.
//   • The PageNav enum standardizes navigation responses.
//
// NAVIGATION ENUMS
//   PageNav.None     → No navigation change.
//   PageNav.Redraw   → Redraw current page.
//   PageNav.Pop      → Pop current page (return to previous).
//   PageNav.ExitApp  → Exit the entire program.
//
// FUTURE EXPANSION
//   • Add animated or timed updates by overriding WantsPassiveRefresh = true.
//   • Support sub-page transitions or transient modals (like ConfirmDialogPage).
//   • Provide more advanced layout helpers (table rendering, text wrapping).
// ============================================================================

using System;

namespace Sim7600Console
{
    /// <summary>
    /// Defines common navigation actions that a page may request from the UI loop.
    /// </summary>
    public enum PageNav
    {
        None,     // No navigation change
        Redraw,   // Redraw current page
        Pop,      // Return to previous page
        ExitApp   // Exit the application entirely
    }

    /// <summary>
    /// Interface defining the contract for all console UI pages.
    /// Each page must be able to draw itself and handle keyboard input.
    /// </summary>
    public interface IPage
    {
        /// <summary>
        /// Draws the entire page to the console window.
        /// </summary>
        /// <param name="width">Console window width in characters.</param>
        /// <param name="height">Console window height in lines.</param>
        void Draw(int width, int height);

        /// <summary>
        /// Handles a single keypress and outputs a navigation directive.
        /// </summary>
        /// <param name="key">Key input captured by Console.ReadKey().</param>
        /// <param name="nav">Output enum specifying navigation intent.</param>
        /// <returns>True if the key was handled; false otherwise.</returns>
        bool HandleKey(ConsoleKeyInfo key, out PageNav nav);

        /// <summary>
        /// Determines whether the page should be periodically redrawn
        /// even when no input occurs (used for “heartbeat” pages).
        /// </summary>
        bool WantsPassiveRefresh { get; }

        /// <summary>
        /// Forward navigation reference — may be used for chain navigation.
        /// </summary>
        IPage? Next { get; set; }

        /// <summary>
        /// Backward navigation reference (e.g., to return from subpages).
        /// </summary>
        IPage? Previous { get; set; }
    }

    /// <summary>
    /// Provides shared functionality for pages in the console UI, including
    /// drawing helpers, navigation links, and safe output methods.
    /// Derived pages should override <see cref="Draw"/> and <see cref="HandleKey"/>.
    /// </summary>
    public abstract class PageBase : IPage
    {
        /// <summary>
        /// Reference to the owning ConsoleUi manager (for pushing/popping pages).
        /// </summary>
        protected readonly ConsoleUi Ui;

        /// <summary>
        /// Shared session context containing runtime state and data.
        /// </summary>
        protected readonly AppSession Session;

        // NEW: public accessor so ConsoleUi can read session state for the ribbon
        public AppSession AppSession => Session;

        // NEW: whether the global status area (bottom log) should be hidden for this page.
        public virtual bool HideStatusArea => false;

        /// <summary>
        /// Points to the next page in a manual navigation chain (optional).
        /// </summary>
        public IPage? Next { get; set; }

        /// <summary>
        /// Points to the previous page, used when returning to prior context.
        /// </summary>
        public IPage? Previous { get; set; }

        /// <summary>
        /// Indicates whether the page should request periodic redraws even
        /// without key input. Disabled by default to prevent flicker.
        /// Override to true for live-update pages (e.g., call status).
        /// </summary>
        public virtual bool WantsPassiveRefresh => false;

        /// <summary>
        /// Base constructor linking this page to the Console UI and shared session.
        /// </summary>
        /// <param name="ui">Reference to the ConsoleUi navigation manager.</param>
        /// <param name="session">Shared AppSession for runtime data.</param>
        protected PageBase(ConsoleUi ui, AppSession session)
        {
            Ui = ui;
            Session = session;
        }

        /// <summary>
        /// Draws the page’s content using the given console dimensions.
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract void Draw(int width, int height);

        /// <summary>
        /// Handles user key input for this page.
        /// Derived pages define navigation and actions here.
        /// </summary>
        public abstract bool HandleKey(ConsoleKeyInfo key, out PageNav nav);

        // --------------------------------------------------------------------
        // Common Drawing Helpers
        // --------------------------------------------------------------------

        /// <summary>
        /// Formats a consistent header line showing both title and instance ID.
        /// </summary>
        protected string Header(string title) => $"{title} — {InstanceInfo.InstanceId}";

        /// <summary>
        /// Draws a centered title row with padding to fill the full console width.
        /// </summary>
        /// <param name="width">Console width in characters.</param>
        /// <param name="text">Header text (centered).</param>
        protected void DrawTitleRow(int width, string text)
        {
            text ??= string.Empty;
            int pad = Math.Max(0, (width - text.Length) / 2);
            var line = new string(' ', pad) + text;
            if (line.Length < width)
                line += new string(' ', width - line.Length);
            SafeWrite(0, 0, line);
        }

        /// <summary>
        /// Writes text safely to a specific console coordinate, handling edge cases
        /// such as resizing or cursor out-of-range errors.
        /// </summary>
        /// <param name="x">Horizontal column position (0-based).</param>
        /// <param name="y">Vertical row position (0-based).</param>
        /// <param name="text">The text to render.</param>
        protected void SafeWrite(int x, int y, string text)
        {
            if (y < 0 || y >= Console.WindowHeight) return;
            if (text == null) text = string.Empty;

            try
            {
                Console.SetCursorPosition(x, y);

                // Truncate to console width to prevent wrapping or overflow.
                if (text.Length > Console.WindowWidth)
                    text = text[..Console.WindowWidth];

                Console.Write(text);
            }
            catch
            {
                // Ignore exceptions (e.g., due to window resize races or
                // concurrent writes) to keep UI stable.
            }
        }
    }
}
