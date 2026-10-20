# Installation

## Windows

1. Download the Windows x64 setup EXE from [Releases](https://github.com/nikhilgs817/AI_PULSE/releases).
2. Open it. The installer includes the application runtime and SQLite, creates a Start menu entry, and offers a desktop shortcut.
3. Launch **AI Pulse**. The first launch creates your data folders and starts hardware/model discovery.
4. Open **Settings** to enable sign-in startup, connect model endpoints or change the temperature warning threshold.

The preview installer is not publisher-signed. Windows may identify an unknown publisher. Obtain it only from the intended repository/release and compare the published checksum; do not disable Windows security protections. A trusted signing identity is needed for a polished public distribution.

Devices with Application Control policies may block the unsigned app entirely. If that happens, use a build approved by the device administrator or wait for an appropriately signed release; the installer cannot override that policy.

The default install is per-user. Ordinary monitoring does not require administrator privileges. The portable ZIP can be extracted into a writable folder and launched by opening `AiPulse.exe`.

### Deep sensors on Windows

CPU/board/drive sensors may need privileged hardware access. AI Pulse uses the signed [PawnIO driver](https://pawnio.eu/) through [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor).

1. Install the official **signed** PawnIO distribution if it is not already installed. Do not choose its unrestricted/unsigned edition.
2. In AI Pulse, select **Sensors or Settings → Connect deep sensors**.
3. Approve the Windows administrator prompt if you want these readings.

This starts a small, separate hardware-only helper. The main dashboard and model gateway remain unprivileged. The helper sends readings through a same-user named pipe, accepts no control commands, and exits when the dashboard process exits. Permission is session-scoped; it is not a permanently elevated startup service. After restarting AI Pulse, connect deep sensors again if needed. The optional **Ask for deep-sensor permission when AIPULSE starts** setting requests this on each launch; it is off by default and still requires your approval.

Some sensors still may not exist in the driver's interface. On the tested Radeon 8060S, the GPU core temperature is not exposed by this collector; **GPU VR SoC** is an available, separately labeled reading. Do not interpret the latter as hotspot/core temperature. CPU Tctl/Tdie and drive temperatures became available with the helper.

### Upgrade or uninstall

Open a newer installer to upgrade. Close AI Pulse with **Ctrl+Q** or its tray **Quit AI Pulse** action first. Your data lives outside installed program files and is preserved. Use Windows **Installed apps** or the Start menu uninstaller to remove the app. The uninstaller removes a sign-in entry pointing to that installation. Data and backups are not automatically deleted.

## Ubuntu

Target platforms are Ubuntu Desktop x64 24.04 and 26.04; check the [validation report](VALIDATION.md) for which environments have actually run tests.

Download `ai-pulse_0.3.0_amd64.deb`. If your desktop has a graphical installer for local DEB packages, open it and select Install. Some Ubuntu installations do not associate DEB files with a graphical installer. In that case:

```bash
sudo apt install ./ai-pulse_0.3.0_amd64.deb
ai-pulse
```

APT resolves standard system-library dependencies. It does not install a separate .NET runtime: the app includes one. Internet access can be needed for missing OS packages. Launch subsequent sessions from the applications menu.

For an AppImage, allow execution in the file's Properties, then open it. A system without compatible FUSE support may use AppImage's extraction/run option or the DEB instead. An AppImage does not remove dependencies on a functioning Linux desktop and graphics stack.

Ubuntu sensors use procfs, hwmon and DRM interfaces exposed by the kernel. No Windows driver is installed on Linux. Missing/read-restricted sensors are unavailable. Pinning, exact window positioning, tray icons and notifications depend on the desktop/compositor. Wayland may limit application-controlled placement; use the desktop's “Always on top” option when needed. Do not run the entire dashboard as root to work around this.

To upgrade, install the new DEB. To remove app files:

```bash
sudo apt remove ai-pulse
```

Personal history remains in your user data folder.

## Sharing with friends and family

Share a release installer, DEB, AppImage or portable ZIP. Do not share your `data`, `backups` or settings folders. Each installation initializes a fresh database, auto-detects its own hardware and looks for local tools. A user without Ollama or agent tools can still use hardware monitoring. Model weights and third-party AI applications are not bundled.

One installer cannot run on both operating systems: choose the matching platform download. ARM64 is not a tested target for this release.


## Central monitoring roles

The installer bundles the dashboard, hub and Host Agent runtime. Service installation is an optional, administrator-authorized step in Settings; it starts background collection before sign-in. Follow the [central setup guide](CENTRAL-MONITORING.md), including host pairing, network profiles, history migration and independent service removal. Uninstalling only the desktop application leaves those services and their data intact.
