"""Valheim admin panel: live player count behind passkey (WebAuthn) login.

Runs on 127.0.0.1:8088 behind Caddy, which terminates TLS for
valheim.jurenka.software. Player state comes from tailing the `valheim`
unit's journal; nothing here talks to the game server directly. Host and
server metrics are sampled into SQLite by metrics.py.

To approve a passkey: sign in with it on the panel, copy the line it shows
into APPROVED below, then `systemctl restart valpanel`.
"""

import json
import os
import re
import secrets
import subprocess
import threading
from collections import deque
import time
from http import HTTPStatus
from http.cookies import SimpleCookie
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import metrics
import visitors
from webauthn import (
    generate_authentication_options,
    generate_registration_options,
    options_to_json,
    verify_authentication_response,
    verify_registration_response,
)
from webauthn.helpers import base64url_to_bytes, bytes_to_base64url
from webauthn.helpers.structs import (
    AuthenticatorSelectionCriteria,
    ResidentKeyRequirement,
    UserVerificationRequirement,
)

# --- Approved passkeys -------------------------------------------------------
# credential ID -> public key (both base64url). The key is pinned here, not
# looked up from disk, so nothing written at runtime can grant access.
APPROVED = {
    # "credential-id": "public-key",  # who / which device
    "pWRIZvEbsyKDtNelqZaPDVepHYUHOZILe5b-SRoFUu-tZTXFZvYwe71UXe90joC1": "pAEBAycgBiFYIKVkSGbxG7Mig7TXpalIEEAw1xcaUp-oGtChj9-tyz4F",  # mattj
}

RP_ID = "valheim.jurenka.software"
RP_NAME = "Valheim admin"
ORIGIN = f"https://{RP_ID}"

# Who may read the one public endpoint, /api/join, from a browser. Everything else on this
# panel is passkey-gated; the join code is the opposite of a secret - it is the thing players
# are given - and verseworlds.fun needs it to put on the page.
SITE_ORIGINS = ("https://verseworlds.fun", "https://www.verseworlds.fun")
LISTEN = ("127.0.0.1", 8088)
UNIT = "valheim"
STATIC = os.path.join(os.path.dirname(os.path.abspath(__file__)), "static")
REGISTRATIONS = "/var/lib/valpanel/registrations.json"
MAX_REGISTRATIONS = 500
SESSION_TTL = 12 * 3600
CHALLENGE_TTL = 300
MAX_BODY = 64 * 1024


# --- The join code ----------------------------------------------------------
#
# The crossplay join code is what players actually type to get in, and it is not ours to
# choose: PlayFab issues it when the server registers its session, and a restart can be handed
# a different one. So verseworlds.fun must not have it typed into the page - it would go stale
# silently, and a stale join code is a server nobody can reach.
#
# This is where it comes from instead. The panel already reads the journal, and the journal is
# where the game says what code it got: the answer is derived rather than stored, so there is
# nothing to keep in sync. The one public route on this panel, because the join code is the
# opposite of a secret.

JOIN_TTL = 30.0
_join = {"at": 0.0, "body": None}
_join_lock = threading.Lock()

# "Session "..." with join code 968470 and IP 1.2.3.4:2456 is active with 0 player(s)"
#
# Deliberately the *activation* line and not "registered with join code": a code that was
# registered but never activated is the exact shape of crossplay's worst failure - the server
# looks healthy and no discovery path works. See HANDOFF.md. If the only line present is a
# registration, the code is not usable yet and this says so by reporting none.
JOIN_ACTIVE = re.compile(r"with join code (\d+) and IP \S+ is active")


def join_info():
    """The code players need right now, and whether crossplay is even on."""
    now = time.time()

    with _join_lock:
        if _join["body"] is not None and now - _join["at"] < JOIN_TTL:
            return _join["body"]

    code = None
    crossplay = False
    running = False

    try:
        pid = subprocess.run(["systemctl", "show", "-p", "MainPID", "--value", UNIT],
                             capture_output=True, text=True, timeout=5).stdout.strip()

        if pid and pid != "0":
            running = True

            # The flag, from the process itself rather than from any config file: what the
            # server was actually started with is the only version that counts.
            with open(f"/proc/{pid}/cmdline", "rb") as f:
                crossplay = b"-crossplay" in f.read().split(b"\0")

            if crossplay:
                # This process only, so a code from a previous run can never be served as
                # though it were current.
                out = subprocess.run(
                    ["journalctl", "-u", UNIT, f"_PID={pid}", "-o", "cat", "--no-pager"],
                    capture_output=True, text=True, timeout=20).stdout

                for line in out.splitlines():
                    found = JOIN_ACTIVE.search(line)
                    if found:
                        code = found.group(1)       # the last one wins: codes can be reissued
    except Exception:
        # A panel that cannot read the journal must not take the site's join code with it;
        # the site has a fallback and would rather have "unknown" than an error.
        pass

    body = {"crossplay": crossplay, "code": code, "running": running, "checked": int(now)}

    with _join_lock:
        _join["at"] = now
        _join["body"] = body

    return body


# --- Player tracking from the journal ---------------------------------------

class LogBuffer:
    """Recent server output, kept in memory for the panel's console.

    Fed from the journal tail the player tracker already runs, so there is no second
    journalctl process and no second place for the two views to disagree. A sequence number
    per line is what the browser polls against; it only ever asks for what it has not seen.

    Deliberately not persisted: journald already has all of it, with better tools.
    """

    def __init__(self, keep=2000):
        self.keep = keep
        self.lock = threading.Lock()
        self.lines = deque(maxlen=keep)
        self.seq = 0

    def add(self, at, text):
        with self.lock:
            self.seq += 1
            self.lines.append({"seq": self.seq, "at": at, "text": text})

    def since(self, after, limit=400):
        """Lines newer than `after`. `dropped` is how many the caller missed entirely,
        which happens when a tab has been in the background while the server was noisy."""
        with self.lock:
            if not self.lines:
                return {"lines": [], "next": self.seq, "dropped": 0}

            oldest = self.lines[0]["seq"]
            dropped = max(0, oldest - after - 1) if after else 0
            fresh = [ln for ln in self.lines if ln["seq"] > after]

            if len(fresh) > limit:
                dropped += len(fresh) - limit
                fresh = fresh[-limit:]

            return {"lines": fresh, "next": self.seq, "dropped": dropped}


LOG = LogBuffer()


class PlayerTracker:
    """The live roster, from the journal tail.

    Who is connected comes from `visitors.Sessions`; this adds the server's own periodic
    count, the journal tail that feeds both, and the history table every event is also
    written to.
    """

    SNAPSHOT = re.compile(r"Connections (\d+) ZDOS:(\d+)")

    def __init__(self, history=None):
        self.lock = threading.Lock()
        self.history = history
        self.sessions = visitors.Sessions()
        self.snapshot = None  # {"count", "objects", "at"}
        threading.Thread(target=self._run, daemon=True).start()

    def _since(self):
        out = subprocess.run(
            ["systemctl", "show", UNIT, "-p", "ActiveEnterTimestamp", "--timestamp=unix", "--value"],
            capture_output=True, text=True,
        ).stdout.strip()
        return out if out.startswith("@") else f"@{int(time.time())}"

    def _run(self):
        while True:
            try:
                proc = subprocess.Popen(
                    ["journalctl", "-u", UNIT, "-o", "json", "--output-fields=MESSAGE,_PID",
                     "--since", self._since(), "-f", "--no-pager"],
                    stdout=subprocess.PIPE, text=True,
                )
                for line in proc.stdout:
                    try:
                        self._handle(json.loads(line))
                    except (ValueError, KeyError):
                        continue
                proc.wait()
            except OSError:
                pass
            time.sleep(5)

    def _handle(self, entry):
        msg = entry.get("MESSAGE")
        if isinstance(msg, list):
            msg = bytes(msg).decode("utf-8", "replace")
        if not msg:
            return
        at = int(entry["__REALTIME_TIMESTAMP"]) / 1e6

        # Everything goes to the console, including the plugin chatter the tracker below
        # deliberately ignores - the console's whole job is to show what the server said.
        LOG.add(at, msg)

        if "Hall-Patton]" in msg:
            return
        pid = entry.get("_PID")
        with self.lock:
            if pid != self.sessions.pid:
                self.snapshot = None  # the old run's count says nothing about the new one
            events = self.sessions.feed(msg, at, pid)
            if m := self.SNAPSHOT.search(msg):
                self.snapshot = {"count": int(m[1]), "objects": int(m[2]), "at": at}
        if self.history:
            self.history.record(events)

    def status(self):
        active = subprocess.run(["systemctl", "is-active", UNIT], capture_output=True, text=True).stdout.strip()
        with self.lock:
            players = [] if active != "active" else [
                {"steam_id": sid, "name": p["name"], "since": p["since"]}
                for sid, p in sorted(self.sessions.online.items(), key=lambda kv: kv[1]["since"])
            ]
            return {"server": active, "online": len(players), "players": players,
                    "snapshot": self.snapshot, "now": time.time()}


# --- Passkey state ------------------------------------------------------------

lock = threading.Lock()
challenges = {}  # flow id -> (kind, challenge bytes, expiry)
sessions = {}  # token -> {"cred_id", "approved", "expires"}
sign_counts = {}  # cred id -> last seen sign count (this process only)
hits = {}  # client ip -> [timestamps]


def load_registrations():
    try:
        with open(REGISTRATIONS) as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def save_registration(cred_id, public_key, label):
    with lock:
        regs = load_registrations()
        if cred_id in regs:
            return False
        if len(regs) >= MAX_REGISTRATIONS:
            raise RuntimeError("registration store is full")
        regs[cred_id] = {"public_key": public_key, "label": label, "created": int(time.time())}
        tmp = REGISTRATIONS + ".tmp"
        with open(tmp, "w") as f:
            json.dump(regs, f, indent=1)
        os.replace(tmp, REGISTRATIONS)
        return True


def new_flow(kind, challenge):
    now = time.time()
    with lock:
        for k in [k for k, v in challenges.items() if v[2] < now]:
            del challenges[k]
        if len(challenges) > 1000:
            raise RuntimeError("too many pending logins")
        flow = secrets.token_urlsafe(24)
        challenges[flow] = (kind, challenge, now + CHALLENGE_TTL)
        return flow


def take_flow(flow, kind):
    with lock:
        entry = challenges.pop(flow or "", None)
    if not entry or entry[0] != kind or entry[2] < time.time():
        raise ValueError("login attempt expired, try again")
    return entry[1]


def allowlist_line(cred_id, public_key, label):
    return f'    "{cred_id}": "{public_key}",  # {label or "?"}'


def normalize_id(raw):
    return bytes_to_base64url(base64url_to_bytes(raw))


# --- HTTP ---------------------------------------------------------------------

class Handler(BaseHTTPRequestHandler):
    server_version = "valpanel"
    sys_version = ""

    def log_message(self, fmt, *args):
        print(f"{self.client_ip()} {fmt % args}", flush=True)

    def client_ip(self):
        return self.headers.get("X-Forwarded-For", self.client_address[0]).split(",")[-1].strip()

    def send(self, status, body, ctype="application/json", cookie=None, extra=None):
        if not isinstance(body, bytes):
            body = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))

        # no-store unless the caller asks otherwise, which only /api/join does: everything
        # else here is somebody's live admin view and must not be held anywhere. Sending both
        # would be two conflicting Cache-Control headers, and the cacheable one would lose.
        extra = extra or {}
        self.send_header("Cache-Control", extra.get("Cache-Control", "no-store"))
        self.send_header("Content-Security-Policy",
                         "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; "
                         "img-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Referrer-Policy", "no-referrer")
        if cookie:
            self.send_header("Set-Cookie", cookie)
        for name, value in extra.items():
            if name == "Cache-Control":
                continue
            self.send_header(name, value)
        self.end_headers()
        self.wfile.write(body)

    def session(self):
        jar = SimpleCookie(self.headers.get("Cookie", ""))
        token = jar["__Host-sid"].value if "__Host-sid" in jar else None
        with lock:
            s = sessions.get(token)
            if s and s["expires"] < time.time():
                del sessions[token]
                s = None
        return token, s

    def do_GET(self):
        path = self.path.split("?")[0]
        if path in ("/", "/index.html"):
            return self.static("index.html", "text/html; charset=utf-8")
        if path == "/app.js":
            return self.static("app.js", "text/javascript; charset=utf-8")
        if path == "/app.css":
            return self.static("app.css", "text/css; charset=utf-8")
        if path == "/api/join":
            # The one route with no session check. It carries the join code and nothing else -
            # no player names, no host metrics, no console - and the join code is the thing
            # players are handed, so there is nothing here to protect. verseworlds.fun reads
            # it so that the code on the page is the code the server actually has.
            origin = self.headers.get("Origin")
            extra = {"Cache-Control": "public, max-age=30"}
            if origin in SITE_ORIGINS:
                extra["Access-Control-Allow-Origin"] = origin
            return self.send(200, join_info(), extra=extra)

        if path == "/api/me":
            _, s = self.session()
            if not s:
                return self.send(200, {"signed_in": False})
            return self.send(200, {"signed_in": True, "approved": s["approved"], "cred_id": s["cred_id"],
                                   "verified": s["verified"], "line": s["line"]})
        if path == "/api/status":
            _, s = self.session()
            if not s:
                return self.send(401, {"error": "sign in first"})
            if not s["approved"]:
                return self.send(403, {"error": "this passkey is not approved"})
            return self.send(200, tracker.status())
        if path == "/api/metrics":
            _, s = self.session()
            if not s or not s["approved"]:
                return self.send(403, {"error": "this passkey is not approved"})
            query = dict(p.split("=", 1) for p in self.path.partition("?")[2].split("&") if "=" in p)
            try:
                return self.send(200, metrics.query(query.get("range", "6h")))
            except ValueError as e:
                return self.send(400, {"error": str(e)})
        if path == "/api/visitors":
            _, s = self.session()
            if not s or not s["approved"]:
                return self.send(403, {"error": "this passkey is not approved"})
            query = dict(p.split("=", 1) for p in self.path.partition("?")[2].split("&") if "=" in p)
            try:
                page = int(query.get("page", 1))
                per = int(query.get("per", visitors.DEFAULT_PER_PAGE))
            except ValueError:
                return self.send(400, {"error": "bad page"})
            online = {p["steam_id"] for p in tracker.status()["players"]}
            try:
                return self.send(200, history.page(query.get("window", "24h"), page, per, online))
            except ValueError as e:
                return self.send(400, {"error": str(e)})
        if path == "/api/log":
            _, s = self.session()
            if not s or not s["approved"]:
                return self.send(403, {"error": "this passkey is not approved"})
            query = dict(p.split("=", 1) for p in self.path.partition("?")[2].split("&") if "=" in p)
            try:
                after = int(query.get("after", 0))
            except ValueError:
                return self.send(400, {"error": "bad cursor"})
            return self.send(200, LOG.since(after))
        self.send(404, {"error": "not found"})

    def static(self, name, ctype):
        with open(os.path.join(STATIC, name), "rb") as f:
            self.send(200, f.read(), ctype)

    def do_POST(self):
        if self.headers.get("Origin") != ORIGIN:
            return self.send(403, {"error": "bad origin"})
        if not self.rate_ok():
            return self.send(429, {"error": "slow down"})
        length = int(self.headers.get("Content-Length") or 0)
        if length > MAX_BODY or "application/json" not in self.headers.get("Content-Type", ""):
            return self.send(400, {"error": "bad request"})
        try:
            body = json.loads(self.rfile.read(length) or b"{}")
        except ValueError:
            return self.send(400, {"error": "bad json"})
        routes = {
            "/api/register/options": self.register_options,
            "/api/register/verify": self.register_verify,
            "/api/login/options": self.login_options,
            "/api/login/verify": self.login_verify,
            "/api/logout": self.logout,
        }
        route = routes.get(self.path)
        if not route:
            return self.send(404, {"error": "not found"})
        try:
            route(body)
        except (ValueError, RuntimeError) as e:
            self.send(400, {"error": str(e)})
        except Exception as e:  # webauthn raises a range of InvalidX errors
            self.log_message("verify failed: %r", e)
            self.send(400, {"error": "passkey check failed"})

    def rate_ok(self):
        ip, now = self.client_ip(), time.time()
        with lock:
            recent = [t for t in hits.get(ip, []) if t > now - 60]
            recent.append(now)
            hits[ip] = recent
            if len(hits) > 5000:
                hits.clear()
        return len(recent) <= 30

    def register_options(self, body):
        label = str(body.get("label") or "").strip()[:40] or "admin"
        opts = generate_registration_options(
            rp_id=RP_ID, rp_name=RP_NAME, user_name=label, user_display_name=label,
            user_id=secrets.token_bytes(16),
            authenticator_selection=AuthenticatorSelectionCriteria(
                resident_key=ResidentKeyRequirement.REQUIRED,
                user_verification=UserVerificationRequirement.REQUIRED,
            ),
        )
        flow = new_flow(("register", label), opts.challenge)
        self.send(200, {"flow": flow, "options": json.loads(options_to_json(opts))})

    def register_verify(self, body):
        flow = body.get("flow")
        with lock:
            kind = challenges.get(flow or "", (None,))[0]
        if not (isinstance(kind, tuple) and kind[0] == "register"):
            raise ValueError("login attempt expired, try again")
        challenge = take_flow(flow, kind)
        v = verify_registration_response(
            credential=body.get("credential"), expected_challenge=challenge,
            expected_rp_id=RP_ID, expected_origin=ORIGIN, require_user_verification=True,
        )
        cred_id = bytes_to_base64url(v.credential_id)
        public_key = bytes_to_base64url(v.credential_public_key)
        if not save_registration(cred_id, public_key, kind[1]):
            raise ValueError("that passkey is already registered")
        sign_counts[cred_id] = v.sign_count
        self.start_session(cred_id, public_key, kind[1])

    def login_options(self, body):
        opts = generate_authentication_options(rp_id=RP_ID, user_verification=UserVerificationRequirement.REQUIRED)
        flow = new_flow("login", opts.challenge)
        self.send(200, {"flow": flow, "options": json.loads(options_to_json(opts))})

    def login_verify(self, body):
        challenge = take_flow(body.get("flow"), "login")
        credential = body.get("credential") or {}
        cred_id = normalize_id(str(credential.get("rawId") or credential.get("id") or ""))
        reg = load_registrations().get(cred_id, {})
        public_key = APPROVED.get(cred_id) or reg.get("public_key")
        if not public_key:
            # No key on file, so the signature can't be checked. Show the ID
            # the browser reported, but never treat it as approved.
            return self.start_session(cred_id, None, None, verified=False)
        v = verify_authentication_response(
            credential=credential, expected_challenge=challenge, expected_rp_id=RP_ID,
            expected_origin=ORIGIN, credential_public_key=base64url_to_bytes(public_key),
            credential_current_sign_count=sign_counts.get(cred_id, 0), require_user_verification=True,
        )
        sign_counts[cred_id] = v.new_sign_count
        self.start_session(cred_id, public_key, reg.get("label"))

    def start_session(self, cred_id, public_key, label, verified=True):
        approved = verified and cred_id in APPROVED and APPROVED[cred_id] == public_key
        token = secrets.token_urlsafe(32)
        with lock:
            sessions[token] = {
                "cred_id": cred_id, "approved": approved, "verified": verified,
                "line": allowlist_line(cred_id, public_key, label) if public_key else None,
                "expires": time.time() + SESSION_TTL,
            }
        self.log_message("signed in %s approved=%s verified=%s", cred_id, approved, verified)
        cookie = f"__Host-sid={token}; Path=/; Secure; HttpOnly; SameSite=Strict; Max-Age={SESSION_TTL}"
        self.send(200, {"ok": True}, cookie=cookie)

    def logout(self, body):
        token, _ = self.session()
        with lock:
            sessions.pop(token, None)
        self.send(200, {"ok": True}, cookie="__Host-sid=; Path=/; Secure; HttpOnly; SameSite=Strict; Max-Age=0")


if __name__ == "__main__":
    history = visitors.History()
    tracker = PlayerTracker(history)
    metrics.Collector(tracker)
    print(f"valpanel listening on {LISTEN[0]}:{LISTEN[1]}, {len(APPROVED)} approved passkey(s)", flush=True)
    ThreadingHTTPServer(LISTEN, Handler).serve_forever()
