// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: SmsStore.cs
// Project: SIM7600G-H Voice & SMS Console (C# .NET 8)
// ----------------------------------------------------------------------------
// PURPOSE
//   Provides an optional in-memory cache for SMS messages retrieved from the
//   SIM7600G-H modem. This store enables quick access to messages by their
//   modem storage index (as reported by +CMGL and +CMGR commands).
//
//   While the current UI primarily uses AppSession.SmsList for lightweight
//   metadata display, SmsStore allows for future enhancements such as:
//     • Persistent local storage of messages between sessions.
//     • Indexed lookups by sender, date, or read status.
//     • Fast re-access of already-read messages without modem re-query.
//
// DESIGN NOTES
//   • Uses a simple dictionary keyed by message index (int).
//   • The Upsert() method simplifies replacement or insertion of messages
//     without needing to check for existence first.
//   • Thread-safety is not implemented — this store assumes single-threaded
//     access in the current console environment.
//   • The class is sealed for safety and simplicity (no inheritance intended).
//
// USAGE FLOW
//   - SmsManager reads messages from the modem and calls Upsert().
//   - The UI can later call TryGet() to quickly access cached messages.
//   - Delete operations trigger Remove(), keeping cache synchronized.
// ============================================================================

using System.Collections.Generic;

namespace Sim7600Console
{
    /// <summary>
    /// Maintains an in-memory dictionary of SMS messages keyed by their
    /// modem storage index. Useful for quick access to previously loaded
    /// messages without re-querying the modem.
    /// </summary>
    public sealed class SmsStore
    {
        // Internal lookup table of messages by index.
        private readonly Dictionary<int, SmsMessage> _byIndex = new();

        /// <summary>
        /// Inserts a new message or replaces an existing one with the same index.
        /// </summary>
        /// <param name="msg">The message to store or update.</param>
        public void Upsert(SmsMessage msg) => _byIndex[msg.Index] = msg;

        /// <summary>
        /// Attempts to retrieve a cached message by its storage index.
        /// </summary>
        /// <param name="index">The storage index of the SMS message.</param>
        /// <param name="msg">Outputs the cached message if found.</param>
        /// <returns>True if the message was found in the cache; otherwise false.</returns>
        public bool TryGet(int index, out SmsMessage msg) => _byIndex.TryGetValue(index, out msg!);

        /// <summary>
        /// Removes a cached message entry by its storage index, if it exists.
        /// </summary>
        /// <param name="index">The storage index of the message to remove.</param>
        public void Remove(int index)
        {
            if (_byIndex.ContainsKey(index))
                _byIndex.Remove(index);
        }

        /// <summary>
        /// Clears the entire in-memory cache of all stored messages.
        /// </summary>
        public void Clear() => _byIndex.Clear();
    }
}
