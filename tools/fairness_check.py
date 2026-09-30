#!/usr/bin/env python3
"""Краш-тест справедливости: главное условие ТЗ — заявки распределяются между исполнителями равномерно.

Каждый сценарий заводит своих исполнителей в отделе «Краш-тест» (создаётся сам, шаблон «Банк»), шлёт заявки
через эмулятор АИС — тем же путём, что и в жизни (АИС → балансировщик → назначение), — и сравнивает, кому
сколько досталось, с тем, что должно было достаться. Ожидаемый результат в каждом сценарии известен заранее.
Демо-отделы не затрагиваются; после сценария его исполнители выключаются, а заявки закрыты.

Только стандартная библиотека. Запуск — там, где доступен эмулятор АИС (на сервере он слушает только 127.0.0.1):
    python tools/fairness_check.py                       # основные сценарии, ~3–5 минут
    python tools/fairness_check.py --only two_equal      # один сценарий
    python tools/fairness_check.py --redis-restart "docker compose restart redis" \\
                                   --api-restart "docker compose restart api"   # ещё и сбои посреди потока
Ключи и пароль администратора берутся из .env (INTEGRATION_API_KEY, AIS_API_KEY, ADMIN_PASSWORD) или из аргументов.
"""
import argparse
import http.cookiejar
import json
import os
import random
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

DEPARTMENT_CODE = "crashtest"
DEPARTMENT_NAME = "Краш-тест"
ALL_SKILLS = {
    "min_sum": 0,
    "max_sum": 10_000_000,
    "order_types": ["ORDER_1", "ORDER_2", "ORDER_3"],
    "subjects": ["кредит", "вклад", "карты", "ипотека", "страхование"],
    "segments": ["микро", "малый", "средний", "крупный"],
    "client_classes": ["обычный", "VIP"],
}
# вес заявки по правилам шаблона «Банк»: сумма от 200 000 — 2, от 1 000 000 — 3, иначе 1
SUM_FOR_WEIGHT = {1: 50_000, 2: 300_000, 3: 1_500_000}


def read_env(name):
    if os.environ.get(name):
        return os.environ[name]
    env = Path(__file__).resolve().parent.parent / ".env"
    if env.exists():
        for line in env.read_text(encoding="utf-8").splitlines():
            if line.startswith(name + "="):
                return line.split("=", 1)[1].strip()
    return None


class Client:
    """HTTP без сторонних библиотек: АИС (ключ АИС), интеграция балансировщика (ключ интеграции), админка (сессия)."""

    def __init__(self, api, ais, integration_key, ais_key):
        self.api, self.ais = api.rstrip("/"), ais.rstrip("/")
        self.integration_key, self.ais_key = integration_key, ais_key
        self.admin = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

    @staticmethod
    def _call(opener, url, method="GET", body=None, headers=None, timeout=15):
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(url, data=data, method=method, headers={"Content-Type": "application/json", **(headers or {})})
        try:
            with (opener.open(req, timeout=timeout) if opener else urllib.request.urlopen(req, timeout=timeout)) as resp:
                raw = resp.read()
                return resp.status, (json.loads(raw) if raw else None)
        except urllib.error.HTTPError as e:
            raw = e.read()
            try:
                return e.code, json.loads(raw) if raw else None
            except ValueError:
                return e.code, None

    def ais_call(self, method, path, body=None):
        return self._call(None, f"{self.ais}/api/ais{path}", method, body, {"X-Api-Key": self.ais_key})

    def integration(self, method, path, body=None):
        return self._call(None, f"{self.api}/api/integration{path}", method, body, {"X-Api-Key": self.integration_key})

    def admin_call(self, method, path, body=None):
        return self._call(self.admin, f"{self.api}{path}", method, body, {"X-Requested-With": "executor-balancer"})

    def healthy(self):
        try:
            return self._call(None, f"{self.api}/health", timeout=3)[0] == 200
        except OSError:
            return False


class Run:
    """Один сценарий: свои исполнители, свои заявки, подсчёт по назначениям."""

    def __init__(self, client, first_id):
        self.c = client
        self.next_id = first_id
        self.executors = []
        self.assigned = {}   # номер заявки -> исполнитель
        self.weights = {}    # номер заявки -> вес
        self.latency = []    # секунды от отправки в АИС до назначения
        self.lost = 0
        self.foreign = 0     # назначено не исполнителю сценария — значит, остался кто-то посторонний
        self.lock = threading.Lock()

    def executor(self, name, qualification=1.0, daily_limit=None, skills=None):
        eid = self.next_id
        self.next_id += 1
        status, _ = self.c.ais_call("PUT", f"/executors/{eid}", {
            "department": DEPARTMENT_CODE, "fullName": name, "isActive": True, "dailyLimit": daily_limit,
            "qualificationWeight": qualification, "attributes": {**ALL_SKILLS, **(skills or {})}})
        if status != 200:
            raise RuntimeError(f"АИС не завела исполнителя ({status})")
        self.executors.append((eid, name))
        return eid

    def wait_executors_known(self):
        # АИС передаёт исполнителей балансировщику асинхронно — ждём, пока все появятся
        deadline = time.time() + 15
        while time.time() < deadline:
            status, body = self.c.admin_call("GET", f"/api/dashboard/summary?department={self.department_id}")
            known = {e["id"] for e in (body or {}).get("executors", [])} if status == 200 else set()
            if all(eid in known for eid, _ in self.executors):
                return
            time.sleep(0.3)
        raise RuntimeError("исполнители не дошли до балансировщика за 15 с")

    def set_active(self, eid, active):
        self.c.ais_call("POST", f"/executors/{eid}/active", {"isActive": active})
        time.sleep(1.0)  # АИС пересылает изменение асинхронно

    def send(self, weight=1, subject=None, client_class="обычный"):
        attributes = {"sum": SUM_FOR_WEIGHT[weight], "order_type": "ORDER_1",
                      "subject": subject or random.choice(ALL_SKILLS["subjects"]),
                      "client_segment": "малый", "client_class": client_class}
        started = time.time()
        status, body = self.c.ais_call("POST", "/orders", {"department": DEPARTMENT_CODE, "attributes": attributes})
        if status != 201:
            raise RuntimeError(f"АИС не приняла заявку ({status})")
        oid = body["id"]
        # назначение появляется, как только АИС переслала заявку; ждём до 20 с
        deadline = started + 20
        while time.time() < deadline:
            st, a = self.c.integration("GET", f"/orders/{oid}/assignment")
            if st == 200:
                with self.lock:
                    if all(a["executorId"] != eid for eid, _ in self.executors):
                        self.foreign += 1
                    self.assigned[oid] = a["executorId"]
                    self.weights[oid] = weight
                    self.latency.append(time.time() - started)
                self.c.ais_call("POST", f"/orders/{oid}/status", {"status": "accept"})
                return a["executorId"]
            time.sleep(0.03)
        with self.lock:
            self.lost += 1
        return None

    def send_many(self, count, workers=4, weight_fn=lambda: 1, subject_fn=lambda: None):
        with ThreadPoolExecutor(max_workers=workers) as pool:
            list(pool.map(lambda _: self.send(weight_fn(), subject_fn()), range(count)))

    def totals(self):
        count = {eid: 0 for eid, _ in self.executors}
        weight = {eid: 0 for eid, _ in self.executors}
        for oid, eid in self.assigned.items():
            if eid in count:
                count[eid] += 1
                weight[eid] += self.weights[oid]
        return count, weight

    def drain(self):
        # дождаться, пока АИС перешлёт всё (закрытия заявок), и выключить исполнителей сценария
        deadline = time.time() + 30
        while time.time() < deadline:
            st, stats = self.c.ais_call("GET", f"/stats?department={DEPARTMENT_CODE}")
            if st == 200 and stats["forwarding"]["backlog"] == 0:
                break
            time.sleep(0.3)
        for eid, _ in self.executors:
            self.c.ais_call("POST", f"/executors/{eid}/active", {"isActive": False})


def retire_leftovers(client, department_id):
    """Хвосты прерванного прогона: исполнители на работе (отправляем на перерыв) и ждущие заявки (отклоняем)."""
    status, body = client.admin_call("GET", f"/api/dashboard/summary?department={department_id}")
    leftovers = [e["id"] for e in (body or {}).get("executors", []) if e.get("isActive")] if status == 200 else []
    for eid in leftovers:
        client.ais_call("POST", f"/executors/{eid}/active", {"isActive": False})
    if leftovers:
        time.sleep(1.0)
    # заявки прерванного прогона, ждущие исполнителя, достались бы следующему сценарию и сбили бы счёт — отклоняем
    waiting = 0
    for _ in range(50):  # по 200 за раз; предел — чтобы не зациклиться, если отклонить не выходит
        status, body = client.admin_call("GET", f"/api/dashboard/orders?department={department_id}&state=pending&limit=200")
        items = (body or {}).get("items", []) if status == 200 else []
        if not items:
            break
        for order in items:
            client.integration("POST", f"/orders/{order['id']}/status", {"status": "reject"})
        waiting += len(items)
    return len(leftovers) + waiting


def pct(part, whole):
    return 100.0 * part / whole if whole else 0.0


def fmt_split(values):
    return " / ".join(str(v) for v in values)


# ---------------- сценарии ----------------
# каждый возвращает (ожидали, получили, отклонение в %, прошёл ли, пояснение)

def two_equal(run):
    """Двое одинаковых, 200 заявок одного веса — строго по очереди."""
    a, b = run.executor("Анна"), run.executor("Борис")
    run.wait_executors_known()
    run.send_many(200)
    count, _ = run.totals()
    got = [count[a], count[b]]
    dev = pct(abs(got[0] - got[1]) / 2, 100)
    return "100 / 100", fmt_split(got), dev, abs(got[0] - got[1]) <= 1 and run.lost == 0, "разница не больше 1 заявки"


def five_mixed(run):
    """Пятеро одинаковых, заявки весом 1–3 — выравнивается суммарный вес."""
    ids = [run.executor(f"Сотрудник {i + 1}") for i in range(5)]
    run.wait_executors_known()
    run.send_many(500, weight_fn=lambda: random.choices([1, 2, 3], [0.6, 0.3, 0.1])[0])
    _, weight = run.totals()
    got = [weight[i] for i in ids]
    mean = sum(got) / len(got)
    dev = max(pct(abs(w - mean), mean) for w in got)
    return f"по {mean:.0f} веса", fmt_split(got), dev, max(got) - min(got) <= 3 and run.lost == 0, "вес: разброс не больше одной тяжёлой заявки (3)"


def qualification(run):
    """Квалификация 1 и 2 — опытному вдвое больше."""
    a, b = run.executor("Новичок, ×1", 1.0), run.executor("Опытный, ×2", 2.0)
    run.wait_executors_known()
    run.send_many(300)
    count, _ = run.totals()
    got = [count[a], count[b]]
    dev = pct(abs(got[1] - 200), 200)
    return "100 / 200", fmt_split(got), dev, abs(got[0] - 100) <= 1 and run.lost == 0, "доли 1/3 и 2/3"


def specialist(run):
    """Узкий специалист: умеет только кредиты — получает кредиты, остальное делят двое."""
    a, b = run.executor("Универсал 1"), run.executor("Универсал 2")
    c = run.executor("Только кредиты", skills={"subjects": ["кредит"]})
    run.wait_executors_known()
    subjects = [random.choice(ALL_SKILLS["subjects"]) for _ in range(300)]
    credits = subjects.count("кредит")
    queue = iter(subjects)
    lock = threading.Lock()

    def next_subject():
        with lock:
            return next(queue)
    run.send_many(300, subject_fn=next_subject)
    count, _ = run.totals()
    got = [count[a], count[b], count[c]]
    rest = 300 - credits
    dev = max(pct(abs(got[0] - rest / 2), rest / 2), pct(abs(got[1] - rest / 2), rest / 2))
    ok = abs(got[0] - got[1]) <= 1 and abs(got[2] - credits) <= 1 and run.lost == 0
    return f"{rest // 2} / {rest - rest // 2} / {credits}", fmt_split(got), dev, ok, "специалисту — кредиты, остальное поровну"


def break_return(run):
    """Третий уходит на перерыв и возвращается — догоняет, к концу у всех поровну."""
    ids = [run.executor(n) for n in ("Анна", "Борис", "Вера")]
    run.wait_executors_known()
    run.send_many(90, workers=1)
    run.set_active(ids[2], False)
    run.send_many(90, workers=1)
    run.set_active(ids[2], True)
    before = dict(run.assigned)
    run.send_many(120, workers=1)
    count, _ = run.totals()
    got = [count[i] for i in ids]
    # сколько заявок подряд получила вернувшаяся Вера — это «догоняет»
    after = [run.assigned[o] for o in sorted(run.assigned) if o not in before]
    streak = 0
    for eid in after:
        if eid != ids[2]:
            break
        streak += 1
    dev = pct(max(got) - min(got), 100)
    return "100 / 100 / 100", fmt_split(got), dev, max(got) - min(got) <= 1 and run.lost == 0, f"после перерыва Вера получила {streak} заявок подряд и догнала"


def daily_limit(run):
    """Суточная норма: у одного норма 20 — больше он не получает, остальное второму."""
    a, b = run.executor("С нормой 20", daily_limit=20), run.executor("Без нормы")
    run.wait_executors_known()
    run.send_many(60)
    count, _ = run.totals()
    got = [count[a], count[b]]
    return "20 / 40", fmt_split(got), pct(abs(got[0] - 20), 20), got[0] == 20 and got[1] == 40 and run.lost == 0, "норма не превышена ни на одну заявку"


def burst(run):
    """Всплеск 20 000 заявок в час в течение минуты на 10 одинаковых — ничего не потеряно, поровну."""
    ids = [run.executor(f"Сотрудник {i + 1}") for i in range(10)]
    run.wait_executors_known()
    total, seconds = 330, 60.0
    interval = seconds / total
    with ThreadPoolExecutor(max_workers=16) as pool:
        futures = []
        start = time.time()
        for i in range(total):
            delay = start + i * interval - time.time()
            if delay > 0:
                time.sleep(delay)
            futures.append(pool.submit(run.send))
        for f in futures:
            f.result()
    count, _ = run.totals()
    got = [count[i] for i in ids]
    lat = sorted(run.latency)
    p50 = lat[len(lat) // 2] * 1000 if lat else 0
    p95 = lat[int(len(lat) * 0.95)] * 1000 if lat else 0
    dev = pct(max(got) - min(got), total / 10)
    return f"по {total // 10}, 0 потерь", fmt_split(got), dev, max(got) - min(got) <= 1 and run.lost == 0, \
        f"потеряно {run.lost}; путь АИС→назначение: медиана {p50:.0f} мс, 95% — {p95:.0f} мс"


def with_restart(command, what):
    def scenario(run):
        ids = [run.executor(n) for n in ("Анна", "Борис", "Вера")]
        run.wait_executors_known()
        run.send_many(150, workers=2)
        subprocess.run(command, shell=True, check=False)
        deadline = time.time() + 90
        while time.time() < deadline and not run.c.healthy():
            time.sleep(1)
        time.sleep(3)
        run.send_many(150, workers=2)
        count, _ = run.totals()
        got = [count[i] for i in ids]
        ok = run.lost == 0 and max(got) - min(got) <= 2
        return "100 / 100 / 100, 0 потерь", fmt_split(got), pct(max(got) - min(got), 100), ok, f"{what}; потеряно {run.lost}"
    scenario.__doc__ = f"{what} посреди потока — ничего не потеряно, распределение ровное."
    return scenario


SCENARIOS = [
    ("two_equal", "Двое одинаковых, 200 заявок", two_equal),
    ("five_mixed", "Пятеро одинаковых, вес заявок 1–3", five_mixed),
    ("qualification", "Квалификация 1 и 2", qualification),
    ("specialist", "Узкий специалист (только кредиты)", specialist),
    ("break_return", "Перерыв и возвращение", break_return),
    ("daily_limit", "Суточная норма", daily_limit),
    ("burst", "Всплеск 20 000 заявок/ч, 10 человек", burst),
]


def ensure_department(client, password):
    status, _ = client.admin_call("POST", "/api/auth/login", {"login": "admin", "password": password})
    if status not in (200, 204):
        raise RuntimeError(f"не удалось войти администратором ({status}) — проверьте ADMIN_PASSWORD")
    _, departments = client.admin_call("GET", "/api/admin/departments")
    for d in departments or []:
        if d["code"] == DEPARTMENT_CODE:
            return d["id"]
    status, created = client.admin_call("POST", "/api/admin/departments",
                                        {"name": DEPARTMENT_NAME, "code": DEPARTMENT_CODE, "presetId": "bank"})
    if status != 200:
        raise RuntimeError(f"не удалось создать отдел «{DEPARTMENT_NAME}» ({status}): {created}")
    return created["id"]


def current_hour():
    return int(time.time() // 3600)


def main():
    parser = argparse.ArgumentParser(description="Краш-тест справедливого распределения")
    parser.add_argument("--api", default="http://127.0.0.1:8080")
    parser.add_argument("--ais", default="http://127.0.0.1:8081")
    parser.add_argument("--admin-password", default=None)
    parser.add_argument("--only", default=None, help="через запятую: " + ", ".join(k for k, _, _ in SCENARIOS))
    parser.add_argument("--redis-restart", default=None, help="команда перезапуска Redis, например: docker compose restart redis")
    parser.add_argument("--api-restart", default=None, help="команда перезапуска сервиса, например: docker compose restart api")
    parser.add_argument("--seed", type=int, default=7)
    parser.add_argument("--json", default=None, help="сохранить результаты в файл")
    args = parser.parse_args()
    random.seed(args.seed)

    integration_key, ais_key = read_env("INTEGRATION_API_KEY"), read_env("AIS_API_KEY")
    password = args.admin_password or read_env("ADMIN_PASSWORD")
    if not (integration_key and ais_key and password):
        sys.exit("Нужны INTEGRATION_API_KEY, AIS_API_KEY и ADMIN_PASSWORD (в .env или переменных окружения)")
    client = Client(args.api, args.ais, integration_key, ais_key)
    department_id = ensure_department(client, password)
    retired = retire_leftovers(client, department_id)

    scenarios = list(SCENARIOS)
    if args.redis_restart:
        scenarios.append(("redis_restart", "Перезапуск Redis посреди потока", with_restart(args.redis_restart, "Redis перезапущен")))
    if args.api_restart:
        scenarios.append(("api_restart", "Перезапуск сервиса посреди потока", with_restart(args.api_restart, "сервис перезапущен")))
    if args.only:
        wanted = set(args.only.split(","))
        scenarios = [s for s in scenarios if s[0] in wanted]

    first_id = int(time.time()) * 100  # номера исполнителей — свои у каждого прогона
    rows = []
    print(f"Отдел «{DEPARTMENT_NAME}» ({DEPARTMENT_CODE}), сценариев: {len(scenarios)}"
          + (f"; убрано хвостов прошлого прогона: {retired}" if retired else "") + "\n")
    for key, title, fn in scenarios:
        for attempt in (1, 2):
            run = Run(client, first_id)
            run.department_id = department_id
            hour = current_hour()
            print(f"• {title}…", end=" ", flush=True)
            try:
                expected, got, dev, ok, note = fn(run)
            except Exception as e:  # сценарий не смог пройти — это тоже результат
                expected, got, dev, ok, note = "—", "—", 0.0, False, f"ошибка: {e}"
            finally:
                first_id = run.next_id + 10
                run.drain()
            if run.foreign:
                ok, note = False, f"{note}; ушло посторонним исполнителям: {run.foreign}"
            # выбор идёт по нагрузке за текущий час: сценарий, попавший на смену часа, повторяем
            if current_hour() != hour and attempt == 1:
                print("пришёлся на смену часа — повторяю")
                continue
            print("✓" if ok else "✗")
            rows.append({"key": key, "title": title, "expected": expected, "got": got, "deviation": round(dev, 2),
                         "ok": ok, "note": note})
            break

    width = max(len(r["title"]) for r in rows) if rows else 10
    print(f"\n{'Сценарий'.ljust(width)}  {'Ожидали':<24} {'Получили':<24} {'Откл.':>7}  Итог")
    for r in rows:
        print(f"{r['title'].ljust(width)}  {r['expected']:<24} {r['got']:<24} {r['deviation']:>6.2f}%  {'✓' if r['ok'] else '✗'}  {r['note']}")
    passed = sum(r["ok"] for r in rows)
    print(f"\nПройдено {passed} из {len(rows)}")
    if args.json:
        Path(args.json).write_text(json.dumps(rows, ensure_ascii=False, indent=2), encoding="utf-8")
    sys.exit(0 if passed == len(rows) else 1)


if __name__ == "__main__":
    main()
