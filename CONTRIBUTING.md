# Contributing

Thanks for helping! Issues and pull requests are welcome.

## Reporting a problem

Please include:
- your device and the version of the stream deck software (Help/About),
- the provider and what the key shows, plus the text of the **Status** box in the key settings,
- the relevant lines of `%APPDATA%\AIUsageDeck\plugin.log`. The log never contains tokens, but check it before pasting.

If a provider changed its response format, please paste the JSON response with personal data removed.

## Code rules

- **C# 5 only.** Anyone can build the plugin with the compiler that ships inside Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`), and that compiler stops at C# 5. So don't use string interpolation (`$"..."`), `?.`, `nameof`, `out var`, expression-bodied members or auto-property initialisers. CI compiles with `mcs -langversion:5` and also with the real `csc.exe` on Windows.
- **No dependencies** beyond the .NET Framework assemblies already referenced (`System.Web.Extensions`, `System.Drawing`, `System.Management`).
- **Read-only.** Never write, renew or upload another app's login, and never ask users for cookies or passwords. Never log tokens.
- **No shell commands or scripts** from the plugin or the installer. Use Windows APIs instead, as the WMI and IP Helper calls do.
- **Readable on a 14 mm key.** Don't add text smaller than ~24 px on the 144 px canvas. Before sending UI changes, render the gallery (`test/run-tests.sh`) and look at `build/gallery/gallery-actual-size.png`.

## Adding a provider

Implement `IProvider` (see `src/Usage.cs`) in a new `src/<Name>Provider.cs` and add it to `Providers.All`. Map the provider's data to `Limit`s:

- `session` for windows of about 5 hours, `weekly` for 7-day windows,
- `model` for per-model limits, with `Scope` set to a quota group,
- `ExtraUsage` for money or credits.

Then add the provider to the **Provider** list in `pi.html` / `pi.js`, give it a mock in `test/fake_host.py`, and document where its data comes from in the README.

## Running the tests

```bash
sudo apt-get install mono-devel libgdiplus nsis   # Linux; on Windows use build.ps1 and Python directly
pip install -r test/requirements.txt
./test/run-tests.sh
```

There's also an optional test of the settings panel, which runs it in headless Chromium:

```bash
pip install playwright && python3 -m playwright install chromium
python3 test/pi_test.py com.saeedsh.aiusage.sdPlugin/AIUsage.exe inspector.png
```

## Releasing

1. Bump `Version` in `com.saeedsh.aiusage.sdPlugin/manifest.json`, `src/AssemblyInfo.cs` and `CHANGELOG.md`.
2. Tag `vX.Y.Z` and push the tag. The workflow builds on Windows, runs the tests, and attaches `AIUsage-Setup-X.Y.Z.exe` and `SHA256SUMS.txt` to a GitHub release.

## Code signing

Unsigned installers trigger Windows SmartScreen's "unknown publisher" warning. To sign builds, add two repository secrets:

- `SIGN_PFX_BASE64`: a code-signing certificate (`.pfx`), base64-encoded
- `SIGN_PFX_PASSWORD`: its password

`build.ps1` then signs both `AIUsage.exe` and the installer with `signtool`, using SHA-256 and a timestamp.

Free options exist for open-source projects, for example [SignPath Foundation](https://signpath.org). Azure Trusted Signing is a paid alternative. Even a signed build may be flagged by SmartScreen until it builds up reputation.
