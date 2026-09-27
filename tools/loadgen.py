#!/usr/bin/env python3
"""Генератор нагрузки для демонстрации: создаёт исполнителей в эмуляторе АИС,
шлёт поток заявок и имитирует работу исполнителей (решено / отклонено / на доработку).

Только стандартная библиотека. Примеры:
    python tools/loadgen.py --setup                      # завести исполнителей
    python tools/loadgen.py --rate 4000 --duration 300   # 4000 заявок в час, 5 минут
    python tools/loadgen.py --rate 20000 --duration 60   # пиковая нагрузка
    python tools/loadgen.py --rate 4000 --chaos          # в середине прогона один исполнитель «уходит»
"""
import argparse
import json
import random
import sys
import threading
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

ORDER_TYPES = ["ORDER_1", "ORDER_2", "ORDER_3"]
SUBJECTS = ["кредит", "вклад", "карты", "ипотека", "страхование"]
SEGMENTS = ["микро", "малый", "средний", "крупный"]
NAMES = ["Иванов И.", "Петрова А.", "Сидоров К.", "Галиева Р.", "Хасанов Т.", "Кузнецова Е.", "Смирнов Д.",
         "Юсупова Л.", "Абдуллин Р.", "Морозова О.", "Валиев А.", "Никитина М.", "Фёдоров С.", "Ахметова З.",
         "Попов В.", "Гарипова Э.", "Соколов П.", "Лебедева Н.", "Шарипов И.", "Козлова Ю."]


def read_env_key(name):
    env = Path(__file__).resolve().parent.parent / ".env"
    if env.exists():
        for line in env.read_text(encoding="utf-8").splitlines():
            if line.startswith(name + "="):
                return line.split("=", 1)[1].strip()
    return None


class Ais:
    def __init__(self, base, key):
        self.base = base.rstrip("/")
        self.key = key

    def call(self, method, path, body=None):
        data = json.dumps(body).encode("utf-8") if body is not None else None
        request = urllib.request.Request(self.base + path, data=data, method=method, headers={
            "X-Api-Key": self.key, "Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=10) as response:
            raw = response.read()
            return json.loads(raw) if raw else None


def setup_executors(ais, count, rng):
    for i in range(1, count + 1):
        vip = rng.random() < 0.4
        subjects = sorted(set(rng.sample(SUBJECTS, rng.randint(2, 5)) + [SUBJECTS[i % len(SUBJECTS)]]))
        ais.call("PUT", f"/api/ais/executors/{i}", {
            "fullName": NAMES[(i - 1) % len(NAMES)],
            "isActive": True,
            "dailyLimit": None if rng.random() < 0.3 else rng.choice([60, 80, 120, 200]),
            "qualificationWeight": rng.choice([0.8, 1, 1, 1.2, 1.5, 2]),
            "attributes": {
                "min_sum": 0,
                "max_sum": rng.choice([500_000, 2_000_000, 10_000_000]),
                "order_types": ORDER_TYPES,
                "subjects": subjects,
                "segments": SEGMENTS,
                "client_classes": ["обычный", "VIP"] if vip else ["обычный"],
            },
        })
    print(f"Заведено исполнителей: {count}")


def random_order(rng, recent):
    order = {"attributes": {
        "sum": round(rng.lognormvariate(11.5, 1.1), 2),
        "order_type": rng.choice(ORDER_TYPES),
        "subject": rng.choice(SUBJECTS),
        "client_segment": rng.choice(SEGMENTS),
        "client_class": "VIP" if rng.random() < 0.08 else "обычный",
    }}
    if recent and rng.random() < 0.08:
        order["parentId"] = rng.choice(recent)
    return order


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--ais", default="http://127.0.0.1:8081")
    parser.add_argument("--key", default=None, help="ключ АИС (по умолчанию AIS_API_KEY из .env)")
    parser.add_argument("--setup", action="store_true", help="только завести исполнителей")
    parser.add_argument("--executors", type=int, default=15)
    parser.add_argument("--rate", type=float, default=3600, help="заявок в час")
    parser.add_argument("--duration", type=float, default=120, help="секунд")
    parser.add_argument("--chaos", action="store_true", help="деактивировать исполнителя в середине прогона")
    parser.add_argument("--seed", type=int, default=42)
    args = parser.parse_args()

    key = args.key or read_env_key("AIS_API_KEY")
    if not key:
        sys.exit("Нет ключа: передайте --key или задайте AIS_API_KEY в .env")
    ais = Ais(args.ais, key)
    rng = random.Random(args.seed)

    try:
        existing = ais.call("GET", "/api/ais/executors")
    except urllib.error.URLError as error:
        sys.exit(f"Эмулятор АИС недоступен по {args.ais}: {error}")
    if args.setup or not existing:
        setup_executors(ais, args.executors, rng)
        if args.setup:
            return

    stop = threading.Event()
    lock = threading.Lock()
    recent, awaiting = [], {}
    counters = {"sent": 0, "errors": 0, "accept": 0, "reject": 0, "await": 0, "back": 0}

    def bump(name):
        with lock:
            counters[name] += 1

    def send_order():
        with lock:
            snapshot = list(recent[-200:])
        try:
            created = ais.call("POST", "/api/ais/orders", random_order(rng, snapshot))
            with lock:
                recent.append(created["id"])
            bump("sent")
        except Exception:
            bump("errors")

    def change_status(order_id, status):
        try:
            ais.call("POST", f"/api/ais/orders/{order_id}/status", {"status": status})
            bump("back" if status == "processed" else status)
        except Exception:
            bump("errors")

    def worker_simulation():
        # исполнители работают параллельно: часть назначенных заявок закрывается, часть уходит на доработку;
        # смены статусов идут из нескольких потоков одновременно — как у живых сотрудников
        with ThreadPoolExecutor(max_workers=8) as workers:
            while not stop.wait(1.0):
                try:
                    orders = ais.call("GET", "/api/ais/orders?status=processed&assigned=true&limit=300")
                    now = time.time()
                    changes = []
                    for order in orders:
                        if rng.random() > 0.15:
                            continue
                        roll = rng.random()
                        status = "accept" if roll < 0.65 else "reject" if roll < 0.75 else "await"
                        changes.append((order["id"], status))
                        if status == "await":
                            with lock:
                                awaiting[order["id"]] = now + rng.uniform(5, 20)
                    with lock:
                        due = [oid for oid, at in awaiting.items() if at <= now]
                        for oid in due:
                            del awaiting[oid]
                    changes += [(oid, "processed") for oid in due]  # вернулась с доработки — вторичная заявка
                    list(workers.map(lambda c: change_status(*c), changes))
                except Exception:
                    bump("errors")

    def chaos():
        if stop.wait(args.duration / 2):
            return
        executors = [e for e in ais.call("GET", "/api/ais/executors") if e["isActive"]]
        victim = rng.choice(executors)
        print(f"\n>>> {victim['fullName']} (#{victim['id']}) уходит — его заявки должны перераспределиться\n")
        ais.call("POST", f"/api/ais/executors/{victim['id']}/active", {"isActive": False})
        if not stop.wait(min(30, args.duration / 4)):
            ais.call("POST", f"/api/ais/executors/{victim['id']}/active", {"isActive": True})
            print(f"\n>>> {victim['fullName']} вернулся\n")

    threading.Thread(target=worker_simulation, daemon=True).start()
    if args.chaos:
        threading.Thread(target=chaos, daemon=True).start()

    interval = 3600.0 / args.rate
    started = time.time()
    next_at, next_report = started, started + 5
    print(f"Поток: {args.rate:.0f} заявок/ч на {args.duration:.0f} с → {args.ais}")
    with ThreadPoolExecutor(max_workers=16) as pool:
        while time.time() - started < args.duration:
            now = time.time()
            while next_at <= now:
                pool.submit(send_order)
                next_at += interval
            if now >= next_report:
                next_report += 5
                try:
                    stats = ais.call("GET", "/api/ais/stats")
                    store = stats["store"]
                    with lock:
                        c = dict(counters)
                    print(f"[{now - started:5.0f} с] отправлено {c['sent']:6d} | в АИС: без исполнителя "
                          f"{store['processedWithoutExecutor']:4d}, решено {store['byStatus']['accept']:5d}, "
                          f"на доработке {store['byStatus']['await']:3d} | очередь в балансировщик "
                          f"{stats['forwarding']['backlog']:4d} | ошибок {c['errors']}")
                except Exception as error:
                    print("статистика недоступна:", error)
            time.sleep(min(0.05, max(0.0, next_at - time.time())))
    stop.set()
    print("Готово:", counters)
    print("Откройте дашборд: http://127.0.0.1:8080")


if __name__ == "__main__":
    main()
