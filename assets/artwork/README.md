# Game artwork

The desktop app uses these finished, versioned images:

- `golden-ticket-snowy-twilight-20260914.png`: the game background.
- `characters/character-01-human-256.png` through `character-05-human-256.png` and
  their matching `-robot-256.png` images: ten player portraits.

The desktop project embeds these files as WPF resources. The character files include
the later roster refinements; some differ from the original generated thumbnails.

`avatar-set-*`, original `human-avatar-*`/`robot-avatar-*` images, `*-master.png`
files and `*.prompt.md` files are ignored local generation material. Keep them for
future editing if useful; the app does not load or package them. Promote future
finished artwork into the resource paths above after review.

## Provenance review

The [September 23 artwork audit](../../docs/artwork-audit-2026-09-23.md) inventories the
eleven shipped PNGs and records the project owner's confirmation that Codex generated them
for GoldenTicket without copying third-party artwork. Local generation notes and source
images support the creation and later portrait refinements. The audit is limited to these
game-art files; physical-board imagery, game data and model assets have separate provenance.
Any licensable project-owned rights in these PNGs are offered under the repository's
[PolyForm Noncommercial License 1.0.0](../../LICENSE).
