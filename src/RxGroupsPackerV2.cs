// RxGroupsPackerV2.cs
// Packs/unpacks the 4-packet Rx Group List block (4 096 bytes total).
// Derived from RT5D_Data_Structures.PDF §7.

using System;
using System.Collections.Generic;

namespace RT5D
{
    /// <summary>
    /// Packs and unpacks Rx Group entries across the 4-packet Rx Group List block.
    /// Maximum 32 groups; 8 per packet, 128 bytes each.
    /// </summary>
    public static class RxGroupsPackerV2
    {
        public const int MaxGroups      = 32;
        public const int GroupsPerPacket = 8;

        /// <summary>
        /// Decodes all 32 group slots from the 4 Rx Group packets.
        /// Null entries represent empty slots.
        /// </summary>
        public static RxGroup?[] Unpack(byte[][] packets)
        {
            ValidatePackets(packets);
            var groups = new RxGroup?[MaxGroups];

            for (int pkt = 0; pkt < Sizes.RxGroupPackets; pkt++)
            {
                byte[] data = packets[pkt];
                for (int g = 0; g < GroupsPerPacket; g++)
                {
                    int idx = pkt * GroupsPerPacket + g;
                    groups[idx] = RxGroup.Decode(data, g * RxGroup.EntrySize);
                }
            }

            return groups;
        }

        /// <summary>
        /// Encodes up to 32 Rx Groups into the 4-packet byte array.
        /// </summary>
        public static byte[][] Pack(RxGroup?[] groups)
        {
            if (groups is null)
                throw new ArgumentNullException(nameof(groups));
            if (groups.Length > MaxGroups)
                throw new ArgumentException(
                    $"Maximum {MaxGroups} Rx groups, got {groups.Length}.");

            var packets = new byte[Sizes.RxGroupPackets][];
            for (int pkt = 0; pkt < Sizes.RxGroupPackets; pkt++)
            {
                byte[] data = new byte[Sizes.RxGroupPerPacket];
                Codec.PadFF(data, 0, data.Length);
                packets[pkt] = data;

                for (int g = 0; g < GroupsPerPacket; g++)
                {
                    int idx = pkt * GroupsPerPacket + g;
                    if (idx < groups.Length && groups[idx] is not null)
                        groups[idx]!.Encode(data, g * RxGroup.EntrySize);
                    // else: entry stays 0xFF — byte 96 is 0xFF → empty
                }
            }

            return packets;
        }

        private static void ValidatePackets(byte[][] packets)
        {
            if (packets is null || packets.Length != Sizes.RxGroupPackets)
                throw new ArgumentException(
                    $"Expected {Sizes.RxGroupPackets} Rx group packets, got {packets?.Length ?? 0}.");
            for (int i = 0; i < packets.Length; i++)
            {
                if (packets[i] is null || packets[i].Length != Sizes.RxGroupPerPacket)
                    throw new ArgumentException(
                        $"RxGroup packet[{i}] must be {Sizes.RxGroupPerPacket} bytes.");
            }
        }
    }
}
