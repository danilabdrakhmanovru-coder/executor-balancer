// «Разбор заявок»: спрос и покрытие за сутки (считает сервер) и ИИ-разбор по этим цифрам.
// Ответ модели — только текст: выводится через textContent, как и всё остальное.
import { api, problemText } from './api.js';
import { $, el, row, badge, fmt, emptyRow, tile, toast } from './dom.js';
import { can } from './session.js';

let fieldKey = null;
let running = false;

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
  $('in-options').replaceChildren(...options.map((o) => row([
    o.label || o.value,
    o.orders ? `${fmt(o.orders)} (${fmt(o.sharePercent)}%)` : '0',
    o.waiting ? badge(fmt(o.waiting), 'warn') : '0',
    fmt(o.reworked),
    fmt(o.executors),
    `${fmt(o.capacitySharePercent)}%`,
    tension(o),
  ], o.orders === 0 ? 'inactive' : '')));
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

function renderAnalysis(analysis) {
  const box = $('ai-result');
  if (!analysis) {
    box.replaceChildren();
    return;
  }
  const parts = [el('p', analysis.summary, 'ai-summary')];
  if (analysis.findings.length) {
    const list = el('div', null, 'ai-findings');
    for (const f of analysis.findings) {
      const [text, kind] = LEVEL[f.level] || LEVEL.medium;
      const item = el('div', null, 'ai-finding');
      const head = el('div', null, 'ai-finding-head');
      head.append(badge(text, kind), el('strong', f.title));
      item.append(head, el('p', f.detail, 'mb-0 text-secondary'));
      list.append(item);
    }
    parts.push(el('h4', 'Что видно', 'mt-3'), list);
  }
  if (analysis.recommendations.length) {
    const ol = el('ol', null, 'ai-recommendations');
    for (const r of analysis.recommendations) ol.append(el('li', r));
    parts.push(el('h4', 'Что сделать', 'mt-3'), ol);
  }
  const when = new Date(analysis.generatedAt).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
  parts.push(el('p', `Модель ${analysis.model} · ${when}${analysis.cached ? ' · недавний разбор, повтор — через минуту' : ''}`
    + ' · по обезличенной сводке за сутки. Это подсказка: проверяйте выводы по цифрам ниже.', 'text-secondary small mt-3 mb-0'));
  box.replaceChildren(...parts);
}

function renderStatus(status) {
  const runButton = $('ai-run');
  runButton.disabled = running || !status.isConfigured;
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
    $('ai-run').disabled = true;
    $('ai-run-text').textContent = 'ИИ разбирает…';
    $('ai-status').textContent = 'Собираем сводку и ждём ответ модели: облачная — 10–60 секунд, своя без видеокарты — до нескольких минут.';
    try {
      renderAnalysis(await api('/api/admin/ai/analysis', { method: 'POST' }));
      $('ai-status').textContent = '';
    } catch (e) {
      $('ai-status').textContent = problemText(e);
      toast(problemText(e), 'bad');
    } finally {
      running = false;
      $('ai-run').disabled = false;
      $('ai-run-text').textContent = 'Разобрать с ИИ';
    }
  });
}
