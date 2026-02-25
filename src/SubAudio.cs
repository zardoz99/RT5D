// SubAudio.cs
// Encodes and decodes the 2-byte sub-audio field used in Channel and VFO records.
// All logic derived from RT5D_Data_Structures.PDF §8.2 (Sub-Audio Encoding).
//
// Three states:
//   OFF   : [0x00, 0x00]
//   DCS   : [index+1, 0x00]  — byte0 = DCS table index + 1 (1–210)
//   CTCSS : [lo, hi]          — little-endian uint16 = freq × 10 Hz
//
// DCS table: 105 normal codes (D023N … D754N) then 105 inverted (D023I … D754I).

using System;

namespace RT5D
{
    public enum SubAudioType { Off, Ctcss, Dcs }

    public sealed class SubAudio
    {
        public SubAudioType Type    { get; }

        /// <summary>CTCSS frequency in Hz (e.g. 88.5). Only valid when Type == Ctcss.</summary>
        public float CtcssHz { get; }

        /// <summary>
        /// DCS code string, e.g. "D023N" or "D754I".
        /// Only valid when Type == Dcs.
        /// </summary>
        public string DcsCode { get; }

        private SubAudio(SubAudioType type, float ctcss, string dcs)
        {
            Type    = type;
            CtcssHz = ctcss;
            DcsCode = dcs;
        }

        public static SubAudio Off()                  => new(SubAudioType.Off,   0,    "");
        public static SubAudio Ctcss(float hz)        => new(SubAudioType.Ctcss, hz,   "");
        public static SubAudio Dcs(string code)       => new(SubAudioType.Dcs,   0,    code ?? "");

        // ── Decode ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Decodes 2 bytes from <paramref name="buf"/> starting at <paramref name="offset"/>
        /// into a <see cref="SubAudio"/> value.
        /// </summary>
        public static SubAudio Decode(byte[] buf, int offset)
        {
            if (buf is null || offset + 2 > buf.Length)
                throw new ArgumentException("Buffer too short for sub-audio field.");

            byte b0 = buf[offset];
            byte b1 = buf[offset + 1];

            // OFF
            if (b0 == 0x00 && b1 == 0x00)
                return Off();

            // DCS — byte1 is 0x00, byte0 is index+1 (1–210)
            if (b1 == 0x00 && b0 >= 1 && b0 <= DcsTable.Length)
            {
                string code = DcsTable[b0 - 1];
                return Dcs(code);
            }

            // CTCSS — little-endian uint16 = freq × 10
            int raw = b0 | (b1 << 8);
            float hz = raw / 10.0f;
            return Ctcss(hz);
        }

        // ── Encode ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Encodes this value into exactly 2 bytes at <paramref name="buf"/>[<paramref name="offset"/>].
        /// </summary>
        public void Encode(byte[] buf, int offset)
        {
            if (buf is null || offset + 2 > buf.Length)
                throw new ArgumentException("Buffer too short for sub-audio field.");

            switch (Type)
            {
                case SubAudioType.Off:
                    buf[offset]     = 0x00;
                    buf[offset + 1] = 0x00;
                    break;

                case SubAudioType.Ctcss:
                    int raw = (int)MathF.Round(CtcssHz * 10);
                    buf[offset]     = (byte)(raw & 0xFF);        // low byte
                    buf[offset + 1] = (byte)((raw >> 8) & 0xFF); // high byte
                    break;

                case SubAudioType.Dcs:
                    int idx = Array.IndexOf(DcsTable, DcsCode);
                    if (idx < 0)
                        throw new InvalidOperationException($"Unknown DCS code: '{DcsCode}'.");
                    buf[offset]     = (byte)(idx + 1);
                    buf[offset + 1] = 0x00;
                    break;

                default:
                    throw new InvalidOperationException($"Unknown SubAudioType: {Type}.");
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        public override string ToString() => Type switch
        {
            SubAudioType.Off   => "OFF",
            SubAudioType.Ctcss => $"CTCSS {CtcssHz:F1} Hz",
            SubAudioType.Dcs   => $"DCS {DcsCode}",
            _                  => "Unknown"
        };

        // ── DCS code table (RT5D_Data_Structures.PDF §8.2) ─────────────────────
        // 105 normal (N) codes followed by 105 inverted (I) codes.
        // Indices 0-104 = D023N … D754N (normal)
        // Indices 105-209 = D023I … D754I (inverted)

        // 105 standard DCS codes as used by this radio.
        // Spec examples (§8.2): 0x01=D023N (index 0), 0x69=D754N (index 104), 0x6A=D023I (index 105).
        // Wire byte = table index + 1.
        public static readonly string[] DcsTable = {
            // Normal (N) codes — indices 0-104.  Wire values 0x01-0x69.
            "D023N","D025N","D026N","D031N","D032N","D036N","D043N","D047N",  //  0- 7
            "D051N","D053N","D054N","D065N","D071N","D072N","D073N","D074N",  //  8-15
            "D114N","D115N","D116N","D122N","D125N","D131N","D132N","D134N",  // 16-23
            "D143N","D145N","D152N","D155N","D156N","D162N","D165N","D172N",  // 24-31
            "D174N","D205N","D212N","D214N","D215N","D216N","D221N","D223N",  // 32-39
            "D225N","D226N","D243N","D244N","D245N","D246N","D251N","D252N",  // 40-47
            "D255N","D261N","D263N","D265N","D266N","D271N","D274N","D306N",  // 48-55
            "D311N","D315N","D325N","D331N","D332N","D343N","D346N","D351N",  // 56-63
            "D356N","D364N","D365N","D371N","D411N","D412N","D413N","D423N",  // 64-71
            "D431N","D432N","D445N","D446N","D452N","D454N","D455N","D462N",  // 72-79
            "D464N","D465N","D466N","D503N","D506N","D516N","D523N","D526N",  // 80-87
            "D532N","D546N","D565N","D606N","D612N","D624N","D627N","D631N",  // 88-95
            "D632N","D654N","D662N","D664N","D703N","D712N","D723N","D731N",  // 96-103
            "D754N",                                                            // 104 (0x69)
            // Inverted (I) codes — indices 105-209.  Wire values 0x6A-0xD2.
            "D023I","D025I","D026I","D031I","D032I","D036I","D043I","D047I",  // 105-112
            "D051I","D053I","D054I","D065I","D071I","D072I","D073I","D074I",  // 113-120
            "D114I","D115I","D116I","D122I","D125I","D131I","D132I","D134I",  // 121-128
            "D143I","D145I","D152I","D155I","D156I","D162I","D165I","D172I",  // 129-136
            "D174I","D205I","D212I","D214I","D215I","D216I","D221I","D223I",  // 137-144
            "D225I","D226I","D243I","D244I","D245I","D246I","D251I","D252I",  // 145-152
            "D255I","D261I","D263I","D265I","D266I","D271I","D274I","D306I",  // 153-160
            "D311I","D315I","D325I","D331I","D332I","D343I","D346I","D351I",  // 161-168
            "D356I","D364I","D365I","D371I","D411I","D412I","D413I","D423I",  // 169-176
            "D431I","D432I","D445I","D446I","D452I","D454I","D455I","D462I",  // 177-184
            "D464I","D465I","D466I","D503I","D506I","D516I","D523I","D526I",  // 185-192
            "D532I","D546I","D565I","D606I","D612I","D624I","D627I","D631I",  // 193-200
            "D632I","D654I","D662I","D664I","D703I","D712I","D723I","D731I",  // 201-208
            "D754I",                                                            // 209 (0xD2)
        };
    }
}
