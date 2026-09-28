// Кто вошёл: роль, отделы и включён ли демо-режим. Права проверяет сервер; здесь — только чтобы не показывать
// кнопки, которые всё равно вернут «нет прав». На <body> — класс роли: role-viewer, role-manager, role-admin.
const RANK = { Viewer: 0, Manager: 1, Admin: 2 };
export const ROLE_TITLE = { Viewer: 'наблюдатель', Manager: 'руководитель', Admin: 'администратор' };

let me = { login: '', name: '', role: 'Viewer', departments: null, demo: false };

export function setSession(value) {
  me = { ...me, ...value, role: RANK[value?.role] === undefined ? 'Viewer' : value.role };
  document.body.classList.remove('role-viewer', 'role-manager', 'role-admin');
  document.body.classList.add(`role-${me.role.toLowerCase()}`);
}

export const session = () => me;

/** Есть ли у вошедшего хотя бы такая роль: can('Manager') — руководитель или администратор. */
export const can = (role) => RANK[me.role] >= RANK[role];

/** Демо-режим и права администратора — только тогда доступны тестовый стенд и правка сотрудников через эмулятор. */
export const demoAdmin = () => me.demo && can('Admin');

/** Тестовый стенд: поток и заявки вручную — с руководителя (и гостя-руководителя), заведение сотрудников — у администратора. */
export const demoManager = () => me.demo && can('Manager');
