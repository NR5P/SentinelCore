using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SentinelCore.Licensing;

namespace LicenseGenerator.Gui;

public partial class MainWindow : Window
{
    private static readonly IBrush GoodBackground = new SolidColorBrush(Color.Parse("#3327AE60"));
    private static readonly IBrush WarnBackground = new SolidColorBrush(Color.Parse("#33E8B339"));
    private static readonly IBrush BadBackground = new SolidColorBrush(Color.Parse("#33FF6B6B"));

    private readonly AppState _state = AppState.Load();
    private SigningKey? _key;
    private string? _lastIssuedPath;

    // Set once the filename has been hand-edited, after which the customer name stops driving it -
    // silently renaming a file someone has deliberately named is the kind of small betrayal that
    // makes a tool feel untrustworthy.
    private bool _fileNameEditedByHand;

    public MainWindow()
    {
        InitializeComponent();

        BrowseKeyButton.Click += async (_, _) => await ChooseSigningKeyAsync();
        BrowseOutputButton.Click += async (_, _) => await ChooseOutputDirectoryAsync();
        IssueButton.Click += (_, _) => IssueLicense();
        RevealButton.Click += (_, _) => RevealLastIssued();
        CopyPathButton.Click += async (_, _) => await CopyToClipboardAsync(_lastIssuedPath);

        VerifyBrowseButton.Click += async (_, _) => await VerifyLicenseAsync();

        BrowseKeyGenButton.Click += async (_, _) => await ChooseKeyGenDirectoryAsync();
        // The confirmation gate is enforced inside the handler rather than by disabling the
        // button. Avalonia throws ElementNotEnabledException when a disabled button is activated
        // through the platform accessibility API, and that lands on the UI thread unhandled - so
        // a disabled button is a way for a VoiceOver user to kill the app, not a safety feature.
        // Refusing in the handler is also plainly better UX: it says why nothing happened.
        GenerateKeysButton.Click += (_, _) => GenerateKeys();
        CopyPemButton.Click += async (_, _) => await CopyToClipboardAsync(KeyGenPublicPem.Text);

        CustomerBox.TextChanged += (_, _) => SyncFileNameToCustomer();
        FileNameBox.TextChanged += (_, _) =>
        {
            if (FileNameBox.IsFocused) _fileNameEditedByHand = true;
        };

        RestoreState();
    }


    private void RestoreState()
    {
        ProductBox.Text = string.IsNullOrWhiteSpace(_state.Product) ? "SentinelCore" : _state.Product;

        var defaultOutput = _state.OutputDirectory is { Length: > 0 } saved && Directory.Exists(saved)
            ? saved
            : DefaultOutputDirectory();
        OutputDirBox.Text = defaultOutput;
        KeyGenDirBox.Text = defaultOutput;

        // A remembered key is re-read rather than trusted: it may have been moved to a USB stick
        // and unplugged since the last sale, which is exactly what you'd want it to say.
        if (_state.SigningKeyPath is { Length: > 0 } keyPath && File.Exists(keyPath))
            LoadSigningKey(keyPath);
    }

    private static string DefaultOutputDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = Path.Combine(home, "Desktop");
        return Directory.Exists(desktop) ? desktop : home;
    }

    private async Task ChooseSigningKeyAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose your license signing key",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("PEM key") { Patterns = ["*.pem"] },
                FilePickerFileTypes.All
            ]
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            LoadSigningKey(path);
    }

    private void LoadSigningKey(string path)
    {
        try
        {
            _key = SigningKey.Load(path);
        }
        catch (SigningKeyException ex)
        {
            _key = null;
            KeyPathBox.Text = path;
            ShowKeyStatus(BadBackground, ex.Message);
            return;
        }

        KeyPathBox.Text = path;
        _state.SigningKeyPath = path;
        _state.Save();

        // The fingerprint mismatch warning is the point of this whole screen. A license signed
        // with the wrong key is indistinguishable from a good one until a customer installs it.
        if (_key.MatchesShippedPublicKey)
        {
            ShowKeyStatus(GoodBackground,
                $"Matches the public key built into SentinelCore. Licenses signed with this key will verify.\n{_key.Fingerprint}");
        }
        else
        {
            ShowKeyStatus(WarnBackground,
                "This key does NOT match the public key built into SentinelCore. It will still produce a " +
                "signed .lic file, but no install will accept it.\n" +
                $"This key:  {_key.Fingerprint}\n" +
                $"Shipped:   {SigningKey.ShippedPublicKeyFingerprint}");
        }
    }

    private void ShowKeyStatus(IBrush background, string message)
    {
        KeyStatusBorder.Background = background;
        KeyStatusBorder.IsVisible = true;
        KeyStatusText.Text = message;
    }

    private async Task ChooseOutputDirectoryAsync()
    {
        var chosen = await PickFolderAsync("Where should the .lic files go?", OutputDirBox.Text);
        if (chosen is null) return;

        OutputDirBox.Text = chosen;
        _state.OutputDirectory = chosen;
        _state.Save();
    }

    private async Task ChooseKeyGenDirectoryAsync()
    {
        var chosen = await PickFolderAsync("Where should the new key pair go?", KeyGenDirBox.Text);
        if (chosen is not null) KeyGenDirBox.Text = chosen;
    }

    private async Task<string?> PickFolderAsync(string title, string? startAt)
    {
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (startAt is { Length: > 0 } && Directory.Exists(startAt))
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(startAt);

        var folders = await StorageProvider.OpenFolderPickerAsync(options);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private void SyncFileNameToCustomer()
    {
        if (_fileNameEditedByHand) return;

        var slug = Slugify(CustomerBox.Text ?? string.Empty);
        FileNameBox.Text = slug.Length == 0 ? string.Empty : $"{slug}.lic";
    }

    private void IssueLicense()
    {
        if (_key is null)
        {
            ShowIssueResult(BadBackground, "Choose your signing key first.");
            return;
        }

        var customer = (CustomerBox.Text ?? string.Empty).Trim();
        var email = (EmailBox.Text ?? string.Empty).Trim();
        if (customer.Length == 0 || email.Length == 0)
        {
            ShowIssueResult(BadBackground, "A license needs both a customer name and an email address.");
            return;
        }

        var directory = (OutputDirBox.Text ?? string.Empty).Trim();
        var fileName = (FileNameBox.Text ?? string.Empty).Trim();
        if (fileName.Length == 0) fileName = $"{Slugify(customer)}.lic";
        if (!fileName.EndsWith(".lic", StringComparison.OrdinalIgnoreCase)) fileName += ".lic";

        var outputPath = Path.Combine(directory, fileName);
        if (File.Exists(outputPath))
        {
            // Overwriting silently would quietly replace a license already sent to someone - the
            // file name is derived from the customer's name, so a repeat sale collides by design.
            ShowIssueResult(WarnBackground,
                $"{fileName} already exists in that folder. Rename it above so an existing license isn't overwritten.");
            return;
        }

        LicenseInfo issued;
        try
        {
            issued = _key.Issue(customer, email, ProductBox.Text ?? "SentinelCore", outputPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SigningKeyException)
        {
            ShowIssueResult(BadBackground, $"Couldn't write the license: {ex.Message}");
            return;
        }

        _state.Product = ProductBox.Text;
        _state.Save();
        _lastIssuedPath = outputPath;

        var warning = _key.MatchesShippedPublicKey
            ? string.Empty
            : "\n\nWARNING: signed with a key that doesn't match the shipped public key - no install will accept this file.";

        ShowIssueResult(_key.MatchesShippedPublicKey ? GoodBackground : WarnBackground,
            $"Issued for {issued.CustomerName} <{issued.CustomerEmail}>\n" +
            $"License {issued.LicenseId}\n" +
            $"Saved to {outputPath}\n\n" +
            "Email this file to the customer. They install it from Settings in their dashboard, " +
            "or save it as license.lic next to appsettings.json." + warning);

        RevealButton.IsVisible = true;
        CopyPathButton.IsVisible = true;
    }

    private void ShowIssueResult(IBrush background, string message)
    {
        IssueResultBorder.Background = background;
        IssueResultBorder.IsVisible = true;
        IssueResultText.Text = message;
        RevealButton.IsVisible = _lastIssuedPath is not null;
        CopyPathButton.IsVisible = _lastIssuedPath is not null;
    }

    private async Task VerifyLicenseAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a license to check",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("License") { Patterns = ["*.lic"] },
                FilePickerFileTypes.All
            ]
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;

        string contents;
        try
        {
            contents = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowVerifyResult(BadBackground, $"Couldn't read that file: {ex.Message}");
            return;
        }

        using var publicKey = System.Security.Cryptography.RSA.Create();
        publicKey.ImportFromPem(LicensePublicKey.Pem);
        var info = LicenseVerifier.Verify(contents, publicKey);

        if (info is null)
        {
            ShowVerifyResult(BadBackground,
                $"{Path.GetFileName(path)}\n\nNot valid. Either it wasn't signed by the key SentinelCore " +
                "verifies against, or the file has been edited or corrupted since it was signed.");
            return;
        }

        ShowVerifyResult(GoodBackground,
            $"{Path.GetFileName(path)}\n\nValid.\n\n" +
            $"Customer  {info.CustomerName}\n" +
            $"Email     {info.CustomerEmail}\n" +
            $"Product   {info.Product}\n" +
            $"License   {info.LicenseId}\n" +
            $"Issued    {info.IssuedAt:u}");
    }

    private void ShowVerifyResult(IBrush background, string message)
    {
        VerifyResultBorder.Background = background;
        VerifyResultBorder.IsVisible = true;
        VerifyResultText.Text = message;
    }

    private void GenerateKeys()
    {
        if (KeyGenConfirmCheck.IsChecked != true)
        {
            KeyGenResultBorder.Background = WarnBackground;
            KeyGenResultBorder.IsVisible = true;
            KeyGenResultText.Text = "Tick the box above first. Replacing the signing key invalidates every license you've already issued.";
            KeyGenPublicPem.Text = string.Empty;
            CopyPemButton.IsVisible = false;
            return;
        }

        try
        {
            var pair = KeyPairFactory.Generate(KeyGenDirBox.Text ?? DefaultOutputDirectory());

            KeyGenResultBorder.Background = WarnBackground;
            KeyGenResultBorder.IsVisible = true;
            KeyGenResultText.Text =
                $"Wrote {pair.PrivateKeyPath} and {pair.PublicKeyPath}.\n\n" +
                "Back up the private key somewhere safe and offline. It must never be committed to git " +
                "or leave this machine - if it's lost you can't issue another valid license without " +
                "shipping a new public key to every past customer.\n\n" +
                "Paste the public key below into SentinelCore.Licensing/LicensePublicKey.cs as Pem, then " +
                "rebuild and redeploy before issuing anything against this key.";
            KeyGenPublicPem.Text = pair.PublicKeyPem;
            CopyPemButton.IsVisible = true;
        }
        catch (Exception ex) when (ex is SigningKeyException or IOException or UnauthorizedAccessException)
        {
            KeyGenResultBorder.Background = BadBackground;
            KeyGenResultBorder.IsVisible = true;
            KeyGenResultText.Text = ex.Message;
            KeyGenPublicPem.Text = string.Empty;
            CopyPemButton.IsVisible = false;
        }
    }

    private void RevealLastIssued()
    {
        if (_lastIssuedPath is null) return;

        try
        {
            if (OperatingSystem.IsMacOS())
                Process.Start("open", ["-R", _lastIssuedPath]);
            else if (OperatingSystem.IsLinux())
                Process.Start("xdg-open", [Path.GetDirectoryName(_lastIssuedPath) ?? "."]);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            // A missing file manager isn't worth an error dialog - the full path is on screen
            // right above this button.
        }
    }

    private async Task CopyToClipboardAsync(string? text)
    {
        if (string.IsNullOrEmpty(text) || Clipboard is null) return;
        await Clipboard.SetTextAsync(text);
    }

    // Matches the CLI's naming exactly, so the same customer produces the same file name whichever
    // tool issued it.
    private static string Slugify(string value)
    {
        var slug = new string(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        return string.Join('-', slug.Split('-', StringSplitOptions.RemoveEmptyEntries));
    }
}
