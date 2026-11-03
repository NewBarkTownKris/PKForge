using System.Buffers.Binary;
using System.Security.Cryptography;
using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine.Quetzal;

/// <summary>
/// Versioned native Bank capsule: context + original 31/104-byte record + SHA-256.
/// Native record bytes are never replaced by the approximate PK9 view.
/// </summary>
internal sealed class QuetzalEntry
{
    internal const string Format = "QZT9";
    internal byte[] Bytes { get; }
    internal QuetzalRecord Record { get; }
    internal QuetzalConversion Conversion { get; }
    internal static bool HasMagic(ReadOnlySpan<byte> bytes) => bytes.StartsWith("QZT9"u8);
    internal static bool IsFormat(string? format) => string.Equals(format, Format, StringComparison.OrdinalIgnoreCase);

    internal QuetzalEntry(QuetzalBattery source, int box, int slot, byte[] sourceHash)
    {
        Record = source.Records[(box, slot)];
        Bytes = new byte[56 + Record.Data.Length + 32];
        "QZT9"u8.CopyTo(Bytes); Bytes[4] = 1; Bytes[5] = Record.Party ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(6), (ushort)Record.Data.Length);
        source.TrainerName.CopyTo(Bytes, 8); Bytes[15] = source.TrainerAppearance;
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(16), source.TrainerId);
        BinaryPrimitives.WriteInt16LittleEndian(Bytes.AsSpan(20), (short)box); Bytes[22] = (byte)slot;
        sourceHash.CopyTo(Bytes, 24); Record.Data.CopyTo(Bytes, 56);
        SHA256.HashData(Bytes.AsSpan(0, Bytes.Length - 32), Bytes.AsSpan(Bytes.Length - 32));
        Conversion = QuetzalConversion.Create(Record, source.TrainerName, source.TrainerAppearance, source.TrainerId);
    }

    internal QuetzalEntry(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is not (119 or 192) || !HasMagic(bytes) || bytes[4] != 1 || bytes[5] > 1 || bytes[23] != 0)
            throw new InvalidDataException("Invalid or unsupported native Quetzal record.");
        int length = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        bool party = bytes[5] == 1; int box = BinaryPrimitives.ReadInt16LittleEndian(bytes[20..]);
        if (length != (party ? 104 : 31) || bytes.Length != 88 + length ||
            (party ? box != -1 || bytes[22] >= 6 : box is < 0 or >= 45 || bytes[22] >= 30) ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes[..^32]), bytes[^32..]))
            throw new InvalidDataException("Native Quetzal record integrity check failed.");
        Bytes = bytes.ToArray(); Record = new QuetzalRecord(bytes.Slice(56, length));
        if (Record.NativeSpecies == 0 || !Record.Plausible || party && (Record.Level is < 1 or > 100 || (Record.Data[0x13] & 3) != 2))
            throw new InvalidDataException("Unsupported data in native Quetzal record.");
        Conversion = QuetzalConversion.Create(Record, bytes.Slice(8, 7), bytes[15], BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]));
    }

    internal BankEntryInfo Describe(string sourceName)
    {
        var mon = Conversion.Pokemon;
        return new BankEntryInfo(mon.Species, mon.Form, mon.IsShiny,
            string.IsNullOrWhiteSpace(mon.Nickname) ? SpeciesName.GetSpeciesName(mon.Species, 2) : mon.Nickname,
            mon.CurrentLevel, 3, sourceName, Format, mon.HeldItem, EntitySprite.Traits(mon));
    }

    internal SlotExport Export()
    {
        var safe = string.Concat(Record.Nickname.Where(char.IsAsciiLetterOrDigit));
        int box = BinaryPrimitives.ReadInt16LittleEndian(Bytes.AsSpan(20));
        string position = box < 0 ? $"P{Bytes[22] + 1:D2}" : $"B{box + 1:D2}S{Bytes[22] + 1:D2}";
        string identity = Convert.ToHexString(Bytes.AsSpan(Bytes.Length - 32, 8));
        return new SlotExport(Bytes.ToArray(), $"{position}-{Record.NativeSpecies:D4}-{safe}-{identity}.qzt9", Format);
    }
}
