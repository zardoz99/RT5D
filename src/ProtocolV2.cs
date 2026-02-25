// ProtocolV2.cs
// Implements the RT-5D packet framing layer.
// All behaviour derived from RT5D_Protocol_Analysis.PDF §3 (Packet Framing)
// and §5.1 (Timeout and Retry Mechanism).
//
// Frame layout (§3):
//   [0]      SOF       = 0xA5
//   [1]      CMD       = command byte
//   [2-3]    SEQ       = sequence / page ID, big-endian uint16
//   [4-5]    LEN       = payload length N, big-endian uint16
//   [6..6+N-1] PAYLOAD = N bytes
//   [6+N]    CRC_HI    = high byte of CRC-16/CCITT
//   [6+N+1]  CRC_LO    = low byte of CRC-16/CCITT
//
// CRC coverage: bytes 1 through 5+N  (CMD, SEQ×2, LEN×2, PAYLOAD)
// Total frame size: N + 8 bytes.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace RT5D
{
    // ── Command byte constants (RT5D_Protocol_Analysis.PDF §4) ─────────────────

    public static class Commands
    {
        public const byte EndSession      = 0x01;
        public const byte Handshake       = 0x02;
        public const byte CheckPassword   = 0x05;
        public const byte GetVersion      = 0x46;

        public const byte ReadChannels    = 0x10;
        public const byte ReadVfo         = 0x11;
        public const byte ReadOptFun      = 0x12;
        public const byte ReadAddrBook    = 0x13;
        public const byte ReadRxGroups    = 0x14;
        public const byte ReadEncKeys     = 0x15;
        public const byte ReadDtmf        = 0x16;
        public const byte ReadBasicInfo   = 0x19;

        public const byte WriteChannels   = 0x30;
        public const byte WriteVfo        = 0x31;
        public const byte WriteOptFun     = 0x32;
        public const byte WriteAddrBook   = 0x33;
        public const byte WriteRxGroups   = 0x34;
        public const byte WriteEncKeys    = 0x35;
        public const byte WriteDtmf       = 0x36;
        public const byte WriteBasicInfo  = 0x39;

        /// <summary>NAK/error response from radio (§3.2, §4).</summary>
        public const byte Nak             = 0xEE;

        /// <summary>Start-of-frame sentinel (§3).</summary>
        public const byte Sof             = 0xA5;
    }

    // ── Received frame ──────────────────────────────────────────────────────────

    /// <summary>A successfully decoded frame received from the radio.</summary>
    public sealed class RadioFrame
    {
        public byte   Command  { get; }
        public ushort Sequence { get; }
        /// <summary>Payload bytes only — framing and CRC have been stripped.</summary>
        public byte[] Payload  { get; }

        public RadioFrame(byte command, ushort sequence, byte[] payload)
        {
            Command  = command;
            Sequence = sequence;
            Payload  = payload ?? Array.Empty<byte>();
        }

        public override string ToString() =>
            $"Frame[CMD=0x{Command:X2} SEQ={Sequence} LEN={Payload.Length}]";
    }

    // ── Protocol layer ──────────────────────────────────────────────────────────

    /// <summary>
    /// Implements RT-5D packet framing over a <see cref="SerialLink"/>.
    /// Handles SOF scanning, CRC validation, NAK detection, and retries.
    /// </summary>
    public sealed class ProtocolV2
    {
        // §5.1 — Timeout and Retry Mechanism
        private const int TimeoutMs  = 1000;   // ~1 second (5 ticks × 200 ms)
        private const int MaxRetries = 3;

        private readonly SerialLink _link;
        private readonly Action<string>? _log;

        /// <param name="link">Open <see cref="SerialLink"/> instance.</param>
        /// <param name="log">Optional log sink; receives human-readable protocol trace lines.</param>
        public ProtocolV2(SerialLink link, Action<string>? log = null)
        {
            _link = link ?? throw new ArgumentNullException(nameof(link));
            _log  = log;
        }

        // ── Frame building ──────────────────────────────────────────────────────

        /// <summary>
        /// Builds a complete on-wire frame byte array for the given parameters.
        /// </summary>
        /// <param name="cmd">Command byte.</param>
        /// <param name="seq">Sequence / page number (big-endian in frame).</param>
        /// <param name="payload">Payload bytes; may be empty but not null.</param>
        public static byte[] BuildFrame(byte cmd, ushort seq, byte[] payload)
        {
            if (payload is null) throw new ArgumentNullException(nameof(payload));

            int    n     = payload.Length;
            byte[] frame = new byte[n + 8];   // SOF(1) + CMD(1) + SEQ(2) + LEN(2) + PAYLOAD(N) + CRC(2)

            frame[0] = Commands.Sof;
            frame[1] = cmd;
            frame[2] = (byte)(seq >> 8);       // SEQ high byte
            frame[3] = (byte)(seq & 0xFF);     // SEQ low byte
            frame[4] = (byte)(n >> 8);         // LEN high byte
            frame[5] = (byte)(n & 0xFF);       // LEN low byte

            if (n > 0)
                Buffer.BlockCopy(payload, 0, frame, 6, n);

            // CRC covers bytes 1 through 5+N (cmd, seq, len, payload)
            ushort crc = Crc16Ccitt.Compute(frame, 1, 5 + n);
            frame[6 + n]     = (byte)(crc >> 8);    // CRC high
            frame[6 + n + 1] = (byte)(crc & 0xFF);  // CRC low

            return frame;
        }

        // ── Send / Receive ──────────────────────────────────────────────────────

        /// <summary>
        /// Sends a request frame and waits for a valid response frame.
        /// Implements the retry loop from §5.1:
        ///   — 1 000 ms timeout per attempt
        ///   — up to 3 retries
        ///   — flushes receive buffer before each retransmit
        ///   — silently ignores NAK frames (0xEE) per §3.2
        /// </summary>
        /// <returns>The response <see cref="RadioFrame"/>.</returns>
        /// <exception cref="ProtocolException">Thrown after all retries are exhausted.</exception>
        public async Task<RadioFrame> SendReceiveAsync(
            byte   cmd,
            ushort seq,
            byte[] payload,
            CancellationToken ct = default)
        {
            byte[] frame = BuildFrame(cmd, seq, payload);
            Log($"TX  {DescribeFrame(cmd, seq, payload.Length)}");

            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    Log($"    Retry {attempt}/{MaxRetries} — flushing RX buffer and retransmitting.");
                    _link.DiscardInBuffer();
                }

                await _link.WriteAsync(frame, ct).ConfigureAwait(false);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeoutMs);

                try
                {
                    RadioFrame response = await ReceiveFrameAsync(timeoutCts.Token)
                                                    .ConfigureAwait(false);

                    // NAK — silently ignore and retry (§3.2, §4)
                    if (response.Command == Commands.Nak)
                    {
                        Log($"    NAK received (0xEE), ignoring.");
                        continue;
                    }

                    Log($"RX  {response}");
                    return response;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timeout on this attempt — loop to retry
                    Log($"    Timeout on attempt {attempt + 1}.");
                    if (attempt == MaxRetries)
                        throw new ProtocolException(
                            $"No response after {MaxRetries + 1} attempts for CMD=0x{cmd:X2} SEQ={seq}.");
                }
            }

            // Unreachable — loop above always throws or returns.
            throw new ProtocolException("Unexpected exit from retry loop.");
        }

        /// <summary>
        /// Sends a frame and does NOT wait for a response.
        /// Used for fire-and-forget cases if ever needed; currently only the
        /// End Session step could use this, but we still wait for the radio's ACK.
        /// </summary>
        public async Task SendOnlyAsync(byte cmd, ushort seq, byte[] payload,
                                        CancellationToken ct = default)
        {
            byte[] frame = BuildFrame(cmd, seq, payload);
            Log($"TX  {DescribeFrame(cmd, seq, payload.Length)} [no-reply]");
            await _link.WriteAsync(frame, ct).ConfigureAwait(false);
        }

        // ── Receive state machine (§3.1) ────────────────────────────────────────

        /// <summary>
        /// Reads one complete, CRC-validated frame from the link.
        /// Implements the three-stage receive state machine from §3.1.
        /// </summary>
        private async Task<RadioFrame> ReceiveFrameAsync(CancellationToken ct)
        {
            // ── Stage 1: scan for SOF (0xA5) ───────────────────────────────────
            byte sof;
            do
            {
                sof = await _link.ReadByteAsync(ct).ConfigureAwait(false);
            }
            while (sof != Commands.Sof);

            // ── Stage 2: read 5-byte header (CMD, SEQ×2, LEN×2) ───────────────
            byte[] header = await _link.ReadExactAsync(5, ct).ConfigureAwait(false);

            byte   cmd    = header[0];
            ushort seq    = (ushort)((header[1] << 8) | header[2]);
            int    len    = (header[3] << 8) | header[4];

            if (len > 65535)
                throw new ProtocolException($"Implausible payload length {len} in received header.");

            // ── Stage 3: read payload + 2 CRC bytes, validate ─────────────────
            byte[] body   = await _link.ReadExactAsync(len + 2, ct).ConfigureAwait(false);

            byte[] payload = new byte[len];
            if (len > 0) Buffer.BlockCopy(body, 0, payload, 0, len);

            ushort rxCrc   = (ushort)((body[len] << 8) | body[len + 1]);

            // Build the byte sequence that the CRC was computed over:
            // header bytes (cmd, seq×2, len×2) then payload — i.e. everything
            // except the SOF and the CRC bytes themselves.
            byte[] crcData = new byte[5 + len];
            Buffer.BlockCopy(header, 0, crcData, 0, 5);
            if (len > 0) Buffer.BlockCopy(payload, 0, crcData, 5, len);

            ushort calcCrc = Crc16Ccitt.Compute(crcData, 0, crcData.Length);

            if (calcCrc != rxCrc)
                throw new ProtocolException(
                    $"CRC mismatch on received frame CMD=0x{cmd:X2} SEQ={seq}: " +
                    $"expected 0x{calcCrc:X4}, got 0x{rxCrc:X4}.");

            return new RadioFrame(cmd, seq, payload);
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private void Log(string message) => _log?.Invoke(message);

        private static string DescribeFrame(byte cmd, ushort seq, int payloadLen) =>
            $"Frame[CMD=0x{cmd:X2} SEQ={seq} LEN={payloadLen}]";
    }

    // ── Exception type ──────────────────────────────────────────────────────────

    /// <summary>
    /// Thrown when the protocol layer cannot complete an exchange
    /// (CRC failure, retry exhaustion, or malformed frame).
    /// </summary>
    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message) { }
        public ProtocolException(string message, Exception inner) : base(message, inner) { }
    }
}
