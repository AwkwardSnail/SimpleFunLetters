// A letter: page margins, the text (paragraphs of sized pieces in one font
// version), and the drawings and font versions it uses. Reads and writes the
// same .funletter XML as the Windows app (Letter.cs), and SimpleLetters'
// .sletter files.
//
// paragraphs: [{ size, items: [{ text, size, font } | { br: true, size }] }]

import { ALL_CHARACTERS } from './designs.js';
import { COLUMNS, ROWS, resample } from './pixelfont.js';

export const EXTENSION = '.funletter';

export function emptyLetter(size) {
  return {
    margins: { top: 25, bottom: 25, left: 25, right: 25 },
    paragraphs: [{ size, items: [] }],
    glyphs: [], fonts: [], currentFont: null,
  };
}

export function fromPlainText(text, size) {
  const letter = emptyLetter(size);
  letter.paragraphs = text.replace(/\r\n?/g, '\n').split('\n')
    .map((line) => ({ size, items: line ? [{ text: line, size, font: 0 }] : [] }));
  return letter;
}

/** Fills in which drawings and font versions the text uses (plus the current one). */
export function attachDesigns(letter, designs) {
  const used = new Set([designs.current.id]);
  for (const p of letter.paragraphs) for (const it of p.items) if (!it.br) used.add(it.font);
  letter.fonts = designs.versions.filter((v) => v.id !== 0 && used.has(v.id))
    .map((v) => ({ id: v.id, glyphIds: [...v.glyphs.values()].map((g) => g.id).sort((a, b) => a - b) }));
  const usedGlyphs = new Set(letter.fonts.flatMap((f) => f.glyphIds));
  letter.glyphs = designs.glyphs.filter((g) => usedGlyphs.has(g.id));
  letter.currentFont = designs.current.id;
  return letter;
}

// ------------------------------------------------------------------- XML

const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
// Characters XML can't hold (rare control characters) are dropped.
const xmlSafe = (s) => s.replace(/[^\t\n\r -퟿-�\u{10000}-\u{10FFFF}]/gu, '');

export function writeGlyph(g, indent = '    ') {
  const rows = [];
  for (let r = 0; r < ROWS; r++) {
    let row = '';
    for (let c = 0; c < COLUMNS; c++) row += g.d[r * COLUMNS + c] ? '#' : '.';
    rows.push(`${indent}  <row>${row}</row>`);
  }
  return `${indent}<glyph id="${g.id}" char="${esc(g.ch)}" columns="${COLUMNS}" rows="${ROWS}">\n${rows.join('\n')}\n${indent}</glyph>`;
}

/** Reads a drawing, scaling it to today's grid if it was drawn on another size. */
export function readGlyph(el) {
  const ch = el.getAttribute('char');
  if (!ch || ch.length !== 1) return null;
  const rows = [...el.getElementsByTagName('row')].map((r) => r.textContent.trim());
  // Drawings saved before the grid size was recorded are 12 x 16.
  const rowCount = clampInt(el.getAttribute('rows'), rows.length || 16);
  const colCount = clampInt(el.getAttribute('columns'), rows.length ? Math.max(...rows.map((r) => r.length)) : 12);
  const source = new Uint8Array(rowCount * colCount);
  for (let r = 0; r < Math.min(rows.length, rowCount); r++)
    for (let c = 0; c < Math.min(rows[r].length, colCount); c++)
      source[r * colCount + c] = rows[r][c] === '#' ? 1 : 0;
  return { id: Number(el.getAttribute('id')) || 0, ch, d: resample(source, rowCount, colCount) };
}

function clampInt(text, fallback) {
  const n = parseInt(text ?? '', 10);
  return Math.min(256, Math.max(1, Number.isFinite(n) ? n : fallback));
}

export function toXml(letter) {
  const m = letter.margins;
  const out = ['<?xml version="1.0" encoding="utf-8"?>', '<funletter version="1">',
    `  <page size="A4" units="mm" top="${m.top}" bottom="${m.bottom}" left="${m.left}" right="${m.right}" />`];
  out.push(letter.glyphs.length ? '  <glyphs>' : '  <glyphs />');
  if (letter.glyphs.length) {
    for (const g of [...letter.glyphs].sort((a, b) => a.id - b.id)) out.push(writeGlyph(g));
    out.push('  </glyphs>');
  }
  const current = letter.currentFont != null ? ` current="${letter.currentFont}"` : '';
  if (!letter.fonts.length) out.push(`  <fonts${current} />`);
  else {
    out.push(`  <fonts${current}>`);
    for (const f of [...letter.fonts].sort((a, b) => a.id - b.id)) {
      if (!f.glyphIds.length) { out.push(`    <font id="${f.id}" />`); continue; }
      out.push(`    <font id="${f.id}">`);
      for (const id of f.glyphIds) out.push(`      <use glyph="${id}" />`);
      out.push('    </font>');
    }
    out.push('  </fonts>');
  }
  out.push('  <text>');
  for (const p of letter.paragraphs) {
    if (!p.items.length) { out.push(`    <p size="${p.size}" />`); continue; }
    out.push(`    <p size="${p.size}">`);
    for (const it of p.items) {
      if (it.br) out.push(`      <br size="${it.size}" />`);
      else out.push(`      <run size="${it.size}"${it.font ? ` font="${it.font}"` : ''}>${esc(xmlSafe(it.text))}</run>`);
    }
    out.push('    </p>');
  }
  out.push('  </text>', '</funletter>');
  return out.join('\n') + '\n';
}

const clampSize = (pt) => Math.min(400, Math.max(1, pt));
const clampMargin = (mm) => Math.min(150, Math.max(0, mm));
const num = (el, name, fallback) => {
  const v = parseFloat(el?.getAttribute(name) ?? '');
  return Number.isFinite(v) ? v : fallback;
};

export function fromXml(text) {
  const doc = new DOMParser().parseFromString(text, 'application/xml');
  const root = doc.documentElement;
  if (doc.getElementsByTagName('parsererror').length || !root || (root.nodeName !== 'funletter' && root.nodeName !== 'sletter'))
    throw new Error("This isn't a SimpleFunLetters or SimpleLetters document.");
  const child = (el, name) => [...el.children].filter((c) => c.nodeName === name);

  const letter = emptyLetter(12);
  const page = child(root, 'page')[0];
  letter.margins = {
    top: clampMargin(num(page, 'top', 25)), bottom: clampMargin(num(page, 'bottom', 25)),
    left: clampMargin(num(page, 'left', 25)), right: clampMargin(num(page, 'right', 25)),
  };

  for (const g of child(child(root, 'glyphs')[0] ?? root, 'glyph')) {
    const glyph = readGlyph(g);
    if (glyph && glyph.id > 0 && ALL_CHARACTERS.includes(glyph.ch)) letter.glyphs.push(glyph);
  }
  const fontsEl = child(root, 'fonts')[0];
  if (fontsEl) {
    const cur = parseInt(fontsEl.getAttribute('current') ?? '', 10);
    letter.currentFont = Number.isFinite(cur) ? cur : null;
    for (const f of child(fontsEl, 'font')) {
      const id = parseInt(f.getAttribute('id') ?? '', 10);
      if (id > 0) letter.fonts.push({ id, glyphIds: child(f, 'use').map((u) => parseInt(u.getAttribute('glyph'), 10) || 0) });
    }
  }
  const fontIds = new Set(letter.fonts.map((f) => f.id));

  const textEl = child(root, 'text')[0];
  letter.paragraphs = (textEl ? child(textEl, 'p') : []).map((p) => {
    const size = clampSize(num(p, 'size', 12));
    const items = [];
    for (const e of p.children) {
      const s = clampSize(num(e, 'size', size));
      if (e.nodeName === 'run') {
        let font = parseInt(e.getAttribute('font') ?? '0', 10) || 0;
        if (!fontIds.has(font)) font = 0;
        if (e.textContent) items.push({ text: e.textContent, size: s, font });
      } else if (e.nodeName === 'br') items.push({ br: true, size: s });
    }
    return { size, items };
  });
  if (!letter.paragraphs.length) letter.paragraphs = [{ size: 12, items: [] }];
  return letter;
}

// --------------------------------------------------- the child's letters

export function myLettersToXml(designs) {
  const glyphs = [...designs.current.glyphs.values()].sort((a, b) => a.ch.localeCompare(b.ch));
  return `<myletters>\n${glyphs.map((g) => writeGlyph(g, '  ')).join('\n')}\n</myletters>\n`;
}

export function myLettersFromXml(text) {
  const result = new Map();
  try {
    const doc = new DOMParser().parseFromString(text, 'application/xml');
    if (doc.getElementsByTagName('parsererror').length) return result;
    for (const g of doc.getElementsByTagName('glyph')) {
      const glyph = readGlyph(g);
      if (glyph) result.set(glyph.ch, glyph.d);
    }
  } catch { /* unreadable: start without */ }
  return result;
}
