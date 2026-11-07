// The drawing grid, and a small TrueType font builder for pixel drawings.
// Same grid and font layout as the Windows app (PixelFont.cs), so letters look
// the same in both and .funletter files move freely between them.

export const COLUMNS = 18;
export const ROWS = 24;
export const DESCENT_ROWS = 6;                 // rows below the writing line, for tails
export const ASCENT_ROWS = ROWS - DESCENT_ROWS; // rows sitting on the line

/** A drawing is a Uint8Array of ROWS * COLUMNS cells, 1 = drawn. */
export const emptyDrawing = () => new Uint8Array(ROWS * COLUMNS);
export const at = (r, c) => r * COLUMNS + c;
export const isEmpty = (d) => !d.some((v) => v);
export const sameDrawing = (a, b) => a.length === b.length && a.every((v, i) => v === b[i]);
export const copyDrawing = (d) => new Uint8Array(d);

/**
 * Fits a drawing made on a grid of another size (such as the 12 x 16 grid of
 * the first version) onto this one, square by square. Both grids put the
 * writing line three quarters of the way down, so letters stay on it.
 */
export function resample(source, rows, cols) {
  if (rows === ROWS && cols === COLUMNS) return copyDrawing(source);
  const result = emptyDrawing();
  for (let r = 0; r < ROWS; r++)
    for (let c = 0; c < COLUMNS; c++)
      result[at(r, c)] = source[Math.floor((r * rows) / ROWS) * cols + Math.floor((c * cols) / COLUMNS)];
  return result;
}

// ------------------------------------------------------------------ font

const PIXEL = 50;                                   // one square, in font units
const UNITS_PER_EM = ROWS * PIXEL;                  // the whole grid is one em (1200)
const ASCENDER = Math.trunc((UNITS_PER_EM * 950) / 1024); // line spacing close to Atkinson Hyperlegible
const DESCENDER = Math.trunc((UNITS_PER_EM * 300) / 1024);

class Bytes {
  constructor() { this.a = []; }
  u8(v) { this.a.push(v & 0xff); return this; }
  u16(v) { v &= 0xffff; this.a.push(v >> 8, v & 0xff); return this; }
  i16(v) { return this.u16(v < 0 ? v + 0x10000 : v); }
  u32(v) { v >>>= 0; this.a.push(v >>> 24, (v >>> 16) & 0xff, (v >>> 8) & 0xff, v & 0xff); return this; }
  bytes(arr) { for (const b of arr) this.a.push(b); return this; }
  pad4() { while (this.a.length % 4) this.a.push(0); return this; }
  get length() { return this.a.length; }
}

/** Turns lit squares into rectangles, one per run of squares in a row. */
function makeGlyph(ch, d) {
  let minCol = Infinity, maxCol = -1;
  for (let r = 0; r < ROWS; r++)
    for (let c = 0; c < COLUMNS; c++)
      if (d[at(r, c)]) { minCol = Math.min(minCol, c); maxCol = Math.max(maxCol, c); }

  const side = PIXEL / 2; // half a square of space each side of the drawing
  const advance = (maxCol - minCol + 1) * PIXEL + 2 * side;

  const rects = [];
  for (let r = 0; r < ROWS; r++) {
    let c = 0;
    while (c < COLUMNS) {
      if (!d[at(r, c)]) { c++; continue; }
      const start = c;
      while (c < COLUMNS && d[at(r, c)]) c++;
      const yTop = (ASCENT_ROWS - r) * PIXEL;
      // Overlap the row below by one unit so no hairline gap shows between rows.
      rects.push([side + (start - minCol) * PIXEL, yTop - PIXEL - 1, side + (c - minCol) * PIXEL, yTop]);
    }
  }

  const xMin = Math.min(...rects.map((q) => q[0])), yMin = Math.min(...rects.map((q) => q[1]));
  const xMax = Math.max(...rects.map((q) => q[2])), yMax = Math.max(...rects.map((q) => q[3]));
  const o = new Bytes();
  o.i16(rects.length).i16(xMin).i16(yMin).i16(xMax).i16(yMax);
  rects.forEach((_, i) => o.u16(i * 4 + 3)); // endPtsOfContours
  o.u16(0);                                  // no instructions
  const points = rects.length * 4;
  for (let i = 0; i < points; i++) o.u8(0x01); // all on-curve, 16-bit deltas
  // Clockwise: up the left side, along the top, down the right side.
  const xs = [], ys = [];
  for (const [x0, y0, x1, y1] of rects) { xs.push(x0, x0, x1, x1); ys.push(y0, y1, y1, y0); }
  let prev = 0;
  for (const x of xs) { o.i16(x - prev); prev = x; }
  prev = 0;
  for (const y of ys) { o.i16(y - prev); prev = y; }
  o.pad4();
  return { ch, outline: o.a, advance, xMin, yMin, xMax, yMax, points, contours: rects.length };
}

function checksum(arr) {
  let sum = 0;
  for (let i = 0; i < arr.length; i += 4)
    sum = (sum + (((arr[i] << 24) | ((arr[i + 1] ?? 0) << 16) | ((arr[i + 2] ?? 0) << 8) | (arr[i + 3] ?? 0)) >>> 0)) >>> 0;
  return sum;
}

/**
 * Builds a TrueType font from drawings: a Map of character -> drawing.
 * Characters that aren't in it fall back to the next font in the CSS list.
 */
export function buildFont(familyName, drawings) {
  const data = [...drawings].filter(([, d]) => !isEmpty(d))
    .sort((a, b) => a[0].charCodeAt(0) - b[0].charCodeAt(0))
    .map(([ch, d]) => makeGlyph(ch, d));
  if (!data.length) throw new Error('A font needs at least one drawn character.');
  const numGlyphs = data.length + 1; // glyph 0 is the empty .notdef

  const glyf = new Bytes();
  const loca = new Bytes().u32(0).u32(0); // .notdef has no outline
  for (const g of data) { glyf.bytes(g.outline); loca.u32(glyf.length); }

  const xMin = Math.min(...data.map((g) => g.xMin)), yMin = Math.min(...data.map((g) => g.yMin));
  const xMax = Math.max(...data.map((g) => g.xMax)), yMax = Math.max(...data.map((g) => g.yMax));

  const head = new Bytes().u32(0x00010000).u32(0x00010000).u32(0).u32(0x5f0f3cf5)
    .u16(0x000b).u16(UNITS_PER_EM).u32(0).u32(0).u32(0).u32(0)
    .i16(xMin).i16(yMin).i16(xMax).i16(yMax).u16(0).u16(8).i16(2).i16(1).i16(0);

  const hhea = new Bytes().u32(0x00010000).i16(ASCENDER).i16(-DESCENDER).i16(0)
    .u16(Math.max(...data.map((g) => g.advance))).i16(xMin)
    .i16(Math.min(...data.map((g) => g.advance - g.xMax))).i16(Math.max(...data.map((g) => g.xMax)))
    .i16(1).i16(0).i16(0).i16(0).i16(0).i16(0).i16(0).i16(0).u16(numGlyphs);

  const hmtx = new Bytes().u16((PIXEL * COLUMNS) / 2).i16(0);
  for (const g of data) hmtx.u16(g.advance).i16(g.xMin);

  const maxp = new Bytes().u32(0x00010000).u16(numGlyphs)
    .u16(Math.max(...data.map((g) => g.points))).u16(Math.max(...data.map((g) => g.contours)))
    .u16(0).u16(0).u16(2);
  for (let i = 0; i < 8; i++) maxp.u16(0);

  const avg = Math.trunc(data.reduce((s, g) => s + g.advance, 0) / data.length);
  const os2 = new Bytes().u16(4).i16(avg).u16(400).u16(5).u16(0)
    .i16(650).i16(600).i16(0).i16(75).i16(650).i16(600).i16(0).i16(350).i16(50).i16(300).i16(0)
    .bytes(new Array(10).fill(0)).u32(1).u32(0).u32(0).u32(0)
    .bytes([0x41, 0x57, 0x53, 0x4e]) // "AWSN"
    .u16(0x0040).u16(data[0].ch.charCodeAt(0)).u16(data[data.length - 1].ch.charCodeAt(0))
    .i16(ASCENDER).i16(-DESCENDER).i16(0).u16(ASCENDER).u16(DESCENDER).u32(1).u32(0)
    .i16((PIXEL * ROWS) / 2).i16(PIXEL * ASCENT_ROWS).u16(0).u16(32).u16(1);

  // cmap format 4: one segment per character plus the closing 0xFFFF segment.
  const segCount = data.length + 1;
  const searchRange = 2 * 2 ** Math.floor(Math.log2(segCount));
  const sub = new Bytes().u16(4).u16(0).u16(0).u16(segCount * 2).u16(searchRange)
    .u16(Math.log2(searchRange / 2)).u16(segCount * 2 - searchRange);
  data.forEach((g) => sub.u16(g.ch.charCodeAt(0)));
  sub.u16(0xffff).u16(0);
  data.forEach((g) => sub.u16(g.ch.charCodeAt(0)));
  sub.u16(0xffff);
  data.forEach((g, i) => sub.u16(i + 1 - g.ch.charCodeAt(0)));
  sub.u16(1);
  for (let i = 0; i < segCount; i++) sub.u16(0);
  sub.a[2] = sub.length >> 8; sub.a[3] = sub.length & 0xff;
  const cmap = new Bytes().u16(0).u16(1).u16(3).u16(1).u32(12).bytes(sub.a);

  const records = [[1, familyName], [2, 'Regular'], [3, familyName + ' Regular'], [4, familyName],
    [6, familyName.replace(/[^A-Za-z0-9]/g, '') + '-Regular']];
  const strings = new Bytes();
  const name = new Bytes().u16(0).u16(records.length).u16(6 + records.length * 12);
  for (const [id, text] of records) {
    const offset = strings.length;
    for (const ch of text) strings.u16(ch.charCodeAt(0)); // UTF-16BE
    name.u16(3).u16(1).u16(0x0409).u16(id).u16(text.length * 2).u16(offset);
  }
  name.bytes(strings.a);

  const post = new Bytes().u32(0x00030000).u32(0).i16(-100).i16(50).u32(0).u32(0).u32(0).u32(0).u32(0);

  const tables = [['OS/2', os2], ['cmap', cmap], ['glyf', glyf], ['head', head], ['hhea', hhea],
    ['hmtx', hmtx], ['loca', loca], ['maxp', maxp], ['name', name], ['post', post]];

  const n = tables.length;
  const tableSearch = 16 * 2 ** Math.floor(Math.log2(n));
  const font = new Bytes().u32(0x00010000).u16(n).u16(tableSearch).u16(Math.log2(tableSearch / 16)).u16(n * 16 - tableSearch);
  let offset = 12 + n * 16;
  let headOffset = 0;
  for (const [tag, t] of tables) {
    for (const ch of tag) font.u8(ch.charCodeAt(0));
    font.u32(checksum(t.a)).u32(offset).u32(t.length);
    if (tag === 'head') headOffset = offset;
    offset += (t.length + 3) & ~3;
  }
  for (const [, t] of tables) font.bytes(t.a).pad4();

  const bytes = new Uint8Array(font.a);
  const adjust = (0xb1b0afba - checksum(bytes)) >>> 0;
  new DataView(bytes.buffer).setUint32(headOffset + 8, adjust);
  return bytes.buffer;
}
