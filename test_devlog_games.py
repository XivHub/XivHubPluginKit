"""Checks the /games endpoint: naming, atomic storage, idempotent retries, and never replacing a file.

    python3 test_devlog_games.py

MahjongAdvisor's GameUploader POSTs one match file and marks it sent only when the answer is the
SHA-256 of what it sent, so every case below checks the stored bytes and the echoed hash together.
"""
import hashlib
import os
import pathlib
import signal
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

TMP = pathlib.Path(tempfile.mkdtemp(prefix="devlog-games-test-"))
GAMES = TMP / "incoming"
BASE = "http://127.0.0.1:9903"
env = dict(os.environ, PORT="9903", LOGFILE=str(TMP / "live.log"), DUMPDIR=str(TMP / "dumps"),
           GAMESDIR=str(GAMES), MAX_GAME_BYTES="4096")
srv = subprocess.Popen([sys.executable, str(pathlib.Path(__file__).with_name("devlog_server.py"))],
                       env=env, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
time.sleep(1.0)


def post(body, name=None):
    req = urllib.request.Request(f"{BASE}/games", data=body, method="POST")
    if name is not None:
        req.add_header("X-Filename", name)
    try:
        with urllib.request.urlopen(req, timeout=5) as r:
            return r.status, r.read().decode().strip()
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode().strip()


def sha(body):
    return hashlib.sha256(body).hexdigest()


def stored():
    return sorted(p.name for p in GAMES.iterdir()) if GAMES.exists() else []


failures = []


def check(name, got, want):
    ok = got == want
    print(f"{'ok  ' if ok else 'FAIL'} {name}: got {got!r}" + ("" if ok else f", want {want!r}"))
    if not ok:
        failures.append(name)


try:
    first = b'{"kind":"emj.mode"}\n{"kind":"emj.msg"}\n'
    name = "2026-09-26-070509-a1b2c3d4.jsonl"
    check("stores and echoes the hash", post(first, name), (200, sha(first)))
    check("stored bytes", (GAMES / name).read_bytes(), first)

    # The uploader retries a file whose answer it could not read; the same bytes must not land twice.
    check("identical retry echoes the hash", post(first, name), (200, sha(first)))
    check("identical retry stores nothing new", stored(), [name])

    # A different file under a taken name is kept beside it, never over it.
    other = b'{"kind":"emj.mode","dutyId":767}\n'
    check("different content takes a suffix", post(other, name), (200, sha(other)))
    check("original untouched", (GAMES / name).read_bytes(), first)
    check("suffixed copy", (GAMES / "2026-09-26-070509-a1b2c3d4-2.jsonl").read_bytes(), other)
    check("suffixed retry is idempotent", post(other, name), (200, sha(other)))
    check("no temp files left", [n for n in stored() if n.startswith(".")], [])
    check("two files so far", len(stored()), 2)

    # The name comes from an unauthenticated socket: it is reduced to one harmless stem.
    evil = b'{"kind":"emj.msg"}\n'
    check("hostile name stored", post(evil, "../../etc/passwd"), (200, sha(evil)))
    check("hostile name stays inside", "etc-passwd.jsonl" in stored(), True)
    check("missing name gets a default", post(b"x\n"), (200, sha(b"x\n")))
    check("default name", "dump.jsonl" in stored(), True)

    check("empty body refused", post(b"", name)[0], 400)
    check("oversize refused", post(b"x" * 5000, "big.jsonl")[0], 413)
    check("oversize not stored", "big.jsonl" in stored(), False)

    with urllib.request.urlopen(f"{BASE}/health", timeout=5) as r:
        check("health still answers", r.status, 200)
finally:
    srv.send_signal(signal.SIGINT)
    srv.wait(timeout=5)

print("PASS" if not failures else f"FAIL ({', '.join(failures)})")
sys.exit(0 if not failures else 1)
