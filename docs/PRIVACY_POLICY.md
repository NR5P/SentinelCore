# Sentinel Core Privacy Policy

_Last updated: 3 October 2026_

Sentinel Core is a client for a camera-alert server that **you** run on your own computer. This
policy covers the Sentinel Core apps for iPhone, iPad, Mac, Apple TV, Android and Windows, and the
push-notification relay operated by the developer.

## The short version

- The apps talk to your own Sentinel Core server. Your camera images, video, events, Organizer
  notes and calendar, files, movies, music and settings stay on that server unless you turn on
  a feature that sends something elsewhere - those are listed below under "Services your server
  can contact". The developer cannot see them.
- The developer receives data in two ways only. The push-notification relay sees the text of
  each notification ("Front door - person approaching") for as long as it takes to hand it to
  Apple or Google, and it keeps a record of which license registered which device. It does not
  keep notification text. And if your server has an email account set up, it emails the
  developer the text of internal errors (see "Error reports"), which you can turn off.
- There are no ads, no analytics, no tracking, and nothing is sold to anyone.

## What the apps store on your device

- The address of your server, your login session, and your preferences - so you don't have to
  sign in every time. Login sessions are stored in the platform's protected storage (Keychain
  on Apple devices) and are excluded from cloud backups on Android.
- A push-notification token issued by Apple or Google, which is sent to your server so it can
  alert you.
- A copy of the Organizer (notes, calendar, reminders, boards and their attachments) that you
  can see, so it works offline and in Home Screen widgets. It syncs with your server only.
- Music and podcast episodes you choose to download for offline listening.
- Camera images, artwork and video are fetched from your server when you view them and are cached
  only as any app caches images.

## What your server stores

Everything about your cameras, events, recordings, accounts, Organizer, family files and media
libraries lives on the computer running Sentinel Core, under your control. Retention, deletion
and who can log in are all settings on that server. The developer has no access to it.

## The push-notification relay

Apple and Google will only deliver notifications to an app through credentials held by the
app's developer. So that you do not need developer accounts of your own, your server can send
notifications through a relay the developer operates (`push.nr5p.com`). For each notification,
the relay receives:

- your license (a signed file identifying the purchase - name, email and a license number);
- the device token to deliver to;
- the notification's title and body - a camera alert, or another notice your server sends, such
  as a home-alarm event, a weather warning or a board automation's card - and the camera name,
  event type, event number or Organizer item it refers to.

It verifies the license, checks the device was registered under it, forwards the notification to
Apple or Google, and logs which license pushed to which platform and whether it succeeded. **The
notification text is not logged or stored.** Device tokens and license details are kept so that
pushes can be delivered. A token is deleted when your server unregisters it, when Apple or Google
reports it is no longer valid, or when its license is cut off from the relay.

Background "something changed, sync now" messages that keep the Organizer and its widgets current
carry no content and are never sent through the relay - only when your server pushes directly with
your own credentials.

You can turn the relay off at any time in your server's settings, in which case notifications
require your own Apple/Firebase credentials or are simply not sent.

## Services your server can contact

Your server talks to other services only for the features below. Each sends only what is
described; none sends your camera images unless noted. Features marked _off by default_ do nothing
until someone turns them on in the server's settings.

| Feature | Service | What is sent |
| --- | --- | --- |
| Cloud vision (_off by default_) | The AI provider you choose (OpenAI or any compatible service, Anthropic, Google) | Alert images, to be described |
| Email, Telegram and Discord alerts (_off by default_) | Your email provider, Telegram, Discord | Alert text, and the alert image unless you turn that off; with Telegram chat on, your questions and the server's answers |
| Webhook (_off by default_) | The address you enter | The alert analysis |
| Weather tab, morning briefing and weather alerts | US National Weather Service, NOAA, Iowa Environmental Mesonet, Esri (map background) | The location set in the server's settings, to four decimal places (about 11 m), for the forecast and alerts; the map's area for radar and storm tiles |
| Looking up an address in Settings | OpenStreetMap Nominatim | The address you type |
| Internet outage alerts (_off by default_) | Google, Cloudflare and Apple connectivity checks | A plain request with no data, to see whether the Internet is reachable |
| Movies & TV (only with a TMDB key) | The Movie Database (TMDB) | Movie and show titles and years, to fetch artwork and descriptions |
| Music | MusicBrainz and the Cover Art Archive; LRCLIB | Artist and album names, for missing album covers; artist, title, album and length, for missing lyrics. Each can be turned off |
| ListenBrainz scrobbling (_off unless a person connects it_) | ListenBrainz | That person's plays: track, artist, album and time |
| Podcasts | Apple's podcast directory, and each show's own feed and audio host | Search terms and your directory country; feed and episode requests, as any podcast app makes |
| Movie downloads (_off by default_) | Your VPN provider (Mullvad's API when you give a Mullvad account number), the torrent sites you add and the people sharing each download, ipinfo.io, am.i.mullvad.net, api.ipify.org | The Mullvad account number and a key the server makes; searches and downloads, through the VPN; requests that check the VPN is hiding your home address |
| Fast local detection (_off by default_) | Hugging Face | A one-time download of the detection model |

Podcast transcripts and the Organizer's holiday calendars are worked out on your server and send
nothing anywhere.

Some apps also contact a service directly: when you add a movie or an album, the poster or cover
shown in the results is loaded from TMDB or the Cover Art Archive. On iPhone, iPad, Mac and Apple
TV the weather radar map is drawn over Apple Maps.

## Error reports

If your server has an email account set up under Settings -> Email, it emails the developer
(`nathan@nr5p.com`) when something inside the server or dashboard hits an internal error, through
that email account. A report holds the error message, the technical details of where it happened,
the program's build, and the time - never images or clips. This is on by default so problems get
noticed; turn it off under Settings -> Bug Reports and nothing is sent.

## Permissions the apps ask for

- **Notifications** - to show alerts from your server.
- **Local network / Bonjour** - to find and talk to your server on your home network.
- **Camera** (optional) - to stream the device to your server as one of your cameras, and to take
  a reference photo when enrolling a known face. Video and photos go to your server, not to the
  developer.
- **Microphone** (optional) - to say a reminder, note or event, and to include sound when streaming
  the device as a camera.
- **Speech recognition** (optional, iPhone, iPad and Mac) - to turn what you say into a reminder.
  Recognition is done by the device's own speech service: Apple's on Apple devices (on the device
  where it can, otherwise on Apple's servers), and the system's speech service on Android.
- **Reminders** (optional, iPhone, iPad and Mac) - only if you turn on bringing reminders from an
  Apple Reminders list into the Organizer. They move to your server and out of that list.

## Children

Sentinel Core is not directed at children and does not knowingly collect information from them.

## Changes and contact

Changes to this policy will be posted at this address with a new date. Questions:
**privacy@nr5p.com**.
