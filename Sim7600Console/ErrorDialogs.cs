// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: ErrorDialogs.cs
// Project: SIM7600G-H Console Dialer (C# .NET 8)
// ----------------------------------------------------------------------------
// PURPOSE
//   Implements a minimal confirmation dialog (Yes/No modal page) used by
//   higher-level pages to verify user intent before performing irreversible
//   actions — such as deleting an SMS, clearing logs, or exiting the program.
//
// USER EXPERIENCE (UX)
//   • Displays a simple header, followed by a one-line message prompt.
//   • Accepts keyboard input:
//        Y  → confirm (Yes)
//        N  → cancel  (No)
//        ESC → cancel (No)
//   • When the user presses a key, the result (true/false) is passed to the
//     provided callback, and the modal page pops itself off the stack.
//
// DESIGN NOTES
//   • Inherits from PageBase, giving access to drawing helpers such as
//     SafeWrite(), DrawTitleRow(), and Header() for consistent formatting.
//   • The dialog truncates its message if it exceeds the console width, ensuring
//     clean display without line wrapping.
//   • It is fully self-contained: no direct coupling to SMS, modem, or controller
//     classes. The invoking page handles what to do with the callback result.
//
// EXAMPLE USAGE
//   var dlg = new ConfirmDialogPage(ui, session,
//       "Delete SMS", $"Delete message at index {idx}?",
//       confirmed => { if (confirmed) DoDelete(); });
//   ui.Push(dlg);
//
// NAVIGATION
//   • Push() adds the dialog on top of the navigation stack.
//   • On Y/N/ESC, it executes the callback and returns PageNav.Pop to close itself.
//
// DEPENDENCIES
//   • ConsoleUi:  For navigation and redraw handling.
//   • AppSession: For logging and shared runtime state.
//   • PageBase:   For drawing abstraction and SafeWrite boundary protection.
// ============================================================================

using System;

namespace Sim7600Console
{
    /// <summary>
    /// Represents a simple modal confirmation dialog that asks the user
    /// a Yes/No question before continuing. The result is reported through
    /// a callback and the dialog automatically closes afterward.
    /// </summary>
    public sealed class ConfirmDialogPage : PageBase
    {
        // --------------------------------------------------------------------
        // Fields
        // --------------------------------------------------------------------

        /// <summary>
        /// Title text displayed in the top header bar (e.g., “Delete SMS”).
        /// </summary>
        private readonly string _title;

        /// <summary>
        /// Message body prompt shown below the header (single line).
        /// </summary>
        private readonly string _message;

        /// <summary>
        /// Callback that receives the user’s decision:
        /// true  → confirmed (Yes)
        /// false → cancelled (No)
        /// </summary>
        private readonly Action<bool> _onResult;

        // --------------------------------------------------------------------
        // Construction
        // --------------------------------------------------------------------

        /// <summary>
        /// Initializes a new confirmation dialog.
        /// </summary>
        /// <param name="ui">Reference to the ConsoleUi for redraw/navigation.</param>
        /// <param name="session">Active AppSession (for consistency/logging).</param>
        /// <param name="title">Dialog title, shown at the top (e.g., "Confirm Delete").</param>
        /// <param name="message">Prompt message (e.g., "Delete SMS #7?").</param>
        /// <param name="onResult">
        /// Callback invoked with <c>true</c> if user presses Y, or <c>false</c> if user presses N/ESC.
        /// </param>
        public ConfirmDialogPage(ConsoleUi ui, AppSession session, string title, string message, Action<bool> onResult)
            : base(ui, session)
        {
            _title = title ?? "Confirm";
            _message = message ?? string.Empty;
            _onResult = onResult ?? (_ => { });
        }

        // --------------------------------------------------------------------
        // Rendering
        // --------------------------------------------------------------------

        /// <summary>
        /// Draws the modal confirmation dialog, including:
        ///   - Title row with centered header
        ///   - Key legend (Y/N/ESC)
        ///   - One-line message prompt (clamped to console width)
        /// </summary>
        public override void Draw(int width, int height)
        {
            // Draw a standard title bar at the top using PageBase helper
            DrawTitleRow(width, Header(_title));

            int row = 2;

            // Display input legend to guide user interaction
            SafeWrite(0, row++, "Y = Yes | N = No | ESC = No");
            SafeWrite(0, row++, "");

            // Display the message body, truncated to fit within screen width
            SafeWrite(0, row++, _message.Length > width ? _message[..width] : _message);
        }

        // --------------------------------------------------------------------
        // Key Handling
        // --------------------------------------------------------------------

        /// <summary>
        /// Handles Y/N/ESC inputs:
        ///   • Y → confirms (calls _onResult(true))
        ///   • N or ESC → cancels (calls _onResult(false))
        /// Any other keypress is ignored.
        /// </summary>
        /// <param name="key">The pressed console key.</param>
        /// <param name="nav">The navigation result to control page stack behavior.</param>
        /// <returns>True if the key was handled; otherwise false.</returns>
        public override bool HandleKey(ConsoleKeyInfo key, out PageNav nav)
        {
            nav = PageNav.None;

            // User confirmed (Yes)
            if (key.Key == ConsoleKey.Y)
            {
                _onResult(true);
                nav = PageNav.Pop; // Close the dialog and return to previous page
                return true;
            }

            // User cancelled (No or ESC)
            if (key.Key == ConsoleKey.N || key.Key == ConsoleKey.Escape)
            {
                _onResult(false);
                nav = PageNav.Pop; // Close dialog
                return true;
            }

            // Any other key is ignored (dialog remains active)
            return false;
        }
    }
}
