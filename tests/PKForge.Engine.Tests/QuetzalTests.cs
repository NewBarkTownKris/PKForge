using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using PKForge.Domain;
using PKForge.Engine;
using PKForge.Engine.Quetzal;
using PKForge.Infrastructure;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class QuetzalTests
{
    private static readonly SaveEngine Engine = new();

    internal static byte[] Party(bool shiny = false, bool radiant = false, int ball = 1)
    {
        var raw = new byte[104];
        BinaryPrimitives.WriteUInt32LittleEndian(raw, 123);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), 0x11223344);
        raw[0x13] = (byte)(2 | (shiny ? 8 : 0) | (radiant ? 16 : 0));
        StringConverter3.SetString(raw.AsSpan(8, 10), "CHARMANDER", 10, false, StringConverterOption.ClearFF);
        StringConverter3.SetString(raw.AsSpan(20, 7), "TESTER", 7, false, StringConverterOption.ClearFF);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(0x28), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(0x34), 33);
        raw[0x32] = (byte)ball; raw[0x3C] = 35; raw[0x58] = 5;
        return raw;
    }

    internal static void Bits(byte[] raw, int at, int count, uint value)
    {
        for (int i = 0; i < count; i++)
        {
            int p = (at + i) / 8; byte mask = (byte)(1 << ((at + i) % 8));
            raw[p] = (byte)((raw[p] & ~mask) | (((value >> i) & 1) != 0 ? mask : 0));
        }
    }

    internal static byte[] Box(bool shiny = false, bool radiant = false, int ball = 1, int length = 31)
    {
        var raw = new byte[length]; Bits(raw, 0, 11, 4); Bits(raw, 38, 6, (uint)ball);
        Bits(raw, 21, 17, 100); Bits(raw, 44, 1, shiny ? 1u : 0u); Bits(raw, 45, 1, radiant ? 1u : 0u);
        Bits(raw, 48, 10, 33); Bits(raw, 124, 5, 31);
        if (length >= 31) StringConverter3.SetString(raw.AsSpan(21, 10), "CHARMANDER", 10, false, StringConverterOption.ClearFF);
        return raw;
    }

    internal static byte[] Save(int rotation = 0, uint first = 10, uint second = 9, bool boxes = true, int codec = 31)
    {
        var data = new byte[0x20000]; var block = new byte[4 * 3968]; var storage = new byte[11 * 3968];
        block[0x6A4] = 1; Party().CopyTo(block, 0x6A8);
        if (boxes)
            foreach (int i in codec == 31 ? new[] { 0, 1, 91, 1349 } : new[] { 0, 1 })
                Box(length: codec).CopyTo(storage, 0x461 + i * codec);
        for (int half = 0; half < 2; half++)
        for (int id = 0; id < 16; id++)
        {
            int physical = (id + rotation) % 16;
            var sector = data.AsSpan((half * 16 + physical) * 4096, 4096);
            if (id == 0)
            {
                StringConverter3.SetString(sector[..7], "TESTER", 7, false, StringConverterOption.ClearFF);
                BinaryPrimitives.WriteUInt32LittleEndian(sector[10..], 0x11223344);
            }
            else if (id < 5) block.AsSpan((id - 1) * 3968, 3968).CopyTo(sector);
            else storage.AsSpan((id - 5) * 3968, 3968).CopyTo(sector);
            BinaryPrimitives.WriteUInt16LittleEndian(sector[4084..], (ushort)id);
            BinaryPrimitives.WriteUInt32LittleEndian(sector[4088..], 0x08012025);
            BinaryPrimitives.WriteUInt32LittleEndian(sector[4092..], half == 0 ? first : second);
            Rehash(data, half * 16 + physical);
        }
        return data;
    }

    private static void Rehash(byte[] data, int physical)
    {
        var sector = data.AsSpan(physical * 4096, 4096);
        BinaryPrimitives.WriteUInt16LittleEndian(sector[4086..], Checksums.CheckSum32(sector[..4084]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    public void PublicRoutesAgreeAndNativeRecordsCrossSectorBoundaries(int rotation)
    {
        var bytes = Save(rotation); var copy = bytes.ToArray();
        Assert.True(QuetzalBattery.IsCandidate(bytes)); Assert.True(Engine.Validate(bytes));
        Assert.Equal("Quetzal (Alpha 9 v1 profile)", Engine.TryDescribe(bytes)!.GameName);
        using var session = Engine.OpenSession(bytes);
        Assert.Equal(5, session.Snapshot.Slots.Count(s => s.Species is > 0));
        Assert.Equal(1351, session.Snapshot.Slots.Count); // 45 boxes + one party record
        Assert.Equal(3, session.Generation); Assert.NotNull(session.ReadOnlyReason);
        Assert.False(session.SupportsSaveDataDetails);
        Assert.Equal(Engine.Open(bytes).Slots, session.Snapshot.Slots);
        using var explicitRoute = Engine.OpenSession(bytes, null, SaveFormat.Standard);
        Assert.Equal(session.Snapshot.Slots, explicitRoute.Snapshot.Slots);
        Assert.Equal(5, session.Snapshot.Slots.Where(s => s.Species is > 0)
            .Select(s => session.ExportSlot(s.Box, s.Slot).FileName).Distinct(StringComparer.Ordinal).Count());
        Assert.Throws<InvalidDataException>(() => Engine.OpenSession(bytes, null, SaveFormat.RadicalRed));
        foreach (var coordinate in new[] { (0, 0), (0, 1), (3, 1), (44, 29), (-1, 0) })
        {
            var exported = session.ExportSlot(coordinate.Item1, coordinate.Item2);
            Assert.Equal("QZT9", exported.Format); Assert.EndsWith(".qzt9", exported.FileName);
            var entry = new QuetzalEntry(exported.Data);
            Assert.Equal(coordinate.Item1 == -1 ? Party() : Box(), entry.Record.Data);
        }
        Assert.Equal(copy, session.Serialize().ToArray()); Assert.Equal(copy, bytes);
    }

    [Theory]
    [InlineData(uint.MaxValue, 0u, 1)]
    [InlineData(0u, uint.MaxValue, 0)]
    [InlineData(10u, 10u, 0)]
    public void NewestCompleteSlotHandlesCounterWraparound(uint first, uint second, int expected)
    {
        var parsed = new QuetzalBattery(Save(first: first, second: second));
        Assert.Equal(expected, parsed.ActiveSlot);
    }

    [Fact]
    public void DamagedNewestFallsBackButTwoDamagedHalvesNeverUseRetailFallback()
    {
        var bytes = Save(); bytes[4096] ^= 1;
        Assert.Equal(1, new QuetzalBattery(bytes).ActiveSlot);
        bytes[17 * 4096] ^= 1;
        Assert.True(QuetzalBattery.IsCandidate(bytes)); Assert.False(Engine.Validate(bytes));
        Assert.Null(Engine.TryDescribe(bytes)); Assert.NotNull(Engine.GetWriteRestriction(bytes));
        Assert.Throws<InvalidDataException>(() => Engine.OpenSession(bytes));
    }

    [Fact]
    public void RetroArchEnvelopeRemainsReadOnlyAndByteIdentical()
    {
        var raw = Save(); var wrapped = RetroArchSaveContainer.Encode(raw, 16384);
        Assert.True(Engine.Validate(wrapped)); Assert.NotNull(Engine.GetWriteRestriction(wrapped));
        using var session = Engine.OpenSession(wrapped);
        Assert.Equal(wrapped, session.Serialize().ToArray());
        Assert.Equal(5, session.Snapshot.Slots.Count(s => s.Species is > 0));
        var altered = raw.ToArray(); altered[0x20] ^= 1; Rehash(altered, 0);
        Assert.NotNull(Engine.CheckWriteSafety(wrapped, RetroArchSaveContainer.Encode(altered), WriteScope.Everything));
    }

    [Theory]
    [InlineData(38)]
    [InlineData(21)]
    public void AlternateCompactCodecsAreRefused(int stride)
    {
        var bytes = Save(codec: stride);
        Assert.Throws<InvalidDataException>(() => new QuetzalBattery(bytes));
        Assert.False(Engine.Validate(bytes));
    }

    [Fact]
    public void EmptyPcSupportsPartyRecoveryWithoutGuessingCodecOrBoxCapacity()
    {
        using var session = Engine.OpenSession(Save(boxes: false));
        Assert.Single(session.Snapshot.Slots, s => s.Species is > 0);
        Assert.DoesNotContain(session.Snapshot.Slots, s => s.Box >= 0);
        Assert.True(session.HasParty);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.ReadEntity(0, 0));
    }

    [Fact]
    public void AmbiguousPcCodecIsNotGuessedAs31Bytes()
    {
        var data = Save(boxes: false);
        foreach (int half in new[] { 0, 1 })
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan((half * 16 + 5) * 4096 + 0x461), 4);
            Rehash(data, half * 16 + 5);
        }
        Assert.False(Engine.Validate(data));
        Assert.Throws<InvalidDataException>(() => Engine.OpenSession(data));
    }

    [Fact]
    public void RetailEmeraldDoesNotEnterTheQuetzalRoute()
    {
        var raw = new byte[0x20000];
        for (int id = 0; id < 14; id++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(id * 4096 + 4084), (ushort)id);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(id * 4096 + 4088), 0x08012025);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(id * 4096 + 4092), 1);
        }
        raw[0x900] = 1;
        var data = new SAV3E(raw).Write().ToArray();
        Assert.False(QuetzalBattery.IsCandidate(data)); Assert.Null(Engine.GetWriteRestriction(data));
        Assert.True(Engine.Validate(data));
        using var session = Engine.OpenSession(data);
        Assert.Null(session.ReadOnlyReason); Assert.False(session.SupportsPortableExport); Assert.True(session.SupportsSaveDataDetails);
    }

    [Fact]
    public void UnknownNativeSpeciesIsRefusedWithoutGuessingOrAlteringTheRecord()
    {
        var bytes = Box(); Bits(bytes, 0, 11, 1529); var native = new QuetzalRecord(bytes);
        Assert.Throws<InvalidDataException>(() => QuetzalConversion.Create(native, Party().AsSpan(20, 7), 0, 0x11223344));
        Assert.Equal(bytes, native.Data);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ShinyAndRadiantAreIndependentNativeFlags(bool shiny, bool radiant)
    {
        foreach (var raw in new[] { Party(shiny, radiant), Box(shiny, radiant) })
        {
            var native = new QuetzalRecord(raw);
            Assert.Equal(shiny, native.Shiny); Assert.Equal(radiant, native.Radiant);
            var converted = QuetzalConversion.Create(native, Party().AsSpan(20, 7), 0, 0x11223344);
            Assert.Equal(shiny || radiant, converted.Pokemon.IsShiny); Assert.True(converted.Pokemon.ChecksumValid);
            Assert.Equal(radiant, converted.Warnings.Any(s => s.Contains("Radiant", StringComparison.Ordinal)));
            Assert.Equal(raw, native.Data);
        }
    }

    [Theory]
    [InlineData(1, Ball.Poke, false)]
    [InlineData(2, Ball.Great, false)]
    [InlineData(25, Ball.Great, true)]
    [InlineData(63, Ball.Great, true)]
    public void BallSubstitutionIsExplicitAndNeverImpliesRadiant(int nativeBall, Ball expected, bool approximate)
    {
        var raw = new QuetzalRecord(Box(ball: nativeBall));
        var converted = QuetzalConversion.Create(raw, Party().AsSpan(20, 7), 0, 0x11223344);
        Assert.Equal((byte)expected, converted.Pokemon.Ball);
        Assert.Equal(approximate, converted.Warnings.Any(s => s.Contains("Great Ball", StringComparison.Ordinal)));
        Assert.False(converted.Pokemon.IsShiny); Assert.False(raw.Radiant);
        Assert.Equal(0, converted.Pokemon.MetLocation); Assert.Equal(GameVersion.E, converted.Pokemon.Version);
    }

    [Fact]
    public void BankRecordsReopenNativelyAndEditingCannotStripTheirMetadata()
    {
        using var source = Engine.OpenSession(Save());
        foreach (var slot in source.Snapshot.Slots.Where(s => s.Species is > 0))
        {
            var exported = source.ExportSlot(slot.Box, slot.Slot);
            Assert.Equal("QZT9", EntityBytes.Normalize("native.qzt9"));
            Assert.Equal("Quetzal", EntityBytes.RomHackGame(exported.Format));
            Assert.Equal(["QZT9"], EntityBytes.Candidates(exported.Data));
            Assert.Equal(slot.Species, Engine.TryDescribeEntity(exported.Data, "test", exported.Format)!.Species);
            using var bank = Engine.OpenEntitySession(exported.Data, format: exported.Format);
            Assert.NotNull(bank); Assert.NotNull(bank.ReadOnlyReason);
            Assert.Throws<NotSupportedException>(() => bank.ApplyEdit(0, 0, new(Nickname: "LOSS")));
            Assert.Equal(exported.Data, bank.ExportSlot(0, 0).Data);
            Assert.Equal(exported.Data, bank.Serialize().ToArray());
            var portable = bank.ExportPortableSlot(0, 0)!;
            Assert.Equal("PK9", portable.Export.Format); Assert.NotEmpty(portable.Warnings);
            Assert.IsType<PK9>(EntityBytes.Parse(portable.Export.Data, portable.Export.Format));
        }
    }

    [Fact]
    public void CorruptOrUnknownCapsuleVersionsAreNotReadAsRetailPokemon()
    {
        using var source = Engine.OpenSession(Save()); var raw = source.ExportSlot(0, 0).Data;
        foreach (int offset in new[] { 4, 5, 6, 15, 20, 23, 56, raw.Length - 1 })
        {
            var corrupt = raw.ToArray(); corrupt[offset] ^= 0xFF;
            Assert.Null(Engine.TryDescribeEntity(corrupt, "test", "QZT9"));
            Assert.Null(EntityBytes.Parse(corrupt, "QZT9"));
            Assert.Throws<InvalidDataException>(() => Engine.OpenEntitySession(corrupt, format: "QZT9"));
        }
    }

    [Fact]
    public void AllSessionMutationsAndIndirectEditorsRefuse()
    {
        var data = Save(); using var session = Engine.OpenSession(data);
        var methods = typeof(ISaveEngineSession).GetMethods().Where(m => !m.IsSpecialName &&
            !m.Name.StartsWith("Get", StringComparison.Ordinal) && !m.Name.StartsWith("Is", StringComparison.Ordinal) &&
            !m.Name.StartsWith("Read", StringComparison.Ordinal) && !m.Name.StartsWith("Export", StringComparison.Ordinal) &&
            m.Name is not ("Serialize" or "MakeActive" or "Dispose"));
        foreach (var method in methods)
        {
            var args = method.GetParameters().Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
            var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(session, args));
            Assert.IsType<NotSupportedException>(error.InnerException);
            Assert.Equal(data, session.Serialize().ToArray());
        }
        Assert.Throws<NotSupportedException>(() => new LegalizerService().LegalizeSlot(session, 0, 0));
        Assert.Throws<NotSupportedException>(() => MonFieldService.SetPid(session, 0, 0, 1));
        Assert.Throws<NotSupportedException>(() => BulkBoxActions.Heal(session, [(0, 0)]));
    }

    [Fact]
    public void PortableTransfersCarryLossWarningsAndRetainTheNativeBankRecord()
    {
        using var source = Engine.OpenSession(Save()); var native = source.ExportSlot(0, 0);
        var blank = new PK9 { Species = 4 }; blank.RefreshChecksum();
        var data = new byte[blank.SIZE_PARTY]; blank.WriteDecryptedDataParty(data);
        using var target = Engine.OpenEntitySession(data, format: "PK9");
        Assert.NotNull(target);
        var imported = ((SaveEngineSession)target).ImportSlotWithReport(0, 1, native.Data, out var refusal, native.Format);
        Assert.NotNull(imported); Assert.Null(refusal); Assert.NotEmpty(imported.Warnings);
        Assert.Contains(imported.Warnings, s => s.Contains("Box records omit", StringComparison.Ordinal));
        Assert.Equal("PK9", target.ExportSlot(0, 1).Format);
        Assert.Equal(native.Data, source.ExportSlot(0, 0).Data);

        using var scratch = Engine.OpenEntitySession(data, format: "PK9");
        Assert.NotNull(scratch);
        var preview = new TransferPreviewService(new AlwaysLegal()).Preview(scratch, 0, 1, native.Data, native.Format);
        Assert.NotNull(preview); Assert.Equal(TransferLegality.Unknown, preview.Legality);
        Assert.Contains(preview.Warnings, s => s.Contains("Box records omit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SafeWriterAndUnrestrictedRestoreRefuseWithoutBackupOrWrite()
    {
        var data = Save(); var calls = new NoWrites(); var writer = new SafeSaveWriter(Engine, calls, calls);
        var snapshot = Engine.Open(data);
        var candidate = data.ToArray(); candidate[0x20] ^= 1; Rehash(candidate, 0);
        Assert.True(Engine.Validate(candidate));
        writer.ConfirmLayoutRisk("doc");
        await Assert.ThrowsAsync<UnsafeSaveWriteException>(() => writer.WriteAsync("doc", snapshot, candidate).AsTask());
        await Assert.ThrowsAsync<UnsafeSaveWriteException>(() => writer.WriteScopedAsync("doc", snapshot, candidate, WriteScope.Everything).AsTask());
        Assert.False((await writer.WriteAsync("doc", snapshot, data)).Changed);
        Assert.Equal(0, calls.Backups); Assert.Equal(0, calls.Writes);
    }

    [Fact]
    public async Task NativeBankCapsuleAlsoRefusesAnUnrestrictedReplacement()
    {
        using var source = Engine.OpenSession(Save()); var native = source.ExportSlot(0, 0);
        using var bank = Engine.OpenEntitySession(native.Data, format: native.Format);
        Assert.NotNull(bank); Assert.NotNull(Engine.GetWriteRestriction(native.Data));
        var candidate = Save();
        Assert.NotNull(Engine.CheckWriteSafety(native.Data, candidate, WriteScope.Everything));
        var calls = new NoWrites(); var writer = new SafeSaveWriter(Engine, calls, calls);
        await Assert.ThrowsAsync<UnsafeSaveWriteException>(() => writer.WriteScopedAsync("native", bank.Snapshot, candidate, WriteScope.Everything).AsTask());
        Assert.Equal(0, calls.Backups); Assert.Equal(0, calls.Writes);
        Assert.Equal(native.Data, bank.Serialize().ToArray());
    }

    [Fact]
    public void TrainerAppearanceIsRetainedWithoutTreatingAnAvatarIdAsGender()
    {
        var data = Save(); data[8] = 30; Rehash(data, 0);
        using var source = Engine.OpenSession(data);
        var native = source.ExportSlot(0, 0);
        Assert.Equal(30, native.Data[15]);
        var portable = source.ExportPortableSlot(0, 0)!;
        Assert.Contains(portable.Warnings, s => s.Contains("gender is unknown", StringComparison.Ordinal));
        Assert.Equal(data, source.Serialize().ToArray());
    }

    [Fact]
    public void OptionalPrivateSavePreservesAll54NativeRecords()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln"))) directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", "quetzal", "quetzal-9v1.sav");
        if (path is null || !File.Exists(path)) return;
        var original = File.ReadAllBytes(path); var decoded = new QuetzalBattery(original);
        Assert.Equal(5, decoded.PartyCount); Assert.Equal(54, decoded.Records.Count);
        using var source = Engine.OpenSession(original);
        Assert.Equal(54, source.Snapshot.Slots.Count(s => s.Species is > 0));
        int radiants = 0, balls = 0;
        foreach (var (coordinate, record) in decoded.Records)
        {
            var native = source.ExportSlot(coordinate.Box, coordinate.Slot);
            Assert.Equal(record.Data, new QuetzalEntry(native.Data).Record.Data);
            using var bank = Engine.OpenEntitySession(native.Data, format: native.Format);
            Assert.NotNull(bank); Assert.Equal(native.Data, bank.ExportSlot(0, 0).Data);
            var portable = source.ExportPortableSlot(coordinate.Box, coordinate.Slot)!;
            var pk9 = Assert.IsType<PK9>(EntityBytes.Parse(portable.Export.Data, "PK9"));
            Assert.True(pk9.ChecksumValid); Assert.Equal(record.Shiny || record.Radiant, pk9.IsShiny);
            Assert.Equal(0, pk9.MetLocation); Assert.Equal(GameVersion.E, pk9.Version);
            if (record.Radiant) radiants++;
            if (portable.Warnings.Any(s => s.Contains("becomes a Great Ball", StringComparison.Ordinal))) balls++;
        }
        Assert.Equal(2, radiants); Assert.Equal(9, balls);
        Assert.Equal(original, source.Serialize().ToArray()); Assert.Equal(original, File.ReadAllBytes(path));
    }

    private sealed class NoWrites : IBackupService, ISaveFileAccess
    {
        public int Backups { get; private set; } public int Writes { get; private set; }
        public ValueTask<BackupReceipt> CreateAsync(SaveSnapshot source, string? changeDescription = null, string? documentId = null, CancellationToken cancellationToken = default)
        { Backups++; throw new InvalidOperationException(); }
        public ValueTask WriteAtomicallyAsync(string documentId, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        { Writes++; throw new InvalidOperationException(); }
        public ValueTask<IReadOnlyList<BackupInfo>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class AlwaysLegal : ILegalityService
    {
        public LegalityReport Analyze(ISaveEngineSession session, int box, int slot) => new(true, []);
        public IReadOnlyList<SlotLegality> Sweep(ISaveEngineSession session, Action<int, int>? onProgress = null, CancellationToken cancellationToken = default) => [];
    }
}
