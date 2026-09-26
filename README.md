# Executor Balancer (.NET)

Сервис распределения заявок между исполнителями в реальном времени.
Готово: ядро (этап 1) и интеграция с АИС, дашборд, генератор нагрузки (этап 2).

Стек: .NET 10, ASP.NET Core Minimal API, EF Core + PostgreSQL, Redis (атомарный выбор), xUnit, Python для нагрузки.

## Структура

```
src/ExecutorBalancer.Domain          сущности и перечисления
src/ExecutorBalancer.Application     движок правил, алгоритм, OrderBalancer
src/ExecutorBalancer.Infrastructure  EF Core, миграции, Redis + Lua, доставка в АИС, повтор ожидающих
src/ExecutorBalancer.Api             HTTP API, дашборд (wwwroot), вход администратора, защита
src/AisEmulator.Api                  эмулятор внешней АИС (назначение записывается с задержкой 2–10 с)
tests/ExecutorBalancer.Tests         модульные тесты и тесты конкурентности
tools/loadgen.py                     генератор нагрузки
docs/DECISIONS.md                    трактовки ТЗ и принятые решения
```

## Запуск (Windows PowerShell)

1. Секреты — одной командой, файл `.env` создаётся со случайными значениями:

```powershell
function New-Secret { -join ((48..57)+(65..90)+(97..122) | Get-Random -Count 32 | % {[char]$_}) }
@"
POSTGRES_PASSWORD=$(New-Secret)
REDIS_PASSWORD=$(New-Secret)
INTEGRATION_API_KEY=$(New-Secret)
AIS_API_KEY=$(New-Secret)
ADMIN_PASSWORD=$(New-Secret)
SWAGGER_ENABLED=true
"@ | Set-Content -Encoding ascii .env
Get-Content .env
```

`ADMIN_PASSWORD` — пароль для входа в дашборд.

2. Сборка и тесты: `dotnet build` и `dotnet test`.
3. Запуск: `docker compose up -d --build`.

| Адрес | Что |
|---|---|
| http://127.0.0.1:8080 | дашборд (пароль `ADMIN_PASSWORD`) |
| http://127.0.0.1:8080/swagger | описание API балансировщика |
| http://127.0.0.1:8081 | эмулятор АИС (API, ключ `AIS_API_KEY`) |

4. Нагрузка (Python 3, без установки пакетов):

```powershell
python tools/loadgen.py --setup                      # завести 15 исполнителей
python tools/loadgen.py --rate 4000 --duration 300   # 4000 заявок/ч, 5 минут
python tools/loadgen.py --rate 20000 --duration 60   # пик
python tools/loadgen.py --rate 4000 --chaos          # исполнитель «уходит» в середине прогона
```

Путь заявки: генератор → эмулятор АИС → балансировщик (выбор за миллисекунды) →
доставка назначения обратно в АИС → запись в АИС через 2–10 с.

## API интеграции

Все запросы — с заголовком `X-Api-Key`.

```bash
KEY=ваш_ключ; URL=http://127.0.0.1:8080/api/integration

# исполнитель
curl -X PUT $URL/executors/1 -H "X-Api-Key: $KEY" -H "Content-Type: application/json" -d '{
  "fullName": "Иванов И. И.", "isActive": true, "dailyLimit": 40, "qualificationWeight": 1.5,
  "attributes": {"min_sum": 0, "max_sum": 5000000, "order_types": ["ORDER_1","ORDER_2"],
    "subjects": ["credit","cards"], "segments": ["small","medium"], "client_classes": ["standard","vip"]}}'

# заявка
curl -X POST $URL/orders -H "X-Api-Key: $KEY" -H "Content-Type: application/json" -d '{
  "id": 1001, "parentId": null,
  "attributes": {"sum": 250000, "order_type": "ORDER_1", "subject": "credit",
    "client_segment": "small", "client_class": "standard"}}'

# почему назначен именно этот исполнитель
curl $URL/orders/1001/assignment -H "X-Api-Key: $KEY"

# смена статуса: processed | await | accept | reject
curl -X POST $URL/orders/1001/status -H "X-Api-Key: $KEY" -H "Content-Type: application/json" -d '{"status":"await"}'
```

Повторная отправка той же заявки не создаёт второго назначения — в ответе `"duplicate": true`.

## Безопасность (этап 1)

Ключ интеграции (сравнение за постоянное время, длина ≥ 24), лимит частоты запросов, лимит тела 256 КБ,
проверка входных данных, ошибки в формате ProblemDetails без стека, заголовки безопасности,
секреты только из переменных окружения, контейнер без root и с файловой системой только для чтения,
Postgres и Redis не опубликованы наружу, API слушает только 127.0.0.1 (наружу — через обратный прокси с HTTPS).

## Важно

Код написан без компиляции: в среде разработки был закрыт доступ к .NET SDK и NuGet.
Синтаксис проверен парсером, логика — вручную. Если восстановление пакетов не найдёт какую-то версию,
поправьте её в `Directory.Packages.props` — все версии там.
