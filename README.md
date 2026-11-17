# NetSwitch

A small Windows tray app that switches a network adapter between saved profiles with one click (for example a fixed IP and DHCP) and pops up a notification when the IP address of a watched adapter changes.

![Profiles](docs/screenshots/profiles.png)

![Right-click overview](docs/screenshots/right-click-overview.png) ![Left-click menu](docs/screenshots/left-click-menu.png)

## Install

**Option 1, one line in PowerShell** (no admin needed):

```powershell
irm https://raw.githubusercontent.com/JetLaggedJackal/netswitch/main/install.ps1 | iex
```

It downloads the latest release to `%LOCALAPPDATA%\Programs\NetSwitch`, adds a Start menu shortcut and starts it.

**Option 2, download the exe** from [the latest release](https://github.com/JetLaggedJackal/netswitch/releases/latest) and open it. NetSwitch offers to install itself to the same place (Install), or to run from where it is (Run from here; it won't ask again for that folder). Opening a newer exe later offers to update the installed copy.

The exe is self-contained (~70 MB, nothing else needed) and not code-signed, so Windows SmartScreen may warn the first time: click *More info* > *Run anyway*.

After that, updates come from **Settings > Updates**. Profiles and settings live in `%APPDATA%\NetSwitch` and survive updates and reinstalls.

## Using it

- **Left-click the tray icon:** your profiles. Click one to switch; the active one says "Active". After a switch, "Previous settings" lets you go back.
- **Right-click the tray icon:** internet status (pings 1.1.1.1 / 8.8.8.8, DNS lookup), and every adapter with its IP, DHCP/static, link speed, gateway and matching profile; dots blink with traffic.
  - Click an adapter: **Set static IP…**, **Switch to DHCP**, **Return to previous settings**, **Copy IP**.
  - Click its gateway: ping -t, ping, tracert, pathping, open the router page, copy.
- After every switch the pop-up has **Undo**. With **Safe switching** on (Settings), NetSwitch asks you to keep the new settings and goes back by itself after 30 seconds if you don't, which saves you when a change cuts you off.
- **Settings…** opens the window where you:
  - add, duplicate and delete profiles: name, adapter, IP Automatic/Manual, DNS Automatic/Manual (DHCP with your own DNS works). Type `192.168.1.50/24` in the IP box or `24` in the mask box and the mask fills in as 255.255.255.0;
  - turn on **Start with Windows** (starts at sign-in with admin rights, so switching never shows a UAC prompt), or quit NetSwitch;
  - turn on safe switching, choose pop-up behaviour, and per adapter whether it shows in the overview and whether it pops up when its IP changes;
  - check for updates (or let it check once a day): it downloads the newest release from GitHub and restarts itself.
- When a watched adapter's IP changes, a pop-up shows the old and new IP, mask, gateway, DNS, DHCP server and lease, Wi-Fi name, matching profile, link speed and MAC.
- **Hotkeys:** Ctrl+Alt+1 to 9 switch to profile 1 to 9 (in list order), Ctrl+Alt+0 goes back to the previous settings.
- **Tray icon** gets a green, amber or red dot for internet status (checked every 30 s); hover for details.
- **Apply automatically** (per profile): when the adapter joins a given Wi-Fi network, or whenever it connects. Works best with Start with Windows on, so there's no permission prompt.
- **Export / import profiles** (Settings > Profiles backup) to move them between PCs.
- **Uninstall…** (Settings > Startup) removes the app, shortcut and startup task, and optionally your settings.
- If something unexpected goes wrong, NetSwitch shows a pop-up and keeps running; details go to `%APPDATA%\NetSwitch\error.log`.

## Where settings live

`%APPDATA%\NetSwitch\config.json` – plain JSON, safe to edit by hand or copy to another PC:

```json
{
  "Profiles": [
    { "Name": "Office", "Adapter": "Ethernet", "Dhcp": false, "Ip": "192.168.1.50", "Mask": "255.255.255.0", "Gateway": "192.168.1.1", "Dns": ["192.168.1.1", "8.8.8.8"] },
    { "Name": "DHCP", "Adapter": "Ethernet", "Dhcp": true }
  ],
  "WatchAdapters": ["Ethernet", "Wi-Fi"],
  "NotifyOnIpChange": true
}
```

## How it works

- Switching runs `netsh interface ipv4 set address / set dnsservers` for the adapter. When NetSwitch is not elevated it re-launches itself through UAC with `--apply "<profile>"`, does only that, and exits.
- IP watching uses Windows network change events (no polling), waits 2 seconds for things to settle, then compares the watched adapters' IPv4 addresses with the previous state.
- Before each switch NetSwitch saves the adapter's current settings (`Previous` in config.json), which is what Undo and "Return to previous settings" put back.

## Building

Needs the .NET 8 SDK (Windows, Linux or macOS). On Windows: `winget install Microsoft.DotNet.SDK.8`, then:

```
git clone https://github.com/JetLaggedJackal/netswitch
cd netswitch\src
dotnet publish -c Release --self-contained false -o ..\dist\small
dotnet publish -c Release --self-contained true -p:EnableCompressionInSingleFile=true -o ..\dist\standalone
```

To uninstall: untick "Start with Windows", exit from the tray menu, delete the exe and `%APPDATA%\NetSwitch`.

`NetSwitch.exe --preview editor|notify|popup|applied|error|menu` shows one piece of the UI on its own (for design checks).

## Releases and updates

Every push to `main` that changes `src/` builds `NetSwitch.exe` on GitHub Actions and publishes it as the latest release (version `1.0.<build number>`). NetSwitch's **Check for updates** button downloads that release, swaps the exe in place and restarts. This works when NetSwitch lives in a folder you can write to, such as `%LOCALAPPDATA%\Programs\NetSwitch`.
