#!/usr/bin/env python3
"""Mini live-log server for Zhyra Dalamud plugins (local dev only).

Plugins POST newline-delimited log lines (via ZhyraPluginKit.DevTelemetry); this appends them to a
file and echoes them to stdout so you (or an AI assistant on this box) can read plugin behaviour in
real time. Binds the LAN so the game client on another machine can reach it.

  python3 devlog_server.py                 # 0.0.0.0:9999, logs -> ~/.cache/zhyra-devlog/live.log
  PORT=9000 LOGFILE=/tmp/x.log python3 devlog_server.py

Point the plugin's dev-log URL at  http://<this-box-LAN-ip>:<port>/log
NOT for public exposure — no auth, plain HTTP, local network only.
"""
import http.server
import os
import pathlib
import socketserver
import sys

PORT = int(os.environ.get("PORT", "9999"))
LOGFILE = pathlib.Path(os.environ.get("LOGFILE", pathlib.Path.home() / ".cache" / "zhyra-devlog" / "live.log"))
LOGFILE.parent.mkdir(parents=True, exist_ok=True)


class Handler(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(length).decode("utf-8", "replace")
        if not body.endswith("\n"):
            body += "\n"
        with open(LOGFILE, "a", encoding="utf-8") as f:
            f.write(body)
        sys.stdout.write(body)
        sys.stdout.flush()
        self.send_response(204)
        self.end_headers()

    def do_GET(self):
        self.send_response(200)
        self.end_headers()
        self.wfile.write(b"zhyra-devlog ok\n")

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
