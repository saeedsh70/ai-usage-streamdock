"""End-to-end test: emulates the StreamDock host, Anthropic's and ChatGPT's usage endpoints and the
Antigravity language server (HTTPS, self-signed), then drives AIUsage.exe.

    python3 test/fake_host.py path/to/AIUsage.exe [output-folder-for-key-images]

Runs on Linux with Mono (see test/run-tests.sh) and on Windows. Exits non-zero on the first failed check.
"""
import asyncio, base64, hashlib, json, os, shutil, ssl, subprocess, sys, tempfile, threading, time
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import websockets

EXE = sys.argv[1]
OUT = sys.argv[2] if len(sys.argv) > 2 else None
WORK = tempfile.mkdtemp(prefix="ai-usage-test-")
HOME, CLAUDE, CODEX = (os.path.join(WORK, d) for d in ("home", "claude", "codex"))
for d in (HOME, CLAUDE, CODEX):
    os.makedirs(d)
ACTION = "com.saeedsh.aiusage.meter"
CSRF = "0f1e2d3c-aaaa-bbbb-cccc-123456789abc"

now = datetime.now(timezone.utc)
iso = lambda dt: dt.isoformat()
unix = lambda dt: int(dt.timestamp())
b64 = lambda o: base64.urlsafe_b64encode(json.dumps(o).encode()).decode().rstrip("=")
jwt = lambda claims: b64({"alg": "none"}) + "." + b64(claims) + ".sig"

CLAUDE_USAGE = {"limits": [
    {"kind": "session", "percent": 37, "resets_at": iso(now + timedelta(hours=2, minutes=14))},
    {"kind": "weekly_all", "percent": 71, "resets_at": iso(now + timedelta(days=3, hours=5))},
    {"kind": "weekly_scoped", "percent": 92, "resets_at": iso(now + timedelta(days=3)),
     "scope": {"model": {"id": None, "display_name": "Opus"}}},
], "extra_usage": {"is_enabled": True, "monthly_limit": 5000, "used_credits": 1234.0}}

CODEX_USAGE = {"plan_type": "plus",
               "rate_limit": {"primary_window": {"used_percent": 22, "limit_window_seconds": 18000,
                                                 "reset_at": unix(now + timedelta(hours=3))},
                              "secondary_window": {"used_percent": 64, "limit_window_seconds": 604800,
                                                   "reset_after_seconds": 200000}},
               "additional_rate_limits": [{"limit_name": "GPT-5-Codex-Mini",
                                           "rate_limit": {"primary_window": {"used_percent": 5, "limit_window_seconds": 18000}}}],
               "credits": {"has_credits": True, "unlimited": False, "balance": "480.5"}}

AGY_STATUS = {"userStatus": {"name": "x", "email": "x@example.com",
    "planStatus": {"planInfo": {"planName": "Google AI Pro", "monthlyPromptCredits": 500}, "availablePromptCredits": 480},
    "cascadeModelConfigData": {"clientModelConfigs": [
        {"label": "Gemini 3 Pro (High)", "quotaInfo": {"remainingFraction": 0.4, "resetTime": iso(now + timedelta(hours=4))}},
        {"label": "Claude Sonnet 4.5", "quotaInfo": {"remainingFraction": 1}},
        {"label": "No quota model"}]}}}
AGY_SUMMARY = {"response": {"groups": [
    {"displayName": "Gemini models", "buckets": [
        {"bucketId": "g5", "window": "5h", "remainingFraction": 0.08, "resetTime": iso(now + timedelta(hours=1))},
        {"bucketId": "gw", "window": "weekly", "remainingFraction": 0.5, "resetTime": iso(now + timedelta(days=4))}]},
    {"displayName": "Claude and GPT models", "buckets": [
        {"bucketId": "c5", "window": "5h", "remainingFraction": 0.75, "resetTime": iso(now + timedelta(hours=2))},
        {"bucketId": "cw", "window": "weekly", "disabled": True}]}]}}

hits = []

class Api(BaseHTTPRequestHandler):
    def log_message(self, *a): pass

    def reply(self, code, body):
        data = json.dumps(body).encode()
        self.send_response(code); self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data))); self.end_headers(); self.wfile.write(data)

    def do_GET(self):
        hits.append(("GET", self.path, dict(self.headers)))
        auth = self.headers.get("Authorization")
        if self.path == "/api/oauth/usage":
            if auth != "Bearer claude-token": return self.reply(401, {"error": "bad"})
            if self.headers.get("anthropic-beta") != "oauth-2025-04-20": return self.reply(400, {})
            return self.reply(200, CLAUDE_USAGE)
        if self.path == "/backend-api/codex/usage":
            return self.reply(404, {"detail": "not here"})  # exercises the fallback to wham/usage
        if self.path == "/backend-api/wham/usage":
            if auth != "Bearer " + CODEX_TOKEN or self.headers.get("ChatGPT-Account-Id") != "acct-1":
                return self.reply(401, {})
            return self.reply(200, CODEX_USAGE)
        self.reply(404, {})

    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        body = json.loads(self.rfile.read(length) or b"{}")
        hits.append(("POST", self.path, dict(self.headers)))
        if self.headers.get("X-Codeium-Csrf-Token") != CSRF: return self.reply(403, {})
        if self.path.endswith("/GetUserStatus") and body.get("metadata", {}).get("ideName") == "antigravity":
            return self.reply(200, AGY_STATUS)
        if self.path.endswith("/RetrieveUserQuotaSummary"):
            return self.reply(200, AGY_SUMMARY)
        self.reply(404, {})

def serve(server):
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server.server_port

api_port = serve(ThreadingHTTPServer(("127.0.0.1", 0), Api))

# Antigravity's language server speaks HTTPS with a self-signed certificate.
def self_signed(cert, key):
    from cryptography import x509
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import rsa
    from cryptography.x509.oid import NameOID
    k = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "localhost")])
    c = (x509.CertificateBuilder().subject_name(name).issuer_name(name).public_key(k.public_key())
         .serial_number(x509.random_serial_number()).not_valid_before(now - timedelta(days=1))
         .not_valid_after(now + timedelta(days=1)).sign(k, hashes.SHA256()))
    open(cert, "wb").write(c.public_bytes(serialization.Encoding.PEM))
    open(key, "wb").write(k.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.TraditionalOpenSSL,
                                          serialization.NoEncryption()))

cert, key = os.path.join(WORK, "c.pem"), os.path.join(WORK, "k.pem")
self_signed(cert, key)
tls = ThreadingHTTPServer(("127.0.0.1", 0), Api)
ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER); ctx.load_cert_chain(cert, key)
tls.socket = ctx.wrap_socket(tls.socket, server_side=True)
agy_port = serve(tls)

CODEX_TOKEN = jwt({"exp": int(time.time()) + 86400})
claude_creds = {"claudeAiOauth": {"accessToken": "claude-token", "refreshToken": "r", "expiresAt": (time.time() + 7200) * 1000,
                                  "rateLimitTier": "default_claude_max_5x"}}
json.dump(claude_creds, open(os.path.join(CLAUDE, ".credentials.json"), "w"))
json.dump({"tokens": {"access_token": CODEX_TOKEN, "account_id": "acct-1",
                      "id_token": jwt({"https://api.openai.com/auth": {"chatgpt_plan_type": "plus"}})}},
          open(os.path.join(CODEX, "auth.json"), "w"))
json.dump({"proxy": "none", "refreshMinutes": 5}, open(os.path.join(HOME, "config.json"), "w"))

def digest(path):
    return hashlib.sha256(open(path, "rb").read()).hexdigest()
before_claude, before_codex = digest(os.path.join(CLAUDE, ".credentials.json")), digest(os.path.join(CODEX, "auth.json"))

KEYS = {
    "c-5h": {"provider": "claude", "metric": "session"},
    "c-opus": {"provider": "claude", "metric": "model", "filter": "opus", "style": "ring"},
    "c-extra": {"provider": "claude", "metric": "extra"},
    "g-5h": {"provider": "chatgpt", "metric": "session"},
    "g-over": {"provider": "chatgpt", "metric": "overview"},
    "g-cred": {"provider": "chatgpt", "metric": "extra"},
    "a-gem": {"provider": "antigravity", "metric": "session", "filter": "gemini"},
    "a-model": {"provider": "antigravity", "metric": "model", "filter": "gemini 3"},
    "a-cred": {"provider": "antigravity", "metric": "extra"},
    "legacy": {"metric": "session"},   # settings saved by 0.x: no provider -> Claude
}
images, pi_msgs, other, failures = {}, [], [], []

def check(cond, what):
    print(("PASS  " if cond else "FAIL  ") + what)
    if not cond: failures.append(what)

def dump(tag):
    if not OUT: return
    os.makedirs(OUT, exist_ok=True)
    for ctx_, url in images.items():
        open(os.path.join(OUT, "%s_%s.png" % (tag, ctx_)), "wb").write(base64.b64decode(url.split(",", 1)[1]))

def state(provider):
    return pi_msgs[-1]["payload"]["providers"][provider] if pi_msgs else {}

def rows(provider):
    return [(l["name"], l["percent"]) for l in state(provider).get("limits", [])]

async def main():
    registered = asyncio.Event()
    conn = {}

    async def handler(ws):
        conn["ws"] = ws
        try:
            async for raw in ws:
                m = json.loads(raw)
                ev = m.get("event")
                if ev == "registerPlugin": registered.set()
                elif ev == "setImage": images[m["context"]] = m["payload"]["image"]
                elif ev == "sendToPropertyInspector": pi_msgs.append(m)
                else: other.append(m)
        except websockets.ConnectionClosed:
            pass

    async with websockets.serve(handler, "127.0.0.1", 0, max_size=None) as server:
        port = server.sockets[0].getsockname()[1]
        env = dict(os.environ, AI_USAGE_DECK_HOME=HOME, CLAUDE_CONFIG_DIR=CLAUDE, CODEX_HOME=CODEX,
                   AI_USAGE_DECK_ANTHROPIC_API="http://127.0.0.1:%d" % api_port,
                   AI_USAGE_DECK_CHATGPT="http://127.0.0.1:%d" % api_port,
                   AI_USAGE_DECK_AGY="%d|%s" % (agy_port, CSRF))
        for k in ("HTTPS_PROXY", "HTTP_PROXY", "https_proxy", "http_proxy", "ALL_PROXY", "all_proxy"):
            env.pop(k, None)
        cmd = ([] if os.name == "nt" else ["mono"]) + [EXE, "-port", str(port), "-pluginUUID", "plug-1",
                                                      "-registerEvent", "registerPlugin", "-info", "{}"]
        proc = subprocess.Popen(cmd, env=env)
        await asyncio.wait_for(registered.wait(), 20)
        ws = conn["ws"]
        send = lambda o: ws.send(json.dumps(o))
        for ctx_, s in KEYS.items():
            await send({"event": "willAppear", "action": ACTION, "context": ctx_, "payload": {"settings": s}})
        await send({"event": "propertyInspectorDidAppear", "action": ACTION, "context": "c-5h"})
        await asyncio.sleep(6)
        dump("1-all")

        check(set(images) == set(KEYS), "every key received an image")
        check(rows("claude") == [("Session", 37), ("Weekly", 71), ("Opus", 92)], "Claude limits parsed")
        check(state("claude").get("plan") == "max 5x", "Claude plan read from the login")
        ex = state("claude").get("extra", {})
        check(ex.get("value") == "$12.34" and ex.get("sub") == "of $50", "Claude extra usage converted from cents")

        check(rows("chatgpt") == [("Session", 22), ("Weekly", 64), ("GPT-5-Codex-Mini · 5-hour", 5)],
              "ChatGPT/Codex windows and extra limit parsed")
        check(state("chatgpt").get("plan") == "plus", "ChatGPT plan read")
        check(state("chatgpt").get("extra", {}).get("value") == "480", "Codex credits balance read")
        check(any(h[1] == "/backend-api/wham/usage" for h in hits), "falls back to wham/usage when codex/usage is missing")

        check(rows("antigravity") == [("Gemini · 5-hour", 92), ("Gemini · Weekly", 50),
                                      ("Claude and GPT · 5-hour", 25), ("Gemini 3 Pro (High)", 60),
                                      ("Claude Sonnet 4.5", 0)],
              "Antigravity quota groups and model quota parsed (disabled bucket skipped)")
        check(state("antigravity").get("plan") == "Google AI Pro", "Antigravity plan read")
        ex = state("antigravity").get("extra", {})
        check(ex.get("value") == "480" and ex.get("sub") == "of 500", "Antigravity prompt credits read")
        check(any(h[0] == "POST" and h[2].get("X-Codeium-Csrf-Token") == CSRF for h in hits),
              "language server called over HTTPS with the CSRF token")

        check(digest(os.path.join(CLAUDE, ".credentials.json")) == before_claude and
              digest(os.path.join(CODEX, "auth.json")) == before_codex, "login files never written")

        # An expired Claude login: reported, never renewed, file untouched.
        claude_creds["claudeAiOauth"]["expiresAt"] = 1000
        json.dump(claude_creds, open(os.path.join(CLAUDE, ".credentials.json"), "w"))
        expired_digest = digest(os.path.join(CLAUDE, ".credentials.json"))
        await send({"event": "sendToPlugin", "action": ACTION, "context": "pi", "payload": {"command": "refresh", "ctx": "c-5h"}})
        await asyncio.sleep(3)
        dump("2-expired")
        check(state("claude").get("error") == "Login expired" and state("claude").get("limits"),
              "expired Claude login reported, last numbers kept")
        check(not any(h[1].startswith("/v1/oauth/token") for h in hits) and
              digest(os.path.join(CLAUDE, ".credentials.json")) == expired_digest, "token not renewed, file untouched")

        # Switching a key's provider from the inspector redraws it.
        before = images.get("legacy")
        await send({"event": "sendToPlugin", "action": ACTION, "context": "pi",
                    "payload": {"command": "settings", "ctx": "legacy",
                                "settings": {"provider": "antigravity", "metric": "weekly", "filter": "gemini", "style": "number"}}})
        await asyncio.sleep(1.5)
        check(images.get("legacy") != before, "key redrawn after switching provider")

        # Shared settings saved.
        await send({"event": "sendToPlugin", "action": ACTION, "context": "pi",
                    "payload": {"command": "saveConfig", "ctx": "c-5h",
                                "config": {"refreshMinutes": 3, "proxy": "none", "claudeDir": CLAUDE, "codexDir": CODEX}}})
        await asyncio.sleep(2)
        cfg = json.load(open(os.path.join(HOME, "config.json")))
        check(cfg.get("refreshMinutes") == 3 and cfg.get("codexDir") == CODEX, "shared settings saved")
        check("sessionKey" not in json.dumps(cfg), "no secrets in the settings file")

        # Long press opens the provider's page.
        await send({"event": "keyDown", "context": "g-5h", "action": ACTION, "payload": {}})
        await asyncio.sleep(0.8)
        await send({"event": "keyUp", "context": "g-5h", "action": ACTION, "payload": {}})
        await asyncio.sleep(1)
        dump("3-final")
        check(any(o["event"] == "openUrl" and "chatgpt.com" in o["payload"]["url"] for o in other),
              "long press opens the provider's page")

        await ws.close()
        try:
            proc.wait(10)
            check(proc.returncode == 0, "plugin exits cleanly when the host disconnects")
        except subprocess.TimeoutExpired:
            proc.kill(); check(False, "plugin exits when the host disconnects")

asyncio.run(main())
log = open(os.path.join(HOME, "plugin.log"), encoding="utf-8-sig").read()
check("claude-token" not in log and CODEX_TOKEN not in log and CSRF not in log, "no tokens in the log")

shutil.rmtree(WORK, ignore_errors=True)
print("\n%d check(s) failed" % len(failures) if failures else "\nall checks passed")
sys.exit(1 if failures else 0)
