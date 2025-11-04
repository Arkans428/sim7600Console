// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: InstanceInfo.cs
// Description:
//   Holds static identity & version info to stamp into logs, headers, etc.
// ============================================================================

namespace Sim7600Console
{
    /// <summary>
    /// Centralized instance identity for consistent credit in all generated files.
    /// </summary>
    public static class InstanceInfo
    {
        /// <summary>
        /// ID for this generated program instance. Keep this consistent across files.
        /// </summary>
        public const string InstanceId = "SIM7600Consolev0";

        /// <summary>
        /// A human-friendly banner to show in UI pages if desired.
        /// </summary>
        public const string Banner =
            "SIM7600G-H Voice & SMS Console — Instance: " + InstanceId;
    }
}
