using System.Buffers.Binary;
using System.Numerics;
using PKHeX.Core;

namespace PKForge.Engine.Monomyth;

/// <summary>
/// Reader for the compressed sector stream observed in a Pokémon Monomyth v1.0
/// battery save, independently checked against live memory under v1.1. It does
/// not identify a ROM version or write saves.
/// </summary>
internal sealed class MonomythBattery
{
    internal const string UnsupportedReason = "This appears to be a Pokémon Monomyth save. Its compressed save and Pokémon data cannot safely be edited as Emerald; Monomyth support is still in development. The file was not changed.";
    private const int SectorSize = 0x1000, DataSize = 0xF80, SectionCount = 14;
    private const uint Signature = 0x08012025;
    private static readonly int[] ExpandedSizes = [0xF68, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0x878];
    private readonly byte[][] _sections;
    private byte[]? _storage;

    private MonomythBattery(byte[][] sections) => _sections = sections;
    internal int PartyCount => _sections[1][0x239];
    internal ReadOnlySpan<byte> Party(int slot) => _sections[1].AsSpan(0x23C + slot * 100, 100);

    internal byte[] Storage() => GetStorage().ToArray();

    private byte[] GetStorage()
    {
        if (_storage is not null) return _storage;
        var result = new byte[8 * DataSize + ExpandedSizes[13]];
        var pos = 0;
        for (var i = 5; i < SectionCount; i++)
        {
            _sections[i].CopyTo(result, pos);
            pos += _sections[i].Length;
        }
        return _storage = result;
    }

    internal byte[] BoxPokemon(int box, int slot)
    {
        if ((uint)box >= 14 || (uint)slot >= 30) throw new ArgumentOutOfRangeException();
        return GetStorage().AsSpan(4 + (box * 30 + slot) * 80, 80).ToArray();
    }

    /// <summary>
    /// Decode the common Gen 3 structure without converting game-specific IDs.
    /// This intermediate must never be presented or exported as a retail PK3.
    /// </summary>
    internal static PK3 DecodePokemon(ReadOnlySpan<byte> native)
    {
        if (!ValidPokemon(native)) throw new InvalidDataException("Invalid Monomyth Pokémon checksum.");
        var bytes = native.ToArray();
        var personality = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var trainer = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        var delta = Key(personality, trainer) ^ personality ^ trainer;
        for (var i = 32; i < 80; i += 4)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i), word ^ delta);
        }
        PokeCrypto.Decrypt3(bytes);
        return new PK3(bytes);
    }

    internal static bool TryOpen(ReadOnlySpan<byte> data, out MonomythBattery? save)
    {
        save = null;
        if (data.Length != 0x20000) return false;
        var a = TrySlot(data[..(14 * SectorSize)], out var first, out var countA);
        var b = TrySlot(data.Slice(14 * SectorSize, 14 * SectorSize), out var second, out var countB);
        if (!a && !b) return false;
        var chosen = b && (!a || unchecked((int)(countB - countA)) > 0) ? second! : first!;
        save = new MonomythBattery(chosen);
        return true;
    }

    private static bool TrySlot(ReadOnlySpan<byte> slot, out byte[][]? sections, out uint counter)
    {
        sections = null;
        counter = 0;
        var found = new byte[SectionCount][];
        uint? sequence = null;
        for (var i = 0; i < SectionCount; i++)
        {
            var sector = slot.Slice(i * SectorSize, SectorSize);
            var id = BinaryPrimitives.ReadUInt16LittleEndian(sector[0xFF4..]);
            var checksum = BinaryPrimitives.ReadUInt16LittleEndian(sector[0xFF6..]);
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(sector[0xFF8..]);
            var current = BinaryPrimitives.ReadUInt32LittleEndian(sector[0xFFC..]);
            if (id >= SectionCount || found[id] is not null || signature != Signature || (sequence is not null && sequence != current)) return false;
            sequence = current;
            var header = BinaryPrimitives.ReadUInt16LittleEndian(sector);
            var length = header & 0x7FFF;
            if (length < 4 || length > 0xFF4 || (length & 1) != 0 || Checksum(sector, length) != checksum) return false;
            var output = new byte[ExpandedSizes[id]];
            if (!Expand(sector[..length], output, (header & 0x8000) != 0)) return false;
            found[id] = output;
        }
        var partyCount = found[1][0x239];
        if (partyCount is 0 or > 6) return false;
        for (var i = 0; i < partyCount; i++)
            if (!ValidPokemon(found[1].AsSpan(0x23C + i * 100, 100))) return false;
        sections = found;
        counter = sequence!.Value;
        return true;
    }

    private static ushort Checksum(ReadOnlySpan<byte> data, int length)
    {
        uint sum = 0;
        for (var i = 0; i + 4 <= length; i += 4)
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32LittleEndian(data[i..]));
        return (ushort)((sum & 0xFFFF) + (sum >> 16));
    }

    private static bool Expand(ReadOnlySpan<byte> input, Span<byte> output, bool literal)
    {
        var pos = 2;
        var written = 0;
        while (pos < input.Length)
        {
            if (pos + 2 > input.Length) return false;
            var bytes = BinaryPrimitives.ReadUInt16LittleEndian(input[pos..]) * 2;
            pos += 2;
            if (bytes > output.Length - written || (literal && bytes > input.Length - pos)) return false;
            if (literal)
            {
                input.Slice(pos, bytes).CopyTo(output[written..]);
                pos += bytes;
            }
            written += bytes;
            literal = !literal;
        }
        return pos == input.Length && written == output.Length;
    }

    // PokeDaisy Gen3Mon.kt, GPLv3, commit 54f4c66f5ef0857192838d877418c7db96ccfa99.
    internal static uint Key(uint personality, uint trainer)
    {
        var value = unchecked(trainer + BitOperations.RotateRight(personality ^ 0x690u, 7));
        value = BitOperations.RotateRight(unchecked(value + BitOperations.RotateRight(personality, 11)), 13);
        value = unchecked(personality + BitOperations.RotateRight(0x5A000000u ^ value ^ trainer, 17));
        return value ^ BitOperations.RotateRight(trainer, 5);
    }

    internal static bool ValidPokemon(ReadOnlySpan<byte> mon)
    {
        if (mon.Length < 80) return false;
        var personality = BinaryPrimitives.ReadUInt32LittleEndian(mon);
        var trainer = BinaryPrimitives.ReadUInt32LittleEndian(mon[4..]);
        var key = Key(personality, trainer);
        uint sum = 0;
        for (var i = 32; i < 80; i += 4)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(mon[i..]) ^ key;
            sum += (word & 0xFFFF) + (word >> 16);
        }
        return (ushort)sum == BinaryPrimitives.ReadUInt16LittleEndian(mon[28..]);
    }
}
