// Простые SVG-графики без сторонних библиотек (CSP разрешает только свои скрипты).
// Подписи — через textContent и <title>, данные в разметку не вставляются.

const NS = 'http://www.w3.org/2000/svg';
const number = new Intl.NumberFormat('ru-RU', { maximumFractionDigits: 2 });

function node(tag, attrs, parent) {
  const n = document.createElementNS(NS, tag);
  for (const [k, v] of Object.entries(attrs || {})) n.setAttribute(k, String(v));
  if (parent) parent.append(n);
  return n;
}

function text(parent, x, y, value, cls, anchor = 'middle') {
  const t = node('text', { x, y, 'text-anchor': anchor, class: cls || 'axis' }, parent);
  t.textContent = value;
  return t;
}

function tip(parent, value) {
  const t = node('title', null, parent);
  t.textContent = value;
}

function niceMax(value) {
  if (value <= 0) return 1;
  const power = 10 ** Math.floor(Math.log10(value));
  for (const step of [1, 2, 2.5, 5, 10]) {
    if (value <= step * power) return step * power;
  }
  return 10 * power;
}

function frame(container, height) {
  const width = Math.max(280, container.clientWidth || 600);
  const svg = node('svg', { viewBox: `0 0 ${width} ${height}`, role: 'img', class: 'svg-chart' });
  container.replaceChildren(svg);
  return { svg, width };
}

function yAxis(svg, max, left, top, plotW, plotH) {
  for (const f of [0, 0.5, 1]) {
    const y = top + plotH - f * plotH;
    node('line', { x1: left, x2: left + plotW, y1: y, y2: y, class: 'grid' }, svg);
    text(svg, left - 6, y + 4, number.format(max * f), 'axis', 'end');
  }
}

function xLabels(svg, labels, left, step, y, every) {
  labels.forEach((label, i) => {
    if (i % every === 0 || i === labels.length - 1) text(svg, left + step * i + step / 2, y, label);
  });
}

/** Столбцы одной серии: points = [{ label, value, title }]. */
export function columns(container, points, { height = 150, cls = 'series-a', empty = 'Нет данных' } = {}) {
  if (!points.length) { container.textContent = empty; return; }
  const { svg, width } = frame(container, height);
  const left = 40, right = 8, top = 10, bottom = 22;
  const plotW = width - left - right, plotH = height - top - bottom;
  const max = niceMax(Math.max(...points.map((p) => p.value)));
  yAxis(svg, max, left, top, plotW, plotH);
  const step = plotW / points.length;
  points.forEach((p, i) => {
    const h = (p.value / max) * plotH;
    const bar = node('rect', {
      x: left + step * i + step * 0.15, y: top + plotH - h, width: Math.max(1, step * 0.7), height: Math.max(0, h), class: cls,
    }, svg);
    tip(bar, p.title || `${p.label}: ${number.format(p.value)}`);
  });
  xLabels(svg, points.map((p) => p.label), left, step, height - 6, Math.ceil(points.length / Math.max(1, Math.floor(plotW / 60))));
}

/** Несколько линий: labels — подписи по оси X, series = [{ name, cls, values }]. */
export function lines(container, labels, series, { height = 220 } = {}) {
  if (!labels.length) { container.textContent = 'Нет данных'; return; }
  const { svg, width } = frame(container, height);
  const left = 44, right = 12, top = 12, bottom = 24;
  const plotW = width - left - right, plotH = height - top - bottom;
  const max = niceMax(Math.max(1, ...series.flatMap((s) => s.values)));
  yAxis(svg, max, left, top, plotW, plotH);
  const step = plotW / labels.length;
  const x = (i) => left + step * i + step / 2;
  const y = (v) => top + plotH - (v / max) * plotH;
  for (const s of series) {
    node('polyline', { points: s.values.map((v, i) => `${x(i)},${y(v)}`).join(' '), class: `line ${s.cls}` }, svg);
    s.values.forEach((v, i) => {
      const dot = node('circle', { cx: x(i), cy: y(v), r: labels.length > 40 ? 1.5 : 3, class: `point ${s.cls}` }, svg);
      tip(dot, `${labels[i]} — ${s.name}: ${number.format(v)}`);
    });
  }
  xLabels(svg, labels, left, step, height - 6, Math.ceil(labels.length / Math.max(1, Math.floor(plotW / 70))));
}

/**
 * Горизонтальные столбцы от нуля в обе стороны — отклонение от справедливой доли.
 * items = [{ label, value }], value в процентах или null (мало данных).
 */
export function diverging(container, items, { limit = 2 } = {}) {
  const measured = items.filter((i) => i.value !== null && i.value !== undefined);
  if (!measured.length) { container.textContent = 'Пока мало данных: отклонение считается, когда исполнителю причитается вес от 10.'; return; }
  const rowH = 22;
  const height = measured.length * rowH + 30;
  const { svg, width } = frame(container, height);
  const left = Math.min(200, width * 0.38), right = 56, top = 8;
  const plotW = width - left - right;
  const span = Math.max(limit * 2.5, ...measured.map((i) => Math.abs(i.value)));
  const zero = left + plotW / 2;
  // запас под подпись значения, чтобы она не наезжала на имя исполнителя
  const scale = Math.max(1, plotW / 2 - 56) / span;
  // коридор допустимого отклонения
  node('rect', { x: zero - limit * scale, y: top, width: limit * 2 * scale, height: measured.length * rowH, class: 'band' }, svg);
  node('line', { x1: zero, x2: zero, y1: top, y2: top + measured.length * rowH, class: 'zero' }, svg);
  measured.forEach((item, i) => {
    const y = top + i * rowH;
    const w = Math.abs(item.value) * scale;
    const abs = Math.abs(item.value);
    const cls = abs <= limit ? 'dev-ok' : abs <= limit * 2.5 ? 'dev-warn' : 'dev-bad';
    const bar = node('rect', { x: item.value >= 0 ? zero : zero - w, y: y + 4, width: Math.max(1, w), height: rowH - 8, class: cls }, svg);
    tip(bar, `${item.label}: ${item.value > 0 ? '+' : ''}${number.format(item.value)}%`);
    const label = item.label.length > 28 ? `${item.label.slice(0, 27)}…` : item.label;
    text(svg, left - 8, y + rowH / 2 + 4, label, 'axis', 'end');
    text(svg, item.value >= 0 ? zero + w + 4 : zero - w - 4, y + rowH / 2 + 4,
      `${item.value > 0 ? '+' : ''}${number.format(item.value)}%`, 'axis', item.value >= 0 ? 'start' : 'end');
  });
  text(svg, zero, height - 6, `коридор ±${limit}%`, 'axis');
}

/**
 * «Количество против качества»: точка — сотрудник, по X — сколько закрыл, по Y — качество в процентах.
 * Пунктир — порог, ниже которого режим «больше нормы» приостанавливается.
 */
export function scatter(container, points, { height = 240, threshold = null, empty = 'Нет данных' } = {}) {
  if (!points.length) { container.textContent = empty; return; }
  const { svg, width } = frame(container, height);
  const left = 44, right = 16, top = 12, bottom = 34;
  const plotW = width - left - right, plotH = height - top - bottom;
  const maxX = niceMax(Math.max(1, ...points.map((p) => p.x)));
  const y = (value) => top + plotH - (value / 100) * plotH;
  yAxis(svg, 100, left, top, plotW, plotH);
  for (const f of [0, 0.5, 1]) text(svg, left + f * plotW, height - 18, number.format(maxX * f));
  text(svg, left + plotW / 2, height - 3, 'закрыто заявок →');
  if (threshold !== null) {
    node('line', { x1: left, x2: left + plotW, y1: y(threshold), y2: y(threshold), class: 'threshold' }, svg);
    text(svg, left + plotW - 2, y(threshold) - 4, `порог ${number.format(threshold)}%`, 'axis', 'end');
  }
  for (const p of points) {
    const cx = left + (p.x / maxX) * plotW;
    const dot = node('circle', { cx, cy: y(p.y), r: 6, class: p.cls || 'dev-ok' }, svg);
    tip(dot, p.title || `${p.label}: ${p.x} закрыто, качество ${number.format(p.y)}%`);
    text(svg, cx + 9, y(p.y) + 4, p.label, 'axis', 'start');
  }
}
