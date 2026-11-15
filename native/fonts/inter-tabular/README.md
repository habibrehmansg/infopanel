# Inter Tabular

`InfoPanel.Core/Fonts/InterTabular-{Regular,Bold,Italic,BoldItalic}.ttf` are the bundled Inter 4.001 faces
(`InfoPanel.Core/Fonts/Inter-*.ttf`), its digits mapped to Inter's own tabular figures; nothing else changed but the
names. A scene names them as the family "Inter Tabular"; "Inter" stays the original. A number drawn in Inter Tabular
keeps its width as its digits change, so a live reading doesn't jitter. Like Inter, they are under the SIL Open Font
License 1.1 (`InfoPanel.Core/Fonts/OFL.txt`), which names no Reserved Font Name for Inter.

`make_inter_tabular.py` makes them, once, with [fontTools](https://github.com/fonttools/fonttools); nothing in the
engine's build, packages or CI runs it.

## What changes

- **The character map.** U+0030 to U+0039 map to `zero.tf` … `nine.tf`, the tabular figures Inter already carries
  (1,328 units wide in Regular and Italic, 1,324 in Bold and Bold Italic, of 2,048), in every `cmap` subtable that maps
  them (formats 4 and 12, Unicode and Windows platforms). It works with and without shaping: a renderer that measures
  glyphs without HarfBuzz sees the same digits.
- **The name table.** The family (name id 1) is "Inter Tabular", the full name (4) "Inter Tabular Regular" and so on,
  the PostScript name (6) `InterTabular-Regular` and so on, the unique id (3) `4.001;InterTabular-Regular;tabular default`
  and so on, and the version (5) Inter's with "; tabular default" after it. The copyright (0), the style (2), the licence
  URL (14) and every other record stay as Inter's (Inter's faces carry no licence description, id 13, nor typographic
  family names, ids 16 and 17).

Everything else is Inter's byte for byte: the outlines, advances, kerning, features and every other table (the head
table's checksum, which covers the whole file, aside). The script checks that on its own output, and the engine's tests
check it again on the embedded files.

Only the digits change. Inter's `tnum` feature substitutes 61 glyphs, not only the ten digits: the space, the no-break
space, the period, comma, colon, semicolon, hyphen, brackets, the asterisk and the maths operators too, some of which
would change the width of ordinary words. Here they stay Inter's, so text set in Inter Tabular reads as Inter does. The
digits are remapped rather than their outlines swapped: Inter kerns its proportional digits and not its tabular ones, so
the proportional glyphs' kerning would break the equal widths.

## What Inter's features do to the digits

- **On by default.** The contextual alternates (`calt`) take the case forms of brackets and dashes beside a digit, as
  in "(1)" and "-1", and leave "<-" unjoined before a digit; their classes list the tabular figures with the
  proportional ones, so they work as in Inter and the digits keep their width. The glyph composition (`ccmp`) makes a
  zero followed by U+0338 a slashed zero, `zero.tf.slash`, as wide as the other digits.
- **Lost.** A digit followed by U+20DD or U+20DE no longer composes into a circled or squared digit: those rules name
  the proportional digits. Likewise `numr`, `dnom` and `frac`, if a renderer asks for them, no longer change the digits:
  their rules name the proportional digits, so "1" stays the tabular figure where Inter gives its numerator or
  denominator form (`one.numr`, `one.dnom`), and a fraction's digits stay full-width. Inter has no `sups` or `subs`.
- **On request.** `pnum`, if a renderer asks for it, maps the digits back to Inter's proportional ones, as Inter does.

## Making them again

The files were made with fontTools **4.60.1** on Python 3.9.6, in a virtual environment of its own; Python 3.14.9 with
the same fontTools gives the same bytes. The head table's modified time is the input's own (fontTools' timestamp
recalculation is off), so no time of day reaches the files and no `SOURCE_DATE_EPOCH` is needed.

```bash
python3 -m venv <venv>
<venv>/bin/pip install fonttools==4.60.1
<venv>/bin/python native/fonts/inter-tabular/make_inter_tabular.py InfoPanel.Core/Fonts <new or empty folder>
```

The last command, from the repository's root, is the one that makes them: it writes the four files into the folder, checks
them and prints the SHA-256 of its inputs and outputs, which must be these.

Inputs (`InfoPanel.Core/Fonts/`), SHA-256:

```
1b08e7fc267a5c7e1d614100f604b83e7e8a0be241f0f288faa2b3ac93a683ba  Inter-Regular.ttf
b37284b5701b6b168dfc770aa1a4ac492106422fd3ba76bc7641e37434e8019c  Inter-Bold.ttf
ef8f755d933b9310804cc9152f1f18cb2ac460d6b05054ec913d889c9f11f79e  Inter-Italic.ttf
c1811d32ac0f6eed1bbf57529f2ecdfc6c717d48a969dd864942436315a1b198  Inter-BoldItalic.ttf
```

Outputs, SHA-256:

```
7efc234a99f623387b447fa64fc71892dd81ce5a06080c539e7509e8c9694b51  InterTabular-Regular.ttf
29c7a346cb320e5925b43421c7b8626405efb49217613eb062cbe76e51f93570  InterTabular-Bold.ttf
32fb81f4da3f6cdebb3979996a09062dc848fd67c7673630649278a425dab7c1  InterTabular-Italic.ttf
ed43f64dba2ece9f5b89c46e236bbacc6a9c66dbd192f125574a226911330854  InterTabular-BoldItalic.ttf
```

`InfoPanel.Core.Tests/FontRegistryTests.cs` holds these to the files in `InfoPanel.Core/Fonts/`.
