# Quetzal Alpha 9 v1 / 9.1 extraction

Read-only support for the verified English Alpha 9 v1 battery layout: 128 KiB,
two rotated 16-sector slots, and 31-byte compact PC records. Only complete
slots with unique IDs, matching counters/signatures and `0xFF4` checksum windows
are accepted. The newest valid slot wins, with counter wraparound. Logical
blocks are joined before reading party/PC records. Ambiguous and unsupported
38/21-byte codecs fail closed. An empty PC permits party recovery but supplies
no evidence of box capacity, so no invented empty boxes are shown.

Detection reads only the small sector footers. Full mappings are lazy and are
not loaded by ordinary retail save paths. No ROM reading, network dependency,
state decompression, source writer, new navigation page or game assets are added.

## Native Bank records and conversion

The existing storage grid shows approximate PK9 views. Source mutations and
indirect editors are denied by the session, and the safe writer refuses changed
source bytes, including unrestricted restores and accepted layout risks.
Serialization returns the original input unchanged. Trainer, bag and Pokédex
detail menus are hidden because those source fields have not been implemented.

`QZT9` / `.qzt9` stores the exact native 104-byte party or 31-byte PC record,
raw save-trainer context, original position and source SHA-256, protected by a
capsule SHA-256. The native Bank record stays read-only. PK9 is an explicit copy,
with warnings before export or transfer. Neither native capsule nor view is a
legal retail encounter claim.

Capsule version 1, little endian:

| Offset | Value |
| --- | --- |
| 0 | ASCII `QZT9` |
| 4 | Version `1` |
| 5 | `1` party / `0` PC |
| 6 | UInt16 native record length, `104` / `31` |
| 8 | Seven raw trainer-name bytes |
| 15 | Raw trainer appearance byte (not assumed to be binary gender) |
| 16 | UInt32 save trainer ID |
| 20 | Int16 source box (`-1` party) |
| 22 | Byte source slot; byte 23 reserved zero |
| 24 | SHA-256 of the decoded battery save |
| 56 | Unmodified native record |
| 56 + record length | SHA-256 of the preceding capsule bytes |

Box records omit OT, PID, friendship and PP. The view/portable copy uses save
trainer context, a deterministic generated PID and defaults for absent fields.
Source Radiant and shiny bits remain independent in native bytes; portable
Radiant becomes a standard shiny, with a warning. Unmapped/custom balls become
Great Balls, with their native IDs retained in the capsule. Experience/met level
are approximate. Portable origin uses Emerald as a source placeholder and met
location is unknown; no Scarlet encounter is fabricated. Unsupported species,
forms or names refuse conversion rather than substituting an arbitrary Pokémon.

## Provenance

Adapted from the owner's GPLv3 PKHeX-everywhere modifications:

- `src/PKHeX.Web.React/src/pages/load/quetzalSave.ts`
- `src/PKHeX.Everywhere.RomHacks/Quetzal/QuetzalExporter.cs`
- `src/PKHeX.Everywhere.ProfileChecks/QuetzalChecks.cs`
- `tools/generate_quetzal_tables.py`, informed by PokeDaisy's GPLv3 table tools
  ([PokeDaisy](https://github.com/lidor30/pokedaisy)).

The local reference has no Git metadata; no upstream commit is asserted. Tables
contain names and numeric mappings from the pinned owner-supplied ROM, SHA-1
`c108cec750a35775084c0806cc704d8abd1994e7` (English Alpha 9 v1).
`Data/tables.json` is the unchanged reference `quetzal-9-v1.json`, SHA-256
`f73462a88ffe31eef06df2981b3de07acb83c3783dcbb414cb772334fd485e81`.
Species forms reuse the prior port's unique stats/types/ability matching; an
ambiguous form is refused. These mappings are not general version detection.

Meaningful synthetic tests run without player files. Optional fixtures live
under ignored `.local-testdata/quetzal/`; never commit ROMs, saves, exports or
private evidence. The supplied save provides five party and 49 boxed records,
including two Radiant records and nine ball substitutions. Broader revisions,
codecs and game-specific mechanics require separate verification.
