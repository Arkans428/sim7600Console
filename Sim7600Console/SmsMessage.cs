// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: SmsMessage.cs
// Project: SIM7600G-H Voice & SMS Console (C# .NET 8)
// ----------------------------------------------------------------------------
// PURPOSE
//   Defines the core data structure used to represent a single SMS message
//   within the application. This includes both metadata (e.g., sender,
//   timestamp, and message status) and the actual text body.
//
//   The structure is intentionally minimal, serving as a data transfer object
//   (DTO) between the modem interface (SmsManager) and the UI components
//   (SmsMenuPage, SmsReadPage, etc.).
//
// DESIGN NOTES
//   • The object is immutable after creation — all properties are `init`-only
//     to ensure message integrity once parsed from the modem.
//   • Default property values ensure that even partial modem responses do not
//     cause null reference issues in the UI.
//   • The “Index” property refers to the SIM or modem storage slot, not an
//     array index.
//
// TYPICAL USAGE FLOW
//   1. SmsManager reads and constructs SmsMessage from +CMGR response.
//   2. AppSession caches a summary via SmsListItem for quick display.
//   3. When user opens a message, SmsMessage provides full content for viewing.
// ============================================================================

namespace Sim7600Console
{
    /// <summary>
    /// Represents a complete SMS message stored or retrieved from the modem.
    /// Includes metadata and the message text body.
    /// </summary>
    public sealed class SmsMessage
    {
        /// <summary>
        /// The message storage index on the SIM/modem (as returned by +CMGL/+CMGR).
        /// </summary>
        public int Index { get; init; }

        /// <summary>
        /// The read/delivery status string (e.g., "REC UNREAD", "REC READ").
        /// </summary>
        public string Status { get; init; } = "REC UNREAD";

        /// <summary>
        /// The originating phone number of the message sender.
        /// Typically formatted with a leading '+' and country code.
        /// </summary>
        public string Sender { get; init; } = "";

        /// <summary>
        /// The timestamp string when the SMS was sent or received.
        /// Format typically follows "yy/MM/dd,HH:mm:ss±ZZ" (GSM standard).
        /// </summary>
        public string Timestamp { get; init; } = "";

        /// <summary>
        /// The actual text content of the SMS.
        /// May contain multiple lines depending on message size.
        /// </summary>
        public string Body { get; init; } = "";
    }
}
