using System.Buffers.Binary;
using PKForge.Engine.Monomyth;
using PKForge.Domain;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class MonomythBatteryTests
{
    private static readonly int[] Sizes = [0xF68, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0x878];

    [Fact]
    public void OptionalRealSaveMatchesTheGamesLiveParty()
    {
        var path = Environment.GetEnvironmentVariable("PKFORGE_MONOMYTH_TEST_SAVE");
        var memoryPath = Environment.GetEnvironmentVariable("PKFORGE_MONOMYTH_TEST_EWRAM");
        var internalMemoryPath = Environment.GetEnvironmentVariable("PKFORGE_MONOMYTH_TEST_IWRAM");
        if (path is null || memoryPath is null || internalMemoryPath is null) return;
        var input = File.ReadAllBytes(path);
        Assert.True(MonomythBattery.TryOpen(input, out var save));
        Assert.NotNull(save);
        var memory = File.ReadAllBytes(memoryPath);
        Assert.Equal(memory[0x25905], save.PartyCount);
        for (var i = 0; i < save.PartyCount; i++)
        {
            Assert.True(save.Party(i).SequenceEqual(memory.AsSpan(0x25908 + 100 * i, 100)));
            var decoded = MonomythBattery.DecodePokemon(save.Party(i));
            Assert.True(decoded.ChecksumValid);
            Assert.NotEqual(0, decoded.SpeciesInternal);
        }
        var internalMemory = File.ReadAllBytes(internalMemoryPath);
        var storageAddress = BinaryPrimitives.ReadUInt32LittleEndian(internalMemory.AsSpan(0x1248));
        var storage = save.Storage();
        Assert.True(storage.AsSpan().SequenceEqual(memory.AsSpan(checked((int)(storageAddress - 0x02000000)), storage.Length)));
        var occupied = 0;
        for (var box = 0; box < 14; box++)
        for (var slot = 0; slot < 30; slot++)
        {
            var record = save.BoxPokemon(box, slot);
            if (record.All(b => b == 0)) continue;
            var decoded = MonomythBattery.DecodePokemon(record);
            Assert.True(decoded.ChecksumValid);
            Assert.NotEqual(0, decoded.SpeciesInternal);
            occupied++;
        }
        Assert.True(occupied > 0);
        Assert.True(input.AsSpan().SequenceEqual(File.ReadAllBytes(path)));
    }

    [Fact]
    public void OpensACompleteSlotAndKeepsItsNativePartyBytes()
    {
        var bytes = Image();
        Assert.True(MonomythBattery.TryOpen(bytes, out var save));
        Assert.NotNull(save);
        Assert.Equal(1, save.PartyCount);
        Assert.True(MonomythBattery.ValidPokemon(save.Party(0)));
        Assert.Equal(8 * 0xF80 + 0x878, save.Storage().Length);
    }

    [Fact]
    public void RejectsCorruptionAndImpossibleCompression()
    {
        var bytes = Image();
        bytes[0x1000 + 12] ^= 1;
        Assert.False(MonomythBattery.TryOpen(bytes, out _));
        bytes = Image();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 0x7FFF);
        SetChecksum(bytes.AsSpan(0, 0x1000));
        Assert.False(MonomythBattery.TryOpen(bytes, out _));
    }

    [Fact]
    public void RejectsAlteredPokemonEvenWhenSectorChecksumIsRepaired()
    {
        var bytes = Image();
        bytes[0x1000 + 4 + 0x23C + 32] ^= 1;
        SetChecksum(bytes.AsSpan(0x1000, 0x1000));
        Assert.False(MonomythBattery.TryOpen(bytes, out _));
    }

    [Fact]
    public void ExistingEmeraldRouteRefusesTheVerifiedLayout()
    {
        var bytes = Image();
        var engine = new SaveEngine();
        Assert.False(engine.Validate(bytes));
        Assert.Throws<InvalidDataException>(() => engine.OpenSession(bytes));
        Assert.Equal(LayoutRiskKind.CorruptingLayout, engine.AssessLayoutRisk(bytes)!.Kind);
        var candidate = bytes.ToArray();
        candidate[100] ^= 1;
        Assert.NotNull(engine.CheckWriteSafety(bytes, candidate, new WriteScope([], Unrestricted: true)));
    }

    [Fact]
    public void UsesTheOlderCompleteSlotWhenTheNewerOneIsDamaged()
    {
        var bytes = Image();
        bytes.AsSpan(0, 14 * 0x1000).CopyTo(bytes.AsSpan(14 * 0x1000));
        for (var i = 14; i < 28; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 0x1000 + 0xFFC), 2);
        bytes[14 * 0x1000 + 2] ^= 1;
        Assert.True(MonomythBattery.TryOpen(bytes, out var save));
        Assert.Equal(1, save!.PartyCount);
    }

    private static byte[] Image()
    {
        var result = new byte[0x20000];
        for (var id = 0; id < 14; id++)
        {
            var sector = result.AsSpan(id * 0x1000, 0x1000);
            var size = Sizes[id];
            BinaryPrimitives.WriteUInt16LittleEndian(sector, (ushort)(id == 1 ? 0x8000 | (size + 4) : 4));
            BinaryPrimitives.WriteUInt16LittleEndian(sector[2..], (ushort)(size / 2));
            if (id == 1)
            {
                var data = sector[4..];
                data[0x239] = 1;
                var mon = data[0x23C..];
                BinaryPrimitives.WriteUInt32LittleEndian(mon, 0x12345678);
                BinaryPrimitives.WriteUInt32LittleEndian(mon[4..], 0x12349999);
                var key = MonomythBattery.Key(0x12345678, 0x12349999);
                for (var i = 32; i < 80; i += 4) BinaryPrimitives.WriteUInt32LittleEndian(mon[i..], key);
            }
            BinaryPrimitives.WriteUInt16LittleEndian(sector[0xFF4..], (ushort)id);
            BinaryPrimitives.WriteUInt32LittleEndian(sector[0xFF8..], 0x08012025);
            BinaryPrimitives.WriteUInt32LittleEndian(sector[0xFFC..], 1);
            SetChecksum(sector);
        }
        return result;
    }

    private static void SetChecksum(Span<byte> sector)
    {
        var length = BinaryPrimitives.ReadUInt16LittleEndian(sector) & 0x7FFF;
        uint sum = 0;
        for (var i = 0; i + 4 <= length; i += 4)
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32LittleEndian(sector[i..]));
        BinaryPrimitives.WriteUInt16LittleEndian(sector[0xFF6..], (ushort)((sum & 0xFFFF) + (sum >> 16)));
    }
}
