# SimpleLetters Fun

Write letters in your own handwriting. A calm letter writer for children (8 and
up): draw your own version of any letter, punctuation mark or number on squared
paper, and everything you type afterwards uses your drawing.

It grew out of [SimpleLetters](https://github.com/AwkwardSnail/SimpleLetters):
one A4 page, margins and text size, nothing else to get in the way.

Made by [aWkward sn@ail](https://awkwardsnail.com.au): calm, simple tools that
stay the way you left them.

## Get it

- **Windows:** coming soon to the Microsoft Store.
- **In your browser:** the `web/` folder is the same app as a static site.
- **Build it yourself (free):** see below.

Both versions open and save the same `.funletter` files.

## Using it

- **Your letters (left):** capital letters, small letters, punctuation and
  numbers. Click one to draw your own. Letters you haven't drawn use the normal
  font: Atkinson Hyperlegible Next, or Literata when the font is set to Serif.
- **Drawing a letter:** squared paper with the normal letter showing faintly
  behind as a guide. Letters sit on the green line; tails like the one on g
  hang below it. The line above the paper always says what to do next.
  - **Pen** and **Eraser:** click and drag. The right mouse button rubs out.
  - **Line:** press where the line starts, drag, and let go where it ends (the
    right mouse button rubs a line out).
  - **Arc:** click the middle of the circle; a dashed circle follows the mouse,
    so click when it's the size you want. The pointer then sticks to the
    circle: click a yellow cross where the arc starts and another where it
    ends, and the arc joins them the short way round. Esc or the right mouse
    button starts the arc again.
  - **Squares:** Large (18 × 24), Medium (30 × 40) or Small (45 × 60) for finer
    detail. Changing the size fits the drawing onto the new squares. Each
    letter keeps its own size, and new drawings start with the last size used.
  - Press **Done** to start using it, or **Cancel** to leave the letter as it
    was. Clearing the squares and pressing Done goes back to the normal letter.
- **Changing a letter later:** what you've already written keeps the drawing it
  was typed with, and only new typing uses the new drawing. To change the letter
  in what you've already written too, press **Apply to existing text** instead
  of Done. Only that letter changes; Undo puts it back.
- **The page (right):** the same page as SimpleLetters. The four arrows mark the
  margins: click one to show a line where the margin is, drag it to move it.
  When the writing reaches the bottom margin it carries on onto another A4 page
  below, whole lines at a time, the way it prints.
- **The toolbar:** the same as SimpleLetters.
  - **Font: Sans / Font: Serif** switches the letters you haven't drawn between
    Atkinson Hyperlegible Next and Literata. Your drawn letters always stay as
    you drew them.
  - **B, I, U** for bold, italic and underline (or Ctrl+B, Ctrl+I, Ctrl+U).
    Drawn letters are thickened or slanted, since a drawing has no bold or
    italic of its own.
  - **Text size − / +** and **Zoom** (or hold Ctrl and turn the mouse wheel).
  - With text selected, the font, B, I, U and size buttons change the
    selection (one undo step each); with nothing selected, they apply to what
    you type next. Moving the cursor into existing text picks up its look.
- **Open, Save, Print, Undo, Redo:** toolbar buttons, or Ctrl+O, Ctrl+S,
  Ctrl+P, Ctrl+Z and Ctrl+Y.
- Your latest letters are remembered, so a new letter starts with them.

## The .funletter format

Letters are saved as `.funletter` files: XML holding the margins, every letter
drawing (as rows of `#` and `.`), every version of your font (and which one new
typing uses), and the text with its sizes and the font version of each piece.

```xml
<funletter version="1">
  <page size="A4" units="mm" top="25" bottom="25" left="25" right="25" />
  <glyphs>
    <glyph id="1" char="i" columns="18" rows="24">
      <row>..................</row>
      <row>..................</row>
      <row>.......###........</row>
      <row>.......###........</row>
      <row>..................</row>
      <row>.......###........</row>
      <!-- … 24 rows in all: 18 above the line, 6 below it -->
    </glyph>
  </glyphs>
  <fonts current="1">
    <font id="1"><use glyph="1" /></font>
  </fonts>
  <text>
    <p size="12">
      <run size="12">Hello, </run>
      <run size="12" font="1">hi there</run>
      <run size="12" font="1" serif="true" bold="true"> friend</run>
    </p>
  </text>
</funletter>
```

A `run` without a `font` uses the normal letters. A run can also have
`serif="true"`, `bold="true"`, `italic="true"` and `underline="true"`; without
them it is plain Sans. (SimpleLetters' `.sletter` files write `font="serif"`
instead, and open with their fonts and styles.) Each drawing records its own
size in `columns` and `rows` (18 × 24, 30 × 40 or 45 × 60); the writing line is
always three quarters of the way down. Drawings made on the first version's
12 × 16 grid are scaled up to 18 × 24 when they're opened. SimpleLetters Fun can also
open SimpleLetters' `.sletter` files and plain `.txt` files.

## How it works

Each time you press Done, SimpleLetters Fun builds a small TrueType font from
your drawings (each square becomes a square in the font) and keeps the earlier
ones, so old text keeps its look. The fonts are made on your computer, kept in
`%LOCALAPPDATA%\SimpleFunLetters\fonts` while the app runs (a folder per open
copy of the app), and rebuilt from
the `.funletter` file when you open it. Nothing is installed in Windows.

## Running

The Windows app is .NET 10 WPF in `desktop/SimpleFunLetters`:

```
dotnet run --project desktop/SimpleFunLetters
```

`packaging\pack.ps1` builds the MSIX package for the Microsoft Store (in
`packaging\out\`). With Windows Developer Mode on, `packaging\pack.ps1 -Install`
also installs it on this PC for testing, so it shows in the Start menu and
opens `.funletter` files, like the Store version. `packaging\generate-icons.ps1`
makes the icons from `packaging\icon-master.png`.

The browser version is the self-contained `web/` folder (HTML, CSS, JavaScript
modules and the bundled font, no build step), ready to host as a static site
such as GitHub Pages. Serve it and open it:

```
python -m http.server 8175 --directory web
```

Then open http://localhost:8175. It builds the drawn fonts in the browser, just
like the Windows app, and keeps your settings and latest letters in the
browser's own storage. Save writes a `.funletter` file (in Chrome and Edge it
can save straight back to the file you opened; other browsers download it).

## Privacy

SimpleLetters Fun collects no data and never contacts an Awkward Snail server.
Your letters and drawings stay on your device. See the
[privacy policy](https://awkwardsnail.com.au/privacy.html).

## Licence

The code is under the [MIT licence](LICENSE), so you're welcome to fork it. The
Awkward Snail name and icon are not covered by the licence: please don't use them
for your fork. The bundled fonts, Atkinson Hyperlegible Next and Literata, keep
their own licence (SIL OFL 1.1, see `desktop/SimpleFunLetters/Fonts/OFL.txt` and
`OFL-Literata.txt`).

The browser version still has the earlier toolbar: no font choice, bold, italic
or underline.
