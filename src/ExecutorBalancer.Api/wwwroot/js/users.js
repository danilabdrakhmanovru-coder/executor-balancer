// «Пользователи»: кто может войти, с какой ролью и в какие отделы. Только для администратора.
import { api, problemText } from './api.js';
import { $, el, row, badge, button, actions, toast, field, input, select, emptyRow, checkbox, fmtDateTime } from './dom.js';
import { openEditor } from './editor.js';
import { allDepartments } from './department.js';
import { session, ROLE_TITLE } from './session.js';

const ROLES = [
  ['Viewer', 'Наблюдатель — только просмотр'],
  ['Manager', 'Руководитель отдела — работа с сотрудниками и заявками'],
  ['Admin', 'Администратор — всё, включая настройки и пользователей'],
];
const ROLE_BADGE = { Viewer: '', Manager: 'ok', Admin: 'warn' };

/** Случайный пароль из 16 символов без похожих букв (l/1, O/0) — показывается администратору один раз. */
function generatePassword() {
  const alphabet = 'abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789-_';
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  return Array.from(bytes, (b) => alphabet[b % alphabet.length]).join('');
}

function departmentsText(ids) {
  if (!ids.length) return 'все отделы';
  const names = new Map(allDepartments().map((d) => [d.id, d.name]));
  return ids.map((id) => names.get(id) || `№${id}`).join(', ');
}

function editor(user) {
  const isNew = !user;
  const login = input('text', user?.login ?? '', { maxlength: '32', required: '', autocomplete: 'off', spellcheck: 'false',
    placeholder: 'например, petrova' });
  if (!isNew) login.disabled = true;
  const name = input('text', user?.displayName ?? '', { maxlength: '120', required: '', placeholder: 'Петрова А. В.' });
  const role = select(ROLES, user?.role ?? 'Manager');
  const password = input('text', isNew ? generatePassword() : '', { minlength: '10', maxlength: '128', autocomplete: 'off',
    spellcheck: 'false', ...(isNew ? { required: '' } : { placeholder: 'оставьте пустым, чтобы не менять' }) });
  const regenerate = button('Сгенерировать', () => { password.value = generatePassword(); }, 'btn btn-sm', 'refresh');
  const passwordBox = el('div', null, 'input-row');
  passwordBox.append(password, regenerate);
  const active = checkbox(user?.isActive ?? true, 'Может входить (снимите, чтобы заблокировать — сессии закроются сразу)');
  const boxes = allDepartments().map((d) => ({ id: d.id, ...checkbox((user?.departmentIds ?? []).includes(d.id), d.name) }));
  const deptList = el('div', null, 'check-list');
  deptList.append(...boxes.map((b) => b.wrap));

  openEditor(isNew ? 'Новый пользователь' : `Пользователь ${user.login}`, [
    field('Логин', login, isNew ? 'Латинские буквы, цифры, «.», «_», «-»; потом не меняется.' : undefined),
    field('Имя', name),
    field('Роль', role),
    field('Отделы', deptList, 'Ничего не отмечено — все отделы. Администратор всегда видит все.'),
    field(isNew ? 'Пароль' : 'Новый пароль', passwordBox,
      'Не короче 10 символов. Передайте его пользователю лично — в журнал и логи пароль не пишется.'),
    active.wrap,
  ], async () => {
    const body = {
      login: login.value.trim(),
      displayName: name.value.trim(),
      role: role.value,
      departmentIds: boxes.filter((b) => b.box.checked).map((b) => b.id),
      password: password.value || null,
      isActive: active.box.checked,
    };
    if (isNew) await api('/api/admin/users', { method: 'POST', body });
    else await api(`/api/admin/users/${user.id}`, { method: 'PUT', body });
    toast(isNew ? `Пользователь ${body.login} создан` : 'Изменения сохранены; открытые сессии пользователя закрыты');
  }, refreshUsers, { submitText: isNew ? 'Создать' : 'Сохранить' });
}

async function remove(user) {
  if (!confirm(`Удалить пользователя ${user.login}? Войти под ним больше будет нельзя; записи журнала сохранятся.`)) return;
  try {
    await api(`/api/admin/users/${user.id}`, { method: 'DELETE' });
    toast(`Пользователь ${user.login} удалён`);
    await refreshUsers();
  } catch (e) {
    toast(problemText(e), 'bad');
  }
}

export async function refreshUsers() {
  const users = await api('/api/admin/users');
  const builtIn = row([
    el('strong', 'admin'), 'Администратор (встроенный)', badge(ROLE_TITLE.Admin, 'warn'), 'все отделы', badge('может входить', 'ok'),
    el('span', 'пароль — ADMIN_PASSWORD в настройках сервера', 'text-secondary small'), '',
  ]);
  $('users').replaceChildren(builtIn, ...(users.length ? users.map((u) => row([
    el('strong', u.login), u.displayName, badge(ROLE_TITLE[u.role], ROLE_BADGE[u.role]), departmentsText(u.departmentIds),
    u.isActive ? badge('может входить', 'ok') : badge('заблокирован', 'bad'),
    u.lastLoginAt ? fmtDateTime(u.lastLoginAt) : el('span', 'ещё не входил', 'text-secondary'),
    actions(button('Изменить', () => editor(u), 'btn btn-sm', 'pencil'),
      ...(u.login === session().login ? [] : [button('Удалить', () => remove(u), 'btn btn-sm btn-outline-danger', 'trash')])),
  ], u.isActive ? '' : 'inactive')) : [emptyRow(7, 'Других пользователей пока нет — добавьте руководителей и наблюдателей')]));
}

export function initUsers() {
  $('user-add').addEventListener('click', () => editor(null));
}
