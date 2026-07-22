# TOA - Fan Control

**Zero-config fan control for Windows. Your case fans follow your hottest component — that's it, that's the app.**

---

<p align="center">
  <img src="Docs/images/dashboard-dark.png" width="380" alt="TOA - Fan Control — dark theme" />
  &nbsp;&nbsp;
  <img src="Docs/images/dashboard-light.png" width="380" alt="TOA - Fan Control — light theme" />
</p>

## Why this exists

Years ago I installed SpeedFan — the FanControl of its day — to cool my PC down. After installing it, I was overwhelmed with all the options, cryptic sensor names, and settings I didn't understand. After about five or 10 minutes of asking myself, "What is this? What is that?," I uninstalled it. SpeedFan is long dead now, and its spiritual successor, FanControl, is genuinely powerful… and still greets you with curve editors, sensor mixes, and homework.

I never wanted that type of control over my fans. I didn't want the confusion or possibility of changing a setting and cooking my PC. I wanted simplicity. Cooler parts, quiet fans, and nothing to configure. Basically, a "start it and forget it" type of app. Here it is, years later and I built exactly what I wanted. Hopefully, it's what you've wanted, too.

The first real test I put it through was Diablo IV. It would continuously push my GPU to 85°C to almost 90°C. With this app running, it peaked at 75°C, and I barely heard the fans at all. Ten plus degrees cooler, near silent and peace of mind. That's when I knew I had the app I wanted.

## What it does

The app adjusts **only your PC case fans**, making them follow your hottest component — your CPU or your GPU. Every second it reads both temperatures and sets every case fan to match the hotter of the two, one-to-one: **70°C means 70% fan speed.**

- A hard **30% floor** means no case fan ever stalls, and changes are **rate-limited** so fans glide to the needed speed instead of jerking up and down.
- Your **AIO pumps, CPU coolers, and GPU fans are never touched** — they stay on your motherboard's BIOS control (and your GPU driver's), where they belong.
- On first run, the app shows you the case fans it found and lets you confirm which ones it may drive. Fans it recognizes as pumps or CPU/GPU coolers are never even listed.

There are no curves, no profiles, no targets, no tuning. If that's what you want, this isn't your app — [FanControl](https://github.com/Rem0o/FanControl.Releases) is excellent. This app is for everyone who bounced off tools like that and just wants their PC to run cooler and quieter.

## Everyday use

There's nothing you *have* to do — it sits in the system tray and does its job on its own. But when you want to check in on things, you get:

- **A dashboard** — CPU/GPU temperature dials with peak markers, plus a spinning fan tile per case fan showing the driven % (the blades spin at the fan's real speed).

  <p align="center"><img src="Docs/images/dials-dark.png" width="300" alt="Temperature dials — dark theme" />&nbsp;&nbsp;<img src="Docs/images/dials-light.png" width="300" alt="Temperature dials — light theme" /></p>

- **Game Mode** — a small, always-on-top, drag to place any where on your screen, temps/fans readout for gaming. Works over Borderless/Windowed Fullscreen. Nothing can draw over true exclusive fullscreen. That's a Windows rule.

  <p align="center"><img src="Docs/images/game-mode-dark.png" width="300" alt="Game Mode overlay — dark theme" />&nbsp;&nbsp;<img src="Docs/images/game-mode-light.png" width="300" alt="Game Mode overlay — light theme" /></p>

- **Settings (the ⚙ cog)** — dark/light theme, hand fans back to BIOS anytime, choose fans, start with Windows (boots silently into the tray), Check for updates, About and a full uninstall.

  <p align="center"><img src="Docs/images/settings-menu.png" width="220" alt="Settings menu" /></p>

- **Self-maintaining prerequisites** — the app checks PawnIO and .NET for updates daily (if you leave your PC on continuously) and at app launch. The app only downloads official signed installers, verifies their signatures and never interrupts you mid-game with a popup.

## If the app ever fails, your BIOS takes over. Every time.

This is the design promise: **there is no failure that leaves your fans stranded.**

<table align="center">
  <tr><th>What happens</th><th>Result</th></tr>
  <tr><td>You close the app</td><td>Fans back to BIOS control</td></tr>
  <tr><td>The app crashes</td><td>Fans back to BIOS control</td></tr>
  <tr><td>The app is force-killed (Task Manager)</td><td>A watchdog process restores BIOS control</td></tr>
  <tr><td>Temperature readings vanish</td><td>Fans handed to BIOS within 3 seconds</td></tr>
  <tr><td>Windows shuts down or you log off</td><td>Fans back to BIOS control</td></tr>
  <tr><td>You uninstall</td><td>Fans back to BIOS control</td></tr>
</table>

Worst case, your PC behaves exactly as it did before you installed this. Every one of those paths has been tested on real hardware.

## Requirements

- **Windows 10 or 11, 64-bit.** That's the only requirement you need to meet yourself.
- Administrator access — the app asks automatically (required to talk to the motherboard's fan hardware).
- .NET 10 and the PawnIO driver — **the installer handles both** if they're missing, with your permission.

Windows only, by design. No Linux/SteamOS version is planned — that world already has native tools.

## Installing

1. Download **`TOA - Fan Control Setup.exe`** from the [Releases](../../releases) page.
2. Run it. **Windows will show a blue "Windows protected your PC" warning** — that's SmartScreen reacting to an unsigned open-source app with no download reputation yet, not a threat detection. Click **More info → Run anyway**. (Don't take my word for what the app does — the entire source code is this repository.)
3. The installer checks for .NET 10 and PawnIO (installs them if needed, telling you first), asks where you want the app installed (drive letter, etc.), adds it to your Start menu, and offers a desktop shortcut.
4. First launch shows you the fans it found — uncheck anything that isn't a regular case fan, hit Save, done.

**Rolling back:** every version stays downloadable on the Releases page. If an update ever misbehaves on your hardware, grab the previous installer and run it.

## FAQ

**Why does it need administrator?**
Fan speed lives on the motherboard's Super I/O chip, which Windows only exposes to elevated processes via a signed kernel driver (PawnIO — the same signed driver modern hardware tools use).

**Won't adjusting fans every second wear them out?**
No — it's a dimmer switch, not a gear shift. Speed changes are electronic (PWM), rate-limited to gentle ramps, and 1°C = 1% (~15 RPM — inaudible). The one thing that's actually semi-hard on a fan motor is stop/start cycling, which the 30% floor makes impossible. Full explanation: [Docs/why_it_doesnt_wear_fans.md](Docs/why_it_doesnt_wear_fans.md)

**Does this touch my RGB lighting?**
Never. The app writes fan *motor* speeds on the motherboard's fan-control chip. RGB runs on entirely separate hardware (ARGB headers / USB controllers) that this app can't even see. Your light show is untouched.

**I run SignalRGB / iCUE / Armoury Crate — will they fight?**
Only if that software *also* has fan control enabled (RGB suites sometimes switch it on after updates). If something else starts overwriting fan speeds, this app detects it within seconds, holds your speeds steady by re-asserting them faster than the other program writes, and shows you a notification naming the fix: turn off fan control in the other app. Lighting features are never affected.

**Some sensors don't show on my brand-new hardware.**
The sensor library is deliberately version-pinned — it never updates behind your back (silent sensor-library updates breaking PCs overnight is a recurring story with other tools). Brand-new chips can lag behind support; when that happens, the app runs read-only and your BIOS keeps running the fans — nothing breaks. Support arrives via a normal, tested release. Open an Issue with your hardware details.

## Your data stays yours

This app collects **nothing** and transmits **nothing**: no telemetry, no analytics, no accounts, no personal data — there isn't even a server to send anything to. Temperatures and fan speeds are read from your hardware, shown on screen, and written only to a local log file next to the app. The app's only internet use is checking for updates (PawnIO from its author's GitHub, .NET from Microsoft, and the app itself from this repository), and those requests send nothing about you or your PC.

## Found a bug?

Open an [Issue](../../issues). Include the `fan_debug.log` file that lives next to the app's exe — it records exactly what the app was doing, and it contains no personal information (temperatures, fan speeds, and app events only — read it yourself first if you like).

---

**© 2026 TOA. All rights reserved.**

This software is provided "as is", without warranty of any kind, express or implied. It controls real hardware — your fans — and while it is designed to always return them to motherboard (BIOS) control and never drive them below a safe floor, you use it at your own risk. TOA is not liable for any damage to hardware, loss of data, or any other damages arising from the use of this software.
