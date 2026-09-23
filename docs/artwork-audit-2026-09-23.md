# Game artwork provenance review — September 23, 2026

## Scope and conclusion

The desktop app embeds eleven committed game-art PNGs: one snowy railway background and five
human portraits with matching robot portraits. The project owner confirmed that Codex's
built-in image generation created this artwork for GoldenTicket and that no third-party
artwork was copied. This review found a local generation trail for all eleven shipped images
and no visible third-party logo, watermark, board/map reproduction, or recognizable licensed
character in them. The background ticket reads “Golden Ticket.”

This is a provenance and visual review of the shipped game art, not a search of every
published image or a determination of copyright or trademark rights. The physical board,
game rules/data, camera photos, documentation screenshots, and ML models are outside this
artwork review.

## Generation trail

- Ignored local prompt records date the background, initial portraits and human-to-robot edits
  to September 14, 2026 and identify Codex's built-in image generator. The references named
  in those records are earlier project-generated portraits, not external artwork.
- The four shipped character 01–02 thumbnails are byte-identical to retained local generated
  thumbnails. The six shipped character 03–05 thumbnails come from later Codex-generated
  source images. Resampling those sources to 256 × 256 with the recorded bicubic method gave
  zero difference across sampled RGB pixels against each corresponding shipped image. An
  unused character 05 robot variant was excluded from the shipped set.
- The generated masters and prompts are ignored local working material and are not included
  in the repository or app package. The later 03–05 refinement prompts are not preserved in
  the artwork folder; their source PNGs remain in the local Codex generation output.
- The retained generated masters and background contain metadata identifying OpenAI image
  generation. The 256-pixel portrait exports do not retain that metadata. Its signature was
  not independently validated, so the owner statement, generation notes and source-image
  comparisons are the principal evidence here.

The PNGs are embedded as WPF resources by
[`GoldenTicket.Desktop.csproj`](../src/GoldenTicket.Desktop/GoldenTicket.Desktop.csproj).
The Git commits introducing and refining them are `f7dbb11` and `f6ed58d`.

## Shipped file manifest

SHA-256 identifies the exact files reviewed. All paths are under `assets/artwork/`.

| File | SHA-256 |
|---|---|
| `golden-ticket-snowy-twilight-20260914.png` | `DB2A501C67B4071D1E2C23A957AC8440B3F963042C4F750E12FFF47B3CBA6342` |
| `characters/character-01-human-256.png` | `B52298E683B0A306D5587A0AD9028C5E7D63AE77C37C46E5D0EEF12C5E4AD8BF` |
| `characters/character-01-robot-256.png` | `74B2A733B1D838F611E42518BACA0B687178F2E6B689E4F15C0A75742E4DB895` |
| `characters/character-02-human-256.png` | `03A99225B42630A9A874003780AD2B4D271677AC698B1E13B572EDE5F0295E89` |
| `characters/character-02-robot-256.png` | `93BA5739C35958151195D5776740FFCEC56CC4CBC08A501437E6D4C251E4D80C` |
| `characters/character-03-human-256.png` | `3CB1DAA6E3D64002F123E38097C9002F15DA4B903CEAC563AE6C1347AD5AA5AF` |
| `characters/character-03-robot-256.png` | `709D97B1EF988A916F8CBCFE2D7547E9F7D02FF897D1F66F6036AF7D1FCE1624` |
| `characters/character-04-human-256.png` | `4C4786C8D4FEB33754B48A8ED6C0834C117BBB2155A1E95D05AE0C233B4A1DD8` |
| `characters/character-04-robot-256.png` | `80D90C1A23FA9E9E643908E426F54F784E192C3C01A0B9FC79D24668DEBADD1A` |
| `characters/character-05-human-256.png` | `AD37108797F779240D04C0C044E7DAFF9CF5EB76D3CA83F3D8578BBEF92719D9` |
| `characters/character-05-robot-256.png` | `11EF99748C32786120008EE9E91606E63A09B3B30C81CB48E31B4F4B76209E22` |

## Licensing boundary

Any licensable project-owned rights in these PNGs are offered under the repository's
[PolyForm Noncommercial License 1.0.0](../LICENSE). AI generation and absence of apparent
copying do not by themselves establish exclusive copyright in each output. The
[U.S. Copyright Office's AI authorship guidance](https://www.copyright.gov/newsnet/2025/1060.html)
assesses protection for human expressive contributions case by case. This review makes no
claim that a license can grant rights in material outside the project's ownership.
