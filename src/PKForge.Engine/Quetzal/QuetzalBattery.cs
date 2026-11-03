using System.Buffers.Binary;
using PKHeX.Core;

namespace PKForge.Engine.Quetzal;

/// <summary>Port of PKHeX-everywhere's quetzalSave.ts. Battery saves only.</summary>
internal sealed class QuetzalBattery
{
    internal const int Size = 0x20000;
    internal const string ReadOnlyReason = "Quetzal is read-only. Copy Pokémon to the Bank or export their native records; source edits, deposits and withdrawals are not supported.";
    private const int SectorSize = 4096, PayloadSize = 3968, StorageStart = 0x461;
    private readonly byte[] _original;
    internal int ActiveSlot { get; }
    internal uint Counter { get; }
    internal byte[] TrainerName { get; }
    internal byte TrainerAppearance { get; }
    internal uint TrainerId { get; }
    internal int PartyCount { get; }
    internal int CurrentBox { get; }
    internal int BoxCount { get; }
    internal IReadOnlyDictionary<(int Box, int Slot), QuetzalRecord> Records { get; }

    // Header-only recognition: a complete 16-sector ordering is distinct from
    // retail's 14-sector halves. Corrupt payloads still reach the strict reader.
    internal static bool IsCandidate(ReadOnlySpan<byte> data)
    {
        if (data.Length != Size) return false;
        for (int half = 0; half < 2; half++)
        {
            int mask = 0;
            for (int i = 0; i < 16; i++)
            {
                var sector = data.Slice((half * 16 + i) * SectorSize, SectorSize);
                int id = BinaryPrimitives.ReadUInt16LittleEndian(sector[4084..]);
                if (id < 16 && BinaryPrimitives.ReadUInt32LittleEndian(sector[4088..]) == 0x08012025)
                    mask |= 1 << id;
            }
            if (mask == 0xFFFF) return true;
        }
        return false;
    }

    internal QuetzalBattery(ReadOnlySpan<byte> data)
    {
        if (data.Length != Size) throw new InvalidDataException("Quetzal Alpha 9 v1 requires a 128 KiB battery save.");
        _original = data.ToArray();
        var a = ReadSlot(0); var b = ReadSlot(1);
        var active = a is null ? b : b is null ? a : unchecked((int)(b.Counter - a.Counter)) > 0 ? b : a;
        if (active is null) throw new InvalidDataException("Neither Quetzal 16-sector save slot has valid signatures, counters and checksums.");
        ActiveSlot = active.Half; Counter = active.Counter;
        var trainer = _original.AsSpan(active.Sectors[0], PayloadSize);
        TrainerName = trainer[..7].ToArray(); TrainerAppearance = trainer[8];
        TrainerId = BinaryPrimitives.ReadUInt32LittleEndian(trainer[10..]);
        var block = Join(active.Sectors, 1, 4);
        PartyCount = block[0x6A4];
        if (PartyCount > 6) throw new InvalidDataException("Invalid Quetzal party count.");
        var records = new Dictionary<(int, int), QuetzalRecord>();
        for (int i = 0; i < PartyCount; i++)
        {
            var record = new QuetzalRecord(block.AsSpan(0x6A8 + i * 104, 104));
            if (!record.Plausible || record.NativeSpecies == 0 || record.Level is < 1 or > 100 || (record.Data[0x13] & 3) != 2)
                throw new InvalidDataException($"Invalid Quetzal party record {i + 1}.");
            records[(-1, i)] = record;
        }
        var storage = Join(active.Sectors, 5, 11);
        CurrentBox = storage[0];
        var formats = new[] { (Stride: 38, Boxes: 37), (Stride: 31, Boxes: 45), (Stride: 21, Boxes: 67) }
            .Select(format => Score(storage, format.Stride, format.Boxes)).OrderByDescending(f => f.Score).ToArray();
        if (!formats.All(f => f.Records.Count == 0))
        {
            var best = formats[0];
            if (best.Score <= 0 || best.Score == formats[1].Score || best.Records.Any(r => !r.Record.Plausible))
                throw new InvalidDataException("Quetzal PC layout is ambiguous or contains unsupported records.");
            if (best.Stride != 31) throw new InvalidDataException($"Quetzal's {best.Stride}-byte PC codec is not supported; only the verified 31-byte codec is available.");
            BoxCount = 45;
            if (CurrentBox >= BoxCount) throw new InvalidDataException("Unsupported Quetzal current box.");
            foreach (var (index, record) in best.Records) records[(index / 30, index % 30)] = record;
        }
        Records = records;
    }

    internal byte[] Serialize() => _original.ToArray();
    private sealed record Slot(int Half, uint Counter, int[] Sectors);
    private Slot? ReadSlot(int half)
    {
        var offsets = Enumerable.Repeat(-1, 16).ToArray(); uint? counter = null;
        for (int i = 0; i < 16; i++)
        {
            int offset = (half * 16 + i) * SectorSize;
            var sector = _original.AsSpan(offset, SectorSize);
            int id = BinaryPrimitives.ReadUInt16LittleEndian(sector[4084..]);
            uint current = BinaryPrimitives.ReadUInt32LittleEndian(sector[4092..]);
            if (id >= 16 || offsets[id] >= 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(sector[4088..]) != 0x08012025 ||
                counter.HasValue && counter.Value != current ||
                BinaryPrimitives.ReadUInt16LittleEndian(sector[4086..]) != Checksums.CheckSum32(sector[..4084])) return null;
            offsets[id] = offset; counter = current;
        }
        return new Slot(half, counter!.Value, offsets);
    }

    private byte[] Join(int[] sectors, int start, int count)
    {
        var block = new byte[count * PayloadSize];
        for (int i = 0; i < count; i++) _original.AsSpan(sectors[start + i], PayloadSize).CopyTo(block.AsSpan(i * PayloadSize));
        return block;
    }

    private sealed record Scored(int Stride, int Score, List<(int Index, QuetzalRecord Record)> Records);
    private static Scored Score(byte[] storage, int stride, int boxes)
    {
        var records = new List<(int, QuetzalRecord)>(); int score = 0;
        for (int i = 0; i < boxes * 30; i++)
        {
            var raw = storage.AsSpan(StorageStart + i * stride, stride);
            var record = new QuetzalRecord(raw);
            if (record.NativeSpecies == 0) continue;
            score += record.Plausible ? 1 : -1;
            records.Add((i, record));
        }
        return new Scored(stride, score, records);
    }
}

/// <summary>The native fields, before any official-format approximation.</summary>
internal sealed class QuetzalRecord
{
    internal byte[] Data { get; }
    internal bool Party => Data.Length == 104;
    internal QuetzalRecord(ReadOnlySpan<byte> raw) => Data = raw.ToArray();
    internal uint Bits(int offset, int count)
    {
        uint value = 0;
        for (int i = 0; i < count; i++) value |= (uint)((Data[(offset + i) / 8] >> ((offset + i) % 8)) & 1) << i;
        return value;
    }
    internal ushort NativeSpecies => Party ? U16(0x28) : (ushort)Bits(0, 11);
    internal int NativeItem => Party ? U16(0x2A) : (int)Bits(11, 10);
    internal int NativeBall => Party ? Data[0x32] : (int)Bits(38, 6);
    internal bool Shiny => Party ? (Data[0x13] & 8) != 0 : Bits(44, 1) != 0;
    internal bool Radiant => Party ? (Data[0x13] & 16) != 0 : Bits(45, 1) != 0;
    internal int AbilitySlot => Party ? (int)((U32(0x54) >> 28) & 3) : (int)Bits(166, 2);
    internal int Nature => Party ? (int)((U32(0) & 255) % 25) : (int)Bits(161, 5);
    internal uint Experience => Party ? U32(0x2C) : Bits(21, 17) * 10;
    internal int Level => Party ? Data[0x58] : ApproximateLevel(Experience);
    internal int[] Moves => Party ? Enumerable.Range(0, 4).Select(i => (int)U16(0x34 + 2 * i)).ToArray() : Enumerable.Range(0, 4).Select(i => (int)Bits(48 + i * 10, 10)).ToArray();
    internal int[] IVs => Enumerable.Range(0, 6).Select(i => Party ? (int)((U32(0x50) >> (5 * i)) & 31) : (int)Bits(124 + i * 5, 5)).ToArray();
    internal int[] EVs => Enumerable.Range(0, 6).Select(i => Party ? (int)Data[0x40 + i] : (int)Bits(88 + i * 6, 6) * 4).ToArray();
    internal string Nickname => StringConverter3.GetString(Data.AsSpan(Party ? 8 : 21, Math.Min(10, Data.Length - (Party ? 8 : 21))), false);
    // Keep the original port's proven discriminator separate from mapping bounds.
    // Unsupported codecs must not become a guessed collection of 31-byte records.
    internal bool Plausible => NativeSpecies <= 1528 && (Party || Experience <= 1059860) && Nature <= 24 && AbilitySlot <= 2 && Moves.All(m => m <= 848);
    internal ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(at));
    internal uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(at));
    private static int ApproximateLevel(uint experience)
    {
        int level = 1;
        for (int n = 2; n <= 100; n++) { if (6 * n * n * n / 5 - 15 * n * n + 100 * n - 140 > experience) break; level = n; }
        return level;
    }
}
