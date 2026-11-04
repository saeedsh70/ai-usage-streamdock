"""Runs the real property inspector (headless Chromium via Playwright) and the plugin against one
fake host that routes messages between them like StreamDock does, and saves a screenshot.

    pip install websockets playwright && python3 -m playwright install chromium
    python3 test/pi_test.py path/to/ClaudeUsage.exe inspector.png
"""
import asyncio, json, os, shutil, subprocess, sys, tempfile, threading
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import websockets
from playwright.async_api import async_playwright

EXE, SHOT = sys.argv[1], sys.argv[2]
ROOT = Path(__file__).resolve().parent.parent / "com.saeedsh.aiusage.sdPlugin"
WORK = tempfile.mkdtemp(prefix="ai-usage-pi-")
HOME, CLAUDE = os.path.join(WORK, "home"), os.path.join(WORK, "claude")
os.makedirs(HOME); os.makedirs(CLAUDE)

now = datetime.now(timezone.utc)
USAGE = {"limits": [
    {"kind": "session", "percent": 37, "resets_at": (now + timedelta(hours=2, minutes=14)).isoformat()},
    {"kind": "weekly_all", "percent": 71, "resets_at": (now + timedelta(days=3)).isoformat()},
    {"kind": "weekly_scoped", "percent": 92, "resets_at": (now + timedelta(days=3)).isoformat(),
     "scope": {"model": {"display_name": "Opus"}}}],
    "extra_usage": {"is_enabled": True, "monthly_limit": 5000, "used_credits": 1234}}

class Api(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def do_GET(self):
        ok = self.path == "/api/oauth/usage" and self.headers.get("Authorization") == "Bearer tok"
        data = json.dumps(USAGE if ok else {"error": 1}).encode()
        self.send_response(200 if ok else 401); self.send_header("Content-Length", str(len(data))); self.end_headers()
        self.wfile.write(data)

api = ThreadingHTTPServer(("127.0.0.1", 0), Api)
threading.Thread(target=api.serve_forever, daemon=True).start()
json.dump({"claudeAiOauth": {"accessToken": "tok", "refreshToken": "r", "expiresAt": 9e15,
                             "rateLimitTier": "default_claude_max_20x"}}, open(CLAUDE + "/.credentials.json", "w"))
json.dump({"proxy": "none"}, open(HOME + "/config.json", "w"))

CTX = "ctx-key-1"
log = []

async def main():
    plugin = {}; pi = {}
    plugin_ready = asyncio.Event(); pi_ready = asyncio.Event()

    async def handler(ws):
        async for raw in ws:
            m = json.loads(raw); ev = m.get("event")
            log.append(ev)
            if ev == "registerPlugin": plugin["ws"] = ws; plugin_ready.set()
            elif ev == "registerPropertyInspector": pi["ws"] = ws; pi_ready.set()
            elif ev == "sendToPlugin":
                m["context"] = CTX
                await plugin["ws"].send(json.dumps(m))
            elif ev == "setSettings":
                log.append("settings=" + json.dumps(m["payload"], ensure_ascii=False))
                await plugin["ws"].send(json.dumps({"event": "didReceiveSettings", "action": m.get("action"),
                                                    "context": CTX, "payload": {"settings": m["payload"]}}))
            elif ev == "sendToPropertyInspector" and "ws" in pi:
                await pi["ws"].send(json.dumps(m))

    async with websockets.serve(handler, "127.0.0.1", 0, max_size=None) as server:
        port = server.sockets[0].getsockname()[1]
        env = dict(os.environ, AI_USAGE_DECK_HOME=HOME, CLAUDE_CONFIG_DIR=CLAUDE, CODEX_HOME=os.path.join(WORK, "nocodex"),
                   AI_USAGE_DECK_ANTHROPIC_API="http://127.0.0.1:%d" % api.server_port)
        for k in ("HTTPS_PROXY", "HTTP_PROXY", "https_proxy", "http_proxy"): env.pop(k, None)
        proc = subprocess.Popen(([] if os.name == "nt" else ["mono"]) + [EXE, "-port", str(port), "-pluginUUID", "p", "-registerEvent", "registerPlugin", "-info", "{}"], env=env)
        await asyncio.wait_for(plugin_ready.wait(), 15)
        await plugin["ws"].send(json.dumps({"event": "willAppear", "action": "com.saeedsh.aiusage.meter", "context": CTX,
                                            "payload": {"settings": {"provider": "claude", "metric": "model", "filter": "opus"}}}))
        await asyncio.sleep(2)
        await plugin["ws"].send(json.dumps({"event": "propertyInspectorDidAppear", "action": "com.saeedsh.aiusage.meter", "context": CTX}))

        async with async_playwright() as p:
            b = await p.chromium.launch()
            page = await b.new_page(viewport={"width": 380, "height": 900}, device_scale_factor=1)
            errors = []
            page.on("pageerror", lambda e: errors.append(str(e)))
            await page.goto((ROOT / "pi.html").as_uri())
            info = json.dumps({"action": "com.saeedsh.aiusage.meter", "context": CTX,
                               "payload": {"settings": {"provider": "claude", "metric": "model", "filter": "opus"}}})
            await page.evaluate("([port, info]) => connectElgatoStreamDeckSocket(port, 'pi-uuid', 'registerPropertyInspector', '{}', info)", [port, info])
            await asyncio.wait_for(pi_ready.wait(), 10)
            await asyncio.sleep(2)
            await page.select_option("#style", "bar")
            await page.fill("#proxy", "")
            await page.click("#save")
            await asyncio.sleep(2.5)
            await page.screenshot(path=SHOT, full_page=True)
            status = await page.inner_text("#status")
            print("status:", status)
            ok = not errors and "Claude" in status and "Opus" in status and "max 20x" in status
            claude_models = await page.eval_on_selector_all("#filterSelect option", "os => os.map(o => o.textContent)")
            print("claude model list:", claude_models)
            ok = ok and "Opus" in claude_models

            # Antigravity: the model list comes from the provider's data. Inject a state like the
            # plugin sends while Antigravity runs, then pick a model from the dropdown.
            agy = {"name": "Antigravity", "where": "app", "fetching": False, "plan": "Google AI Pro", "fetchedAt": "12:00:00",
                   "limits": [{"kind": "session", "name": "Gemini \u00b7 5-hour", "scope": "Gemini", "window": "5-hour", "percent": 92, "reset": "1h"},
                              {"kind": "session", "name": "Claude and GPT \u00b7 5-hour", "scope": "Claude and GPT", "window": "5-hour", "percent": 25, "reset": "2h"},
                              {"kind": "model", "name": "Gemini 3 Pro (High)", "scope": "", "window": "Gemini 3 Pro (High)", "percent": 60, "reset": "4h"},
                              {"kind": "model", "name": "Claude Sonnet 4.5", "scope": "", "window": "Claude Sonnet 4.5", "percent": 0, "reset": ""}]}
            state = {"event": "state", "config": {}, "providers": {"claude": {}, "chatgpt": {}, "antigravity": agy}}
            await page.select_option("#provider", "antigravity")
            await page.select_option("#metric", "model")
            await asyncio.sleep(2)  # the plugin reports Antigravity as not running here; then it "starts"
            await pi["ws"].send(json.dumps({"event": "sendToPropertyInspector", "context": CTX, "payload": state}))
            await asyncio.sleep(0.5)
            models = await page.eval_on_selector_all("#filterSelect option", "os => os.map(o => o.textContent)")
            print("antigravity model list:", models)
            await page.select_option("#filterSelect", "Gemini 3 Pro (High)")
            await asyncio.sleep(0.5)
            await page.screenshot(path=SHOT.replace(".png", "-antigravity.png"), full_page=True)
            saved = [e for e in log if e.startswith("settings=")][-1]
            print("saved:", saved)
            ok = ok and "Gemini 3 Pro (High)" in models and '"filter": "Gemini 3 Pro (High)"' in saved
            await page.select_option("#metric", "session")
            await asyncio.sleep(1.5)
            await pi["ws"].send(json.dumps({"event": "sendToPropertyInspector", "context": CTX, "payload": state}))
            await asyncio.sleep(0.5)
            groups = await page.eval_on_selector_all("#filterSelect option", "os => os.map(o => o.textContent)")
            print("antigravity group list:", groups)
            ok = ok and "Gemini" in groups and "Claude and GPT" in groups
            result["ok"] = ok
            print("page errors:", errors)
            await b.close()
        await plugin["ws"].close()
        proc.wait(10)
    print("events:", log)

result = {"ok": False}
asyncio.run(main())
shutil.rmtree(WORK, ignore_errors=True)
print("inspector test", "passed" if result["ok"] else "FAILED")
sys.exit(0 if result["ok"] else 1)
