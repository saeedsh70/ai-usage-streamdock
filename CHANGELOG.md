# Changelog

## 1.0.0 — unreleased

First public release.

- **Providers:** Claude (through Claude Code's login), ChatGPT (through Codex's login) and Google Antigravity (through the running app's local language server). Each key picks its own provider.
- **What a key can show:** the 5-hour limit, the weekly limit, a model's or quota group's limit, an overview of 5-hour and weekly, and extra usage or credits.
- **Read-only:** the plugin only reads logins that the providers' apps already keep. It never stores, renews or writes tokens and never asks for cookies or passwords.
- **Styles:** liquid fill, gradient number, glowing bar and glowing ring, with label chips and large digits. They are designed for the ~100 px keys of the Redragon SS-550: bold type only, text that shrinks to fit, and nothing below ~17 px on the device.
- **Network:** system or custom HTTP proxy, a configurable refresh interval, and back-off when rate limited.
- **Model and group picker:** a dropdown lists the models, extra limits and Antigravity quota groups your plan reports.
- **Keys:** tap to refresh, hold to open the provider's page.
- **Installer:** a standard per-user installer (no administrator rights) with an Apps & features entry. It restarts the stream deck software on its own, so the setup file can be deleted afterwards. It also replaces the earlier "Claude Usage" preview plugin.
