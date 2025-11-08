// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: AppSession.cs
// Project: SIM7600G-H Console Dialer (C# .NET 8)
// ----------------------------------------------------------------------------
// PURPOSE
//   Provides a centralized, shared state container used by all components
//   (UI pages, controllers, and services) during a running session.
//
//   This design decouples the user interface from the modem logic and makes
//   the system testable — pages only read/write state here rather than owning
//   global variables.
//
// DESIGN
//   • AppSession is the single point of truth for all runtime information,
//     such as which COM ports are selected, modem readiness, and the cached
//     list of SMS metadata.
//   • StatusHub (referenced as `Status`) is injected at construction so every
//     class that references AppSession can log through a shared channel.
//   • The SMS list cache (`SmsList`) is updated by SmsManager and read by
//     UI pages that display messages.
//
//   This keeps responsibilities clearly divided:
//     - UI (Console pages):  show and navigate data
//     - Controllers:         manage devices and I/O
//     - AppSession:          store current app-wide state
// ============================================================================

using Sim7600Console.SMS;

namespace Sim7600Console.UI
{
    /// <summary>
    /// Represents the shared runtime context for the entire application.
    /// Holds the currently selected COM ports, modem readiness flags, incoming
    /// caller information, and a cached SMS list for display. This object is
    /// passed to every page and controller to coordinate state.
    /// </summary>
    public sealed class AppSession
    {
        /// <summary>
        /// Shared <see cref="StatusHub"/> instance for centralized logging.
        /// Every subsystem writes to it so the console UI can show a unified
        /// scrolling status area.
        /// </summary>
        public StatusHub Status { get; }

        // Selected audio devices (by NAudio WaveIn/WaveOut device index).
        // If null, AudioBridge will use system defaults.
        public int? AudioInDeviceIndex { get; set; }
        public int? AudioOutDeviceIndex { get; set; }

        // Friendly names for UI display (optional sugar).
        public string? AudioInDeviceName { get; set; }
        public string? AudioOutDeviceName { get; set; }

        public int? AudioInputDeviceId { get; set; }   // WaveIn device index (null = default)
        public int? AudioOutputDeviceId { get; set; }  // WaveOut device index (null = default)

        // ------------------ Connection Configuration ------------------

        /// <summary>
        /// Selected COM port for AT command communication (e.g., "COM4").
        /// Set by the port-selection page before modem initialization.
        /// </summary>
        public string? AtComPort { get; set; }

        /// <summary>
        /// Selected COM port for the serial-audio interface (e.g., "COM6").
        /// Set by the user alongside AtComPort in the setup menu.
        /// </summary>
        public string? AudioComPort { get; set; }

        // ------------------ Runtime Flags ------------------------------

        /// <summary>
        /// True when the modem has been successfully initialized and passed
        /// the basic “AT” handshake. Used to enable or disable certain menus.
        /// </summary>
        public bool ModemReady { get; set; } = false;

        /// <summary>
        /// Indicates that a new SMS notification (+CMTI) has been received.
        /// The main menu may display this to alert the user.
        /// </summary>
        public bool NewSmsIndicator { get; set; } = false;

        /// <summary>
        /// Stores the most recently reported caller ID number (via +CLIP) when
        /// an incoming call is detected. “Unknown” if no CLIP information was given.
        /// </summary>
        public string? IncomingCallerId { get; set; }

        public bool IsRinging { get; set; } = false;  // true while modem presents RING
        public bool InCall { get; set; } = false;     // true while call is active

        // ------------------ SMS Data Cache ------------------------------

        /// <summary>
        /// A lightweight in-memory cache of SMS metadata (index, sender,
        /// timestamp, and a short preview). Updated by <see cref="SmsManager"/>
        /// whenever the list is refreshed or messages are deleted/sent.
        /// </summary>
        public List<SmsListItem> SmsList { get; } = new();

        // ------------------ Construction -------------------------------

        /// <summary>
        /// Creates a new AppSession instance with the required StatusHub.
        /// </summary>
        public AppSession(StatusHub status)
        {
            Status = status ?? throw new ArgumentNullException(nameof(status));
        }
    }

    /// <summary>
    /// Represents a minimal summary of an SMS used for listing purposes.
    /// Each item includes:
    ///   - Index:    The message slot number in the SIM storage.
    ///   - Sender:   The originating number.
    ///   - Timestamp:Time when the SMS was received or sent.
    ///   - Preview:  A shortened version of the message content.
    ///
    /// The full message text is retrieved on-demand via <see cref="SmsManager.ReadAsync"/>.
    /// </summary>
    public sealed class SmsListItem
    {
        /// <summary>
        /// Numeric index of the message in modem storage (as reported by +CMGL or +CMGR).
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// The sender’s phone number. May be blank for messages sent from the SIM itself.
        /// </summary>
        public string Sender { get; set; } = "";

        /// <summary>
        /// Timestamp string (local time, modem-reported). 
        /// Typically in the format “yy/MM/dd,hh:mm:ss±tz”.
        /// </summary>
        public string Timestamp { get; set; } = "";

        /// <summary>
        /// A short snippet (preview) of the SMS body used in list displays.
        /// </summary>
        public string Preview { get; set; } = "";

        // NEW: where the message actually lives (SM or ME)
        public SmsStorage Storage { get; set; } = SmsStorage.SM;


    }
}
