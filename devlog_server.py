#!/usr/bin/env python3
"""Mini live-log server for Zhyra Dalamud plugins (local dev only).

Plugins POST newline-delimited log lines (via ZhyraPluginKit.DevTelemetry); this appends them to a
file and echoes them to stdout so you (or an AI assistant on this box) can read plugin behaviour in
real time. Binds the LAN so the game client on another machine can reach it.

  python3 devlog_server.py                 # 0.0.0.0:9999, logs -> ~/.cache/zhyra-devlog/live.log
  PORT=9000 LOGFILE=/tmp/x.log python3 devlog_server.py

Point the plugin's dev-log URL at  http://<this-box-LAN-ip>:<port>/log
Read it back in a browser at the same address (?n=200 for the last 200 lines).
NOT for public exposure — no auth, plain HTTP, local network only.
"""
import http.server
import os
import pathlib
import socketserver
import sys
import urllib.parse

PORT = int(os.environ.get("PORT", "9999"))
LOGFILE = pathlib.Path(os.environ.get("LOGFILE", pathlib.Path.home() / ".cache" / "zhyra-devlog" / "live.log"))
LOGFILE.parent.mkdir(parents=True, exist_ok=True)
# Bound disk use: rotate live.log -> live.log.1 past MAX_BYTES (keeps one old file). Generous default.
MAX_BYTES = int(os.environ.get("MAX_BYTES", str(25 * 1024 * 1024)))


def _rotate_if_needed():
    try:
        if LOGFILE.exists() and LOGFILE.stat().st_size >= MAX_BYTES:
            LOGFILE.replace(LOGFILE.with_suffix(LOGFILE.suffix + ".1"))
    except OSError:
        pass


class Handler(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(length).decode("utf-8", "replace")
        if not body.endswith("\n"):
            body += "\n"
        _rotate_if_needed()
        with open(LOGFILE, "a", encoding="utf-8") as f:
            f.write(body)
        sys.stdout.write(body)
        sys.stdout.flush()
        self.send_response(204)
        self.end_headers()

    def do_GET(self):
        """Serve the tail of the log, so it can be read from a browser.

        Only the POST side of this server was ever implemented, so every GET
        answered "ok" and the log was readable only by opening the file on the
        box it lands on. `?n=` sets how many lines (default 500, 0 = all).
        """
        path, _, query = self.path.partition("?")
        if path.rstrip("/") == "/health":
            self._text(200, "zhyra-devlog ok\n")
            return

        params = urllib.parse.parse_qs(query)
        try:
            n = int(params.get("n", ["500"])[0])
        except ValueError:
            n = 500

        try:
            with open(LOGFILE, encoding="utf-8", errors="replace") as f:
                lines = f.readlines()
        except FileNotFoundError:
            self._text(200, "(no log yet)\n")
            return
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
        print(f"zhyra-devlog listening on 0.0.0.0:{PORT} -> {LOGFILE}", flush=True)
        try:
            srv.serve_forever()
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()
