using System.Security.Cryptography;
using SentinelCore.Licensing;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var command = args[0];
var flags = ParseFlags(args.AsSpan(1));

switch (command)
{
    case "generate-keys":
        return GenerateKeys(flags);
    case "issue":
        return Issue(flags);
    default:
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintUsage();
        return 1;
}

static int GenerateKeys(Dictionary<string, string> flags)
{
    var outputDir = flags.GetValueOrDefault("output-dir", ".");
    Directory.CreateDirectory(outputDir);

    var privatePath = Path.Combine(outputDir, "private.pem");
    var publicPath = Path.Combine(outputDir, "public.pem");

    if (File.Exists(privatePath))
    {
        Console.Error.WriteLine($"{privatePath} already exists - refusing to overwrite an existing key pair.");
        Console.Error.WriteLine("Generating a new pair would invalidate every license you've already issued.");
        return 1;
    }

    using var rsa = RSA.Create(3072);
    File.WriteAllText(privatePath, rsa.ExportPkcs8PrivateKeyPem());
    File.WriteAllText(publicPath, rsa.ExportSubjectPublicKeyInfoPem());

    Console.WriteLine($"Wrote {privatePath} and {publicPath}.");
    Console.WriteLine();
    Console.WriteLine("=== IMPORTANT ===");
    Console.WriteLine($"{privatePath} is the root of trust for every license you will ever issue.");
    Console.WriteLine("Back it up somewhere safe and offline. It must never be committed to git,");
    Console.WriteLine("uploaded anywhere, or leave this machine. If it's lost, you can never issue");
    Console.WriteLine("another valid license without shipping a new public key to every past customer.");
    Console.WriteLine();
    Console.WriteLine("Paste the contents below into SentinelCore.Licensing/LicensePublicKey.cs as Pem:");
    Console.WriteLine();
    Console.WriteLine(File.ReadAllText(publicPath));
    return 0;
}

static int Issue(Dictionary<string, string> flags)
{
    if (!flags.TryGetValue("key", out var keyPath))
    {
        Console.Error.WriteLine("Missing --key <path to private.pem>.");
        return 1;
    }
    if (!flags.TryGetValue("customer", out var customer))
    {
        Console.Error.WriteLine("Missing --customer \"Name\".");
        return 1;
    }
    if (!flags.TryGetValue("email", out var email))
    {
        Console.Error.WriteLine("Missing --email you@example.com.");
        return 1;
    }
    if (!File.Exists(keyPath))
    {
        Console.Error.WriteLine($"No such file: {keyPath}");
        return 1;
    }

    var product = flags.GetValueOrDefault("product", "SentinelCore");
    var licenseId = Guid.NewGuid();
    var output = flags.GetValueOrDefault("output", $"{Slugify(customer)}.lic");

    using var rsa = RSA.Create();
    rsa.ImportFromPem(File.ReadAllText(keyPath));

    var info = new LicenseInfo(customer, email, product, licenseId, DateTimeOffset.UtcNow);
    var licenseText = LicenseSigner.Sign(info, rsa);
    File.WriteAllText(output, licenseText);

    Console.WriteLine($"Issued license {licenseId} for {customer} <{email}> -> {output}");
    Console.WriteLine("Email this file to the customer. They drop it in as license.lic next to appsettings.json.");
    return 0;
}

static string Slugify(string value)
{
    var slug = new string(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
    return string.Join('-', slug.Split('-', StringSplitOptions.RemoveEmptyEntries));
}

static Dictionary<string, string> ParseFlags(ReadOnlySpan<string> args)
{
    var flags = new Dictionary<string, string>();
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (!arg.StartsWith("--", StringComparison.Ordinal)) continue;

        var name = arg[2..];
        var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
        flags[name] = hasValue ? args[++i] : "true";
    }
    return flags;
}

static void PrintUsage()
{
    Console.WriteLine("""
        Usage:
          LicenseGenerator generate-keys [--output-dir <dir>]
              One-time setup. Creates private.pem and public.pem.

          LicenseGenerator issue --key <private.pem> --customer "Name" --email you@example.com [--output file.lic] [--product "SentinelCore"]
              Run once per sale. Writes a signed .lic file to email to the customer.
        """);
}
