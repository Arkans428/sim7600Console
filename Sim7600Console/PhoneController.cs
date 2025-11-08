// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: PhoneController.cs
// Project: SIM7600G-H Console Dialer (C# .NET 8)
// ----------------------------------------------------------------------------
// PURPOSE
//   Central coordinator for all phone logic. This class orchestrates
//   high-level telephony behavior by combining three lower-level modules:
//
//     • ModemControl  → handles AT command communication and URC parsing.
//     • AudioBridge   → manages serial-audio streaming (TX/RX of PCM frames).
//     • SmsManager    → provides basic SMS send/receive/list/delete features.
//     • AppSession    → holds runtime UI/session state and a shared StatusHub.
//
// RESPONSIBILITIES
//   • Initialize and configure the SIM7600G-H modem for voice and SMS mode.
//   • Manage call state transitions: dialing, answering, active, hanging up.
//   • Control audio routing via AT+CPCMREG and trigger AudioBridge operations.
//   • Debounce teardown so repeated hangup or NO CARRIER signals do not
//     double-release audio resources.
//   • Pass SMS-related URCs (+CMTI) to SmsManager and refresh local cache.
//   • Provide safe concurrent access through locks and atomic flags.
//
// BEHAVIOR (THIS REVISION)
//   • Outgoing calls: routes serial audio ~1s after ATD (so ringback is audible).
//   • Incoming calls: sends ATA first, then 1s later routes serial audio to
//     prevent premature channel activation. (AT+CHFA removed.)
//   • When call ends: stops streaming, flushes buffers, sends AT+CPCMREG=0,1
//     to restore the default audio path.
//   • Uses routeOnceFlag to ensure we don’t issue redundant CPCMREG commands.
//
// THREADING NOTES
//   • Outgoing and incoming call routing are run on background Tasks to
//     avoid blocking the console UI.
//   • Access to call-end and in-call flags is protected with _stateLock.
//   • AT commands are awaited sequentially to preserve response integrity.
//
// DEPENDENCIES
//   • AudioBridge.Open(), PrepareForCall(), StartStreaming(), StopStreamingAndFlush()
//   • ModemControl.OpenAsync(), SendExpectOkAsync(), event hooks for URCs
//   • SmsManager.SendAsync(), ListAsync(), ReadAsync(), DeleteAsync()
// ============================================================================

using Sim7600Console.SMS;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Sim7600Console
{
    /// <summary>
    /// The high-level telephony controller that coordinates modem, audio, and SMS.
    /// </summary>
    public sealed class PhoneController : IDisposable
    {
        // ---- Dependencies ------------------------------------------------------------

        private readonly AppSession _session; // Application state (menu data, indicators, status)
        private readonly ModemControl _modem; // AT command interface and URC dispatcher
        private readonly AudioBridge _audio;  // Serial-audio streaming (TX/RX)
        private readonly SmsManager _sms;     // SMS subsystem (wrapper over AT commands)

        // ---- Concurrency & lifetime guards ------------------------------------------

        private readonly object _stateLock = new(); // Protects in-call and teardown flags
        private bool _disposed;

        // Tracks if a call teardown (hang-up, NO CARRIER, etc.) has already been handled.
        private volatile bool _callEndHandled;

        // Tracks if the serial-audio routing (AT+CPCMREG=1,0) has been established.
        private volatile bool _audioRouteActive;

        // Atomic guard used to ensure routing occurs only once per call.
        private int _routeOnceFlag = 0; // 0 = not routed yet, 1 = routed

        // Reserved for future call-state tracking (muted, held, etc.); currently suppressed.
#pragma warning disable CS0414
        private volatile bool _inCall;
#pragma warning restore CS0414

        // ------------------------------------------------------------------------------
        // Construction and setup
        // ------------------------------------------------------------------------------

        public PhoneController(AppSession session, string atPort, string audioPort)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));

            // Construct submodules with shared StatusHub for consistent logging.
            _modem = new ModemControl(_session.Status, atPort);
            // NEW: pass sampleRate: 16000 (you may also bump baud here if you like)
            _audio = new AudioBridge(_session.Status, audioPort, baud: 460800, sampleRate: 16000);

            _sms = new SmsManager(_session.Status, _modem);

            // Wire event handlers from ModemControl to our internal handlers.
            _modem.OnIncomingCall += Modem_OnIncomingCall;
            _modem.OnCallEnded += Modem_OnCallEnded;
            _modem.OnNewSms += Modem_OnNewSms;
            _modem.OnError += (msg) => _session.Status.Add($"[ModemError] {msg}");

            _modem.OnReadyChanged += (ready) =>
            {
                _session.ModemReady = ready;
                _session.Status.Add(ready ? "Modem is READY." : "Modem is NOT ready.");
            };
        }

        /// <summary>
        /// Initializes the modem for operation:
        ///   • Opens AT port and performs handshake.
        ///   • Sends core setup commands for verbose errors, SMS text mode, and caller ID.
        ///   • Opens the serial-audio COM port.
        /// </summary>
        public async Task InitializeAsync(CancellationToken ct = default)
        {
            await _modem.OpenAsync(ct).ConfigureAwait(false);

            await _modem.SendExpectOkAsync("ATE0", 1000, ct);
            await _modem.SendExpectOkAsync("AT+CMEE=2", 1000, ct);
            await _modem.SendExpectOkAsync("AT+CLIP=1", 1000, ct);
            await _modem.SendExpectOkAsync("AT+CMGF=1", 1000, ct);
            await _modem.SendExpectOkAsync("AT+CSCS=\"GSM\"", 1000, ct);
            await _modem.SendExpectOkAsync("AT+CNMI=2,1,0,2,0", 1000, ct);
            await _modem.SendExpectOkAsync("AT+CTZU=1", 1000, ct);
            await _modem.SendExpectOkAsync("AT+CREG?", 1000, ct);

            // --- NEW: Ensure 16 kHz framed PCM is enabled for this boot/session ---
            try
            {
                // Query current framing/sample mode
                var frm = await _modem.SendAsync("AT+CPCMFRM?", 1500, ct).ConfigureAwait(false);
                // Typical responses: "\r\n+CPCMFRM: 0\r\n\r\nOK\r\n" or "...: 1"
                bool is16k = frm.Contains(": 1");

                if (!is16k)
                {
                    await _modem.SendExpectOkAsync("AT+CPCMFRM=1", 2000, ct);
                    _session.Status.Add("[AudioFmt] Set CPCMFRM=1 (16 kHz framed PCM).");
                }
                else
                {
                    _session.Status.Add("[AudioFmt] CPCMFRM already 1 (16 kHz).");
                }
            }
            catch (Exception ex)
            {
                // Not fatal — we’ll still run, but log it so it’s visible in the Status Area.
                _session.Status.Add($"[AudioFmt] CPCMFRM check/set failed: {ex.Message}");
            }

            // Open audio bridge AFTER CPCMFRM so local pipeline matches the modem
            _audio.Open();
            _session.ModemReady = true;
        }

        // ------------------------------------------------------------------------------
        // Outgoing calls (Mobile Originated)
        // ------------------------------------------------------------------------------

        /// <summary>
        /// Dials a number and establishes serial audio routing ~1 second later so
        /// the local user can hear ringback tones. Runs CPCMREG setup asynchronously.
        /// </summary>
        public async Task DialAsync(string number, CancellationToken ct = default)
        {
            EnsureNotDisposed();
            if (!_session.ModemReady) throw new InvalidOperationException("Modem not ready.");

            // Reset call teardown debounce for a fresh attempt
            lock (_stateLock)
            {
                _callEndHandled = false;
                _inCall = false;
            }
            Interlocked.Exchange(ref _routeOnceFlag, 0);
            _audioRouteActive = false;

            await _modem.SendExpectOkAsync($"ATD{number};", 8000, ct);
            _session.Status.Add($"Dial initiated -> {number}");

            // Start delayed audio routing task (non-blocking to UI).
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1000, ct); // Give the modem time to start ringback.

                    // Route voice path from modem to serial-audio interface.
                    await _modem.SendExpectOkAsync("AT+CPCMREG=1,0", 1500, ct);
                    _audioRouteActive = true;
                    _session.Status.Add("[AudioRoute] AT+CPCMREG=1,0 (early route for ring tone)");

                    _audio.PrepareForCall();

                    if (!_audio.IsStreaming && _audio.StartStreaming())
                        _session.Status.Add("[Audio] Streaming started (ringback).");
                }
                catch (Exception ex)
                {
                    _session.Status.Add($"[AudioRoute] Early CPCMREG failed: {ex.Message}");
                }
            });
        }

        // ------------------------------------------------------------------------------
        // Incoming calls (Mobile Terminated)
        // ------------------------------------------------------------------------------

        /// <summary>
        /// Answers an incoming call immediately (ATA), then—after a 1s delay—routes
        /// audio to the serial-audio COM port and starts streaming. This delay prevents
        /// half-open audio sessions before the call is fully connected.
        /// </summary>
        public async Task AnswerAsync(CancellationToken ct = default)
        {
            EnsureNotDisposed();

            // Reset debounce for the new call
            lock (_stateLock)
            {
                _callEndHandled = false;
                _inCall = false;
                _session.IsRinging = false;   // NEW
                _session.InCall = true;       // NEW
            }
            Interlocked.Exchange(ref _routeOnceFlag, 0);

            await _modem.SendExpectOkAsync("ATA", 6000, ct);
            _session.Status.Add("Answer sent.");

            // Delay ~1s AFTER ATA before enabling serial-audio routing.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1000, ct); // Allow modem to stabilize.

                    await _modem.SendExpectOkAsync("AT+CPCMREG=1,0", 1500, ct);
                    _audioRouteActive = true;
                    _session.Status.Add("[AudioRoute] AT+CPCMREG=1,0 (serial-audio active; post-ATA)");

                    _audio.PrepareForCall();

                    if (!_audio.IsStreaming && _audio.StartStreaming())
                        _session.Status.Add("[Audio] Streaming started (duplex; incoming).");
                }
                catch (Exception ex)
                {
                    _session.Status.Add($"[AudioRoute] Incoming CPCMREG (post-ATA) failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Called by ModemControl when the modem emits "VOICE CALL: BEGIN" or similar URC.
        /// Normally AnswerAsync or DialAsync already handles audio routing, but this method
        /// acts as a fallback to ensure audio activates even if earlier tasks were skipped.
        /// </summary>
        internal void NotifyVoiceCallBegin()
        {
            _session.Status.Add("Voice call active.");

            // Reset route guard for safety.
            Interlocked.CompareExchange(ref _routeOnceFlag, 0, 1);

            lock (_stateLock)
            {
                _inCall = true;
                _callEndHandled = false;
                _session.IsRinging = false;  // NEW
                _session.InCall = true;      // NEW
            }

            // Safety net: if audio hasn't been routed/started after 1.2s, route it once.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1200);

                    if (!_audioRouteActive)
                    {
                        await _modem.SendExpectOkAsync("AT+CPCMREG=1,0", 1500);
                        _audioRouteActive = true;
                        _session.Status.Add("[AudioRoute] AT+CPCMREG=1,0 (fallback during ACTIVE)");
                        _audio.PrepareForCall();
                    }

                    if (!_audio.IsStreaming && _audio.StartStreaming())
                        _session.Status.Add("[Audio] Streaming started (duplex; fallback).");
                }
                catch (Exception ex)
                {
                    _session.Status.Add($"[AudioRoute] ACTIVE fallback failed: {ex.Message}");
                }
            });
        }

        // ------------------------------------------------------------------------------
        // Hang-up / teardown
        // ------------------------------------------------------------------------------

        /// <summary>
        /// Ends the current call using AT+CHUP (or ATH fallback) and performs a clean
        /// teardown: stops streaming, flushes buffers, and restores CPCMREG=0,1.
        /// </summary>
        public async Task HangUpAsync(CancellationToken ct = default)
        {
            EnsureNotDisposed();

            // Send hangup (best-effort)
            try { await _modem.SendExpectOkAsync("AT+CHUP", 5000, ct); }
            catch { await _modem.SendExpectOkAsync("ATH", 5000, ct); }
            _session.Status.Add("Hang-up sent.");

            bool shouldDoRouteRestore;
            lock (_stateLock)
            {
                // Mark once, but NEVER return early; we must stop audio regardless.
                shouldDoRouteRestore = !_callEndHandled;   // only first teardown triggers CPCMREG restore
                _callEndHandled = true;
                _inCall = false;
                _session.IsRinging = false;          // NEW
                _session.InCall = false;             // NEW
                _session.IncomingCallerId = null;    // NEW: clear “Incoming” field
            }

            // Always stop local audio pipeline so TX/RX threads die.
            try { _audio.StopStreamingAndFlush(); } catch { }

            // Only the first teardown attempts to restore CPCMREG (idempotent but we avoid spam)
            if (shouldDoRouteRestore || _audioRouteActive)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _modem.SendExpectOkAsync("AT+CPCMREG=0,1", 2000);
                        _audioRouteActive = false;
                        _session.Status.Add("[AudioRoute] AT+CPCMREG=0,1 (serial-audio released)");
                    }
                    catch (Exception ex)
                    {
                        _session.Status.Add($"[AudioRoute] Restore failed: {ex.Message}");
                    }
                });
            }
        }


        /// <summary>
        /// Called when ModemControl detects a call-ending URC (NO CARRIER, END, BUSY).
        /// Performs the same cleanup as HangUpAsync() but without sending AT commands again.
        /// </summary>
        private void Modem_OnCallEnded()
        {
            bool shouldDoRouteRestore;
            lock (_stateLock)
            {
                if (_callEndHandled)
                {
                    // Even if we handled this, ensure local audio is down.
                    try { _audio.StopStreamingAndFlush(); } catch { }
                    return;
                }
                _callEndHandled = true;
                _inCall = false;
                shouldDoRouteRestore = true;
                _session.IsRinging = false;          // NEW
                _session.InCall = false;             // NEW
                _session.IncomingCallerId = null;    // NEW
            }

            _session.Status.Add("Call ended.");

            try { _audio.StopStreamingAndFlush(); } catch { }

            if (shouldDoRouteRestore || _audioRouteActive)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _modem.SendExpectOkAsync("AT+CPCMREG=0,1", 1500);
                        _audioRouteActive = false;
                        _session.Status.Add("[AudioRoute] AT+CPCMREG=0,1 (serial-audio released)");
                    }
                    catch (Exception ex)
                    {
                        _session.Status.Add($"[AudioRoute] Restore failed: {ex.Message}");
                    }
                });
            }
        }


        // ------------------------------------------------------------------------------
        // SMS integration
        // ------------------------------------------------------------------------------

        /// <summary>
        /// Triggered when ModemControl reports a +CMTI URC. Marks a new SMS indicator
        /// and asynchronously refreshes the message list.
        /// </summary>
        private void Modem_OnNewSms(int index)
        {
            _session.NewSmsIndicator = true;
            _session.Status.Add($"New SMS indication at index: {index}");

            _ = Task.Run(async () =>
            {
                try { await RefreshSmsListAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _session.Status.Add($"[SMS] Refresh after CMTI failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Re-queries the modem for all stored SMS messages and updates session cache.
        /// </summary>
        public async Task RefreshSmsListAsync(CancellationToken ct = default)
        {
            var items = await _sms.ListAsync(ct).ConfigureAwait(false);
            lock (_stateLock)
            {
                _session.SmsList.Clear();
                _session.SmsList.AddRange(items);
                _session.Status.Add($"SMS list updated. Count: {items.Count}");
            }
        }

        /// <summary>Reads a specific SMS message by index.</summary>
        public Task<SmsMessage> ReadSmsAsync(int index, SmsStorage storage, CancellationToken ct = default)
            => _sms.ReadAsync(index, storage, ct);

        // Back-compat (assume SM if unknown)
        public Task<SmsMessage> ReadSmsAsync(int index, CancellationToken ct = default)
            => _sms.ReadAsync(index, SmsStorage.SM, ct);

        /// <summary>Deletes a stored SMS by index and refreshes the cached list.</summary>
        public async Task DeleteSmsAsync(int index, SmsStorage storage, CancellationToken ct = default)
        {
            await _sms.DeleteAsync(index, storage, ct).ConfigureAwait(false);
            _session.Status.Add($"SMS[{index}] deleted.");
        }

        public async Task DeleteSmsAsync(int index, CancellationToken ct = default)
            => await DeleteSmsAsync(index, SmsStorage.SM, ct).ConfigureAwait(false);

        /// <summary>Sends an SMS to the given number with the specified message text.</summary>
        public async Task SendSmsAsync(string to, string text, CancellationToken ct = default)
        {
            await _sms.SendAsync(to, text, ct).ConfigureAwait(false);
            _session.Status.Add($"SMS sent to {to} ({text.Length} chars).");
        }

        // ------------------------------------------------------------------------------
        // Misc / lifecycle
        // ------------------------------------------------------------------------------

        /// <summary>
        /// Logs and stores the caller ID of an incoming call. “Unknown” if CLIP not provided.
        /// </summary>
        private void Modem_OnIncomingCall(string? caller)
        {
            lock (_stateLock)
            {
                _session.IncomingCallerId = string.IsNullOrWhiteSpace(caller) ? "Unknown" : caller;
                _session.IsRinging = true;             // NEW: start blinking / show Answer/Reject
                _session.InCall = false;               // ensure not marked in-call yet
                _session.Status.Add($"Incoming call from: {_session.IncomingCallerId}");
            }
        }

        /// <summary>
        /// Atomically marks the audio route as used once. Returns true if this is the first call.
        /// </summary>
        private bool TryMarkAudioRoutedOnce()
            => Interlocked.CompareExchange(ref _routeOnceFlag, 1, 0) == 0;

        /// <summary>Throws if this controller has already been disposed.</summary>
        private void EnsureNotDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PhoneController));
        }

        /// <summary>
        /// Cleans up all owned resources (audio bridge and modem). Safe to call multiple times.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _audio?.Dispose(); } catch { }
            try { _modem?.Dispose(); } catch { }
        }
    }
}
