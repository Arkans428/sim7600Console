// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: AboutPage.cs
// Project: SIM7600G-H Console Dialer (C# .NET 8)
// ----------------------------------------------------------------------------
// PURPOSE
//   Provides a simple static “About” page shown from the main menu. This page
//   displays basic information about the program, its purpose, dependencies,
//   and author credits (including this AI instance’s ID for traceability).
//
// DESIGN
//   • Inherits from PageBase for consistent look and navigation behavior.
//   • Displays multiline static content in a top-aligned layout.
//   • Uses the SafeWrite helper from PageBase to guard against small console
//     windows and cursor exceptions.
//   • Pressing ESC returns to the previous page (PageNav.Pop).
//
// TYPICAL NAVIGATION FLOW
//   [MainMenu] → (press "About") → [AboutPage]
//       ESC → returns to MainMenu
//
// DEPENDENCIES
//   • PageBase: Provides drawing helpers like DrawTitleRow(), Header(), and SafeWrite().
//   • InstanceInfo: Provides program metadata such as InstanceId and Banner.
// ============================================================================

using System;

namespace Sim7600Console
{
    /// <summary>
    /// A simple static "About" page that shows program information, credits,
    /// and usage notes. It can be invoked from the main menu and exited with ESC.
    /// </summary>
    public sealed class AboutPage : PageBase
    {
        /// <summary>
        /// Constructs an AboutPage tied to the given UI host and shared session.
        /// </summary>
        public AboutPage(ConsoleUi ui, AppSession session) : base(ui, session) { }

        /// <summary>
        /// Draws the About page text content, including banner, description,
        /// author credits, and basic usage notes.
        /// </summary>
        public override void Draw(int width, int height)
        {
            // Draw the standard title header row using helper from PageBase.
            DrawTitleRow(width, Header("About"));

            int row = 2;
            SafeWrite(0, row++, "ESC = Back");  // Navigation hint
            SafeWrite(0, row++, "");

            // Display application banner (e.g., version or title art)
            SafeWrite(0, row++, InstanceInfo.Banner);

            // General description of the program’s purpose and capabilities.
            SafeWrite(0, row++, "This program lets you place/receive voice calls and send/receive SMS on a SIM7600G-H.");
            SafeWrite(0, row++, "UI is page-based with a rolling Status Area at the bottom for live updates.");
            SafeWrite(0, row++, "");

            // Attribution / credits section
            SafeWrite(0, row++, "Credits:");
            SafeWrite(0, row++, "  • Author: AI");
            SafeWrite(0, row++, $"  • Instance ID: {InstanceInfo.InstanceId}");
            SafeWrite(0, row++, "");

            // Technical / dependency notes
            SafeWrite(0, row++, "Notes:");
            SafeWrite(0, row++, "  • Requires NAudio (install via NuGet).");
            SafeWrite(0, row++, "  • Choose correct AT and Audio COM ports before use.");
        }

        /// <summary>
        /// Handles key presses specific to the About page.
        /// Pressing ESC navigates back to the previous page.
        /// </summary>
        public override bool HandleKey(ConsoleKeyInfo key, out PageNav nav)
        {
            nav = PageNav.None;

            // ESC closes this page and returns to the previous one.
            if (key.Key == ConsoleKey.Escape)
            {
                nav = PageNav.Pop;
                return true;
            }

            // No other keys are handled here.
            return false;
        }
    }
}
