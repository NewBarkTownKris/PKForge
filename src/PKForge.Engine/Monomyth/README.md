# Pokémon Monomyth battery-format investigation

This is a work-in-progress sector decoder. Verified nonempty-party samples are
blocked from PKForge's retail save parser to prevent accidental Emerald edits.
It does not yet enable browsing, export, editing, or writing. Recognition is
limited: empty-party saves and damaged or different-version saves can fail the
detector, so this guard must not be treated as protection for every revision.

Evidence: an owner-provided 128 KiB v1.0 `.srm` (SHA-256
`8d1e54de156e3ed64257fc7420f643fac8335cc4fac0928561a834748cdf2c8e`) was
loaded under the owner-provided v1.1 ROM (SHA-256
`8f50f845a2b6328084dc18fce46fd29f526f94ff6e4bebbbc82d578c10417df9`)
in headless mGBA using a copy of the save. The decoded party (six records)
and the entire 33,912-byte PC storage buffer matched the running game's memory.
All six party and 14 occupied PC Pokémon passed the native Pokémon checksum.
This does not establish compatibility with other versions or saves.

The sector envelope has two rotating sets of fourteen 4 KiB sectors. The first
u16 of each section contains the total compressed byte count in its low 15 bits
and whether the first run contains literals in its high bit. Following u16
counts alternate between runs of zero words and literal words. The sector
checksum sums complete u32 words of the compressed data through that byte count
and folds the result to 16 bits. Expanded section sizes are 0xF68 for section
0, 0xF80 for sections 1–12, and 0x878 for section 13. Section 1 contains
100-byte party records beginning at 0x23C; its party-count byte is at 0x239.
Sections 5–13 concatenate into the storage buffer, starting with four metadata
bytes followed by 14 boxes of thirty 80-byte records.

The Pokémon data uses a game-specific XOR key, adapted from the GPLv3
[PokeDaisy implementation](https://github.com/lidor30/pokedaisy/blob/54f4c66f5ef0857192838d877418c7db96ccfa99/app/src/main/kotlin/com/pokedaisy/app/companion/data/Gen3Mon.kt).
Monomyth also renumbers species and includes original species. A valid native
checksum alone is not sufficient to produce a valid retail Pokémon or sprite.

The optional local test takes paths in `PKFORGE_MONOMYTH_TEST_SAVE`,
`PKFORGE_MONOMYTH_TEST_EWRAM`, and `PKFORGE_MONOMYTH_TEST_IWRAM`. These private
files must not be committed or attached to a contribution.
