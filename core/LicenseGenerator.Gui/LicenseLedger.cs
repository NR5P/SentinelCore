using System.Text.Json;
using SentinelCore.Licensing;

namespace LicenseGenerator.Gui;

public sealed record IssuedLicense(
    Guid LicenseId,
    string CustomerName,
    string CustomerEmail,
    string Product,
    DateTimeOffset IssuedAt,
    string? FilePath)
{
    public override string ToString() => $"{CustomerName} <{CustomerEmail}> - issued {IssuedAt:yyyy-MM-dd}";
}

// Every license this machine has issued or verified: who it's for and its id. Nothing else keeps
// that list - licenses are verified offline and never phone home - and without it a refund or a
// leaked file means hunting through sent mail for the .lic to find out what to block.
//
// Kept beside AppState. Unlike AppState it is a record rather than a convenience, so a file that
// fails to read is left alone rather than silently replaced with an empty list.
public sealed class LicenseLedger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly List<IssuedLicense> _entries;

    private LicenseLedger(List<IssuedLicense> entries, string? loadError)
    {
        _entries = entries;
        LoadError = loadError;
    }

    public string? LoadError { get; }

    public IReadOnlyList<IssuedLicense> Entries => _entries;

    public static string LedgerPath
    {
        get
        {
            var config = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(config, "SentinelCore", "issued-licenses.json");
        }
    }

    public static LicenseLedger Load()
    {
        if (!File.Exists(LedgerPath)) return new LicenseLedger([], null);
        try
        {
            var entries = JsonSerializer.Deserialize<List<IssuedLicense>>(File.ReadAllText(LedgerPath), JsonOptions) ?? [];
            return new LicenseLedger(entries, null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new LicenseLedger([], $"Couldn't read your license records at {LedgerPath} ({ex.Message}). Nothing will be recorded until that file is fixed or moved aside.");
        }
    }

    public IssuedLicense? Find(Guid licenseId) => _entries.FirstOrDefault(e => e.LicenseId == licenseId);

    // Returns whether it was new. Re-recording a license already on the list (verifying it again)
    // keeps the original entry.
    public bool Record(LicenseInfo license, string? filePath)
    {
        if (LoadError is not null || Find(license.LicenseId) is not null) return false;
        _entries.Add(new IssuedLicense(license.LicenseId, license.CustomerName, license.CustomerEmail, license.Product, license.IssuedAt, filePath));
        _entries.Sort((a, b) => b.IssuedAt.CompareTo(a.IssuedAt));
        Save();
        return true;
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LedgerPath)!);
        var temporary = LedgerPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_entries, JsonOptions));
        File.Move(temporary, LedgerPath, overwrite: true);
    }
}
