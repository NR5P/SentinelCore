# LicenseGenerator

A local-only tool for issuing signed licenses. Nothing in here is deployed - this never runs
anywhere except your own machine.

## One-time setup

```bash
dotnet run --project LicenseGenerator -- generate-keys --output-dir ~/sentinelcore-license-keys
```

This creates `private.pem` and `public.pem`. Back up `private.pem` somewhere safe and offline -
it's the root of trust for every license you'll ever issue, it must never be committed to git or
leave your machine, and if it's lost you can't issue another valid license without shipping a
new public key to every past customer.

Paste the printed public key into `SentinelCore.Licensing/LicensePublicKey.cs` (`Pem`) once,
then rebuild and deploy the app as normal. This only needs to happen once, ever. Both the app and
the push relay (`SentinelCore.PushRelay`, see `docs/PUSH_RELAY.md`) verify against that one
constant - the relay accepts a license as its callers' credential, so every issued license also
entitles that install to push through the relay.

## Per sale

```bash
dotnet run --project LicenseGenerator -- issue \
  --key ~/sentinelcore-license-keys/private.pem \
  --customer "Jane Doe" \
  --email jane@example.com \
  --output jane-doe.lic
```

Email the resulting `.lic` file to the customer. They save it as `license.lic` next to their
`appsettings.json`.

There's no expiry and no machine binding - a license is a one-time-purchase, perpetual, signed
statement of who it was issued to. It's not a functional lock: an invalid or missing license
shows a notice in the customer's dashboard, but never stops the app from working. The signature
is what actually matters - it can't be forged or edited without the private key.
