// Crc16Ccitt.cs
// CRC-16/CCITT implementation derived exactly from the CPS source code
// as documented in RT5D_Protocol_Analysis.PDF §3.2 (CRC Algorithm).
//
// Parameters (from spec):
//   Polynomial   : 0x1021
//   Initial value: 0x0000   (code XORs starting from 0 — NOT 0xFFFF)
//   Reflect input: false
//   Reflect output: false
//   XOR output   : 0x0000
//
// Coverage: bytes 1 through 5+N of the frame
//   (cmd, seq_hi, seq_lo, len_hi, len_lo, payload)
//   — does NOT include the SOF byte (0xA5) or the CRC bytes themselves.

using System;

namespace RT5D
{
    /// <summary>
    /// CRC-16/CCITT (polynomial 0x1021, initial value 0x0000).
    /// Reproduces the exact algorithm from DataHandleHelper.CrcValidation()
    /// as documented in RT5D_Protocol_Analysis.PDF §3.2.
    /// </summary>
    public static class Crc16Ccitt
    {
        private const ushort Polynomial = 0x1021;

        /// <summary>
        /// Computes the CRC over <paramref name="data"/>[<paramref name="offset"/>..
        /// <paramref name="offset"/>+<paramref name="count"/>-1].
        /// </summary>
        public static ushort Compute(byte[] data, int offset, int count)
        {
            // Exact translation of the C# loop from the CPS source:
            //
            //   int crc = 0;
            //   for each byte:
            //       crc ^= (byte << 8)
            //       for 8 iterations:
            //           if (crc & 0x8000) != 0: crc = (crc << 1) ^ 0x1021
            //           else:                   crc = (crc << 1)
            //   crc = crc & 0xFFFF

            int crc = 0;                        // initial value is 0x0000

            for (int i = offset; i < offset + count; i++)
            {
                crc ^= (data[i] << 8);

                for (int bit = 0; bit < 8; bit++)
                {
                    if ((crc & 0x8000) != 0)
                        crc = (crc << 1) ^ Polynomial;
                    else
                        crc <<= 1;
                }
            }

            return (ushort)(crc & 0xFFFF);
        }

        /// <summary>
        /// Convenience overload — computes over the entire array.
        /// </summary>
        public static ushort Compute(byte[] data) =>
            Compute(data, 0, data.Length);

        /// <summary>
        /// Computes the CRC over a <see cref="ReadOnlySpan{T}"/>.
        /// </summary>
        public static ushort Compute(ReadOnlySpan<byte> span)
        {
            int crc = 0;

            foreach (byte b in span)
            {
                crc ^= (b << 8);

                for (int bit = 0; bit < 8; bit++)
                {
                    if ((crc & 0x8000) != 0)
                        crc = (crc << 1) ^ Polynomial;
                    else
                        crc <<= 1;
                }
            }

            return (ushort)(crc & 0xFFFF);
        }

        /// <summary>
        /// Returns true if the CRC of <paramref name="data"/> matches
        /// <paramref name="expected"/>.
        /// </summary>
        public static bool Validate(byte[] data, int offset, int count, ushort expected) =>
            Compute(data, offset, count) == expected;
    }
}
