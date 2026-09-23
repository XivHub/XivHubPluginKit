"""Checks the /records endpoint: dedupe, attribution, invalid lines, and its separation from /log.

    python3 test_devlog_records.py

DevTelemetry.Record posts JSON lines to /records with the same session and offset headers /log
uses, so a retried batch must land once there too. Every stored line is one JSON object carrying
the sender ("client") and the receive time ("rx").
"""
import json
import os
import pathlib
import signal
import subprocess
import sys
import tempfile
import time
import urllib.request

TMP = pathlib.Path(tempfile.mkdtemp(prefix="devlog-records-test-"))
LOG = TMP / "live.log"
RECORDS = TMP / "records.jsonl"
BASE = "http://127.0.0.1:9902"
env = dict(os.environ, PORT="9902", LOGFILE=str(LOG), RECORDSFILE=str(RECORDS), DUMPDIR=str(TMP / "dumps"))
srv = subprocess.Popen([sys.executable, str(pathlib.Path(__file__).with_name("devlog_server.py"))],
                       env=env, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
time.sleep(1.0)


def post(path, body, session=None, offset=None):
    req = urllib.request.Request(f"{BASE}{path}?client=testbox", data=body.encode(), method="POST")
    if session is not None: req.add_header("X-Devlog-Session", session)
    if offset is not None: req.add_header("X-Devlog-Offset", str(offset))
    with urllib.request.urlopen(req, timeout=5) as r:
        return r.status


def get(path):
    with urllib.request.urlopen(f"{BASE}{path}", timeout=5) as r:
        return r.read().decode()


def records():
    return [json.loads(l) for l in RECORDS.read_text(encoding="utf-8").splitlines()] if RECORDS.exists() else []


failures = []


def check(name, got, want):
    ok = got == want
    print(f"{'ok  ' if ok else 'FAIL'} {name}: got {got!r}" + ("" if ok else f", want {want!r}"))
    if not ok:
        failures.append(name)


def strip(rec):
    return {k: v for k, v in rec.items() if k not in ("client", "rx")}


try:
    # A batch, its retry grown by one line, and the same retry again: each record lands once.
    post("/records", '{"seq":1}\n{"seq":2}\n', "sess1-r", 0)
    post("/records", '{"seq":1}\n{"seq":2}\n{"seq":3}\n', "sess1-r", 0)
    post("/records", '{"seq":1}\n{"seq":2}\n{"seq":3}\n', "sess1-r", 0)
    post("/records", '{"seq":4}\n', "sess1-r", 3)
    check("batch and grown retry land once", [r["seq"] for r in records()], [1, 2, 3, 4])

    # A post without the headers cannot be deduped, so it is written every time, unchanged.
    before = len(records())
    post("/records", '{"kind":"old","n":7,"nested":{"a":[1,"é"]}}\n')
    post("/records", '{"kind":"old","n":7,"nested":{"a":[1,"é"]}}\n')
    old = records()[before:]
    check("old-style post lands verbatim", [strip(r) for r in old],
          [{"kind": "old", "n": 7, "nested": {"a": [1, "é"]}}] * 2)

    # client is added from ?client=, and a client already on the record is kept.
    before = len(records())
    post("/records", '{"kind":"a"}\n{"kind":"b","client":"elsewhere"}\n', "sess2-r", 0)
    two = records()[before:]
    check("client is added", two[0].get("client"), "testbox")
    check("existing client is kept", two[1].get("client"), "elsewhere")
    check("rx is added", all(isinstance(r.get("rx"), str) and r["rx"][:4].isdigit() for r in two), True)

    # A line that is not a JSON object is kept whole, marked invalid.
    before = len(records())
    post("/records", 'not json\n[1,2]\n', "sess3-r", 0)
    bad = records()[before:]
    check("non-JSON line becomes invalid", [(r.get("kind"), r.get("raw"), r.get("client")) for r in bad],
          [("invalid", "not json", "testbox"), ("invalid", "[1,2]", "testbox")])

    # /log is untouched by records, and records never reach live.log.
    before = len(records())
    post("/log", "plain line\n", "sess4", 0)
    time.sleep(0.2)
    check("/log post adds no record", len(records()), before)
    log_lines = LOG.read_text(encoding="utf-8").splitlines()
    check("/log post reaches live.log", log_lines, ["testbox plain line"])

    # GET /records serves the tail as JSON lines.
    tail = [json.loads(l) for l in get("/records?n=2").splitlines()]
    check("GET /records?n=2 is the tail", tail, records()[-2:])
finally:
    srv.send_signal(signal.SIGINT)
    srv.wait(timeout=5)

print("PASS" if not failures else f"FAIL ({', '.join(failures)})")
sys.exit(0 if not failures else 1)
