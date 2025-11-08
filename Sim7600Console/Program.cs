// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: Program.cs
// Project: SIM7600G-H Voice & SMS Console (C# .NET 8)
// ----------------------------------------------------------------------------
// PURPOSE
//   Defines the application’s entry point and bootstraps the console framework.
//   This file ties together the StatusHub, UI layer, and main navigation pages
//   (PortSelectionPage and MainMenuPage), launching the fully interactive
//   console environment.
//
// FUNCTIONAL OVERVIEW
//   1. Initializes the console window (title and behavior).
//   2. Sets up centralized status logging (both in-memory and to a file).
//   3. Creates the AppSession object to hold runtime state shared across pages.
//   4. Creates the initial pages and links them via navigation references.
//   5. Starts the ConsoleUi event loop and blocks until exit.
//   6. Cleans up the console and exits gracefully.
//
// DESIGN NOTES
//   • Console.TreatControlCAsInput = true prevents Ctrl+C from terminating
//     the app abruptly — allowing it to be handled as normal key input.
//   • StatusHub handles dual logging (console + file) with thread safety.
//   • The log file (“sim7600-log.txt”) is created in the same directory
//     as the executable to simplify post-run analysis or bug reports.
//   • The console UI operates in a single-threaded loop; all async operations
//     (like modem initialization) are safely offloaded using UiTaskRunner.
//
// FILE OUTPUT
//   The log file contains all session logs, including:
//     - AT command transactions
//     - Audio routing transitions
//     - Call and SMS events
//     - User interaction traces
//
// UX FLOW
//   On startup:
//     [1] PortSelectionPage — prompts for AT and Audio COM ports.
//     [2] MainMenuPage      — provides access to Dialer, SMS, Settings, etc.
//     [3] When the user exits, console clears and prints a friendly message.
//
// PLATFORM NOTES
//   [SupportedOSPlatform("windows")]
//   The app targets Windows specifically due to the dependency on:
//     - System.IO.Ports.SerialPort
//     - NAudio (Windows-specific wave APIs)
// ============================================================================

using System;
using System.Runtime.Versioning;
using Sim7600Console.UI.Pages;
using Sim7600Console.UI;

namespace Sim7600Console
{
    [SupportedOSPlatform("windows")]
    internal static class Program
    {
        /// <summary>
        /// Application entry point. Configures the console environment,
        /// initializes all shared services, and launches the main UI loop.
        /// </summary>
        private static void Main()
        {
            // -----------------------------
            // Console Setup
            // -----------------------------
            // Set a descriptive window title for easier taskbar recognition.
            Console.Title = "SIM7600G-H Voice & SMS Console (AI Build)";

            // Disable Ctrl+C as a hard kill; treat it as normal input so the
            // program can manage it gracefully.
            Console.TreatControlCAsInput = true;

            // -----------------------------
            // Logging Setup
            // -----------------------------
            // Persistent log file for session data (rotates manually if needed).
            var logPath = "sim7600-log.txt";

            // StatusHub handles both on-screen status updates and file output.
            // We increase the in-memory buffer to 400 lines for smoother scrolling.
            var statusHub = new StatusHub(maxEntries: 400, logFilePath: logPath);

            // -----------------------------
            // Core Session Objects
            // -----------------------------
            // Shared application state — includes COM port selections, SMS list, etc.
            var session = new AppSession(statusHub);

            // Console UI manager handles rendering and navigation stack.
            var ui = new ConsoleUi(statusHub);

            // -----------------------------
            // Page Initialization
            // -----------------------------
            // The port selection page appears first at startup.
            var portSelect = new PortSelectionPage(ui, session);

            // Once ports are selected and initialization succeeds,
            // the app transitions to the main menu.
            var mainMenu = new MainMenuPage(ui, session);

            // Link pages to establish forward/backward navigation flow.
            portSelect.Next = mainMenu;
            mainMenu.Previous = portSelect;

            // -----------------------------
            // UI Execution Loop
            // -----------------------------
            // Push the initial page and enter the event loop.
            ui.Push(portSelect);
            ui.Run();

            // -----------------------------
            // Graceful Exit
            // -----------------------------
            Console.Clear();
            Console.WriteLine("Goodbye.");
        }
    }
}
