// The writing area on the page: a contenteditable box whose text is made of
// pieces, each with its own size and version of the child's letters.
//
// DOM shape (always kept this way):
//   <p data-s="12"> <span data-f="3" data-s="12">text</span> <br data-s="12"> ... </p>
// An empty paragraph, or one ending in a line break, also gets a "filler"
// <br data-filler> so the browser shows the empty line.
//
// Ordinary typing into text that already has the current size and letters is
// left to the browser (so spellcheck and accented-letter input work). Every
// other edit is made by the app on its model of the text, then shown again.
// Undo is the app's own, a word at a time.

const clone = (pars) => pars.map((p) => ({ size: p.size, items: p.items.map((it) => ({ ...it })) }));
const itemLen = (it) => (it.br ? 1 : it.text.length);
const parLen = (p) => p.items.reduce((s, it) => s + itemLen(it), 0);

export class PageEditor {
  /**
   * @param {HTMLElement} el the contenteditable element
   * @param {object} host { designs(), size(), onChange(), onCaretSize(pt) }
   */
  constructor(el, host) {
    this.el = el;
    this.host = host;
    this.undoStack = [];
    this.redoStack = [];
    this.lastKind = null;
    this.expectedCaret = -1;
    this.heldCaret = null;

    el.addEventListener('beforeinput', (e) => this.#beforeInput(e));
    el.addEventListener('input', () => this.#afterNativeEdit());
    el.addEventListener('compositionend', () => this.#afterNativeEdit());
    el.addEventListener('keydown', (e) => this.#keyDown(e));
    el.addEventListener('drop', (e) => e.preventDefault());
    document.addEventListener('selectionchange', () => this.#selectionChanged());
  }

  // ---------------------------------------------------------- public

  /** Shows new text and starts a fresh undo history. */
  load(paragraphs) {
    this.#render(paragraphs);
    this.undoStack = [];
    this.redoStack = [];
    this.lastKind = null;
    this.setCaret(0);
  }

  /** The text as paragraphs of pieces (see letter.js). */
  paragraphs() { return this.#read(); }

  get canUndo() { return this.undoStack.length > 0; }
  get canRedo() { return this.redoStack.length > 0; }

  undo() { this.#travel(this.undoStack, this.redoStack); }
  redo() { this.#travel(this.redoStack, this.undoStack); }

  /**
   * Starts a new undo step at the next edit, and keeps the size just chosen
   * with − / + until the caret actually moves.
   */
  breakUndo() {
    this.lastKind = null;
    this.heldCaret = this.caretOffsets()?.join(',') ?? null;
  }

  /**
   * "Apply to existing text": every piece containing `ch` switches to the
   * version that uses the current drawing of `ch`. One undo step.
   */
  applyToExisting(ch) {
    const designs = this.host.designs();
    const pars = this.#read();
    let changed = false;
    for (const p of pars)
      for (const it of p.items) {
        if (it.br || !it.text.includes(ch)) continue;
        const updated = designs.withCurrentDrawingOf(designs.versionById(it.font), ch);
        if (updated.id !== it.font) { it.font = updated.id; changed = true; }
      }
    if (!changed) return false;
    const caret = this.caretOffsets() ?? [0, 0];
    this.#pushUndo();
    this.lastKind = null;
    this.#render(pars);
    this.setCaret(caret[0], caret[1]);
    this.host.onChange();
    return true;
  }

  focus() { this.el.focus({ preventScroll: true }); }

  // ------------------------------------------------------- rendering

  #render(pars) {
    const designs = this.host.designs();
    const nodes = pars.map((p) => {
      const P = document.createElement('p');
      P.dataset.s = p.size;
      P.style.fontSize = `${p.size}pt`;
      for (const it of p.items) {
        if (it.br) {
          const br = document.createElement('br');
          br.dataset.s = it.size;
          P.append(br);
        } else {
          const span = document.createElement('span');
          span.dataset.f = it.font;
          span.dataset.s = it.size;
          span.style.fontSize = `${it.size}pt`;
          span.style.fontFamily = designs.versionById(it.font).family;
          span.textContent = it.text;
          P.append(span);
        }
      }
      if (!p.items.length || p.items[p.items.length - 1].br) {
        const filler = document.createElement('br');
        filler.dataset.filler = '1';
        P.append(filler);
      }
      return P;
    });
    if (!nodes.length) return this.#render([{ size: this.host.size(), items: [] }]);
    this.el.replaceChildren(...nodes);
  }

  /** Reads the text back from the page, whatever shape the browser left it in. */
  #read() {
    const fallback = { size: this.host.size(), font: this.host.designs().current.id };
    const pars = [];
    const readParagraph = (P) => {
      const p = { size: parseFloat(P.dataset?.s) || fallback.size, items: [] };
      const walk = (node, fmt) => {
        for (const n of node.childNodes) {
          if (n.nodeType === Node.TEXT_NODE) {
            if (n.data) p.items.push({ text: n.data.replace(/\n/g, ' '), size: fmt.size, font: fmt.font });
          } else if (n.nodeName === 'BR') {
            if (!n.dataset.filler) p.items.push({ br: true, size: parseFloat(n.dataset.s) || fmt.size });
          } else if (n.nodeType === Node.ELEMENT_NODE) {
            const f = n.dataset?.f !== undefined
              ? { size: parseFloat(n.dataset.s) || fmt.size, font: parseInt(n.dataset.f, 10) || 0 }
              : fmt;
            walk(n, f);
          }
        }
      };
      walk(P, fallback);
      // A line break the browser left at the very end only shows the empty line.
      const last = p.items[p.items.length - 1];
      if (last?.br && P.lastChild?.nodeName === 'BR' && !P.lastChild.dataset.filler && !P.querySelector('br[data-filler]'))
        p.items.pop();
      return p;
    };
    let loose = null;
    for (const n of this.el.childNodes) {
      if (n.nodeName === 'P' || n.nodeName === 'DIV') { loose = null; pars.push(readParagraph(n)); }
      else {
        // Text outside a paragraph: gather it into one.
        if (!loose) { loose = document.createElement('p'); pars.push(null); }
        loose.append(n.cloneNode(true));
        pars[pars.length - 1] = readParagraph(loose);
      }
    }
    if (!pars.length) pars.push({ size: fallback.size, items: [] });
    pars.forEach((p) => this.#tidy(p));
    return pars;
  }

  /** True when the page is still in the app's own shape. */
  #isTidy() {
    for (const P of this.el.childNodes) {
      if (P.nodeName !== 'P') return false;
      for (const c of P.childNodes) {
        if (c.nodeName === 'BR') continue;
        if (c.nodeName !== 'SPAN' || c.dataset.f === undefined || c.childNodes.length !== 1
          || c.firstChild.nodeType !== Node.TEXT_NODE || !c.firstChild.data) return false;
      }
      if (!P.childNodes.length) return false;
    }
    return this.el.childNodes.length > 0;
  }

  /** Merges neighbouring pieces that look the same, and drops empty ones. */
  #tidy(p) {
    const items = [];
    for (const it of p.items) {
      if (!it.br && !it.text) continue;
      const prev = items[items.length - 1];
      if (prev && !prev.br && !it.br && prev.size === it.size && prev.font === it.font) prev.text += it.text;
      else items.push({ ...it });
    }
    p.items = items;
  }

  // -------------------------------------------------- caret <-> offset
  //
  // Offsets count characters across the whole text; a line break counts 1 and
  // so does the step from one paragraph to the next.

  #domLen(P) {
    let n = 0;
    for (const c of P.childNodes) {
      if (c.nodeName === 'BR') n += c.dataset.filler ? 0 : 1;
      else n += c.textContent.length;
    }
    return n;
  }

  #offsetOf(node, offset) {
    const ps = [...this.el.childNodes];
    if (node === this.el) {
      let total = 0;
      for (let i = 0; i < Math.min(offset, ps.length); i++) total += this.#domLen(ps[i]) + 1;
      return offset >= ps.length ? Math.max(0, total - 1) : total;
    }
    let total = 0;
    for (const P of ps) {
      if (P === node || P.contains(node)) {
        let count = 0;
        const kids = [...P.childNodes];
        for (let i = 0; i < kids.length; i++) {
          const c = kids[i];
          if (node === P && i === offset) return total + count;
          if (c === node || c.contains(node)) {
            if (c.nodeName === 'BR') return total + count;
            if (node.nodeType === Node.TEXT_NODE) {
              // Count text before this text node inside the piece.
              let before = 0;
              const walker = document.createTreeWalker(c, NodeFilter.SHOW_TEXT);
              for (let t = walker.nextNode(); t && t !== node; t = walker.nextNode()) before += t.data.length;
              return total + count + before + offset;
            }
            return total + count + (offset > 0 ? c.textContent.length : 0);
          }
          count += c.nodeName === 'BR' ? (c.dataset.filler ? 0 : 1) : c.textContent.length;
        }
        return total + count;
      }
      total += this.#domLen(P) + 1;
    }
    return Math.max(0, total - 1);
  }

  #positionAt(offset) {
    const ps = [...this.el.childNodes];
    for (let pi = 0; pi < ps.length; pi++) {
      const P = ps[pi];
      const len = this.#domLen(P);
      if (offset > len && pi < ps.length - 1) { offset -= len + 1; continue; }
      let local = Math.min(offset, len);
      const kids = [...P.childNodes];
      for (let i = 0; i < kids.length; i++) {
        const c = kids[i];
        if (c.nodeName === 'BR') {
          if (c.dataset.filler || local === 0) return [P, i];
          local -= 1;
          continue;
        }
        const L = c.textContent.length;
        if (local <= L) {
          const t = c.nodeType === Node.TEXT_NODE ? c : c.firstChild;
          return t ? [t, local] : [P, i];
        }
        local -= L;
      }
      return [P, kids.length];
    }
    return [this.el, 0];
  }

  /** [start, end] offsets of the selection on the page, or null when it's elsewhere. */
  caretOffsets() {
    const sel = document.getSelection();
    if (!sel.rangeCount) return null;
    const r = sel.getRangeAt(0);
    if (!this.el.contains(r.startContainer) || !this.el.contains(r.endContainer)) return null;
    return [this.#offsetOf(r.startContainer, r.startOffset), this.#offsetOf(r.endContainer, r.endOffset)];
  }

  setCaret(start, end = start) {
    const sel = document.getSelection();
    const range = document.createRange();
    const [sn, so] = this.#positionAt(start);
    const [en, eo] = this.#positionAt(end);
    range.setStart(sn, so);
    range.setEnd(en, eo);
    sel.removeAllRanges();
    sel.addRange(range);
  }

  // ------------------------------------------------------ text model

  #locate(pars, offset) {
    for (let pi = 0; pi < pars.length; pi++) {
      const len = parLen(pars[pi]);
      if (offset <= len) return [pi, offset];
      offset -= len + 1;
    }
    const last = pars.length - 1;
    return [last, parLen(pars[last])];
  }

  /** Index in p.items where `local` falls, splitting a piece of text if needed. */
  #splitAt(p, local) {
    let i = 0;
    for (; i < p.items.length; i++) {
      const it = p.items[i];
      const L = itemLen(it);
      if (local === 0) return i;
      if (local < L) {
        p.items.splice(i, 1, { ...it, text: it.text.slice(0, local) }, { ...it, text: it.text.slice(local) });
        return i + 1;
      }
      local -= L;
    }
    return i;
  }

  #deleteRange(pars, a, b) {
    if (b <= a) return;
    const [pa, la] = this.#locate(pars, a);
    const [pb, lb] = this.#locate(pars, b);
    if (pa === pb) {
      const p = pars[pa];
      const i = this.#splitAt(p, la); // split at the start first: it can't move the end's index past it
      const j = this.#splitAt(p, lb);
      p.items.splice(i, j - i);
      this.#tidy(p);
      return;
    }
    const first = pars[pa], last = pars[pb];
    first.items = first.items.slice(0, this.#splitAt(first, la));
    const tail = last.items.slice(this.#splitAt(last, lb));
    first.items.push(...tail);
    pars.splice(pa + 1, pb - pa);
    this.#tidy(first);
  }

  #insertItems(pars, offset, items) {
    const [pi, local] = this.#locate(pars, offset);
    const p = pars[pi];
    p.items.splice(this.#splitAt(p, local), 0, ...items);
    this.#tidy(p);
  }

  #splitParagraph(pars, offset) {
    const [pi, local] = this.#locate(pars, offset);
    const p = pars[pi];
    const rest = p.items.splice(this.#splitAt(p, local));
    pars.splice(pi + 1, 0, { size: this.host.size(), items: rest });
    this.#tidy(p);
    this.#tidy(pars[pi + 1]);
  }

  // ----------------------------------------------------------- editing

  #format() { return { size: this.host.size(), font: this.host.designs().current.id }; }

  /** True when the caret sits in text that already has the current size and letters. */
  #caretMatches() {
    const sel = document.getSelection();
    if (!sel.rangeCount || !sel.isCollapsed) return false;
    const node = sel.anchorNode;
    const span = node?.nodeType === Node.TEXT_NODE ? node.parentNode : null;
    if (!span || span.nodeName !== 'SPAN' || span.parentNode?.nodeName !== 'P' || span.dataset.f === undefined) return false;
    const fmt = this.#format();
    return parseInt(span.dataset.f, 10) === fmt.font && parseFloat(span.dataset.s) === fmt.size;
  }

  /** Starts a new undo step when this edit doesn't simply continue the last bit of typing. */
  #noteEdit(kind, at) {
    const continues = kind === 'type' && this.lastKind === 'type' && at === this.expectedCaret;
    if (!continues) this.#pushUndo();
    this.redoStack = [];
    this.lastKind = kind === 'space' ? 'type' : kind; // a word starts with its space
  }

  #pushUndo() {
    this.undoStack.push({ pars: this.#read(), caret: this.caretOffsets() ?? [0, 0] });
    if (this.undoStack.length > 300) this.undoStack.shift();
  }

  #travel(from, to) {
    if (!from.length) return;
    to.push({ pars: this.#read(), caret: this.caretOffsets() ?? [0, 0] });
    const state = from.pop();
    this.#render(clone(state.pars));
    this.setCaret(...state.caret);
    this.lastKind = null;
    this.host.onChange();
  }

  #rangeOffsets(e) {
    const ranges = e.getTargetRanges?.() ?? [];
    if (ranges.length && this.el.contains(ranges[0].startContainer) && this.el.contains(ranges[0].endContainer)) {
      const a = this.#offsetOf(ranges[0].startContainer, ranges[0].startOffset);
      const b = this.#offsetOf(ranges[0].endContainer, ranges[0].endOffset);
      return [Math.min(a, b), Math.max(a, b)];
    }
    return this.caretOffsets() ?? [0, 0];
  }

  #beforeInput(e) {
    const t = e.inputType;
    if (t.startsWith('format') || t === 'insertFromDrop' || t === 'deleteByDrag' || t === 'insertLink') {
      e.preventDefault(); // size is the only formatting
      return;
    }
    if (t === 'historyUndo') { e.preventDefault(); this.undo(); return; }
    if (t === 'historyRedo') { e.preventDefault(); this.redo(); return; }
    if (t === 'insertCompositionText') { this.#noteEdit('type', (this.caretOffsets() ?? [0])[0]); return; }

    const sel = this.caretOffsets() ?? [0, 0];
    if (t === 'insertText' && e.data && !e.data.includes('\n') && this.#caretMatches()) {
      this.#noteEdit(e.data === ' ' ? 'space' : 'type', sel[0]);
      return; // the browser types it into text that already looks right
    }

    e.preventDefault();
    if (!e.cancelable) return;
    const pars = this.#read();
    const fmt = this.#format();
    let [a, b] = sel;
    let caret = a;

    if (t === 'insertText' || t === 'insertReplacementText' || t === 'insertFromPaste' || t === 'insertFromYank') {
      if (t !== 'insertText') [a, b] = this.#rangeOffsets(e);
      const text = (e.data ?? e.dataTransfer?.getData('text/plain') ?? '').replace(/\r\n?/g, '\n');
      this.#noteEdit(t === 'insertText' ? (text === ' ' ? 'space' : 'type') : 'paste', a);
      this.#deleteRange(pars, a, b);
      caret = a;
      text.split('\n').forEach((line, i) => {
        if (i > 0) { this.#splitParagraph(pars, caret); caret += 1; }
        if (line) { this.#insertItems(pars, caret, [{ text: line, ...fmt }]); caret += line.length; }
      });
    } else if (t === 'insertParagraph') {
      this.#noteEdit('enter', a);
      this.#deleteRange(pars, a, b);
      this.#splitParagraph(pars, a);
      caret = a + 1;
    } else if (t === 'insertLineBreak') {
      this.#noteEdit('enter', a);
      this.#deleteRange(pars, a, b);
      this.#insertItems(pars, a, [{ br: true, size: fmt.size }]);
      caret = a + 1;
    } else if (t.startsWith('delete')) {
      [a, b] = this.#rangeOffsets(e);
      if (a === b) {
        if (t.includes('Backward')) a = Math.max(0, a - 1);
        else b = a + 1;
      }
      this.#noteEdit('delete', a);
      const total = pars.reduce((s, p) => s + parLen(p) + 1, -1);
      this.#deleteRange(pars, a, Math.min(b, total));
      caret = a;
    } else {
      return; // anything else: leave the text as it is
    }

    this.#render(pars);
    this.setCaret(caret);
    this.expectedCaret = caret;
    this.host.onChange();
  }

  #afterNativeEdit() {
    if (!this.#isTidy()) {
      const caret = this.caretOffsets() ?? [0, 0];
      this.#render(this.#read());
      this.setCaret(...caret);
    }
    this.expectedCaret = (this.caretOffsets() ?? [-1])[0];
    this.host.onChange();
  }

  #keyDown(e) {
    const ctrl = e.ctrlKey || e.metaKey;
    const key = e.key.toLowerCase();
    if (ctrl && (key === 'z' || key === 'y')) {
      e.preventDefault();
      if (key === 'y' || e.shiftKey) this.redo(); else this.undo();
    } else if (ctrl && ['b', 'i', 'u', 'e', 'l', 'r', 'j'].includes(key)) {
      e.preventDefault(); // no bold, italic, alignment
    } else if (e.key === 'Tab' && !ctrl && !e.altKey) {
      e.preventDefault();
      const pars = this.#read();
      const [a, b] = this.caretOffsets() ?? [0, 0];
      this.#noteEdit('type', a);
      this.#deleteRange(pars, a, b);
      this.#insertItems(pars, a, [{ text: '\t', ...this.#format() }]);
      this.#render(pars);
      this.setCaret(a + 1);
      this.expectedCaret = a + 1;
      this.host.onChange();
    }
  }

  /** Moving the caret picks up the size of the text it is in. */
  #selectionChanged() {
    const sel = document.getSelection();
    if (!sel.rangeCount || !this.el.contains(sel.anchorNode)) return;
    if (this.heldCaret !== null && this.heldCaret === this.caretOffsets()?.join(',')) return;
    this.heldCaret = null;
    let node = sel.anchorNode;
    let holder = null;
    if (node.nodeName === 'P') {
      // Between pieces (say, at the end of a line): use the text just before the caret.
      for (let i = sel.anchorOffset - 1; i >= 0 && !holder; i--) {
        const c = node.childNodes[i];
        if (c?.nodeName === 'SPAN' || (c?.nodeName === 'BR' && !c.dataset.filler)) holder = c;
      }
      holder ??= node;
    } else {
      if (node.nodeType === Node.TEXT_NODE) node = node.parentNode;
      holder = node.closest?.('span[data-s], p[data-s]');
    }
    const size = parseFloat(holder?.dataset.s);
    if (size) this.host.onCaretSize(size);
  }
}
