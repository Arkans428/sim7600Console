// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: InputHelper.cs
// Project: SIM7600G-H Console Dialer (C# .NET 8)
// ----------------------------------------------------------------------------
// PURPOSE
//   Provides lightweight static helper methods for handling text input within
//   console-based, page-driven UIs. These helpers simplify implementing
//   editable text fields such as:
//       • Dial strings
//       • SMS message text boxes
//       • PIN/password entry (with optional masking in future updates)
//
// DESIGN NOTES
//   • The helpers are intentionally stateless and safe to call repeatedly from
//     within page HandleKey() loops.
//   • They process only essential editing keys (character input and Backspace).
//   • ConsoleKeyInfo is used directly to interpret the pressed key.
//   • By returning a boolean value, the caller can decide whether to trigger
//     a UI redraw after the buffer changes.
//
// FUTURE EXPANSION
//   Planned extensions could include:
//     - Masked input for secure PIN/password entry.
//     - Cursor-based text editing (left/right navigation, insert mode).
//     - Field validation (numeric-only, length limits, etc.).
//
// USAGE EXAMPLE
//   In a PageBase.HandleKey():
//       if (InputHelper.EditBuffer(_inputBuffer, key))
//           nav = PageNav.Redraw;
//
// DEPENDENCIES
//   • System.Text.StringBuilder — for mutable text buffers.
//   • ConsoleKeyInfo — for raw keyboard event data.
// ============================================================================

using System.Text;

namespace Sim7600Console.UI
{
    /// <summary>
    /// Provides static helper methods for handling editable text buffers in
    /// console pages. Intended for simple single-line text entry such as
    /// phone numbers, names, or configuration strings.
    /// </summary>
    public static class InputHelper
    {
        /// <summary>
        /// Processes a single ConsoleKeyInfo event to modify a <see cref="StringBuilder"/> buffer.
        /// Supports basic editing:
        ///   • Appends printable characters.
        ///   • Deletes the last character on Backspace.
        /// Returns true if the buffer content changed, false otherwise.
        ///
        /// This is designed to be used inside a non-blocking key event handler,
        /// such as a PageBase.HandleKey() override.
        /// </summary>
        /// <param name="target">The StringBuilder buffer to modify.</param>
        /// <param name="key">The ConsoleKeyInfo representing the key pressed.</param>
        /// <returns>
        /// True if the buffer was modified (added or removed characters);
        /// false if the key did not result in a change.
        /// </returns>
        public static bool EditBuffer(StringBuilder target, ConsoleKeyInfo key)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                // Remove the last character if any exist.
                if (target.Length > 0)
                {
                    target.Remove(target.Length - 1, 1);
                    return true;
                }
                return false;
            }

            // Capture printable characters (exclude control keys like ESC, Enter, etc.)
            char c = key.KeyChar;
            if (!char.IsControl(c))
            {
                target.Append(c);
                return true;
            }

            // Ignore control or navigation keys
            return false;
        }
    }
}
