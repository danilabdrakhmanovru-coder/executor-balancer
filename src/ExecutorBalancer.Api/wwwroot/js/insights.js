// «Разбор заявок»: спрос и покрытие за сутки (считает сервер) и ИИ-разбор по этим цифрам.
// Ответ модели — только текст: выводится через textContent, как и всё остальное.
import { api, problemText } from './api.js';
import { $, el, row, badge, button, fmt, emptyRow, tile, toast, icon } from './dom.js';
import { can } from './session.js';
import { currentDepartment } from './department.js';

let fieldKey = null;
let lastFields = [];
let lastAnalysis = null;
let running = false;
let configured = false;
let retryUntil = 0; // до этого момента новый разбор по отделу не запрашивается — модель отдала бы прежний
let countdown = null;

/** Кнопка разбора: «ИИ разбирает…», обратный отсчёт до нового разбора или готова. */
function renderButton() {
  const left = Math.ceil((retryUntil - Date.now()) / 1000);
  const button = $('ai-run');
  button.disabled = running || !configured || left > 0;
  $('ai-run-text').textContent = running ? 'ИИ разбирает…' : left > 0 ? `Новый разбор — через ${left} с` : 'Разобрать с ИИ';
  if (left <= 0 && countdown) { clearInterval(countdown); countdown = null; }
}

function waitBeforeNew(seconds) {
  retryUntil = Date.now() + Math.max(0, seconds) * 1000;
  if (seconds > 0 && !countdown) countdown = setInterval(renderButton, 1000);
  renderButton();
}

const LEVEL = { high: ['важно', 'bad'], medium: ['заметно', 'warn'], low: ['к сведению', 'ok'] };

function seconds(value) {
  if (value === null || value === undefined) return '—';
  if (value < 90) return `${fmt(Math.round(value))} с`;
  if (value < 5400) return `${fmt(Math.round(value / 60))} мин`;
  return `${fmt(Math.round(value / 360) / 10)} ч`;
}

function renderTiles(report) {
  const t = report.totals;
  const unmatchedShare = report.checked ? (report.unmatched * 100) / report.checked : 0;
  $('in-tiles').replaceChildren(
    tile('Пришло за сутки', fmt(t.received), `назначений ${fmt(t.assignments)}, решено ${fmt(t.closed)}`, false, 'file-text'),
    tile('Ждут исполнителя', fmt(t.waitingNow), t.waitingNow ? 'причины — в таблице ниже' : 'очереди нет', t.waitingNow > 0, 'hourglass'),
    tile('Ожидание исполнителя', seconds(t.waitMedianSeconds), `медиана; у 90% заявок — не дольше ${seconds(t.waitP90Seconds)}`, false, 'clock'),
    tile('Не подходят никому', `${fmt(Math.round(unmatchedShare * 10) / 10)}%`,
      report.checked ? `${fmt(report.unmatched)} из ${fmt(report.checked)} заявок — нет сотрудника с нужными навыками` : 'заявок пока нет',
      report.unmatched > 0, 'alert-triangle'),
  );
}

/** Хватает ли сил на такие заявки: словами, число — в подсказке. */
function tension(option) {
  if (!option.orders) return el('span', '—', 'text-secondary');
  if (option.executors === 0) return badge('некому брать', 'bad');
  const b = option.tension >= 1.5 ? badge('не хватает', 'bad') : option.tension > 1 ? badge('впритык', 'warn') : badge('хватает', 'ok');
  b.title = `напряжение ${fmt(option.tension)}: доля в заявках ${fmt(option.sharePercent)}% ÷ доля сил ${fmt(option.capacitySharePercent)}%`;
  return b;
}

function renderFields(fields) {
  lastFields = fields;
  const group = $('in-fields');
  if (!fields.length) {
    group.replaceChildren(el('span', 'У заявок отдела нет параметров-справочников — разрез не строится', 'text-secondary'));
    $('in-options').replaceChildren();
    return;
  }
  if (!fields.some((f) => f.key === fieldKey)) fieldKey = fields[0].key;
  group.replaceChildren(...fields.map((f) => {
    const b = el('button', f.label, `btn${f.key === fieldKey ? ' active' : ''}`);
    b.type = 'button';
    b.addEventListener('click', () => { fieldKey = f.key; renderFields(fields); });
    return b;
  }));
  const options = [...fields.find((f) => f.key === fieldKey).options]
    .sort((a, b) => b.orders - a.orders || a.executors - b.executors);
  $('in-options').replaceChildren(...options.map((o) => withValue(o.value, row([
    o.label || o.value,
    o.orders ? `${fmt(o.orders)} (${fmt(o.sharePercent)}%)` : '0',
    o.waiting ? badge(fmt(o.waiting), 'warn') : '0',
    fmt(o.reworked),
    fmt(o.executors),
    `${fmt(o.capacitySharePercent)}%`,
    tension(o),
  ], o.orders === 0 ? 'inactive' : ''))));
}

function renderLists(report) {
  const rows = report.blocked.map((b) => row([b.rule, fmt(b.orders), `${fmt(b.sharePercent)}%`]));
  const byRules = report.blocked.length ? Math.max(...report.blocked.map((b) => b.orders)) : 0;
  if (report.unmatched > byRules) {
    // по каждому правилу отдельно кто-то подходит, но всем сразу — никто: нет сотрудника с нужным сочетанием навыков
    const share = report.checked ? (report.unmatched * 100) / report.checked : 0;
    rows.push(row([el('span', 'Сочетание правил: по каждому отдельно кто-то подходит, но всем сразу — никто', 'text-secondary'),
      fmt(report.unmatched), `${fmt(Math.round(share * 10) / 10)}%`]));
  }
  $('in-blocked').replaceChildren(...(rows.length ? rows
    : [emptyRow(3, 'Таких правил нет: каждую заявку может взять хотя бы один сотрудник')]));
  $('in-waiting').replaceChildren(...(report.waiting.length
    ? report.waiting.map((w) => row([w.reason, fmt(w.orders)]))
    : [emptyRow(2, 'Сейчас никто не ждёт')]));
}

/** Строка таблицы помнит своё значение — к ней ведёт ссылка из отчёта ИИ. */
function withValue(value, tr) {
  tr.dataset.value = value;
  return tr;
}

/** Показать значение в «Спросе и покрытии»: нужный параметр, строка подсвечена. */
function showDemand(key, value) {
  if (!lastFields.some((f) => f.key === key)) return;
  fieldKey = key;
  renderFields(lastFields);
  const tr = [...$('in-options').querySelectorAll('tr')].find((r) => r.dataset.value === value);
  if (!tr) return;
  tr.classList.add('row-highlight');
  tr.scrollIntoView({ behavior: 'smooth', block: 'center' });
  setTimeout(() => tr.classList.remove('row-highlight'), 2500);
}

// разделы отчёта: название и иконка; порядок — как у находок (важное первым)
const CATEGORY = {
  skills: ['Навыки', 'users'], queue: ['Очередь', 'hourglass'], rules: ['Правила', 'adjustments'],
  quality: ['Качество', 'star'], fairness: ['Справедливость', 'scale'], data: ['Данные', 'info-circle'],
  ok: ['Всё в порядке', 'circle-check'],
};
const WHO = { руководитель: '', администратор: 'warn' };

/** Текст, где «С-3023» — ссылка на страницу сотрудника. Только узлы и textContent — без разметки из ответа модели. */
function linked(text, cls) {
  const node = el('span', null, cls);
  let last = 0;
  for (const m of String(text ?? '').matchAll(/[СC]-(\d{1,12})/g)) {
    node.append(text.slice(last, m.index));
    const a = el('a', m[0]);
    a.href = `#executor-${m[1]}`;
    node.append(a);
    last = m.index + m[0].length;
  }
  node.append(String(text ?? '').slice(last));
  return node;
}

function actionItem(a) {
  const li = el('li');
  li.append(badge(a.who, WHO[a.who] ?? ''), ' ', linked(a.text));
  if (a.effect) li.append(el('span', ` → ${a.effect}`, 'text-secondary'));
  return li;
}

function findingCard(f) {
  const [text, kind] = LEVEL[f.level] || LEVEL.medium;
  const card = el('div', null, 'ai-finding');
  const head = el('div', null, 'ai-finding-head');
  head.append(badge(text, kind), el('strong', f.title));
  card.append(head, linked(f.detail, 'd-block text-secondary'));
  if (f.why) {
    const why = el('p', null, 'ai-why mb-0');
    why.append(el('span', 'Почему так может быть: ', 'fw-medium'), linked(f.why));
    card.append(why);
  }
  if (f.actions.length) {
    const ul = el('ul', null, 'ai-actions');
    ul.append(...f.actions.map(actionItem));
    card.append(ul);
  }
  const links = el('div', null, 'ai-links');
  for (const id of f.executors) {
    const a = el('a', null, 'btn btn-sm btn-ghost-primary');
    a.href = `#executor-${id}`;
    a.append(icon('user-circle'), `Страница С-${id}`);
    links.append(a);
  }
  if (f.fieldKey) {
    links.append(button('Показать в «Спросе и покрытии»', () => showDemand(f.fieldKey, f.value), 'btn btn-sm btn-ghost-primary', 'chart-bar'));
  }
  if (links.childElementCount) card.append(links);
  return card;
}

/** Отчёт текстом — для копирования в чат или письмо. */
function reportText(a) {
  const when = new Date(a.generatedAt).toLocaleString('ru-RU', { dateStyle: 'short', timeStyle: 'short' });
  const lines = [`ИИ-разбор заявок · отдел «${currentDepartment()?.name ?? ''}» · ${when}`, '', a.summary, ''];
  for (const f of a.findings) {
    lines.push(`[${(LEVEL[f.level] || LEVEL.medium)[0]}] ${(CATEGORY[f.category] || CATEGORY.ok)[0]} — ${f.title}`, `  ${f.detail}`);
    if (f.why) lines.push(`  Почему: ${f.why}`);
    for (const x of f.actions) lines.push(`  — ${x.text} (кому: ${x.who}${x.effect ? `; что изменится: ${x.effect}` : ''})`);
    lines.push('');
  }
  if (a.recommendations.length) {
    lines.push('Общие действия:');
    for (const x of a.recommendations) lines.push(`— ${x.text} (кому: ${x.who}${x.effect ? `; что изменится: ${x.effect}` : ''})`);
  }
  lines.push('', `Модель ${a.model}; находки и цифры посчитаны сервером, пояснения и действия — ИИ.`);
  return lines.join('\n');
}

async function copyReport() {
  if (!lastAnalysis) return;
  try {
    await navigator.clipboard.writeText(reportText(lastAnalysis));
    toast('Отчёт скопирован — вставьте его в чат или письмо');
  } catch {
    toast('Браузер не дал доступ к буферу обмена — выделите текст отчёта вручную', 'bad');
  }
}

function printReport() {
  document.body.classList.add('print-ai');
  window.addEventListener('afterprint', () => document.body.classList.remove('print-ai'), { once: true });
  window.print();
}

function renderAnalysis(analysis) {
  lastAnalysis = analysis;
  const box = $('ai-result');
  if (!analysis) {
    box.replaceChildren();
    return;
  }
  const when = new Date(analysis.generatedAt).toLocaleString('ru-RU', { dateStyle: 'short', timeStyle: 'short' });
  const parts = [el('p', `Отдел «${currentDepartment()?.name ?? ''}» · ${when}`, 'ai-print-head'),
    linked(analysis.summary, 'ai-summary d-block')];

  // находки по разделам
  const groups = new Map();
  for (const f of analysis.findings) {
    if (!groups.has(f.category)) groups.set(f.category, []);
    groups.get(f.category).push(f);
  }
  for (const [category, list] of groups) {
    const [title, iconName] = CATEGORY[category] || CATEGORY.ok;
    const section = el('section', null, 'ai-section');
    const h = el('h4', null, 'ai-section-title');
    h.append(icon(iconName), title);
    section.append(h, ...list.map(findingCard));
    parts.push(section);
  }

  if (analysis.recommendations.length) {
    const section = el('section', null, 'ai-section');
    const h = el('h4', null, 'ai-section-title');
    h.append(icon('bulb'), 'Общие действия');
    const ul = el('ul', null, 'ai-actions');
    ul.append(...analysis.recommendations.map(actionItem));
    section.append(h, ul);
    parts.push(section);
  }

  const foot = el('div', null, 'ai-foot');
  foot.append(el('p', `Модель ${analysis.model} · ${when}${analysis.cached ? ' · это недавний разбор' : ''}. `
    + (analysis.structured ? 'Находки и цифры посчитал сервер, пояснения и действия предложил ИИ — это подсказка.'
      : 'Модель ответила не по формату: находки посчитаны сервером, её текст показан как есть.'), 'text-secondary small mb-0'));
  const tools = el('div', null, 'btn-list ai-report-tools');
  tools.append(button('Скопировать', copyReport, 'btn btn-sm', 'file-text'), button('Распечатать', printReport, 'btn btn-sm', 'download'));
  foot.append(tools);
  parts.push(foot);
  box.replaceChildren(...parts);
}

function renderStatus(status) {
  configured = status.isConfigured;
  if (!running) waitBeforeNew(status.retryInSeconds ?? 0);
  $('ai-status').textContent = status.isConfigured
    ? (status.last ? '' : can('Manager')
      ? `Подключена модель ${status.model}. Нажмите «Разобрать с ИИ» — облачная модель отвечает за 10–60 секунд, своя без видеокарты — за несколько минут.`
      : `Подключена модель ${status.model}. Запустить разбор может руководитель или администратор — результат появится здесь.`)
    : 'ИИ не подключён. Чтобы включить, задайте в .env адрес модели AI_BASE_URL, её имя AI_MODEL и при необходимости ключ '
      + 'AI_API_KEY (подходит любая модель с OpenAI-совместимым API, в том числе своя в контуре компании). '
      + 'Цифры ниже считаются и без ИИ.';
  if (!running) renderAnalysis(status.last);
}

export async function refreshInsights() {
  const [report, status] = await Promise.all([api('/api/dashboard/demand'), api('/api/admin/ai/status')]);
  renderTiles(report);
  renderFields(report.fields);
  renderLists(report);
  renderStatus(status);
}

export function initInsights() {
  $('ai-run').addEventListener('click', async () => {
    if (running) return;
    running = true;
    renderButton();
    $('ai-status').textContent = 'Собираем сводку и ждём ответ модели: облачная — 10–60 секунд, своя без видеокарты — до нескольких минут.';
    try {
      renderAnalysis(await api('/api/admin/ai/analysis', { method: 'POST' }));
      $('ai-status').textContent = '';
    } catch (e) {
      $('ai-status').textContent = problemText(e);
      toast(problemText(e), 'bad');
    } finally {
      running = false;
      // сразу узнаём у сервера, когда можно следующий разбор (обычно через минуту)
      try { renderStatus(await api('/api/admin/ai/status')); } catch { renderButton(); }
    }
  });
}
