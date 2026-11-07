// SimpleFunLetters in the browser. The same app as the Windows version:
// draw your own letters on the left, write on an A4 page on the right.
// Everything stays in this browser: settings and the child's latest letters
// are kept in local storage; letters are saved as .funletter files.

import { ASCENT_ROWS, COLUMNS, ROWS, at, copyDrawing, emptyDrawing } from './pixelfont.js';
import { CAPITALS, Designs, NORMAL_FONT, NUMBERS, PUNCTUATION, SMALLS } from './designs.js';
import { EXTENSION, attachDesigns, emptyLetter, fromPlainText, fromXml, myLettersFromXml, myLettersToXml, toXml } from './letter.js';
import { PageEditor } from './editor.js';

const $ = (id) => document.getElementById(id);

const PAGE_W = 210, PAGE_H = 297; // A4, mm
const MIN_TEXT_MM = 40;            // margins never squeeze the writing area below this
const MIN_PT = 6, MAX_PT = 72;
const ZOOM_STEPS = [0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2];
const SIDES = ['top', 'bottom', 'left', 'right'];
const DESCRIBE = {
  '.': 'full stop', ',': 'comma', '!': 'exclamation mark', '?': 'question mark', "'": 'apostrophe',
  '"': 'quotation mark', ':': 'colon', ';': 'semicolon', '-': 'dash', '(': 'opening bracket',
  ')': 'closing bracket', '&': 'ampersand', '/': 'slash',
};
const describe = (ch) => DESCRIBE[ch] ?? ch;

// ------------------------------------------------------------- storage

const store = {
  get(key) { try { return localStorage.getItem('simplefunletters.' + key); } catch { return null; } },
  set(key, value) { try { localStorage.setItem('simplefunletters.' + key, value); } catch { /* private mode: not remembered */ } },
};

const settings = (() => {
  const defaults = { margins: { top: 25, bottom: 25, left: 25, right: 25 }, fontSize: 12, zoom: 0,
    lines: { top: false, bottom: false, left: false, right: false } };
  try {
    const saved = JSON.parse(store.get('settings') ?? 'null');
    if (saved) return { ...defaults, ...saved, margins: { ...defaults.margins, ...saved.margins }, lines: { ...defaults.lines, ...saved.lines } };
  } catch { /* start fresh */ }
  return defaults;
})();
const saveSettings = () => store.set('settings', JSON.stringify(settings));

const loadMyLetters = () => myLettersFromXml(store.get('myletters') ?? '');

// --------------------------------------------------------------- state

let designs = new Designs();
let currentSize = Math.min(MAX_PT, Math.max(MIN_PT, settings.fontSize));
let dirty = false;
let fileName = 'Untitled';
let fileHandle = null;

const editor = new PageEditor($('editor'), {
  designs: () => designs,
  size: () => currentSize,
  onChange: () => { markDirty(); updateUndoButtons(); checkOverflow(); },
  onCaretSize: (pt) => { currentSize = Math.min(MAX_PT, Math.max(MIN_PT, pt)); updateSizeControls(); },
});

function markDirty() { if (!dirty) { dirty = true; updateTitle(); } }
function updateTitle() { document.title = `${dirty ? '*' : ''}${fileName} – SimpleFunLetters`; }
function updateUndoButtons() { $('undoButton').disabled = !editor.canUndo; $('redoButton').disabled = !editor.canRedo; }
function checkOverflow() {
  requestAnimationFrame(() => {
    const el = $('editor');
    $('overflowNote').hidden = el.scrollHeight <= el.clientHeight + 1;
  });
}

// Toolbar and panel buttons don't take the typing focus away from the page.
document.querySelectorAll('.bar button, .panel button').forEach((b) => b.addEventListener('mousedown', (e) => e.preventDefault()));

// ----------------------------------------------------------- text size

function updateSizeControls() {
  $('sizeLabel').textContent = `${+currentSize.toFixed(1)} pt`;
  $('smallerButton').disabled = currentSize <= MIN_PT;
  $('largerButton').disabled = currentSize >= MAX_PT;
}

function changeSize(delta) {
  currentSize = Math.min(MAX_PT, Math.max(MIN_PT, Math.round(currentSize) + delta));
  settings.fontSize = currentSize;
  saveSettings();
  updateSizeControls();
  editor.breakUndo();
  editor.focus();
}
$('smallerButton').onclick = () => changeSize(-1);
$('largerButton').onclick = () => changeSize(1);
$('undoButton').onclick = () => { editor.undo(); editor.focus(); };
$('redoButton').onclick = () => { editor.redo(); editor.focus(); };

// -------------------------------------------------------- letter tiles

const tiles = new Map();

function buildTiles() {
  for (const [id, chars] of [['capitals', CAPITALS], ['smalls', SMALLS], ['punctuation', PUNCTUATION], ['numbers', NUMBERS]]) {
    for (const ch of chars) {
      const tile = document.createElement('button');
      tile.type = 'button';
      tile.className = 'tile';
      tile.addEventListener('mousedown', (e) => e.preventDefault());
      tile.onclick = () => openDrawing(ch);
      tiles.set(ch, tile);
      $(id).append(tile);
    }
  }
  refreshTiles();
}

function refreshTiles() { for (const ch of tiles.keys()) refreshTile(ch); }

/** Shows each letter as the child drew it, or as the normal letter if not drawn yet. */
function refreshTile(ch) {
  const tile = tiles.get(ch);
  const d = designs.currentDrawing(ch);
  tile.title = d ? `Change your ${describe(ch)}` : `Draw your own ${describe(ch)}`;
  tile.setAttribute('aria-label', tile.title);
  if (!d) { tile.replaceChildren(document.createTextNode(ch)); return; }
  const canvas = document.createElement('canvas');
  canvas.width = COLUMNS;
  canvas.height = ROWS;
  const ctx = canvas.getContext('2d');
  ctx.fillStyle = getComputedStyle(document.documentElement).getPropertyValue('--green');
  for (let r = 0; r < ROWS; r++) for (let c = 0; c < COLUMNS; c++) if (d[at(r, c)]) ctx.fillRect(c, r, 1, 1);
  tile.replaceChildren(canvas);
}

// ------------------------------------------------------- drawing pane

const CELL = 20;
const canvas = $('drawing');
let drawingChar = null;
let pixels = emptyDrawing();
let erasing = false;
let stroke = null; // { value, last: [r, c] }

function openDrawing(ch) {
  drawingChar = ch;
  $('drawTitle').textContent = `Draw your ${describe(ch)}`;
  pixels = copyDrawing(designs.currentDrawing(ch) ?? emptyDrawing());
  setTool(false);
  $('lettersView').hidden = true;
  $('drawView').hidden = false;
  paintGrid();
}

function closeDrawing() {
  drawingChar = null;
  $('drawView').hidden = true;
  $('lettersView').hidden = false;
}

function setTool(erase) {
  erasing = erase;
  $('penTool').setAttribute('aria-pressed', String(!erase));
  $('eraserTool').setAttribute('aria-pressed', String(erase));
}

function paintGrid() {
  const dpr = window.devicePixelRatio || 1;
  const w = COLUMNS * CELL, h = ROWS * CELL;
  if (canvas.width !== w * dpr) { canvas.width = w * dpr; canvas.height = h * dpr; }
  const ctx = canvas.getContext('2d');
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.fillStyle = '#fdfdfb';
  ctx.fillRect(0, 0, w, h);

  // The normal letter, faintly, as a guide: a capital H reaches almost to the top.
  const baseline = ASCENT_ROWS * CELL;
  if (drawingChar) {
    ctx.font = `100px "${NORMAL_FONT}"`;
    const cap = ctx.measureText('H').actualBoundingBoxAscent / 100 || 0.7;
    const em = ((ASCENT_ROWS - 1) * CELL) / cap;
    ctx.font = `${em}px "${NORMAL_FONT}"`;
    ctx.fillStyle = '#dce6dc';
    ctx.textBaseline = 'alphabetic';
    ctx.textAlign = 'center';
    ctx.fillText(drawingChar, w / 2, baseline);
  }

  ctx.fillStyle = '#1a1a1a';
  for (let r = 0; r < ROWS; r++)
    for (let c = 0; c < COLUMNS; c++)
      if (pixels[at(r, c)]) ctx.fillRect(c * CELL, r * CELL, CELL, CELL);

  ctx.strokeStyle = '#e4e4e0';
  ctx.lineWidth = 1;
  ctx.beginPath();
  for (let c = 0; c <= COLUMNS; c++) { ctx.moveTo(c * CELL + 0.5, 0); ctx.lineTo(c * CELL + 0.5, h); }
  for (let r = 0; r <= ROWS; r++) { ctx.moveTo(0, r * CELL + 0.5); ctx.lineTo(w, r * CELL + 0.5); }
  ctx.stroke();

  // The writing line: letters sit on it, tails hang below.
  ctx.strokeStyle = '#3c8c48';
  ctx.lineWidth = 2;
  ctx.beginPath();
  ctx.moveTo(0, baseline);
  ctx.lineTo(w, baseline);
  ctx.stroke();
}

function cellAt(e) {
  const rect = canvas.getBoundingClientRect();
  const r = Math.floor(((e.clientY - rect.top) / rect.height) * ROWS);
  const c = Math.floor(((e.clientX - rect.left) / rect.width) * COLUMNS);
  return [Math.min(ROWS - 1, Math.max(0, r)), Math.min(COLUMNS - 1, Math.max(0, c))];
}

function paintTo(e) {
  const [r, c] = cellAt(e);
  const [r0, c0] = stroke.last ?? [r, c];
  // Fill in every square between the last point and this one, so quick strokes don't skip.
  const steps = Math.max(Math.abs(r - r0), Math.abs(c - c0));
  for (let i = 0; i <= steps; i++) {
    const rr = steps ? r0 + Math.round(((r - r0) * i) / steps) : r;
    const cc = steps ? c0 + Math.round(((c - c0) * i) / steps) : c;
    pixels[at(rr, cc)] = stroke.value;
  }
  stroke.last = [r, c];
  paintGrid();
}

canvas.addEventListener('pointerdown', (e) => {
  if (e.button !== 0 && e.button !== 2) return;
  stroke = { value: e.button === 2 || erasing ? 0 : 1, last: null };
  try { canvas.setPointerCapture(e.pointerId); } catch { /* not a tracked pointer */ }
  paintTo(e);
  e.preventDefault();
});
canvas.addEventListener('pointermove', (e) => { if (stroke) paintTo(e); });
const endStroke = () => { stroke = null; };
canvas.addEventListener('pointerup', endStroke);
canvas.addEventListener('pointercancel', endStroke);
canvas.addEventListener('contextmenu', (e) => e.preventDefault());

$('penTool').onclick = () => setTool(false);
$('eraserTool').onclick = () => setTool(true);
$('clearButton').onclick = () => { pixels = emptyDrawing(); paintGrid(); };
$('cancelButton').onclick = () => { closeDrawing(); editor.focus(); };

/** Uses the drawing for everything typed from now on. Text already on the page keeps its drawings. */
function useDrawing() {
  const ch = drawingChar;
  if (ch && designs.redesign(ch, pixels)) {
    store.set('myletters', myLettersToXml(designs));
    refreshTile(ch);
    markDirty();
  }
  editor.breakUndo();
  return ch;
}

$('doneButton').onclick = () => { useDrawing(); closeDrawing(); editor.focus(); };

/** Like Done, and also changes this character everywhere it is already written. */
$('applyButton').onclick = async () => {
  const ch = useDrawing();
  closeDrawing();
  await designs.ready();
  if (ch && editor.applyToExisting(ch)) markDirty();
  updateUndoButtons();
  editor.focus();
};

// ------------------------------------------------------------- margins

const page = $('page');
const handles = {};
const guides = {};
const valueLabel = document.createElement('div');
valueLabel.className = 'value-label';
valueLabel.hidden = true;
let drag = null; // { side, moved, x, y }

function buildMargins() {
  for (const side of SIDES) {
    const horizontal = side === 'left' || side === 'right';
    const h = document.createElement('button');
    h.type = 'button';
    h.className = 'handle';
    h.innerHTML = horizontal
      ? '<svg viewBox="0 0 20 20" aria-hidden="true"><path d="M3.5 5 L16.5 5 L10 17 Z"/></svg>'
      : '<svg viewBox="0 0 20 20" aria-hidden="true"><path d="M5 3.5 L5 16.5 L17 10 Z"/></svg>';
    h.addEventListener('pointerdown', (e) => handleDown(e, side));
    h.addEventListener('pointermove', (e) => handleMove(e));
    h.addEventListener('pointerup', (e) => handleUp(e));
    h.addEventListener('keydown', (e) => handleKey(e, side));
    h.addEventListener('focus', updateMargins);
    h.addEventListener('blur', updateMargins);
    (horizontal ? $('rulerTop') : $('rulerSide')).append(h);
    handles[side] = h;

    const g = document.createElement('div');
    g.className = 'guide ' + (horizontal ? 'v' : 'h');
    $('guides').append(g);
    guides[side] = g;
  }
  $('guides').append(valueLabel);
  updateMargins();
}

const marginAt = (side) => settings.margins[side];
/** Where the margin line is, in mm from the page's left or top edge. */
const lineAt = (side) => ({ left: marginAt('left'), right: PAGE_W - marginAt('right'), top: marginAt('top'), bottom: PAGE_H - marginAt('bottom') })[side];

function updateMargins() {
  for (const side of SIDES) page.style.setProperty(`--m-${side}`, `${marginAt(side)}mm`);
  for (const side of SIDES) {
    const horizontal = side === 'left' || side === 'right';
    const pos = `calc(${lineAt(side)}mm - 10px)`;
    const h = handles[side];
    if (horizontal) h.style.left = pos; else h.style.top = pos;
    h.classList.toggle('on', settings.lines[side]);
    h.title = `${side[0].toUpperCase() + side.slice(1)} margin: ${marginAt(side)} mm\nClick to show or hide its line. Drag to move it.`;
    h.setAttribute('aria-label', `${side} margin, ${marginAt(side)} millimetres`);
    const g = guides[side];
    g.hidden = !(settings.lines[side] || (drag?.side === side && drag.moved));
    if (horizontal) g.style.left = `${lineAt(side)}mm`; else g.style.top = `${lineAt(side)}mm`;
  }
  // "Left margin 25 mm" beside an arrow while it is dragged or has keyboard focus.
  const side = drag?.moved ? drag.side : SIDES.find((s) => handles[s] === document.activeElement && handles[s].matches(':focus-visible'));
  valueLabel.hidden = !side;
  if (side) {
    valueLabel.textContent = `${side[0].toUpperCase() + side.slice(1)} margin ${marginAt(side)} mm`;
    const p = lineAt(side);
    const s = valueLabel.style;
    s.left = s.right = s.top = s.bottom = '';
    if (side === 'left') { s.left = `calc(${p}mm + 6px)`; s.top = '6px'; }
    if (side === 'right') { s.right = `calc(${PAGE_W - p}mm + 6px)`; s.top = '6px'; }
    if (side === 'top') { s.top = `calc(${p}mm + 6px)`; s.left = '6px'; }
    if (side === 'bottom') { s.bottom = `calc(${PAGE_H - p}mm + 6px)`; s.left = '6px'; }
  }
  checkOverflow();
}

/** Sets a margin, keeping it on the page and leaving room to write. */
function setMargin(side, mm) {
  const opposite = { left: 'right', right: 'left', top: 'bottom', bottom: 'top' }[side];
  const pageMm = side === 'left' || side === 'right' ? PAGE_W : PAGE_H;
  mm = Math.min(pageMm - marginAt(opposite) - MIN_TEXT_MM, Math.max(0, Math.round(mm)));
  if (mm === marginAt(side)) return;
  settings.margins[side] = mm;
  markDirty(); // margins are saved with the letter
  updateMargins();
}

function toggleLine(side) {
  settings.lines[side] = !settings.lines[side];
  saveSettings();
  updateMargins();
}

function handleDown(e, side) {
  drag = { side, moved: false, x: e.clientX, y: e.clientY };
  try { e.currentTarget.setPointerCapture(e.pointerId); } catch { /* not a tracked pointer */ }
  e.preventDefault(); // keep the typing focus on the page
}

function handleMove(e) {
  if (!drag) return;
  if (!drag.moved && Math.hypot(e.clientX - drag.x, e.clientY - drag.y) < 3) return; // still a click
  drag.moved = true;
  const rect = page.getBoundingClientRect();
  const x = ((e.clientX - rect.left) / rect.width) * PAGE_W;
  const y = ((e.clientY - rect.top) / rect.height) * PAGE_H;
  const mm = { left: x, right: PAGE_W - x, top: y, bottom: PAGE_H - y }[drag.side];
  setMargin(drag.side, mm);
  updateMargins();
}

function handleUp() {
  if (!drag) return;
  const { side, moved } = drag;
  drag = null;
  if (moved) saveSettings(); else toggleLine(side);
  updateMargins();
}

/** Arrow keys nudge a focused margin arrow by 1 mm (Shift: 5 mm); Space or Enter shows its line. */
function handleKey(e, side) {
  const horizontal = side === 'left' || side === 'right';
  const step = e.shiftKey ? 5 : 1;
  const move = horizontal
    ? { ArrowLeft: -step, ArrowRight: step }[e.key]
    : { ArrowUp: -step, ArrowDown: step }[e.key];
  if (move) {
    const sign = side === 'left' || side === 'top' ? 1 : -1;
    setMargin(side, marginAt(side) + sign * move);
    saveSettings();
    e.preventDefault();
  } else if (e.key === ' ' || e.key === 'Enter') {
    toggleLine(side);
    e.preventDefault();
  } else if (e.key === 'Escape') {
    editor.focus();
    e.preventDefault();
  }
}

// ---------------------------------------------------------------- zoom

let zoom = 1;

function setZoom(value, remember) {
  zoom = Math.min(ZOOM_STEPS[ZOOM_STEPS.length - 1], Math.max(ZOOM_STEPS[0], value));
  $('deskContent').style.zoom = zoom;
  $('zoomLabel').textContent = `${Math.round(zoom * 100)}%`;
  $('zoomOutButton').disabled = zoom <= ZOOM_STEPS[0] + 0.001;
  $('zoomInButton').disabled = zoom >= ZOOM_STEPS[ZOOM_STEPS.length - 1] - 0.001;
  if (remember) { settings.zoom = zoom; saveSettings(); }
}

function stepZoom(direction) {
  const next = direction > 0
    ? ZOOM_STEPS.find((z) => z > zoom + 0.001) ?? ZOOM_STEPS[ZOOM_STEPS.length - 1]
    : [...ZOOM_STEPS].reverse().find((z) => z < zoom - 0.001) ?? ZOOM_STEPS[0];
  setZoom(next, true);
}

/** Until the child picks a zoom, the page fits the window (never larger than actual size). */
function fitZoom() {
  if (settings.zoom > 0) { setZoom(settings.zoom, false); return; }
  const deskContent = $('deskContent');
  deskContent.style.zoom = 1;
  const needed = deskContent.scrollWidth + 32;
  const fit = $('desk').clientWidth / needed;
  setZoom(Math.min(1, [...ZOOM_STEPS].reverse().find((z) => z <= fit) ?? ZOOM_STEPS[0]), false);
}

$('zoomInButton').onclick = () => stepZoom(1);
$('zoomOutButton').onclick = () => stepZoom(-1);
$('desk').addEventListener('wheel', (e) => {
  if (!e.ctrlKey) return;
  e.preventDefault();
  stepZoom(e.deltaY < 0 ? 1 : -1);
}, { passive: false });
window.addEventListener('resize', fitZoom);

// --------------------------------------------------------------- files

function currentLetter() {
  const letter = emptyLetter(currentSize);
  letter.margins = { ...settings.margins };
  letter.paragraphs = editor.paragraphs();
  return attachDesigns(letter, designs);
}

async function showLetter(letter, isFunLetter, keepMargins) {
  designs = new Designs();
  if (isFunLetter) designs.restore(letter.glyphs, letter.fonts, letter.currentFont);
  else designs.startFrom(loadMyLetters()); // existing text stays in the normal letters
  if (keepMargins) { settings.margins = { ...letter.margins }; saveSettings(); }
  await designs.ready();
  refreshTiles();
  closeDrawing();
  updateMargins();
  editor.load(letter.paragraphs);
  $('desk').scrollTo(0, 0);
  updateUndoButtons();
  checkOverflow();
}

function confirmDiscard() {
  return !dirty || window.confirm(`Your changes to ${fileName} aren't saved. Leave them and carry on?`);
}

async function openFile(file, handle) {
  const name = file.name;
  const ext = name.slice(name.lastIndexOf('.')).toLowerCase();
  let letter;
  try {
    const text = await file.text();
    letter = ext === EXTENSION || ext === '.sletter' ? fromXml(text) : fromPlainText(text, currentSize);
  } catch (err) {
    window.alert(`SimpleFunLetters couldn't open that file.\n\n${err.message}`);
    return;
  }
  const isFunLetter = ext === EXTENSION;
  await showLetter(letter, isFunLetter, ext === EXTENSION || ext === '.sletter');
  // Other files are brought in as a new letter: saving makes a .funletter.
  fileName = name.replace(/\.[^.]+$/, '');
  fileHandle = isFunLetter ? handle ?? null : null;
  dirty = !isFunLetter;
  updateTitle();
  editor.focus();
}

const pickerTypes = [{ description: 'Letters', accept: { 'application/xml': ['.funletter', '.sletter'], 'text/plain': ['.txt'] } }];

async function openLetter() {
  if (!confirmDiscard()) return;
  if (window.showOpenFilePicker) {
    try {
      const [handle] = await window.showOpenFilePicker({ types: pickerTypes });
      await openFile(await handle.getFile(), handle);
    } catch (err) { if (err.name !== 'AbortError') window.alert(`SimpleFunLetters couldn't open that file.\n\n${err.message}`); }
    return;
  }
  $('fileInput').value = '';
  $('fileInput').click();
}
$('fileInput').onchange = () => { const f = $('fileInput').files[0]; if (f) openFile(f, null); };

async function saveLetter() {
  const xml = toXml(currentLetter());
  try {
    if (!fileHandle && window.showSaveFilePicker) {
      fileHandle = await window.showSaveFilePicker({
        suggestedName: fileName + EXTENSION,
        types: [{ description: 'SimpleFunLetters letter', accept: { 'application/xml': [EXTENSION] } }],
      });
      fileName = fileHandle.name.replace(/\.[^.]+$/, '');
    }
    if (fileHandle) {
      const w = await fileHandle.createWritable();
      await w.write(new Blob([xml], { type: 'application/xml' }));
      await w.close();
    } else {
      // Browsers without the file picker: download the letter.
      const a = document.createElement('a');
      a.href = URL.createObjectURL(new Blob([xml], { type: 'application/xml' }));
      a.download = fileName + EXTENSION;
      document.body.append(a);
      a.click();
      a.remove();
      setTimeout(() => URL.revokeObjectURL(a.href), 10000);
    }
    dirty = false;
    updateTitle();
  } catch (err) {
    if (err.name !== 'AbortError') window.alert(`SimpleFunLetters couldn't save the file.\n\n${err.message}`);
  }
  editor.focus();
}

function printLetter() {
  // Every printed page gets the letter's margins.
  let style = $('printPage');
  if (!style) { style = document.createElement('style'); style.id = 'printPage'; document.head.append(style); }
  const m = settings.margins;
  style.textContent = `@page { size: A4; margin: ${m.top}mm ${m.right}mm ${m.bottom}mm ${m.left}mm; }`;
  window.print();
}

$('openButton').onclick = openLetter;
$('saveButton').onclick = saveLetter;
$('printButton').onclick = printLetter;

document.addEventListener('keydown', (e) => {
  if (!(e.ctrlKey || e.metaKey) || e.altKey) return;
  const key = e.key.toLowerCase();
  if (key === 'o') { e.preventDefault(); openLetter(); }
  else if (key === 's') { e.preventDefault(); saveLetter(); }
  else if (key === 'p') { e.preventDefault(); printLetter(); }
});

window.addEventListener('beforeunload', (e) => { if (dirty) e.preventDefault(); });

// --------------------------------------------------------------- start

async function start() {
  await document.fonts.load(`16px "${NORMAL_FONT}"`).catch(() => {});
  buildTiles();
  buildMargins();
  updateSizeControls();
  fitZoom();
  const letter = emptyLetter(currentSize);
  letter.margins = { ...settings.margins };
  await showLetter(letter, false, false);
  dirty = false;
  updateTitle();
  editor.focus();
}

start();
