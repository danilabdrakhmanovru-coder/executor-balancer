// Пробная проверка: кому ушла бы заявка с такими параметрами. Ничего не назначает.
import { api, problemText } from './api.js';
import { $, el, field, input } from './dom.js';
import { renderExplanation, renderCheck } from './explain.js';
import { attributeForm } from './forms.js';

let form = null;

export function buildPreviewForm(config) {
  form = attributeForm(config, 'Order');
  const parent = input('number', '', { min: '1', step: '1', placeholder: 'необязательно' });
  const submit = el('button', 'Кому уйдёт заявка?', 'btn btn-primary w-100');
  submit.type = 'submit';
  const node = $('preview-form');
  node.replaceChildren(
    ...form.nodes,
    field('Родительская заявка №', parent, 'Если указана — заявка уйдёт исполнителю родительской, если он подходит.'),
    submit,
  );
  node.onsubmit = async (event) => {
    event.preventDefault();
    const result = $('preview-result');
    try {
      const parentId = parent.value ? Number(parent.value) : null;
      submit.disabled = true;
      const attributes = form.read();
      const explanation = await api('/api/admin/preview', { method: 'POST', body: { parentId, attributes } });
      result.replaceChildren(renderExplanation(explanation, { onCandidate: (c) => showCheck(c, attributes) }));
    } catch (e) {
      result.replaceChildren(el('p', e instanceof Error && !('status' in e) ? e.message : problemText(e), 'error'));
    } finally {
      submit.disabled = false;
    }
  };
}

/** Разбор по правилам для сотрудника — в окне поверх результата. */
export async function showCheck(candidate, attributes, back = null) {
  const body = $('details-body');
  $('details-title').textContent = `Почему — ${candidate.name}`;
  body.replaceChildren(el('p', 'Загрузка…', 'muted'));
  if (!$('details').open) $('details').showModal();
  try {
    const check = await api(`/api/admin/preview/executors/${encodeURIComponent(candidate.executorId)}`,
      { method: 'POST', body: { attributes } });
    const nodes = [renderCheck(check)];
    const profile = el('a', 'Открыть страницу сотрудника', 'btn btn-sm mt-2');
    profile.href = `#executor-${candidate.executorId}`;
    profile.addEventListener('click', () => $('details').close());
    nodes.push(profile);
    if (back) {
      const button = el('button', '← к заявке', 'btn btn-sm mt-2 me-2');
      button.type = 'button';
      button.addEventListener('click', back);
      nodes.unshift(button);
    }
    body.replaceChildren(...nodes);
  } catch (e) {
    body.replaceChildren(el('p', e.status === 404 ? 'Сотрудник больше не числится в этом отделе.' : problemText(e), 'error'));
  }
}

export function fillSample() { form?.sample(); }
