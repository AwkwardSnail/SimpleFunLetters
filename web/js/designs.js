// The letters a child has drawn in one document, with every earlier version.
// Mirrors Designs.cs in the Windows app.

import { buildFont, copyDrawing, isEmpty, sameDrawing } from './pixelfont.js';

export const CAPITALS = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';
export const SMALLS = 'abcdefghijklmnopqrstuvwxyz';
export const PUNCTUATION = '.,!?\'":;-()&/';
export const NUMBERS = '0123456789';
export const ALL_CHARACTERS = CAPITALS + SMALLS + PUNCTUATION + NUMBERS;

export const NORMAL_FONT = 'Atkinson Hyperlegible Next';
export const NORMAL_FAMILY = `"${NORMAL_FONT}", sans-serif`;

/**
 * A version of the child's font: which drawing each character used at one
 * moment. Text keeps the version it was typed with, so redrawing a letter
 * only changes what is typed afterwards.
 *   { id, glyphs: Map<char, {id, ch, d}>, family: CSS font-family }
 */
export class Designs {
  constructor() {
    this.glyphs = [];
    this.versions = [];
    this.derived = new Map();
    this.loading = [];
    // Font names are unique to this document so two documents never mix.
    this.token = Math.random().toString(36).slice(2, 10);
    this.current = this.#add(0, new Map());
  }

  currentDrawing(ch) { return this.current.glyphs.get(ch)?.d ?? null; }

  versionById(id) { return this.versions.find((v) => v.id === id) ?? this.versions[0]; }

  /** Resolves once every font made so far can be drawn. */
  ready() { return Promise.allSettled(this.loading); }

  /**
   * Records a new drawing for a character (an empty drawing means "use the
   * normal letter"). Returns false if nothing changed.
   */
  redesign(ch, d) {
    const existing = this.currentDrawing(ch);
    const empty = isEmpty(d);
    if (existing === null ? empty : !empty && sameDrawing(existing, d)) return false;
    const map = new Map(this.current.glyphs);
    if (empty) map.delete(ch);
    else {
      const glyph = { id: this.#nextGlyphId(), ch, d: copyDrawing(d) };
      this.glyphs.push(glyph);
      map.set(ch, glyph);
    }
    this.current = this.#add(this.#nextVersionId(), map);
    return true;
  }

  /**
   * For "Apply to existing text": the version that looks like `old` except
   * that `ch` uses its current drawing (or the normal letter). Returns `old`
   * itself when it already does.
   */
  withCurrentDrawingOf(old, ch) {
    const wanted = this.current.glyphs.get(ch) ?? null;
    const has = old.glyphs.get(ch) ?? null;
    if (wanted === has) return old;
    const key = `${old.id}|${ch}|${wanted?.id ?? 0}`;
    if (this.derived.has(key)) return this.derived.get(key);
    const map = new Map(old.glyphs);
    if (wanted) map.set(ch, wanted); else map.delete(ch);
    const same = this.versions.find((v) => v.glyphs.size === map.size && [...map].every(([k, g]) => v.glyphs.get(k) === g));
    const version = same ?? this.#add(this.#nextVersionId(), map);
    this.derived.set(key, version);
    return version;
  }

  /** Starts a new document's font from the child's saved letters (Map char -> drawing). */
  startFrom(drawings) {
    for (const [ch, d] of drawings)
      if (ALL_CHARACTERS.includes(ch) && !isEmpty(d)) this.glyphs.push({ id: this.glyphs.length + 1, ch, d: copyDrawing(d) });
    if (this.glyphs.length) this.current = this.#add(1, new Map(this.glyphs.map((g) => [g.ch, g])));
  }

  /** Rebuilds the designs saved in a letter. */
  restore(glyphs, fonts, currentId) {
    this.glyphs.push(...glyphs);
    const byId = new Map(this.glyphs.map((g) => [g.id, g]));
    for (const f of [...fonts].sort((a, b) => a.id - b.id)) {
      if (f.id === 0) continue; // version 0 (no drawings) always exists
      const map = new Map();
      for (const gid of f.glyphIds) { const g = byId.get(gid); if (g) map.set(g.ch, g); }
      this.#add(f.id, map);
    }
    // Older files don't say which version is current: it is the newest.
    this.current = this.versions.find((v) => v.id === currentId)
      ?? this.versions.reduce((a, b) => (b.id > a.id ? b : a));
  }

  #nextGlyphId() { return this.glyphs.length ? Math.max(...this.glyphs.map((g) => g.id)) + 1 : 1; }
  #nextVersionId() { return Math.max(...this.versions.map((v) => v.id)) + 1; }

  #add(id, map) {
    let family = NORMAL_FAMILY;
    if (map.size) {
      const name = `Fun Letters ${this.token} ${id}`;
      const drawings = new Map([...map].map(([ch, g]) => [ch, g.d]));
      const face = new FontFace(name, buildFont(name, drawings));
      document.fonts.add(face);
      this.loading.push(face.load());
      // The drawn font first; anything it doesn't have comes from the normal font.
      family = `"${name}", ${NORMAL_FAMILY}`;
    }
    const version = { id, glyphs: map, family };
    this.versions.push(version);
    return version;
  }
}
