// Текущий отдел: переключатель в шапке. У каждого отдела свои параметры, правила, сотрудники, заявки и отчёты;
// переключение ничего не меняет и не стирает. Выбор запоминается в браузере.
import { api, setApiDepartment } from './api.js';
import { $, el } from './dom.js';

const STORAGE_KEY = 'eb.department';
let list = [];
let current = null;
let onChange = () => {};

export const currentDepartment = () => current;
export const allDepartments = () => list;

function stored() {
  try { return Number(localStorage.getItem(STORAGE_KEY)) || null; } catch { return null; }
}

function remember(id) {
  try { localStorage.setItem(STORAGE_KEY, String(id)); } catch { /* приватный режим */ }
}

function render() {
  const select = $('department');
  select.replaceChildren(...list.map((d) => {
    const option = el('option', d.name);
    option.value = String(d.id);
    option.selected = d.id === current?.id;
    return option;
  }));
  $('department-sphere').textContent = current?.presetTitle ? `сфера: ${current.presetTitle}` : 'своя настройка';
}

/** Перечитывает список отделов; если выбранный удалён — переходит на первый. */
export async function loadDepartments(preferId = null) {
  list = await api('/api/admin/departments');
  const wanted = preferId ?? current?.id ?? stored();
  const next = list.find((d) => d.id === wanted) || list[0] || null;
  const changed = next?.id !== current?.id;
  current = next;
  setApiDepartment(current?.id ?? null);
  if (current) remember(current.id);
  render();
  return changed;
}

/** Переключиться на отдел: запросы интерфейса сразу идут в его рамках. */
export async function selectDepartment(id) {
  if (id === current?.id) return;
  await loadDepartments(id);
  onChange();
}

export function initDepartments(changed) {
  onChange = changed;
  $('department').addEventListener('change', (event) => { selectDepartment(Number(event.target.value)); });
}
