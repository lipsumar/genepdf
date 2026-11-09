// GenePdf studio: edit templates + data, render through the real pipeline, see problems and embedded metadata.
'use strict';

const MONACO = 'https://cdnjs.cloudflare.com/ajax/libs/monaco-editor/0.52.2/min/vs';
const $ = (id) => document.getElementById(id);

const state = {
  templates: null,
  readOnly: false,
  doc: null,            // { name, version, layout, layoutVersion }
  files: [],            // { path, role, saved }   (templates)
  samples: [],          // { name, path, saved }
  sample: null,         // current sample path
  active: null,         // path of the file in the editor
  models: new Map(),    // path -> monaco model
  view: 'pdf',
  pdfUrl: null,
  renderSeq: 0,
  lastProblems: [],
};

let monaco, editor, renderTimer;

// ------------------------------------------------------------------ boot

window.MonacoEnvironment = {
  // Monaco's workers must be same-origin: load them through a data: URL that imports from the CDN.
  getWorkerUrl: () => `data:text/javascript;charset=utf-8,${encodeURIComponent(
    `self.MonacoEnvironment={baseUrl:'${MONACO}/../'};importScripts('${MONACO}/base/worker/workerMain.js');`)}`,
};

require.config({ paths: { vs: MONACO } });
require(['vs/editor/editor.main'], async () => {
  monaco = window.monaco;
  const dark = matchMedia('(prefers-color-scheme: dark)');
  editor = monaco.editor.create($('editor'), {
    automaticLayout: true, minimap: { enabled: false }, fontSize: 13, tabSize: 2,
    scrollBeyondLastLine: false, wordWrap: 'on', theme: dark.matches ? 'vs-dark' : 'vs',
  });
  dark.addEventListener('change', (e) => monaco.editor.setTheme(e.matches ? 'vs-dark' : 'vs'));
  editor.onDidChangeModelContent(() => { refreshTabs(); scheduleRender(); });
  editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, save);
  editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, render);
  registerCompletions();
  await loadTemplates();
});

async function api(path, options = {}) {
  const res = await fetch(path, { headers: { 'Content-Type': 'application/json' }, ...options });
  const body = res.headers.get('content-type')?.includes('json') ? await res.json() : null;
  if (!res.ok) { const err = new Error(body?.error || `${res.status} ${res.statusText}`); err.body = body; err.status = res.status; throw err; }
  return body;
}

async function loadTemplates(select) {
  state.templates = await api('/api/templates');
  state.readOnly = state.templates.readOnly;
  $('readOnlyBadge').hidden = !state.readOnly;
  $('saveBtn').disabled = state.readOnly;
  $('newVersionBtn').disabled = state.readOnly;

  const sel = $('documentSelect');
  sel.innerHTML = '';
  for (const d of state.templates.documents) {
    const group = document.createElement('optgroup');
    group.label = d.name;
    for (const v of [...d.versions].reverse()) group.append(new Option(`${d.name} · ${v}`, `${d.name}/${v}`));
    sel.append(group);
  }
  const [hashDoc, hashVersion, hashSample] = decodeURIComponent(location.hash.slice(1)).split('/');
  const wanted = select || (hashDoc && hashVersion ? `${hashDoc}/${hashVersion}` : null);
  if (wanted && [...sel.options].some((o) => o.value === wanted)) sel.value = wanted;
  if (sel.value) await openDocument(sel.value, hashSample);
}

// ------------------------------------------------------------------ documents & files

async function openDocument(key, sampleName) {
  if (hasUnsaved() && !confirm('Discard unsaved changes?')) { $('documentSelect').value = `${state.doc.name}/${state.doc.version}`; return; }
  const [name, version] = key.split('/');
  const data = await api(`/api/documents/${encodeURIComponent(name)}/${encodeURIComponent(version)}`);

  for (const m of state.models.values()) m.dispose();
  state.models.clear();
  state.doc = { name, version, layout: data.layout, layoutVersion: data.layoutVersion };
  state.files = data.files.map((f) => ({ path: f.path, role: f.role, saved: f.content ?? '' }));
  state.samples = data.samples.map((s) => ({ name: s.name, path: s.path, saved: s.content ?? '{}' }));
  if (!state.samples.length) {
    const path = `documents/${name}/${version}/samples/default.json`;
    state.samples.push({ name: 'default', path, saved: '{\n}\n' });
  }
  for (const f of [...state.files, ...state.samples]) state.models.set(f.path, monaco.editor.createModel(f.saved, languageOf(f.path), monaco.Uri.parse(`file:///${f.path}`)));

  $('layoutInfo').textContent = data.layout ? `layout ${data.layout} · ${data.layoutVersion}` : 'layout ?';
  $('layoutInfo').classList.toggle('warn', !!data.layoutError);
  $('layoutInfo').title = data.layoutError || "Layout pinned by the document's layout / layout-version attributes";

  const sampleSel = $('sampleSelect');
  sampleSel.innerHTML = '';
  for (const s of state.samples) sampleSel.append(new Option(s.name, s.path));
  const pick = state.samples.find((s) => s.name === sampleName) || state.samples[0];
  state.sample = pick.path;
  sampleSel.value = pick.path;

  setActive(state.files[0].path);
  updateHash();
  render();
}

function languageOf(path) {
  if (path.endsWith('.css')) return 'css';
  if (path.endsWith('.json')) return 'json';
  return 'xml';
}

function tabList() {
  const sample = state.samples.find((s) => s.path === state.sample);
  return [...state.files, { path: sample.path, role: 'data', label: `${sample.name}.json` }];
}

function setActive(path) {
  state.active = path;
  editor.setModel(state.models.get(path));
  const file = state.files.find((f) => f.path === path);
  const note = $('tabNote');
  if (file?.role === 'layout' || file?.role === 'stylesheet') {
    note.hidden = false;
    note.textContent = `Shared layout ${state.doc.layout} ${state.doc.layoutVersion}: changes affect every document pinned to this version. Create a new layout version for breaking changes.`;
  } else if (!file) {
    note.hidden = false;
    note.textContent = 'Sample data used for this preview. Saved next to the template, in samples/.';
  } else note.hidden = true;
  refreshTabs();
}

function isDirty(path) {
  const f = state.files.find((x) => x.path === path) || state.samples.find((x) => x.path === path);
  const m = state.models.get(path);
  return !!(f && m && m.getValue() !== f.saved);
}

function hasUnsaved() { return [...state.models.keys()].some(isDirty); }

function refreshTabs() {
  const tabs = $('tabs');
  tabs.innerHTML = '';
  for (const t of tabList()) {
    const b = document.createElement('button');
    b.className = 'tab' + (t.path === state.active ? ' active' : '');
    b.title = t.path;
    const label = t.label || t.path.split('/').pop();
    b.innerHTML = `<span class="role">${t.role}</span><span></span>${isDirty(t.path) ? '<span class="dirty" title="Unsaved"></span>' : ''}`;
    b.children[1].textContent = label;
    b.onclick = () => setActive(t.path);
    tabs.append(b);
  }
}

function updateHash() {
  const sample = state.samples.find((s) => s.path === state.sample);
  history.replaceState(null, '', `#${state.doc.name}/${state.doc.version}/${sample?.name ?? ''}`);
}

// ------------------------------------------------------------------ render

function scheduleRender() {
  if (!$('autoRender').checked) return;
  clearTimeout(renderTimer);
  renderTimer = setTimeout(render, 600);
}

async function render() {
  if (!state.doc) return;
  clearTimeout(renderTimer);
  const seq = ++state.renderSeq;
  const overrides = {};
  for (const f of state.files) if (isDirty(f.path)) overrides[f.path] = state.models.get(f.path).getValue();
  const json = state.models.get(state.sample).getValue();
  const html = state.view === 'html';

  document.querySelector('.preview').classList.add('busy');
  try {
    const r = await api('/api/render', { method: 'POST', body: JSON.stringify({ document: state.doc.name, version: state.doc.version, json, overrides, html }) });
    if (seq !== state.renderSeq) return; // a newer render was started
    const problems = r.diagnostics.map((d) => ({ severity: 'warning', ...d }));
    if (html) {
      $('htmlFrame').srcdoc = r.html;
      setStatus(`HTML stage${r.footer ? ' · footer template present' : ''}`);
    } else {
      const bytes = Uint8Array.from(atob(r.pdf), (c) => c.charCodeAt(0));
      if (state.pdfUrl) URL.revokeObjectURL(state.pdfUrl);
      state.pdfUrl = URL.createObjectURL(new Blob([bytes], { type: 'application/pdf' }));
      $('pdfFrame').src = state.pdfUrl + '#view=FitH&navpanes=0';
      const a = $('downloadLink');
      a.href = state.pdfUrl;
      a.download = `${state.doc.name}-${state.doc.version}.pdf`;
      a.hidden = false;
      setStatus(`<span class="badge">PDF/A-2b</span> · ${r.elapsedMs} ms · ${(r.size / 1024).toFixed(0)} KB · “${escapeHtml(r.title)}”`, false, true);
      showMetadata(r.metadata);
    }
    $('previewEmpty').hidden = true;
    showProblems(problems);
  } catch (e) {
    if (seq !== state.renderSeq) return;
    const b = e.body || {};
    setStatus(`Error: ${escapeHtml(b.error || e.message)}`, true, true);
    showProblems([{ severity: 'error', message: b.error || e.message, file: b.file, line: b.line }]);
  } finally {
    if (seq === state.renderSeq) document.querySelector('.preview').classList.remove('busy');
  }
}

function setStatus(text, isError = false, isHtml = false) {
  const s = $('status');
  s.classList.toggle('error', isError);
  if (isHtml) s.innerHTML = text; else s.textContent = text;
}

function showProblems(problems) {
  state.lastProblems = problems;
  const ul = $('problems');
  ul.innerHTML = '';
  const count = $('problemCount');
  count.textContent = problems.length;
  count.className = 'count' + (problems.some((p) => p.severity === 'error') ? ' has-errors' : problems.length ? ' has-warnings' : '');
  if (!problems.length) ul.innerHTML = '<li class="empty">No problems.</li>';
  for (const p of problems) {
    const li = document.createElement('li');
    li.innerHTML = '<span class="sev"></span><span class="msg"></span><span class="loc"></span>';
    li.children[0].textContent = p.severity;
    li.children[0].classList.add(p.severity);
    li.children[1].textContent = p.message;
    li.children[2].textContent = p.file ? `${p.file}${p.line ? ':' + p.line : ''}` : '';
    li.onclick = () => reveal(p);
    ul.append(li);
  }
  if (problems.some((p) => p.severity === 'error')) selectPanel('problems');

  // Editor markers
  for (const [path, model] of state.models) {
    const markers = problems.filter((p) => p.file === path || (p.file === 'data.json' && path === state.sample)).map((p) => ({
      severity: p.severity === 'error' ? monaco.MarkerSeverity.Error : monaco.MarkerSeverity.Warning,
      message: p.message,
      startLineNumber: p.line || 1, endLineNumber: p.line || 1, startColumn: 1, endColumn: model.getLineMaxColumn(Math.min(p.line || 1, model.getLineCount())),
    }));
    monaco.editor.setModelMarkers(model, 'genepdf', markers);
  }
}

function reveal(p) {
  const path = p.file === 'data.json' ? state.sample : p.file;
  if (!path || !state.models.has(path)) return;
  setActive(path);
  if (p.line) { editor.revealLineInCenter(p.line); editor.setPosition({ lineNumber: p.line, column: 1 }); }
  editor.focus();
}

function showMetadata(meta) {
  const t = $('metadata');
  t.innerHTML = '';
  const intro = t.insertRow();
  intro.innerHTML = '<td colspan="2" class="muted" style="font-family:inherit;padding-bottom:6px">Stored in the PDF\'s XMP metadata (genepdf: namespace), not visible on the page.</td>';
  for (const [k, v] of Object.entries(meta || {})) {
    const row = t.insertRow();
    row.innerHTML = '<th></th><td></td>';
    row.cells[0].textContent = k;
    row.cells[1].textContent = v;
  }
}

function escapeHtml(s) { return String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }

// ------------------------------------------------------------------ save & versions

async function save() {
  if (state.readOnly) return toast('Read-only demo: changes are kept in the browser only.');
  const dirty = [...state.files, ...state.samples].filter((f) => isDirty(f.path));
  if (!dirty.length) return toast('Nothing to save.');
  try {
    for (const f of dirty) {
      const content = state.models.get(f.path).getValue();
      await api('/api/files', { method: 'PUT', body: JSON.stringify({ path: f.path, content }) });
      f.saved = content;
    }
    refreshTabs();
    toast(`Saved ${dirty.length} file${dirty.length > 1 ? 's' : ''}.`);
  } catch (e) {
    toast(`Save failed: ${e.message}`);
  }
}

async function newVersion() {
  if (hasUnsaved()) return toast('Save your changes first.');
  const versions = state.templates.documents.find((d) => d.name === state.doc.name)?.versions ?? [];
  $('versionFrom').textContent = `${state.doc.name} ${state.doc.version}`;
  $('versionInput').value = suggestNext(versions[versions.length - 1] || state.doc.version);
  const dlg = $('versionDialog');
  dlg.showModal();
  dlg.onclose = async () => {
    if (dlg.returnValue !== 'ok') return;
    const v = $('versionInput').value.trim();
    try {
      await api(`/api/documents/${state.doc.name}/${state.doc.version}/copy`, { method: 'POST', body: JSON.stringify({ version: v }) });
      await loadTemplates(`${state.doc.name}/${v}`);
      toast(`Created ${state.doc.name} ${v}.`);
    } catch (e) { toast(e.message); }
  };
}

function suggestNext(v) {
  const parts = v.split('.');
  const last = parseInt(parts[parts.length - 1], 10);
  if (Number.isNaN(last)) return v + '.1';
  parts[parts.length - 1] = String(last + 1);
  return parts.join('.');
}

function toast(text) {
  const t = $('toast');
  t.textContent = text;
  t.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => (t.hidden = true), 2600);
}

// ------------------------------------------------------------------ completions for g: directives

function registerCompletions() {
  const snippets = [
    ['g:if', 'g:if="${1:condition}"', 'Keep the element only when the condition is true'],
    ['g:else-if', 'g:else-if="${1:condition}"', 'After a g:if sibling'],
    ['g:else', 'g:else=""', 'After a g:if / g:else-if sibling'],
    ['g:each', 'g:each="${1:item} in ${2:collection}"', 'Repeat the element for each item'],
    ['g:text', 'g:text="${1:expression}"', 'Replace the content with a value'],
    ['g:block', '<g:block g:if="${1:condition}">$0</g:block>', 'Invisible wrapper'],
    ['g:fill', '<g:fill slot="${1:subject}">$0</g:fill>', 'Fill a layout slot'],
    ['g:slot', '<g:slot name="${1:name}"/>', 'Layout: a place documents can fill'],
    ['g:page-number', '<g:page-number/>', 'Header/footer: current page'],
    ['g:page-count', '<g:page-count/>', 'Header/footer: total pages'],
    ['{{ }}', '{{ ${1:expression} }}', 'Insert a value'],
  ];
  monaco.languages.registerCompletionItemProvider('xml', {
    triggerCharacters: [':', '{'],
    provideCompletionItems(model, position) {
      const word = model.getWordUntilPosition(position);
      const line = model.getLineContent(position.lineNumber).slice(0, position.column - 1);
      const m = line.match(/(<?g:[\w-]*|\{\{?)$/);
      const start = m ? position.column - m[0].length : word.startColumn;
      const range = { startLineNumber: position.lineNumber, endLineNumber: position.lineNumber, startColumn: start, endColumn: position.column };
      return {
        suggestions: snippets.map(([label, insert, doc]) => ({
          label, documentation: doc, kind: monaco.languages.CompletionItemKind.Snippet,
          insertText: insert, insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet, range,
        })),
      };
    },
  });
}

// ------------------------------------------------------------------ UI wiring

$('documentSelect').onchange = (e) => openDocument(e.target.value);
$('sampleSelect').onchange = (e) => { state.sample = e.target.value; if (!state.files.some((f) => f.path === state.active)) setActive(state.sample); else refreshTabs(); updateHash(); render(); };
$('renderBtn').onclick = render;
$('saveBtn').onclick = save;
$('newVersionBtn').onclick = newVersion;
$('autoRender').onchange = (e) => { if (e.target.checked) render(); };

document.querySelectorAll('.segmented button').forEach((b) => b.onclick = () => {
  document.querySelectorAll('.segmented button').forEach((x) => x.classList.toggle('active', x === b));
  state.view = b.dataset.view;
  $('pdfFrame').hidden = state.view !== 'pdf';
  $('htmlFrame').hidden = state.view !== 'html';
  render();
});

function selectPanel(name) {
  document.querySelectorAll('.panel-tabs button').forEach((x) => x.classList.toggle('active', x.dataset.panel === name));
  for (const p of ['problems', 'metadata', 'help']) $(`panel-${p}`).hidden = p !== name;
}
document.querySelectorAll('.panel-tabs button').forEach((b) => b.onclick = () => selectPanel(b.dataset.panel));

window.addEventListener('keydown', (e) => {
  if ((e.metaKey || e.ctrlKey) && e.key === 's') { e.preventDefault(); save(); }
  if ((e.metaKey || e.ctrlKey) && e.key === 'Enter') { e.preventDefault(); render(); }
});
window.addEventListener('beforeunload', (e) => { if (hasUnsaved() && !state.readOnly) e.preventDefault(); });

// Resizable split, remembered per browser.
(() => {
  const pane = document.querySelector('.editor-pane');
  try { const w = localStorage.getItem('genepdf.split'); if (w) pane.style.flexBasis = w; } catch { /* storage unavailable */ }
  const splitter = $('splitter');
  splitter.addEventListener('pointerdown', (e) => {
    splitter.setPointerCapture(e.pointerId);
    splitter.classList.add('dragging');
    document.querySelectorAll('iframe').forEach((f) => (f.style.pointerEvents = 'none'));
    const move = (ev) => { pane.style.flexBasis = `${Math.min(Math.max(ev.clientX, 280), innerWidth - 320)}px`; };
    const up = () => {
      splitter.classList.remove('dragging');
      document.querySelectorAll('iframe').forEach((f) => (f.style.pointerEvents = ''));
      splitter.removeEventListener('pointermove', move);
      try { localStorage.setItem('genepdf.split', pane.style.flexBasis); } catch { /* ignore */ }
    };
    splitter.addEventListener('pointermove', move);
    splitter.addEventListener('pointerup', up, { once: true });
  });
})();
