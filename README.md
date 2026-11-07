# SimpleFunLetters

Write letters in your own handwriting. A calm letter writer for children (8 and
up): draw your own version of any letter, punctuation mark or number on squared
paper, and everything you type afterwards uses your drawing.

It grew out of [SimpleLetters](https://github.com/AwkwardSnail/SimpleLetters):
one A4 page, margins and text size, nothing else to get in the way.

Made by [aWkward sn@ail](https://awkwardsnail.com.au): calm, simple tools that
stay the way you left them.

## Get it

- **Windows:** coming soon to the Microsoft Store.
- **Build it yourself (free):** see below.

## Using it

- **Your letters (left):** capital letters, small letters, punctuation and
  numbers. Click one to draw your own. Letters you haven't drawn use the normal
  font (Atkinson Hyperlegible Next).
- **Drawing a letter:** click and drag on the squares; the right mouse button
  rubs out (or choose Eraser). The normal letter shows faintly behind as a
  guide. Letters sit on the green line; tails like the one on g hang below it.
  Press **Done** to start using it, or **Cancel** to leave the letter as it was.
  Clearing the squares and pressing Done goes back to the normal letter.
- **Changing a letter later:** what you've already written keeps the drawing it
  was typed with. Only new typing uses the new drawing.
- **The page (right):** the same page as SimpleLetters. The four arrows mark the
  margins: click one to show a line where the margin is, drag it to move it.
  The − and + buttons set the size of what you type next.
- **Open, Save, Print, Undo, Redo:** toolbar buttons, or Ctrl+O, Ctrl+S,
  Ctrl+P, Ctrl+Z and Ctrl+Y.
- Your latest letters are remembered, so a new letter starts with them.

## The .funletter format

Letters are saved as `.funletter` files: XML holding the margins, every letter
drawing (as rows of `#` and `.`), every version of your font, and the text with
its sizes and which font version each piece was typed with.

```xml
<funletter version="1">
  <page size="A4" units="mm" top="25" bottom="25" left="25" right="25" />
  <glyphs>
    <glyph id="1" char="i">
      <row>............</row>
      <row>............</row>
      <row>............</row>
      <row>.....##.....</row>
      <row>............</row>
      <row>.....##.....</row>
      <!-- … 16 rows in all: 12 above the line, 4 below it -->
    </glyph>
  </glyphs>
  <fonts>
    <font id="1"><use glyph="1" /></font>
  </fonts>
  <text>
    <p size="12">
      <run size="12">Hello, </run>
      <run size="12" font="1">hi there</run>
    </p>
  </text>
</funletter>
```

A `run` without a `font` uses the normal letters. SimpleFunLetters can also
open SimpleLetters' `.sletter` files and plain `.txt` files.

## How it works

Each time you press Done, SimpleFunLetters builds a small TrueType font from
your drawings (each square becomes a square in the font) and keeps the earlier
ones, so old text keeps its look. The fonts are made on your computer, kept in
`%LOCALAPPDATA%\SimpleFunLetters\fonts` while the app runs, and rebuilt from
the `.funletter` file when you open it. Nothing is installed in Windows.

## Running

The Windows app is .NET 10 WPF in `desktop/SimpleFunLetters`:

```
dotnet run --project desktop/SimpleFunLetters
```

## Privacy

SimpleFunLetters collects no data and never contacts an Awkward Snail server.
Your letters and drawings stay on your device. See the
[privacy policy](https://awkwardsnail.com.au/privacy.html).

## Licence

The code is under the [MIT licence](LICENSE), so you're welcome to fork it. The
Awkward Snail name and icon are not covered by the licence: please don't use them
for your fork. The bundled font, Atkinson Hyperlegible Next, keeps its own
licence (SIL OFL 1.1, see `desktop/SimpleFunLetters/Fonts/OFL.txt`).
