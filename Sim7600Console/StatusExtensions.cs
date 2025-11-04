// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: StatusExtensions.cs
// Project: SIM7600G-H Voice & SMS Console (C# .NET 8)
// ----------------------------------------------------------------------------
// PURPOSE
//   Provides small but convenient extension methods for the StatusHub class,
//   enabling consistent, semantically tagged log messages throughout the
//   program’s Status Area (the scrolling log at the bottom of the console UI).
//
//   These helpers make code more readable and maintain a clear distinction
//   between informational messages, error logs, and general notes.
//
// DESIGN RATIONALE
//   • Encourages consistent message prefixes such as “[ERROR]” or “[Note]”.
//   • Keeps log message generation concise in calling code (e.g., `status.Error()`).
//   • Does not modify StatusHub functionality — purely syntactic sugar.
//   • Useful for future expansion (e.g., color-coding or log severity filters).
//
// EXAMPLE USAGE
//   ```csharp
//   status.Info("Port initialized successfully.");
//   status.Error("Failed to open audio stream.");
//   status.Note("Try reconnecting the modem and retrying.");
//   ```
//
// RELATIONSHIP
//   - Works in tandem with StatusHub.Add(), which handles timestamping,
//     storage, and file output.
//   - Integrated by any class that references StatusHub (e.g., PhoneController,
//     AudioBridge, ModemControl).
// ============================================================================

namespace Sim7600Console
{
    /// <summary>
    /// Extension methods that simplify and standardize logging
    /// through the <see cref="StatusHub"/> class.
    /// </summary>
    public static class StatusExtensions
    {
        /// <summary>
        /// Logs a plain informational message (neutral event).
        /// This is equivalent to calling <see cref="StatusHub.Add(string)"/>.
        /// </summary>
        /// <param name="s">The StatusHub instance to write to.</param>
        /// <param name="msg">The message text to record.</param>
        public static void Info(this StatusHub s, string msg) => s.Add(msg);

        /// <summary>
        /// Logs an error message with a standardized “[ERROR]” prefix,
        /// allowing faster visual identification of problems in the log.
        /// </summary>
        /// <param name="s">The StatusHub instance to write to.</param>
        /// <param name="msg">The error message text.</param>
        public static void Error(this StatusHub s, string msg) => s.Add("[ERROR] " + msg);

        /// <summary>
        /// Logs a neutral note or progress annotation with a “[Note]” prefix.
        /// Ideal for hints, progress checkpoints, or non-critical observations.
        /// </summary>
        /// <param name="s">The StatusHub instance to write to.</param>
        /// <param name="msg">The note text to display and store.</param>
        public static void Note(this StatusHub s, string msg) => s.Add("[Note] " + msg);
    }
}
