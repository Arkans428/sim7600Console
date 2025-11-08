// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: AudioDevicesPage.cs
// Description:
//   Allows the user to select active audio input (microphone) and output
//   (speaker/headphones) devices available to the system via NAudio.
//   The selected devices are persisted to AppSession for use by the audio
//   subsystem (e.g., AudioBridge or in-call handling logic).
//
//   Navigation Keys:
//     • 1 : Focus Input list
//     • 2 : Focus Output list
//     • ↑/↓ : Move cursor within the focused list
//     • ENTER : Confirm current selection
//     • ESC : Return to previous page
//
//   Implementation Notes:
//     • Input devices are enumerated using WaveInEvent (legacy WaveIn replaced).
//     • Output devices use MMDeviceEnumerator (CoreAudio API), since
//       WaveOutEvent lacks static enumeration support in newer NAudio versions.
//     • Optional preview fields are stubbed for future audio preview playback.
//     • All device access wrapped in safe try/catch to avoid UI crashes when
//       device enumeration fails (e.g., exclusive mode or permissions).
// ============================================================================

using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace Sim7600Console.UI.Pages
{
    /// <summary>
    /// UI page that lists all available audio input and output devices, allowing
    /// the operator to choose which devices will be used for voice calls.
    /// The selections are saved to <see cref="AppSession"/> for reuse by
    /// the runtime audio path.
    /// </summary>
    public sealed class AudioDevicesPage : PageBase
    {
        /// <summary>
        /// Represents which list is currently in focus for cursor navigation.
        /// </summary>
        private enum Focus { Input, Output }

        // --- UI state management fields ---
        private Focus _focus = Focus.Input;  // Which list currently has navigation focus
        private int _cursorInput = 0;        // Current cursor index in input list
        private int _cursorOutput = 0;       // Current cursor index in output list

        // --- Optional preview/audio test fields ---
        // These are placeholders for future use; they allow implementation of
        // small audio previews to confirm that selected devices are functional.
        private WaveInEvent? _previewIn;
        private WaveOutEvent? _previewOut;
        private BufferedWaveProvider? _previewBuffer;

        /// <summary>
        /// Constructor — initializes device lists and sets up UI defaults.
        /// </summary>
        public AudioDevicesPage(ConsoleUi ui, AppSession session) : base(ui, session)
        {
            Session.Status.Add("Audio Devices page opened.");
            ClampCursorsToDeviceCounts(); // Prevent invalid cursor index if no devices exist
        }

        /// <summary>
        /// Draws the entire page contents: input and output device lists with
        /// highlighting for cursor and current focus state.
        /// </summary>
        public override void Draw(int width, int height)
        {
            DrawTitleRow(width, Header("Audio Devices"));

            int row = 2;
            SafeWrite(0, row++, "ESC = Back | 1 = Focus Input | 2 = Focus Output | ENTER = Select");
            SafeWrite(0, row++, "");

            // Refresh device count dynamically (supports hot-plugging)
            int inCount = SafeGetInputCount();
            int outCount = SafeGetOutputCount();

            // --- Input Devices ---
            SafeWrite(0, row++, $"Input Devices (microphones) [{inCount}] {(_focus == Focus.Input ? "◀ focus" : "")}");
            int inputShown = DrawDeviceList(
                startRow: row,
                width: width,
                height: height,
                listCount: inCount,
                currentCursor: _cursorInput,
                isInput: true,
                selectedId: Session.AudioInputDeviceId);
            row += inputShown;

            SafeWrite(0, row++, "");

            // --- Output Devices ---
            SafeWrite(0, row++, $"Output Devices (speakers) [{outCount}] {(_focus == Focus.Output ? "◀ focus" : "")}");
            int outputShown = DrawDeviceList(
                startRow: row,
                width: width,
                height: height,
                listCount: outCount,
                currentCursor: _cursorOutput,
                isInput: false,
                selectedId: Session.AudioOutputDeviceId);
            row += outputShown;

            // --- Footer: show current selections for confirmation ---
            SafeWrite(0, row++, "");
            var inSel = Session.AudioInputDeviceId is int iid && iid >= 0 ? iid.ToString() : "—";
            var outSel = Session.AudioOutputDeviceId is int oid && oid >= 0 ? oid.ToString() : "—";
            SafeWrite(0, row++, $"Selected: InputID={inSel}, OutputID={outSel}");
        }

        /// <summary>
        /// Handles keyboard navigation and device selection.
        /// </summary>
        public override bool HandleKey(ConsoleKeyInfo key, out PageNav nav)
        {
            nav = PageNav.None;

            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    nav = PageNav.Pop; // Return to previous page
                    return true;

                // Focus switching
                case ConsoleKey.D1:
                    _focus = Focus.Input;
                    Ui.Redraw();
                    return true;

                case ConsoleKey.D2:
                    _focus = Focus.Output;
                    Ui.Redraw();
                    return true;

                // Move cursor up/down depending on focused list
                case ConsoleKey.UpArrow:
                    if (_focus == Focus.Input)
                        _cursorInput = Math.Max(0, _cursorInput - 1);
                    else
                        _cursorOutput = Math.Max(0, _cursorOutput - 1);
                    Ui.Redraw();
                    return true;

                case ConsoleKey.DownArrow:
                    if (_focus == Focus.Input)
                        _cursorInput = Math.Min(Math.Max(0, SafeGetInputCount() - 1), _cursorInput + 1);
                    else
                        _cursorOutput = Math.Min(Math.Max(0, SafeGetOutputCount() - 1), _cursorOutput + 1);
                    Ui.Redraw();
                    return true;

                // Commit selection
                case ConsoleKey.Enter:
                    return OnSelect(out nav);

                default:
                    return false;
            }
        }

        /// <summary>
        /// Executes when ENTER is pressed; saves the currently highlighted
        /// device index to <see cref="AppSession"/> depending on active focus.
        /// </summary>
        private bool OnSelect(out PageNav nav)
        {
            nav = PageNav.None;

            if (_focus == Focus.Input)
            {
                int inCount = SafeGetInputCount();
                if (inCount <= 0)
                {
                    Session.Status.Add("Audio: No input devices found.");
                    return true;
                }

                int idx = Math.Clamp(_cursorInput, 0, inCount - 1);
                Session.AudioInputDeviceId = idx;
                var name = SafeGetInputName(idx);
                Session.Status.Add($"Audio: Selected INPUT [{idx}] {name}");
                Ui.Redraw();
                return true;
            }
            else
            {
                int outCount = SafeGetOutputCount();
                if (outCount <= 0)
                {
                    Session.Status.Add("Audio: No output devices found.");
                    return true;
                }

                int idx = Math.Clamp(_cursorOutput, 0, outCount - 1);
                Session.AudioOutputDeviceId = idx;
                var name = SafeGetOutputName(idx);
                Session.Status.Add($"Audio: Selected OUTPUT [{idx}] {name}");
                Ui.Redraw();
                return true;
            }
        }

        /// <summary>
        /// Draws a paginated, scroll-safe list of devices (input or output).
        /// Highlights the current cursor line with ▶ and the active selection
        /// with [Selected]. Handles list window clipping to avoid overflow.
        /// </summary>
        private int DrawDeviceList(int startRow, int width, int height, int listCount, int currentCursor, bool isInput, int? selectedId)
        {
            if (listCount <= 0)
            {
                SafeWrite(0, startRow, "  (No devices found)");
                return 1;
            }

            // Compute scroll window bounds (center cursor in view)
            int maxRows = Math.Max(4, height - startRow - 6);
            int window = Math.Max(3, maxRows);
            int first = Math.Clamp(currentCursor - window / 2, 0, Math.Max(0, listCount - window));
            int lastEx = Math.Min(listCount, first + window);

            int y = startRow;
            for (int i = first; i < lastEx; i++)
            {
                bool isCursor = i == currentCursor;
                bool isSelected = selectedId.HasValue && selectedId.Value == i;

                string marker = isCursor ? "▶" : " ";
                string sel = isSelected ? " [Selected]" : "";

                string name = isInput ? SafeGetInputName(i) : SafeGetOutputName(i);
                string line = $"{marker} [{i}] {name}{sel}";
                if (line.Length > width) line = line[..width];
                SafeWrite(0, y++, line);
            }

            // Display ellipses if list is truncated
            if (first > 0) SafeWrite(0, y++, "  …");
            if (lastEx < listCount) SafeWrite(0, y++, "  …");

            return y - startRow;
        }

        /// <summary>
        /// Safely retrieves the total count of available input (recording) devices.
        /// Uses WaveInEvent enumeration; exceptions are caught and suppressed to
        /// prevent UI interruption (e.g., due to exclusive mode or driver errors).
        /// </summary>
        private int SafeGetInputCount()
        {
            try { return WaveInEvent.DeviceCount; } catch { return 0; }
        }

        /// <summary>
        /// Safely retrieves the total count of available output (render) devices.
        /// Uses CoreAudio's MMDeviceEnumerator since WaveOutEvent no longer
        /// supports static enumeration in modern NAudio builds.
        /// </summary>
        private int SafeGetOutputCount()
        {
            try
            {
                using var mm = new MMDeviceEnumerator();
                var devs = mm.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                return devs.Count;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Returns a human-readable name for an input device index.
        /// </summary>
        private string SafeGetInputName(int index)
        {
            try
            {
                var caps = WaveInEvent.GetCapabilities(index);
                return string.IsNullOrWhiteSpace(caps.ProductName)
                    ? "(Unnamed Input)"
                    : caps.ProductName;
            }
            catch { return "(Input unavailable)"; }
        }

        /// <summary>
        /// Returns a human-readable name for an output device index using
        /// CoreAudio API. Falls back to "(Output unavailable)" on failure.
        /// </summary>
        private string SafeGetOutputName(int index)
        {
            try
            {
                using var mm = new MMDeviceEnumerator();
                var devs = mm.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                if (index < 0 || index >= devs.Count) return "(Output unavailable)";
                var name = devs[index].FriendlyName;
                return string.IsNullOrWhiteSpace(name) ? "(Unnamed Output)" : name;
            }
            catch { return "(Output unavailable)"; }
        }

        /// <summary>
        /// Ensures the input/output cursor indices are clamped within
        /// the available device count ranges to avoid out-of-range errors.
        /// </summary>
        private void ClampCursorsToDeviceCounts()
        {
            _cursorInput = Math.Clamp(_cursorInput, 0, Math.Max(0, SafeGetInputCount() - 1));
            _cursorOutput = Math.Clamp(_cursorOutput, 0, Math.Max(0, SafeGetOutputCount() - 1));
        }

        /// <summary>
        /// Stops and disposes any preview audio resources (if enabled later).
        /// Defensive no-throw implementation ensures no runtime fault on disposal.
        /// </summary>
        private void StopPreview_NoThrow()
        {
            try { _previewIn?.StopRecording(); } catch { }
            try { _previewOut?.Stop(); } catch { }
            try { _previewIn?.Dispose(); } catch { }
            try { _previewOut?.Dispose(); } catch { }
            _previewIn = null;
            _previewOut = null;
            _previewBuffer = null;
        }
    }
}
