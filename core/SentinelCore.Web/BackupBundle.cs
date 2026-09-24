using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SentinelCore.Web;

// The full-backup file: the same BackupDocument the JSON export produces, plus the files that
// configuration only points at and a JSON document can't sensibly carry - organizer attachment
// bytes, known-face reference photos, the licence file, and this machine's HTTPS certificate
// authority and HomeKit pairing identity. It is what the nightly auto-export writes, and what the
// dashboard's "download full backup" button produces.
//
// Layout, inside a plain zip:
//   backup.json                    the BackupDocument
//   files/<kind>/<relative path>   one entry per file, kinds from BackupFileKinds
//
// Optionally encrypted with a passphrase, because this file holds every secret the install has
// and the whole point of the nightly export is to put a copy on another machine. The encrypted
// form is the zip, streamed through AES-256-GCM in 1 MiB chunks so an install with gigabytes of
// attachments never has to hold the file in memory:
//
//   "SCBACKUP" | version byte | iterations (int32 BE) | salt (16) | nonce prefix (4)
//   then per chunk: ciphertext length (int32 BE) | ciphertext | tag (16)
//
// Each chunk's nonce is the prefix plus its index, and its additional data says whether it is
// the last one - so reordering, dropping or truncating chunks all fail authentication rather than
// producing a zip that is quietly missing its end.
public static class BackupBundle
{
    public const string DocumentEntry = "backup.json";
    public const string FilesPrefix = "files/";

    private static readonly byte[] Magic = "SCBACKUP"u8.ToArray();
    private const byte FormatVersion = 1;
    private const int SaltLength = 16;
    private const int NoncePrefixLength = 4;
    private const int TagLength = 16;
    private const int KeyLength = 32;
    private const int ChunkSize = 1 << 20;

    // OWASP's current figure for PBKDF2-HMAC-SHA256. Stored in the header so it can be raised
    // later without making older backups unreadable.
    private const int Iterations = 600_000;

    private static readonly JsonSerializerOptions DocumentOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);

    public enum Format { Json, Zip, Encrypted, Unknown }

    public static Format Detect(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(Magic)) return Format.Encrypted;
        if (head.StartsWith("PK\u0003\u0004"u8)) return Format.Zip;

        // A JSON export - possibly with a byte-order mark or leading whitespace from an editor.
        foreach (var b in head)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0xEF or 0xBB or 0xBF) continue;
            return b == (byte)'{' ? Format.Json : Format.Unknown;
        }
        return Format.Unknown;
    }

    // Writes the zip to `output`. Each file source is opened only when it's copied in, so nothing
    // is held in memory beyond one buffer.
    public static async Task WriteZipAsync(
        Stream output, BackupDocument document, IEnumerable<BackupFile> files, CancellationToken cancellationToken)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        var documentEntry = zip.CreateEntry(DocumentEntry, CompressionLevel.Optimal);
        await using (var entryStream = documentEntry.Open())
            await JsonSerializer.SerializeAsync(entryStream, document, DocumentOptions, cancellationToken);

        foreach (var file in files)
        {
            // Photos and attachments are mostly already-compressed formats; deflating them again
            // costs time on a small box and saves nothing.
            var entry = zip.CreateEntry(FilesPrefix + file.EntryName, CompressionLevel.Fastest);
            entry.LastWriteTime = file.LastWriteTime;
            await using var entryStream = entry.Open();
            await using var source = file.Open();
            await source.CopyToAsync(entryStream, cancellationToken);
        }
    }

    public static async Task EncryptAsync(Stream plaintext, Stream output, string passphrase, CancellationToken cancellationToken)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var noncePrefix = RandomNumberGenerator.GetBytes(NoncePrefixLength);
        var key = DeriveKey(passphrase, salt, Iterations);

        var header = new byte[Magic.Length + 1 + 4 + SaltLength + NoncePrefixLength];
        Magic.CopyTo(header, 0);
        header[Magic.Length] = FormatVersion;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(Magic.Length + 1), Iterations);
        salt.CopyTo(header, Magic.Length + 5);
        noncePrefix.CopyTo(header, Magic.Length + 5 + SaltLength);
        await output.WriteAsync(header, cancellationToken);

        using var aes = new AesGcm(key, TagLength);
        var current = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        var ciphertext = new byte[ChunkSize];
        var tag = new byte[TagLength];
        var lengthPrefix = new byte[4];

        // One chunk of look-ahead, so the last chunk can be marked as last. An empty input still
        // produces one (empty) final chunk, so "nothing at all after the header" is a truncation.
        var currentLength = await ReadFullAsync(plaintext, current, cancellationToken);
        long index = 0;
        while (true)
        {
            var nextLength = currentLength == ChunkSize ? await ReadFullAsync(plaintext, next, cancellationToken) : 0;
            var isLast = nextLength == 0;

            aes.Encrypt(Nonce(noncePrefix, index), current.AsSpan(0, currentLength),
                ciphertext.AsSpan(0, currentLength), tag, isLast ? [1] : [0]);

            BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, currentLength);
            await output.WriteAsync(lengthPrefix, cancellationToken);
            await output.WriteAsync(ciphertext.AsMemory(0, currentLength), cancellationToken);
            await output.WriteAsync(tag, cancellationToken);

            if (isLast) break;
            (current, next) = (next, current);
            currentLength = nextLength;
            index++;
        }

        CryptographicOperations.ZeroMemory(key);
    }

    // Throws BackupBundleException with a message fit to show the user for a wrong passphrase or
    // a damaged file - the two can't be told apart, by design of the cipher, so the message says so.
    public static async Task DecryptAsync(Stream input, Stream output, string passphrase, CancellationToken cancellationToken)
    {
        var header = new byte[Magic.Length + 1 + 4 + SaltLength + NoncePrefixLength];
        if (await ReadFullAsync(input, header, cancellationToken) != header.Length || !header.AsSpan().StartsWith(Magic))
            throw new BackupBundleException("That file isn't an encrypted Sentinel Core backup.");
        if (header[Magic.Length] != FormatVersion)
            throw new BackupBundleException(
                $"That backup was encrypted by a newer version of Sentinel Core (format {header[Magic.Length]}) - update this install first.");

        var iterations = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(Magic.Length + 1));
        if (iterations is < 10_000 or > 100_000_000)
            throw new BackupBundleException("That encrypted backup's header is damaged.");
        var salt = header.AsSpan(Magic.Length + 5, SaltLength).ToArray();
        var noncePrefix = header.AsSpan(Magic.Length + 5 + SaltLength, NoncePrefixLength).ToArray();
        var key = DeriveKey(passphrase, salt, iterations);

        using var aes = new AesGcm(key, TagLength);
        var ciphertext = new byte[ChunkSize];
        var plaintext = new byte[ChunkSize];
        var tag = new byte[TagLength];
        var lengthPrefix = new byte[4];

        try
        {
            for (long index = 0; ; index++)
            {
                if (await ReadFullAsync(input, lengthPrefix, cancellationToken) != 4)
                    throw new BackupBundleException("That encrypted backup is incomplete - it ends before its last block.");
                var length = BinaryPrimitives.ReadInt32BigEndian(lengthPrefix);
                if (length is < 0 or > ChunkSize)
                    throw new BackupBundleException("That encrypted backup is damaged.");
                if (await ReadFullAsync(input, ciphertext.AsMemory(0, length), cancellationToken) != length ||
                    await ReadFullAsync(input, tag, cancellationToken) != TagLength)
                    throw new BackupBundleException("That encrypted backup is incomplete - it ends before its last block.");

                // Try as a middle chunk first, then as the last. Only a genuine last chunk
                // authenticates with the last-chunk flag, so a file cut off at a chunk boundary
                // can't pass for a complete one.
                var isLast = false;
                try
                {
                    aes.Decrypt(Nonce(noncePrefix, index), ciphertext.AsSpan(0, length), tag, plaintext.AsSpan(0, length), [0]);
                }
                catch (AuthenticationTagMismatchException)
                {
                    try
                    {
                        aes.Decrypt(Nonce(noncePrefix, index), ciphertext.AsSpan(0, length), tag, plaintext.AsSpan(0, length), [1]);
                        isLast = true;
                    }
                    catch (AuthenticationTagMismatchException)
                    {
                        throw new BackupBundleException(index == 0
                            ? "Wrong passphrase, or the file is damaged - it could not be decrypted."
                            : "That encrypted backup is damaged partway through - it could not be decrypted.");
                    }
                }

                await output.WriteAsync(plaintext.AsMemory(0, length), cancellationToken);
                if (isLast) break;
            }

            // Anything after the last chunk wasn't written by EncryptAsync.
            if (await input.ReadAsync(new byte[1], cancellationToken) != 0)
                throw new BackupBundleException("That encrypted backup has unexpected data after its end.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    // Opens a zip bundle and reads its document. The archive stays open for the caller to pull
    // files from; dispose it when done.
    public static async Task<(BackupDocument? Document, ZipArchive Archive)> OpenZipAsync(Stream zipStream, CancellationToken cancellationToken)
    {
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            throw new BackupBundleException("That backup file is damaged - it isn't a readable zip.");
        }

        var documentEntry = archive.GetEntry(DocumentEntry);
        if (documentEntry is null)
        {
            archive.Dispose();
            throw new BackupBundleException("That zip isn't a Sentinel Core backup - it has no backup.json inside.");
        }

        try
        {
            await using var stream = documentEntry.Open();
            var document = await JsonSerializer.DeserializeAsync<BackupDocument>(stream, ReadOptions, cancellationToken);
            return (document, archive);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            archive.Dispose();
            throw new BackupBundleException("The backup.json inside that zip is damaged.");
        }
    }

    public static IEnumerable<(string Kind, string RelativePath, ZipArchiveEntry Entry)> Files(ZipArchive archive)
    {
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(FilesPrefix, StringComparison.Ordinal) || entry.FullName.EndsWith('/')) continue;
            var rest = entry.FullName[FilesPrefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0) continue;

            var relative = rest[(slash + 1)..];
            // Never trust a path out of an archive: an entry named files/x/../../etc/passwd would
            // otherwise be written wherever it says. Anything that isn't a plain relative path
            // made of ordinary segments is skipped.
            if (!IsSafeRelativePath(relative)) continue;
            yield return (rest[..slash], relative, entry);
        }
    }

    public static bool IsSafeRelativePath(string path) =>
        path.Length > 0 &&
        !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':') &&
        path.Split('/').All(segment => segment.Length > 0 && segment != "." && segment != "..");

    private static byte[] DeriveKey(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, KeyLength);

    private static byte[] Nonce(byte[] prefix, long index)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), index);
        return nonce;
    }

    private static async Task<int> ReadFullAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}

// One file going into a bundle. Open is called once, while the zip is being written.
public sealed record BackupFile(string EntryName, Func<Stream> Open, DateTimeOffset LastWriteTime);

// The kinds of file a bundle carries - the first path segment under files/.
public static class BackupFileKinds
{
    public const string OrganizerBlob = "organizer";
    public const string KnownFacePhoto = "known-faces";
    public const string License = "license";
    public const string CertificateAuthority = "certs";
    public const string HomeKit = "homekit";
}

public sealed class BackupBundleException(string message) : Exception(message);
