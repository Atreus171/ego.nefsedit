# NeFsTool

`nefstool` is a small command-line companion for `VictorBush.Ego.NefsLib`. It
opens NeFS archives with the same reader the GUI uses and exposes the parts
that are useful when you are translating a game: item listing, extraction,
block/transform diagnostics, checksum inspection and a "patch" mode that
writes replacement files back into an existing archive.

It started as a debugging utility while translating DiRT Showdown (Xbox 360)
and is kept here because most of the format knowledge it prints is not
obvious from the archive alone.

## Build

```sh
dotnet build NeFsTool/NeFsTool.csproj -c Release
```

The project references `VictorBush.Ego.NefsLib` with a relative path, so it
builds from a normal clone.

## Usage

```
nefstool list <volume0.nfs> [filter]
nefstool extract <volume0.nfs> <outdir> [filter]
nefstool patch <volume0.nfs> <new_language_eng.lng> <new_language_use.lng> [--apply] [--inplace|--relocate|--relayout] [--origlangs] [--origorder]
nefstool diag <volume0.nfs>
nefstool verify <volume0.nfs>
nefstool diff <fileA> <fileB>
nefstool intro <volume0.nfs>
nefstool headerdiff <fileA> <fileB>
nefstool flip <volume0.nfs> <hexOffset> <hexByte>
nefstool introtweak <volume0.nfs> <dwordIdx> <newHexValue>
nefstool crack <volume0.nfs>
nefstool sweep <volume0.nfs>
nefstool datacrc <volume0.nfs>
nefstool blocksum <volume0.nfs>
```

### Common flows

List the items and their transform type:

```sh
nefstool list x360_000.nfs language_eng
nefstool list x360_000.nfs
```

Extract everything (or a filtered subset) into a folder:

```sh
nefstool extract x360_000.nfs out language_eng
nefstool extract x360_000.nfs out
```

Inspect the compressed blocks and their checksums:

```sh
nefstool diag x360_000.nfs
nefstool verify x360_000.nfs
```

`diag` prints, for every data block, the slot size, the declared uncompressed
size, the zlib/deflate stream properties and the checksum stored in the
executable. `verify` re-reads the archive and checks that what is on disk
really decompresses to the declared data.

### `patch`

`patch` replaces two language files inside the archive by rebuilding the
compressed blocks that contain them. It works on the actual block layout, so
the replacement must fit in the same compressed slot; options:

- `--apply` – write the result (otherwise it only reports).
- `--inplace` – keep every block at its original offset and size.
- `--relocate` – allow blocks to move.
- `--relayout` – rebuild the block table.
- `--origlangs` / `--origorder` – keep the original language files/order as
  the base for the rebuild.

## Notes

- The tool does not patch executables. Checksums that the game expects are
  read from the executable, never written to it.
- See [`docs/lng-format.md`](../docs/lng-format.md) for the `.lng` container
  and the per-block compression/checksum rules used by Ego Engine 360 titles.
