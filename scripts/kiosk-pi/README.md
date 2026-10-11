# Wall kiosk (Raspberry Pi) config

The wall tablet is a Pi 4 (Pi OS trixie, labwc, official DSI touch display) running Chromium
full screen against the dashboard with `?kiosk=1`. These are copies of the files that live on it,
kept here so the setup can be rebuilt. Changing them here does not change the Pi: copy them over
by hand (`scp` as the Pi's login user).

**Before copying:** set `KIOSK_DASHBOARD` at the top of `autostart` to your dashboard's address
(for example `https://192.168.1.50:3443`). `autostart` exports it, so `kiosk-touch-wake` uses the
same value; nothing else holds an address.

| File | On the Pi | What it does |
| --- | --- | --- |
| `autostart` | `~/.config/labwc/autostart` | Starts Chromium (Wayland, kiosk, software video decode) and the screen-sleep watcher |
| `rc.xml` | `~/.config/labwc/rc.xml` | Real touch for the Goodix screen (`mouseEmulation="no"`), needed for native swiping |
| `kiosk-touch-wake` | `~/.local/bin/kiosk-touch-wake` | Wakes the screen on a tap and runs swayidle |
| `kiosk-screen` | `~/.local/bin/kiosk-screen` | Dim / off / on, called by swayidle |

## Screen sleep

The screen dims and goes dark after the times set in Settings > WALL KIOSK (1 and 5 minutes by
default; `kiosk-touch-wake` re-reads them every minute from `/api/kiosk/screen-sleep`, checking
HTTPS against the dashboard CA saved at `~/.config/sentinelcore/ca.crt`). A tap brings it back. The
dashboard's `kiosk-wake.js` swallows that first tap (it reads the same off time; `screenoff=300`
in the start URL only stands in until it does), and a page Wake Lock (the radar or a camera full
screen) keeps it awake.

Two Pi-specific traps shaped this:

- With `mouseEmulation="no"`, labwc does not count touches as idle activity, so swayidle alone
  never sees a tap. `kiosk-touch-wake` reads the touchscreen directly, runs swayidle as a child and
  restarts it on taps, which resets its timers.
- Never power the display output off (`wlopm --off DSI-1`). On this vc4 DSI panel it cannot be
  turned back on ("Swapchain for output 'DSI-1' failed test") until a reboot. "Off" is backlight 0.
