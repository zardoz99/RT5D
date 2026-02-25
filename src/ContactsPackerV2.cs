// ContactsPackerV2.cs
// Packs/unpacks the 80-packet address book block (64 000 bytes total).
// Derived from RT5D_Data_Structures.PDF §6.

using System;
using System.Collections.Generic;

namespace RT5D
{
    /// <summary>
    /// Packs and unpacks contact records across the 80-packet address book block.
    /// Maximum 4 000 contacts; 50 per packet, 16 bytes each.
    /// </summary>
    public static class ContactsPackerV2
    {
        public const int MaxContacts       = 4000;
        public const int ContactsPerPacket = 50;

        /// <summary>
        /// Decodes all contacts from the 80 address-book packets.
        /// Null entries represent empty/unprogrammed contact slots.
        /// </summary>
        public static Contact?[] Unpack(byte[][] packets)
        {
            ValidatePackets(packets);
            var contacts = new Contact?[MaxContacts];

            for (int pkt = 0; pkt < Sizes.AddrBookPackets; pkt++)
            {
                byte[] data = packets[pkt];
                for (int c = 0; c < ContactsPerPacket; c++)
                {
                    int idx = pkt * ContactsPerPacket + c;
                    contacts[idx] = Contact.Decode(data, c * Contact.RecordSize);
                }
            }

            return contacts;
        }

        /// <summary>
        /// Encodes a contact list into the 80-packet byte array.
        /// Null entries produce all-0xFF records (empty contact sentinel).
        /// </summary>
        public static byte[][] Pack(Contact?[] contacts)
        {
            if (contacts is null)
                throw new ArgumentNullException(nameof(contacts));
            if (contacts.Length > MaxContacts)
                throw new ArgumentException(
                    $"Maximum {MaxContacts} contacts, got {contacts.Length}.");

            var packets = new byte[Sizes.AddrBookPackets][];
            for (int pkt = 0; pkt < Sizes.AddrBookPackets; pkt++)
            {
                byte[] data = new byte[Sizes.AddrBookPerPacket];
                Codec.PadFF(data, 0, data.Length);
                packets[pkt] = data;

                for (int c = 0; c < ContactsPerPacket; c++)
                {
                    int idx = pkt * ContactsPerPacket + c;
                    if (idx < contacts.Length && contacts[idx] is not null)
                        contacts[idx]!.Encode(data, c * Contact.RecordSize);
                }
            }

            return packets;
        }

        /// <summary>
        /// Returns only non-null contacts, in order, as a simple list.
        /// </summary>
        public static List<Contact> ToList(Contact?[] contacts)
        {
            var list = new List<Contact>(contacts.Length);
            foreach (var c in contacts)
                if (c is not null) list.Add(c);
            return list;
        }

        private static void ValidatePackets(byte[][] packets)
        {
            if (packets is null || packets.Length != Sizes.AddrBookPackets)
                throw new ArgumentException(
                    $"Expected {Sizes.AddrBookPackets} address book packets, got {packets?.Length ?? 0}.");
            for (int i = 0; i < packets.Length; i++)
            {
                if (packets[i] is null || packets[i].Length != Sizes.AddrBookPerPacket)
                    throw new ArgumentException(
                        $"AddrBook packet[{i}] must be {Sizes.AddrBookPerPacket} bytes.");
            }
        }
    }
}
