// DataBlocksV2.cs
// Encode/decode all RT-5D data block payloads.
// All field layouts, offsets, sizes, and value enumerations derived from
// RT5D_Data_Structures.PDF (the authoritative reference).
//
// Sections referenced throughout:
//   §4  DTMF Block
//   §5  Encryption Key Block
//   §6  Address Book Block
//   §7  Receive Group List Block
//   §8  Channel Mode Block
//   §9  VFO Mode Block
//  §10  Optional Functions Block
//  §11  Basic Info Block

using System;
using System.Collections.Generic;
using System.Text;

namespace RT5D
{
    // ── Shared utilities ────────────────────────────────────────────────────────

    internal static class Codec
    {
        private static readonly Encoding Gb2312;

        static Codec()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Gb2312 = Encoding.GetEncoding("GB2312");
        }

        // Padding sentinel (§Document Conventions)
        public const byte Pad = 0xFF;

        /// <summary>
        /// Reads a GB2312 string from <paramref name="buf"/> at <paramref name="offset"/>
        /// up to <paramref name="maxBytes"/> bytes, stopping at 0x00 or 0xFF.
        /// </summary>
        public static string ReadGb2312(byte[] buf, int offset, int maxBytes)
        {
            int end = offset;
            int limit = Math.Min(offset + maxBytes, buf.Length);
            while (end < limit && buf[end] != 0x00 && buf[end] != 0xFF)
                end++;
            if (end == offset) return string.Empty;
            return Gb2312.GetString(buf, offset, end - offset);
        }

        /// <summary>
        /// Writes a GB2312 string into <paramref name="buf"/> at <paramref name="offset"/>,
        /// filling the remaining <paramref name="fieldLen"/> bytes with 0xFF.
        /// Truncates silently if encoded bytes exceed <paramref name="fieldLen"/>.
        /// </summary>
        public static void WriteGb2312(byte[] buf, int offset, int fieldLen, string value)
        {
            // Fill field with 0xFF first
            for (int i = offset; i < offset + fieldLen; i++)
                buf[i] = Pad;

            if (string.IsNullOrEmpty(value)) return;

            byte[] encoded = Gb2312.GetBytes(value);
            int copyLen = Math.Min(encoded.Length, fieldLen);
            Buffer.BlockCopy(encoded, 0, buf, offset, copyLen);
            // Null-terminate if room remains
            if (copyLen < fieldLen)
                buf[offset + copyLen] = 0x00;
        }

        /// <summary>Reads a uint32 little-endian from buf[offset..offset+3].</summary>
        public static uint ReadUInt32LE(byte[] buf, int offset) =>
            (uint)(buf[offset] | (buf[offset + 1] << 8) |
                   (buf[offset + 2] << 16) | (buf[offset + 3] << 24));

        /// <summary>Writes a uint32 little-endian into buf[offset..offset+3].</summary>
        public static void WriteUInt32LE(byte[] buf, int offset, uint value)
        {
            buf[offset]     = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8)  & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        /// <summary>Reads a uint16 little-endian.</summary>
        public static ushort ReadUInt16LE(byte[] buf, int offset) =>
            (ushort)(buf[offset] | (buf[offset + 1] << 8));

        /// <summary>Writes a uint16 little-endian.</summary>
        public static void WriteUInt16LE(byte[] buf, int offset, ushort value)
        {
            buf[offset]     = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        /// <summary>Returns low nibble of byte, clamped to [0, mod).</summary>
        public static int ReadNibble(byte b, int mod) => (b & 0x0F) % mod;

        /// <summary>Writes low nibble only; high nibble of existing byte is preserved.</summary>
        public static byte WriteNibble(byte existing, int value) =>
            (byte)((existing & 0xF0) | (value & 0x0F));

        /// <summary>Fills buf[offset..offset+count-1] with 0xFF.</summary>
        public static void PadFF(byte[] buf, int offset, int count)
        {
            for (int i = offset; i < offset + count; i++)
                buf[i] = Pad;
        }

        /// <summary>
        /// Decodes a nibble-indexed digit string using the provided alphabet.
        /// Each byte is a 0-based index; 0xFF = end of string.
        /// </summary>
        public static string ReadNibbleString(byte[] buf, int offset, int maxBytes, string alphabet)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = offset; i < offset + maxBytes; i++)
            {
                if (buf[i] == Pad) break;
                int idx = buf[i] & 0x0F;
                if (idx < alphabet.Length)
                    sb.Append(alphabet[idx]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Encodes a digit string using nibble indices.
        /// Field is 0xFF-padded to <paramref name="fieldLen"/> bytes.
        /// </summary>
        public static void WriteNibbleString(byte[] buf, int offset, int fieldLen,
                                              string value, string alphabet)
        {
            PadFF(buf, offset, fieldLen);
            if (string.IsNullOrEmpty(value)) return;
            int limit = Math.Min(value.Length, fieldLen);
            for (int i = 0; i < limit; i++)
            {
                int idx = alphabet.IndexOf(value[i]);
                buf[offset + i] = idx >= 0 ? (byte)idx : Pad;
            }
        }

        // Frequency helpers (§8.1 / §9.1)
        // Frequencies stored as uint32 LE in units of 10 Hz.
        public static double ReadFreqMHz(byte[] buf, int offset)
        {
            uint raw = ReadUInt32LE(buf, offset);
            return raw / 100_000.0;   // 10 Hz units → MHz
        }

        public static void WriteFreqMHz(byte[] buf, int offset, double mhz)
        {
            uint raw = (uint)Math.Round(mhz * 100_000.0);
            WriteUInt32LE(buf, offset, raw);
        }

        // DMR ID encoding (24-bit big-endian, bytes 2-4 of contact record)
        public static uint ReadDmrId(byte[] buf, int offset)
        {
            // offset = byte 2 (MSB), offset+1 = mid, offset+2 = LSB
            return (uint)((buf[offset] << 16) | (buf[offset + 1] << 8) | buf[offset + 2]);
        }

        public static void WriteDmrId(byte[] buf, int offset, uint id)
        {
            buf[offset]     = (byte)((id >> 16) & 0xFF);
            buf[offset + 1] = (byte)((id >> 8)  & 0xFF);
            buf[offset + 2] = (byte)(id & 0xFF);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // §4 — DTMF Block (272 bytes)
    // ═══════════════════════════════════════════════════════════════════════════

    // Alphabet for DTMF digits (§4.1)
    public static class DtmfAlphabet
    {
        public const string Chars = "0123456789ABCD*#";
    }

    public enum DtmfPttId   { Off = 0, Bot = 1, Eot = 2, Both = 3 }
    public enum DtmfDuration { Ms50=0, Ms100=1, Ms150=2, Ms200=3, Ms250=4 }
    public enum DtmfInterval { Ms50=0, Ms100=1, Ms150=2, Ms200=3, Ms250=4 }

    public sealed class DtmfBlock
    {
        public const int PayloadSize  = 272;
        public const int GlobalSize   = 32;
        public const int GroupCount   = 15;
        public const int GroupSize    = 16;
        public const int GroupCodeLen =  6;

        // §4.1 Global Settings
        /// <summary>Current DTMF ID — up to 5 digits from DtmfAlphabet.</summary>
        public string     CurrentId  { get; set; } = "";
        public DtmfPttId  PttId      { get; set; } = DtmfPttId.Off;
        public DtmfDuration Duration { get; set; } = DtmfDuration.Ms100;
        public DtmfInterval Interval { get; set; } = DtmfInterval.Ms100;

        // §4.2 Code Group List (up to 15 entries; null/empty = unused)
        public string?[] CodeGroups { get; set; } = new string?[GroupCount];

        // ── Decode ──────────────────────────────────────────────────────────────

        public static DtmfBlock Decode(byte[] buf)
        {
            if (buf.Length != PayloadSize)
                throw new ArgumentException($"DTMF payload must be {PayloadSize} bytes, got {buf.Length}.");

            var b = new DtmfBlock();

            // Bytes 0-4: CurrentId (5 nibble-indexed digits)
            b.CurrentId = Codec.ReadNibbleString(buf, 0, 5, DtmfAlphabet.Chars);

            // Byte 6: PttId (low nibble, mod 4)
            b.PttId     = (DtmfPttId)Codec.ReadNibble(buf[6], 4);

            // Byte 7: Duration (low nibble, mod 5)
            b.Duration  = (DtmfDuration)Codec.ReadNibble(buf[7], 5);

            // Byte 8: Interval (low nibble, mod 5)
            b.Interval  = (DtmfInterval)Codec.ReadNibble(buf[8], 5);

            // Bytes 32-271: 15 code groups × 16 bytes
            for (int g = 0; g < GroupCount; g++)
            {
                int gOffset = GlobalSize + g * GroupSize;
                // First byte 0xFF → empty entry
                if (buf[gOffset] == Codec.Pad)
                {
                    b.CodeGroups[g] = null;
                }
                else
                {
                    b.CodeGroups[g] = Codec.ReadNibbleString(buf, gOffset, GroupCodeLen, DtmfAlphabet.Chars);
                }
            }

            return b;
        }

        // ── Encode ──────────────────────────────────────────────────────────────

        public byte[] Encode()
        {
            byte[] buf = new byte[PayloadSize];
            Codec.PadFF(buf, 0, PayloadSize);

            Codec.WriteNibbleString(buf, 0, 5, CurrentId, DtmfAlphabet.Chars);
            // Byte 5 stays 0xFF (reserved)
            buf[6] = Codec.WriteNibble(buf[6], (int)PttId);
            buf[7] = Codec.WriteNibble(buf[7], (int)Duration);
            buf[8] = Codec.WriteNibble(buf[8], (int)Interval);
            // Bytes 9-31 stay 0xFF (reserved)

            for (int g = 0; g < GroupCount; g++)
            {
                int gOffset = GlobalSize + g * GroupSize;
                // Bytes gOffset+6 .. gOffset+15 stay 0xFF (already padded)
                string? code = CodeGroups[g];
                if (string.IsNullOrEmpty(code))
                {
                    // Leave first byte as 0xFF → empty entry
                }
                else
                {
                    Codec.WriteNibbleString(buf, gOffset, GroupCodeLen, code, DtmfAlphabet.Chars);
                }
            }

            return buf;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // §5 — Encryption Key Block (264 bytes)
    // ═══════════════════════════════════════════════════════════════════════════

    public enum EncAlgo { Arc4 = 0, Aes128 = 1, Aes256 = 2 }

    public sealed class EncKeyEntry
    {
        private static readonly string HexAlpha = "0123456789ABCDEF";

        public EncAlgo Algorithm { get; set; } = EncAlgo.Arc4;
        /// <summary>Hex digit string — 10, 32, or 64 chars depending on algorithm.</summary>
        public string Key        { get; set; } = "";

        // Key lengths in hex characters per algorithm (§5.1)
        private static int KeyCharLen(EncAlgo algo) => algo switch
        {
            EncAlgo.Arc4   => 10,
            EncAlgo.Aes128 => 32,
            EncAlgo.Aes256 => 64,
            _              => 10,
        };

        public static EncKeyEntry? Decode(byte[] buf, int offset)
        {
            // Entry is empty if bytes 0 and 1 are both 0xFF (§5)
            if (buf[offset] == Codec.Pad && buf[offset + 1] == Codec.Pad)
                return null;

            var e = new EncKeyEntry();
            e.Algorithm = (EncAlgo)(Codec.ReadNibble(buf[offset], 3));
            e.Key       = Codec.ReadNibbleString(buf, offset + 1, 32, HexAlpha);
            return e;
        }

        public void Encode(byte[] buf, int offset)
        {
            buf[offset] = Codec.WriteNibble(Codec.Pad, (int)Algorithm);

            // Pad key to required length with '0', truncate, write as nibble string
            int required = KeyCharLen(Algorithm);
            string padded = (Key + new string('0', required))
                            .Substring(0, required)
                            .ToUpperInvariant();

            Codec.PadFF(buf, offset + 1, 32);
            Codec.WriteNibbleString(buf, offset + 1, required, padded, HexAlpha);
        }
    }

    public sealed class EncKeyBlock
    {
        public const int PayloadSize = 264;
        public const int EntryCount  = 8;
        public const int EntrySize   = 33;

        public EncKeyEntry?[] Entries { get; set; } = new EncKeyEntry?[EntryCount];

        public static EncKeyBlock Decode(byte[] buf)
        {
            if (buf.Length != PayloadSize)
                throw new ArgumentException($"EncKey payload must be {PayloadSize} bytes, got {buf.Length}.");
            var b = new EncKeyBlock();
            for (int i = 0; i < EntryCount; i++)
                b.Entries[i] = EncKeyEntry.Decode(buf, i * EntrySize);
            return b;
        }

        public byte[] Encode()
        {
            byte[] buf = new byte[PayloadSize];
            Codec.PadFF(buf, 0, PayloadSize);
            for (int i = 0; i < EntryCount; i++)
            {
                int offset = i * EntrySize;
                if (Entries[i] is not null)
                    Entries[i]!.Encode(buf, offset);
                // else: both bytes 0 and 1 remain 0xFF → empty entry marker
            }
            return buf;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // §6 — Address Book Contact Record (16 bytes)
    // ═══════════════════════════════════════════════════════════════════════════

    public enum CallType { Group = 0, Private = 1, AllCall = 2 }

    public sealed class Contact
    {
        public const int RecordSize   = 16;
        public const int NameOffset   =  5;
        public const int NameLen      = 10;
        public const uint MinId       = 1;
        public const uint MaxId       = 16_777_215;  // 0xFFFFFF

        public CallType CallType { get; set; } = CallType.Private;
        public uint     CallId   { get; set; }
        public string   Name     { get; set; } = "";

        /// <summary>
        /// Decodes one contact record from buf[offset..offset+15].
        /// Returns null for empty entries (per §6: byte 0, 1, or 5 == 0xFF).
        /// </summary>
        public static Contact? Decode(byte[] buf, int offset)
        {
            if (buf[offset] == Codec.Pad || buf[offset + 1] == Codec.Pad || buf[offset + 5] == Codec.Pad)
                return null;

            var c = new Contact();
            c.CallType = (CallType)Codec.ReadNibble(buf[offset], 3);
            // byte offset+1 is always 0x00 for valid entries
            c.CallId   = Codec.ReadDmrId(buf, offset + 2);   // bytes 2,3,4
            c.Name     = Codec.ReadGb2312(buf, offset + 5, NameLen);
            return c;
        }

        /// <summary>Encodes this contact into buf[offset..offset+15].</summary>
        public void Encode(byte[] buf, int offset)
        {
            Codec.PadFF(buf, offset, RecordSize);
            buf[offset]     = Codec.WriteNibble(0, (int)CallType);
            buf[offset + 1] = 0x00;
            Codec.WriteDmrId(buf, offset + 2, CallId);
            Codec.WriteGb2312(buf, offset + 5, NameLen, Name);
            buf[offset + 15] = Codec.Pad;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // §7 — Receive Group List Entry (128 bytes per entry)
    // ═══════════════════════════════════════════════════════════════════════════

    public sealed class RxGroup
    {
        public const int EntrySize       = 128;
        public const int MemberListBytes =  96;
        public const int MaxMembers      =  32;  // 96 / 3
        public const int NameOffset      =  96;
        public const int NameLen         =  12;

        public string Name    { get; set; } = "";
        public uint[] Members { get; set; } = Array.Empty<uint>();

        /// <summary>Returns null for empty entries (byte 96 == 0xFF).</summary>
        public static RxGroup? Decode(byte[] buf, int offset)
        {
            if (buf[offset + NameOffset] == Codec.Pad)
                return null;

            var g = new RxGroup();
            g.Name = Codec.ReadGb2312(buf, offset + NameOffset, NameLen);

            // Member IDs — 3 bytes each, big-endian. All-zero = end of list.
            var members = new List<uint>();
            for (int m = 0; m < MaxMembers; m++)
            {
                int mOff = offset + m * 3;
                uint id = Codec.ReadDmrId(buf, mOff);
                if (id == 0) break;
                members.Add(id);
            }
            g.Members = members.ToArray();
            return g;
        }

        public void Encode(byte[] buf, int offset)
        {
            Codec.PadFF(buf, offset, EntrySize);
            Codec.WriteGb2312(buf, offset + NameOffset, NameLen, Name);
            int limit = Math.Min(Members.Length, MaxMembers);
            for (int m = 0; m < limit; m++)
                Codec.WriteDmrId(buf, offset + m * 3, Members[m]);
            // Write a zero-terminator slot after the last member (§7.1).
            // The decoder stops at the first all-zero 3-byte slot; slots left
            // as 0xFF would be read as ID 16,777,215 (valid DMR ID range).
            if (limit < MaxMembers)
            {
                int termOff = offset + limit * 3;
                buf[termOff]     = 0x00;
                buf[termOff + 1] = 0x00;
                buf[termOff + 2] = 0x00;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // §8 — Channel Record (64 bytes)
    // ═══════════════════════════════════════════════════════════════════════════

    public enum TxPower    { Low = 0, Medium = 1, High = 2 }
    public enum ChPttId    { Off = 0, Bot = 1, Eot = 2, Both = 3 }
    public enum Encryption { None = 0, Basic = 1, Enhanced = 2, Aes = 3 }

    /// <summary>
    /// Combined channel type decoded from bytes 14 and 15 of a channel/VFO record (§8.4).
    /// byte14=1 means Analog FM; byte14=0 means Digital DMR.
    /// When digital: byte15=0 → Tier I (simplex), byte15=1 → Tier II (repeater).
    /// </summary>
    public enum ChannelType
    {
        AnalogFm      = 0,  // byte14=1, byte15=0   (1=Analog flag set)
        DmrTierI      = 1,  // byte14=0, byte15=0   (digital, simplex)
        DmrTierII     = 2,  // byte14=0, byte15=1   (digital, repeater)
    }

    public sealed class Channel
    {
        public const int RecordSize     = 64;
        public const int NameOffset     = 32;
        public const int NameLen        = 12;
        public const int ContactOffset  = 44;
        public const int FhssOffset     = 28;

        // Frequencies in MHz
        public double   RxFreqMHz     { get; set; }
        public double   TxFreqMHz     { get; set; }

        public SubAudio RxSubAudio    { get; set; } = SubAudio.Off();
        public SubAudio TxSubAudio    { get; set; } = SubAudio.Off();

        public int      SignalingCode  { get; set; }  // 0-14, 0=none
        public ChPttId  PttId         { get; set; }
        public ChannelType Type       { get; set; }
        public TxPower  Power         { get; set; }
        public int      Scramble      { get; set; }  // 0-8
        public Encryption EncMode     { get; set; }
        public bool     BusyLockout   { get; set; }
        public bool     ScanAdd       { get; set; }
        public int      TimeSlot      { get; set; }  // 0=TS1, 1=TS2
        public int      ColorCode     { get; set; }  // 0-15
        public int      RxGroupIndex  { get; set; }  // 0=none, 1-32
        public int      EncKeyIndex   { get; set; }  // 0-7
        public bool     DmrRepeater   { get; set; }  // false=simplex, true=repeater
        public bool     LearnFhss     { get; set; }
        public string?  FhssCode      { get; set; }  // 6 hex chars, or null
        public string   Name          { get; set; } = "";
        public ushort   ContactIndex  { get; set; }  // 0=none

        /// <summary>Returns null for empty records (first 4 bytes all 0xFF or all 0x00).</summary>
        public static Channel? Decode(byte[] buf, int offset)
        {
            // Empty channel check (§8)
            bool allFF = buf[offset]==0xFF && buf[offset+1]==0xFF && buf[offset+2]==0xFF && buf[offset+3]==0xFF;
            bool allZ  = buf[offset]==0x00 && buf[offset+1]==0x00 && buf[offset+2]==0x00 && buf[offset+3]==0x00;
            if (allFF || allZ) return null;

            var c = new Channel();
            c.RxFreqMHz   = Codec.ReadFreqMHz(buf, offset);
            c.TxFreqMHz   = Codec.ReadFreqMHz(buf, offset + 4);
            c.RxSubAudio  = SubAudio.Decode(buf, offset + 8);
            c.TxSubAudio  = SubAudio.Decode(buf, offset + 10);
            c.SignalingCode = Codec.ReadNibble(buf[offset + 12], 15);
            c.PttId        = (ChPttId)Codec.ReadNibble(buf[offset + 13], 4);

            // Channel type decoded from bytes 14 and 15 (§8.4):
            //   byte14=1 → Analog FM  (byte15 ignored for type purposes)
            //   byte14=0, byte15=0 → Digital DMR Tier I  (simplex)
            //   byte14=0, byte15=1 → Digital DMR Tier II (repeater)
            int f14 = Codec.ReadNibble(buf[offset + 14], 2);
            int f15 = Codec.ReadNibble(buf[offset + 15], 2);
            c.Type = f14 == 1
                ? ChannelType.AnalogFm
                : (f15 == 1 ? ChannelType.DmrTierII : ChannelType.DmrTierI);

            c.Power        = (TxPower)Codec.ReadNibble(buf[offset + 16], 3);
            c.Scramble     = Codec.ReadNibble(buf[offset + 17], 9);
            c.EncMode      = (Encryption)Codec.ReadNibble(buf[offset + 18], 4);
            c.BusyLockout  = Codec.ReadNibble(buf[offset + 19], 2) == 1;
            c.ScanAdd      = Codec.ReadNibble(buf[offset + 20], 2) == 1;
            c.TimeSlot     = Codec.ReadNibble(buf[offset + 21], 2);
            c.ColorCode    = Codec.ReadNibble(buf[offset + 22], 16);
            c.RxGroupIndex = buf[offset + 23] % 33;
            // byte 24 reserved
            c.EncKeyIndex  = Codec.ReadNibble(buf[offset + 25], 8);
            c.DmrRepeater  = Codec.ReadNibble(buf[offset + 26], 2) == 1;
            c.LearnFhss    = Codec.ReadNibble(buf[offset + 27], 2) == 1;

            // FHSS code (§8.3) — byte 31 = 0x00 means valid
            if (buf[offset + FhssOffset + 3] == 0x00)
                c.FhssCode = DecodeFhss(buf, offset + FhssOffset);
            else
                c.FhssCode = null;

            c.Name         = Codec.ReadGb2312(buf, offset + NameOffset, NameLen);
            c.ContactIndex = Codec.ReadUInt16LE(buf, offset + ContactOffset);

            return c;
        }

        public void Encode(byte[] buf, int offset)
        {
            Codec.PadFF(buf, offset, RecordSize);

            Codec.WriteFreqMHz(buf, offset,     RxFreqMHz);
            Codec.WriteFreqMHz(buf, offset + 4, TxFreqMHz);
            RxSubAudio.Encode(buf, offset + 8);
            TxSubAudio.Encode(buf, offset + 10);

            buf[offset + 12] = Codec.WriteNibble(0, SignalingCode);
            buf[offset + 13] = Codec.WriteNibble(0, (int)PttId);

            // ChannelType → bytes 14/15 (§8.4):
            //   AnalogFm  → byte14=1, byte15=0
            //   DmrTierI  → byte14=0, byte15=0
            //   DmrTierII → byte14=0, byte15=1
            (int f14, int f15) = Type switch
            {
                ChannelType.AnalogFm  => (1, 0),
                ChannelType.DmrTierI  => (0, 0),
                ChannelType.DmrTierII => (0, 1),
                _                     => (1, 0),
            };
            buf[offset + 14] = Codec.WriteNibble(0, f14);
            buf[offset + 15] = Codec.WriteNibble(0, f15);

            buf[offset + 16] = Codec.WriteNibble(0, (int)Power);
            buf[offset + 17] = Codec.WriteNibble(0, Scramble);
            buf[offset + 18] = Codec.WriteNibble(0, (int)EncMode);
            buf[offset + 19] = Codec.WriteNibble(0, BusyLockout ? 1 : 0);
            buf[offset + 20] = Codec.WriteNibble(0, ScanAdd     ? 1 : 0);
            buf[offset + 21] = Codec.WriteNibble(0, TimeSlot);
            buf[offset + 22] = Codec.WriteNibble(0, ColorCode);
            buf[offset + 23] = (byte)(RxGroupIndex % 33);
            buf[offset + 24] = Codec.Pad;
            buf[offset + 25] = Codec.WriteNibble(0, EncKeyIndex);
            buf[offset + 26] = Codec.WriteNibble(0, DmrRepeater ? 1 : 0);
            buf[offset + 27] = Codec.WriteNibble(0, LearnFhss   ? 1 : 0);

            // FHSS
            if (!string.IsNullOrEmpty(FhssCode))
                EncodeFhss(buf, offset + FhssOffset, FhssCode);
            else
                buf[offset + FhssOffset + 3] = Codec.Pad; // validity flag = 0xFF = unused

            Codec.WriteGb2312(buf, offset + NameOffset, NameLen, Name);
            Codec.WriteUInt16LE(buf, offset + ContactOffset, ContactIndex);
            // bytes 46-63 stay 0xFF (reserved)
        }

        // FHSS encoding (§8.3):
        // 6 hex chars in "reversed nibble order":
        //   byte30 (MSB) = digit0 | (digit1 << 4)  → high nibble = digit0, low = digit1
        //   byte29 (mid) = digit2 | (digit3 << 4)
        //   byte28 (LSB) = digit4 | (digit5 << 4)
        //   byte31 = 0x00 (valid flag)
        private static string DecodeFhss(byte[] buf, int offset)
        {
            byte lsb = buf[offset];      // byte28: digits 4,5
            byte mid = buf[offset + 1];  // byte29: digits 2,3
            byte msb = buf[offset + 2];  // byte30: digits 0,1

            return $"{(msb >> 4) & 0xF:X}{msb & 0xF:X}" +
                   $"{(mid >> 4) & 0xF:X}{mid & 0xF:X}" +
                   $"{(lsb >> 4) & 0xF:X}{lsb & 0xF:X}";
        }

        private static void EncodeFhss(byte[] buf, int offset, string code)
        {
            code = code.ToUpperInvariant().PadRight(6, '0');
            int d0 = HexVal(code[0]), d1 = HexVal(code[1]);
            int d2 = HexVal(code[2]), d3 = HexVal(code[3]);
            int d4 = HexVal(code[4]), d5 = HexVal(code[5]);

            buf[offset]     = (byte)((d4 << 4) | d5);  // byte28 LSB
            buf[offset + 1] = (byte)((d2 << 4) | d3);  // byte29 mid
            buf[offset + 2] = (byte)((d0 << 4) | d1);  // byte30 MSB
            buf[offset + 3] = 0x00;                      // byte31 valid flag
        }

        private static int HexVal(char c) =>
            c >= '0' && c <= '9' ? c - '0' :
            c >= 'A' && c <= 'F' ? c - 'A' + 10 : 0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // §9 — VFO Bank (64 bytes per bank, 2 banks = 128 bytes total)
    // ═══════════════════════════════════════════════════════════════════════════

    public enum StepFreq
    {
        kHz2_5 = 0, kHz5 = 1, kHz6_25 = 2, kHz10 = 3,
        kHz12_5 = 4, kHz20 = 5, kHz25 = 6, kHz50 = 7
    }

    public sealed class VfoBank
    {
        public const int BankSize      = 64;
        public const int ContactOffset = 32;

        // Default frequencies (§9)
        public const double DefaultFreqA = 136.125;
        public const double DefaultFreqB = 400.125;

        public double    RxFreqMHz    { get; set; }
        public double    TxFreqMHz    { get; set; }
        public SubAudio  RxSubAudio   { get; set; } = SubAudio.Off();
        public SubAudio  TxSubAudio   { get; set; } = SubAudio.Off();
        public int       SignalingCode { get; set; }
        public ChannelType Type       { get; set; }
        public TxPower   Power        { get; set; }
        public int       Scramble     { get; set; }
        public Encryption EncMode     { get; set; }
        public bool      BusyLockout  { get; set; }
        public int       TimeSlot     { get; set; }
        public int       ColorCode    { get; set; }
        public int       RxGroupIndex { get; set; }
        public int       EncKeyIndex  { get; set; }
        public bool      DmrRepeater  { get; set; }
        public StepFreq  Step         { get; set; }
        public ushort    ContactIndex { get; set; }

        public static VfoBank Decode(byte[] buf, int offset, double defaultFreq)
        {
            var v = new VfoBank();

            uint rxRaw = Codec.ReadUInt32LE(buf, offset);
            uint txRaw = Codec.ReadUInt32LE(buf, offset + 4);

            // Substitute default if all 0x00 or all 0xFF (§9)
            v.RxFreqMHz = (rxRaw == 0 || rxRaw == 0xFFFFFFFF)
                          ? defaultFreq
                          : rxRaw / 100_000.0;
            v.TxFreqMHz = (txRaw == 0 || txRaw == 0xFFFFFFFF)
                          ? defaultFreq
                          : txRaw / 100_000.0;

            v.RxSubAudio   = SubAudio.Decode(buf, offset + 8);
            v.TxSubAudio   = SubAudio.Decode(buf, offset + 10);
            v.SignalingCode = Codec.ReadNibble(buf[offset + 12], 15);

            int f14 = Codec.ReadNibble(buf[offset + 14], 2);
            int f15 = Codec.ReadNibble(buf[offset + 15], 2);
            v.Type = f14 == 1
                ? ChannelType.AnalogFm
                : (f15 == 1 ? ChannelType.DmrTierII : ChannelType.DmrTierI);

            v.Power        = (TxPower)Codec.ReadNibble(buf[offset + 16], 3);
            v.Scramble     = Codec.ReadNibble(buf[offset + 17], 9);
            v.EncMode      = (Encryption)Codec.ReadNibble(buf[offset + 18], 4);
            v.BusyLockout  = Codec.ReadNibble(buf[offset + 19], 2) == 1;
            // byte 20 unused in VFO
            v.TimeSlot     = Codec.ReadNibble(buf[offset + 21], 2);
            v.ColorCode    = Codec.ReadNibble(buf[offset + 22], 16);
            v.RxGroupIndex = buf[offset + 23] % 33;
            v.EncKeyIndex  = Codec.ReadNibble(buf[offset + 25], 8);
            v.DmrRepeater  = Codec.ReadNibble(buf[offset + 26], 2) == 1;
            v.Step         = (StepFreq)Codec.ReadNibble(buf[offset + 27], 8);
            v.ContactIndex = Codec.ReadUInt16LE(buf, offset + ContactOffset);

            return v;
        }

        public void Encode(byte[] buf, int offset)
        {
            Codec.PadFF(buf, offset, BankSize);
            Codec.WriteFreqMHz(buf, offset,     RxFreqMHz);
            Codec.WriteFreqMHz(buf, offset + 4, TxFreqMHz);
            RxSubAudio.Encode(buf, offset + 8);
            TxSubAudio.Encode(buf, offset + 10);
            buf[offset + 12] = Codec.WriteNibble(0, SignalingCode);
            buf[offset + 13] = Codec.Pad; // ChPttId unused in VFO

            (int f14, int f15) = Type switch
            {
                ChannelType.AnalogFm  => (1, 0),
                ChannelType.DmrTierI  => (0, 0),
                ChannelType.DmrTierII => (0, 1),
                _                     => (1, 0),
            };
            buf[offset + 14] = Codec.WriteNibble(0, f14);
            buf[offset + 15] = Codec.WriteNibble(0, f15);
            buf[offset + 16] = Codec.WriteNibble(0, (int)Power);
            buf[offset + 17] = Codec.WriteNibble(0, Scramble);
            buf[offset + 18] = Codec.WriteNibble(0, (int)EncMode);
            buf[offset + 19] = Codec.WriteNibble(0, BusyLockout ? 1 : 0);
            buf[offset + 20] = Codec.Pad; // ScanAdd unused in VFO
            buf[offset + 21] = Codec.WriteNibble(0, TimeSlot);
            buf[offset + 22] = Codec.WriteNibble(0, ColorCode);
            buf[offset + 23] = (byte)(RxGroupIndex % 33);
            buf[offset + 24] = Codec.Pad;
            buf[offset + 25] = Codec.WriteNibble(0, EncKeyIndex);
            buf[offset + 26] = Codec.WriteNibble(0, DmrRepeater ? 1 : 0);
            buf[offset + 27] = Codec.WriteNibble(0, (int)Step);
            // bytes 28-31: FHSS not applicable in VFO (stay 0xFF)
            Codec.WriteUInt16LE(buf, offset + ContactOffset, ContactIndex);
            // bytes 34-63: reserved (0xFF already)
        }
    }

    public sealed class VfoBlock
    {
        public VfoBank BankA { get; set; } = new VfoBank { RxFreqMHz = VfoBank.DefaultFreqA, TxFreqMHz = VfoBank.DefaultFreqA };
        public VfoBank BankB { get; set; } = new VfoBank { RxFreqMHz = VfoBank.DefaultFreqB, TxFreqMHz = VfoBank.DefaultFreqB };

        public static VfoBlock Decode(byte[] buf)
        {
            if (buf.Length != Sizes.Vfo)
                throw new ArgumentException($"VFO payload must be {Sizes.Vfo} bytes, got {buf.Length}.");
            return new VfoBlock
            {
                BankA = VfoBank.Decode(buf, 0,  VfoBank.DefaultFreqA),
                BankB = VfoBank.Decode(buf, 64, VfoBank.DefaultFreqB),
            };
        }

        public byte[] Encode()
        {
            byte[] buf = new byte[Sizes.Vfo];
            Codec.PadFF(buf, 0, Sizes.Vfo);
            BankA.Encode(buf, 0);
            BankB.Encode(buf, 64);
            return buf;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // §10 — Optional Functions Block (64 bytes)
    // ═══════════════════════════════════════════════════════════════════════════

    public enum ScanMode    { TimeOperated=0, CarrierOperated=1, Search=2 }
    public enum SosMode     { OnSite=0, SendSound=1, SendCode=2 }
    public enum SideTone    { Off=0, DtSt=1, AniSt=2, DtAndAni=3 }
    public enum RTone       { Hz1000=0, Hz1450=1, Hz1750=2, Hz2100=3 }
    public enum DisplayMode { Name=0, Freq=1, Channel=2 }
    public enum SteFreq     { Hz55=0, Hz62_5=1 }
    public enum KeyFunc     { Radio=0, Moni=1, Scan=2, Search=3, Sos=4, Noaa=5, ScanQt=6, PttB=7 }

    public sealed class OptFunBlock
    {
        // ── Part 1 (bytes 0-31) ─────────────────────────────────────────────────
        public int         AnalogSql     { get; set; } = 3;   // 0-9
        public int         PowerSave     { get; set; } = 1;   // 0-4
        public int         Vox           { get; set; } = 0;   // 0-9
        public int         AutoBacklight { get; set; } = 5;   // 0-8
        public bool        Tdr           { get; set; }
        public int         Tot           { get; set; }        // 0-5
        public bool        Beep          { get; set; } = true;
        public bool        Voice         { get; set; } = true;
        public bool        ChineseLang   { get; set; }
        public SideTone    SideTone      { get; set; }
        public ScanMode    ScanMode      { get; set; } = ScanMode.CarrierOperated;
        public ChPttId     GlobalPttId   { get; set; }
        public int         IdDelayTime   { get; set; } = 4;   // 0-6
        public DisplayMode DisplayA      { get; set; }
        public DisplayMode DisplayB      { get; set; }
        public int         AutoLock      { get; set; }        // 0-3
        public SosMode     SosMode       { get; set; }
        public bool        AlarmSound    { get; set; } = true;
        public int         TdrTxPriority { get; set; }        // 0-2
        public bool        TailClear     { get; set; } = true;
        public int         RptClearTail  { get; set; }        // 0-10
        public int         RptDetectTail { get; set; }        // 0-10
        public bool        TxOverSound   { get; set; }
        public bool        WorkBandIsB   { get; set; }
        public bool        FmRadioOff    { get; set; }        // 0=ON allowed, 1=OFF disabled
        public bool        WorkModeACh   { get; set; }        // false=VFO, true=Channel
        public bool        WorkModeBCh   { get; set; }
        public bool        KeyLock       { get; set; }
        public bool        BootVoltage   { get; set; }        // false=Logo, true=Voltage
        public RTone       RTone         { get; set; } = RTone.Hz1750;
        public bool        TxStartSound  { get; set; }

        // ── Part 2 (bytes 32-63) ────────────────────────────────────────────────
        public int      VoxDelay      { get; set; } = 5;   // 0-15
        public int      MenuAutoQuit  { get; set; } = 1;   // 0-10
        public int      DigitalSql    { get; set; } = 3;   // 0-9
        public SteFreq  SteFreq       { get; set; }
        public int      WeatherCh     { get; set; }        // 0-9
        public KeyFunc  TopKey1S      { get; set; }
        public KeyFunc  SideKey2S     { get; set; } = KeyFunc.Moni;
        public KeyFunc  SideKey2L     { get; set; } = KeyFunc.Scan;
        public KeyFunc  SideKey3S     { get; set; } = KeyFunc.Search;
        public KeyFunc  SideKey3L     { get; set; } = KeyFunc.Sos;
        public int      KeepCallTime  { get; set; } = 4;  // 0-19
        public int      TdrRecovery   { get; set; } = 3;  // 0-10

        public static OptFunBlock Decode(byte[] buf)
        {
            if (buf.Length != Sizes.OptFun)
                throw new ArgumentException($"OptFun payload must be {Sizes.OptFun} bytes.");
            var o = new OptFunBlock();

            // Part 1
            o.AnalogSql     = Codec.ReadNibble(buf[0],  10);
            o.PowerSave     = Codec.ReadNibble(buf[1],   5);
            o.Vox           = Codec.ReadNibble(buf[2],  10);
            o.AutoBacklight = Codec.ReadNibble(buf[3],   9);
            o.Tdr           = Codec.ReadNibble(buf[4],   2) == 1;
            o.Tot           = Codec.ReadNibble(buf[5],   6);
            o.Beep          = Codec.ReadNibble(buf[6],   2) == 1;
            o.Voice         = Codec.ReadNibble(buf[7],   2) == 1;
            o.ChineseLang   = Codec.ReadNibble(buf[8],   2) == 1;
            o.SideTone      = (SideTone)Codec.ReadNibble(buf[9],  4);
            o.ScanMode      = (ScanMode)Codec.ReadNibble(buf[10], 3);
            o.GlobalPttId   = (ChPttId) Codec.ReadNibble(buf[11], 4);
            o.IdDelayTime   = Codec.ReadNibble(buf[12], 7);
            o.DisplayA      = (DisplayMode)Codec.ReadNibble(buf[13], 3);
            o.DisplayB      = (DisplayMode)Codec.ReadNibble(buf[14], 3);
            // byte 15 reserved
            o.AutoLock      = Codec.ReadNibble(buf[16], 4);
            o.SosMode       = (SosMode)Codec.ReadNibble(buf[17], 3);
            o.AlarmSound    = Codec.ReadNibble(buf[18], 2) == 1;
            o.TdrTxPriority = Codec.ReadNibble(buf[19], 3);
            o.TailClear     = Codec.ReadNibble(buf[20], 2) == 1;
            o.RptClearTail  = Codec.ReadNibble(buf[21], 11);
            o.RptDetectTail = Codec.ReadNibble(buf[22], 11);
            o.TxOverSound   = Codec.ReadNibble(buf[23], 2) == 1;
            o.WorkBandIsB   = Codec.ReadNibble(buf[24], 2) == 1;
            o.FmRadioOff    = Codec.ReadNibble(buf[25], 2) == 1;
            int wm          = buf[26];
            o.WorkModeACh   = (wm & 0x0F) == 1;
            o.WorkModeBCh   = ((wm >> 4) & 0x0F) == 1;
            o.KeyLock       = Codec.ReadNibble(buf[27], 2) == 1;
            o.BootVoltage   = Codec.ReadNibble(buf[28], 2) == 1;
            // byte 29 reserved
            o.RTone         = (RTone)Codec.ReadNibble(buf[30], 4);
            o.TxStartSound  = Codec.ReadNibble(buf[31], 2) == 1;

            // Part 2
            o.VoxDelay     = Codec.ReadNibble(buf[32], 16);
            o.MenuAutoQuit = Codec.ReadNibble(buf[33], 11);
            o.DigitalSql   = Codec.ReadNibble(buf[34],  10);
            // bytes 35-38 reserved
            o.SteFreq      = (SteFreq)Codec.ReadNibble(buf[39], 2);
            o.WeatherCh    = Codec.ReadNibble(buf[40], 10);
            // bytes 41-42 reserved
            o.TopKey1S     = (KeyFunc)Codec.ReadNibble(buf[43], 7);
            o.SideKey2S    = (KeyFunc)Codec.ReadNibble(buf[44], 8);
            o.SideKey2L    = (KeyFunc)Codec.ReadNibble(buf[45], 7);
            o.SideKey3S    = (KeyFunc)Codec.ReadNibble(buf[46], 7);
            o.SideKey3L    = (KeyFunc)Codec.ReadNibble(buf[47], 7);
            // bytes 48-49 reserved
            o.KeepCallTime = buf[50] & 0x1F;  // bits 4:0
            // byte 51 reserved
            o.TdrRecovery  = Codec.ReadNibble(buf[52], 11);
            // bytes 53-63 reserved

            return o;
        }

        public byte[] Encode()
        {
            byte[] b = new byte[Sizes.OptFun];
            Codec.PadFF(b, 0, Sizes.OptFun);

            b[0]  = Codec.WriteNibble(0, AnalogSql);
            b[1]  = Codec.WriteNibble(0, PowerSave);
            b[2]  = Codec.WriteNibble(0, Vox);
            b[3]  = Codec.WriteNibble(0, AutoBacklight);
            b[4]  = Codec.WriteNibble(0, Tdr ? 1 : 0);
            b[5]  = Codec.WriteNibble(0, Tot);
            b[6]  = Codec.WriteNibble(0, Beep        ? 1 : 0);
            b[7]  = Codec.WriteNibble(0, Voice       ? 1 : 0);
            b[8]  = Codec.WriteNibble(0, ChineseLang ? 1 : 0);
            b[9]  = Codec.WriteNibble(0, (int)SideTone);
            b[10] = Codec.WriteNibble(0, (int)ScanMode);
            b[11] = Codec.WriteNibble(0, (int)GlobalPttId);
            b[12] = Codec.WriteNibble(0, IdDelayTime);
            b[13] = Codec.WriteNibble(0, (int)DisplayA);
            b[14] = Codec.WriteNibble(0, (int)DisplayB);
            b[15] = Codec.Pad;
            b[16] = Codec.WriteNibble(0, AutoLock);
            b[17] = Codec.WriteNibble(0, (int)SosMode);
            b[18] = Codec.WriteNibble(0, AlarmSound    ? 1 : 0);
            b[19] = Codec.WriteNibble(0, TdrTxPriority);
            b[20] = Codec.WriteNibble(0, TailClear     ? 1 : 0);
            b[21] = Codec.WriteNibble(0, RptClearTail);
            b[22] = Codec.WriteNibble(0, RptDetectTail);
            b[23] = Codec.WriteNibble(0, TxOverSound   ? 1 : 0);
            b[24] = Codec.WriteNibble(0, WorkBandIsB   ? 1 : 0);
            b[25] = Codec.WriteNibble(0, FmRadioOff    ? 1 : 0);
            b[26] = (byte)(((WorkModeBCh ? 1 : 0) << 4) | (WorkModeACh ? 1 : 0));
            b[27] = Codec.WriteNibble(0, KeyLock       ? 1 : 0);
            b[28] = Codec.WriteNibble(0, BootVoltage   ? 1 : 0);
            b[29] = Codec.Pad;
            b[30] = Codec.WriteNibble(0, (int)RTone);
            b[31] = Codec.WriteNibble(0, TxStartSound  ? 1 : 0);

            b[32] = Codec.WriteNibble(0, VoxDelay);
            b[33] = Codec.WriteNibble(0, MenuAutoQuit);
            b[34] = Codec.WriteNibble(0, DigitalSql);
            // bytes 35-38 stay 0xFF
            b[39] = Codec.WriteNibble(0, (int)SteFreq);
            b[40] = Codec.WriteNibble(0, WeatherCh);
            // bytes 41-42 stay 0xFF
            b[43] = Codec.WriteNibble(0, (int)TopKey1S);
            b[44] = Codec.WriteNibble(0, (int)SideKey2S);
            b[45] = Codec.WriteNibble(0, (int)SideKey2L);
            b[46] = Codec.WriteNibble(0, (int)SideKey3S);
            b[47] = Codec.WriteNibble(0, (int)SideKey3L);
            // bytes 48-49 stay 0xFF
            b[50] = (byte)(KeepCallTime & 0x1F);
            // byte 51 stays 0xFF
            b[52] = Codec.WriteNibble(0, TdrRecovery);
            // bytes 53-63 stay 0xFF

            return b;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // §11 — Basic Info Block (64 bytes)
    // ═══════════════════════════════════════════════════════════════════════════

    public sealed class BasicInfoBlock
    {
        public const int ModelNameOffset = 8;
        public const int ModelNameLen    = 12;
        public const int ModelIdOffset   = 20;
        public const int ModelIdLen      = 8;

        public string ModelName { get; set; } = "DMR";
        public int    ModelId   { get; set; } = 1;

        public static BasicInfoBlock Decode(byte[] buf)
        {
            if (buf.Length != Sizes.BasicInfo)
                throw new ArgumentException($"BasicInfo payload must be {Sizes.BasicInfo} bytes.");
            var b = new BasicInfoBlock();
            b.ModelName = Codec.ReadGb2312(buf, ModelNameOffset, ModelNameLen);
            string idStr = System.Text.Encoding.ASCII.GetString(buf, ModelIdOffset, ModelIdLen).Trim();
            b.ModelId   = int.TryParse(idStr, out int id) ? id : 1;
            return b;
        }

        public byte[] Encode()
        {
            byte[] buf = new byte[Sizes.BasicInfo];
            Codec.PadFF(buf, 0, Sizes.BasicInfo);
            // bytes 0-7 stay 0xFF (reserved prefix)
            Codec.WriteGb2312(buf, ModelNameOffset, ModelNameLen, ModelName);
            string idStr = ModelId.ToString().PadLeft(ModelIdLen, '0');
            byte[] idBytes = System.Text.Encoding.ASCII.GetBytes(idStr);
            Buffer.BlockCopy(idBytes, 0, buf, ModelIdOffset, Math.Min(idBytes.Length, ModelIdLen));
            // bytes 28-63 stay 0xFF (reserved suffix)
            return buf;
        }
    }
}
