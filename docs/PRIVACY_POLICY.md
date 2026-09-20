# Sentinel Core Privacy Policy

_Last updated: 20 September 2026_

Sentinel Core is a client for a camera-alert server that **you** run on your own computer. This
policy covers the Sentinel Core apps for iPhone, iPad, Mac, Apple TV, Android and Windows, and the
push-notification relay operated by the developer.

## The short version

- The apps talk to your own Sentinel Core server. Your camera images, video, events and
  settings stay on that server. The developer cannot see them.
- The developer's only service is a push-notification relay. It sees the text of each alert
  ("Front door - person approaching") for as long as it takes to hand it to Apple or Google, and
  it keeps a record of which license registered which device. It does not keep alert text.
- There are no ads, no analytics, no tracking, and nothing is sold to anyone.

## What the apps store on your device

- The address of your server, your login session, and your preferences - so you don't have to
  sign in every time. Login sessions are stored in the platform's protected storage (Keychain
  on Apple devices) and are excluded from cloud backups on Android.
- A push-notification token issued by Apple or Google, which is sent to your server so it can
  alert you.
- Nothing else. Camera images are fetched from your server when you view them and are cached
  only as any app caches images.

## What your server stores

Everything about your cameras, events, recordings and accounts lives on the computer running
Sentinel Core, under your control. Retention, deletion and who can log in are all settings on
that server. The developer has no access to it.

## The push-notification relay

Apple and Google will only deliver notifications to an app through credentials held by the
app's developer. So that you do not need developer accounts of your own, your server can send
alerts through a relay the developer operates (`push.nr5p.com`). For each alert, the relay
receives:

- your license (a signed file identifying the purchase - name, email and a license number);
- the device token to deliver to;
- the alert's title, body, camera name, event type and event number.

It verifies the license, checks the device was registered under it, forwards the alert to Apple
or Google, and logs which license pushed to which platform and whether it succeeded. **The alert
text is not logged or stored.** Device tokens and license details are kept so that pushes can be
delivered; a token nobody has refreshed in thirty days is discarded.

You can turn the relay off at any time in your server's settings, in which case notifications
require your own Apple/Firebase credentials or are simply not sent.

## Permissions the apps ask for

- **Notifications** - to show alerts from your server.
- **Local network / Bonjour** - to find and talk to your server on your home network.
- **Camera** (Android only, optional) - to take a reference photo when enrolling a known face.
  Photos are sent to your server, not to the developer.

## Children

Sentinel Core is not directed at children and does not knowingly collect information from them.

## Changes and contact

Changes to this policy will be posted at this address with a new date. Questions:
**privacy@nr5p.com**.
