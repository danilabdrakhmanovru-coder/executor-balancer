// Конструктор: параметры заявки и исполнителя, правила подбора, правила веса.
// Интерфейс подсказывает допустимые сочетания, но окончательная проверка — на сервере.
import { api, problemText } from './api.js';
import { openEditor as openShared } from './editor.js';
import { $, el, row, badge, button, actions, fmt, toast, field, input, select, checkbox, emptyRow } from './dom.js';
import { can } from './session.js';
import { optionText } from './forms.js';

const TYPE_LABEL = { String: 'строка', Number: 'число', Boolean: 'да/нет', Enum: 'справочник', Array: 'список' };
const NUMERIC_OPS = ['greaterThan', 'greaterThanOrEqual', 'lessThan', 'lessThanOrEqual'];
const isText = (t) => t === 'String' || t === 'Enum';
const kind = (t) => (t === 'Number' ? 'n' : t === 'Boolean' ? 'b' : t === 'Array' ? 'a' : 't');

let config = null;
let onChanged = () => {};

// ---------- совместимость типов (зеркало RuleCompiler) ----------

/** Оператор применим к полю заявки при сравнении с константой. */
function constantAllowed(op, left) {
  if (op === 'equals' || op === 'notEquals') return true;
  if (NUMERIC_OPS.includes(op) || op === 'between') return left === 'Number';
  if (op === 'in' || op === 'notIn') return left !== 'Array';
  if (op === 'contains') return left === 'Array' || isText(left);
  return false;
}

/** Оператор применим к паре «поле заявки — поле исполнителя». */
function fieldsCompatible(op, left, right) {
  switch (op) {
    case 'equals': case 'notEquals': return (left === 'Array' && right === 'Array') || (left !== 'Array' && kind(left) === kind(right));
    case 'in': case 'notIn': return left !== 'Array' && right === 'Array';
    case 'contains': return (left === 'Array' && right !== 'Array') || (isText(left) && isText(right));
    case 'between': return left === 'Number' && right === 'Number';
    default: return NUMERIC_OPS.includes(op) && left === 'Number' && right === 'Number';
  }
}

const fieldsOf = (owner) => config.fields.filter((f) => f.owner === owner);
const findField = (owner, key) => config.fields.find((f) => f.owner === owner && f.key === key);
const opLabel = (op) => config.operators.find((o) => o.value === op)?.label || op;

// ---------- таблицы ----------

function renderFields(owner, tbody) {
  const list = fieldsOf(owner);
  if (!list.length) { tbody.replaceChildren(emptyRow(6, 'Параметров нет')); return; }
  tbody.replaceChildren(...list.map((f) => {
    // коды со своими подписями показываем как «Претензия (ORDER_3)»: видно и что на экране, и что шлёт АИС
    const options = f.options.length
      ? f.options.map((o) => (optionText(f, o) === o ? o : `${optionText(f, o)} (${o})`)).join(', ') : '—';
    const optionsCell = el('span', options.length > 160 ? `${options.slice(0, 159)}…` : options, 'wrap-text');
    optionsCell.title = options;
    return row([
      el('code', f.key), f.label, TYPE_LABEL[f.type] || f.type, optionsCell,
      f.usedByRules ? badge(String(f.usedByRules), 'ok') : '—',
      can('Admin') ? actions(button('Изменить', () => editField(f), 'btn btn-sm', 'pencil'), button('Удалить', () => removeField(f), 'btn btn-sm btn-outline-danger', 'trash')) : '',
    ]);
  }));
}

function toggle(checked, onChange) {
  const box = input('checkbox');
  box.checked = checked;
  box.title = checked ? 'Выключить' : 'Включить';
  box.addEventListener('change', () => onChange(box.checked).catch(() => { box.checked = !box.checked; }));
  return box;
}

function renderRules() {
  const tbody = $('rules');
  if (!config.rules.length) { tbody.replaceChildren(emptyRow(6, 'Правил нет — подходит любой активный исполнитель')); return; }
  tbody.replaceChildren(...config.rules.map((r) => {
    const condition = el('span', r.text, 'wrap-text');
    if (r.error) condition.append(el('div', `не применяется: ${r.error}`, 'error small'));
    return row([
      toggle(r.isEnabled, (on) => saveRule(r.id, { ...ruleInput(r), isEnabled: on })),
      r.priority, r.name, condition, r.isStrict ? badge('строгое', 'warn') : '—',
      can('Admin') ? actions(button('Изменить', () => editRule(r), 'btn btn-sm', 'pencil'), button('Удалить', () => removeRule(r), 'btn btn-sm btn-outline-danger', 'trash')) : '',
    ], r.isEnabled ? '' : 'inactive');
  }));
}

function renderWeightRules() {
  $('weight-hint').textContent = `Правила проверяются по приоритету, срабатывает первое подходящее. `
    + `Если не сработало ни одно — вес ${fmt(config.defaultOrderWeight)}. Вес исполнителя (квалификация) приходит из АИС.`;
  const tbody = $('weight-rules');
  if (!config.weightRules.length) { tbody.replaceChildren(emptyRow(5, 'Правил веса нет — все заявки одного веса')); return; }
  tbody.replaceChildren(...config.weightRules.map((r) => {
    const condition = el('span', r.text, 'wrap-text');
    if (r.error) condition.append(el('div', `не применяется: ${r.error}`, 'error small'));
    return row([
      toggle(r.isEnabled, (on) => saveWeightRule(r.id, { ...weightInput(r), isEnabled: on })),
      r.priority, condition, el('strong', fmt(r.weight)),
      can('Admin') ? actions(button('Изменить', () => editWeightRule(r), 'btn btn-sm', 'pencil'), button('Удалить', () => removeWeightRule(r), 'btn btn-sm btn-outline-danger', 'trash')) : '',
    ], r.isEnabled ? '' : 'inactive');
  }));
}

export async function refreshConstructor() {
  config = await api('/api/admin/config');
  renderFields('Order', $('fields-order'));
  renderFields('Executor', $('fields-executor'));
  renderRules();
  renderWeightRules();
}

export function currentConfig() { return config; }

// ---------- диалог ----------

function openEditor(title, nodes, onSubmit) {
  openShared(title, nodes, onSubmit, async () => { await refreshConstructor(); onChanged(); });
}

async function mutate(promise, success) {
  try {
    await promise;
    toast(success);
    await refreshConstructor();
    onChanged();
  } catch (e) {
    toast(problemText(e), 'bad');
    throw e;
  }
}

// ---------- параметры ----------

function parseOptions(text) {
  return text.split(/[\n,;]/).map((s) => s.trim()).filter(Boolean);
}

function editField(existing, owner) {
  const isNew = !existing;
  const ownerSelect = select([['Order', 'заявка'], ['Executor', 'исполнитель']], existing?.owner || owner, isNew ? {} : { disabled: '' });
  const key = input('text', existing?.key, { maxlength: '48', pattern: '[a-z][a-z0-9_]*', required: '', placeholder: 'например, language' });
  if (!isNew) key.disabled = true;
  const label = input('text', existing?.label, { maxlength: '120', required: '', placeholder: 'например, Язык обращения' });
  const type = select(Object.entries(TYPE_LABEL), existing?.type || 'String', isNew ? {} : { disabled: '' });
  const options = el('textarea', null, 'form-control');
  options.rows = 4;
  options.maxLength = 11000;
  options.placeholder = 'по одному значению в строке или через запятую';
  options.value = (existing?.options || []).join('\n');
  const optionsField = field('Справочник значений', options, 'Для «справочника» и «списка». Пусто — любые значения.');
  const labels = el('textarea', null, 'form-control');
  labels.rows = 4;
  labels.maxLength = 11000;
  labels.placeholder = 'по одной подписи в строке, в том же порядке; пусто — показывать сами значения';
  labels.value = (existing?.optionLabels || []).join('\n');
  const labelsField = field('Подписи на экране', labels,
    'Если значения — коды (например, ORDER_1 от АИС): люди увидят подписи, а правила и АИС работают с кодами.');
  const sync = () => {
    optionsField.classList.toggle('hidden', !['Enum', 'Array'].includes(type.value));
    labelsField.classList.toggle('hidden', !['Enum', 'Array'].includes(type.value));
  };
  type.addEventListener('change', sync);
  sync();

  const nodes = [
    field('Чей параметр', ownerSelect),
    field('Ключ', key, 'Латиница, цифры и _. Под этим ключом АИС передаёт значение. После создания не меняется.'),
    field('Название', label),
    field('Тип', type, isNew ? 'После создания не меняется: АИС уже передаёт значения в этом формате.' : undefined),
    optionsField,
    labelsField,
  ];
  openEditor(isNew ? 'Новый параметр' : `Параметр «${existing.label}»`, nodes, async () => {
    const body = {
      owner: ownerSelect.value, key: key.value.trim(), label: label.value.trim(), type: type.value,
      options: ['Enum', 'Array'].includes(type.value) ? parseOptions(options.value) : [],
      // подписи — строго по строкам (запятая может быть внутри подписи); пустые строки — «без подписи»
      optionLabels: ['Enum', 'Array'].includes(type.value) && labels.value.trim()
        ? parseOptions(options.value).map((_, i) => (labels.value.split('\n')[i] || '').trim()) : [],
    };
    if (isNew) await api('/api/admin/fields', { method: 'POST', body });
    else await api(`/api/admin/fields/${existing.id}`, { method: 'PUT', body });
    toast(isNew ? 'Параметр добавлен' : 'Параметр сохранён');
  });
}

async function removeField(f) {
  if (!confirm(`Удалить параметр «${f.label}» (${f.key})?`)) return;
  await mutate(api(`/api/admin/fields/${f.id}`, { method: 'DELETE' }), 'Параметр удалён').catch(() => {});
}

// ---------- значение для сравнения ----------

/** Поле ввода константы под тип параметра и оператор. read() возвращает JSON-значение или бросает ошибку. */
function valueEditor(orderField, op, current) {
  const wrap = el('div', null, 'value-editor');
  const type = orderField?.type;
  const opts = orderField?.options || [];
  const asNumber = (text) => {
    const n = Number(String(text).replace(',', '.').trim());
    if (text === '' || !Number.isFinite(n)) throw new Error('Введите число');
    return n;
  };

  if (!orderField) {
    wrap.append(el('span', 'Сначала выберите параметр заявки', 'muted'));
    return { node: wrap, read: () => { throw new Error('Выберите параметр заявки'); } };
  }

  if (op === 'between') {
    const [a, b] = Array.isArray(current) ? current : ['', ''];
    const from = input('number', a, { step: 'any', placeholder: 'от' });
    const to = input('number', b, { step: 'any', placeholder: 'до' });
    wrap.append(from, el('span', '—', 'muted'), to);
    return { node: wrap, read: () => [asNumber(from.value), asNumber(to.value)] };
  }

  if (op === 'in' || op === 'notIn') {
    if (opts.length) {
      const selected = new Set(Array.isArray(current) ? current.map(String) : []);
      const boxes = opts.map((o) => { const c = checkbox(selected.has(o), optionText(orderField, o)); wrap.append(c.wrap); return [o, c.box]; });
      wrap.classList.add('checks');
      return { node: wrap, read: () => {
        const values = boxes.filter(([, b]) => b.checked).map(([o]) => o);
        if (!values.length) throw new Error('Отметьте хотя бы одно значение');
        return values;
      } };
    }
    const text = input('text', Array.isArray(current) ? current.join(', ') : '', { placeholder: 'значения через запятую' });
    wrap.append(text);
    return { node: wrap, read: () => {
      const values = parseOptions(text.value);
      if (!values.length) throw new Error('Укажите хотя бы одно значение');
      return type === 'Number' ? values.map(asNumber) : values;
    } };
  }

  if (op === 'contains' && type === 'Array' && opts.length) {
    const s = select(opts.map((o) => [o, optionText(orderField, o)]), current);
    wrap.append(s);
    return { node: wrap, read: () => s.value };
  }

  if (type === 'Boolean') {
    const s = select([['true', 'да'], ['false', 'нет']], current === false ? 'false' : 'true');
    wrap.append(s);
    return { node: wrap, read: () => s.value === 'true' };
  }

  if (type === 'Number' || NUMERIC_OPS.includes(op)) {
    const n = input('number', current, { step: 'any' });
    wrap.append(n);
    return { node: wrap, read: () => asNumber(n.value) };
  }

  if (type === 'Enum' && opts.length && op !== 'contains') {
    const s = select(opts.map((o) => [o, optionText(orderField, o)]), current);
    wrap.append(s);
    return { node: wrap, read: () => s.value };
  }

  if (type === 'Array') {
    const text = input('text', Array.isArray(current) ? current.join(', ') : '', { placeholder: 'значения через запятую' });
    wrap.append(text);
    return { node: wrap, read: () => parseOptions(text.value) };
  }

  const t = input('text', current, { maxlength: '500' });
  wrap.append(t);
  return { node: wrap, read: () => {
    if (!t.value.trim()) throw new Error('Введите значение');
    return t.value.trim();
  } };
}

// ---------- правила подбора ----------

function ruleInput(r) {
  return {
    name: r.name, isEnabled: r.isEnabled, priority: r.priority, orderField: r.orderField, operator: r.operator,
    target: r.target, executorField: r.executorField, executorFieldUpper: r.executorFieldUpper,
    value: r.value, isStrict: r.isStrict,
  };
}

function saveRule(id, body) {
  const request = id
    ? api(`/api/admin/rules/${id}`, { method: 'PUT', body })
    : api('/api/admin/rules', { method: 'POST', body });
  return mutate(request, 'Правило сохранено');
}

function editRule(existing) {
  const r = existing ? ruleInput(existing) : {
    name: '', isEnabled: true, priority: (Math.max(0, ...config.rules.map((x) => x.priority)) + 10), orderField: '',
    operator: 'equals', target: 'ExecutorField', executorField: '', executorFieldUpper: '', value: null, isStrict: false,
  };
  const name = input('text', r.name, { maxlength: '160', required: '', placeholder: 'например, Язык обращения' });
  const priority = input('number', r.priority, { min: '0', max: '1000000', step: '1' });
  const orderField = select([['', '— выберите —'], ...fieldsOf('Order').map((f) => [f.key, `${f.label} (${TYPE_LABEL[f.type]})`])], r.orderField);
  const target = select([['ExecutorField', 'с параметром исполнителя'], ['Constant', 'со значением']], r.target);
  const operator = select([], null);
  const execSlot = el('div');
  const valueSlot = el('div');
  const enabled = checkbox(r.isEnabled, 'Правило включено');
  const strict = checkbox(r.isStrict, 'Строгое: если значение не заполнено — исполнитель не подходит');
  let executorSelect = null;
  let upperSelect = null;
  let value = null;

  const rebuild = (keepOperator) => {
    const left = findField('Order', orderField.value);
    const ops = config.operators.filter((o) => left && (target.value === 'Constant'
      ? constantAllowed(o.value, left.type)
      : fieldsOf('Executor').some((f) => fieldsCompatible(o.value, left.type, f.type))));
    const previous = keepOperator ? r.operator : operator.value;
    operator.replaceChildren(...ops.map((o) => { const opt = el('option', o.label); opt.value = o.value; return opt; }));
    if (ops.some((o) => o.value === previous)) operator.value = previous;
    rebuildTarget(keepOperator);
  };

  const rebuildTarget = (keepValues) => {
    const left = findField('Order', orderField.value);
    const op = operator.value;
    execSlot.replaceChildren();
    valueSlot.replaceChildren();
    executorSelect = upperSelect = value = null;
    if (!left || !op) return;
    if (target.value === 'ExecutorField') {
      const candidates = fieldsOf('Executor').filter((f) => fieldsCompatible(op, left.type, f.type));
      const opts = candidates.map((f) => [f.key, `${f.label} (${TYPE_LABEL[f.type]})`]);
      if (!opts.length) { execSlot.append(el('p', 'Нет подходящих по типу параметров исполнителя', 'error')); return; }
      executorSelect = select(opts, keepValues ? r.executorField : null);
      if (op === 'between') {
        upperSelect = select(opts, keepValues ? r.executorFieldUpper : opts[opts.length > 1 ? 1 : 0][0]);
        execSlot.append(field('Нижняя граница (исполнитель)', executorSelect), field('Верхняя граница (исполнитель)', upperSelect));
      } else {
        execSlot.append(field('Параметр исполнителя', executorSelect));
      }
    } else {
      value = valueEditor(left, op, keepValues ? r.value : null);
      valueSlot.append(field('Значение', value.node));
    }
  };

  orderField.addEventListener('change', () => rebuild(false));
  target.addEventListener('change', () => rebuild(false));
  operator.addEventListener('change', () => rebuildTarget(false));
  rebuild(true);

  const sentence = el('div', null, 'sentence');
  sentence.append(field('Параметр заявки', orderField), field('Сравнить', target), field('Оператор', operator));

  openEditor(existing ? `Правило «${existing.name}»` : 'Новое правило подбора', [
    field('Название', name), sentence, execSlot, valueSlot,
    field('Приоритет', priority, 'Меньше — проверяется раньше. Влияет на то, какая причина отказа попадёт в объяснение.'),
    enabled.wrap, strict.wrap,
  ], async () => {
    if (!orderField.value) throw new Error('Выберите параметр заявки');
    const body = {
      name: name.value.trim(), isEnabled: enabled.box.checked, priority: Number(priority.value) || 0,
      orderField: orderField.value, operator: operator.value, target: target.value,
      executorField: executorSelect?.value ?? null, executorFieldUpper: upperSelect?.value ?? null,
      value: target.value === 'Constant' ? value?.read() : null, isStrict: strict.box.checked,
    };
    if (existing) await api(`/api/admin/rules/${existing.id}`, { method: 'PUT', body });
    else await api('/api/admin/rules', { method: 'POST', body });
    toast('Правило сохранено');
  });
}

async function removeRule(r) {
  if (!confirm(`Удалить правило «${r.name}»?`)) return;
  await mutate(api(`/api/admin/rules/${r.id}`, { method: 'DELETE' }), 'Правило удалено').catch(() => {});
}

// ---------- правила веса ----------

function weightInput(r) {
  return { isEnabled: r.isEnabled, priority: r.priority, orderField: r.orderField, operator: r.operator, value: r.value, weight: r.weight };
}

function saveWeightRule(id, body) {
  return mutate(api(`/api/admin/weight-rules/${id}`, { method: 'PUT', body }), 'Правило веса сохранено');
}

function editWeightRule(existing) {
  const r = existing ? weightInput(existing) : {
    isEnabled: true, priority: (Math.max(0, ...config.weightRules.map((x) => x.priority)) + 10),
    orderField: '', operator: 'equals', value: null, weight: 2,
  };
  const orderField = select([['', '— выберите —'], ...fieldsOf('Order').map((f) => [f.key, `${f.label} (${TYPE_LABEL[f.type]})`])], r.orderField);
  const operator = select([], null);
  const valueSlot = el('div');
  const weight = input('number', r.weight, { min: '0.1', max: '1000', step: '0.1', required: '' });
  const priority = input('number', r.priority, { min: '0', max: '1000000', step: '1' });
  const enabled = checkbox(r.isEnabled, 'Правило включено');
  let value = null;

  const rebuild = (keep) => {
    const left = findField('Order', orderField.value);
    const ops = config.operators.filter((o) => left && constantAllowed(o.value, left.type));
    const previous = keep ? r.operator : operator.value;
    operator.replaceChildren(...ops.map((o) => { const opt = el('option', o.label); opt.value = o.value; return opt; }));
    if (ops.some((o) => o.value === previous)) operator.value = previous;
    rebuildValue(keep);
  };
  const rebuildValue = (keep) => {
    value = valueEditor(findField('Order', orderField.value), operator.value, keep ? r.value : null);
    valueSlot.replaceChildren(field('Значение', value.node));
  };
  orderField.addEventListener('change', () => rebuild(false));
  operator.addEventListener('change', () => rebuildValue(false));
  rebuild(true);

  const sentence = el('div', null, 'sentence');
  sentence.append(field('Если параметр заявки', orderField), field('Оператор', operator));
  openEditor(existing ? 'Правило веса' : 'Новое правило веса', [
    sentence, valueSlot,
    field('То вес заявки', weight, 'Например: простая — 1, средняя — 2, сложная — 3. От 0,1 до 1000.'),
    field('Приоритет', priority, 'Срабатывает первое подходящее правило с наименьшим приоритетом.'),
    enabled.wrap,
  ], async () => {
    if (!orderField.value) throw new Error('Выберите параметр заявки');
    const body = {
      isEnabled: enabled.box.checked, priority: Number(priority.value) || 0, orderField: orderField.value,
      operator: operator.value, value: value.read(), weight: Number(String(weight.value).replace(',', '.')),
    };
    if (existing) await api(`/api/admin/weight-rules/${existing.id}`, { method: 'PUT', body });
    else await api('/api/admin/weight-rules', { method: 'POST', body });
    toast('Правило веса сохранено');
  });
}

async function removeWeightRule(r) {
  if (!confirm(`Удалить правило веса «${r.text} → ${fmt(r.weight)}»?`)) return;
  await mutate(api(`/api/admin/weight-rules/${r.id}`, { method: 'DELETE' }), 'Правило веса удалено').catch(() => {});
}

export function initConstructor(changed) {
  onChanged = changed;
  for (const b of document.querySelectorAll('[data-add-field]')) {
    b.addEventListener('click', () => editField(null, b.dataset.addField));
  }
  $('add-rule').addEventListener('click', () => editRule(null));
  $('add-weight-rule').addEventListener('click', () => editWeightRule(null));
}
