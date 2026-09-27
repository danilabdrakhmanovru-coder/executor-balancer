// Настройки → Отделы: список, создание со сферой (шаблоном), переименование, удаление пустого отдела.
// Каждый отдел — отдельное пространство: свои параметры, правила, сотрудники, заявки и отчёты.
import { api, problemText } from './api.js';
import { $, el, row, badge, button, actions, fmt, toast, field, input, select, emptyRow, checkbox } from './dom.js';
import { openEditor } from './editor.js';
import { sphereBuilder } from './sphere.js';
import { allDepartments, currentDepartment, loadDepartments, selectDepartment } from './department.js';
import { can } from './session.js';

const MAIN_ID = 1;
let demoEnabled = false;

/** В демо-режиме новый отдел можно сразу наполнить тестовыми сотрудниками через эмулятор АИС. */
export function setDepartmentsDemo(enabled) { demoEnabled = enabled; }
let presets = [];

async function presetList() {
  if (!presets.length) presets = await api('/api/admin/presets');
  return presets;
}

function address(code) {
  const box = el('code', `/api/integration/departments/${code}/orders`, 'wrap-text');
  box.title = 'Сюда АИС отправляет новые заявки этого отдела; исполнителей — в …/executors/{id}';
  return box;
}

export async function refreshDepartments() {
  await loadDepartments();
  const current = currentDepartment();
  const list = allDepartments();
  const tbody = $('departments');
  if (!list.length) { tbody.replaceChildren(emptyRow(6, 'Отделов нет')); return; }
  tbody.replaceChildren(...list.map((d) => {
    const name = el('span');
    name.append(el('strong', d.name));
    if (d.id === current?.id) name.append(' ', badge('открыт', 'ok'));
    const buttons = [];
    if (d.id !== current?.id) buttons.push(button('Открыть', () => selectDepartment(d.id), 'btn btn-sm btn-primary', 'arrow-right'));
    if (can('Admin')) buttons.push(button('Переименовать', () => rename(d), 'btn btn-sm', 'pencil'));
    // основной отдел принимает заявки по общему адресу, а с сотрудниками — хранит историю: их не удалить
    if (can('Admin') && d.id !== MAIN_ID && d.executors === 0 && d.openOrders === 0) {
      buttons.push(button('Удалить', () => remove(d), 'btn btn-sm btn-outline-danger', 'trash'));
    }
    return row([
      name, d.presetTitle || 'своя настройка', `${fmt(d.activeExecutors)} из ${fmt(d.executors)}`, fmt(d.openOrders),
      address(d.code), actions(...buttons),
    ]);
  }));
}

async function create() {
  const list = await presetList();
  const name = input('text', '', { maxlength: '120', required: '', placeholder: 'например, Отдел кредитования' });
  const CUSTOM = '__custom';
  const sphere = select([...list.map((p) => [p.id, p.title]), [CUSTOM, 'Своя сфера — задать параметры и правила'],
    ['', 'Пустой — настрою параметры сам']], list[0]?.id ?? '');
  const code = input('text', '', { maxlength: '32', placeholder: 'подберётся сам' });
  const builder = sphereBuilder();
  const seed = checkbox(true, 'Сразу завести 10 тестовых сотрудников (демо-режим)');
  const builderBox = el('div', null, 'hidden');
  builderBox.append(...builder.nodes);
  sphere.addEventListener('change', () => builderBox.classList.toggle('hidden', sphere.value !== CUSTOM));
  openEditor('Новый отдел', [
    el('p', 'Отдел — отдельное пространство: свои параметры и правила, сотрудники, заявки и отчёты. '
      + 'Остальные отделы не меняются.', 'hint'),
    field('Название', name),
    field('Сфера', sphere, 'Готовый набор параметров, правил и весов — потом его можно дополнить в «Параметрах и правилах».'),
    builderBox,
    ...(demoEnabled ? [seed.wrap] : []),
    field('Код для АИС', code, 'Латинские буквы, цифры, «-» и «_». По нему АИС отправляет заявки отдела.'),
  ], async () => {
    const custom = sphere.value === CUSTOM;
    const created = await api('/api/admin/departments', {
      method: 'POST',
      body: {
        name: name.value.trim(),
        code: code.value.trim() || null,
        presetId: custom ? null : sphere.value || null,
        sphere: custom ? builder.read() : null,
      },
    });
    await selectDepartment(created.id);
    if (demoEnabled && seed.box.checked) {
      // запрос уже идёт в рамках нового отдела — он выбран в шапке
      await api('/api/admin/demo/executors/seed', { method: 'POST', body: { count: 10 } })
        .catch((e) => toast(`Отдел создан, но сотрудники не заведены: ${problemText(e)}`, 'bad'));
    }
    toast(`Отдел «${created.name}» создан и открыт`);
  }, refreshDepartments);
}

function rename(d) {
  const name = input('text', d.name, { maxlength: '120', required: '' });
  openEditor('Переименовать отдел', [field('Название', name)], async () => {
    await api(`/api/admin/departments/${d.id}`, { method: 'PUT', body: { name: name.value.trim() } });
    toast('Отдел переименован');
  }, refreshDepartments);
}

async function remove(d) {
  if (!confirm(`Удалить отдел «${d.name}» вместе с его параметрами и правилами?`)) return;
  try {
    await api(`/api/admin/departments/${d.id}`, { method: 'DELETE' });
    toast(`Отдел «${d.name}» удалён`);
    await refreshDepartments();
  } catch (e) {
    toast(problemText(e), 'bad');
  }
}

export function initDepartmentsAdmin() {
  $('department-add').addEventListener('click', () => { create().catch((e) => toast(problemText(e), 'bad')); });
}
