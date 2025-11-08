// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: StatusHub.cs
// Project: SIM7600G-H Voice & SMS Console (C# .NET 8)
// ----------------------------------------------------------------------------
// PURPOSE
//   Centralized, thread-safe logging hub for all runtime status updates,
//   system messages, and error reports. Acts as the data backbone for the
//   scrolling Status Area displayed in the console UI.
//
//   The class provides:
//     • In-memory rolling buffer of the most recent log entries.
//     • Optional persistent log file sink for long-term traceability.
//     • A monotonically increasing Version counter for efficient UI redraws.
//
// DESIGN FEATURES
//   • Thread-Safe Logging:
//       All Add/AddBlock operations are protected by a lock to ensure
//       consistent writes even when multiple threads log concurrently
//       (e.g., async tasks, background threads).
//
//   • Rolling In-Memory Log:
//       Maintains a capped collection of the most recent entries, ensuring
//       memory usage remains bounded regardless of runtime duration.
//
//   • Persistent Log File (Optional):
//       If a log file path is specified at initialization, all messages
//       are also appended to disk for offline debugging or diagnostics.
//
//   • Version Counter:
//       Incremented each time a new message is added, allowing the UI layer
//       to detect log updates without polling the message list directly.
//
//   • Robustness:
//       File I/O errors (e.g., permissions, disk full) are caught and ignored
//       to prevent runtime crashes — logging is best-effort.
//
// EXAMPLE USAGE
//   ```csharp
//   var hub = new StatusHub(maxEntries: 400, logFilePath: "sim7600-log.txt");
//   hub.Add("System initialized.");
//   hub.Add("[Audio] Port opened successfully.");
//   var lastLines = hub.GetTail(10);
//   ```
//
// RELATIONSHIP
//   • Read by ConsoleUi for live screen updates.
//   • Written to by nearly every subsystem (ModemControl, AudioBridge,
//     PhoneController, PortSelectionPage, etc.).
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace Sim7600Console.UI
{
    /// <summary>
    /// Provides a thread-safe rolling log that stores and optionally persists
    /// status messages for both the console UI and an external log file.
    /// </summary>
    public sealed class StatusHub
    {
        // Maximum number of messages retained in the rolling buffer.
        private readonly int _maxEntries;

        // Synchronization lock for thread-safe access to _entries and Version.
        private readonly object _lock = new();

        // Internal rolling list of log entries.
        private readonly LinkedList<string> _entries = new();

        // Optional file path for persistent logging.
        private readonly string? _logFilePath;

        /// <summary>
        /// Version counter incremented on every new entry.
        /// Used by the ConsoleUi to detect when new lines are available.
        /// </summary>
        public int Version { get; private set; }

        /// <summary>
        /// Initializes a new StatusHub with optional file logging.
        /// </summary>
        /// <param name="maxEntries">Maximum number of in-memory log lines retained.</param>
        /// <param name="logFilePath">
        /// Optional file path to append log messages to.
        /// If null or empty, logging will remain in-memory only.
        /// </param>
        public StatusHub(int maxEntries = 300, string? logFilePath = null)
        {
            _maxEntries = Math.Max(50, maxEntries);
            _logFilePath = logFilePath;

            // Log initial startup context for debugging purposes.
            Add($"[{DateTime.Now:T}] StatusHub initialized. Instance: {InstanceInfo.InstanceId}");

            if (!string.IsNullOrWhiteSpace(_logFilePath))
                Add($"Logging to: {_logFilePath}");
        }

        /// <summary>
        /// Adds a single message to the rolling log buffer and optional file sink.
        /// Thread-safe and timestamped automatically.
        /// </summary>
        /// <param name="message">Message text to log.</param>
        public void Add(string message)
        {
            // Prepend an ISO-like timestamp for consistent formatting.
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";

            lock (_lock)
            {
                _entries.AddLast(line);

                // Enforce rolling buffer: remove oldest entries when capacity exceeded.
                while (_entries.Count > _maxEntries)
                    _entries.RemoveFirst();

                Version++; // Notify listeners (UI) that log content changed.

                // Write to file sink if configured.
                WriteToFile_NoThrow(line);
            }
        }

        /// <summary>
        /// Adds a multi-line message block (each line treated as an individual entry).
        /// Useful for commands that return multiple response lines or URCs.
        /// </summary>
        /// <param name="message">Multiline text to split and append.</param>
        public void AddBlock(string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            // Split lines and log each individually to maintain chronological order.
            var lines = message.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (var l in lines)
                Add(l);
        }

        /// <summary>
        /// Retrieves the last N lines from the in-memory rolling buffer.
        /// This is used by ConsoleUi to populate the Status Area.
        /// </summary>
        /// <param name="tailCount">Number of lines to retrieve from the end of the log.</param>
        /// <returns>A list of the most recent log entries.</returns>
        public List<string> GetTail(int tailCount)
        {
            lock (_lock)
            {
                tailCount = Math.Max(0, tailCount);
                var result = new List<string>(tailCount);

                // Calculate how many lines to skip from the front.
                int skip = Math.Max(0, _entries.Count - tailCount);

                int i = 0;
                foreach (var line in _entries)
                {
                    if (i++ < skip) continue;
                    result.Add(line);
                }

                return result;
            }
        }

        /// <summary>
        /// Appends a line to the configured log file without throwing exceptions
        /// on I/O errors. Ensures the app remains stable even if the disk fails.
        /// </summary>
        /// <param name="line">Log line to append.</param>
        private void WriteToFile_NoThrow(string line)
        {
            if (string.IsNullOrWhiteSpace(_logFilePath)) return;

            try
            {
                File.AppendAllText(_logFilePath!, line + Environment.NewLine);
            }
            catch
            {
                // Swallow exceptions silently — the console must not crash due to I/O errors.
            }
        }
    }
}
