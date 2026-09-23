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
import urllib.error
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
    try:
        with urllib.request.urlopen(req, timeout=5) as r:
            return r.status
    except urllib.error.HTTPError as e:
        return e.code
    except OSError:
        # The server dropped the connection without answering.
        return None


def get(path):
    with urllib.request.urlopen(f"{BASE}{path}", timeout=5) as r:
        return r.read().decode()


def strict_loads(line):
    """json.loads that refuses NaN and Infinity, which are not JSON and break jq."""
    def refuse(name):
        raise ValueError(f"non-JSON constant {name}")
    return json.loads(line, parse_constant=refuse)


def records():
    # Split on "\n" only: a stored string may hold U+2028 unescaped, which str.splitlines would cut.
    text = RECORDS.read_bytes().decode("utf-8") if RECORDS.exists() else ""
    return [strict_loads(l) for l in text.split("\n")[:-1]]


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

    # A line split only on "\n": U+2028 and U+0085 inside a string stay in the record, and the
    # next batch's offset lines up.
    before = len(records())
    post("/records", '{"s":"a\u2028b\u0085c"}\n{"n":2}\n', "sess5-r", 0)
    post("/records", '{"n":3}\n', "sess5-r", 2)
    check("records split on \\n only", [strip(r) for r in records()[before:]],
          [{"s": "a\u2028b\u0085c"}, {"n": 2}, {"n": 3}])

    # Nesting past Python's recursion limit and NaN/Infinity are kept as invalid, and the file stays
    # strict JSON.
    before = len(records())
    deep = "[" * 100_000 + "]" * 100_000
    status = post("/records", f'{deep}\n{{"x":NaN}}\n{{"y":-Infinity}}\n', "sess6-r", 0)
    check("deep and NaN lines are answered", status, 204)
    bad = records()[before:]
    check("deep and NaN lines become invalid", [(r.get("kind"), len(r.get("raw", ""))) for r in bad],
          [("invalid", len(deep)), ("invalid", len('{"x":NaN}')), ("invalid", len('{"y":-Infinity}'))])

    # A failed write must not move the offset: the retry of the whole batch still lands.
    before = len(records())
    RECORDS.chmod(0o444)
    try:
        status = post("/records", '{"k":1}\n{"k":2}\n', "sess7-r", 0)
    finally:
        RECORDS.chmod(0o644)
    check("failed write answers 500", status, 500)
    post("/records", '{"k":1}\n{"k":2}\n{"k":3}\n', "sess7-r", 0)
    check("failed write keeps the offset", [r.get("k") for r in records()[before:]], [1, 2, 3])

    # GET /records serves the tail as JSON lines.
    tail = [strict_loads(l) for l in get("/records?n=2").split("\n")[:-1]]
    check("GET /records?n=2 is the tail", tail, records()[-2:])

    # An append caught halfway: the partial last line is left out until its newline arrives.
    whole = get("/records?n=0")
    with open(RECORDS, "a", encoding="utf-8") as f:
        f.write('{"seq":99,"kind":"hal')
    check("GET /records leaves out a partial last line", get("/records?n=0") == whole, True)
    check("GET /records?n=1 leaves out a partial last line", get("/records?n=1"), whole.split("\n")[-2] + "\n")
finally:
    srv.send_signal(signal.SIGINT)
    srv.wait(timeout=5)

print("PASS" if not failures else f"FAIL ({', '.join(failures)})")
sys.exit(0 if not failures else 1)
