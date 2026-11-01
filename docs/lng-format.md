# Ego Engine `.lng` files (NeFS-contained localisation)

These notes come from reverse-engineering the localisation files of
**DiRT Showdown** (Xbox 360), stored inside `x360_000.nfs`. They are written
down because the `.lng` container is not handled by `NefsLib` itself: it is
just an item inside the archive, and everything below is what the game does
with that item.

## Container

A `.lng` is a little-endian (on 360) structure with six tags:

```
LNGT  header
HSHS  string-hash table
HSHT  hash table
SIDA  key index table      (pairs of u32: key offset, value offset)
SIDB  key byte pool
LNGB  value byte pool
```

Layout observed on DiRT Showdown (`language_eng.lng`, 304153 bytes):

| section | offset  | size     |
|---------|---------|----------|
| LNGT    | 0       | 8        |
| HSHS    | 8       | 20       |
| HSHT    | 28      | 19336    |
| SIDA    | 19364   | 38648    |
| SIDB    | 58024   | 119408   |
| LNGB    | 177440  | 126705   |

- `SIDA` starts with the tag, then a `u32` count, then `count` pairs of
  `u32` offsets. All offsets are relative to the start of the respective
  pool (`SIDB` for keys, `LNGB` for values).
- Strings are UTF-8 and NUL terminated.
- `SIDB`/`LNGB` sizes are declared in their headers. The declared value-pool
  size is a hard limit: you may leave unused space at the end (zeros), but
  you cannot grow it past that value without also fixing the section
  headers, and the block layout that follows.

### Deduplication

The pools are deduplicated: several keys can point at the same value offset.
On DiRT Showdown there are 4831 pairs but only ~3800 unique values, and 439
value offsets are shared by more than one key.

This matters when editing. If a value offset is shared, editing "one key"
edits every key that points to it. To give two keys different text you must
**add a new value** to `LNGB` and repoint one of them, which also means the
pool must be rebuilt and the `SIDA` value offsets rewritten. Rebuilding the
pool in ascending physical offset order keeps compression close to the
original; rebuilding it in key order can cost several kilobytes.

## Packing inside the archive

The `.lng` is stored uncompressed as an item, but the archive itself is split
into fixed 128 KiB chunks (`BlockSize` = `0x20000`) and each chunk is
compressed independently.

On DiRT Showdown the `language_eng.lng` item is split into 3 chunks:

| chunk | uncompressed | compressed slot |
|-------|--------------|-----------------|
| 0     | 131072       | 52403           |
| 1     | 131072       | 40019           |
| 2     | 42009        | 14677           |

Rules that the 360 executable enforces (all verified in-game):

1. **Raw deflate**, no zlib/gzip wrapper.
2. The stream must decompress to exactly the declared uncompressed size.
3. The stream must **end exactly at the end of its slot**. Trailing bytes
   after the deflate final block make the loader reject the chunk
   (`unused_data != 0`). In practice each stream is built to be exactly
   `slot_size` bytes long.
4. Each compressed chunk has a **CRC16 stored in the executable**, in the
   block table. That CRC is computed over the **compressed bytes**, not the
   decompressed data:

   ```
   crc16 = (crc32(compressed_bytes) & 0xFFFF) ^ (crc32(compressed_bytes) >> 16)
   ```

   i.e. the two halves of the CRC32 folded together (sometimes called
   `fold16`). The value must match the one in the executable; the executable
   itself does not need to be patched.

Point 3 and 4 are the two things that usually break translations:
recompressing with a normal zlib call leaves slack at the end of the slot
and changes the CRC. A practical approach is to hard-code the target size and
target CRC and pad the deflate stream itself (for example with an extra
stored block) instead of padding the slot.

## Extracting text

`NeFsTool extract` writes the item as-is. The `.lng` can then be parsed with
any reader that understands the six tags above; no decompression step is
needed because the archive layer already did it.

## Writing text back

Use `NeFsTool patch` or rebuild the chunks yourself:

1. Edit `LNGB` (respecting the declared size) and/or `SIDB`.
2. Rewrite the value offsets in `SIDA`.
3. Split the file into 128 KiB chunks.
4. Compress each chunk with raw deflate to exactly the original slot size.
5. Fix the per-chunk CRC16 in the executable's block table (or reuse the
   original target values).

Never change item offsets/sizes in the archive header if you want to keep the
executable untouched; keep the slot sizes identical instead.
