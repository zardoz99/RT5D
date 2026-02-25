// Tests.cs
// Unit tests for all RT-5D encoders, decoders, CRC, and frame building.
// Run from CLI: dotnet test  (or invoke RunAll() from the main program).
//
// Test vectors derived directly from the spec documents:
//   RT5D_Protocol_Analysis.PDF §3.2, §6.x, §11
//   RT5D_Data_Structures.PDF §1-§12

using System;
using System.Collections.Generic;
using System.Linq;

namespace RT5D
{
    public static class Tests
    {
        private static int _pass, _fail;
        private static readonly List<string> _failures = new();

        // ── Test runner ──────────────────────────────────────────────────────────

        public static bool RunAll()
        {
            _pass = 0; _fail = 0; _failures.Clear();

            Console.WriteLine("═══════════════════════════════════════════════");
            Console.WriteLine("  RT-5D Unit Tests");
            Console.WriteLine("═══════════════════════════════════════════════");

            // Layer 1 — CRC
            T_Crc_KnownVector();
            T_Crc_HandshakeFrame();
            T_Crc_PasswordFrame();

            // Layer 2 — Frame building
            T_Frame_HandshakeBytes();
            T_Frame_PasswordBytes();
            T_Frame_ChannelWriteHeader();

            // Layer 3 — SubAudio
            T_SubAudio_Off();
            T_SubAudio_Ctcss_88_5();
            T_SubAudio_Ctcss_67_0();
            T_SubAudio_Dcs_D023N();
            T_SubAudio_Dcs_D023I();
            T_SubAudio_RoundTrip_Ctcss();
            T_SubAudio_RoundTrip_Dcs();

            // Layer 4 — FHSS
            T_Fhss_Encode_1A2B3C();
            T_Fhss_ValidFlag_Unused();

            // Layer 5 — Frequency
            T_Freq_145_5_MHz();
            T_Freq_146_520_MHz();
            T_Freq_400_125_MHz();

            // Layer 6 — DTMF block
            T_Dtmf_DefaultId();
            T_Dtmf_PttId();
            T_Dtmf_Duration();
            T_Dtmf_CodeGroup();
            T_Dtmf_EmptyGroup();
            T_Dtmf_RoundTrip();

            // Layer 7 — Encryption keys
            T_EncKey_Empty();
            T_EncKey_Arc4();
            T_EncKey_Aes128();
            T_EncKey_Aes256();
            T_EncKey_RoundTrip();

            // Layer 8 — Contact record
            T_Contact_Decode_GroupCall();
            T_Contact_Decode_PrivateCall();
            T_Contact_Decode_Empty();
            T_Contact_DmrId_MaxValue();
            T_Contact_RoundTrip();

            // Layer 9 — Rx Group
            T_RxGroup_Empty();
            T_RxGroup_WithMembers();
            T_RxGroup_RoundTrip();

            // Layer 10 — Channel record
            T_Channel_Empty_AllFF();
            T_Channel_Empty_AllZero();
            T_Channel_AnalogFm();
            T_Channel_DmrTierI();
            T_Channel_DmrTierII();
            T_Channel_RoundTrip();

            // Layer 11 — VFO block
            T_Vfo_DefaultFreqSubstitution();
            T_Vfo_RoundTrip();

            // Layer 12 — OptFun block
            T_OptFun_Defaults();
            T_OptFun_RoundTrip();

            // Layer 13 — Basic Info
            T_BasicInfo_DefaultModelName();
            T_BasicInfo_ModelId_Encoding();
            T_BasicInfo_RoundTrip();

            // Layer 14 — Packers
            T_ChannelPacker_EmptyRoundTrip();
            T_ContactsPacker_EmptyRoundTrip();
            T_RxGroupsPacker_EmptyRoundTrip();

            // Summary
            Console.WriteLine();
            Console.WriteLine("═══════════════════════════════════════════════");
            Console.WriteLine($"  Results: {_pass} passed, {_fail} failed");
            if (_failures.Count > 0)
            {
                Console.WriteLine("  Failures:");
                foreach (var f in _failures)
                    Console.WriteLine($"    ✗ {f}");
            }
            Console.WriteLine("═══════════════════════════════════════════════");
            return _fail == 0;
        }

        // ── CRC tests ────────────────────────────────────────────────────────────

        private static void T_Crc_KnownVector()
        {
            // CRC-16/CCITT with initial 0 over "123456789" = 0x31C3
            byte[] data = System.Text.Encoding.ASCII.GetBytes("123456789");
            AssertEqual("CRC known vector (initial=0)", (ushort)0x31C3, Crc16Ccitt.Compute(data));
        }

        private static void T_Crc_HandshakeFrame()
        {
            // §11.1: handshake frame.  CRC covers bytes 1-20 (cmd through payload).
            // Build the frame and verify CRC field is consistent.
            byte[] payload = FixedPayloads.Handshake;
            byte[] frame   = ProtocolV2.BuildFrame(Commands.Handshake, 0, payload);

            // Extract CRC from frame
            int    n      = payload.Length;
            ushort rxCrc  = (ushort)((frame[6 + n] << 8) | frame[6 + n + 1]);
            // Recompute over bytes 1..5+n
            ushort calcCrc = Crc16Ccitt.Compute(frame, 1, 5 + n);
            AssertEqual("Handshake frame CRC self-consistent", calcCrc, rxCrc);
        }

        private static void T_Crc_PasswordFrame()
        {
            byte[] frame  = ProtocolV2.BuildFrame(Commands.CheckPassword, 0, FixedPayloads.Password);
            int    n      = FixedPayloads.Password.Length;
            ushort rxCrc  = (ushort)((frame[6 + n] << 8) | frame[6 + n + 1]);
            ushort calcCrc = Crc16Ccitt.Compute(frame, 1, 5 + n);
            AssertEqual("Password frame CRC self-consistent", calcCrc, rxCrc);
        }

        // ── Frame building tests ─────────────────────────────────────────────────

        private static void T_Frame_HandshakeBytes()
        {
            // §11.1: A5 02 00 00 00 0F 50 52 4F 47 52 41 4D 4A 43 38 38 31 30 44 55 [CRC]
            byte[] frame = ProtocolV2.BuildFrame(Commands.Handshake, 0, FixedPayloads.Handshake);
            AssertEqual("Handshake frame[0]=SOF",     (byte)0xA5, frame[0]);
            AssertEqual("Handshake frame[1]=CMD",     (byte)0x02, frame[1]);
            AssertEqual("Handshake frame[2]=SEQ_HI",  (byte)0x00, frame[2]);
            AssertEqual("Handshake frame[3]=SEQ_LO",  (byte)0x00, frame[3]);
            AssertEqual("Handshake frame[4]=LEN_HI",  (byte)0x00, frame[4]);
            AssertEqual("Handshake frame[5]=LEN_LO",  (byte)0x0F, frame[5]);
            // First payload byte = 'P' = 0x50
            AssertEqual("Handshake frame[6]=P",       (byte)0x50, frame[6]);
            // Last payload byte = 'U' = 0x55
            AssertEqual("Handshake frame[20]=U",      (byte)0x55, frame[20]);
            AssertEqual("Handshake frame total length", 23, frame.Length);
        }

        private static void T_Frame_PasswordBytes()
        {
            // §11.2: A5 05 00 00 00 06 FF FF FF FF FF FF [CRC]
            byte[] frame = ProtocolV2.BuildFrame(Commands.CheckPassword, 0, FixedPayloads.Password);
            AssertEqual("Password frame[1]=CMD",     (byte)0x05, frame[1]);
            AssertEqual("Password frame[5]=LEN_LO",  (byte)0x06, frame[5]);
            AssertEqual("Password frame[6]=0xFF",    (byte)0xFF, frame[6]);
            AssertEqual("Password frame total length", 14, frame.Length);
        }

        private static void T_Frame_ChannelWriteHeader()
        {
            // §11.3: A5 30 00 00 04 00 [1024 bytes] [CRC]
            // LEN = 0x0400 = 1024
            byte[] payload = new byte[1024];
            byte[] frame   = ProtocolV2.BuildFrame(Commands.WriteChannels, 0, payload);
            AssertEqual("Channel write frame[1]=CMD",    (byte)0x30, frame[1]);
            AssertEqual("Channel write frame[4]=LEN_HI", (byte)0x04, frame[4]);
            AssertEqual("Channel write frame[5]=LEN_LO", (byte)0x00, frame[5]);
            AssertEqual("Channel write total length", 1024 + 8, frame.Length);
        }

        // ── SubAudio tests ───────────────────────────────────────────────────────

        private static void T_SubAudio_Off()
        {
            var sa = SubAudio.Off();
            byte[] buf = new byte[2];
            sa.Encode(buf, 0);
            AssertEqual("SubAudio OFF byte0", (byte)0x00, buf[0]);
            AssertEqual("SubAudio OFF byte1", (byte)0x00, buf[1]);
            var decoded = SubAudio.Decode(buf, 0);
            AssertEqual("SubAudio OFF type", SubAudioType.Off, decoded.Type);
        }

        private static void T_SubAudio_Ctcss_88_5()
        {
            // 88.5 Hz → 885 = 0x0375 → LE: byte0=0x75, byte1=0x03 (§8.2)
            var sa = SubAudio.Ctcss(88.5f);
            byte[] buf = new byte[2];
            sa.Encode(buf, 0);
            AssertEqual("CTCSS 88.5 byte0=0x75", (byte)0x75, buf[0]);
            AssertEqual("CTCSS 88.5 byte1=0x03", (byte)0x03, buf[1]);
            var decoded = SubAudio.Decode(buf, 0);
            AssertEqual("CTCSS 88.5 type",  SubAudioType.Ctcss, decoded.Type);
            AssertClose("CTCSS 88.5 freq",  88.5f, decoded.CtcssHz, 0.1f);
        }

        private static void T_SubAudio_Ctcss_67_0()
        {
            var sa = SubAudio.Ctcss(67.0f);
            byte[] buf = new byte[2];
            sa.Encode(buf, 0);
            // 67.0 × 10 = 670 = 0x029E → LE: byte0=0x9E, byte1=0x02
            AssertEqual("CTCSS 67.0 byte0=0x9E", (byte)0x9E, buf[0]);
            AssertEqual("CTCSS 67.0 byte1=0x02", (byte)0x02, buf[1]);
        }

        private static void T_SubAudio_Dcs_D023N()
        {
            // D023N is index 0 in the table → byte0 = 1, byte1 = 0 (§8.2)
            var sa = SubAudio.Dcs("D023N");
            byte[] buf = new byte[2];
            sa.Encode(buf, 0);
            AssertEqual("DCS D023N byte0=0x01", (byte)0x01, buf[0]);
            AssertEqual("DCS D023N byte1=0x00", (byte)0x00, buf[1]);
            var decoded = SubAudio.Decode(buf, 0);
            AssertEqual("DCS D023N type",  SubAudioType.Dcs, decoded.Type);
            AssertEqual("DCS D023N code",  "D023N", decoded.DcsCode);
        }

        private static void T_SubAudio_Dcs_D023I()
        {
            // D023I is index 105 → byte0 = 106, byte1 = 0 (§8.2: 0x6A = D023I)
            var sa = SubAudio.Dcs("D023I");
            byte[] buf = new byte[2];
            sa.Encode(buf, 0);
            AssertEqual("DCS D023I byte0=106", (byte)106, buf[0]);
            AssertEqual("DCS D023I byte1=0",   (byte)0,   buf[1]);
            // 0x6A = 106 decimal ✓ matches spec example
            AssertEqual("DCS D023I byte0=0x6A", (byte)0x6A, buf[0]);
        }

        private static void T_SubAudio_RoundTrip_Ctcss()
        {
            float[] tones = { 67.0f, 69.3f, 88.5f, 100.0f, 141.3f, 210.7f, 254.1f };
            foreach (float tone in tones)
            {
                var enc = SubAudio.Ctcss(tone);
                byte[] buf = new byte[2];
                enc.Encode(buf, 0);
                var dec = SubAudio.Decode(buf, 0);
                AssertEqual($"CTCSS {tone} round-trip type", SubAudioType.Ctcss, dec.Type);
                AssertClose($"CTCSS {tone} round-trip freq", tone, dec.CtcssHz, 0.1f);
            }
        }

        private static void T_SubAudio_RoundTrip_Dcs()
        {
            string[] codes = { "D023N", "D754N", "D023I", "D754I" };
            foreach (var code in codes)
            {
                var enc = SubAudio.Dcs(code);
                byte[] buf = new byte[2];
                enc.Encode(buf, 0);
                var dec = SubAudio.Decode(buf, 0);
                AssertEqual($"DCS {code} round-trip type", SubAudioType.Dcs, dec.Type);
                AssertEqual($"DCS {code} round-trip code", code, dec.DcsCode);
            }
        }

        // ── FHSS tests ───────────────────────────────────────────────────────────

        private static void T_Fhss_Encode_1A2B3C()
        {
            // §8.3 example: "1A2B3C"
            // digit0=1,1=A,2=2,3=B,4=3,5=C
            // byte30=(1<<4|A)=0x1A, byte29=(2<<4|B)=0x2B, byte28=(3<<4|C)=0x3C, byte31=0x00
            var ch = new Channel
            {
                RxFreqMHz = 145.5, TxFreqMHz = 145.5,
                RxSubAudio = SubAudio.Off(), TxSubAudio = SubAudio.Off(),
                Name = "TEST", FhssCode = "1A2B3C"
            };
            byte[] rec = new byte[Channel.RecordSize];
            Codec.PadFF(rec, 0, rec.Length);
            ch.Encode(rec, 0);

            AssertEqual("FHSS byte28=0x3C", (byte)0x3C, rec[28]);
            AssertEqual("FHSS byte29=0x2B", (byte)0x2B, rec[29]);
            AssertEqual("FHSS byte30=0x1A", (byte)0x1A, rec[30]);
            AssertEqual("FHSS byte31=0x00", (byte)0x00, rec[31]);
        }

        private static void T_Fhss_ValidFlag_Unused()
        {
            var ch = new Channel
            {
                RxFreqMHz = 145.5, TxFreqMHz = 145.5,
                RxSubAudio = SubAudio.Off(), TxSubAudio = SubAudio.Off(),
                Name = "TEST", FhssCode = null
            };
            byte[] rec = new byte[Channel.RecordSize];
            Codec.PadFF(rec, 0, rec.Length);
            ch.Encode(rec, 0);
            AssertEqual("FHSS unused flag=0xFF", (byte)0xFF, rec[31]);
        }

        // ── Frequency tests ──────────────────────────────────────────────────────

        private static void T_Freq_145_5_MHz()
        {
            // 145.5 MHz = 145,500,000 Hz / 10 = 14,550,000 = 0x00DE03F0
            // LE: F0 03 DE 00
            // Note: the spec PDF §8.1 example erroneously states 0x00DE1B90
            // (which would decode to 145.56048 MHz). The correct value is 0x00DE03F0.
            byte[] buf = new byte[4];
            Codec.WriteFreqMHz(buf, 0, 145.5);
            AssertEqual("145.5 MHz LE[0]=0xF0", (byte)0xF0, buf[0]);
            AssertEqual("145.5 MHz LE[1]=0x03", (byte)0x03, buf[1]);
            AssertEqual("145.5 MHz LE[2]=0xDE", (byte)0xDE, buf[2]);
            AssertEqual("145.5 MHz LE[3]=0x00", (byte)0x00, buf[3]);
            double decoded = Codec.ReadFreqMHz(buf, 0);
            AssertClose("145.5 MHz round-trip", 145.5, decoded, 0.0001);
        }

        private static void T_Freq_146_520_MHz()
        {
            // 146.520 MHz = 146,520,000 Hz / 10 = 14,652,000 = 0x00DF9260
            // LE: 60 92 DF 00
            // Note: the spec PDF erroneously states 0x00DFC1A0 (= 146.64096 MHz).
            byte[] buf = new byte[4];
            Codec.WriteFreqMHz(buf, 0, 146.520);
            AssertEqual("146.52 LE[0]=0x60", (byte)0x60, buf[0]);
            AssertEqual("146.52 LE[1]=0x92", (byte)0x92, buf[1]);
            AssertEqual("146.52 LE[2]=0xDF", (byte)0xDF, buf[2]);
            AssertEqual("146.52 LE[3]=0x00", (byte)0x00, buf[3]);
        }

        private static void T_Freq_400_125_MHz()
        {
            byte[] buf = new byte[4];
            Codec.WriteFreqMHz(buf, 0, 400.125);
            double rt = Codec.ReadFreqMHz(buf, 0);
            AssertClose("400.125 MHz round-trip", 400.125, rt, 0.0001);
        }

        // ── DTMF tests ───────────────────────────────────────────────────────────

        private static void T_Dtmf_DefaultId()
        {
            // Default ID is "12345" → [01 02 03 04 05] (§4.1)
            var d = new DtmfBlock { CurrentId = "12345" };
            byte[] buf = d.Encode();
            AssertEqual("DTMF ID[0]=1", (byte)1, buf[0]);
            AssertEqual("DTMF ID[1]=2", (byte)2, buf[1]);
            AssertEqual("DTMF ID[2]=3", (byte)3, buf[2]);
            AssertEqual("DTMF ID[3]=4", (byte)4, buf[3]);
            AssertEqual("DTMF ID[4]=5", (byte)5, buf[4]);
        }

        private static void T_Dtmf_PttId()
        {
            var d = new DtmfBlock { PttId = DtmfPttId.Both };
            byte[] buf = d.Encode();
            AssertEqual("DTMF PttId=3 (Both)", (byte)3, (byte)(buf[6] & 0x0F));
        }

        private static void T_Dtmf_Duration()
        {
            var d = new DtmfBlock { Duration = DtmfDuration.Ms150 };
            byte[] buf = d.Encode();
            AssertEqual("DTMF Duration=2 (150ms)", (byte)2, (byte)(buf[7] & 0x0F));
        }

        private static void T_Dtmf_CodeGroup()
        {
            var d = new DtmfBlock();
            d.CodeGroups[0] = "123";   // indices: 1,2,3
            byte[] buf = d.Encode();
            int off = 32; // first group
            AssertEqual("DTMF group[0] digit0=1", (byte)1, buf[off]);
            AssertEqual("DTMF group[0] digit1=2", (byte)2, buf[off+1]);
            AssertEqual("DTMF group[0] digit2=3", (byte)3, buf[off+2]);
            AssertEqual("DTMF group[0] digit3=pad", (byte)0xFF, buf[off+3]);
        }

        private static void T_Dtmf_EmptyGroup()
        {
            var d = new DtmfBlock();
            d.CodeGroups[0] = null;
            byte[] buf = d.Encode();
            AssertEqual("DTMF empty group byte0=0xFF", (byte)0xFF, buf[32]);
        }

        private static void T_Dtmf_RoundTrip()
        {
            var d = new DtmfBlock
            {
                CurrentId = "1A2*#",
                PttId     = DtmfPttId.Both,
                Duration  = DtmfDuration.Ms200,
                Interval  = DtmfInterval.Ms50,
            };
            d.CodeGroups[0]  = "ABC";
            d.CodeGroups[14] = "0*";
            byte[] buf = d.Encode();
            AssertEqual("DTMF payload size", DtmfBlock.PayloadSize, buf.Length);
            var d2 = DtmfBlock.Decode(buf);
            AssertEqual("DTMF RT CurrentId", d.CurrentId, d2.CurrentId);
            AssertEqual("DTMF RT PttId",     d.PttId,     d2.PttId);
            AssertEqual("DTMF RT Duration",  d.Duration,  d2.Duration);
            AssertEqual("DTMF RT Interval",  d.Interval,  d2.Interval);
            AssertEqual("DTMF RT Group[0]",  d.CodeGroups[0],  d2.CodeGroups[0]);
            AssertEqual("DTMF RT Group[14]", d.CodeGroups[14], d2.CodeGroups[14]);
            Assert("DTMF RT Group[1] null",  d2.CodeGroups[1] is null);
        }

        // ── Encryption Key tests ─────────────────────────────────────────────────

        private static void T_EncKey_Empty()
        {
            byte[] buf = new EncKeyBlock().Encode();
            // All entries empty → every byte 0xFF
            AssertEqual("EncKey empty block[0]=0xFF", (byte)0xFF, buf[0]);
            AssertEqual("EncKey empty block[1]=0xFF", (byte)0xFF, buf[1]);
        }

        private static void T_EncKey_Arc4()
        {
            var b = new EncKeyBlock();
            b.Entries[0] = new EncKeyEntry { Algorithm = EncAlgo.Arc4, Key = "AABBCCDDEE" };
            byte[] buf = b.Encode();
            AssertEqual("ARC4 algo nibble=0", (byte)0, (byte)(buf[0] & 0x0F));
        }

        private static void T_EncKey_Aes128()
        {
            var b = new EncKeyBlock();
            b.Entries[0] = new EncKeyEntry { Algorithm = EncAlgo.Aes128, Key = "DEADBEEF0000111122223333CAFEBABE" };
            byte[] buf = b.Encode();
            AssertEqual("AES128 algo nibble=1", (byte)1, (byte)(buf[0] & 0x0F));
        }

        private static void T_EncKey_Aes256()
        {
            var b = new EncKeyBlock();
            b.Entries[0] = new EncKeyEntry { Algorithm = EncAlgo.Aes256, Key = new string('A', 64) };
            byte[] buf = b.Encode();
            AssertEqual("AES256 algo nibble=2", (byte)2, (byte)(buf[0] & 0x0F));
            AssertEqual("EncKey block total size", EncKeyBlock.PayloadSize, buf.Length);
        }

        private static void T_EncKey_RoundTrip()
        {
            var b = new EncKeyBlock();
            b.Entries[0] = new EncKeyEntry { Algorithm = EncAlgo.Aes128, Key = "01234567890ABCDEF01234567890ABCD" };
            b.Entries[7] = new EncKeyEntry { Algorithm = EncAlgo.Arc4,   Key = "1234567890" };
            byte[] buf = b.Encode();
            var b2 = EncKeyBlock.Decode(buf);
            AssertEqual("EncKey RT [0] algo", EncAlgo.Aes128, b2.Entries[0]!.Algorithm);
            Assert("EncKey RT [1] null", b2.Entries[1] is null);
            AssertEqual("EncKey RT [7] algo", EncAlgo.Arc4, b2.Entries[7]!.Algorithm);
        }

        // ── Contact tests ────────────────────────────────────────────────────────

        private static void T_Contact_Decode_GroupCall()
        {
            byte[] rec = new byte[Contact.RecordSize];
            Codec.PadFF(rec, 0, rec.Length);
            rec[0] = 0x00; // CallType=Group
            rec[1] = 0x00;
            Codec.WriteDmrId(rec, 2, 12345);
            Codec.WriteGb2312(rec, 5, 10, "Test");
            var c = Contact.Decode(rec, 0);
            AssertEqual("Contact Group type",  CallType.Group, c!.CallType);
            AssertEqual("Contact Group ID",    12345u,         c.CallId);
            AssertEqual("Contact Group name",  "Test",         c.Name);
        }

        private static void T_Contact_Decode_PrivateCall()
        {
            byte[] rec = new byte[Contact.RecordSize];
            Codec.PadFF(rec, 0, rec.Length);
            rec[0] = 0x01;
            rec[1] = 0x00;
            Codec.WriteDmrId(rec, 2, 1234567);
            Codec.WriteGb2312(rec, 5, 10, "W1AW");
            var c = Contact.Decode(rec, 0);
            AssertEqual("Contact Private type", CallType.Private, c!.CallType);
            AssertEqual("Contact Private ID",   1234567u,          c.CallId);
        }

        private static void T_Contact_Decode_Empty()
        {
            byte[] rec = new byte[Contact.RecordSize];
            Codec.PadFF(rec, 0, rec.Length); // all 0xFF
            var c = Contact.Decode(rec, 0);
            Assert("Contact empty returns null", c is null);
        }

        private static void T_Contact_DmrId_MaxValue()
        {
            byte[] buf = new byte[3];
            Codec.WriteDmrId(buf, 0, 16_777_215u);
            uint rt = Codec.ReadDmrId(buf, 0);
            AssertEqual("DMR ID max value round-trip", 16_777_215u, rt);
        }

        private static void T_Contact_RoundTrip()
        {
            var c = new Contact { CallType = CallType.Private, CallId = 9876543, Name = "VE3XYZ" };
            byte[] rec = new byte[Contact.RecordSize];
            Codec.PadFF(rec, 0, rec.Length);
            c.Encode(rec, 0);
            var c2 = Contact.Decode(rec, 0);
            AssertEqual("Contact RT type",  c.CallType, c2!.CallType);
            AssertEqual("Contact RT id",    c.CallId,   c2.CallId);
            AssertEqual("Contact RT name",  c.Name,     c2.Name);
        }

        // ── RxGroup tests ────────────────────────────────────────────────────────

        private static void T_RxGroup_Empty()
        {
            byte[] buf = new byte[RxGroup.EntrySize];
            Codec.PadFF(buf, 0, buf.Length);
            var g = RxGroup.Decode(buf, 0);
            Assert("RxGroup empty returns null", g is null);
        }

        private static void T_RxGroup_WithMembers()
        {
            var g = new RxGroup { Name = "LocalNet", Members = new uint[] { 1234, 5678, 9999 } };
            byte[] buf = new byte[RxGroup.EntrySize];
            Codec.PadFF(buf, 0, buf.Length);
            g.Encode(buf, 0);
            AssertEqual("RxGroup name first char 'L'", (byte)'L', buf[RxGroup.NameOffset]);
            uint m0 = Codec.ReadDmrId(buf, 0);
            AssertEqual("RxGroup member[0]", 1234u, m0);
        }

        private static void T_RxGroup_RoundTrip()
        {
            var g = new RxGroup { Name = "Group1", Members = new uint[] { 111, 222, 333 } };
            byte[] buf = new byte[RxGroup.EntrySize];
            Codec.PadFF(buf, 0, buf.Length);
            g.Encode(buf, 0);
            var g2 = RxGroup.Decode(buf, 0);
            AssertEqual("RxGroup RT name",    g.Name,             g2!.Name);
            AssertEqual("RxGroup RT members", g.Members.Length,   g2.Members.Length);
            AssertEqual("RxGroup RT member0", g.Members[0],       g2.Members[0]);
        }

        // ── Channel tests ────────────────────────────────────────────────────────

        private static void T_Channel_Empty_AllFF()
        {
            byte[] rec = new byte[Channel.RecordSize];
            Codec.PadFF(rec, 0, rec.Length);
            Assert("Channel all-FF returns null", Channel.Decode(rec, 0) is null);
        }

        private static void T_Channel_Empty_AllZero()
        {
            byte[] rec = new byte[Channel.RecordSize]; // all 0x00
            Assert("Channel all-zero returns null", Channel.Decode(rec, 0) is null);
        }

        private static void T_Channel_AnalogFm()
        {
            var ch = new Channel
            {
                RxFreqMHz  = 146.520, TxFreqMHz  = 146.520,
                RxSubAudio = SubAudio.Ctcss(88.5f),
                TxSubAudio = SubAudio.Ctcss(88.5f),
                Type       = ChannelType.AnalogFm,
                Power      = TxPower.High,
                ScanAdd    = true,
                Name       = "ARDF",
            };
            byte[] rec = new byte[Channel.RecordSize];
            Codec.PadFF(rec, 0, rec.Length);
            ch.Encode(rec, 0);

            // §8.1: byte14=1 (Analog flag), byte15=0 (irrelevant for Analog type)
            AssertEqual("AnalogFM byte14=1", (byte)1, (byte)(rec[14] & 0x0F));
            AssertEqual("AnalogFM byte15=0", (byte)0, (byte)(rec[15] & 0x0F));

            var ch2 = Channel.Decode(rec, 0);
            AssertEqual("AnalogFM type",  ChannelType.AnalogFm, ch2!.Type);
            AssertEqual("AnalogFM power", TxPower.High,          ch2.Power);
            AssertEqual("AnalogFM scan",  true,                  ch2.ScanAdd);
            AssertClose("AnalogFM rxfreq", 146.520, ch2.RxFreqMHz, 0.0001);
            AssertClose("AnalogFM CTCSS",  88.5f,   ch2.RxSubAudio.CtcssHz, 0.1f);
        }

        private static void T_Channel_DmrTierII()
        {
            var ch = new Channel
            {
                RxFreqMHz = 441.0, TxFreqMHz = 446.0,
                RxSubAudio = SubAudio.Off(), TxSubAudio = SubAudio.Off(),
                Type       = ChannelType.DmrTierII,
                ColorCode  = 7,
                TimeSlot   = 1,
                Name       = "DMR_RPT",
            };
            byte[] rec = new byte[Channel.RecordSize];
            Codec.PadFF(rec, 0, rec.Length);
            ch.Encode(rec, 0);

            // §8.1: byte14=0 (Digital), byte15=1 (Tier II / repeater)
            AssertEqual("DMR TierII byte14=0", (byte)0, (byte)(rec[14] & 0x0F));
            AssertEqual("DMR TierII byte15=1", (byte)1, (byte)(rec[15] & 0x0F));

            var ch2 = Channel.Decode(rec, 0);
            AssertEqual("DMR TierII type",  ChannelType.DmrTierII, ch2!.Type);
            AssertEqual("DMR TierII CC",    7,                      ch2.ColorCode);
            AssertEqual("DMR TierII TS",    1,                      ch2.TimeSlot);
        }

        private static void T_Channel_DmrTierI()
        {
            // §8.1: byte14=0 (Digital), byte15=0 (Tier I / simplex)
            var ch = new Channel
            {
                RxFreqMHz = 446.0, TxFreqMHz = 446.0,
                RxSubAudio = SubAudio.Off(), TxSubAudio = SubAudio.Off(),
                Type       = ChannelType.DmrTierI,
                ColorCode  = 1,
                TimeSlot   = 0,
                Name       = "DMR_SX",
            };
            byte[] rec = new byte[Channel.RecordSize];
            Codec.PadFF(rec, 0, rec.Length);
            ch.Encode(rec, 0);

            AssertEqual("DMR TierI byte14=0", (byte)0, (byte)(rec[14] & 0x0F));
            AssertEqual("DMR TierI byte15=0", (byte)0, (byte)(rec[15] & 0x0F));

            var ch2 = Channel.Decode(rec, 0);
            AssertEqual("DMR TierI type", ChannelType.DmrTierI, ch2!.Type);
        }

        private static void T_Channel_RoundTrip()
        {
            var ch = new Channel
            {
                RxFreqMHz  = 145.5,   TxFreqMHz  = 145.5,
                RxSubAudio = SubAudio.Dcs("D023N"),
                TxSubAudio = SubAudio.Dcs("D023N"),
                Type       = ChannelType.AnalogFm,
                Power      = TxPower.Medium,
                Scramble   = 3,
                BusyLockout = true,
                ScanAdd    = true,
                Name       = "RT5DTEST",
                FhssCode   = null,
            };
            byte[] rec = new byte[Channel.RecordSize];
            Codec.PadFF(rec, 0, rec.Length);
            ch.Encode(rec, 0);
            var ch2 = Channel.Decode(rec, 0);
            AssertClose("CH RT rxfreq",   ch.RxFreqMHz, ch2!.RxFreqMHz, 0.0001);
            AssertEqual("CH RT DCS type", SubAudioType.Dcs, ch2.RxSubAudio.Type);
            AssertEqual("CH RT DCS code", "D023N", ch2.RxSubAudio.DcsCode);
            AssertEqual("CH RT scramble", ch.Scramble, ch2.Scramble);
            AssertEqual("CH RT busy",     ch.BusyLockout, ch2.BusyLockout);
            AssertEqual("CH RT name",     ch.Name, ch2.Name);
        }

        // ── VFO tests ────────────────────────────────────────────────────────────

        private static void T_Vfo_DefaultFreqSubstitution()
        {
            byte[] buf = new byte[Sizes.Vfo];
            Codec.PadFF(buf, 0, Sizes.Vfo); // all 0xFF
            var vfo = VfoBlock.Decode(buf);
            // Default freq should be substituted (§9)
            AssertClose("VFO A default freq", VfoBank.DefaultFreqA, vfo.BankA.RxFreqMHz, 0.001);
            AssertClose("VFO B default freq", VfoBank.DefaultFreqB, vfo.BankB.RxFreqMHz, 0.001);
        }

        private static void T_Vfo_RoundTrip()
        {
            var vfo = new VfoBlock();
            vfo.BankA.RxFreqMHz = 146.520;
            vfo.BankA.Step      = StepFreq.kHz12_5;
            vfo.BankB.RxFreqMHz = 446.000;
            byte[] buf = vfo.Encode();
            AssertEqual("VFO payload size", Sizes.Vfo, buf.Length);
            var vfo2 = VfoBlock.Decode(buf);
            AssertClose("VFO A RT freq", 146.520, vfo2.BankA.RxFreqMHz, 0.0001);
            AssertEqual("VFO A RT step", StepFreq.kHz12_5, vfo2.BankA.Step);
            AssertClose("VFO B RT freq", 446.000, vfo2.BankB.RxFreqMHz, 0.0001);
        }

        // ── OptFun tests ─────────────────────────────────────────────────────────

        private static void T_OptFun_Defaults()
        {
            var o = new OptFunBlock();
            byte[] buf = o.Encode();
            AssertEqual("OptFun size", Sizes.OptFun, buf.Length);
            var o2 = OptFunBlock.Decode(buf);
            AssertEqual("OptFun RT AnalogSql=3", 3, o2.AnalogSql);
            AssertEqual("OptFun RT Beep=true",   true, o2.Beep);
        }

        private static void T_OptFun_RoundTrip()
        {
            var o = new OptFunBlock
            {
                Vox = 5, AutoBacklight = 2, Tdr = true,
                ScanMode = ScanMode.Search,
                TopKey1S = KeyFunc.Sos,
                SideKey2S = KeyFunc.PttB,
                DigitalSql = 4,
                TdrRecovery = 5,
            };
            byte[] buf = o.Encode();
            var o2 = OptFunBlock.Decode(buf);
            AssertEqual("OptFun RT Vox",       o.Vox,       o2.Vox);
            AssertEqual("OptFun RT Tdr",        o.Tdr,        o2.Tdr);
            AssertEqual("OptFun RT ScanMode",   o.ScanMode,   o2.ScanMode);
            AssertEqual("OptFun RT TopKey1S",   o.TopKey1S,   o2.TopKey1S);
            AssertEqual("OptFun RT SideKey2S",  o.SideKey2S,  o2.SideKey2S);
            AssertEqual("OptFun RT DigitalSql", o.DigitalSql, o2.DigitalSql);
        }

        // ── BasicInfo tests ──────────────────────────────────────────────────────

        private static void T_BasicInfo_DefaultModelName()
        {
            var b = new BasicInfoBlock();
            byte[] buf = b.Encode();
            var b2 = BasicInfoBlock.Decode(buf);
            AssertEqual("BasicInfo default model name", "DMR", b2.ModelName);
        }

        private static void T_BasicInfo_ModelId_Encoding()
        {
            // ModelId=1 → "00000001" as ASCII bytes (§11.1)
            var b = new BasicInfoBlock { ModelId = 1 };
            byte[] buf = b.Encode();
            AssertEqual("ModelId[0]='0'", (byte)'0', buf[BasicInfoBlock.ModelIdOffset]);
            AssertEqual("ModelId[7]='1'", (byte)'1', buf[BasicInfoBlock.ModelIdOffset + 7]);
        }

        private static void T_BasicInfo_RoundTrip()
        {
            var b = new BasicInfoBlock { ModelName = "RT-5D", ModelId = 42 };
            byte[] buf = b.Encode();
            var b2 = BasicInfoBlock.Decode(buf);
            AssertEqual("BasicInfo RT name", "RT-5D", b2.ModelName);
            AssertEqual("BasicInfo RT ID",   42,       b2.ModelId);
        }

        // ── Packer round-trips ───────────────────────────────────────────────────

        private static void T_ChannelPacker_EmptyRoundTrip()
        {
            var channels = new Channel?[ChannelsPackerV2.TotalChannels]; // all null
            byte[][] packets = ChannelsPackerV2.Pack(channels);
            AssertEqual("ChannelPacker packet count", Sizes.ChannelPackets, packets.Length);
            var channels2 = ChannelsPackerV2.Unpack(packets);
            Assert("ChannelPacker empty RT: all null",
                   channels2.All(c => c is null));
        }

        private static void T_ContactsPacker_EmptyRoundTrip()
        {
            var contacts = new Contact?[ContactsPackerV2.MaxContacts];
            byte[][] packets = ContactsPackerV2.Pack(contacts);
            AssertEqual("ContactsPacker packet count", Sizes.AddrBookPackets, packets.Length);
            var contacts2 = ContactsPackerV2.Unpack(packets);
            Assert("ContactsPacker empty RT: all null",
                   contacts2.All(c => c is null));
        }

        private static void T_RxGroupsPacker_EmptyRoundTrip()
        {
            var groups = new RxGroup?[RxGroupsPackerV2.MaxGroups];
            byte[][] packets = RxGroupsPackerV2.Pack(groups);
            AssertEqual("RxGroupsPacker packet count", Sizes.RxGroupPackets, packets.Length);
            var groups2 = RxGroupsPackerV2.Unpack(packets);
            Assert("RxGroupsPacker empty RT: all null",
                   groups2.All(g => g is null));
        }

        // ── Assertion helpers ────────────────────────────────────────────────────

        private static void Assert(string name, bool condition)
        {
            if (condition) Pass(name);
            else Fail(name, "condition false");
        }

        private static void AssertEqual<T>(string name, T expected, T actual)
        {
            if (EqualityComparer<T>.Default.Equals(expected, actual))
                Pass(name);
            else
                Fail(name, $"expected={expected}, actual={actual}");
        }

        private static void AssertClose(string name, double expected, double actual, double tolerance)
        {
            if (Math.Abs(expected - actual) <= tolerance) Pass(name);
            else Fail(name, $"expected≈{expected}, actual={actual}, tolerance={tolerance}");
        }

        private static void AssertClose(string name, float expected, float actual, float tolerance)
        {
            if (MathF.Abs(expected - actual) <= tolerance) Pass(name);
            else Fail(name, $"expected≈{expected}, actual={actual}, tolerance={tolerance}");
        }

        private static void Pass(string name)
        {
            _pass++;
            Console.WriteLine($"  ✓ {name}");
        }

        private static void Fail(string name, string reason)
        {
            _fail++;
            string msg = $"{name}: {reason}";
            _failures.Add(msg);
            Console.WriteLine($"  ✗ {msg}");
        }
    }
}
