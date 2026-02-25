// SessionV2.cs
// Implements the complete RT-5D programming session sequence.
// All step ordering, packet counts, and payload sizes are derived from
// RT5D_Protocol_Analysis.PDF §5 (Full Protocol Sequence).
//
// The sequence is STRICT and must not be reordered (§10.3):
//   1.  Handshake        (0x02)     — 1 packet,  15 bytes
//   2.  Check Password   (0x05)     — 1 packet,   6 bytes
//   3.  Get Version      (0x46)     — 1 packet, 128 bytes
//   4.  DTMF             (0x16/36)  — 1 packet, 272 bytes
//   5.  Encryption Keys  (0x15/35)  — 1 packet, 264 bytes
//   6.  Address Book     (0x13/33)  — 80 packets, 800 bytes/pkt
//   7.  Rx Group List    (0x14/34)  — 4 packets, 1024 bytes/pkt
//   8.  Channel Mode     (0x10/30)  — 64 packets, 1024 bytes/pkt
//   9.  VFO Mode         (0x11/31)  — 1 packet, 128 bytes
//  10.  Optional Funs    (0x12/32)  — 1 packet,  64 bytes
//  11.  Basic Info       (0x19/39)  — 1 packet,  64 bytes (write is conditional)
//  12.  End Session      (0x01)     — 1 packet,   2 bytes

using System;
using System.Threading;
using System.Threading.Tasks;

namespace RT5D
{
    // ── Raw session data (flat byte arrays, one per block) ──────────────────────

    /// <summary>
    /// All raw payload bytes read from or to be written to the radio.
    /// Higher layers (DataBlocksV2) encode/decode these arrays.
    /// </summary>
    public sealed class SessionData
    {
        // Block sizes as per §5 and Appendix B of the protocol PDF.
        public byte[]   VersionData    { get; set; } = new byte[Sizes.Version];
        public byte[]   DtmfData       { get; set; } = new byte[Sizes.Dtmf];
        public byte[]   EncKeyData     { get; set; } = new byte[Sizes.EncKeys];

        // Address book: 80 packets × 800 bytes = 64 000 bytes total
        public byte[][] AddrBookPackets { get; set; } = AllocJagged(Sizes.AddrBookPackets, Sizes.AddrBookPerPacket);

        // Rx groups: 4 packets × 1 024 bytes = 4 096 bytes total
        public byte[][] RxGroupPackets  { get; set; } = AllocJagged(Sizes.RxGroupPackets, Sizes.RxGroupPerPacket);

        // Channels: 64 packets × 1 024 bytes = 65 536 bytes total
        public byte[][] ChannelPackets  { get; set; } = AllocJagged(Sizes.ChannelPackets, Sizes.ChannelPerPacket);

        public byte[]   VfoData         { get; set; } = new byte[Sizes.Vfo];
        public byte[]   OptFunData      { get; set; } = new byte[Sizes.OptFun];
        public byte[]   BasicInfoData   { get; set; } = new byte[Sizes.BasicInfo];

        private static byte[][] AllocJagged(int count, int size)
        {
            var arr = new byte[count][];
            for (int i = 0; i < count; i++)
                arr[i] = new byte[size];
            return arr;
        }
    }

    // ── Payload size constants (RT5D_Protocol_Analysis.PDF §5 / Appendix B) ────

    public static class Sizes
    {
        public const int Handshake          =  15;
        public const int Password           =   6;
        public const int Version            = 128;
        public const int Dtmf               = 272;
        public const int EncKeys            = 264;

        public const int AddrBookPackets    =  80;
        public const int AddrBookPerPacket  = 800;

        public const int RxGroupPackets     =   4;
        public const int RxGroupPerPacket   = 1024;

        public const int ChannelPackets     =  64;
        public const int ChannelPerPacket   = 1024;

        public const int Vfo                = 128;
        public const int OptFun             =  64;
        public const int BasicInfo          =  64;
        public const int EndSession         =   2;
    }

    // ── Fixed payloads ──────────────────────────────────────────────────────────

    public static class FixedPayloads
    {
        /// <summary>Handshake string "PROGRAMJC8810DU" (§3.1 / Data Structures §1).</summary>
        public static readonly byte[] Handshake = {
            0x50, 0x52, 0x4F, 0x47, 0x52, 0x41, 0x4D,
            0x4A, 0x43, 0x38, 0x38, 0x31, 0x30, 0x44, 0x55
        };

        /// <summary>Default blank password — 6 × 0xFF (§2 / Data Structures §2).</summary>
        public static readonly byte[] Password = {
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF
        };

        /// <summary>Version request — empty payload; radio identifies the block from the command byte alone.</summary>
        public static byte[] VersionRequest() => Array.Empty<byte>();

        /// <summary>End session payload — 2 zero bytes (Data Structures §12).</summary>
        public static readonly byte[] EndSession = { 0x00, 0x00 };
    }

    // ── Progress reporting ──────────────────────────────────────────────────────

    public sealed class SessionProgress
    {
        public string Phase          { get; init; } = "";
        public int    PacketIndex    { get; init; }
        public int    TotalPackets   { get; init; }
        public bool   IsComplete     { get; init; }

        public override string ToString() =>
            TotalPackets > 1
                ? $"[{Phase}] packet {PacketIndex + 1}/{TotalPackets}"
                : $"[{Phase}]";
    }

    // ── Session ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Executes a complete read or write programming session with the RT-5D radio.
    /// </summary>
    public sealed class SessionV2
    {
        private readonly ProtocolV2           _proto;
        private readonly Action<string>?      _log;
        private readonly Action<SessionProgress>? _progress;

        public SessionV2(
            ProtocolV2               proto,
            Action<string>?          log      = null,
            Action<SessionProgress>? progress = null)
        {
            _proto    = proto    ?? throw new ArgumentNullException(nameof(proto));
            _log      = log;
            _progress = progress;
        }

        // ── Public entry points ─────────────────────────────────────────────────

        /// <summary>
        /// Executes a full READ session — sends Read commands and collects all
        /// payload data from the radio.
        /// </summary>
        public async Task<SessionData> ReadAsync(CancellationToken ct = default)
        {
            var data = new SessionData();
            Log("=== READ SESSION BEGIN ===");

            await Step1_HandshakeAsync(ct);
            await Step2_PasswordAsync(ct);
            data.VersionData   = await Step3_GetVersionAsync(ct);
            data.DtmfData      = await Step4_ReadDtmfAsync(ct);
            data.EncKeyData    = await Step5_ReadEncKeysAsync(ct);
            data.AddrBookPackets = await Step6_ReadAddrBookAsync(ct);
            data.RxGroupPackets  = await Step7_ReadRxGroupsAsync(ct);
            data.ChannelPackets  = await Step8_ReadChannelsAsync(ct);
            data.VfoData       = await Step9_ReadVfoAsync(ct);
            data.OptFunData    = await Step10_ReadOptFunAsync(ct);
            data.BasicInfoData = await Step11_ReadBasicInfoAsync(ct);
            await Step12_EndSessionAsync(ct);

            Log("=== READ SESSION COMPLETE ===");
            return data;
        }

        /// <summary>
        /// Executes a full WRITE session — sends Write commands carrying the
        /// provided payload data to the radio.
        /// </summary>
        /// <param name="writeBasicInfo">
        /// When false (default), the Basic Info step is skipped.
        /// Set true only for production-line model stamping (§5, Step 11 note).
        /// </param>
        public async Task WriteAsync(
            SessionData data,
            bool        writeBasicInfo = false,
            CancellationToken ct = default)
        {
            if (data is null) throw new ArgumentNullException(nameof(data));
            Log("=== WRITE SESSION BEGIN ===");

            await Step1_HandshakeAsync(ct);
            await Step2_PasswordAsync(ct);
            await Step3_GetVersionAsync(ct);    // still required even in write mode
            await Step4_WriteDtmfAsync(data.DtmfData, ct);
            await Step5_WriteEncKeysAsync(data.EncKeyData, ct);
            await Step6_WriteAddrBookAsync(data.AddrBookPackets, ct);
            await Step7_WriteRxGroupsAsync(data.RxGroupPackets, ct);
            await Step8_WriteChannelsAsync(data.ChannelPackets, ct);
            await Step9_WriteVfoAsync(data.VfoData, ct);
            await Step10_WriteOptFunAsync(data.OptFunData, ct);

            if (writeBasicInfo)
                await Step11_WriteBasicInfoAsync(data.BasicInfoData, ct);
            else
                Log("[BasicInfo] skipped (WriteModelNameAndId not set).");

            await Step12_EndSessionAsync(ct);

            Log("=== WRITE SESSION COMPLETE ===");
        }

        // ── Step implementations ────────────────────────────────────────────────

        // Step 1 — Handshake (0x02)
        private async Task Step1_HandshakeAsync(CancellationToken ct)
        {
            Progress("Handshake", 0, 1);
            Log("[Handshake] Sending PROGRAMJC8810DU");
            await _proto.SendReceiveAsync(Commands.Handshake, 0, FixedPayloads.Handshake, ct)
                        .ConfigureAwait(false);
            // CPS does not validate reply body, only that a valid frame arrives (§1/Data Struct).
        }

        // Step 2 — Password (0x05)
        private async Task Step2_PasswordAsync(CancellationToken ct)
        {
            Progress("Password", 0, 1);
            Log("[Password] Sending default blank password (FF×6)");
            var resp = await _proto.SendReceiveAsync(Commands.CheckPassword, 0, FixedPayloads.Password, ct)
                                   .ConfigureAwait(false);
            // A NAK (0xEE) would have been handled by ProtocolV2; if we get here, auth succeeded.
            _ = resp;
        }

        // Step 3 — Version (0x46) — always sent, even in write mode
        private async Task<byte[]> Step3_GetVersionAsync(CancellationToken ct)
        {
            Progress("Version", 0, 1);
            Log("[Version] Requesting version block");
            var resp = await _proto.SendReceiveAsync(Commands.GetVersion, 0,
                                                      Array.Empty<byte>(), ct)
                                   .ConfigureAwait(false);
            // CPS does not parse the version response (§3/Data Struct).
            return resp.Payload;
        }

        // ── DTMF ───────────────────────────────────────────────────────────────

        private async Task<byte[]> Step4_ReadDtmfAsync(CancellationToken ct)
        {
            Progress("DTMF Read", 0, 1);
            Log("[DTMF] Reading");
            var resp = await _proto.SendReceiveAsync(Commands.ReadDtmf, 0,
                                                      new byte[Sizes.Dtmf], ct)
                                   .ConfigureAwait(false);
            ValidatePayloadSize(resp, Sizes.Dtmf, "DTMF");
            return resp.Payload;
        }

        private async Task Step4_WriteDtmfAsync(byte[] payload, CancellationToken ct)
        {
            ValidateBufferSize(payload, Sizes.Dtmf, "DTMF");
            Progress("DTMF Write", 0, 1);
            Log("[DTMF] Writing");
            await _proto.SendReceiveAsync(Commands.WriteDtmf, 0, payload, ct).ConfigureAwait(false);
        }

        // ── Encryption Keys ────────────────────────────────────────────────────

        private async Task<byte[]> Step5_ReadEncKeysAsync(CancellationToken ct)
        {
            Progress("EncKeys Read", 0, 1);
            Log("[EncKeys] Reading");
            var resp = await _proto.SendReceiveAsync(Commands.ReadEncKeys, 0,
                                                      new byte[Sizes.EncKeys], ct)
                                   .ConfigureAwait(false);
            ValidatePayloadSize(resp, Sizes.EncKeys, "EncKeys");
            return resp.Payload;
        }

        private async Task Step5_WriteEncKeysAsync(byte[] payload, CancellationToken ct)
        {
            ValidateBufferSize(payload, Sizes.EncKeys, "EncKeys");
            Progress("EncKeys Write", 0, 1);
            Log("[EncKeys] Writing");
            await _proto.SendReceiveAsync(Commands.WriteEncKeys, 0, payload, ct).ConfigureAwait(false);
        }

        // ── Address Book (80 packets) ──────────────────────────────────────────

        private async Task<byte[][]> Step6_ReadAddrBookAsync(CancellationToken ct)
        {
            Log($"[AddrBook] Reading {Sizes.AddrBookPackets} packets");
            var packets = new byte[Sizes.AddrBookPackets][];
            for (int i = 0; i < Sizes.AddrBookPackets; i++)
            {
                Progress("AddrBook Read", i, Sizes.AddrBookPackets);
                var resp = await _proto.SendReceiveAsync(Commands.ReadAddrBook,
                                                          (ushort)i,
                                                          new byte[Sizes.AddrBookPerPacket], ct)
                                       .ConfigureAwait(false);
                ValidatePayloadSize(resp, Sizes.AddrBookPerPacket, $"AddrBook[{i}]");
                packets[i] = resp.Payload;
            }
            return packets;
        }

        private async Task Step6_WriteAddrBookAsync(byte[][] packets, CancellationToken ct)
        {
            ValidatePacketArray(packets, Sizes.AddrBookPackets, Sizes.AddrBookPerPacket, "AddrBook");
            Log($"[AddrBook] Writing {Sizes.AddrBookPackets} packets");
            for (int i = 0; i < Sizes.AddrBookPackets; i++)
            {
                Progress("AddrBook Write", i, Sizes.AddrBookPackets);
                await _proto.SendReceiveAsync(Commands.WriteAddrBook,
                                              (ushort)i, packets[i], ct)
                             .ConfigureAwait(false);
            }
        }

        // ── Rx Group List (4 packets) ──────────────────────────────────────────

        private async Task<byte[][]> Step7_ReadRxGroupsAsync(CancellationToken ct)
        {
            Log($"[RxGroups] Reading {Sizes.RxGroupPackets} packets");
            var packets = new byte[Sizes.RxGroupPackets][];
            for (int i = 0; i < Sizes.RxGroupPackets; i++)
            {
                Progress("RxGroups Read", i, Sizes.RxGroupPackets);
                var resp = await _proto.SendReceiveAsync(Commands.ReadRxGroups,
                                                          (ushort)i,
                                                          new byte[Sizes.RxGroupPerPacket], ct)
                                       .ConfigureAwait(false);
                ValidatePayloadSize(resp, Sizes.RxGroupPerPacket, $"RxGroups[{i}]");
                packets[i] = resp.Payload;
            }
            return packets;
        }

        private async Task Step7_WriteRxGroupsAsync(byte[][] packets, CancellationToken ct)
        {
            ValidatePacketArray(packets, Sizes.RxGroupPackets, Sizes.RxGroupPerPacket, "RxGroups");
            Log($"[RxGroups] Writing {Sizes.RxGroupPackets} packets");
            for (int i = 0; i < Sizes.RxGroupPackets; i++)
            {
                Progress("RxGroups Write", i, Sizes.RxGroupPackets);
                await _proto.SendReceiveAsync(Commands.WriteRxGroups,
                                              (ushort)i, packets[i], ct)
                             .ConfigureAwait(false);
            }
        }

        // ── Channels (64 packets) ──────────────────────────────────────────────

        private async Task<byte[][]> Step8_ReadChannelsAsync(CancellationToken ct)
        {
            Log($"[Channels] Reading {Sizes.ChannelPackets} packets");
            var packets = new byte[Sizes.ChannelPackets][];
            for (int i = 0; i < Sizes.ChannelPackets; i++)
            {
                Progress("Channels Read", i, Sizes.ChannelPackets);
                var resp = await _proto.SendReceiveAsync(Commands.ReadChannels,
                                                          (ushort)i,
                                                          new byte[Sizes.ChannelPerPacket], ct)
                                       .ConfigureAwait(false);
                ValidatePayloadSize(resp, Sizes.ChannelPerPacket, $"Channels[{i}]");
                packets[i] = resp.Payload;
            }
            return packets;
        }

        private async Task Step8_WriteChannelsAsync(byte[][] packets, CancellationToken ct)
        {
            ValidatePacketArray(packets, Sizes.ChannelPackets, Sizes.ChannelPerPacket, "Channels");
            Log($"[Channels] Writing {Sizes.ChannelPackets} packets");
            for (int i = 0; i < Sizes.ChannelPackets; i++)
            {
                Progress("Channels Write", i, Sizes.ChannelPackets);
                await _proto.SendReceiveAsync(Commands.WriteChannels,
                                              (ushort)i, packets[i], ct)
                             .ConfigureAwait(false);
            }
        }

        // ── VFO Mode (1 packet) ────────────────────────────────────────────────

        private async Task<byte[]> Step9_ReadVfoAsync(CancellationToken ct)
        {
            Progress("VFO Read", 0, 1);
            Log("[VFO] Reading");
            var resp = await _proto.SendReceiveAsync(Commands.ReadVfo, 0,
                                                      new byte[Sizes.Vfo], ct)
                                   .ConfigureAwait(false);
            ValidatePayloadSize(resp, Sizes.Vfo, "VFO");
            return resp.Payload;
        }

        private async Task Step9_WriteVfoAsync(byte[] payload, CancellationToken ct)
        {
            ValidateBufferSize(payload, Sizes.Vfo, "VFO");
            Progress("VFO Write", 0, 1);
            Log("[VFO] Writing");
            await _proto.SendReceiveAsync(Commands.WriteVfo, 0, payload, ct).ConfigureAwait(false);
        }

        // ── Optional Functions (1 packet) ─────────────────────────────────────

        private async Task<byte[]> Step10_ReadOptFunAsync(CancellationToken ct)
        {
            Progress("OptFun Read", 0, 1);
            Log("[OptFun] Reading");
            var resp = await _proto.SendReceiveAsync(Commands.ReadOptFun, 0,
                                                      new byte[Sizes.OptFun], ct)
                                   .ConfigureAwait(false);
            ValidatePayloadSize(resp, Sizes.OptFun, "OptFun");
            return resp.Payload;
        }

        private async Task Step10_WriteOptFunAsync(byte[] payload, CancellationToken ct)
        {
            ValidateBufferSize(payload, Sizes.OptFun, "OptFun");
            Progress("OptFun Write", 0, 1);
            Log("[OptFun] Writing");
            await _proto.SendReceiveAsync(Commands.WriteOptFun, 0, payload, ct).ConfigureAwait(false);
        }

        // ── Basic Info (1 packet, read always / write conditional) ────────────

        private async Task<byte[]> Step11_ReadBasicInfoAsync(CancellationToken ct)
        {
            Progress("BasicInfo Read", 0, 1);
            Log("[BasicInfo] Reading");
            var resp = await _proto.SendReceiveAsync(Commands.ReadBasicInfo, 0,
                                                      new byte[Sizes.BasicInfo], ct)
                                   .ConfigureAwait(false);
            ValidatePayloadSize(resp, Sizes.BasicInfo, "BasicInfo");
            return resp.Payload;
        }

        private async Task Step11_WriteBasicInfoAsync(byte[] payload, CancellationToken ct)
        {
            ValidateBufferSize(payload, Sizes.BasicInfo, "BasicInfo");
            Progress("BasicInfo Write", 0, 1);
            Log("[BasicInfo] Writing model name / ID");
            await _proto.SendReceiveAsync(Commands.WriteBasicInfo, 0, payload, ct).ConfigureAwait(false);
        }

        // ── End Session (0x01) ─────────────────────────────────────────────────

        private async Task Step12_EndSessionAsync(CancellationToken ct)
        {
            Progress("EndSession", 0, 1);
            Log("[EndSession] Sending end-of-session marker (00 00)");
            await _proto.SendReceiveAsync(Commands.EndSession, 0, FixedPayloads.EndSession, ct)
                        .ConfigureAwait(false);
        }

        // ── Internal helpers ────────────────────────────────────────────────────

        private void Log(string msg) => _log?.Invoke(msg);

        private void Progress(string phase, int idx, int total)
        {
            _progress?.Invoke(new SessionProgress
            {
                Phase       = phase,
                PacketIndex = idx,
                TotalPackets = total,
                IsComplete  = false,
            });
        }

        private static void ValidatePayloadSize(RadioFrame frame, int expected, string block)
        {
            if (frame.Payload.Length != expected)
                throw new ProtocolException(
                    $"{block}: expected {expected}-byte payload, got {frame.Payload.Length} bytes " +
                    $"in frame CMD=0x{frame.Command:X2} SEQ={frame.Sequence}.");
        }

        private static void ValidateBufferSize(byte[] buf, int expected, string block)
        {
            if (buf is null)
                throw new ArgumentNullException(block);
            if (buf.Length != expected)
                throw new ArgumentException(
                    $"{block}: buffer must be exactly {expected} bytes, got {buf.Length}.");
        }

        private static void ValidatePacketArray(byte[][] packets, int count, int perPacket, string block)
        {
            if (packets is null)
                throw new ArgumentNullException(block);
            if (packets.Length != count)
                throw new ArgumentException(
                    $"{block}: expected {count} packets, got {packets.Length}.");
            for (int i = 0; i < count; i++)
            {
                if (packets[i] is null || packets[i].Length != perPacket)
                    throw new ArgumentException(
                        $"{block}[{i}]: each packet must be exactly {perPacket} bytes.");
            }
        }
    }
}
