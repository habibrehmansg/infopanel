"""Makes the bundled "Inter Tabular" faces from the bundled Inter 4.001 faces.

Usage: python make_inter_tabular.py <folder with Inter-*.ttf> <new or empty output folder>

Each face is Inter's, with two changes and nothing else:

- the character map: U+0030 to U+0039 map to Inter's own tabular figures (zero.tf ... nine.tf)
  in every cmap subtable that maps them, so the ten digits share one advance, shaped or not;
- the name table: the family is "Inter Tabular" (name ids 1, 4 and 6), the unique id (3) is its
  own and the version string (5) says it is modified. The copyright (0), the licence URL (14)
  and every other record stay as Inter's.

Every other table is copied as Inter has it, byte for byte; the head table's modified time is
the input's own (no time of now), so two runs give the same bytes. The script checks all of
this on its own output before it ends, and prints the SHA-256 of every input and output.
"""

import hashlib
import os
import sys

from fontTools.ttLib import TTFont

FACES = ["Regular", "Bold", "Italic", "BoldItalic"]
DIGITS = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"]
CODE_POINTS = range(0x30, 0x3A)
FAMILY = "Inter Tabular"
POSTSCRIPT_FAMILY = "InterTabular"
VERSION_SUFFIX = "; tabular default"
# The name ids the script rewrites; any other record must be one it leaves alone.
REWRITTEN = {1, 3, 4, 5, 6}
# Records a face might carry that would also name the family: the script refuses rather than guess.
UNEXPECTED = {16, 17, 18, 21, 22, 25}


def sha256(path):
    with open(path, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()


def rename(record, face):
    text = record.toUnicode()
    nid = record.nameID
    if nid == 1:
        if text != "Inter":
            raise SystemExit("name id 1 of %s is %r, not 'Inter'" % (face, text))
        return FAMILY
    if nid == 3:
        version = text.split(";")[0]
        return "%s;%s-%s;tabular default" % (version, POSTSCRIPT_FAMILY, face)
    if nid == 4:
        if not text.startswith("Inter "):
            raise SystemExit("name id 4 of %s is %r" % (face, text))
        return FAMILY + text[len("Inter"):]
    if nid == 5:
        return text + VERSION_SUFFIX
    if nid == 6:
        if text != "Inter-" + face:
            raise SystemExit("name id 6 of %s is %r" % (face, text))
        return "%s-%s" % (POSTSCRIPT_FAMILY, face)
    raise AssertionError(nid)


def make(source, target, face):
    font = TTFont(source, recalcTimestamp=False, recalcBBoxes=False)
    glyphs = set(font.getGlyphOrder())
    tabular = [d + ".tf" for d in DIGITS]
    missing = [g for g in tabular if g not in glyphs]
    if missing:
        raise SystemExit("%s lacks %s" % (face, missing))

    remapped = 0
    seen = set()
    for table in font["cmap"].tables:
        mapping = getattr(table, "cmap", None)
        # Subtables at one offset (the Unicode and Windows ones) share one mapping when read.
        if not mapping or id(mapping) in seen:
            continue
        seen.add(id(mapping))
        if any(cp in mapping for cp in CODE_POINTS):
            for cp, glyph in zip(CODE_POINTS, tabular):
                if mapping.get(cp) != DIGITS[cp - 0x30]:
                    raise SystemExit("%s: U+%04X maps to %r" % (face, cp, mapping.get(cp)))
                mapping[cp] = glyph
            remapped += 1
    if remapped == 0:
        raise SystemExit("%s: no cmap subtable maps the digits" % face)

    names = font["name"]
    ids = {r.nameID for r in names.names}
    if ids & UNEXPECTED:
        raise SystemExit("%s carries name ids %s, which this script doesn't rewrite" % (face, sorted(ids & UNEXPECTED)))
    for record in names.names:
        if record.nameID in REWRITTEN:
            record.string = rename(record, face)

    font.save(target)
    font.close()


def raw_tables(path):
    font = TTFont(path, lazy=True)
    tables = {tag: font.reader[tag] for tag in font.reader.keys()}
    font.close()
    return tables


def check(source, target):
    """Everything but the character map's ten digits, the name table and the head checksum is Inter's."""
    before, after = raw_tables(source), raw_tables(target)
    if set(before) != set(after):
        raise SystemExit("%s: tables differ: %s" % (target, sorted(set(before) ^ set(after))))
    for tag in before:
        if tag in ("cmap", "name"):
            continue
        a, b = before[tag], after[tag]
        if tag == "head":
            a, b = a[:8] + a[12:], b[:8] + b[12:]  # checksumAdjustment is the file's checksum
        if a != b:
            raise SystemExit("%s: table %r changed" % (target, tag))
    old, new = TTFont(source), TTFont(target)
    old_cmaps = {(t.platformID, t.platEncID, t.format): t for t in old["cmap"].tables}
    for table in new["cmap"].tables:
        theirs = old_cmaps[(table.platformID, table.platEncID, table.format)]
        if table.format == 14:
            if table.uvsDict != theirs.uvsDict:
                raise SystemExit("%s: variation sequences changed" % target)
            continue
        expected = dict(theirs.cmap)
        for cp in CODE_POINTS:
            if cp in expected:
                expected[cp] = DIGITS[cp - 0x30] + ".tf"
        if dict(table.cmap) != expected:
            raise SystemExit("%s: cmap %d/%d changed beyond the digits" % (target, table.platformID, table.platEncID))
    old_names = {(r.nameID, r.platformID, r.platEncID, r.langID): r.toUnicode() for r in old["name"].names}
    new_names = {(r.nameID, r.platformID, r.platEncID, r.langID): r.toUnicode() for r in new["name"].names}
    if set(old_names) != set(new_names):
        raise SystemExit("%s: name records added or removed" % target)
    for key, text in old_names.items():
        if key[0] not in REWRITTEN and new_names[key] != text:
            raise SystemExit("%s: name id %d changed" % (target, key[0]))


def main():
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    source_dir, target_dir = sys.argv[1], sys.argv[2]
    os.makedirs(target_dir, exist_ok=True)
    if os.listdir(target_dir):
        raise SystemExit("%s is not empty" % target_dir)
    for face in FACES:
        source = os.path.join(source_dir, "Inter-%s.ttf" % face)
        target = os.path.join(target_dir, "InterTabular-%s.ttf" % face)
        make(source, target, face)
        check(source, target)
    for face in FACES:
        print("in  %s  Inter-%s.ttf" % (sha256(os.path.join(source_dir, "Inter-%s.ttf" % face)), face))
    for face in FACES:
        print("out %s  InterTabular-%s.ttf" % (sha256(os.path.join(target_dir, "InterTabular-%s.ttf" % face)), face))


if __name__ == "__main__":
    main()
