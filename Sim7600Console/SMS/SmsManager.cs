// ============================================================================
// Authored by AI (Instance ID: gpt5t-2025-10-26-kv1)
// File: SmsManager.cs
// Description:
//   Storage-aware SMS operations for SIM7600G-H.
//   - ListAsync(): merges ME and SM with storage tags
//   - ReadAsync(index, storage)
//   - DeleteAsync(index, storage)
//   - SendAsync(number, text) unchanged (text mode)
// ============================================================================

using Sim7600Console.UI;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Sim7600Console.SMS
{
    public sealed class SmsManager
    {
        private readonly StatusHub _status;
        private readonly ModemControl _modem;
        private readonly SmsStore _store = new();

        public SmsManager(StatusHub status, ModemControl modem)
        {
            _status = status ?? throw new ArgumentNullException(nameof(status));
            _modem = modem ?? throw new ArgumentNullException(nameof(modem));
        }

        /// <summary>
        /// Fetches SMS from both ME and SM storages. Items are tagged with their storage
        /// so later reads/deletes can target the correct memory bank.
        /// </summary>
        public async Task<List<SmsListItem>> ListAsync(CancellationToken ct = default)
        {
            var items = new List<SmsListItem>();

            async Task ListOneAsync(string box, SmsStorage tag)
            {
                // Select the storage for read, write, and receive: CPMS="<box>","<box>","<box>"
                await _modem.SendExpectOkAsync($@"AT+CPMS=""{box}"",""{box}"",""{box}""", 4000, ct)
                            .ConfigureAwait(false);

                var resp = await _modem.SendAsync(@"AT+CMGL=""ALL""", 8000, ct)
                                       .ConfigureAwait(false);

                // Empty set often returns only OK
                if (!resp.Contains("+CMGL:", StringComparison.OrdinalIgnoreCase))
                {
                    _status.Add($"[SMS] {box}: no messages.");
                    return;
                }

                int count = 0;
                // Lines alternate: +CMGL meta / body
                var lines = resp.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < lines.Length; i++)
                {
                    var l = lines[i].Trim();
                    if (!l.StartsWith("+CMGL:", StringComparison.OrdinalIgnoreCase)) continue;

                    var m = Regex.Match(l, @"\+CMGL:\s*(\d+),""[^""]*"",""([^""]*)"",""[^""]*"",""([^""]*)""");
                    if (!m.Success) continue;

                    int index = int.Parse(m.Groups[1].Value);
                    string sender = m.Groups[2].Value;
                    string timestamp = m.Groups[3].Value;

                    string body = i + 1 < lines.Length ? lines[i + 1] : string.Empty;
                    string preview = body ?? "";
                    if (preview.Length > 60) preview = preview[..60] + "…";

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

            // Many modules keep real messages in SM; we still scan both.
            await ListOneAsync("ME", SmsStorage.ME).ConfigureAwait(false);
            await ListOneAsync("SM", SmsStorage.SM).ConfigureAwait(false);

            return items;
        }

        /// <summary>Reads a single message from the specified storage.</summary>
        public async Task<SmsMessage> ReadAsync(int index, SmsStorage storage, CancellationToken ct = default)
        {
            string box = storage == SmsStorage.SM ? "SM" : "ME";
            await _modem.SendExpectOkAsync($@"AT+CPMS=""{box}"",""{box}"",""{box}""", 4000, ct)
                        .ConfigureAwait(false);

            var resp = await _modem.SendAsync($"AT+CMGR={index}", 5000, ct)
                                   .ConfigureAwait(false);

            // Expect:
            // +CMGR: "REC READ","+1234567890","","yy/MM/dd,HH:mm:ss-zz"
            // Body...
            // OK
            var lines = resp.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);

            string status = "", sender = "", timestamp = "", body = "";
            for (int i = 0; i < lines.Length; i++)
            {
                var l = lines[i].Trim();
                if (!l.StartsWith("+CMGR:", StringComparison.OrdinalIgnoreCase)) continue;

                var m = Regex.Match(l, @"\+CMGR:\s*""([^""]*)"",""([^""]*)"",""[^""]*"",""([^""]*)""");
                if (m.Success)
                {
                    status = m.Groups[1].Value;
                    sender = m.Groups[2].Value;
                    timestamp = m.Groups[3].Value;
                }
                if (i + 1 < lines.Length) body = lines[i + 1];
                break;
            }

            var msg = new SmsMessage
            {
                Index = index,
                Status = status,
                Sender = sender,
                Timestamp = timestamp,
                Body = body
            };
            _store.Upsert(msg);
            return msg;
        }

        /// <summary>Deletes a message from the specified storage.</summary>
        public async Task DeleteAsync(int index, SmsStorage storage, CancellationToken ct = default)
        {
            string box = storage == SmsStorage.SM ? "SM" : "ME";
            await _modem.SendExpectOkAsync($@"AT+CPMS=""{box}"",""{box}"",""{box}""", 4000, ct)
                        .ConfigureAwait(false);

            await _modem.SendExpectOkAsync($"AT+CMGD={index}", 5000, ct).ConfigureAwait(false);
            _store.Remove(index);
            _status.Add($"[SMS] {box}: deleted index {index}.");
        }

        /// <summary>
        /// Sends a text SMS in text mode.
        /// </summary>
        public async Task SendAsync(string number, string text, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(number)) throw new ArgumentException("Number required.", nameof(number));
            text ??= "";

            var step1 = await _modem.SendAsync($@"AT+CMGS=""{number}""", 4000, ct).ConfigureAwait(false);
            if (!step1.Contains(">")) await Task.Delay(200, ct);

            var payload = text + char.ConvertFromUtf32(26); // Ctrl+Z
            var step2 = await _modem.SendAsync(payload, 30000, ct).ConfigureAwait(false);

            if (!step2.Contains("OK", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SMS send failed: " + step2);
        }
    }
}
