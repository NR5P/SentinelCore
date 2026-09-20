# SentinelCore Licenses (GUI)

A window for the job `LicenseGenerator` does on the command line: signing a `.lic` file for a
customer who has paid. Same key, same output, same `SentinelCore.Licensing` code underneath - a
license issued here and one issued by the CLI are indistinguishable.

Like the CLI, **this is never shipped to a customer.** It's the one tool that touches the private
signing key, so it only ever runs on the machine you sell from. `scripts/publish-release.sh`
publishes `SentinelCore` and `SentinelCore.Web` by name and never picks this up.

## Building it

```bash
./scripts/build-license-gui.sh
```

On macOS that produces `dist/license-gui/osx-arm64/SentinelCore Licenses.app` - drag it to
/Applications and it behaves like any other Mac app. On Linux you get a folder with a
`sentinelcore-licenses` launcher script. Both are self-contained: the machine running it doesn't
need the .NET SDK or runtime installed.

Pass a runtime identifier to cross-build, e.g. `./scripts/build-license-gui.sh linux-x64`.

For development, `dotnet run --project LicenseGenerator.Gui` works too - it just launches as an
unbundled process, which on macOS means it shows up as "Avalonia Application" in the menu bar.

## Issuing a license

1. **Choose your signing key** - the `private.pem` you sign licenses with. Remembered after the
   first time, so this is a one-off.
2. Fill in the customer's name and email.
3. Press **Issue license**. The file name is derived from the customer's name automatically.

Then email the `.lic` to the customer. They install it themselves from **Settings** in their
dashboard (which verifies it before writing it to disk), or save it as `license.lic` next to
their `appsettings.json`.

### The fingerprint check

When a key is selected, the app compares it against `LicensePublicKey.Pem` - the public half
compiled into the server and the push relay - and says plainly whether they match.

This is the reason the GUI exists rather than just the CLI. A license signed with the wrong key
is a perfectly well-formed, correctly signed file. Nothing about it looks wrong until a customer
installs it and it silently fails to verify. The CLI cannot catch that before you've emailed it;
a window that is already holding the key can.

The fingerprint shown is the SHA-256 of the key's DER `SubjectPublicKeyInfo`, so it can be checked
by hand against the key itself:

```bash
openssl pkey -in private.pem -pubout -outform DER | shasum -a 256
```

## The other two tabs

**Verify** checks any `.lic` against the same public key the customer's server uses - useful
before emailing one, or to work out why one a customer sent back isn't being accepted.

**Key setup** generates a new key pair, and is deliberately awkward to reach. SentinelCore already
has a signing key; generating another doesn't add one, it replaces it, and every license already
issued stops verifying the moment the new public key ships. It refuses to overwrite an existing
`private.pem`, and writes new ones `0600`.

## Where settings live

The chosen key path, output folder and product name are remembered in
`~/Library/Application Support/SentinelCore/license-generator-gui.json` on macOS, and
`~/.config/SentinelCore/license-generator-gui.json` on Linux. Paths only - no key material.

## Notes for anyone editing this

- The signing logic lives in `SigningKey.cs` and `KeyPairFactory.cs`, deliberately free of any
  Avalonia types so it can be exercised without a window.
- Every control carries an `AutomationProperties.Name`. That's for screen readers first, but it's
  also the only practical way to drive this app from a test script.
- Don't gate a destructive action behind `IsEnabled="False"`. Avalonia throws
  `ElementNotEnabledException` when a disabled control is activated through the platform
  accessibility API, and on macOS that exception is unhandled and terminates the app - a disabled
  button is a way for a VoiceOver user to crash it. Check the condition inside the handler and say
  why nothing happened (see `GenerateKeys`). There is no global backstop available either:
  subscribing to `Dispatcher.UIThread.UnhandledException` throws `PlatformNotSupportedException`
  on macOS, because the native run loop drives the dispatcher rather than the reverse.
