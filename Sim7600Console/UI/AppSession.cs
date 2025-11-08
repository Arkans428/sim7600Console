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
//     such as which COM ports are selected, modem readiness, audio device IDs,
//     and the cached list of SMS metadata.
//   • StatusHub (referenced as `Status`) is injected at construction so every
//     class that references AppSession can log through a shared channel.
//   • The SMS list cache (`SmsList`) is updated by SmsManager and read by
//     UI pages that display messages.
//   • Additional transient state flags (e.g., IsRinging, InCall) allow
//     pages to react consistently to live events without direct event wiring.
//
//   This keeps responsibilities clearly divided:
//     - UI (Console pages):  show and navigate data
//     - Controllers:         manage devices and I/O
//     - AppSession:          store current app-wide state
//
//   AppSession should contain *no direct I/O logic* — only durable session data
//   and simple flag state transitions, ensuring separation of concerns.
// ============================================================================

using Sim7600Console.SMS;

namespace Sim7600Console.UI
{
    /// <summary>
    /// Represents the live runtime context for a single execution of the application.
    /// All major subsystems (UI pages, modem controllers, audio bridge) share this
    /// instance to maintain synchronized state and access to the global logging hub.
    /// </summary>
    public sealed class AppSession
    {
        // =====================================================================
        // LOGGING
        // =====================================================================

        /// <summary>
        /// Shared <see cref="StatusHub"/> instance used for system-wide logging.
        /// Every subsystem — UI, ModemControl, AudioBridge, and SMS Manager —
        /// writes messages here, which the console UI renders in the scrolling
        /// status area at the bottom of the screen.
        /// </summary>
        public StatusHub Status { get; }

        // =====================================================================
        // AUDIO CONFIGURATION
        // =====================================================================

        /// <summary>
        /// Index of the input device (microphone) currently selected via NAudio.
        /// If null, the system default capture device will be used.
        /// </summary>
        public int? AudioInDeviceIndex { get; set; }

        /// <summary>
        /// Index of the output device (speaker) currently selected via NAudio.
        /// If null, the system default playback device will be used.
        /// </summary>
        public int? AudioOutDeviceIndex { get; set; }

        /// <summary>
        /// Optional user-friendly name for the selected input device.
        /// Stored for display on UI pages; not required for functionality.
        /// </summary>
        public string? AudioInDeviceName { get; set; }

        /// <summary>
        /// Optional user-friendly name for the selected output device.
        /// Stored for display on UI pages.
        /// </summary>
        public string? AudioOutDeviceName { get; set; }

        /// <summary>
        /// Device ID used by <see cref="NAudio.Wave.WaveInEvent"/>.
        /// This may overlap conceptually with <see cref="AudioInDeviceIndex"/>,
        /// but is kept separate to support future audio enumeration strategies.
        /// </summary>
        public int? AudioInputDeviceId { get; set; }

        /// <summary>
        /// Device ID used by <see cref="NAudio.CoreAudioApi.MMDeviceEnumerator"/>.
        /// May differ from <see cref="AudioOutDeviceIndex"/> depending on backend.
        /// </summary>
        public int? AudioOutputDeviceId { get; set; }

        // =====================================================================
        // PORT CONFIGURATION
        // =====================================================================

        /// <summary>
        /// COM port name for the modem's AT command interface (e.g., "COM4").
        /// Assigned during the setup phase in the Port Selection page.
        /// </summary>
        public string? AtComPort { get; set; }

        /// <summary>
        /// COM port name for the modem's serial-audio stream (e.g., "COM6").
        /// Typically paired with the AT port during initialization.
        /// </summary>
        public string? AudioComPort { get; set; }

        // =====================================================================
        // RUNTIME FLAGS
        // =====================================================================

        /// <summary>
        /// Indicates whether the modem has successfully initialized (responded to "AT")
        /// and is ready for command/voice/SMS operations.
        /// Pages and menus use this flag to enable or disable modem-dependent actions.
        /// </summary>
        public bool ModemReady { get; set; } = false;

        /// <summary>
        /// True when a new SMS (+CMTI) has been detected but not yet read.
        /// Used by UI layers to display a visual "NEW SMS" indicator.
        /// </summary>
        public bool NewSmsIndicator { get; set; } = false;

        /// <summary>
        /// Caller ID string from the most recent +CLIP notification.
        /// Set when the modem reports an incoming call; cleared after ringing ends.
        /// If CLIP is disabled or unavailable, this may remain "Unknown".
        /// </summary>
        public string? IncomingCallerId { get; set; }

        /// <summary>
        /// True while the modem is reporting a RING event (i.e., an active incoming call).
        /// </summary>
        public bool IsRinging { get; set; } = false;

        /// <summary>
        /// True when the call is currently active (after ATA / connection established).
        /// </summary>
        public bool InCall { get; set; } = false;

        // =====================================================================
        // SMS CACHE
        // =====================================================================

        /// <summary>
        /// In-memory list of SMS metadata currently retrieved from the modem.
        /// Each entry contains minimal identifying information (index, sender,
        /// timestamp, and preview). The message body is only fetched when opened.
        ///
        /// This cache is refreshed by <see cref="Sim7600Console.SMS.SmsManager"/>
        /// during <c>RefreshSmsListAsync()</c> and is used by list display pages.
        /// </summary>
        public List<SmsListItem> SmsList { get; } = new();

        // =====================================================================
        // CONSTRUCTION
        // =====================================================================

        /// <summary>
        /// Creates a new shared session context.
        /// Requires a <see cref="StatusHub"/> instance for centralized logging.
        /// </summary>
        /// <param name="status">Shared status log sink used throughout the app.</param>
        public AppSession(StatusHub status)
        {
            Status = status ?? throw new ArgumentNullException(nameof(status));
        }
    }

    // =====================================================================
    // SUPPORTING TYPE: SMS LIST ITEM
    // =====================================================================

    /// <summary>
    /// Represents a lightweight summary of an SMS used for list displays.
    /// Contains only metadata fields to minimize memory usage and parsing overhead.
    /// Full message text is retrieved on-demand via <see cref="SmsManager.ReadAsync"/>.
    /// </summary>
    public sealed class SmsListItem
    {
        /// <summary>
        /// The storage index of this SMS within the modem’s memory (as reported by +CMGL or +CMGR).
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// The sender’s phone number. For outgoing messages, this may represent
        /// the destination instead.
        /// </summary>
        public string Sender { get; set; } = "";

        /// <summary>
        /// Timestamp string as returned by the modem.
        /// Usually formatted as “yy/MM/dd,hh:mm:ss±tz”, though format may vary
        /// with firmware or regional configuration.
        /// </summary>
        public string Timestamp { get; set; } = "";

        /// <summary>
        /// Shortened one-line preview of the message body (for listing efficiency).
        /// Populated during <c>SmsManager.ListAsync()</c>.
        /// </summary>
        public string Preview { get; set; } = "";

        /// <summary>
        /// Indicates where the message is physically stored:
        ///   • <see cref="SmsStorage.SM"/> → SIM card
        ///   • <see cref="SmsStorage.ME"/> → Modem internal memory
        ///
        /// This is used so operations like <c>AT+CMGD</c> can target the correct
        /// memory bank when deleting or reading messages.
        /// </summary>
        public SmsStorage Storage { get; set; } = SmsStorage.SM;
    }
}
