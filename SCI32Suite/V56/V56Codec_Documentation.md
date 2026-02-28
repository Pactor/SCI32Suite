# V56Codec – Writer/Reader Documentation (SCI32 V56 view resources)

This document describes the **binary layout** used by the current `V56Codec` implementation and the responsibilities of each structure and field. It is **self-contained** and does **not** rely on file-name heuristics.

## High-level body layout

All offsets ending in **Abs** are **absolute from the start of the body**.

Body layout (what your headers’ offsets refer to):

1. **View header** ((fixed) bytes)
2. **Loop headers** (`loopCount * 0x10 (16)` bytes)
3. **Cel headers** (non-mirror loops only; `sum(numCels) * 0x34 (52)` bytes)
4. **Palette chunk** (`0x0300`)
5. **Image chunk** (`0x0400`)
6. **End chunk** (`0x0500`)

---

## Critical offset rules

### Cel header stream offsets are absolute
These are **Abs** (from body start):
- `controlOffsetAbs`
- `dataOffsetAbs`
- `rowTableOffsetAbs`

### Row table entries are relative (this was the “wrong image” bug)
Row table is `h * 8` bytes, entries are:

- `u32 rowControlRel` (relative to `controlOffsetAbs`)
- `u32 rowDataRel` (relative to `dataOffsetAbs`)

Row i control address = `controlOffsetAbs + rowControlRel`  
Row i data address    = `dataOffsetAbs + rowDataRel`

---

## Loop header (0x10 (16) bytes)

Mirror encoding must match what common viewers expect:

| Off | Size | Field | Meaning |
|---:|---:|---|---|
| 0x00 | u8 | mirrorOf | `0xFF` for non-mirror; else target loop index |
| 0x01 | u8 | mirrorFlag | `0` non-mirror; `1` mirror loop |
| 0x02 | u8 | numCels | cel count for non-mirror; `0` for mirror |
| 0x03 | u8 | reserved | typically `0xFF` |
| 0x04 | u32 | sentinel | `0x03FFFFFF` |
| 0x08 | u32 | dataAbs | reserved/unused by this codec (0) |
| 0x0C | u32 | celTableAbs | ABS offset to this loop’s first cel header |

### Mirror loops: monotonic celTableAbs
For mirror loops, many readers derive loop ranges from adjacent `celTableAbs` values.
So mirror loops should get `celTableAbs` patched to the **next non-mirror** loop’s `celTableAbs`,
or to **end-of-cel-headers** if none.

---

## Cel header (0x34 (52) bytes)

Common fields used:

| Off | Size | Field |
|---:|---:|---|
| 0x00 | u16 | w |
| 0x02 | u16 | h |
| 0x04 | s16 | xHot |
| 0x06 | s16 | yHot |
| 0x08 | u8  | transparentIndex |
| 0x18 | u32 | controlOffsetAbs |
| 0x1C | u32 | dataOffsetAbs |
| 0x20 | u32 | rowTableOffsetAbs |

Other bytes are reserved to maintain exact header size.

---

## Palette chunk (tag `0x0300`)
Payload contains **256 entries × 4 bytes**: `[flag, R, G, B]` (plus any required header bytes).
`transparentIndex` in each cel selects which palette entry is treated as transparent during decode.

---

## Image chunk (tag `0x0400`)
Contains:
- Row tables
- Control streams
- Data streams

Offsets in cel headers must point to the correct blocks.

Control/data decode model per row:
- `< 0x40` literals
- `0x40–0x7F` long literals
- `0x80–0xBF` runs of next byte
- `0xC0–0xFF` runs of `transparentIndex`

(Encoder and decoder must match exactly.)

---

## Removing “BMP-only” requirement (documentation + contract)
The codec should not require BMP *files*.

Recommended contract:
- Accept any `Bitmap` / decoded image input.
- Convert internally to:
  - **8bpp indexed** pixel indices (`byte[] w*h`)
  - **256-color palette** (256×4 bytes)
- Then encode.

Disk format (PNG/JPG/BMP) is a pipeline concern, not a codec concern.

---

## Build-time helper: `HeaderPack`
`HeaderPack` is the temporary container used for the two-pass write:
1) Build loop/cel headers with placeholder offsets
2) Emit palette + image sections and compute offsets
3) Patch:
   - Loop `celTableAbs` at `+0x0C`
   - Cel stream offsets
   - Mirror loop `celTableAbs` monotonic rule

---

## Invariants to validate
Writer:
- Exact header sizes (`0x10 (16)`, `0x34 (52)`)
- Row table entries are REL (not ABS)
- Mirror loops: `mirrorFlag=1`, `mirrorOf in range`, `numCels=0`

Reader:
- Chunk tags `0x0300`, `0x0400`, `0x0500`
- Offsets within bounds
- Row table `h*8` within bounds

