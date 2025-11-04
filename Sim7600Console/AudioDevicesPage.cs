// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: AudioDevicesPage.cs
// Description:
//   Lets the user choose audio input (microphone) and output (speaker) devices
//   detected by NAudio. Selections are saved into AppSession so AudioBridge
//   (or future audio layers) can honor them.
//   Keys:
//     • 1 : Focus Input list
//     • 2 : Focus Output list
//     • ↑/↓ : Move cursor within focused list
//     • ENTER : Select focused item
//     • ESC : Back
// Notes:
//   - Uses WaveIn/WaveOut static enumerators to list devices.
//   - Defines preview fields (_previewIn/_previewOut) so no undefined identifiers.
//     (Preview is not auto-started; safe placeholders for future enhancements.)
// ============================================================================

using System;
using NAudio;
using NAudio.Wave;
using NAudio.CoreAudioApi; // for MMDeviceEnumerator (WASAPI)

namespace Sim7600Console
{
    public sealed class AudioDevicesPage : PageBase
    {
        private enum Focus { Input, Output }

        // UI state
        private Focus _focus = Focus.Input;
        private int _cursorInput = 0;   // Index into input device list
        private int _cursorOutput = 0;  // Index into output device list

        // Optional preview fields (defined so they exist; not auto-used yet).
        // You can wire these up later to audition devices.
        private WaveInEvent? _previewIn;
        private WaveOutEvent? _previewOut;
        private BufferedWaveProvider? _previewBuffer;
        
        

        public AudioDevicesPage(ConsoleUi ui, AppSession session) : base(ui, session)
        {
            Session.Status.Add("Audio Devices page opened.");
            ClampCursorsToDeviceCounts();

            
        }

        public override void Draw(int width, int height)
        {
            DrawTitleRow(width, Header("Audio Devices"));

            int row = 2;
            SafeWrite(0, row++, "ESC = Back | 1 = Focus Input | 2 = Focus Output | ENTER = Select");
            SafeWrite(0, row++, "");

            // Read current device counts every draw in case devices changed.
            int inCount = SafeGetInputCount();
            int outCount = SafeGetOutputCount();

            // --- Input list ---
            SafeWrite(0, row++, $"Input Devices (microphones) [{inCount}] {(_focus == Focus.Input ? "◀ focus" : "")}");
            int inputShown = DrawDeviceList(row, width, height, listCount: inCount, currentCursor: _cursorInput,
                isInput: true,
                selectedId: Session.AudioInputDeviceId);
            row += inputShown;

            SafeWrite(0, row++, "");

            // --- Output list ---
            SafeWrite(0, row++, $"Output Devices (speakers) [{outCount}] {(_focus == Focus.Output ? "◀ focus" : "")}");
            int outputShown = DrawDeviceList(row, width, height, listCount: outCount, currentCursor: _cursorOutput,
                isInput: false,
                selectedId: Session.AudioOutputDeviceId);
            row += outputShown;

            // Footer line with current selections
            SafeWrite(0, row++, "");
            var inSel = Session.AudioInputDeviceId is int iid && iid >= 0 ? iid.ToString() : "—";
            var outSel = Session.AudioOutputDeviceId is int oid && oid >= 0 ? oid.ToString() : "—";
            SafeWrite(0, row++, $"Selected: InputID={inSel}, OutputID={outSel}");
        }

        public override bool HandleKey(ConsoleKeyInfo key, out PageNav nav)
        {
            nav = PageNav.None;

            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    nav = PageNav.Pop; // back
                    return true;

                case ConsoleKey.D1:
                    _focus = Focus.Input;
                    Ui.Redraw();
                    return true;

                case ConsoleKey.D2:
                    _focus = Focus.Output;
                    Ui.Redraw();
                    return true;

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

                case ConsoleKey.Enter:
                    return OnSelect(out nav);

                default:
                    return false;
            }
        }

        // Called when user presses ENTER; stores the focused selection into AppSession
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

        // Renders a clamped list of devices. Highlights cursor with ▶ and marks currently selected with [Selected].
        private int DrawDeviceList(int startRow, int width, int height, int listCount, int currentCursor, bool isInput, int? selectedId)
        {
            if (listCount <= 0)
            {
                SafeWrite(0, startRow, "  (No devices found)");
                return 1;
            }

            // Fit into remaining vertical space; show a small window around the cursor
            int maxRows = Math.Max(4, height - startRow - 6);
            int window = Math.Max(3, maxRows);
            int first = Math.Clamp(currentCursor - window / 2, 0, Math.Max(0, listCount - window));
            int lastEx = Math.Min(listCount, first + window);

            int y = startRow;
            for (int i = first; i < lastEx; i++)
            {
                bool isCursor = (i == currentCursor);
                bool isSelected = (selectedId.HasValue && selectedId.Value == i);

                string marker = isCursor ? "▶" : " ";
                string sel = isSelected ? " [Selected]" : "";

                string name = isInput ? SafeGetInputName(i) : SafeGetOutputName(i);
                string line = $"{marker} [{i}] {name}{sel}";
                if (line.Length > width) line = line[..width];
                SafeWrite(0, y++, line);
            }

            if (first > 0) SafeWrite(0, y++, "  …");
            if (lastEx < listCount) SafeWrite(0, y++, "  …");

            return y - startRow;
        }

        private int SafeGetInputCount()
        {
            try { return WaveInEvent.DeviceCount; } catch { return 0; }
        }


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

        private void ClampCursorsToDeviceCounts()
        {
            _cursorInput = Math.Clamp(_cursorInput, 0, Math.Max(0, SafeGetInputCount() - 1));
            _cursorOutput = Math.Clamp(_cursorOutput, 0, Math.Max(0, SafeGetOutputCount() - 1));
        }

        // Optional preview cleanup if you later enable audition
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
