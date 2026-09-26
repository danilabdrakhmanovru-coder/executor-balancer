// Пробная проверка: кому ушла бы заявка с такими параметрами. Ничего не назначает.
import { api, problemText } from './api.js';
import { $, el, field, input } from './dom.js';
import { renderExplanation } from './explain.js';
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
      const explanation = await api('/api/admin/preview', { method: 'POST', body: { parentId, attributes: form.read() } });
      result.replaceChildren(renderExplanation(explanation));
    } catch (e) {
      result.replaceChildren(el('p', e instanceof Error && !('status' in e) ? e.message : problemText(e), 'error'));
    } finally {
      submit.disabled = false;
    }
  };
}

export function fillSample() { form?.sample(); }
