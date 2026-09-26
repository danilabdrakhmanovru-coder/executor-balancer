// Обращения к API балансировщика. Изменяющие запросы несут заголовок защиты от CSRF.

const CSRF_HEADER = 'X-Requested-With';
const CSRF_VALUE = 'executor-balancer';

let unauthorizedHandler = () => {};
let departmentId = null;

/** Отдел, в рамках которого идут запросы интерфейса (выбирается в шапке). */
export function setApiDepartment(id) { departmentId = id; }

/**
 * Адрес с отделом: запросы дашборда и конструктора относятся к выбранному отделу.
 * Список отделов, вход и выход — общие.
 */
export function scoped(path) {
  const own = (path.startsWith('/api/dashboard/') || path.startsWith('/api/admin/'))
    && !path.startsWith('/api/admin/departments');
  if (!own || departmentId === null) return path;
  return `${path}${path.includes('?') ? '&' : '?'}department=${encodeURIComponent(departmentId)}`;
}

export function onUnauthorized(handler) { unauthorizedHandler = handler; }

export class ApiError extends Error {
  constructor(status, problem) {
    super(problem?.title || `HTTP ${status}`);
    this.status = status;
    this.problem = problem;
  }
}

export async function api(path, { method = 'GET', body } = {}) {
  const headers = { [CSRF_HEADER]: CSRF_VALUE };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const response = await fetch(scoped(path), {
    method,
    credentials: 'same-origin',
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (response.status === 401 && path !== '/api/auth/login') {
    unauthorizedHandler();
    throw new ApiError(401, null);
  }
  if (!response.ok) {
    let problem = null;
    try { problem = await response.json(); } catch { /* тело не JSON */ }
    throw new ApiError(response.status, problem);
  }
  if (response.status === 204) return null;
  return response.json();
}

/** Понятный текст ошибки из ProblemDetails. */
export function problemText(error) {
  if (!(error instanceof ApiError)) return 'Нет связи с сервером';
  const p = error.problem;
  if (p?.errors) return Object.values(p.errors).flat().join('; ');
  if (error.status === 429) return 'Слишком много запросов, подождите немного';
  return p?.detail || p?.title || error.message;
}
