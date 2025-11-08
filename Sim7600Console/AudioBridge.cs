// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: AudioBridge.cs
// Project: SIM7600G-H Console Dialer (C# .NET 8)
// ----------------------------------------------------------------------------
// PURPOSE
//   Bridges PC audio (microphone + speaker) with the SIM7600 “serial audio”
//   UART using a full-duplex pipeline. This class owns:
//     • The serial port dedicated to audio (separate from the AT port)
//     • A capture stream from the default system microphone (NAudio WaveInEvent)
//     • A playback stream to the default system speakers (NAudio WaveOutEvent)
//     • A buffered TX queue and background TX writer thread
//     • A background RX loop that feeds a buffered speaker provider
//
// DESIGN NOTES
//   • Sample format is narrowband telephony: 8 kHz, 16-bit mono (linear PCM).
//     This matches the SIM7600 serial-audio expectations. If you change it,
//     you must ensure the modem is configured accordingly and that the remote
//     endpoint is compatible.
//   • Hardware flow control is enabled (RTS/CTS) to avoid overruns on some
//     bridges. The TX loop is CTS-aware and drops frames (instead of blocking)
//     if CTS is low, which prevents WriteTimeout exceptions while the modem is
//     not ready to accept audio (e.g., around call setup/teardown).
//   • The class is intentionally conservative about thread interaction:
//       - All state transitions (start/stop) are protected by _lock.
//       - TX uses a BlockingCollection to decouple microphone timing from
//         serial-write timing.
//       - RX runs on a ThreadPool work item reading the serial port.
//
// LIFECYCLE
//   1) Call Open() once to open and configure the audio SerialPort.
//   2) Call StartStreaming() when the voice path is routed to serial audio
//      (after AT+CPCMREG=1,0). This spawns RX and TX workers.
//   3) Call StopStreamingAndFlush() when the call ends (before CPCMREG=0,1).
//   4) Dispose() will ensure streaming is stopped and the serial port closed.
//
// ERROR HANDLING & LOGGING
//   • All noteworthy events and errors are pushed into StatusHub.
//   • Transient serial timeouts are expected; we handle them quietly.
//   • Mic buffer overruns (queue full) are logged at a low rate.
//
// THREAD SAFETY
//   • Public methods are safe to call from different threads, but the intended
//     usage is from a higher-level controller that sequences calls correctly.
//   • StartStreaming() returns false if already streaming, preventing double
//     workers.
//
// PERFORMANCE TIPS
//   • The TX queue capacity is fixed (64 frames). If you see frequent
//     “[Audio TX] Dropped frame (queue full).” messages, your system cannot
//     write to the serial port fast enough; try reducing Mic BufferMilliseconds,
//     or disable other heavy work on the same thread/core.
//   • WaveOutEvent and WaveInEvent use separate threads under the hood; this
//     reduces jitter compared to callback-based APIs in console apps.
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Threading;
using NAudio.Wave;
using Sim7600Console.UI;

namespace Sim7600Console
{
    /// <summary>
    /// Full-duplex audio bridge between the Windows audio stack and a SIM7600
    /// serial-audio UART (separate from the AT port).
    /// </summary>
    public sealed class AudioBridge : IDisposable
    {
        // ---- Dependencies & configuration -------------------------------------------------

        private readonly StatusHub _status;     // Logging sink shared by the app
        private readonly string _audioPortName; // e.g., "COM6" — the SIM7600 serial-audio port
        private readonly int _baud;             // Usually 115200 for serial-audio
        private readonly int _sampleRate;       // Using 16k Sample rate

        // ---- Serial & audio objects (created/opened at runtime) --------------------------

        private SerialPort? _audioPort;         // The UART used exclusively for audio bytes
        private WaveInEvent? _mic;              // Captures PCM frames from the default microphone
        private WaveOutEvent? _spk;             // Plays PCM frames to the default speakers
        private BufferedWaveProvider? _speakerBuffer; // Smooths RX jitter before playback

        // Telephony PCM: 8 kHz, 16-bit, mono
        private WaveFormat _format;

        private int? _preferredInIndex;   // null = default device
        private int? _preferredOutIndex;  // null = default device

        // ---- Concurrency & state ---------------------------------------------------------

        private volatile bool _streaming;       // True while RX/TX workers are active
        private volatile bool _disposed;        // True after Dispose() is called
        private readonly object _lock = new();  // Guards state transitions (start/stop)

        private BlockingCollection<byte[]>? _txQueue; // Mic frames awaiting serial write
        private Thread? _txThread;                      // Dedicated TX writer thread

        private volatile bool _txStopRequested;

        /// <summary>
        /// True if StartStreaming() has been called successfully and the RX/TX
        /// workers are running.
        /// </summary>
        public bool IsStreaming => _streaming;

        /// <summary>
        /// Create an audio bridge bound to a specific SIM7600 serial-audio COM port.
        /// </summary>
        /// <param name="status">App-wide status logger.</param>
        /// <param name="audioPortName">The COM port name for serial audio (e.g., "COM6").</param>
        /// <param name="baud">Baud rate (defaults to 115200).</param>
        /// <param name="sampleRate">Set audio sampling to 16K</param>
        public AudioBridge(StatusHub status, string audioPortName, int baud = 460800, int sampleRate = 16000)
        {
            _status = status ?? throw new ArgumentNullException(nameof(status));
            _audioPortName = audioPortName ?? throw new ArgumentNullException(nameof(audioPortName));
            _baud = baud;
            _sampleRate = sampleRate;
            _format = new WaveFormat(_sampleRate, 16, 1); // 16-bit mono at desired rate
        }

        /// <summary>
        /// Opens and configures the serial-audio port (RTS/CTS, 8N1). Safe to call once.
        /// </summary>
        public void Open()
        {
            if (_audioPort != null && _audioPort.IsOpen) return;

            _audioPort = new SerialPort(_audioPortName, _baud, Parity.None, 8, StopBits.One)
            {
                DtrEnable = true,
                RtsEnable = true,
                Handshake = Handshake.RequestToSend,
                ReadTimeout = 10,
                WriteTimeout = 5000
            };

            _audioPort.Open();
            _status.Add($"[Audio] Opened {_audioPort.PortName} at {_baud} bps (RTS/CTS).");
            _status.Add($"[Audio] Selected devices -> Input={(_mic == null ? "Default" : "Custom")}, Output={(_spk == null ? "Default" : "Custom")}");
            _status.Add($"[Audio] WaveFormat: {_format.SampleRate} Hz, {_format.BitsPerSample}-bit, {_format.Channels} ch");

            // Advisory: 16k * 16-bit mono ≈ 256 kbps raw; if you truly stream raw PCM over UART,
            // consider increasing baud (e.g., 460800/921600) to reduce CTS backpressure.
            if (_sampleRate >= 16000 && _baud < 256000)
                _status.Add("[Audio] Note: 16 kHz over 115200 bps may throttle; consider 460800/921600.");
        }

        /// <summary>
        /// Performs a quick “prepare” step after the voice path is routed to serial audio
        /// (i.e., after AT+CPCMREG=1,0). This clears buffers, nudges RTS (some USB-UART
        /// chips need it), and waits briefly for CTS to assert.
        /// </summary>
        public void PrepareForCall()
        {
            var sp = _audioPort;
            if (sp == null || !sp.IsOpen) return;

            // Clear any stale data left from previous usage (e.g., call teardown)
            try { sp.DiscardInBuffer(); } catch { /* not fatal */ }
            try { sp.DiscardOutBuffer(); } catch { /* not fatal */ }

            // Some bridges behave better after a short RTS toggle (“kick” the adapter)
            NudgeRts();

            // Wait up to ~1 s for CTS to assert. Not all adapters expose CTS —
            // if an exception is thrown or CTS never asserts, we proceed anyway.
            var start = Environment.TickCount;
            bool lastLogged = false;
            while (Environment.TickCount - start < 1000)
            {
                try
                {
                    if (sp.CtsHolding) return; // CTS high — ready for TX
                }
                catch
                {
                    // Some drivers don’t support modem-status lines; abort the wait
                    break;
                }

                if (!lastLogged)
                {
                    _status.Add("[Audio] Waiting for CTS to assert…");
                    lastLogged = true;
                }
                Thread.Sleep(25); // Short poll — keeps CPU usage low
            }

            // If we get here, CTS didn’t assert within the window. This is OK —
            // the TX loop will detect CTS low per-frame and drop those frames
            // (avoiding WriteTimeout) while still allowing RX to flow.
        }

        /// <summary>
        /// Briefly de-asserts RTS then re-asserts it. Some USB-UART bridges need this
        /// after changing states to flush internal FIFOs or to wake up properly.
        /// </summary>
        public void NudgeRts()
        {
            var sp = _audioPort;
            if (sp == null || !sp.IsOpen) return;
            try
            {
                sp.RtsEnable = false;
                Thread.Sleep(10);  // 10 ms is ample for most bridges
                sp.RtsEnable = true;
                
            }
            catch { /* Non-fatal: best effort only */ }
        }

        /// <summary>
        /// Starts the duplex audio pipeline:
        ///   • RX loop reads from the serial port and feeds a buffered speaker provider.
        ///   • TX path captures mic buffers, enqueues them, and a background writer
        ///     drains the queue into the serial port (CTS-aware).
        /// </summary>
        /// <returns>
        /// True if streaming started now; false if it was already running.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown if the serial-audio port is not open.
        /// </exception>
        public bool StartStreaming()
        {
            lock (_lock)
            {
                // Idempotent: if already streaming, do nothing
                if (_streaming) return false;

                // Require an open serial port (Open() is a separate step)
                if (_audioPort == null || !_audioPort.IsOpen)
                    throw new InvalidOperationException("Audio serial port not open.");

                _txStopRequested = false;
                _streaming = true;

                // Bounded queue prevents unbounded memory growth if serial writes stall
                _txQueue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(), 64);

                // Speaker side uses a buffered provider to smooth RX jitter/bursts
                _speakerBuffer = new BufferedWaveProvider(_format)
                {
                    DiscardOnBufferOverflow = true,            // Drop oldest if flooded
                    BufferDuration = TimeSpan.FromSeconds(2)   // Give play side some slack
                };
                _spk = new WaveOutEvent();
                if (_preferredOutIndex.HasValue)
                {
                    // NAudio uses -1 = default, otherwise the device index.
                    _spk.DeviceNumber = _preferredOutIndex.Value;
                }
                _spk.Init(_speakerBuffer);
                _spk.Play(); // Start playback thread

                // Microphone capture settings – 20ms frames (~160 samples @ 8kHz)
                _mic = new WaveInEvent
                {
                    BufferMilliseconds = 20,
                    NumberOfBuffers = 8,
                    WaveFormat = _format
                };
                if (_preferredInIndex.HasValue)
                {
                    _mic.DeviceNumber = _preferredInIndex.Value;
                }
                _mic.DataAvailable += MicOnData; // Push mic frames into TX queue
                _mic.StartRecording();

                // RX loop runs on the ThreadPool; TX runs on a dedicated thread
                var queueForThread = _txQueue;
                ThreadPool.QueueUserWorkItem(_ => RxLoop());
                _txThread = new Thread(() => TxWriterLoop(queueForThread!))
                {
                    IsBackground = true,
                    Name = "AudioSerial-TX"
                };
                _txThread.Start();

                return true;
            }
        }

        /// <summary>
        /// Stops the duplex pipeline and flushes serial buffers to ensure no stale
        /// audio frames leak into the next call. Safe to call if not streaming.
        /// </summary>
        public void StopStreamingAndFlush()
        {
            BlockingCollection<byte[]>? queueToClose = null;

            lock (_lock)
            {
                // If already stopped, just clear any leftover serial bytes and return
                if (!_streaming)
                {
                    try { _audioPort?.DiscardInBuffer(); } catch { }
                    try { _audioPort?.DiscardOutBuffer(); } catch { }
                    return;
                }

                // Mark not streaming so workers exit
                _streaming = false;
                _txStopRequested = true;

                // Tear down microphone capture first (stop callbacks)
                try { _mic?.StopRecording(); } catch { /* ignore */ }
                if (_mic != null)
                {
                    _mic.DataAvailable -= MicOnData;
                    _mic.Dispose();
                    _mic = null;
                }

                // Tear down playback
                try { _spk?.Stop(); } catch { /* ignore */ }
                _spk?.Dispose();
                _spk = null;

                // Close the TX queue so the writer thread will exit promptly
                queueToClose = _txQueue;
                _txQueue = null;

                // Release the speaker buffer
                _speakerBuffer = null;
            }

            // Let the TX writer drain/exit; then empty any remainder
            if (queueToClose != null)
            {
                try { queueToClose.CompleteAdding(); } catch { }
                try { _txThread?.Join(500); } catch { /* ignore */ }
                while (queueToClose.TryTake(out _)) { /* discard remaining frames */ }
            }

            // Clear serial driver FIFOs so a new call starts clean
            try { _audioPort?.DiscardInBuffer(); } catch { }
            try { _audioPort?.DiscardOutBuffer(); } catch { }

            _status.Add("[Audio] Streaming stopped.");
        }

        /// <summary>
        /// Convenience alias for StopStreamingAndFlush().
        /// </summary>
        public void StopStreaming() => StopStreamingAndFlush();

        /// <summary>
        /// Apply preferred NAudio device indices. Call any time before StartStreaming().
        /// Pass null to use system-default devices.
        /// </summary>
        public void ConfigureAudioDevices(int? inputDeviceIndex, int? outputDeviceIndex)
        {
            _preferredInIndex = inputDeviceIndex;
            _preferredOutIndex = outputDeviceIndex;

            // Log what will be used on next start
            string inTxt = inputDeviceIndex.HasValue ? $"#{inputDeviceIndex.Value}" : "Default";
            string outTxt = outputDeviceIndex.HasValue ? $"#{outputDeviceIndex.Value}" : "Default";
            _status.Add($"[Audio] Selected devices -> Input={inTxt}, Output={outTxt}");
        }

        // ---------------------------- Internal workers ----------------------------

        /// <summary>
        /// RX worker: reads bytes from the serial port and hands them to the speaker buffer.
        /// Uses a small read timeout to poll without blocking shutdown.
        /// </summary>
        private void RxLoop()
        {
            var buf = new byte[1024]; // Read chunk size; not critical
            while (_streaming && _audioPort != null && _audioPort.IsOpen)
            {
                try
                {
                    int n = _audioPort.Read(buf, 0, buf.Length);
                    if (n > 0 && _speakerBuffer != null)
                        _speakerBuffer.AddSamples(buf, 0, n);
                }
                catch (TimeoutException)
                {
                    // Normal — we use short timeouts so the loop can regularly
                    // check _streaming and exit promptly when stopping.
                }
                catch (Exception ex)
                {
                    // Any unexpected serial error gets logged and we back off slightly
                    _status.Add($"[Audio RX] {ex.Message}");
                    Thread.Sleep(5);
                }
            }
        }

        /// <summary>
        /// Mic callback: copies the recorded buffer and enqueues it to the TX queue.
        /// We copy because NAudio reuses the input buffer after the callback returns.
        /// </summary>
        private void MicOnData(object? sender, WaveInEventArgs e)
        {
            if (!_streaming) return;
            var q = _txQueue;
            if (q == null || q.IsAddingCompleted) return;

            // Copy the mic bytes before enqueuing
            var copy = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);

            try
            {
                // Try to add; on failure (queue full) drop the oldest and retry once
                if (!q.TryAdd(copy))
                {
                    if (q.TryTake(out _))
                        q.TryAdd(copy);
                    else
                        _status.Add("[Audio TX] Dropped frame (queue full).");
                }
            }
            catch (InvalidOperationException)
            {
                // Queue may have been completed during stop — safe to ignore
            }
        }

        /// <summary>
        /// TX writer: drains mic frames from the queue and writes to the serial port.
        /// If CTS is low (modem not ready), we drop the frame to avoid blocking and
        /// log occasionally to keep visibility without spamming the status area.
        /// </summary>
        private void TxWriterLoop(BlockingCollection<byte[]> queue)
        {
            int ctsLogCooldown = 0; // Rate-limit CTS-low messages (~1/s)
            try
            {
                while (!_txStopRequested && _streaming && _audioPort != null && _audioPort.IsOpen && !queue.IsCompleted)
                {
                    // Pull a frame; time out periodically to re-check _streaming
                    if (!queue.TryTake(out var frame, millisecondsTimeout: 50))
                        continue;

                    try
                    {
                        // Determine if CTS is asserted; some drivers don’t expose it
                        bool ctsLow = false;
                        try { ctsLow = !_audioPort.CtsHolding; }
                        catch { /* if unsupported, treat as OK and attempt to write */ }

                        // If CTS is LOW, drop this frame to prevent WriteTimeout and backpressure
                        if (ctsLow)
                        {
                            if (ctsLogCooldown-- <= 0)
                            {
                                _status.Add("[Audio TX] Paused (CTS low); dropping frame.");
                                ctsLogCooldown = 20; // ~1 second at 50 ms loop cadence
                            }
                            continue;
                        }

                        // If the driver’s output buffer is already large, yield briefly
                        if (_audioPort.BytesToWrite > 4096)
                            Thread.Sleep(5);

                        // Write the frame to the modem’s serial-audio interface
                        _audioPort.Write(frame, 0, frame.Length);
                    }
                    catch (TimeoutException)
                    {
                        // Defensive timeout guard; this should be rare with the CTS check above
                        _status.Add("[Audio TX] The write timed out.");
                    }
                    catch (Exception ex)
                    {
                        // Any other serial error gets logged; loop continues so we don’t kill the call
                        _status.Add($"[Audio TX] {ex.Message}");
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Normal during teardown races (port disposed while thread unwinds)
            }
        }

        // ------------------------------ IDisposable ------------------------------

        /// <summary>
        /// Stops streaming (if active), closes and disposes the serial port and NAudio devices.
        /// Safe to call multiple times.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { StopStreamingAndFlush(); } catch { /* ignore */ }

            try
            {
                if (_audioPort != null && _audioPort.IsOpen)
                    _audioPort.Close();
            }
            catch { /* ignore */ }

            try { _audioPort?.Dispose(); } catch { /* ignore */ }
        }
    }
}
