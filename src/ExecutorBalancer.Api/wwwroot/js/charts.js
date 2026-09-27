// Графики на SVG без сторонних библиотек (CSP разрешает только свои скрипты).
// Подписи и подсказки — только через textContent; данные в разметку не вставляются.
// Правила оформления: тонкие метки (столбец ≤ 24px со скруглённым концом, линия 2px), сетка — тонкая и
// приглушённая, подписи — цветом текста, а не цветом ряда; подписи выборочные, остальное — в подсказке.

const NS = 'http://www.w3.org/2000/svg';
const number = new Intl.NumberFormat('ru-RU', { maximumFractionDigits: 2 });
const compact = new Intl.NumberFormat('ru-RU', { notation: 'compact', maximumFractionDigits: 1 });
const BAR = 24;

function node(tag, attrs, parent) {
  const n = document.createElementNS(NS, tag);
  for (const [k, v] of Object.entries(attrs || {})) n.setAttribute(k, String(v));
  if (parent) parent.append(n);
  return n;
}

function text(parent, x, y, value, cls = 'axis', anchor = 'middle') {
  const t = node('text', { x, y, 'text-anchor': anchor, class: cls }, parent);
  t.textContent = value;
  return t;
}

/** Круглый шаг делений: 1, 2, 5 × 10ⁿ, чтобы подписи были 0 / 500 / 1 000, а не 625 / 1 250. */
function niceStep(raw) {
  if (raw <= 0) return 1;
  const power = 10 ** Math.floor(Math.log10(raw));
  for (const m of [1, 2, 5, 10]) if (raw <= m * power) return m * power;
  return 10 * power;
}

/** Шкала от 0: максимум — целое число шагов (3–5 делений). */
function scale(value) {
  const step = niceStep(Math.max(value, 1) / 4);
  return { max: Math.ceil(Math.max(value, 1) / step) * step, step };
}

function frame(container, height) {
  const width = Math.max(280, container.clientWidth || 600);
  const svg = node('svg', { viewBox: `0 0 ${width} ${height}`, role: 'img', class: 'svg-chart' });
  container.classList.remove('is-empty');
  container.replaceChildren(svg);
  return { svg, width };
}

function empty(container, message) {
  const p = document.createElement('p');
  p.className = 'chart-empty';
  p.textContent = message;
  // пустой график не держит высоту под оси — одна строка вместо пустого поля в полэкрана
  container.classList.add('is-empty');
  container.replaceChildren(p);
}

function yGrid(svg, { max, step }, left, top, plotW, plotH, format = (v) => number.format(v)) {
  for (let v = 0; v <= max + step / 2; v += step) {
    const y = top + plotH - (v / max) * plotH;
    node('line', { x1: left, x2: left + plotW, y1: y, y2: y, class: v === 0 ? 'baseline' : 'grid' }, svg);
    text(svg, left - 8, y + 4, format(v), 'axis', 'end');
  }
}

/** Столбец со скруглённым верхом, квадратный у основания. */
function columnPath(x, y, w, h) {
  if (h <= 0) return '';
  const r = Math.min(4, w / 2, h);
  return `M${x},${y + h}V${y + r}Q${x},${y} ${x + r},${y}H${x + w - r}Q${x + w},${y} ${x + w},${y + r}V${y + h}Z`;
}

/** Полоса со скруглённым концом, от левого или правого основания. */
function barPath(x, y, w, h, toLeft = false) {
  if (w <= 0) return '';
  const r = Math.min(4, h / 2, w);
  return toLeft
    ? `M${x + w},${y}H${x + r}Q${x},${y} ${x},${y + r}V${y + h - r}Q${x},${y + h} ${x + r},${y + h}H${x + w}Z`
    : `M${x},${y}H${x + w - r}Q${x + w},${y} ${x + w},${y + r}V${y + h - r}Q${x + w},${y + h} ${x + w - r},${y + h}H${x}Z`;
}

// ---------- подсказка при наведении: одна на страницу ----------

let tipBox = null;

function tooltip() {
  if (!tipBox) {
    tipBox = document.createElement('div');
    tipBox.className = 'chart-tip hidden';
    tipBox.setAttribute('role', 'status');
    document.body.append(tipBox);
  }
  return tipBox;
}

/** rows = [[название, значение, класс-метки?]]; title — заголовок. */
function showTip(event, title, rows) {
  const box = tooltip();
  const head = document.createElement('div');
  head.className = 'chart-tip-title';
  head.textContent = title;
  const list = rows.map(([label, value, swatch]) => {
    const line = document.createElement('div');
    line.className = 'chart-tip-row';
    const key = document.createElement('span');
    if (swatch) {
      const mark = document.createElement('i');
      mark.className = `key ${swatch}`;
      key.append(mark);
    }
    key.append(label);
    const val = document.createElement('b');
    val.textContent = value;
    line.append(key, val);
    return line;
  });
  box.replaceChildren(head, ...list);
  box.classList.remove('hidden');
  const pad = 14;
  const { innerWidth, innerHeight } = window;
  const rect = box.getBoundingClientRect();
  let x = event.clientX + pad;
  let y = event.clientY + pad;
  if (x + rect.width > innerWidth - 8) x = event.clientX - rect.width - pad;
  if (y + rect.height > innerHeight - 8) y = event.clientY - rect.height - pad;
  box.style.left = `${Math.max(8, x)}px`;
  box.style.top = `${Math.max(8, y)}px`;
}

function hideTip() { tipBox?.classList.add('hidden'); }

/** Невидимая зона наведения крупнее самой метки — не нужно попадать точно в точку. */
function hover(svg, attrs, onMove) {
  const zone = node('rect', { ...attrs, class: 'hit' }, svg);
  zone.addEventListener('pointermove', onMove);
  zone.addEventListener('pointerleave', () => { hideTip(); zone.dispatchEvent(new Event('chart-leave')); });
  return zone;
}

// ---------- столбцы по времени ----------

/**
 * Одна величина по времени (назначения по минутам, активность по дням): points = [{ label, value, title }].
 * Последний столбец — текущий интервал — выделен цветом, остальные спокойнее.
 */
export function columns(container, points, { height = 170, empty: message = 'Нет данных', unit = '' } = {}) {
  if (!points.length || points.every((p) => !p.value)) { empty(container, message); return; }
  const { svg, width } = frame(container, height);
  const left = 40, right = 8, top = 16, bottom = 24;
  const plotW = width - left - right, plotH = height - top - bottom;
  const axis = scale(Math.max(...points.map((p) => p.value)));
  const { max } = axis;
  yGrid(svg, axis, left, top, plotW, plotH, (v) => compact.format(v));
  const step = plotW / points.length;
  const w = Math.max(2, Math.min(BAR, step - 2));
  const peak = points.reduce((best, p, i) => (p.value > points[best].value ? i : best), 0);
  points.forEach((p, i) => {
    const h = (p.value / max) * plotH;
    const x = left + step * i + (step - w) / 2;
    const cls = i === points.length - 1 ? 'mark-accent' : 'mark-soft';
    if (h > 0) node('path', { d: columnPath(x, top + plotH - h, w, h), class: cls }, svg);
    if (i === peak && p.value > 0) text(svg, x + w / 2, top + plotH - h - 5, number.format(p.value), 'value');
  });
  const every = Math.ceil(points.length / Math.max(1, Math.floor(plotW / 64)));
  points.forEach((p, i) => {
    if (i % every === 0 || i === points.length - 1) text(svg, left + step * i + step / 2, height - 6, p.label);
  });
  const cursor = node('rect', { x: 0, y: top, width: step, height: plotH, class: 'cursor hidden' }, svg);
  const zone = hover(svg, { x: left, y: top, width: plotW, height: plotH }, (event) => {
    const box = svg.getBoundingClientRect();
    const i = Math.min(points.length - 1, Math.max(0, Math.floor(((event.clientX - box.left) * (width / box.width) - left) / step)));
    cursor.setAttribute('x', left + step * i);
    cursor.classList.remove('hidden');
    showTip(event, points[i].label, [[points[i].title || 'значение', `${number.format(points[i].value)}${unit}`]]);
  });
  zone.addEventListener('chart-leave', () => cursor.classList.add('hidden'));
}

// ---------- сгруппированные столбцы: динамика ----------

/**
 * Несколько величин за каждый интервал (назначено, решено, на доработку по часам или дням):
 * labels — подписи интервалов, series = [{ name, cls, values }]. Столбцы, а не линии: это итоги за интервал,
 * и их бывает мало (на демонстрации всё происходит за час-два). Подпись — только у максимума главной величины.
 */
export function groupedColumns(container, labels, series, { height = 240 } = {}) {
  if (!labels.length || series.every((s) => s.values.every((v) => !v))) { empty(container, 'За период ещё нет данных'); return; }
  const { svg, width } = frame(container, height);
  const left = 44, right = 8, top = 22, bottom = 26;
  const plotW = width - left - right, plotH = height - top - bottom;
  const axis = scale(Math.max(1, ...series.flatMap((s) => s.values)));
  const { max } = axis;
  yGrid(svg, axis, left, top, plotW, plotH, (v) => compact.format(v));
  const n = labels.length;
  const step = plotW / n;
  const w = Math.max(2, Math.min(BAR, (step - 10) / series.length - 2));
  const groupW = series.length * w + (series.length - 1) * 2;
  const main = series[0].values;
  const peak = main.reduce((best, v, i) => (v > main[best] ? i : best), 0);
  labels.forEach((label, i) => {
    const x0 = left + step * i + (step - groupW) / 2;
    series.forEach((s, k) => {
      const v = s.values[i];
      const h = (v / max) * plotH;
      if (h > 0) node('path', { d: columnPath(x0 + k * (w + 2), top + plotH - h, w, h), class: `col ${s.cls}` }, svg);
    });
    if (i === peak && main[i] > 0) text(svg, x0 + w / 2, top + plotH - (main[i] / max) * plotH - 7, number.format(main[i]), 'value');
  });
  const every = Math.ceil(n / Math.max(1, Math.floor(plotW / 64)));
  labels.forEach((label, i) => { if (i % every === 0 || i === n - 1) text(svg, left + step * i + step / 2, height - 7, label); });
  const cursor = node('rect', { x: 0, y: top, width: step, height: plotH, class: 'cursor hidden' }, svg);
  const zone = hover(svg, { x: left, y: top, width: plotW, height: plotH }, (event) => {
    const box = svg.getBoundingClientRect();
    const i = Math.min(n - 1, Math.max(0, Math.floor(((event.clientX - box.left) * (width / box.width) - left) / step)));
    cursor.setAttribute('x', left + step * i);
    cursor.classList.remove('hidden');
    showTip(event, labels[i], series.map((s) => [s.name, number.format(s.values[i]), s.cls]));
  });
  zone.addEventListener('chart-leave', () => cursor.classList.add('hidden'));
}

// ---------- горизонтальные полосы: доли ----------

/** Доли одного целого, по убыванию: items = [{ label, value, hint }]. Значение и процент — у конца полосы. */
export function bars(container, items, { empty: message = 'Нет данных' } = {}) {
  const total = items.reduce((sum, i) => sum + i.value, 0);
  if (!total) { empty(container, message); return; }
  const sorted = [...items].sort((a, b) => b.value - a.value);
  const rowH = 34;
  const height = sorted.length * rowH + 4;
  const { svg, width } = frame(container, height);
  const left = Math.min(150, width * 0.36), right = 92;
  const plotW = width - left - right;
  const max = sorted[0].value;
  sorted.forEach((item, i) => {
    const y = i * rowH + 4;
    const w = item.value ? Math.max(2, (item.value / max) * plotW) : 0;
    text(svg, left - 10, y + rowH / 2, item.label, 'label', 'end');
    node('rect', { x: left, y: y + (rowH - 16) / 2 - 3, width: plotW, height: 16, class: 'track' }, svg);
    if (w) node('path', { d: barPath(left, y + (rowH - 16) / 2 - 3, w, 16), class: 'mark-accent' }, svg);
    const share = Math.round((item.value / total) * 1000) / 10;
    text(svg, left + plotW + 10, y + rowH / 2, `${number.format(item.value)} · ${number.format(share)}%`, 'value', 'start');
    hover(svg, { x: 0, y, width, height: rowH }, (event) =>
      showTip(event, item.label, [['заявок', number.format(item.value)], ['доля', `${number.format(share)}%`],
        ...(item.hint ? [['что это', item.hint]] : [])]));
  });
}

// ---------- отклонение от справедливой доли ----------

/**
 * Кто получил больше или меньше справедливой доли: items = [{ label, value, assigned, fair }], value — % или null.
 * Серый коридор — норма ±limit%. Цвет — состояние: в норме, заметно, сильно; всегда вместе с числом.
 */
export function diverging(container, items, { limit = 2 } = {}) {
  const measured = items.filter((i) => i.value !== null && i.value !== undefined).sort((a, b) => b.value - a.value);
  const skipped = items.length - measured.length;
  if (!measured.length) {
    empty(container, 'Пока мало данных: отклонение считается, когда сотруднику причитается вес от 10.');
    return;
  }
  const rowH = 30;
  const top = 26;
  const height = top + measured.length * rowH + (skipped ? 24 : 8);
  const { svg, width } = frame(container, height);
  const left = Math.min(170, width * 0.32), right = 16;
  const plotW = width - left - right;
  const span = Math.max(limit * 3, ...measured.map((i) => Math.abs(i.value)));
  const zero = left + plotW / 2;
  const scale = (plotW / 2 - 64) / span; // запас под подпись значения
  const bandW = limit * scale;
  node('rect', { x: zero - bandW, y: top - 6, width: bandW * 2, height: measured.length * rowH + 6, class: 'band' }, svg);
  text(svg, zero - plotW / 4, 14, '← получил меньше', 'axis');
  text(svg, zero + plotW / 4, 14, 'получил больше →', 'axis');
  text(svg, zero, 14, `норма ±${limit}%`, 'axis-strong');
  node('line', { x1: zero, x2: zero, y1: top - 6, y2: top + measured.length * rowH, class: 'baseline' }, svg);
  measured.forEach((item, i) => {
    const y = top + i * rowH;
    const abs = Math.abs(item.value);
    const w = abs * scale;
    const cls = abs <= limit ? 'status-good' : abs <= limit * 2.5 ? 'status-warning' : 'status-critical';
    const label = item.label.length > 24 ? `${item.label.slice(0, 23)}…` : item.label;
    text(svg, left - 10, y + rowH / 2 + 4, label, 'label', 'end');
    if (w > 0) node('path', { d: barPath(item.value >= 0 ? zero : zero - w, y + 7, w, rowH - 14, item.value < 0), class: cls }, svg);
    const sign = item.value > 0 ? '+' : '';
    text(svg, item.value >= 0 ? zero + w + 6 : zero - w - 6, y + rowH / 2 + 4, `${sign}${number.format(item.value)}%`,
      'value', item.value >= 0 ? 'start' : 'end');
    hover(svg, { x: 0, y, width, height: rowH }, (event) => showTip(event, item.label, [
      ['получил вес', number.format(item.assigned ?? 0)], ['справедливо', number.format(item.fair ?? 0)],
      ['отклонение', `${sign}${number.format(item.value)}%`],
      ['оценка', abs <= limit ? 'в норме' : abs <= limit * 2.5 ? 'заметно' : 'сильно'],
    ]));
  });
  if (skipped) text(svg, left, height - 6, `ещё ${skipped}: мало данных для оценки`, 'axis', 'start');
}

// ---------- количество против качества ----------

/**
 * Точка — сотрудник: по X — сколько закрыл, по Y — качество, %. Ниже порога — зона «работы на количество»:
 * такие точки красные и подписаны; остальные — зелёные, имя в подсказке (подписывается лишь лидер по количеству).
 * points = [{ label, x, y, points }].
 */
export function scatter(container, items, { height = 300, threshold = 80, empty: message = 'Нет данных' } = {}) {
  if (!items.length) { empty(container, message); return; }
  const { svg, width } = frame(container, height);
  const left = 48, right = 24, top = 16, bottom = 40;
  const plotW = width - left - right, plotH = height - top - bottom;
  const xAxis = scale(Math.max(1, ...items.map((p) => p.x)));
  const maxX = xAxis.max;
  // ось Y — от чуть ниже худшего значения (или порога) до 100%: иначе все точки слипаются наверху
  const bottomValue = Math.min(threshold - 10, ...items.map((p) => p.y - 5));
  const yStep = 100 - bottomValue > 50 ? 20 : 10;
  const low = Math.max(0, Math.floor(bottomValue / yStep) * yStep);
  const sx = (v) => left + (v / maxX) * plotW;
  const sy = (v) => top + plotH - ((v - low) / (100 - low)) * plotH;
  node('rect', { x: left, y: sy(threshold), width: plotW, height: sy(low) - sy(threshold), class: 'zone-bad' }, svg);
  for (let v = low; v <= 100; v += yStep) {
    node('line', { x1: left, x2: left + plotW, y1: sy(v), y2: sy(v), class: v === low ? 'baseline' : 'grid' }, svg);
    text(svg, left - 8, sy(v) + 4, `${number.format(Math.round(v))}%`, 'axis', 'end');
  }
  for (let v = 0; v <= maxX + xAxis.step / 2; v += xAxis.step) text(svg, sx(v), top + plotH + 16, compact.format(v));
  text(svg, left + plotW / 2, height - 4, 'закрыто заявок →', 'axis');
  node('line', { x1: left, x2: left + plotW, y1: sy(threshold), y2: sy(threshold), class: 'threshold' }, svg);
  text(svg, left + plotW - 4, sy(threshold) - 6, `порог качества ${number.format(threshold)}%`, 'axis-strong', 'end');
  text(svg, left + 8, sy(low) - 8, 'зона «работы на количество»', 'zone-label', 'start');

  const leader = items.reduce((best, p) => (p.x > best.x ? p : best), items[0]);
  const placed = [];
  for (const p of [...items].sort((a, b) => a.y - b.y)) {
    const bad = p.y < threshold;
    node('circle', { cx: sx(p.x), cy: sy(p.y), r: 6, class: `dot ${bad ? 'status-critical' : 'status-good'}` }, svg);
    if (bad || p === leader) {
      // подпись не ставим поверх уже поставленной
      // справа от точки, если занято — слева, если и там занято — подпись остаётся в подсказке
      const ly = sy(p.y);
      // справа, слева, над или под точкой — где свободно; если нигде — имя остаётся в подсказке и в списке рядом
      const cx = sx(p.x);
      const spots = [
        { x: cx + 10, y: ly, anchor: 'start', x0: cx + 10 }, { x: cx - 10, y: ly, anchor: 'end', x0: cx - 90 },
        { x: cx, y: ly - 14, anchor: 'middle', x0: cx - 40 }, { x: cx, y: ly + 16, anchor: 'middle', x0: cx - 40 },
      ];
      const free = spots.find((spot) => spot.x0 >= left && spot.x0 + 80 <= left + plotW
        && !placed.some((q) => spot.x0 < q.x1 && spot.x0 + 80 > q.x0 && Math.abs(q.y - spot.y) < 14)
        && !items.some((o) => o !== p && Math.abs(sy(o.y) - spot.y) < 9 && sx(o.x) > spot.x0 - 6 && sx(o.x) < spot.x0 + 86));
      if (free) {
        text(svg, free.x, free.y, p.label, 'label', free.anchor);
        placed.push({ x0: free.x0, x1: free.x0 + 80, y: free.y });
      }
    }
  }
  // подсказка по ближайшей точке — попадать точно не нужно
  const ring = node('circle', { cx: 0, cy: 0, r: 10, class: 'focus-ring hidden' }, svg);
  const zone = hover(svg, { x: left, y: top, width: plotW, height: plotH }, (event) => {
    const box = svg.getBoundingClientRect();
    const px = (event.clientX - box.left) * (width / box.width);
    const py = (event.clientY - box.top) * (height / box.height);
    let best = null;
    let dist = Infinity;
    for (const p of items) {
      const d = Math.hypot(sx(p.x) - px, sy(p.y) - py);
      if (d < dist) { dist = d; best = p; }
    }
    if (!best || dist > 40) { hideTip(); ring.classList.add('hidden'); return; }
    ring.setAttribute('cx', sx(best.x));
    ring.setAttribute('cy', sy(best.y));
    ring.classList.remove('hidden');
    showTip(event, best.fullName || best.label, [['закрыто', number.format(best.x)], ['качество', `${number.format(best.y)}%`],
      ['баллы', number.format(best.points ?? 0)], ['оценка', best.y < threshold ? 'ниже порога — вне рейтинга' : 'в норме']]);
  });
  zone.addEventListener('chart-leave', () => ring.classList.add('hidden'));
}
