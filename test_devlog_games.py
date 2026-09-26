"""Checks the /games endpoint: naming, atomic storage, idempotent retries, and never replacing a file.

    python3 test_devlog_games.py

MahjongAdvisor's GameUploader POSTs one match file and marks it sent only when the answer is the
SHA-256 of what it sent, so every case below checks the stored bytes and the echoed hash together.
"""
import gzip
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

    # A gzipped body is detected from its own magic bytes, not the header's extension, and stored
    # with a .jsonl.gz name whatever name the sender sent.
    gz = gzip.compress(first)
    gz_name = "2026-09-27-080000-b2c3d4e5.jsonl"
    check("gzipped body stores and echoes the hash", post(gz, gz_name), (200, sha(gz)))
    check("gzipped body kept extension .jsonl.gz", "2026-09-27-080000-b2c3d4e5.jsonl.gz" in stored(), True)
    check("gzipped body stored bytes are unchanged", (GAMES / "2026-09-27-080000-b2c3d4e5.jsonl.gz").read_bytes(), gz)
    check("no plain .jsonl for the gzipped upload", "2026-09-27-080000-b2c3d4e5.jsonl" in stored(), False)

    # A header that already names the .gz file is normalised the same way, not doubled.
    gz_name_already = "2026-09-27-090000-c3d4e5f6.jsonl.gz"
    check("header already .jsonl.gz", post(gz, gz_name_already), (200, sha(gz)))
    check("not doubled to .jsonl.gz.gz", "2026-09-27-090000-c3d4e5f6.jsonl.gz" in stored(), True)

    # A second, different gzipped upload under the same name takes a suffix with the .gz extension.
    gz2 = gzip.compress(other)
    check("different gzipped content takes a suffix", post(gz2, gz_name), (200, sha(gz2)))
    check("suffixed gz copy", (GAMES / "2026-09-27-080000-b2c3d4e5-2.jsonl.gz").read_bytes(), gz2)

    # A plain (non-gzip) body under a .jsonl.gz header name is stored plain, never mislabelled.
    plain_name_gz_header = "2026-09-27-100000-d4e5f607.jsonl.gz"
    check("plain body under a .gz header name", post(first, plain_name_gz_header), (200, sha(first)))
    check("stored as plain .jsonl", "2026-09-27-100000-d4e5f607.jsonl" in stored(), True)

    with urllib.request.urlopen(f"{BASE}/health", timeout=5) as r:
        check("health still answers", r.status, 200)
finally:
    srv.send_signal(signal.SIGINT)
    srv.wait(timeout=5)

print("PASS" if not failures else f"FAIL ({', '.join(failures)})")
sys.exit(0 if not failures else 1)
