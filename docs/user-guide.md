# ProPS1 Studio — User Guide

ProPS1 Studio compiles PowerShell scripts (`.ps1`) into standalone Windows executables using the
open-source [ps2exe](https://github.com/MScholtes/PS2EXE) module. Optionally it compresses the
result with [UPX](https://github.com/upx/upx), embeds a UAC manifest so the exe requests
administrator rights, encrypts the script source inside the exe, scans the script for secrets
before compiling, and signs the output with Authenticode. The **BAT → EXE** page does the same
for batch files (`.bat` / `.cmd`), compiled directly with the bundled .NET Framework `csc.exe`.

Version 1.0.0 · Developed by Mahdi · Windows 10 (1809) or later · x64

---

## Contents

1. [Requirements](#requirements)
2. [Installing](#installing)
3. [Login (KeyAuth)](#login-keyauth)
4. [Quick start](#quick-start)
5. [The Dashboard page](#the-dashboard-page)
6. [The Convert page](#the-convert-page)
7. [The BAT → EXE page](#the-bat--exe-page)
8. [The Setup page](#the-setup-page)
9. [The Settings page](#the-settings-page)
10. [The Diagnostics page](#the-diagnostics-page)
11. [The Help page](#the-help-page)
12. [Secret scanning](#secret-scanning)
13. [Script source encryption](#script-source-encryption)
14. [Output protection](#output-protection)
15. [Authenticode signing](#authenticode-signing)
16. [Files, logs and data](#files-logs-and-data)
17. [Troubleshooting](#troubleshooting)
18. [Frequently asked questions](#frequently-asked-questions)

---

## Requirements

- **Windows 10 version 1809 or later** (or Windows 11), 64-bit.
- **PowerShell 5.1** or later — included with Windows. Conversions run in a child
  `powershell.exe` process with `-ExecutionPolicy Bypass`.
- **No .NET runtime installation needed** — the released build is self-contained.
- **Administrator rights are not required** to run ProPS1 Studio. They are only required if you
  convert with *Run as administrator*, because the resulting exe will trigger a UAC prompt.

The app installs its two external tools for you (see [Setup](#the-setup-page)):

| Tool    | Required | Purpose                          | How it is obtained |
|---------|----------|----------------------------------|--------------------|
| ps2exe  | Yes      | Compiles `.ps1` → `.exe`         | Installed from the PowerShell Gallery |
| UPX 5.2.1 | No    | Compresses the output executable | Downloaded from the official UPX GitHub release, SHA-256 pinned |

## Installing

### Installer

Run `ProPS1Studio-Setup-1.0.0-x64.exe`, follow the wizard, and launch **ProPS1 Studio** from the
Start menu or desktop shortcut.

### Portable

1. Unzip `ProPS1Studio-portable-1.0.0-x64.zip` anywhere (a USB stick works).
2. Run `ProPS1Studio.exe`.

The portable build writes nothing next to the executable — all per-user data goes to
`%LOCALAPPDATA%\ProPS1Studio`.

## Login (KeyAuth)

ProPS1 Studio is protected by [KeyAuth](https://keyauth.win). Before the main window opens you
must authenticate once per session:

- **Register** (first run) — pick a username and password to create your account. A license key
  is optional here.
- **Sign in** — enter your username and password. Tick *Remember me* to pre-fill your username
  on the next launch.
- **License** — if you were given a license key instead of an account, activate with the key.

After a successful login the studio opens. Closing the window without logging in exits the app.

Notes:

- An internet connection is required for the login check; it contacts the KeyAuth API only.
- Your password is never stored. Only the username is remembered (in
  `settings.json` under `%LOCALAPPDATA%\ProPS1Studio`) and only when *Remember me* is ticked.
- Login errors (wrong password, key revoked, server unreachable) are shown in the error banner
  and logged — details in [Files, logs and data](#files-logs-and-data).

## Quick start

1. Open **Setup** and confirm the ps2exe card shows a green **INSTALLED** badge. If it shows
   **MISSING**, press *Install ps2exe* (installs for the current user from the PowerShell Gallery).
2. Go to **Convert** and press *Browse...* next to **Input** to pick your `.ps1` script. The
   output path is derived automatically (same folder, same name with `.exe`).
3. Tick the options you need — for example *Run as administrator (requireAdministrator)* and
   *Compress with UPX*.
4. Press **Convert**. The result card shows the output path, size and any warnings.
5. If something goes wrong, open **Diagnostics** and press *Export diagnostics* to create a
   redacted support bundle.

Your script is **never executed** by the app — only compiled.

## The Dashboard page

The landing page shows the health of your machine and your recent work:

- **OPERATING SYSTEM / POWERSHELL / PS2EXE / SYSTEM** — environment cards summarising the OS
  build, PowerShell availability, the probed ps2exe module (version and detected parameter
  count), and overall system status.
- **Recent conversions** — every executable compiled on this machine, newest first, with source
  script, output path and result. Use the check/close buttons in a row to open the output folder
  or dismiss the entry.
- **Refresh** (top right) re-runs the environment probe.

## The Convert page

The main workbench.

### Script

- **Input** — the `.ps1` file to compile (*Browse...*).
- **Output** — where the `.exe` is written. *Same folder* derives the name automatically;
  *Browse...* picks a target explicitly.
- **Clear** removes the current selection; **Open output** opens the folder containing the
  produced executable.

### Options

| Option | Effect |
|--------|--------|
| Run as administrator (requireAdministrator) | Embeds a `requireAdministrator` UAC manifest in the output exe. |
| No console window (Windows GUI app) | Builds a windowed (GUI) exe instead of a console exe. |
| Compress with UPX | Post-processes the output with UPX. Skipped automatically for .NET assemblies (see below). |
| Scan for secrets before compiling | Runs secret scanning on the script and blocks the build on critical findings. |
| Encrypt script source (AES-256) | Bakes the script into the exe as ciphertext so opening the file in Notepad shows no source (see [Script source encryption](#script-source-encryption)). |
| Protect output (encrypted stub + anti-debug) | **On by default.** Wraps the finished exe in a fresh encrypted stub that decrypts it at launch and refuses to run under a debugger (see [Output protection](#output-protection)). |
| Overwrite existing output | Replaces the target file if it already exists. |
| Open output folder when done | Opens Explorer at the output location after a successful conversion. |
| Sign with Authenticode after compile | Signs the output using the certificate configured in Settings. |

### Version info & icon (optional)

Embeds version resources into the executable: **Title**, **Company**, **Product**, **Version**,
**Copyright**, **Icon** (*...* picks a `.ico` file) and **Description**.

### Reading the result

Press **Convert** to start. A progress area shows each stage; the **COMPILER OUTPUT** pane
streams the raw ps2exe/compiler output. On success you get a result card with the output path
and size; warnings (for example *UPX skipped — .NET assembly*) are listed alongside. **Cancel**
stops a running conversion.

> **Note:** UPX cannot compress .NET assemblies. ProPS1 Studio detects this up front, compiles
> normally, and reports a warning instead of failing.

## The BAT → EXE page

Compiles batch files (`.bat` / `.cmd`) into standalone executables. Unlike Convert, this page
needs neither ps2exe nor UPX: it invokes the .NET Framework `csc.exe` that ships with Windows
(`\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`), so no Setup step is required.

### Batch file

- **Input** — the `.bat` / `.cmd` file to compile (*Browse...*).
- **Output** — where the `.exe` is written; derived automatically as with Convert.

### Execution level

Radio buttons, same semantics as Convert's manifest option:

| Level | Effect |
|-------|--------|
| As invoker (no elevation) | No UAC manifest requested; the exe runs with the caller's privileges. |
| Require administrator | Embeds a `requireAdministrator` UAC manifest — the exe always prompts for elevation. |
| Highest available | Requests elevation only when the user is an administrator. |

### Options

| Option | Effect |
|--------|--------|
| No console window (Windows GUI app) | Builds a windowed (GUI) exe — the exe's PE subsystem is set to GUI instead of console. |
| Capture output to a log file | Saves console output to `<exe>.log` next to the executable (meaningful in no-console mode; falls back to `%TEMP%`). |
| Keep console open on exit | Adds a pause at the end so the window does not close before you can read it. |
| Forward command-line arguments | Passes everything after the exe name through to the batch script. |
| Run from a temp directory | Extracts and runs in `%TEMP%` so nothing is written next to the exe. |
| Keep extracted temp files | Do not delete the temp folder after the script exits. |
| Compress payload (GZip) | GZip-compresses the batch source before embedding it. |
| Encrypt payload (AES-256) | **On by default.** AES-256-CBC encrypts the embedded payload, so the batch source can no longer be recovered from the exe by Base64-decoding and unzipping it. Untick only if you explicitly want the old, recoverable payload. |
| Protect output (encrypted stub + anti-debug) | **On by default.** Wraps the finished exe in a fresh encrypted stub that decrypts it at launch and refuses to run under a debugger (see [Output protection](#output-protection)). |
| Scan for secrets before compiling | Runs secret scanning on the batch file (see [Secret scanning](#secret-scanning)). |
| Overwrite existing output | Replaces the target file if it already exists. |
| Open output folder when done | Opens Explorer at the output location after a successful conversion. |
| Sign with Authenticode after compile | Signs the output using the certificate configured in Settings. |

### Console & payload (optional)

- **Title** — window title for the console.
- **Color** — two hex digits (e.g. `0A`) using the `cmd color` convention: high nibble is the
  background, low nibble the foreground.
- **Passphrase** — additionally XOR-obfuscates the payload before it is encrypted and Base64
  encoded (an extra layer on top of the AES encryption; see the honest scope note below).
- **Bundled files** — one path per line (or *Add files...*). These files are decoded next to the
  extracted script when the exe runs; 4 MB per file, name collisions are rejected.

### Version info & icon (optional)

Identical to Convert: **Title**, **Company**, **Product**, **Version**, **Copyright**, **Icon**
and **Description** are written into the exe's version resource.

> **Honest scope:** the payload is AES-256-encrypted by default, which stops the trivial
> Base64+GZip reversal that previously recovered the batch source from the exe. The AES key
> material must live in the executable (the exe decrypts at runtime), and the passphrase key
> (if you set one) is handled the same way — but it is no longer stored inside the payload
> blob: it is split and obfuscated in the launcher code, so `strings` or blob extraction does
> not hand over a ready-made key. This hides the batch source from casual inspection (someone
> opening the file in a text editor); it is **not** copy protection. Anyone who decompiles the
> exe or attaches a debugger can recover the key and the plaintext.

### Reading the result

Press **Convert** to start; **Cancel** stops a running conversion. On success the result card
shows the output path, size, execution level, payload encoding (GZip/raw, AES-256 encrypted,
obfuscated or plain),
bundled file count and signing status. Warnings (for example that a passphrase is not copy
protection) are listed alongside, and the **COMPILER OUTPUT** pane shows the raw `csc.exe`
output. Your batch file is **never executed** by the app — only compiled.

## The Setup page

Status and one-click installation for the conversion pipeline tools:

- **ps2exe** (REQUIRED) — version, install location and the exact number of parameters detected
  on your system (never a hard-coded list). *Install ps2exe* appears only while it is missing.
- **UPX** (OPTIONAL) — official 5.2.1 build, verified against a pinned SHA-256 hash.
  *Install UPX* downloads it into the app's tools folder.

The **Tools folder** button (top right) opens the folder where tools live, and **Refresh**
re-checks both tools.

## The Settings page

Persisted preferences stored in `settings.json`. Press **Save settings** to apply, **Reset to
defaults** to restore defaults.

### General

- **Default output folder** — default target directory for new conversions.
- **Conversion timeout (s)** — seconds before the compiler process is killed (15–3600).
- **Log level** — `Debug`, `Info`, `Warn` or `Error`.
- **Open output folder after conversion** — open Explorer automatically when a build succeeds.
- **Enable secret scanning by default** — pre-tick *Scan for secrets before compiling* on Convert.
- **Encrypt script source by default** — *Encrypt script source (AES-256)* on Convert is
  pre-ticked; **on by default**. Untick this setting if you want script encryption to start
  off on the Convert page.
- **Protect output by default** — *Protect output (encrypted stub + anti-debug)* on Convert
  and BAT → EXE is pre-ticked; **on by default**. Untick this setting if you want unprotected
  output to start off on both pages (see [Output protection](#output-protection)).

### Conversion tools

- **ps2exe module path** — leave empty to rely on normal `PSModulePath` discovery, or browse to
  a specific `ps2exe.psd1`/`ps2exe.psm1`.
- **UPX executable** — path to `upx.exe`; empty means the copy in the app tools folder.
- **Compress with UPX by default** / **UPX --force (skip format checks)**.

### Secret scanning

Extra patterns — one regular expression per line, tested against the script and masked in
reports, e.g. `MYAPP_[A-Z0-9]{16}`.

### Authenticode signing (optional)

See [Authenticode signing](#authenticode-signing) below.

The bottom card shows the exact paths of `settings.json` and the current log file.

## The Diagnostics page

- **Live log** — streams the current session log. *Clear* empties the view, *Auto-scroll to
  newest entries* keeps the tail visible. Secrets are masked before they ever reach the log file.
- **Environment report** — the same probe the Dashboard uses, in text form.
- **Export diagnostics** — writes a redacted zip (environment report, settings with secrets
  stripped, recent conversions, logs) into the diagnostics folder. *Open last export* and the
  **Diagnostics folder** button take you there.

## The Help page

- **What this app does** — feature overview.
- **Quick start** — the five steps above.
- **Good to know** — behavioural guarantees (UPX/.NET handling, no auto-execution of your
  script, masked secrets).
- **Links & documentation** — this user guide, ps2exe on GitHub, UPX on GitHub.

## Secret scanning

Before compiling, the script is checked against a built-in rule set (API keys, tokens,
passwords, private keys, connection strings) plus your custom patterns from Settings.

- Findings are shown with line number, rule and a **masked** value — the raw secret never
  appears in the UI, the log, or any export.
- Findings of **critical** severity block the conversion; you can review them and disable the
  scan for a specific build if it is a false positive.
- The scan only ever reads the script file; nothing is uploaded anywhere.

## Script source encryption

**Encrypt script source (AES-256)** on the Convert page is **ticked by default** (controlled by
*Encrypt script source by default* in Settings ▸ General). It compiles a self-decrypting wrapper
instead of your script directly. The output exe then contains only AES-256-CBC ciphertext plus
the small loader that decrypts and runs it at startup:

- Opening the exe in Notepad (or `strings`) no longer reveals your script source.
- The script still runs exactly as before: arguments, `exit` codes and `#requires` directives
  are preserved; your original `.ps1` file is never modified.
- Secret scanning still runs against your original script, before encryption.

> **Honest scope:** the decryption key material is embedded in the executable itself — by
> necessity, the exe must be able to decrypt at runtime — but it is obfuscated in the loader
> rather than stored next to the ciphertext, so `strings` or payload extraction does not
> reveal the key or your source. This hides the source from casual inspection (someone
> opening the file in a text editor); it is **not** copy protection or DRM. Anyone who
> decompiles the exe or attaches a debugger can recover the key and the plaintext.

The temporary loader exists only during compilation and is deleted afterwards, whether the
build succeeds or fails.

## Output protection

**Protect output (encrypted stub + anti-debug)** on the Convert and BAT → EXE pages is
**ticked by default** (controlled by *Protect output by default* in Settings ▸ General). It
takes the executable the conversion just produced — the ps2exe wrapper or the batch launcher —
and wraps it a second time:

- The real executable is encrypted with a fresh **AES-256-CBC** key generated for that one
  build and embedded in the wrapper (stub) as a .NET resource. No two builds share a key.
- At launch the stub decrypts the executable in memory and runs it; command-line arguments,
  exit codes and console behaviour pass through unchanged.
- Before running anything the stub performs a static anti-debug gate: it refuses to run (and
  exits without executing the payload) when a debugger is attached (`Debugger.IsAttached`) or
  when the Windows `IsDebuggerPresent` API reports one. (An earlier build also scanned for
  reverse-engineering tool processes and XOR-obfuscated its strings; that pattern matched
  Windows Defender's XWorm heuristic and was removed — see the [release notes](https://github.com/mahdi2372/ProPS1Studio/releases).)
- The requested execution level (UAC manifest), version resources and icon are written into
  the stub, so the protected exe looks and behaves like the unprotected one. Signing (if
  enabled) signs the final stub.
- Warnings on the result card show the honest scope note for every protected build.

Untick *Protect output* on the page for an ordinary, unprotected executable; the setting
applies per build, independently of *Encrypt script source* / *Encrypt payload* (you can
combine both layers or use either alone).

> **Honest scope:** the real program is AES-256 encrypted inside a per-build stub that
> decrypts it at launch and exits when a debugger is attached. This forces decompilation or a
> memory dump to inspect — it is obfuscation, not DRM or copy
> protection: the decryption key ships inside the exe. Someone determined can attach a
> debugger before the check, dump memory after decryption, or patch the gate. It stops
> casual `strings`/double-click inspection and casual reversing, not a professional attacker.

## Authenticode signing

To sign output executables automatically:

1. Open **Settings → Authenticode signing**.
2. Choose a certificate by **Certificate thumbprint** in the given **Certificate store**
   (`CurrentUser` or `LocalMachine`), *or* pick a **PFX file**.
3. Optionally set a **Timestamp server** so signatures stay valid after the certificate expires.
4. Enable **Sign automatically after each conversion**, then **Save settings**.

The PFX password is **never stored**. It is read at convert time from the
`PROPS_SIGN_PFX_PASSWORD` environment variable of the ProPS1 Studio process:

```powershell
$env:PROPS_SIGN_PFX_PASSWORD = 'your-password'
# then start ProPS1Studio.exe from that shell, or set it system-wide
```

Signing failures never lose your build — the unsigned exe remains and the error is reported.

## Files, logs and data

Everything per-user lives under `%LOCALAPPDATA%\ProPS1Studio`:

| Path | Contents |
|------|----------|
| `settings.json` | Persisted preferences (no secrets). |
| `logs\prop-s1-yyyyMMdd.log` | Daily application log. |
| `tools\` | ps2exe and UPX installs. |
| `temp\` | Conversion work folder (cleaned automatically). |
| `diagnostics\` | Exported support bundles. |

The install location of the app itself contains `docs\user-guide.md` and `docs\licenses\`
(third-party license texts), which the Help page links to.

## Troubleshooting

**Conversion fails with "ps2exe not installed"**
Open Setup and press *Install ps2exe*. The install is per-user and needs no elevation. If the
PowerShell Gallery is blocked by policy, install manually:
`Install-Module ps2exe -Scope CurrentUser`.

**"The installed ps2exe version does not support the option ..."**
Your ps2exe module is too old for a selected option. Update it from Setup or
`Update-Module ps2exe`.

**UPX warning: "the file is a .NET assembly"**
Not an error. ps2exe-produced exes are .NET assemblies and UPX refuses them; the conversion
succeeds without compression. Disable *Compress with UPX* to avoid the warning.

**Antivirus quarantines the output exe**
Heuristic AV tools often flag freshly built, unsigned executables. Sign the output
([Authenticode signing](#authenticode-signing)) and/or add an exclusion for your output folder.

**The exe exits immediately**
Run it from a console (`cmd /c your.exe`) to read its output, or rebuild without *No console
window*. Scripts requiring a different working directory should use `$PSScriptRoot`.

**App starts with missing tools / strange environment**
Check the Dashboard cards, then Diagnostics → *Export diagnostics* and inspect the bundle.

**Need more detail?**
Set **Settings → Log level** to `Debug` and reproduce the problem; the log file path is shown
at the bottom of Settings.

## Frequently asked questions

**Does the app run my script?**
No. It only compiles it. The only way your script executes is if you run the produced exe
yourself.

**Where is my output?**
Beside the input script by default; the result card and *Open output* show it, and recent
conversions are listed on the Dashboard.

**Can I compile batch files?**
Yes — use the **BAT → EXE** page (see [The BAT → EXE page](#the-bat--exe-page)). It compiles
`.bat` / `.cmd` with the bundled .NET Framework compiler; no internet or Setup needed.

**Can I use it without internet?**
Yes, if ps2exe and UPX are already installed (Setup shows them). Nothing else phones home.

**Is the UPX download verified?**
Yes — only the official 5.2.1 win64 release is accepted, checked against a pinned SHA-256 hash.

---

## Third-party software

- [ps2exe](https://github.com/MScholtes/PS2EXE) by Markus Scholtes — Microsoft Limited Public
  License 1.1.
- [UPX](https://github.com/upx/upx) by the UPX team — GNU GPL version 2 or later (used as a
  separate command-line tool).
- .NET runtime — MIT license.

Full license texts ship in `docs\licenses\` next to this guide.
