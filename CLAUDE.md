# Executor Balancer — контекст для Claude

Хакатон 2026, кейс «Executor Balancer»: сервис распределения заявок между исполнителями в реальном времени.
Полуфинал — пн 28.09.2026 (презентация + работающий защищённый сайт), финал — чт 01.10.2026.
Язык интерфейса, комментариев и документации — русский.

## Стек и структура

.NET 10 (Minimal API), EF Core + PostgreSQL, Redis (Lua), xUnit, фронтенд — чистый JS-модули без сборки
(`src/ExecutorBalancer.Api/wwwroot/js`), Python-скрипты без зависимостей. Подробно — README.md,
docs/DECISIONS.md (трактовки ТЗ), docs/ARCHITECTURE.md (диаграммы, алгоритм, метрика справедливости).

## Команды

- `dotnet build` — должно быть без предупреждений (`AnalysisLevel latest-recommended`).
- `dotnet test` — SQLite в памяти + `InMemoryLoadStore` (та же семантика, что у Lua).
- `python tools/fairness_check.py` — краш-тест равномерности на запущенном стенде (свой отдел «Краш-тест»).
- `REDIS_PORT=6379 python scripts/check_lua.py` — Lua-скрипты на живом Redis (делает FLUSHALL!).
- Миграции: `dotnet ef migrations add <Имя> -p src/ExecutorBalancer.Infrastructure -s src/ExecutorBalancer.Infrastructure -o Persistence/Migrations`.
- Сквозной запуск: `docker compose up -d --build`, затем `python tools/loadgen.py --setup` и
  `python tools/loadgen.py --rate 4000 --duration 300 --chaos`.

## Интерфейс

Всё делится на отделы (`Department`, `DepartmentId` у настроек, исполнителей, заявок, статистики): отдел выбирается
в шапке (`department.js`), `api.js` сам добавляет `?department=` к `/api/dashboard` и `/api/admin`, на сервере —
`DepartmentScope` + фильтр `RequireDepartment`. Любой новый запрос к данным — с фильтром по отделу.
`wwwroot/js`: `main.js` — разделы меню (`GROUPS`) и вкладки; по файлу на вкладку (`overview.js` — Мониторинг, `executors.js`, `analytics.js`,
`constructor.js`, `preview.js`, `import.js` (сотрудники из CSV, сервер — `ExecutorImportService`), `departments.js` + `sphere.js` (мастер своей сферы, сервер — `SphereBuilder`), `profile.js`
(страница сотрудника `#executor-ID`), `audit.js`, `insights.js` — Разбор заявок (спрос и покрытие — `Application/Insights/DemandAnalyzer.cs`, ИИ — `AiAnalyst.cs` + `Infrastructure/Ai`), `demo.js` — Тестовый стенд (+ `duo.js` — «Два сотрудника», сервер — `/scenario/duo` и `/api/dashboard/split`), `start.js` — «Как это работает», `orders.js` — Заявки, `users.js` — Пользователи); общие — `session.js` (роль: `can('Manager')`), `api.js`, `dom.js`, `forms.js` (форма по справочнику),
`editor.js` (диалог), `charts.js`, `explain.js`, `presets.js`. Пульт демонстрации — `Endpoints/DemoEndpoints.cs`
(прокси к эмулятору), генератор и симуляция — `src/AisEmulator.Api/Simulation`, шаблоны сфер —
`Application/Configuration/DomainPresets.cs`.

## Правила кода

- Безопасность — требование кейса: никаких `eval`, только белый список операторов; в интерфейсе данные
  только через `textContent`; строгая CSP (никаких inline-скриптов/стилей и CDN; сторонние файлы — только в `wwwroot/vendor` с лицензией
  и записью в `THIRD_PARTY_NOTICES.md`; оформление — классы Tabler, иконки — `icon()` из `dom.js` и `icons.svg`); изменяющие запросы
  администратора — с заголовком `X-Requested-With: executor-balancer`; секреты только из окружения.
- Роли: наблюдатель / руководитель / администратор (`UserRole`). Новый изменяющий адрес — с
  `.RequireAuthorization(Policies.Manager|Admin)`; доступ к отделу проверяет `DepartmentScope.RequireDepartment`;
  кнопки в интерфейсе — через `can()` или классы `need-manager` / `need-admin`. Автор в журнале — `ICurrentActor`.
- Любое изменение Lua-скрипта — повторить в `tests/ExecutorBalancer.Tests/InMemoryLoadStore.cs`
  и прогнать `scripts/check_lua.py`.
- Решение, объяснение, outbox и сводные метрики пишутся в одной транзакции.
- Изменения конфигурации — через `ConfigurationService`: проверка, аудит, `BumpConfigVersionAsync`.

## Мотивация (свои фичи)

Рейтинг (балл = вес × коэффициент качества, `Motivation.QualityOf`), режим «больше нормы» (второй ярус
в Lua-скрипте выбора: только излишки, `Motivation.Extra`), защита (потолок, автоприостановка по качеству
из `QualityTracker`, «вне рейтинга» ниже порога). Настройки — у `Department`, правка через
`ConfigurationService.UpdateMotivationAsync`; интерфейс — `motivation.js`, рейтинг — в `analytics.js`.
