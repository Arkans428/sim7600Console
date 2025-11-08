// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: ModemControl.cs
// Project: SIM7600G-H Console Dialer (C# .NET 8)
// ----------------------------------------------------------------------------
// PURPOSE
//   Robust AT-command driver for a SIM7600G-H modem. This class owns the AT
//   SerialPort, provides synchronous command/response helpers with timeouts,
//   parses unsolicited result codes (URCs), and raises high-level events that
//   the PhoneController subscribes to (incoming call, call ended, new SMS, etc.).
//
// KEY RESPONSIBILITIES
//   • Open and configure the AT serial port (8N1, no HW flow control).
//   • Send AT commands and wait for terminal responses (“OK”/“ERROR”) safely.
//   • Buffer partial serial reads and normalize them into a textual response.
//   • Parse URCs (RING, +CLIP, NO CARRIER, VOICE CALL: BEGIN/END, +CMTI).
//   • Notify the controller via events (OnIncomingCall, OnCallEnded, OnNewSms).
//
// CONCURRENCY MODEL
//   • SerialPort.DataReceived may fire on a worker thread at any time.
//   • _sendLock guards the command/response exchange so that SendAsync() can
//     synchronously wait for “OK/ERROR” while DataReceived keeps appending.
//   • An AutoResetEvent (_respEvent) signals the arrival of a terminal response.
//   • URC parsing is lightweight and tolerant of chunking/partial lines.
//
// RESPONSE MODEL
//   • SendAsync returns the *entire* textual buffer captured until a terminal
//     “OK” or “ERROR” is seen. The string may include command echo and URCs
//     depending on modem configuration (we disable echo with ATE0 during init).
//   • SendExpectOkAsync wraps SendAsync and throws if “OK” is not present.
//
// ERROR HANDLING
//   • Any SerialPort errors are logged via StatusHub and do not crash the app.
//   • If the modem does not respond to initial “AT”, OpenAsync throws so the
//     caller can abort initialization cleanly.
//
// EXTENSIBILITY
//   • Add more URC branches in ParseUrclines for features you enable (e.g. SMS
//     PDU notifications, data attach URCs, call waiting, etc.).
//   • If you prefer a typed response object, wrap SendAsync and parse there.
//
// NOTE ABOUT “VOICE CALL: BEGIN” CALLBACK
//   • Some SIM7600 firmwares emit a “VOICE CALL: BEGIN” line. We opportunistically
//     call an AppDomain data hook named "sim7600_voice_cb" (if present) to notify
//     the controller’s NotifyVoiceCallBegin() without creating a circular reference.
//     If you wire events differently, you can remove that hook safely.
// ============================================================================

using Sim7600Console.UI;
using System.IO.Ports;
using System.Text;

namespace Sim7600Console
{
    /// <summary>
    /// Manages AT command I/O and URC parsing for a SIM7600G-H modem.
    /// </summary>
    public sealed class ModemControl : IDisposable
    {
        // ---- Collaborators & configuration ---------------------------------------------

        private readonly StatusHub _status;  // Central status/log sink for the application
        private readonly string _portName;   // e.g., "COM4"

        /// <summary>
        /// The AT SerialPort (8N1, no hardware flow control). Opened by <see cref="OpenAsync"/>.
        /// </summary>
        private readonly SerialPort _port;

        // ---- Command/response synchronization ------------------------------------------

        /// <summary>
        /// Accumulates raw serial text until a terminal response (“OK”/“ERROR”) is detected.
        /// </summary>
        private readonly StringBuilder _rxBuffer = new();

        /// <summary>
        /// Signaled by DataReceived when a terminal response was observed; awaited by SendAsync.
        /// </summary>
        private readonly AutoResetEvent _respEvent = new(false);

        /// <summary>
        /// Snapshot of the most recent terminal response text (set by DataReceived).
        /// </summary>
        private string? _lastResponse;

        /// <summary>
        /// Protects the command/response critical section so only one SendAsync is in flight.
        /// </summary>
        private readonly object _sendLock = new();

        private bool _disposed;

        // ---- Lightweight state ----------------------------------------------------------

        /// <summary>
        /// True after the modem responded to “AT” during <see cref="OpenAsync"/>.
        /// </summary>
        private volatile bool _ready;

        // ---- Events surfaced to upper layers -------------------------------------------

        /// <summary>Raised when the “ready” state changes (initial handshake success/failure).</summary>
        public event Action<bool>? OnReadyChanged;

        /// <summary>Raised upon RING/+CLIP. Parameter is the caller number if known, otherwise null.</summary>
        public event Action<string?>? OnIncomingCall;

        /// <summary>Raised upon call teardown URCs (NO CARRIER, VOICE CALL: END, BUSY).</summary>
        public event Action? OnCallEnded;

        /// <summary>Raised on +CMTI: a new SMS has been stored at the indicated index.</summary>
        public event Action<int>? OnNewSms;

        /// <summary>Raised on +CME ERROR / +CMS ERROR lines to surface modem errors.</summary>
        public event Action<string>? OnError;

        /// <summary>True if the AT port is open.</summary>
        public bool IsOpen => _port.IsOpen;

        // ------------------------------------------------------------------------------
        // Construction
        // ------------------------------------------------------------------------------

        /// <summary>
        /// Create a new ModemControl bound to a specific COM port and baud.
        /// </summary>
        /// <param name="status">Status/log aggregator.</param>
        /// <param name="portName">COM port for the AT interface (e.g., "COM4").</param>
        /// <param name="baud">Baud rate for the AT port (115200 default).</param>
        public ModemControl(StatusHub status, string portName, int baud = 115200)
        {
            _status = status ?? throw new ArgumentNullException(nameof(status));
            _portName = portName ?? throw new ArgumentNullException(nameof(portName));

            // Configure the AT SerialPort. We use no hardware flow control on the AT port.
            _port = new SerialPort(_portName, baud, Parity.None, 8, StopBits.One)
            {
                DtrEnable = true,               // Keep DTR asserted; some modems expect it
                RtsEnable = true,               // RTS on (no handshake, but harmless asserted)
                Handshake = Handshake.None,     // AT link rarely needs RTS/CTS
                Encoding = Encoding.ASCII,     // AT commands are ASCII text
                NewLine = "\r\n",             // Convenience for line handling
                ReadTimeout = 1000,               // Modest timeouts; SendAsync uses its own event too
                WriteTimeout = 1000
            };

            // Serial event handlers
            _port.DataReceived += OnDataReceived;
            _port.ErrorReceived += (s, e) =>
            {
                // Serial transport errors (framing, overrun). Not fatal; just log.
                _status.Add($"[AT] Serial error: {e.EventType}");
            };
        }

        // ------------------------------------------------------------------------------
        // Open / handshake
        // ------------------------------------------------------------------------------

        /// <summary>
        /// Opens the AT port and probes the modem with “AT” up to 4 times.
        /// Sets the <see cref="_ready"/> flag and raises <see cref="OnReadyChanged"/>.
        /// Throws if the modem never replies “OK”.
        /// </summary>
        public async Task OpenAsync(CancellationToken ct = default)
        {
            _port.Open();
            _status.Add($"[AT] Opened {_port.PortName}.");

            // Probe readiness — some drivers/USB stacks need a beat to settle.
            var ok = false;
            for (int i = 0; i < 4 && !ok; i++)
            {
                try
                {
                    var resp = await SendAsync("AT", 1000, ct).ConfigureAwait(false);
                    ok = resp.Contains("OK", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    // Ignore transient failures and retry
                }
            }

            _ready = ok;
            OnReadyChanged?.Invoke(_ready);
            if (!ok) throw new InvalidOperationException("Modem did not respond to AT.");
        }

        // ------------------------------------------------------------------------------
        // Command helpers
        // ------------------------------------------------------------------------------

        /// <summary>
        /// Sends an AT command and returns the raw textual response captured until a terminal
        /// “OK” or “ERROR” is observed, or until the timeout elapses.
        /// </summary>
        /// <param name="cmd">The AT command text without trailing CR (e.g., "AT+CREG?").</param>
        /// <param name="timeoutMs">Timeout to wait for a terminal response in milliseconds.</param>
        /// <param name="ct">Cancellation token (cooperates with the waiting task).</param>
        /// <returns>
        /// Full response text, including any echoed command/URCs that arrived before the
        /// terminal line. Line endings are preserved as CR/LF pairs in the buffer; for log
        /// readability we show them as “\r\n”.
        /// </returns>
        /// <exception cref="InvalidOperationException">If the port is not open.</exception>
        public async Task<string> SendAsync(string cmd, int timeoutMs, CancellationToken ct = default)
        {
            EnsureNotDisposed();
            if (!IsOpen) throw new InvalidOperationException("AT port not open.");

            // Enter the command/response critical section. We reset shared state here so
            // DataReceived knows we are waiting for a fresh terminal response.
            lock (_sendLock)
            {
                _rxBuffer.Clear();
                _lastResponse = null;
                _respEvent.Reset();

                _status.Add($"[AT->] {cmd}");
                _port.Write(cmd + "\r"); // All AT commands end with CR
            }

            // Wait (on a worker task) for DataReceived to signal a terminal response.
            var wait = Task.Run(() => _respEvent.WaitOne(timeoutMs), ct);
            var done = await wait.ConfigureAwait(false);

            // Even if “done == false” (timeout), we still return what we have in the buffer;
            // the caller can decide whether to treat it as an error.
            string resp;
            lock (_sendLock)
            {
                resp = _lastResponse ?? _rxBuffer.ToString();
            }

            // Log what we saw in a visually compact way
            _status.Add($"[AT<-] {(string.IsNullOrWhiteSpace(resp) ? "(no response)" : resp.Replace("\r", "\\r").Replace("\n", "\\n"))}");
            return resp;
        }

        /// <summary>
        /// Sends an AT command and throws if a terminal “OK” is not present in the response.
        /// Useful for one-liners where only success/failure matters.
        /// </summary>
        public async Task SendExpectOkAsync(string cmd, int timeoutMs, CancellationToken ct = default)
        {
            var r = await SendAsync(cmd, timeoutMs, ct).ConfigureAwait(false);

            // Accept various “OK” placements (start/end/CRLF variants) to be lenient
            if (!r.Contains("\nOK\n", StringComparison.OrdinalIgnoreCase) &&
                !r.EndsWith("\nOK", StringComparison.OrdinalIgnoreCase) &&
                !r.StartsWith("OK", StringComparison.OrdinalIgnoreCase) &&
                !r.Contains("OK\r\n", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Command failed or timed out: {cmd}\nResponse:\n{r}");
            }
        }

        // ------------------------------------------------------------------------------
        // Serial receive & URC parsing
        // ------------------------------------------------------------------------------

        /// <summary>
        /// SerialPort.DataReceived handler: reads any available text, appends to the shared
        /// buffer, detects terminal responses for SendAsync, and forwards lines to the URC
        /// parser. The handler is short and resilient to chunking.
        /// </summary>
        private void OnDataReceived(object? sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                string chunk = _port.ReadExisting(); // Read whatever the driver has
                if (string.IsNullOrEmpty(chunk)) return;

                // 1) Append to the shared response buffer and detect terminal lines.
                lock (_sendLock)
                {
                    _rxBuffer.Append(chunk);
                    var text = _rxBuffer.ToString();

                    // A terminal response ends a SendAsync wait. We don’t try to be clever:
                    // when either OK or ERROR appears delimited by line breaks, we release.
                    if (text.Contains("\r\nOK\r\n") || text.Contains("\nOK\n") ||
                        text.Contains("\r\nERROR\r\n") || text.Contains("\nERROR\n"))
                    {
                        _lastResponse = text;   // Snapshot for SendAsync to read safely
                        _respEvent.Set();       // Wake the waiter
                    }
                }

                // 2) Parse URCs outside the lock; treat chunk as an independent piece of text.
                ParseUrclines(chunk);
            }
            catch (Exception ex)
            {
                _status.Add($"[AT RX] Exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Parses unsolicited result codes (URCs). The input is a raw text chunk from the
        /// serial driver; we split by LF, trim, and match known patterns. Parsing is tolerant
        /// of partial lines and multiple URCs per chunk.
        /// </summary>
        private void ParseUrclines(string chunk)
        {
            // Normalize to LF-only to simplify matching
            var lines = chunk.Replace("\r", string.Empty)
                             .Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                // ---- Call-related URCs ---------------------------------------------------

                if (line.Equals("RING", StringComparison.OrdinalIgnoreCase))
                {
                    // Some networks send several RINGs before +CLIP arrives.
                    OnIncomingCall?.Invoke(null); // Unknown caller yet
                    continue;
                }

                if (line.StartsWith("+CLIP:", StringComparison.OrdinalIgnoreCase))
                {
                    // +CLIP: "<number>",<toa>,<subaddr>,<satype>,<alpha>,<CLI validity>
                    var num = ModemUrp.ExtractClipNumber(line);
                    OnIncomingCall?.Invoke(num);
                    continue;
                }

                if (line.Contains("VOICE CALL: BEGIN", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    // The call is now active. Instead of raising an event here (which would
                    // require another public event), we provide a low-coupling callback hook.
                    // PhoneController installs an Action into the current AppDomain under the
                    // key "sim7600_voice_cb". If it’s present, we invoke it now.
                    try
                    {
                        (AppDomain.CurrentDomain.GetData("sim7600_voice_cb") as Action)?.Invoke();
                    }
                    catch
                    {
                        // Best effort — failure here is non-fatal; the controller may still
                        // transition to active state via other signals (e.g., AnswerAsync).
                    }
                    continue;
                }

                if (line.Contains("VOICE CALL: END", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("NO CARRIER", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("BUSY", StringComparison.OrdinalIgnoreCase))
                {
                    // Teardown paths (remote hangup, failure to connect, etc.)
                    OnCallEnded?.Invoke();
                    continue;
                }

                // ---- SMS-related URCs ---------------------------------------------------

                if (line.StartsWith("+CMTI:", StringComparison.OrdinalIgnoreCase))
                {
                    // +CMTI: "<mem>", <index>
                    var idx = ModemUrp.ExtractIndex(line);
                    if (idx != null) OnNewSms?.Invoke(idx.Value);
                    continue;
                }

                if (line.StartsWith("+CMT:", StringComparison.OrdinalIgnoreCase))
                {
                    // “Deliver” indication containing an entire message inline.
                    // We keep it simple: log and let SmsManager pull the store later.
                    _status.Add("[SMS] +CMT received (deliver).");
                    continue;
                }

                // ---- Modem/Network errors ----------------------------------------------

                if (line.StartsWith("+CME ERROR", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("+CMS ERROR", StringComparison.OrdinalIgnoreCase))
                {
                    // Surface numeric error codes to the UI/status area
                    OnError?.Invoke(line);
                    continue;
                }

                // Other URCs can be handled here as needed (registration, signal, etc.)
            }
        }

        // ------------------------------------------------------------------------------
        // Safety & cleanup
        // ------------------------------------------------------------------------------

        /// <summary>Throws if Dispose() has already been called.</summary>
        private void EnsureNotDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ModemControl));
        }

        /// <summary>
        /// Disposes the SerialPort and synchronization primitives. Safe to call multiple times.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_port.IsOpen) _port.Close();
            }
            catch
            {
                // Ignore transport errors during shutdown
            }

            _port.Dispose();
            _respEvent.Dispose();
        }
    }

    // ======================================================================================
    // Small helper utilities for URC parsing (kept internal to discourage external coupling)
    // ======================================================================================

    /// <summary>
    /// Lightweight helpers to parse specific URCs quickly and robustly.
    /// </summary>
    internal static class ModemUrp
    {
        /// <summary>
        /// Extracts the trailing integer index from URCs of the form: +CMTI: "SM", 7
        /// </summary>
        /// <returns>The parsed index or null if not present/parsable.</returns>
        public static int? ExtractIndex(string line)
        {
            try
            {
                var comma = line.LastIndexOf(',');
                if (comma < 0) return null;

                if (int.TryParse(line[(comma + 1)..].Trim(), out int idx))
                    return idx;

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extracts the phone number from a +CLIP URC, which looks like:
        ///   +CLIP: "<number>",145,,,,0
        /// </summary>
        /// <returns>The number string between the first pair of quotes, or null.</returns>
        public static string? ExtractClipNumber(string line)
        {
            var firstQuote = line.IndexOf('"');
            if (firstQuote < 0) return null;

            var secondQuote = line.IndexOf('"', firstQuote + 1);
            if (secondQuote < 0) return null;

            return line.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
        }
    }
}
