// Общий диалог редактирования: заголовок, поля, ошибка сервера под формой.
import { problemText } from './api.js';
import { $ } from './dom.js';

let submitHandler = null;
let afterSave = null;

/** onSubmit сохраняет; после успеха диалог закрывается и вызывается after (например, перерисовка списка). */
export function openEditor(title, nodes, onSubmit, after) {
  $('editor-title').textContent = title;
  $('editor-body').replaceChildren(...nodes);
  $('editor-error').textContent = '';
  submitHandler = onSubmit;
  afterSave = after;
  $('editor').showModal();
}

async function submit(event) {
  event.preventDefault();
  if (!submitHandler) return;
  const save = $('editor-save');
  save.disabled = true;
  $('editor-error').textContent = '';
  try {
    await submitHandler();
    $('editor').close();
    if (afterSave) await afterSave();
  } catch (e) {
    $('editor-error').textContent = e instanceof Error && !('status' in e) ? e.message : problemText(e);
  } finally {
    save.disabled = false;
  }
}

export function initEditor() {
  $('editor-form').addEventListener('submit', submit);
  $('editor-close').addEventListener('click', () => $('editor').close());
}
