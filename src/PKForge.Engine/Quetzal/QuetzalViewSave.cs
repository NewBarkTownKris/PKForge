using System.Security.Cryptography;
using PKHeX.Core;

namespace PKForge.Engine.Quetzal;

/// <summary>
/// Read-only PK9 display buffers; the native battery/capsule is the source of truth.
/// This class has no save writer. Source export returns a native QuetzalEntry.
/// </summary>
internal sealed class QuetzalViewSave : SaveFile, IBoxDetailName
{
    private readonly Memory<byte> _boxes, _party;
    private readonly Dictionary<(int Box, int Slot), QuetzalEntry> _entries = [];
    private readonly int _boxCount;
    private static readonly PK9 Limits = new();
    private static readonly ushort[] Held = ItemStorage9SV.GetAllHeld();
    internal byte[] NativeBytes { get; }
    internal const string ViewReason = QuetzalBattery.ReadOnlyReason + " The Alpha 9 v1 profile is a layout match; the ROM build is unconfirmed. Displayed stats and boxed trainer/PID fields are approximate; Radiant is shown as shiny. Native exports retain the original data.";

    internal QuetzalViewSave(byte[] data) : base(data.ToArray())
    {
        NativeBytes = data.ToArray();
        var source = new QuetzalBattery(data);
        _boxCount = source.BoxCount; Party = 0; Box = 0; PartyCount = source.PartyCount; CurrentBox = source.CurrentBox;
        _boxes = new byte[BoxCount * 30 * SIZE_STORED]; _party = new byte[6 * SIZE_PARTY];
        OT = StringConverter3.GetString(source.TrainerName, false); Gender = source.TrainerAppearance <= 1 ? source.TrainerAppearance : (byte)0; ID32 = source.TrainerId;
        var hash = SHA256.HashData(data);
        foreach (var (coordinate, _) in source.Records)
            Add(coordinate, new QuetzalEntry(source, coordinate.Box, coordinate.Slot, hash));
    }

    internal QuetzalViewSave(QuetzalEntry entry) : base(entry.Bytes.ToArray())
    {
        NativeBytes = entry.Bytes.ToArray(); _boxCount = 1; Box = 0;
        _boxes = new byte[30 * SIZE_STORED]; _party = new byte[6 * SIZE_PARTY];
        Add((0, 0), entry);
    }

    private void Add((int Box, int Slot) coordinate, QuetzalEntry entry)
    {
        _entries[coordinate] = entry;
        var mon = entry.Conversion.Pokemon;
        if (coordinate.Box == -1) mon.WriteEncryptedDataParty(_party.Span.Slice(coordinate.Slot * SIZE_PARTY, SIZE_PARTY));
        else mon.WriteEncryptedDataStored(_boxes.Span.Slice((coordinate.Box * 30 + coordinate.Slot) * SIZE_STORED, SIZE_STORED));
    }

    internal QuetzalEntry Entry(int box, int slot) => _entries.TryGetValue((box, slot), out var entry)
        ? entry : throw new InvalidOperationException("That Quetzal slot is empty.");
    public override string Extension => ".sav";
    protected override string ShortSummary => "Quetzal Alpha 9 v1 (read-only)";
    protected override SaveFile CloneInternal() => throw new NotSupportedException(ViewReason);
    protected override Memory<byte> GetFinalData() => throw new NotSupportedException(ViewReason);
    protected override void SetChecksums() => throw new NotSupportedException(ViewReason);
    public override bool ChecksumsValid => true; // native input was fully checked before display construction
    public override string ChecksumInfo => "Validated native Quetzal input; display buffers are not a save file.";
    public override GameVersion Version { get => GameVersion.SL; set { } }
    public override byte Generation => 9;
    public override EntityContext Context => EntityContext.Gen9;
    public override int Language { get => 2; set { } }
    public override IPersonalTable Personal => PersonalTable.SV;
    public override int MaxStringLengthTrainer => 7;
    public override int MaxStringLengthNickname => 10;
    public override ushort MaxMoveID => Limits.MaxMoveID;
    public override ushort MaxSpeciesID => Limits.MaxSpeciesID;
    public override int MaxAbilityID => Limits.MaxAbilityID;
    public override int MaxItemID => Limits.MaxItemID;
    public override int MaxBallID => Limits.MaxBallID;
    public override GameVersion MaxGameID => GameVersion.SL;
    public override int MaxEV => 252;
    public override ReadOnlySpan<ushort> HeldItems => Held;
    public override int BoxCount => _boxCount;
    public override ushort TID16 { get => (ushort)ID32; set => ID32 = (ID32 & 0xFFFF0000) | value; }
    public override ushort SID16 { get => (ushort)(ID32 >> 16); set => ID32 = (ID32 & 65535) | ((uint)value << 16); }
    protected override Span<byte> BoxBuffer => _boxes.Span;
    protected override Span<byte> PartyBuffer => _party.Span;
    public override int GetBoxOffset(int box) => box * 30 * SIZE_STORED;
    public override int GetPartyOffset(int slot) => slot * SIZE_PARTY;
    public override Type PKMType => typeof(PK9);
    public override PKM BlankPKM => new PK9();
    public override int SIZE_STORED => Limits.SIZE_STORED;
    public override int SIZE_PARTY => Limits.SIZE_PARTY;
    protected override PKM GetPKM(Memory<byte> data) => new PK9(data);
    protected override void DecryptPKM(Span<byte> data) => PokeCrypto.Decrypt8(data);
    public override string GetString(ReadOnlySpan<byte> data) => StringConverter8.GetString(data);
    public override int LoadString(ReadOnlySpan<byte> data, Span<char> text) => StringConverter8.LoadString(data, text);
    public override int SetString(Span<byte> data, ReadOnlySpan<char> text, int maxLength, StringConverterOption option) => throw new NotSupportedException(ViewReason);
    public string GetBoxName(int box) => $"Box {box + 1}";
    public void SetBoxName(int box, ReadOnlySpan<char> name) => throw new NotSupportedException(ViewReason);
}
