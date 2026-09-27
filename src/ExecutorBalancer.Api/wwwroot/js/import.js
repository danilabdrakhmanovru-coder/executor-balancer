// Загрузка сотрудников из файла (CSV из Excel): выбрать файл → проверка по строкам → загрузить одним действием.
// Файл не разбирается в браузере — только на сервере, по справочнику параметров отдела.
import { api, problemText, scoped } from './api.js';
import { $, el, row, badge, toast, emptyRow } from './dom.js';
import { openEditor } from './editor.js';

const ACTION = { new: ['новый', 'ok'], update: ['обновится', ''], move: ['переведётся', 'warn'] };
const SHOWN = 200;

function renderPreview(box, preview) {
  const nodes = [];
  if (preview.fileErrors.length) {
    nodes.push(el('p', preview.fileErrors.join('; '), 'error'));
  } else {
    const summary = preview.invalid
      ? `Проверено строк: ${preview.valid + preview.invalid}. С ошибками: ${preview.invalid} — исправьте их в файле и выберите его снова.`
      : `Проверено строк: ${preview.valid}. Ошибок нет — можно загружать.`;
    nodes.push(el('p', summary, preview.invalid ? 'error' : 'decision'));
  }
  if (preview.ignored.length) {
    nodes.push(el('p', `Не загружаются (таких параметров в отделе нет): ${preview.ignored.join(', ')}`, 'muted small'));
  }
  if (preview.rows.length) {
    const table = el('table', null, 'table table-sm table-vcenter');
    const head = el('thead');
    head.append(row(['Строка', 'Номер', 'ФИО', 'Что будет', 'Ошибки']));
    const body = el('tbody');
    // сначала строки с ошибками — их нужно исправить
    const rows = [...preview.rows].sort((a, b) => (b.errors.length > 0) - (a.errors.length > 0) || a.line - b.line);
    for (const r of rows.slice(0, SHOWN)) {
      const [label, cls] = ACTION[r.action] || [r.action, ''];
      const what = el('span');
      what.append(badge(label, cls));
      if (r.note) what.append(el('div', r.note, 'muted small'));
      body.append(row([String(r.line), r.id ?? '—', r.fullName || '—', what,
        r.errors.length ? el('span', r.errors.join('; '), 'text-danger wrap-text') : '—'], r.errors.length ? 'warn-row' : ''));
    }
    if (rows.length > SHOWN) body.append(emptyRow(5, `…и ещё ${rows.length - SHOWN} строк`));
    table.append(head, body);
    const scroll = el('div', null, 'table-responsive import-preview');
    scroll.append(table);
    nodes.push(scroll);
  }
  box.replaceChildren(...nodes);
}

export function openImport(after) {
  const file = el('input', null, 'form-control');
  file.type = 'file';
  file.accept = '.csv,text/csv';
  const result = el('div', null, 'mt-3');
  let data = null;
  let preview = null;
  file.addEventListener('change', async () => {
    data = null;
    preview = null;
    const chosen = file.files?.[0];
    if (!chosen) return;
    result.replaceChildren(el('p', 'Проверяем…', 'muted'));
    try {
      data = await chosen.arrayBuffer();
      preview = await api('/api/admin/executors/import', { method: 'POST', raw: data });
      renderPreview(result, preview);
    } catch (e) {
      result.replaceChildren(el('p', problemText(e), 'error'));
    }
  });
  const template = el('a', 'скачайте шаблон отдела', '');
  template.href = scoped('/api/admin/executors/template.csv');
  template.setAttribute('download', '');
  const hint = el('p', null, 'hint');
  hint.append('Откройте в Excel ', template, ', заполните по строке на сотрудника и сохраните как «CSV (разделители — точка с запятой)» '
    + 'или «CSV UTF-8». Колонки: номер в АИС (пусто — выдастся сам), ФИО, на работе (да/нет), норма в день (пусто — без лимита), '
    + 'квалификация и параметры отдела; списки — через запятую. Сначала файл проверяется, ничего не записывая.');
  openEditor('Загрузка сотрудников из файла', [hint, file, result], async () => {
    if (!preview) throw new Error('Сначала выберите файл — он будет проверен');
    if (preview.invalid || preview.fileErrors.length) throw new Error('В файле есть ошибки — исправьте их и выберите файл снова');
    const applied = await api('/api/admin/executors/import?apply=true', { method: 'POST', raw: data });
    if (!applied.applied) {
      renderPreview(result, applied);
      throw new Error('Данные изменились, пока файл проверялся — посмотрите ошибки');
    }
    toast(`Загружено сотрудников: ${applied.valid}`);
  }, after, { submitText: 'Загрузить' });
}
