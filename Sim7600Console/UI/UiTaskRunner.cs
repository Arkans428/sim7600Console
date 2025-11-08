// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: UiTaskRunner.cs
// Project: SIM7600G-H Voice & SMS Console (C# .NET 8)
// ----------------------------------------------------------------------------
// PURPOSE
//   Provides a unified pattern for launching asynchronous (async/await) tasks
//   from within console UI pages, without blocking user interaction or causing
//   unhandled exceptions to crash the program.
//
//   Because the console loop is synchronous by design, direct use of `await`
//   inside page event handlers (like HandleKey) would freeze the interface.
//   UiTaskRunner.Run() solves this by spawning the task on the thread pool and
//   safely logging all progress, success, and error messages to the Status Area.
//
// FEATURES
//   • Thread-safe async task launching for long operations
//   • Non-blocking execution for modem or I/O-bound commands
//   • Consistent user feedback messages (start / success / failure)
//   • Auto-captures and logs all exceptions
//
// EXAMPLE USAGE
//   ```csharp
//   UiTaskRunner.Run(
//       status: Session.Status,
//       startMsg: "Sending SMS...",
//       work: async () => await pc.SendSmsAsync("+15551234567", "Test message"),
//       successMsg: "SMS sent successfully."
//   );
//   ```
//
// DESIGN NOTES
//   - `Run()` is **fire-and-forget**: it launches a Task on the pool but does not
//     return a handle or require awaiting. This avoids freezing the UI loop.
//   - All thrown exceptions are caught internally and logged via StatusHub.
//   - The helper is lightweight and intended for high-level event use only
//     (e.g., responding to keypresses in page classes).
// ============================================================================

namespace Sim7600Console.UI
{
    /// <summary>
    /// Provides a simple way to execute asynchronous tasks from UI pages while
    /// maintaining responsiveness and consistent logging behavior.
    /// </summary>
    public static class UiTaskRunner
    {
        /// <summary>
        /// Runs an asynchronous task safely and logs progress updates.
        /// </summary>
        /// <param name="status">
        /// The shared <see cref="StatusHub"/> instance responsible for displaying
        /// messages in the console’s bottom Status Area.
        /// </param>
        /// <param name="startMsg">
        /// Message displayed immediately before the async task begins. Used to
        /// inform the user that an operation is in progress (e.g., "Dialing...").
        /// </param>
        /// <param name="work">
        /// The asynchronous operation to execute in the background. It may include
        /// modem commands, I/O work, or other time-consuming actions.
        /// </param>
        /// <param name="successMsg">
        /// Optional message displayed after successful completion. If omitted, no
        /// message is shown when the task finishes.
        /// </param>
        public static void Run(StatusHub status, string startMsg, Func<Task> work, string? successMsg = null)
        {
            if (status == null) throw new ArgumentNullException(nameof(status));
            if (work == null) throw new ArgumentNullException(nameof(work));

            // Announce task start to the operator immediately.
            status.Add(startMsg);

            // Launch the async work on a background thread without blocking the main UI loop.
            _ = Task.Run(async () =>
            {
                try
                {
                    // Execute the provided asynchronous function.
                    await work().ConfigureAwait(false);

                    // Log the success message, if provided.
                    if (!string.IsNullOrWhiteSpace(successMsg))
                        status.Add(successMsg!);
                }
                catch (Exception ex)
                {
                    // Capture and display any runtime exceptions gracefully.
                    // Prevents crashes while surfacing the issue to the user.
                    status.Add($"[AsyncError] {ex.Message}");
                }
            });
        }
    }
}
