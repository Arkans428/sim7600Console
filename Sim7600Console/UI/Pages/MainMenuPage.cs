// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: MainMenuPage.cs
// Project: SIM7600G-H Console Dialer (C# .NET 8)
// ----------------------------------------------------------------------------
// PURPOSE
//   Provides the main navigation hub for the console UI. It serves as the
//   user's central access point to all major functions of the application:
//
//     1) Dialer Page       — place or answer voice calls.
//     2) SMS Menu Page     — view, send, or delete SMS messages.
//     3) COM Port Settings — configure AT and Audio serial ports.
//     9) About Page        — display program and author information.
//     0) Exit Application  — terminate program.
//
// USER EXPERIENCE
//   The Main Menu is designed to always be reachable, static, and readable.
//   It also provides at-a-glance system information such as:
//     • Modem readiness status (AT connection established or not)
//     • Current incoming caller ID (if any)
//     • NEW SMS indicator flag
//
// NAVIGATION
//   - Number keys 1–3 and 9 are used for page transitions.
//   - ESC and '0' both trigger application exit for convenience.
//   - Each menu item pushes a new PageBase-derived object onto the UI stack.
//   - PageBase.Previous is maintained to allow back navigation.
//
// DESIGN NOTES
//   • The layout is paged rather than scrolling — screen is cleared per draw.
//   • It uses SafeWrite() and DrawTitleRow() (from PageBase) to prevent
//     rendering outside console bounds and to standardize text formatting.
//   • This class interacts with shared session state (AppSession) to display
//     runtime flags and indicators updated by the PhoneController.
//
// DEPENDENCIES
//   • AppSession   — Holds shared modem and SMS state information.
//   • ConsoleUi    — Manages page stack navigation and redraw logic.
//   • DialerPage   — Outgoing/incoming call controls.
//   • SmsMenuPage  — SMS list, read, and send functions.
//   • AboutPage    — Static program info and credits.
// ============================================================================

namespace Sim7600Console.UI.Pages
{
    /// <summary>
    /// Represents the top-level navigation menu for the SIM7600 console app.
    /// Displays quick system status and allows the user to access sub-pages
    /// such as Dialer, SMS, Settings, and About.
    /// </summary>
    public sealed class MainMenuPage : PageBase
    {
        /// <summary>
        /// Constructs the Main Menu page and logs its initialization.
        /// </summary>
        /// <param name="ui">Reference to the ConsoleUi host for navigation.</param>
        /// <param name="session">The shared AppSession containing runtime state.</param>
        public MainMenuPage(ConsoleUi ui, AppSession session) : base(ui, session)
        {
            Session.Status.Add("Main Menu ready.");
        }

        // --------------------------------------------------------------------
        // Rendering
        // --------------------------------------------------------------------

        /// <summary>
        /// Draws the Main Menu page, including:
        ///   - Control instructions
        ///   - Current modem readiness and call/SMS indicators
        ///   - Numbered menu options for navigation
        /// </summary>
        /// <param name="width">The console window width in characters.</param>
        /// <param name="height">The console window height in lines.</param>
        public override void Draw(int width, int height)
        {
            // Standard title bar (from PageBase)
            DrawTitleRow(width, Header("Main Menu"));

            int row = 2;
            SafeWrite(0, row++, "Use number keys to navigate. ESC exits.");
            SafeWrite(0, row++, "");

            // Render static numbered menu choices (single page layout)
            SafeWrite(0, row++, "  1) Dialing Menu");
            SafeWrite(0, row++, "  2) SMS Menu");
            SafeWrite(0, row++, "  3) Choose COM Ports (Settings)");
            SafeWrite(0, row++, "  4) Choose Audio Devices (Mic/Speaker)");
            SafeWrite(0, row++, "  5) View Live Log");
            SafeWrite(0, row++, "  9) About");
            SafeWrite(0, row++, "  0) Exit");
        }

        // --------------------------------------------------------------------
        // Input Handling / Navigation
        // --------------------------------------------------------------------

        /// <summary>
        /// Interprets user key input and dispatches to the corresponding page.
        /// Each keypress either pushes a new page, replaces the current one,
        /// or exits the application.
        /// </summary>
        /// <param name="key">The ConsoleKeyInfo event from the UI loop.</param>
        /// <param name="nav">Output parameter defining navigation action.</param>
        /// <returns>True if key was handled; otherwise false.</returns>
        public override bool HandleKey(ConsoleKeyInfo key, out PageNav nav)
        {
            nav = PageNav.None;

            switch (key.Key)
            {
                // 1 — Open Dialer Page
                case ConsoleKey.D1:
                    Ui.Push(new DialerPage(Ui, Session) { Previous = this });
                    nav = PageNav.Redraw;
                    return true;

                // 2 — Open SMS Menu Page
                case ConsoleKey.D2:
                    Ui.Push(new SmsMenuPage(Ui, Session) { Previous = this });
                    nav = PageNav.Redraw;
                    return true;

                // 3 — Go to Port Selection (Settings)
                case ConsoleKey.D3:
                    // "Settings" returns to the previous PortSelectionPage if available.
                    if (Previous != null)
                        Ui.ReplaceTop(Previous);
                    nav = PageNav.Redraw;
                    return true;

                case ConsoleKey.D4:
                    Ui.Push(new AudioDevicesPage(Ui, Session) { Previous = this });
                    nav = PageNav.Redraw;
                    return true;

                case ConsoleKey.D5:
                    Ui.Push(new LogViewerPage(Ui, Session) { Previous = this });
                    nav = PageNav.Redraw;
                    return true;

                // 9 — Open About Page
                case ConsoleKey.D9:
                    Ui.Push(new AboutPage(Ui, Session) { Previous = this });
                    nav = PageNav.Redraw;
                    return true;

                // 0 or ESC — Exit application
                case ConsoleKey.D0:
                case ConsoleKey.Escape:
                    nav = PageNav.ExitApp;
                    return true;

                // Unhandled key → do nothing
                default:
                    return false;
            }
        }
    }
}
