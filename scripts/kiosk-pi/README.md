# Wall kiosk (Raspberry Pi) config

The wall tablet is a Pi 4 (Pi OS trixie, labwc, official DSI touch display) running Chromium
full screen against the dashboard with `?kiosk=1`. These are copies of the files that live on it,
kept here so the setup can be rebuilt. Changing them here does not change the Pi: copy them over
by hand (`scp` as `admin@`).

| File | On the Pi | What it does |
| --- | --- | --- |
| `autostart` | `~/.config/labwc/autostart` | Starts Chromium (Wayland, kiosk, software video decode) and the screen-sleep watcher |
| `rc.xml` | `~/.config/labwc/rc.xml` | Real touch for the Goodix screen (`mouseEmulation="no"`), needed for native swiping |
| `kiosk-touch-wake` | `~/.local/bin/kiosk-touch-wake` | Wakes the screen on a tap and runs swayidle |
| `kiosk-screen` | `~/.local/bin/kiosk-screen` | Dim / off / on, called by swayidle |

## Screen sleep

The screen dims after 1 minute untouched and goes dark after 5. A tap brings it back. The
dashboard's `kiosk-wake.js` swallows that first tap (`screenoff=300` in the start URL, which must
match the off timeout), and a page Wake Lock (the radar full screen) keeps it awake.

Two Pi-specific traps shaped this:

- With `mouseEmulation="no"`, labwc does not count touches as idle activity, so swayidle alone
  never sees a tap. `kiosk-touch-wake` reads the touchscreen directly, runs swayidle as a child and
  restarts it on taps, which resets its timers.
- Never power the display output off (`wlopm --off DSI-1`). On this vc4 DSI panel it cannot be
  turned back on ("Swapchain for output 'DSI-1' failed test") until a reboot. "Off" is backlight 0.
