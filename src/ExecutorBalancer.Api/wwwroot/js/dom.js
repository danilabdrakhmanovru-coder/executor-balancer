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
  const td = el('td', text, 'empty-cell');
  td.colSpan = columns;
  tr.append(td);
  return tr;
}

const BADGE = { ok: 'bg-green-lt', bad: 'bg-red-lt', warn: 'bg-yellow-lt' };

export function badge(text, cls) { return el('span', text, `badge ${BADGE[cls] || 'bg-secondary-lt'}`); }

/** Иконка из спрайта icons.svg (Tabler Icons, MIT). */
export function icon(name, cls = '') {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('class', `icon ${cls}`.trim());
  svg.setAttribute('aria-hidden', 'true');
  const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
  use.setAttribute('href', `icons.svg#${name}`);
  svg.append(use);
  return svg;
}

/** Кнопка Tabler: cls — классы btn, iconName — иконка перед текстом. */
export function button(text, onClick, cls = 'btn btn-sm', iconName = null) {
  const b = el('button', null, cls);
  b.type = 'button';
  if (iconName) b.append(icon(iconName));
  b.append(text);
  b.addEventListener('click', onClick);
  return b;
}

/** Плитка-показатель: колонка сетки Tabler с карточкой, иконкой, значением и пояснением. */
export function tile(label, value, hint, warn, iconName) {
  const col = el('div', null, 'col-6 col-md-4 col-xl-3');
  const card = el('div', null, `card tile h-100${warn ? ' warn' : ''}`);
  const body = el('div', null, 'card-body d-flex gap-3 align-items-start');
  if (iconName) {
    const badgeIcon = el('span', null, `avatar ${warn ? 'bg-yellow-lt' : 'bg-primary-lt'}`);
    badgeIcon.append(icon(iconName));
    body.append(badgeIcon);
  }
  const text = el('div');
  text.append(el('div', label, 'subheader'), el('div', value, 'tile-value'));
  if (hint) text.append(el('div', hint, 'tile-hint'));
  body.append(text);
  card.append(body);
  col.append(card);
  return col;
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
  node.className = `toast-box ${kind}`;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => node.classList.add('hidden'), 3500);
}

export function field(label, control, hint) {
  const wrap = el('label', null, 'field d-block');
  wrap.append(el('span', label, 'form-label'), control);
  if (hint) wrap.append(el('small', hint, 'form-hint'));
  return wrap;
}

export function input(type, value, attrs = {}) {
  const node = el('input', null, type === 'checkbox' ? 'form-check-input' : 'form-control');
  node.type = type;
  if (value !== undefined && value !== null) node.value = String(value);
  for (const [k, v] of Object.entries(attrs)) node.setAttribute(k, v);
  return node;
}

export function select(options, value, attrs = {}) {
  const node = el('select', null, 'form-select');
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
  const wrap = el('label', null, 'form-check');
  const box = input('checkbox');
  box.checked = Boolean(checked);
  wrap.append(box, el('span', label, 'form-check-label'));
  return { wrap, box };
}
