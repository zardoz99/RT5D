// ChannelsPackerV2.cs
// Packs/unpacks the 64-packet channel block (65 536 bytes total) into
// a flat list of Channel model objects.
// Derived from RT5D_Data_Structures.PDF §8.

using System;
using System.Collections.Generic;

namespace RT5D
{
    /// <summary>
    /// Packs and unpacks channel records across the 64-packet channel block.
    /// Channels are numbered linearly 0-1023; area = channel_index / 64,
    /// in_area_channel = channel_index % 64.
    /// </summary>
    public static class ChannelsPackerV2
    {
        public const int TotalChannels    = 1024;
        public const int ChannelsPerPacket = 16;
        public const int BytesPerChannel   = 64;

        /// <summary>
        /// Decodes all 1 024 channel slots from the 64-packet payload array.
        /// Null entries represent empty/unprogrammed slots.
        /// </summary>
        public static Channel?[] Unpack(byte[][] packets)
        {
            ValidatePackets(packets);
            var channels = new Channel?[TotalChannels];

            for (int pkt = 0; pkt < Sizes.ChannelPackets; pkt++)
            {
                byte[] data = packets[pkt];
                for (int c = 0; c < ChannelsPerPacket; c++)
                {
                    int chIdx = pkt * ChannelsPerPacket + c;
                    channels[chIdx] = Channel.Decode(data, c * BytesPerChannel);
                }
            }

            return channels;
        }

        /// <summary>
        /// Encodes up to 1 024 channel entries into the 64-packet byte array.
        /// Null or missing entries produce all-0xFF records (empty channel sentinel).
        /// </summary>
        public static byte[][] Pack(Channel?[] channels)
        {
            if (channels is null)
                throw new ArgumentNullException(nameof(channels));
            if (channels.Length > TotalChannels)
                throw new ArgumentException(
                    $"Maximum {TotalChannels} channels, got {channels.Length}.");

            var packets = new byte[Sizes.ChannelPackets][];
            for (int pkt = 0; pkt < Sizes.ChannelPackets; pkt++)
            {
                byte[] data = new byte[Sizes.ChannelPerPacket];
                // Fill with 0xFF — empty channel sentinel
                Codec.PadFF(data, 0, data.Length);
                packets[pkt] = data;

                for (int c = 0; c < ChannelsPerPacket; c++)
                {
                    int chIdx = pkt * ChannelsPerPacket + c;
                    if (chIdx < channels.Length && channels[chIdx] is not null)
                        channels[chIdx]!.Encode(data, c * BytesPerChannel);
                    // else: slot stays 0xFF = empty
                }
            }

            return packets;
        }

        private static void ValidatePackets(byte[][] packets)
        {
            if (packets is null || packets.Length != Sizes.ChannelPackets)
                throw new ArgumentException(
                    $"Expected {Sizes.ChannelPackets} channel packets, got {packets?.Length ?? 0}.");
            for (int i = 0; i < packets.Length; i++)
            {
                if (packets[i] is null || packets[i].Length != Sizes.ChannelPerPacket)
                    throw new ArgumentException(
                        $"Channel packet[{i}] must be {Sizes.ChannelPerPacket} bytes.");
            }
        }
    }
}
