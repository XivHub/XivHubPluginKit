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
    with urllib.request.urlopen(req, timeout=5) as r:
        return r.status

try:
    post("A\nB\nC\n", "sess1", 0)          # first batch lands
    post("A\nB\nC\nD\n", "sess1", 0)       # retry of the same batch, grown by one line
    post("A\nB\nC\nD\n", "sess1", 0)       # retry again, unchanged
    post("E\n", "sess1", 4)                # next batch, offset advanced past the four
    post("X\nY\n")                         # a client too old to send the headers
    post("X\nY\n")                         # ... and its retry, which cannot be deduped
    post("A\nB\n", "sess2", 0)             # a second session is tracked separately
    time.sleep(0.3)
finally:
    srv.send_signal(signal.SIGINT)
    srv.wait(timeout=5)

got = [l.split(" ", 1)[1] for l in LOG.read_text().splitlines()]
want = ["A", "B", "C", "D", "E", "X", "Y", "X", "Y", "A", "B"]
print("got :", got)
print("want:", want)
print("PASS" if got == want else "FAIL")
sys.exit(0 if got == want else 1)
