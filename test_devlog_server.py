"""Checks that a re-sent batch does not land in the log twice.

    python3 test_devlog_server.py

DevTelemetry retries a batch whose fate it could not read, and the server appends before it
answers, so the two agree on a line stream: the client says where its batch starts, the server
writes only the part past what it already holds. Every case below is one the retry path produces.
"""
import os
import pathlib
import signal
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

LOG = pathlib.Path(tempfile.mkdtemp(prefix="devlog-test-")) / "test.log"
env = dict(os.environ, PORT="9901", LOGFILE=str(LOG), DUMPDIR=str(LOG.parent / "dumps"))
srv = subprocess.Popen([sys.executable, str(pathlib.Path(__file__).with_name("devlog_server.py"))],
                       env=env, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
time.sleep(1.0)


def post(body, session=None, offset=None):
    req = urllib.request.Request("http://127.0.0.1:9901/log?client=testbox",
                                 data=body.encode(), method="POST")
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


def logged():
    # "\n" only: a stored line may hold U+2028 or "\x0b", which str.splitlines would cut.
    # Bytes, not read_text, whose universal newlines would turn "\r" into a line break.
    text = LOG.read_bytes().decode("utf-8") if LOG.exists() else ""
    return [l.split(" ", 1)[1] for l in text.split("\n")[:-1]]


failures = []


def check(name, got, want):
    ok = got == want
    print(f"{'ok  ' if ok else 'FAIL'} {name}: got {got!r}" + ("" if ok else f", want {want!r}"))
    if not ok:
        failures.append(name)


try:
    post("A\nB\nC\n", "sess1", 0)          # first batch lands
    post("A\nB\nC\nD\n", "sess1", 0)       # retry of the same batch, grown by one line
    post("A\nB\nC\nD\n", "sess1", 0)       # retry again, unchanged
    post("E\n", "sess1", 4)                # next batch, offset advanced past the four
    post("X\nY\n")                         # a client too old to send the headers
    post("X\nY\n")                         # ... and its retry, which cannot be deduped
    post("A\nB\n", "sess2", 0)             # a second session is tracked separately
    time.sleep(0.3)
    check("retries land once", logged(), ["A", "B", "C", "D", "E", "X", "Y", "X", "Y", "A", "B"])

    # A line may hold any character but "\n"; DevTelemetry counts one position per "\n", and so must
    # the server, or the next batch's offset skips or repeats lines.
    before = len(logged())
    odd = "a\u2028b\x0bc\x0cd\x1ce\x85f\u2029g\rh"
    post(f"{odd}\ni\r\n", "sess3", 0)
    post("j\n", "sess3", 2)
    post("j\n", "sess3", 2)
    check("split on \\n only", logged()[before:], [odd, "i", "j"])

    # A failed write must not move the offset: the retry of the whole batch still lands.
    before = len(logged())
    LOG.chmod(0o444)
    try:
        status = post("K\nL\n", "sess4", 0)
    finally:
        LOG.chmod(0o644)
    check("failed write answers 500", status, 500)
    post("K\nL\nM\n", "sess4", 0)
    check("failed write keeps the offset", logged()[before:], ["K", "L", "M"])

    # Eviction is by recency and the cap is 256: "busy" is the first stream opened but is used again
    # with 127 newer streams after that use, so it survives only in a recency order with room for
    # them; "idle" is never used again and is the first to go once 258 streams are tracked.
    before = len(logged())
    post("keep\n", "busy", 0)
    post("idle\n", "idle", 0)
    for i in range(256):
        post(f"n{i}\n", f"filler{i}", 0)
        if i == 128:
            post("keep\n", "busy", 0)      # a retry, deduped, which counts as use
    post("keep\n", "busy", 0)
    post("idle\n", "idle", 0)
    lines = logged()[before:]
    check("a stream in use is kept", lines.count("keep"), 1)
    check("the stream idle longest is evicted", lines.count("idle"), 2)
finally:
    srv.send_signal(signal.SIGINT)
    srv.wait(timeout=5)

print("PASS" if not failures else f"FAIL ({', '.join(failures)})")
sys.exit(0 if not failures else 1)
