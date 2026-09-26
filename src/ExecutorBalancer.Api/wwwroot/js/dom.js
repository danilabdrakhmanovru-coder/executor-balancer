// Построение DOM только через textContent: данные из АИС и конструктора никогда не попадают в HTML-разметку.

export const $ = (id) => document.getElementById(id);

export function el(tag, text, cls) {
  const node = document.createElement(tag);
  if (text !== undefined && text !== null) node.textContent = String(text);
  if (cls) node.className = cls;
  return node;
}

export function row(cells, cls) {
  const tr = el('tr', null, cls);
  for (const cell of cells) {
    const td = el('td');
    if (cell instanceof Node) td.append(cell); else td.textContent = cell ?? '—';
    tr.append(td);
  }
  return tr;
}

export function emptyRow(columns, text) {
  const tr = el('tr');
  const td = el('td', text, 'muted empty');
  td.colSpan = columns;
  tr.append(td);
  return tr;
}

export function badge(text, cls) { return el('span', text, `badge ${cls || ''}`); }

export function button(text, onClick, cls = 'ghost small') {
  const b = el('button', text, cls);
  b.type = 'button';
  b.addEventListener('click', onClick);
  return b;
}

export function actions(...buttons) {
  const wrap = el('span', null, 'row-actions');
  wrap.append(...buttons);
  return wrap;
}

const number = new Intl.NumberFormat('ru-RU', { maximumFractionDigits: 3 });
export const fmt = (value) => (value === null || value === undefined ? '—' : number.format(value));
export const fmtTime = (iso) => new Date(iso).toLocaleTimeString('ru-RU');
export const fmtDateTime = (iso) => new Date(iso).toLocaleString('ru-RU', { dateStyle: 'short', timeStyle: 'medium' });

/** Отклонение в процентах: зелёное до ±2%, жёлтое до ±5%, дальше красное. */
export function deviation(value) {
  if (value === null || value === undefined) return el('span', '—', 'muted');
  const abs = Math.abs(value);
  const cls = abs <= 2 ? 'ok' : abs <= 5 ? 'warn' : 'bad';
  return el('span', `${value > 0 ? '+' : ''}${number.format(value)}%`, `dev ${cls}`);
}

let toastTimer = null;
export function toast(text, kind = 'ok') {
  const node = $('toast');
  node.textContent = text;
  node.className = `toast ${kind}`;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => node.classList.add('hidden'), 3500);
}

export function field(label, control, hint) {
  const wrap = el('label', null, 'field');
  wrap.append(el('span', label, 'field-label'), control);
  if (hint) wrap.append(el('small', hint, 'muted'));
  return wrap;
}

export function input(type, value, attrs = {}) {
  const node = el('input');
  node.type = type;
  if (value !== undefined && value !== null) node.value = String(value);
  for (const [k, v] of Object.entries(attrs)) node.setAttribute(k, v);
  return node;
}

export function select(options, value, attrs = {}) {
  const node = el('select');
  for (const [optionValue, label] of options) {
    const option = el('option', label);
    option.value = optionValue;
    node.append(option);
  }
  if (value !== undefined && value !== null) node.value = String(value);
  for (const [k, v] of Object.entries(attrs)) node.setAttribute(k, v);
  return node;
}

export function checkbox(checked, label) {
  const wrap = el('label', null, 'check');
  const box = input('checkbox');
  box.checked = Boolean(checked);
  wrap.append(box, el('span', label));
  return { wrap, box };
}
