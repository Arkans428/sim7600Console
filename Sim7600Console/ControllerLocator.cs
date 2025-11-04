// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: ControllerLocator.cs
// Description:
//   Central helper for retrieving and registering the PhoneController and the
//   "voice call begin" callback from/to the AppDomain's data bag.
//   Why AppDomain data?
//     - It provides a simple global registry without static singletons scattered
//       across the codebase.
//     - Pages can safely query for a controller (null if not initialized) and
//       degrade gracefully if the controller hasn't been created yet.
//   The ModemControl emits URCs (e.g., CONNECT / "VOICE CALL: BEGIN") in a
//   background serial thread; to trigger AudioBridge start from there without
//   creating a hard dependency on PhoneController, we store a delegate under
//   a well-known key ("sim7600_voice_cb").
// ============================================================================

using System;

namespace Sim7600Console
{
    public static class ControllerLocator
    {
        /// <summary>
        /// Attempts to retrieve the PhoneController instance that was previously
        /// registered via <see cref="Register"/>. Returns null if initialization
        /// hasn't happened yet (e.g., user hasn't confirmed port selection).
        /// </summary>
        public static PhoneController? Phone =>
            AppDomain.CurrentDomain.GetData("sim7600_phone") as PhoneController;

        /// <summary>
        /// Registers the controller instance and its voice-call-begin callback in the
        /// AppDomain data bag:
        ///   - "sim7600_phone"   => PhoneController instance
        ///   - "sim7600_voice_cb"=> Action delegate invoked by ModemControl when a
        ///                          voice call becomes active; this starts audio streaming.
        /// 
        /// Using a callback avoids ModemControl referencing PhoneController directly,
        /// keeping responsibilities clean: ModemControl parses AT/URCs; PhoneController
        /// orchestrates audio; the AppDomain data acts as the bridge.
        /// </summary>
        public static void Register(PhoneController controller)
        {
            if (controller == null) throw new ArgumentNullException(nameof(controller));

            // Store strongly-typed controller for UI pages and managers to consume.
            AppDomain.CurrentDomain.SetData("sim7600_phone", controller);

            // Store a callback function that starts the voice audio streaming.
            // ModemControl invokes this when it sees CONNECT / "VOICE CALL: BEGIN".
            AppDomain.CurrentDomain.SetData("sim7600_voice_cb", (Action)(() => controller.NotifyVoiceCallBegin()));
        }
    }
}
