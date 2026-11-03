using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PKHeX.Core;

namespace PKForge.Engine.Quetzal;

/// <summary>Explicit, lossy official-format view of a separately retained native record.</summary>
internal sealed record QuetzalConversion(PK9 Pokemon, IReadOnlyList<string> Warnings)
{
    private sealed record SpeciesRow(int National, int[] Stats, int[] Types, int Gender, int[] Abilities);
    private sealed class Tables
    {
        internal readonly SpeciesRow[] Species;
        internal readonly string[] Moves, Items, Abilities;
        internal readonly Dictionary<string, int> OfficialMoves, OfficialItems, OfficialAbilities;
        internal readonly ConcurrentDictionary<int, byte> Forms = new();
        internal Tables()
        {
            using var stream = typeof(QuetzalConversion).Assembly.GetManifestResourceStream("quetzal9.tables.json")!;
            using var doc = JsonDocument.Parse(stream); var root = doc.RootElement;
            static int[] Ints(JsonElement e) => e.EnumerateArray().Select(x => x.GetInt32()).ToArray();
            Species = root.GetProperty("species").EnumerateArray().Select(e => new SpeciesRow(
                e.GetProperty("national").GetInt32(), Ints(e.GetProperty("stats")), Ints(e.GetProperty("types")),
                e.GetProperty("gender").GetInt32(), Ints(e.GetProperty("abilities")))).ToArray();
            static string[] Names(JsonElement e) => e.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            Moves = Names(root.GetProperty("moves")); Items = Names(root.GetProperty("items")); Abilities = Names(root.GetProperty("abilities"));
            static Dictionary<string, int> Index(IEnumerable<(string Name, int Id)> pairs) => pairs
                .Where(x => x.Name.Length != 0).DistinctBy(x => x.Name).ToDictionary(x => x.Name, x => x.Id);
            OfficialMoves = Index(GameInfo.GetStrings("en").movelist.Select((x, i) => (Key(x), i)));
            OfficialItems = Index(GameInfo.GetStrings("en").itemlist.Select((x, i) => (Key(x), i)));
            OfficialAbilities = Index(Enum.GetValues<Ability>().Distinct().Select(x => (Key(x.ToString()), (int)x)));
        }
    }
    private static readonly Lazy<Tables> Data = new(() => new Tables());
    private static string Key(string text) => new(text.Normalize(NormalizationForm.FormD).Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static int Map(string[] table, int native, Dictionary<string, int> official, string kind)
    {
        if (native == 0) return 0;
        if ((uint)native >= table.Length || !official.TryGetValue(Key(table[native]), out int mapped))
            throw new InvalidDataException($"Quetzal {kind} #{native} has no verified official mapping.");
        return mapped;
    }

    internal static QuetzalConversion Create(QuetzalRecord source, ReadOnlySpan<byte> trainerName, byte trainerAppearance, uint trainerId)
    {
        var data = Data.Value; int native = source.NativeSpecies;
        if ((uint)native >= data.Species.Length || data.Species[native].National is < 1 or > 1025)
            throw new InvalidDataException($"Quetzal species #{native} has no verified official mapping. Its native record was not changed.");
        var row = data.Species[native]; ushort species = (ushort)row.National;
        byte form = data.Forms.GetOrAdd(native, _ => ResolveForm(native, species, row, data));
        var warnings = new List<string>
        {
            "The Alpha 9 v1 profile was selected from the save layout; the exact ROM build cannot be confirmed from save data.",
            "Quetzal conversion is approximate. Origin uses Emerald as a source placeholder; met location is unknown. No legal retail encounter is claimed.",
            "Experience and met level are approximated from the source level; stats use official Pokémon data.",
        };
        uint nativePid = source.Party ? source.U32(0) : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(source.Data));
        uint id = source.Party ? source.U32(4) : trainerId;
        string ot = source.Party ? StringConverter3.GetString(source.Data.AsSpan(20, 7), false) : StringConverter3.GetString(trainerName, false);
        // The save's byte 8 can identify an avatar rather than a binary gender.
        // Preserve it in the native capsule; do not infer OT gender from it.
        const bool knownOt = false;
        if (!source.Party) warnings.Add("Box records omit trainer, PID, friendship and PP. The save trainer and generated PID are substitutes; friendship and PP use defaults.");
        if (!knownOt) warnings.Add("The original trainer's gender is unknown and uses a placeholder.");
        int slot = source.AbilitySlot;
        int abilityNative = row.Abilities[slot] == 0 ? row.Abilities[0] : row.Abilities[slot];
        var mappedBall = BallOf(source.NativeBall);
        if (mappedBall.Approximate) warnings.Add($"Native ball #{source.NativeBall} has no supported equivalent and becomes a Great Ball.");
        if (source.Radiant) warnings.Add("Radiant becomes a standard shiny; its Quetzal palette is not retained.");
        var mon = new PK9
        {
            Species = species, Form = form, Version = GameVersion.E, Language = 2,
            Nickname = source.Nickname, OriginalTrainerName = ot, ID32 = id,
            OriginalTrainerGender = 0,
            PID = nativePid, EncryptionConstant = nativePid,
            Nature = (Nature)source.Nature, StatAlignment = (Nature)source.Nature,
            Ability = Map(data.Abilities, abilityNative, data.OfficialAbilities, "ability"), AbilityNumber = 1 << slot,
            HeldItem = Map(data.Items, source.NativeItem, data.OfficialItems, "item"), Ball = mappedBall.Ball,
        };
        mon.OriginalTrainerFriendship = source.Party ? source.Data[0x31] : mon.PersonalInfo.BaseFriendship;
        mon.IsNicknamed = !source.Nickname.Equals(SpeciesName.GetSpeciesNameGeneration(species, 2, 9), StringComparison.OrdinalIgnoreCase);
        bool shiny = source.Shiny || source.Radiant;
        uint low = mon.PID & 65535, xor = (mon.PID >> 16) ^ low ^ mon.TID16 ^ mon.SID16;
        if (shiny != (xor < 16))
        {
            mon.PID = shiny ? low | ((low ^ mon.TID16 ^ mon.SID16) << 16) : mon.PID ^ 0x10000000;
            warnings.Add("PID adjusted to preserve the displayed shiny state.");
        }
        var moves = source.Moves.Select(x => (ushort)Map(data.Moves, x, data.OfficialMoves, "move")).ToArray();
        mon.Moves = moves;
        var ivs = source.IVs; var evs = source.EVs;
        for (int i = 0; i < 6; i++) { mon.SetIV(i, ivs[i]); mon.SetEV(i, evs[i]); }
        var pp = Enumerable.Range(0, 4).Select(i => source.Party ? source.Data[0x3C + i] : MoveInfo.GetPP(EntityContext.Gen9, moves[i])).ToArray();
        (mon.Move1_PP, mon.Move2_PP, mon.Move3_PP, mon.Move4_PP) = (pp[0], pp[1], pp[2], pp[3]);
        mon.Move1_PPUps = source.Party ? source.Data[0x30] & 3 : 0;
        mon.Move2_PPUps = source.Party ? (source.Data[0x30] >> 2) & 3 : 0;
        mon.Move3_PPUps = source.Party ? (source.Data[0x30] >> 4) & 3 : 0;
        mon.Move4_PPUps = source.Party ? (source.Data[0x30] >> 6) & 3 : 0;
        mon.CurrentLevel = (byte)source.Level; mon.ObedienceLevel = (byte)source.Level;
        mon.MetLevel = (byte)source.Level; mon.MetLocation = 0;
        mon.Gender = row.Gender switch { 255 => 2, 254 => 1, 0 => 0, _ => source.Party ? (byte)((nativePid & 255) < row.Gender ? 1 : 0) : (byte)source.Bits(160, 1) };
        mon.ResetPartyStats(); mon.RefreshChecksum();
        return new QuetzalConversion(mon, warnings);
    }

    private static byte ResolveForm(int native, ushort species, SpeciesRow row, Tables data)
    {
        if (native <= 898) return 0;
        static int Type(int t) => t >= 10 ? t - 1 : t;
        var matches = new List<byte>();
        for (byte f = 0; f < Math.Max(1, (int)PersonalTable.SV[species].FormCount); f++)
        {
            var pi = PersonalTable.SV.GetFormEntry(species, f);
            if (new[] { pi.HP, pi.ATK, pi.DEF, pi.SPE, pi.SPA, pi.SPD }.SequenceEqual(row.Stats) &&
                pi.Type1 == Type(row.Types[0]) && pi.Type2 == Type(row.Types[1])) matches.Add(f);
        }
        if (matches.Count > 1)
        {
            var nativeAbilities = row.Abilities.Where(x => x != 0).Select(x => Map(data.Abilities, x, data.OfficialAbilities, "ability")).ToHashSet();
            matches = matches.Where(f => { var pi = PersonalTable.SV.GetFormEntry(species, f); return nativeAbilities.SetEquals(new[] { pi.Ability1, pi.Ability2, pi.AbilityH }.Where(x => x != 0)); }).ToList();
        }
        if (matches.Count != 1) throw new InvalidDataException($"Quetzal species #{native} has no unique verified official form ({matches.Count} matches).");
        return matches[0];
    }

    internal static (byte Ball, bool Approximate) BallOf(int native)
    {
        Ball[] balls = [Ball.Strange, Ball.Poke, Ball.Great, Ball.Ultra, Ball.Master, Ball.Premier, Ball.Heal, Ball.Net, Ball.Nest, Ball.Dive,
            Ball.Dusk, Ball.Timer, Ball.Quick, Ball.Repeat, Ball.Luxury, Ball.Level, Ball.Lure, Ball.Moon, Ball.Friend, Ball.Love, Ball.Fast,
            Ball.Heavy, Ball.Dream, Ball.Safari, Ball.Sport, Ball.Great, Ball.Beast, Ball.Cherish];
        return (uint)native < balls.Length && native != 25 ? ((byte)balls[native], false) : ((byte)Ball.Great, true);
    }
}
