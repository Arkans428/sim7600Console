// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: SmsManager.cs
// Description:
//   Storage-aware SMS operations for SIM7600G-H.
//   Handles multi-storage SMS management (ME + SM):
//     • ListAsync()  – Reads all SMS metadata from both "ME" and "SM" memory banks
//     • ReadAsync()  – Reads a single SMS body from the specified storage
//     • DeleteAsync()– Deletes a message from the correct memory bank
//     • SendAsync()  – Sends an SMS in text mode (+CMGF=1)
//   Notes:
//     - All storage operations switch memory context via AT+CPMS.
//     - Parsing uses robust regex for +CMGL/+CMGR lines to avoid partial matches.
//     - Results are mirrored to StatusHub for traceability.
// ============================================================================

using Sim7600Console.UI;
using System.Text.RegularExpressions;

namespace Sim7600Console.SMS
{
    /// <summary>
    /// Provides high-level SMS management on top of <see cref="ModemControl"/>,
    /// performing storage selection, parsing, and metadata extraction for
    /// both internal ("ME") and SIM ("SM") memory.
    /// </summary>
    public sealed class SmsManager
    {
        private readonly StatusHub _status;   // Reference to shared status logger
        private readonly ModemControl _modem; // Reference to AT command interface
        private readonly SmsStore _store = new(); // Local in-memory cache for read messages

        /// <summary>
        /// Constructs a new <see cref="SmsManager"/> bound to a given modem and logger.
        /// </summary>
        public SmsManager(StatusHub status, ModemControl modem)
        {
            _status = status ?? throw new ArgumentNullException(nameof(status));
            _modem = modem ?? throw new ArgumentNullException(nameof(modem));
        }

        /// <summary>
        /// Fetches and parses all SMS messages stored in both the modem’s
        /// internal memory ("ME") and SIM card memory ("SM").
        /// Each message is tagged with its originating storage so the UI
        /// can later perform context-aware read/delete operations.
        ///
        /// AT Command sequence (per storage):
        ///   1. AT+CPMS="BOX","BOX","BOX"   – Select active memory
        ///   2. AT+CMGL="ALL"               – List all stored messages
        /// 
        /// The result is parsed line-by-line to produce <see cref="SmsListItem"/> entries.
        /// </summary>
        public async Task<List<SmsListItem>> ListAsync(CancellationToken ct = default)
        {
            var items = new List<SmsListItem>();

            // Local function to enumerate one memory bank ("ME" or "SM")
            async Task ListOneAsync(string box, SmsStorage tag)
            {
                // Switch all three memory contexts (read/write/receive)
                await _modem.SendExpectOkAsync($@"AT+CPMS=""{box}"",""{box}"",""{box}""", 4000, ct)
                            .ConfigureAwait(false);

                // Request all stored messages in text mode
                var resp = await _modem.SendAsync(@"AT+CMGL=""ALL""", 8000, ct)
                                       .ConfigureAwait(false);

                // When no messages are present, some modems return only "OK"
                if (!resp.Contains("+CMGL:", StringComparison.OrdinalIgnoreCase))
                {
                    _status.Add($"[SMS] {box}: no messages.");
                    return;
                }

                int count = 0;
                // Normalize newlines and split response
                var lines = resp.Replace("\r", string.Empty)
                                .Split('\n', StringSplitOptions.RemoveEmptyEntries);

                // Iterate through all lines, parsing those starting with +CMGL:
                for (int i = 0; i < lines.Length; i++)
                {
                    var l = lines[i].Trim();
                    if (!l.StartsWith("+CMGL:", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Typical +CMGL response:
                    // +CMGL: <index>,"REC READ","+15551234567","","25/11/07,22:08:50-20"
                    // Message body follows on the next line.
                    var m = Regex.Match(l, @"\+CMGL:\s*(\d+),""[^""]*"",""([^""]*)"",""[^""]*"",""([^""]*)""");
                    if (!m.Success)
                        continue;

                    int index = int.Parse(m.Groups[1].Value);
                    string sender = m.Groups[2].Value;
                    string timestamp = m.Groups[3].Value;

                    // Extract body if present on the next line
                    string body = i + 1 < lines.Length ? lines[i + 1] : string.Empty;
                    string preview = body ?? "";
                    if (preview.Length > 60) preview = preview[..60] + "…";

                    // Append structured entry to combined result
                    items.Add(new SmsListItem
                    {
                        Index = index,
                        Sender = sender,
                        Timestamp = timestamp,
                        Preview = preview,
                        Storage = tag
                    });

                    count++;
                }

                _status.Add($"[SMS] {box}: listed {count} message(s).");
            }

            // Query both storage banks; most SIM7600G-H units store in "SM" by default.
            await ListOneAsync("ME", SmsStorage.ME).ConfigureAwait(false);
            await ListOneAsync("SM", SmsStorage.SM).ConfigureAwait(false);

            return items;
        }

        /// <summary>
        /// Reads the complete body of a specific message.
        /// Automatically switches to the correct memory bank first.
        ///
        /// AT Command sequence:
        ///   1. AT+CPMS="BOX","BOX","BOX"  – Select memory (ME or SM)
        ///   2. AT+CMGR=<index>            – Read message at index
        ///
        /// Expected response format:
        ///   +CMGR: "REC READ","+1234567890","","yy/MM/dd,HH:mm:ss-zz"
        ///   message body
        ///   OK
        /// </summary>
        public async Task<SmsMessage> ReadAsync(int index, SmsStorage storage, CancellationToken ct = default)
        {
            string box = storage == SmsStorage.SM ? "SM" : "ME";

            // Switch storage before read
            await _modem.SendExpectOkAsync($@"AT+CPMS=""{box}"",""{box}"",""{box}""", 4000, ct)
                        .ConfigureAwait(false);

            // Request the message
            var resp = await _modem.SendAsync($"AT+CMGR={index}", 5000, ct)
                                   .ConfigureAwait(false);

            var lines = resp.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);

            string status = "", sender = "", timestamp = "", body = "";
            for (int i = 0; i < lines.Length; i++)
            {
                var l = lines[i].Trim();
                if (!l.StartsWith("+CMGR:", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Typical header format for +CMGR:
                // +CMGR: "REC READ","+15551234567","","25/11/07,22:08:50-20"
                var m = Regex.Match(l, @"\+CMGR:\s*""([^""]*)"",""([^""]*)"",""[^""]*"",""([^""]*)""");
                if (m.Success)
                {
                    status = m.Groups[1].Value;
                    sender = m.Groups[2].Value;
                    timestamp = m.Groups[3].Value;
                }

                // The next line contains the message body text
                if (i + 1 < lines.Length)
                    body = lines[i + 1];
                break;
            }

            // Package message object for UI or further processing
            var msg = new SmsMessage
            {
                Index = index,
                Status = status,
                Sender = sender,
                Timestamp = timestamp,
                Body = body
            };

            _store.Upsert(msg); // cache locally
            return msg;
        }

        /// <summary>
        /// Deletes a message from the given storage.
        /// Automatically switches memory before deletion.
        ///
        /// AT Command sequence:
        ///   1. AT+CPMS="BOX","BOX","BOX" – Select memory (ME or SM)
        ///   2. AT+CMGD=<index>           – Delete message at index
        /// </summary>
        public async Task DeleteAsync(int index, SmsStorage storage, CancellationToken ct = default)
        {
            string box = storage == SmsStorage.SM ? "SM" : "ME";

            // Select proper memory first
            await _modem.SendExpectOkAsync($@"AT+CPMS=""{box}"",""{box}"",""{box}""", 4000, ct)
                        .ConfigureAwait(false);

            // Execute delete command
            await _modem.SendExpectOkAsync($"AT+CMGD={index}", 5000, ct)
                        .ConfigureAwait(false);

            // Reflect removal in in-memory store and log
            _store.Remove(index);
            _status.Add($"[SMS] {box}: deleted index {index}.");
        }

        /// <summary>
        /// Sends a text-mode SMS to a target number.
        /// 
        /// AT Command sequence:
        ///   1. AT+CMGS="number"      – Begin SMS composition
        ///      (Wait for '>' prompt)
        ///   2. Send <text> + Ctrl+Z  – Submit message
        ///   3. Expect +CMGS:<mr> and "OK"
        /// 
        /// Note:
        ///  - Text mode (AT+CMGF=1) must already be configured during modem init.
        ///  - The operation can take up to ~30 seconds for network delivery.
        /// </summary>
        public async Task SendAsync(string number, string text, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(number))
                throw new ArgumentException("Number required.", nameof(number));

            text ??= "";

            // Step 1: start message send and wait for '>' prompt
            var step1 = await _modem.SendAsync($@"AT+CMGS=""{number}""", 4000, ct)
                                    .ConfigureAwait(false);
            if (!step1.Contains(">"))
                await Task.Delay(200, ct); // allow short delay if prompt lagging

            // Step 2: send body and terminating Ctrl+Z
            var payload = text + char.ConvertFromUtf32(26); // ASCII 26 = ^Z
            var step2 = await _modem.SendAsync(payload, 30000, ct)
                                    .ConfigureAwait(false);

            // Step 3: verify success
            if (!step2.Contains("OK", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SMS send failed: " + step2);
        }
    }
}
