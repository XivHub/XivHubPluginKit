#!/usr/bin/env python3
"""Mini live-log server for Zhyra Dalamud plugins (local dev only).

Plugins POST newline-delimited log lines (via XivHubPluginKit.DevTelemetry); this appends them to a
file and echoes them to stdout so you (or an AI assistant on this box) can read plugin behaviour in
real time. Binds the LAN so the game client on another machine can reach it.

  python3 devlog_server.py                 # 0.0.0.0:9999, logs -> ~/.cache/zhyra-devlog/live.log
  PORT=9000 LOGFILE=/tmp/x.log python3 devlog_server.py

Point the plugin's dev-log URL at  http://<this-box-LAN-ip>:<port>/log
Read it back in a browser at the same address (?n=200 for the last 200 lines).

Endpoints:
  POST /log      plain lines -> LOGFILE, each prefixed with the sender's label
  POST /records  JSON lines (DevTelemetry.Record) -> RECORDSFILE (default records.jsonl beside
                 LOGFILE), one compact object per line; "client" and "rx" (receive time) are added
                 when absent, and a line that is not a JSON object is kept as kind "invalid"
  POST /file     one whole artefact -> DUMPDIR
  GET  /log, /   tail of LOGFILE (?n=, default 500, 0 = all)
  GET  /records  tail of RECORDSFILE (?n=, default 500, 0 = all)
  GET  /setup/<name>  SETUPDIR/<name>.json as is (default ~/.cache/zhyra-devlog/setup/), 404 if
                 absent: a capture setup a plugin loads instead of the owner configuring it by hand
  GET  /health

/log and /records split a body on "\n" only, dedupe retried batches by X-Devlog-Session and
X-Devlog-Offset, and both files rotate to <name>.1 past MAX_BYTES.
NOT for public exposure — no auth, plain HTTP, local network only.
"""
import datetime
import http.server
import json
import os
import pathlib
import re
import socket
import socketserver
import sys
import threading
import urllib.parse

PORT = int(os.environ.get("PORT", "9999"))
LOGFILE = pathlib.Path(os.environ.get("LOGFILE", pathlib.Path.home() / ".cache" / "zhyra-devlog" / "live.log"))
LOGFILE.parent.mkdir(parents=True, exist_ok=True)
# Bound disk use: rotate live.log -> live.log.1 past MAX_BYTES (keeps one old file). Generous default.
MAX_BYTES = int(os.environ.get("MAX_BYTES", str(25 * 1024 * 1024)))
# Structured records (JSON lines), kept apart from live.log so jq can read the file whole.
RECORDSFILE = pathlib.Path(os.environ.get("RECORDSFILE", LOGFILE.parent / "records.jsonl"))
SETUPDIR = pathlib.Path(os.environ.get("SETUPDIR", LOGFILE.parent / "setup"))
SETUP_NAME = re.compile(r"^[A-Za-z0-9_-]{1,64}$")
# Whole artefacts (addon dumps, captures) land here as discrete files rather than as log lines, so
# one dump stays one readable unit instead of interleaving with whatever else is logging.
DUMPDIR = pathlib.Path(os.environ.get("DUMPDIR", LOGFILE.parent / "dumps"))
MAX_DUMP_BYTES = int(os.environ.get("MAX_DUMP_BYTES", str(8 * 1024 * 1024)))


def _rotate_if_needed(path=LOGFILE):
    try:
        if path.exists() and path.stat().st_size >= MAX_BYTES:
            path.replace(path.with_suffix(path.suffix + ".1"))
    except OSError:
        pass


_client_names = {}

# How many lines of each client's stream are already on disk, keyed by the session id DevTelemetry
# sends with every batch. A batch is appended before the response goes out and the connection is
# closed after it, so a client can score a stored batch as failed and re-send it; without this it
# would land again. Guarded by _write_lock together with the append it authorises, so a duplicate
# check and its write cannot interleave with another thread's. Insertion order is recency order
# (_commit_offset re-inserts on every use), so eviction drops the stream idle longest. Each
# DevTelemetry instance has two streams (<id> and <id>-r) and a plugin reload makes a new instance.
_stream_offsets = {}
_write_lock = threading.Lock()
MAX_TRACKED_STREAMS = 256


def _new_lines(session, offset, lines):
    """The tail of a batch that is not already on disk, and the stream offset once it is written.

    offset is where this batch starts in the session's line stream. The server has written up to
    some point in that stream; anything at or before it arrived on an earlier attempt. A client too
    old to send the pair (offset < 0) is taken at its word: everything is written and the offset is
    None. Nothing is recorded here; the caller commits the offset with _commit_offset only after
    the write succeeds, so a failed write leaves the lines for the client's retry.
    """
    if not session or offset < 0:
        return lines, None
    written = _stream_offsets.get(session, offset)
    return lines[min(max(written - offset, 0), len(lines)):], max(written, offset + len(lines))


def _commit_offset(session, new_offset):
    """Record that the session's stream is on disk up to new_offset, and mark it most recently used."""
    if new_offset is None:
        return
    _stream_offsets.pop(session, None)
    _stream_offsets[session] = new_offset
    while len(_stream_offsets) > MAX_TRACKED_STREAMS:
        del _stream_offsets[next(iter(_stream_offsets))]


def _record_line(line, client, rx):
    """The stored form of one received line: one compact JSON object, without the newline.

    A JSON object keeps every key it arrived with; client and rx fill in only when absent, so a
    record forwarded from another server keeps its original sender and time. Anything else is kept
    whole under "raw" rather than dropped, since a malformed line is itself a bug worth seeing. That
    includes nesting too deep for the parser (RecursionError) and NaN or Infinity, which json.loads
    accepts but which are not JSON, so writing them back would break every strict reader of the file.
    """
    try:
        rec = json.loads(line)
        if isinstance(rec, dict):
            rec.setdefault("client", client)
            rec.setdefault("rx", rx)
            return json.dumps(rec, ensure_ascii=False, allow_nan=False, separators=(",", ":"))
    except (ValueError, RecursionError):
        pass
    return json.dumps({"kind": "invalid", "client": client, "rx": rx, "raw": line},
                      ensure_ascii=False, separators=(",", ":"))


def _append(path, text, session, new_offset):
    """Append text to path, then commit the stream offset; the OSError, if the write failed.

    Caller holds _write_lock. The offset moves only once the write has succeeded: after a failed
    write the client re-sends from its old offset, and the lines it re-sends are still new here.
    """
    if text:
        try:
            _rotate_if_needed(path)
            with open(path, "a", encoding="utf-8") as f:
                f.write(text)
        except OSError as e:
            return e
    _commit_offset(session, new_offset)
    return None


def _split_lines(body):
    """Split a /log or /records body on "\n" only, dropping one trailing "\r" per line.

    DevTelemetry gives each "\n"-terminated line one stream position, and dedupe compares those
    positions, so the server must count lines the same way. str.splitlines also breaks on "\r",
    "\x0b", "\x0c", "\x1c"-"\x1e", U+0085, U+2028 and U+2029, which a log line or a JSON string may
    hold; splitting there would cut a line in two and shift every offset after it.
    """
    lines = body.split("\n")
    if lines and lines[-1] == "":
        lines.pop()
    return [line[:-1] if line.endswith("\r") else line for line in lines]


def _client_label(addr, override=None):
    """A short, stable name for whoever sent this, so two machines sharing the log stay apart.

    An explicit label wins; otherwise reverse DNS once per address, falling back to the address
    itself. Cached because a POST arrives every second and a failing lookup is slow.
    """
    if override:
        return _safe_stem(override)
    if addr not in _client_names:
        try:
            name = socket.gethostbyaddr(addr)[0].split(".")[0]
        except OSError:
            name = addr
        _client_names[addr] = _safe_stem(name)
    return _client_names[addr]


def _safe_stem(name):
    """Reduce a caller-supplied name to a single harmless filename stem.

    The name arrives over an unauthenticated LAN socket and is used to build a path, so anything
    outside this character class is dropped rather than escaped.
    """
    stem = re.sub(r"[^A-Za-z0-9._-]", "-", name or "").strip("-.") [:80]
    return stem or "dump"


class Handler(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        path, _, query = self.path.partition("?")
        if path.rstrip("/") == "/file":
            return self._save_file(query)
        if path.rstrip("/") == "/records":
            return self._save_records(query)
        length = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(length).decode("utf-8", "replace")
        params = urllib.parse.parse_qs(query)
        client = _client_label(self.client_address[0], params.get("client", [""])[0])
        session, offset = self._stream_position()

        with _write_lock:
            lines, new_offset = _new_lines(session, offset, _split_lines(body))
            # Every line carries its sender: several machines share one log, and a line that cannot
            # be attributed is worse than no line when two people are reproducing the same bug.
            body = "".join(f"{client} {line}\n" for line in lines)
            error = _append(LOGFILE, body, session, new_offset)
            if body and not error:
                sys.stdout.write(body)
                sys.stdout.flush()
        self._stored(error)

    def _stored(self, error):
        """Answer a /log or /records post: 204, or 500 so the client keeps the batch and retries."""
        if error:
            return self._text(500, f"write failed: {error}\n")
        self.send_response(204)
        self.end_headers()

    def _stream_position(self):
        """The batch's (session, offset) pair; offset -1 marks a client that sent none."""
        session = self.headers.get("X-Devlog-Session", "")
        try:
            offset = int(self.headers.get("X-Devlog-Offset", "-1"))
        except ValueError:
            offset = -1
        return session, offset

    def _save_records(self, query):
        """Append a batch of JSON lines to RECORDSFILE, skipping what a retry already delivered."""
        length = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(length).decode("utf-8", "replace")
        params = urllib.parse.parse_qs(query)
        client = _client_label(self.client_address[0], params.get("client", [""])[0])
        session, offset = self._stream_position()
        rx = datetime.datetime.now().astimezone().isoformat(timespec="milliseconds")

        with _write_lock:
            lines, new_offset = _new_lines(session, offset, _split_lines(body))
            out = "".join(_record_line(line, client, rx) + "\n" for line in lines)
            error = _append(RECORDSFILE, out, session, new_offset)
        self._stored(error)

    def _save_file(self, query):
        """Store one POSTed artefact under DUMPDIR and answer with the path it landed at."""
        length = int(self.headers.get("Content-Length", 0))
        if length > MAX_DUMP_BYTES:
            # Drain the body before answering. Replying with it unread makes the kernel reset the
            # connection while the client is still sending, and the client then sees a transport
            # error instead of this 413.
            remaining = length
            while remaining > 0:
                chunk = self.rfile.read(min(remaining, 1 << 16))
                if not chunk:
                    break
                remaining -= len(chunk)
            return self._text(413, f"too large: {length} > {MAX_DUMP_BYTES}\n")
        body = self.rfile.read(length)
        params = urllib.parse.parse_qs(query)
        stem = _safe_stem(params.get("name", [""])[0])
        ext = _safe_stem(params.get("ext", ["txt"])[0]) or "txt"
        stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
        # Several plugins share this server, exactly as they share live.log, where DevTelemetry
        # tags every line with its source. Carry the same attribution into the filename.
        plugin = params.get("plugin", [""])[0]
        client = _client_label(self.client_address[0], params.get("client", [""])[0])
        prefix = "-".join(x for x in (stamp, client, _safe_stem(plugin) if plugin else "") if x)
        DUMPDIR.mkdir(parents=True, exist_ok=True)
        target = DUMPDIR / f"{prefix}-{stem}.{ext}"
        target.write_bytes(body)
        line = f"[devlog] saved {target} ({len(body)} bytes)\n"
        sys.stdout.write(line)
        sys.stdout.flush()
        self._text(200, str(target) + "\n")

    def do_GET(self):
        """Serve the tail of the log, or of the records at /records, so either can be read from a
        browser or piped to jq. `?n=` sets how many lines (default 500, 0 = all).
        """
        path, _, query = self.path.partition("?")
        if path.rstrip("/") == "/health":
            self._text(200, "zhyra-devlog ok\n")
            return
        if path.startswith("/setup/"):
            # The name is matched whole, so it can't climb out of SETUPDIR.
            name = path[len("/setup/"):].rstrip("/")
            if not SETUP_NAME.match(name):
                self._text(400, "setup name: letters, digits, - and _ only\n")
                return
            try:
                body = (SETUPDIR / f"{name}.json").read_text(encoding="utf-8")
            except FileNotFoundError:
                self._text(404, f"no setup {name}\n")
                return
            self._text(200, body)
            return
        records = path.rstrip("/") == "/records"

        params = urllib.parse.parse_qs(query)
        try:
            n = int(params.get("n", ["500"])[0])
        except ValueError:
            n = 500

        try:
            with open(RECORDSFILE if records else LOGFILE, encoding="utf-8", errors="replace") as f:
                lines = f.readlines()
        except FileNotFoundError:
            # An empty body, not a placeholder line, so `curl .../records | jq` stays valid.
            self._text(200, "" if records else "(no log yet)\n")
            return
        # This read does not take _write_lock, so it can catch an append halfway; a record without
        # its newline is not yet whole and would break jq.
        if records and lines and not lines[-1].endswith("\n"):
            lines.pop()
        self._text(200, "".join(lines[-n:] if n > 0 else lines))

    def _text(self, code, body):
        payload = body.encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "text/plain; charset=utf-8")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, *args):
        pass  # quiet; we print the payloads, not the access log


def main():
    socketserver.ThreadingTCPServer.allow_reuse_address = True
    with socketserver.ThreadingTCPServer(("0.0.0.0", PORT), Handler) as srv:
        print(f"zhyra-devlog listening on 0.0.0.0:{PORT} -> {LOGFILE}, {RECORDSFILE}", flush=True)
        try:
            srv.serve_forever()
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()
