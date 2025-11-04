// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: ModemEvents.cs
// Project: SIM7600G-H Console Dialer (C# .NET 8)
// ----------------------------------------------------------------------------
// PURPOSE
//   Defines Plain Old CLR Objects (POCOs) for modem-related event payloads.
//   These lightweight classes act as typed data carriers for higher-level
//   event systems, allowing for more structured communication between
//   ModemControl, PhoneController, and potential future modules such as
//   a GUI or network-based controller.
//
//   While the current implementation of the console app uses delegate-based
//   events (e.g., Action<string?>), these classes serve as a foundation for
//   expansion toward strongly-typed event handling (e.g., via IObservable<T>,
//   EventAggregator patterns, or dependency-injected event buses).
//
// DESIGN NOTES
//   • Each class represents a distinct modem notification type.
//   • They use `init` properties to ensure immutability once created.
//   • Minimal dependencies — simple and serializable by default.
//   • Facilitates future expansion to logging, JSON-based IPC, or unit testing.
//
// FUTURE EXPANSION
//   Possible future uses include:
//     - JSON serialization for event logging or diagnostics
//     - Integration with real-time dashboards or web interfaces
//     - Unified event bus for multi-threaded communication
//     - Richer payloads (e.g., timestamp, signal strength, network info)
//
// EVENT MODEL SUMMARY
//   Event Type        | Origin              | Example Use
//   ------------------|--------------------|---------------------------------------
//   IncomingCallEvent | ModemControl       | Triggered when +CLIP or RING detected
//   CallEndedEvent    | ModemControl       | Triggered when NO CARRIER or END seen
//   NewSmsEvent       | ModemControl/SMS   | Triggered when +CMTI:<index> received
//   ErrorEvent        | ModemControl       | Triggered when +CME ERROR/+CMS ERROR
// ============================================================================

namespace Sim7600Console
{
    /// <summary>
    /// Represents an incoming voice call notification event.
    /// This may be triggered when the modem emits "RING" or "+CLIP"
    /// unsolicited result codes.
    /// </summary>
    public sealed class IncomingCallEvent
    {
        /// <summary>
        /// The caller’s phone number extracted from the +CLIP URC, if available.
        /// May be null if the network does not provide caller ID.
        /// </summary>
        public string? CallerId { get; init; }
    }

    /// <summary>
    /// Represents the termination of a voice call — either due to hang-up,
    /// remote disconnect, or network failure. No extra payload data is
    /// included at this stage.
    /// </summary>
    public sealed class CallEndedEvent
    {
        // Placeholder for future properties (e.g., duration, cause code, etc.)
    }

    /// <summary>
    /// Represents a new SMS notification from the modem.
    /// Typically raised when a +CMTI URC (message storage index) is received.
    /// </summary>
    public sealed class NewSmsEvent
    {
        /// <summary>
        /// The index within the modem’s SMS storage (e.g., SIM or ME)
        /// where the new message was stored. Corresponds to the +CMTI:<index> field.
        /// </summary>
        public int Index { get; init; }
    }

    /// <summary>
    /// Represents an error notification emitted by the modem, typically due
    /// to a +CME ERROR or +CMS ERROR URC. Can also encapsulate system-level
    /// errors detected by the program (I/O or timeout).
    /// </summary>
    public sealed class ErrorEvent
    {
        /// <summary>
        /// Human-readable message describing the error condition.
        /// May include the original modem response (e.g., "+CME ERROR: 515").
        /// </summary>
        public string Message { get; init; } = "";
    }
}
