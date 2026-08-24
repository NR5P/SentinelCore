using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace SentinelCore;

// The single place email actually leaves the building. Every notification path - alerts, the
// daily summary, outage and startup notices, login alerts, the nuisance advisor, bug reports and
// the settings "send test email" button - builds a System.Net.Mail.MailMessage and hands it here.
//
// It used to be nine copies of the same System.Net.Mail.SmtpClient block, which quietly assumed
// a Gmail-shaped account: STARTTLS on 587, and a username that is itself an email address. That
// left three kinds of provider unusable:
//
//   - implicit TLS on port 465 (Yandex, Mail.ru, most cPanel hosts). System.Net.Mail.SmtpClient
//     only ever speaks STARTTLS, so the connection sat there until it timed out.
//   - relays that want no authentication at all - a postfix on the same box, a corporate relay -
//     because credentials were handed over even when the username was blank.
//   - logins that aren't email addresses: Amazon SES (an IAM-style key), SendGrid (the literal
//     word "apikey"), Postmark, SMTP2GO.
//
// MailKit handles the first two; FromAddressFor and the settings validation handle the third.
public static class SmtpMailer
{
    // 465 is SMTPS: TLS from the first byte, no plaintext greeting to upgrade. Everything else in
    // common use (587, 25, 2525, Proton Bridge's 1025) starts in the clear and upgrades.
    public const int ImplicitTlsPort = 465;

    public static async Task SendAsync(EmailOptions options, MailMessage message, CancellationToken cancellationToken)
    {
        var mime = MimeMessage.CreateFromMailMessage(message);

        // CheckCertificateRevocation off is not a shortcut, it is the behaviour we already had:
        // System.Net.Mail never checked revocation lists, and MailKit turning it on by default
        // broke every provider tested from this machine - Gmail on both 587 and 465 - with
        // "an incomplete certificate revocation check occurred". A home server is exactly the
        // deployment that can't reliably reach an OCSP responder, and a soft-failing check is
        // worth very little anyway. The chain and hostname are still fully validated, which is
        // the part that actually stops someone else answering for your mail provider.
        using var client = new MailKit.Net.Smtp.SmtpClient
        {
            Timeout = options.TimeoutSeconds * 1000,
            CheckCertificateRevocation = false
        };
        await client.ConnectAsync(options.SmtpHost, options.SmtpPort, SecurityFor(options), cancellationToken);

        if (UsesAuthentication(options))
            await client.AuthenticateAsync(options.Username, options.AppPassword, cancellationToken);

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    // StartTls rather than StartTlsWhenAvailable: if the box says it can't encrypt, that should be
    // a visible failure, not a silent downgrade to sending credentials in the clear. Turning
    // Enable SSL off is the explicit way to ask for that, for a relay on the LAN that has no cert.
    public static SecureSocketOptions SecurityFor(EmailOptions options)
    {
        if (!options.EnableSsl) return SecureSocketOptions.None;
        return options.SmtpPort == ImplicitTlsPort
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTls;
    }

    // No username means an open relay, and offering to authenticate anyway is how you get a
    // "503 AUTH not available" out of one. A blank password with a username is left alone: a few
    // servers do accept it, and rejecting it here would just be a second opinion on their rules.
    public static bool UsesAuthentication(EmailOptions options) => !string.IsNullOrWhiteSpace(options.Username);

    // From Address wins; the username is only a fallback when it is itself an address. For SES or
    // SendGrid the username is a key, and quietly putting that in the From line built a message
    // no server would accept - the settings validation below requires a real From Address in
    // exactly that case, so an empty return here can only mean email is switched off entirely.
    public static string FromAddressFor(EmailOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.FromAddress)) return options.FromAddress;
        return LooksLikeEmailAddress(options.Username) ? options.Username : string.Empty;
    }

    public static bool LooksLikeEmailAddress(string value) =>
        !string.IsNullOrWhiteSpace(value) && new EmailAddressAttribute().IsValid(value);

    // Shared by Program.cs's startup validation and the settings PUT, so a config file edited by
    // hand and a form saved from a phone are held to the same rules - they were two lists that
    // had already drifted. One sentence back, or null when there is nothing wrong.
    //
    // Note what is deliberately not checked: the username is no longer required to look like an
    // email address (SES and SendGrid logins don't), and it may be blank entirely for a relay
    // that wants no login. What has to hold is that the message is addressable and the transport
    // is coherent.
    public static string? ValidateDeliverable(EmailOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SmtpHost))
            return "SmtpHost is required when email is enabled.";
        if (string.IsNullOrWhiteSpace(options.ToAddress))
            return "ToAddress is required when email is enabled.";
        if (!LooksLikeEmailAddress(options.ToAddress))
            return "ToAddress must be a valid email address.";
        if (string.IsNullOrWhiteSpace(FromAddressFor(options)))
            return "FromAddress is required, because the username is not itself an email address to fall back on.";
        if (!LooksLikeEmailAddress(FromAddressFor(options)))
            return "FromAddress must be a valid email address.";
        if (!string.IsNullOrWhiteSpace(options.Username) && string.IsNullOrWhiteSpace(options.AppPassword))
            return "AppPassword is required when a username is set. Leave both blank for a relay that wants no login.";
        if (options.SmtpPort == ImplicitTlsPort && !options.EnableSsl)
            return "EnableSsl must be on for port 465, which is encrypted from the very first byte. Use port 587 for a STARTTLS or unencrypted connection.";
        return null;
    }
}

// Registered in Program.cs against AddOptions<EmailOptions>().ValidateOnStart(), so a bad Email
// section stops the server at boot with the same sentence the settings page would have shown.
public sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        if (!options.Enabled) return ValidateOptionsResult.Success;
        var error = SmtpMailer.ValidateDeliverable(options);
        return error is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail($"Email:{error}");
    }
}
