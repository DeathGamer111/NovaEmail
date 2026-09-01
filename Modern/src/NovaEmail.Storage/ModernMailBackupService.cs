using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using NovaEmail.Safety;

namespace NovaEmail.Storage;

public static class ModernMailBackupService
{
    private const int CurrentSchemaVersion = 1;
    private const long MaximumManifestBytes = 16L * 1024L * 1024L;
    private const int MaximumManifestEntries = 250_000;
    private const long MaximumContentBlobBytes = 100L * 1024L * 1024L;
    private const long MaximumDatabaseBytes = 1024L * 1024L * 1024L * 1024L;
    private const long MaximumBackupBytes = 8L * 1024L * 1024L * 1024L * 1024L;
    private const long RestoreFreeSpaceReserveBytes = 128L * 1024L * 1024L;
    private const string DatabaseRelativePath = "store.sqlite";
    private const string ManifestFileName = "backup-manifest.json";
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public static async Task<ModernStoreBackupResult> CreateProtectedAsync(
        ModernMailStore store,
        ManagedWriteDestination backupDestination,
        CancellationToken cancellationToken = default)
        => await CreateProtectedAsync(
            store, backupDestination, new WindowsBitLockerProtectionProbe(), cancellationToken)
            .ConfigureAwait(false);

    internal static async Task<ModernStoreBackupResult> CreateProtectedAsync(
        ModernMailStore store,
        ManagedWriteDestination backupDestination,
        IBitLockerProtectionProbe bitLocker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(backupDestination);
        ArgumentNullException.ThrowIfNull(bitLocker);
        EnsurePilotProtectedRoot(
            store.ApprovedDataRoot,
            "The active modern store is not protected for backup.",
            bitLocker);
        var destination = ValidateNewLocalRoot(backupDestination.ApprovedDataRoot);
        var riskAcceptance = EnsureProtectedDestinationVolume(destination, bitLocker);
        return await CreateAsyncCore(
            store,
            destination,
            WindowsProtectedDirectoryBoundary.Instance,
            riskAcceptance,
            bitLocker,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ModernStoreRestoreResult> RestoreProtectedAsync(
        string backupRoot,
        ManagedWriteDestination restoreDestination,
        CancellationToken cancellationToken = default)
        => await RestoreProtectedAsync(
            backupRoot, restoreDestination, new WindowsBitLockerProtectionProbe(), cancellationToken)
            .ConfigureAwait(false);

    internal static async Task<ModernStoreRestoreResult> RestoreProtectedAsync(
        string backupRoot,
        ManagedWriteDestination restoreDestination,
        IBitLockerProtectionProbe bitLocker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(restoreDestination);
        ArgumentNullException.ThrowIfNull(bitLocker);
        var source = ValidateExistingLocalRoot(backupRoot);
        EnsurePilotProtectedRoot(
            source,
            "The selected Nova Email backup is not protected for restore.",
            bitLocker);
        var destination = ValidateNewLocalRoot(restoreDestination.ApprovedDataRoot);
        var riskAcceptance = EnsureProtectedDestinationVolume(destination, bitLocker);
        return await RestoreAsyncCore(
            source,
            destination,
            WindowsProtectedDirectoryBoundary.Instance,
            riskAcceptance,
            bitLocker,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<ModernStoreBackupResult> CreateAsync(
        ModernMailStore store,
        string backupRoot,
        CancellationToken cancellationToken = default)
        => await CreateAsync(
            store, backupRoot, WindowsProtectedDirectoryBoundary.Instance, cancellationToken)
            .ConfigureAwait(false);

    internal static async Task<ModernStoreBackupResult> CreateAsync(
        ModernMailStore store,
        string backupRoot,
        IProtectedDirectoryBoundary protectedDirectories,
        CancellationToken cancellationToken = default)
        => await CreateAsyncCore(
            store,
            backupRoot,
            protectedDirectories,
            riskAcceptance: null,
            pilotBitLocker: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

    private static async Task<ModernStoreBackupResult> CreateAsyncCore(
        ModernMailStore store,
        string backupRoot,
        IProtectedDirectoryBoundary protectedDirectories,
        ValidatedRiskAcceptanceAttestation? riskAcceptance,
        IBitLockerProtectionProbe? pilotBitLocker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(protectedDirectories);
        var destination = ValidateNewLocalRoot(backupRoot);
        EnsureSeparateTrees(store.DataRoot, destination, "Backup output must be separate from modern mail storage.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + $".partial.{Guid.NewGuid():N}";
        IProtectedDirectoryLease? staging = null;
        try
        {
            staging = protectedDirectories.CreateExclusive(partial);
            staging.Verify(partial);
            var databaseBackupPath = Path.Combine(partial, DatabaseRelativePath);
            EnsureNoReparsePath(store.DataRoot, store.DatabasePath);
            await BackupDatabaseAsync(store.DatabasePath, databaseBackupPath, cancellationToken)
                .ConfigureAwait(false);
            var databaseEntry = await DescribeFileAsync(
                databaseBackupPath, DatabaseRelativePath, "sqlite", cancellationToken).ConfigureAwait(false);
            if (databaseEntry.Length is <= 0 or > MaximumDatabaseBytes)
                throw new InvalidDataException("SQLite backup exceeds its bounded file-size contract.");
            var entries = new List<BackupManifestEntry> { databaseEntry };
            var catalog = new List<ContentCatalogEntry>();
            var manifestPaths = new HashSet<string>(StringComparer.Ordinal) { DatabaseRelativePath };
            long declaredBytes = databaseEntry.Length;
            await using (var snapshot = await OpenReadOnlyAsync(databaseBackupPath, cancellationToken)
                .ConfigureAwait(false))
            {
                await using var command = snapshot.CreateCommand();
                command.CommandText =
                    "SELECT sha256, byte_count, relative_path FROM content_blobs ORDER BY relative_path;";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var expectedHash = reader.GetString(0);
                    var expectedBytes = reader.GetInt64(1);
                    var contentRelativePath = reader.GetString(2);
                    if (catalog.Count >= MaximumManifestEntries - 1 ||
                        expectedBytes is < 0 or > MaximumContentBlobBytes ||
                        expectedHash.Length != 64 || expectedHash.Any(character =>
                            character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                        throw new InvalidDataException(
                            "Content catalog exceeds the bounded backup contract.");
                    var sourcePath = ResolveBeneath(
                        Path.Combine(store.DataRoot, "content"), contentRelativePath);
                    EnsureNoReparsePath(Path.Combine(store.DataRoot, "content"), sourcePath);
                    var sourceInfo = new FileInfo(sourcePath);
                    if (!sourceInfo.Exists || (sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0 ||
                        sourceInfo.Length != expectedBytes)
                        throw new InvalidDataException(
                            "Content-addressed source length/type does not match its database record.");
                    var manifestRelativePath = NormalizeRelative(
                        Path.Combine("content", contentRelativePath));
                    if (manifestRelativePath.Length > 1_024 || !manifestPaths.Add(manifestRelativePath) ||
                        expectedBytes > MaximumBackupBytes - declaredBytes)
                        throw new InvalidDataException(
                            "Content catalog contains duplicate paths or exceeds aggregate backup limits.");
                    declaredBytes += expectedBytes;
                    catalog.Add(new ContentCatalogEntry(
                        expectedHash, expectedBytes, sourcePath, manifestRelativePath));
                }
            }
            EnsureBackupCapacity(partial, declaredBytes - databaseEntry.Length);
            foreach (var item in catalog)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destinationPath = ResolveBeneath(partial, item.ManifestRelativePath);
                var copied = await CopyAndHashNewAsync(
                    item.SourcePath, destinationPath, item.ManifestRelativePath, "content", cancellationToken)
                    .ConfigureAwait(false);
                if (copied.Length != item.Length ||
                    !string.Equals(copied.Sha256, item.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException("Content-addressed source does not match its database record.");
                entries.Add(copied);
            }

            var manifest = new BackupManifest(
                CurrentSchemaVersion, DateTimeOffset.UtcNow, DatabaseRelativePath,
                entries.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray());
            _ = ValidateManifest(manifest);
            var manifestPath = Path.Combine(partial, ManifestFileName);
            await using (var output = new FileStream(
                manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    output, manifest, SerializerOptions, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (new FileInfo(manifestPath).Length > MaximumManifestBytes)
                throw new InvalidDataException("Generated backup manifest exceeds its size limit.");
            var manifestHash = await HashFileAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            if (riskAcceptance is not null)
            {
                await WriteRiskAcceptanceAsync(partial, riskAcceptance, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (pilotBitLocker is not null)
            {
                EnsurePilotProtectedRoot(
                    partial,
                    "The completed Nova Email backup staging root did not retain its offline protection boundary.",
                    pilotBitLocker);
            }
            staging.Verify(partial);
            staging.Publish(destination);
            return new ModernStoreBackupResult(
                destination, manifestHash, entries.Count, entries.Sum(entry => entry.Length));
        }
        finally
        {
            if (staging is not null)
            {
                _ = staging.CleanupIfOwned();
                staging.Dispose();
            }
        }
    }

    internal static async Task<ModernStoreRestoreResult> RestoreAsync(
        string backupRoot,
        string destinationDataRoot,
        CancellationToken cancellationToken = default)
        => await RestoreAsync(
            backupRoot, destinationDataRoot,
            WindowsProtectedDirectoryBoundary.Instance, cancellationToken)
            .ConfigureAwait(false);

    internal static async Task<ModernStoreRestoreResult> RestoreAsync(
        string backupRoot,
        string destinationDataRoot,
        IProtectedDirectoryBoundary protectedDirectories,
        CancellationToken cancellationToken = default)
        => await RestoreAsyncCore(
            backupRoot,
            destinationDataRoot,
            protectedDirectories,
            riskAcceptance: null,
            pilotBitLocker: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

    private static async Task<ModernStoreRestoreResult> RestoreAsyncCore(
        string backupRoot,
        string destinationDataRoot,
        IProtectedDirectoryBoundary protectedDirectories,
        ValidatedRiskAcceptanceAttestation? riskAcceptance,
        IBitLockerProtectionProbe? pilotBitLocker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(protectedDirectories);
        var source = ValidateExistingLocalRoot(backupRoot);
        var destination = ValidateNewLocalRoot(destinationDataRoot);
        EnsureSeparateTrees(source, destination, "Restore destination must be separate from its backup source.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var manifestPath = ResolveBeneath(source, ManifestFileName);
        var manifestHashBefore = await HashFileAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var manifest = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var declaredTotalBytes = ValidateManifest(manifest);
        var expectedFiles = manifest.Entries
            .ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);
        ValidateOptionalRiskAcceptanceMetadata(source);
        var actualFiles = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(path => NormalizeRelative(Path.GetRelativePath(source, path)))
            .Where(path => !path.Equals(ManifestFileName, StringComparison.Ordinal) &&
                !path.Equals(
                    FileUnencryptedVolumeRiskAcceptanceProbe.FileName,
                    StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!actualFiles.SequenceEqual(expectedFiles.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("Backup contains missing or unmanifested files.");
        await PreflightRestoreSourcesAsync(source, manifest.Entries, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                manifestHashBefore,
                await HashFileAsync(manifestPath, cancellationToken).ConfigureAwait(false),
                StringComparison.Ordinal))
            throw new IOException("Backup manifest changed during restore preflight.");
        EnsureRestoreCapacity(destination, declaredTotalBytes);

        var partial = destination + $".partial.{Guid.NewGuid():N}";
        IProtectedDirectoryLease? staging = null;
        try
        {
            staging = protectedDirectories.CreateExclusive(partial);
            staging.Verify(partial);
            foreach (var entry in manifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = ResolveBeneath(source, entry.RelativePath);
                var destinationPath = ResolveBeneath(partial, entry.RelativePath);
                var copied = await CopyAndHashNewAsync(
                    sourcePath, destinationPath, entry.RelativePath, entry.Kind, cancellationToken)
                    .ConfigureAwait(false);
                if (copied.Length != entry.Length ||
                    !string.Equals(copied.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Backup file failed integrity validation: {entry.RelativePath}");
            }

            var databasePath = ResolveBeneath(partial, manifest.DatabaseRelativePath);
            await ValidateRestoredDatabaseAsync(databasePath, partial, expectedFiles, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    manifestHashBefore,
                    await HashFileAsync(manifestPath, cancellationToken).ConfigureAwait(false),
                    StringComparison.Ordinal))
                throw new IOException("Backup manifest changed during restore.");
            foreach (var entry in manifest.Entries)
            {
                var currentHash = await HashFileAsync(
                    ResolveBeneath(source, entry.RelativePath), cancellationToken).ConfigureAwait(false);
                if (!string.Equals(currentHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Backup source changed during restore: {entry.RelativePath}");
            }

            if (riskAcceptance is not null)
            {
                await WriteRiskAcceptanceAsync(partial, riskAcceptance, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (pilotBitLocker is not null)
            {
                EnsurePilotProtectedRoot(
                    partial,
                    "The completed inactive restore staging root did not retain its offline protection boundary.",
                    pilotBitLocker);
            }
            staging.Verify(partial);
            staging.Publish(destination);
            return new ModernStoreRestoreResult(
                destination,
                Path.Combine(destination, manifest.DatabaseRelativePath.Replace('/', Path.DirectorySeparatorChar)),
                manifestHashBefore,
                manifest.Entries.Count,
                manifest.Entries.Sum(entry => entry.Length));
        }
        finally
        {
            if (staging is not null)
            {
                _ = staging.CleanupIfOwned();
                staging.Dispose();
            }
        }
    }

    private static async Task BackupDatabaseAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = await OpenReadOnlyAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
        await using var journalMode = destination.CreateCommand();
        journalMode.CommandText = "PRAGMA journal_mode=DELETE;";
        var result = (string?)await journalMode.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(result, "delete", StringComparison.OrdinalIgnoreCase))
            throw new IOException("SQLite backup could not be normalized to a self-contained journal mode.");
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ValidateRestoredDatabaseAsync(
        string databasePath,
        string restoredRoot,
        IReadOnlyDictionary<string, BackupManifestEntry> entries,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadOnlyAsync(databasePath, cancellationToken).ConfigureAwait(false);
        await using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = (string?)await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Restored SQLite database failed integrity_check.");
        }
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sha256, byte_count, relative_path FROM content_blobs;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var referencedContent = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            referencedContent++;
            var expectedHash = reader.GetString(0);
            var expectedBytes = reader.GetInt64(1);
            var relativePath = NormalizeRelative(Path.Combine("content", reader.GetString(2)));
            if (!entries.TryGetValue(relativePath, out var entry) ||
                entry.Length != expectedBytes ||
                !string.Equals(entry.Sha256, expectedHash, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(ResolveBeneath(restoredRoot, relativePath)))
                throw new InvalidDataException("Restored content does not reconcile with its SQLite catalog.");
        }
        if (referencedContent != entries.Values.Count(entry => entry.Kind.Equals("content", StringComparison.Ordinal)))
            throw new InvalidDataException("Backup manifest contains content absent from its SQLite catalog.");
    }

    private static async Task<BackupManifest> ReadManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaximumManifestBytes)
            throw new InvalidDataException("Backup manifest is missing or exceeds its size limit.");
        await using var input = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            return await JsonSerializer.DeserializeAsync<BackupManifest>(
                input, SerializerOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Backup manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Backup manifest is not valid strict JSON.", exception);
        }
    }

    private static long ValidateManifest(BackupManifest manifest)
    {
        if (manifest.SchemaVersion != CurrentSchemaVersion ||
            manifest.CreatedUtc == default ||
            manifest.CreatedUtc > DateTimeOffset.UtcNow.AddMinutes(5) ||
            !string.Equals(manifest.DatabaseRelativePath, DatabaseRelativePath, StringComparison.Ordinal) ||
            manifest.Entries is null ||
            manifest.Entries.Count is < 1 or > MaximumManifestEntries)
            throw new InvalidDataException("Backup manifest metadata is invalid.");
        if (manifest.Entries.Any(entry => entry is null))
            throw new InvalidDataException("Backup manifest contains a null file entry.");
        if (manifest.Entries.GroupBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .Any(group => group.Count() != 1))
            throw new InvalidDataException("Backup manifest contains duplicate paths.");
        long totalBytes = 0;
        foreach (var entry in manifest.Entries)
        {
            if (entry is null || entry.Length < 0 || entry.Sha256 is null || entry.Sha256.Length != 64 ||
                entry.Sha256.Any(character => character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')) ||
                string.IsNullOrWhiteSpace(entry.RelativePath) || entry.RelativePath.Length > 1_024 ||
                entry.Kind is not ("sqlite" or "content"))
                throw new InvalidDataException("Backup manifest contains an invalid file entry.");
            _ = ResolveBeneath(Path.GetPathRoot(Path.GetFullPath("."))!, entry.RelativePath);
            if (entry.Kind == "sqlite")
            {
                if (!entry.RelativePath.Equals(DatabaseRelativePath, StringComparison.Ordinal) ||
                    entry.Length is <= 0 or > MaximumDatabaseBytes)
                    throw new InvalidDataException("Backup manifest contains an invalid SQLite entry.");
            }
            else if (!entry.RelativePath.StartsWith("content/", StringComparison.Ordinal) ||
                entry.Length > MaximumContentBlobBytes)
            {
                throw new InvalidDataException("Backup manifest contains an invalid content entry.");
            }
            if (entry.Length > MaximumBackupBytes - totalBytes)
                throw new InvalidDataException("Backup manifest exceeds the aggregate restore limit.");
            totalBytes += entry.Length;
        }
        if (manifest.Entries.Count(entry =>
                entry.RelativePath.Equals(DatabaseRelativePath, StringComparison.Ordinal) &&
                entry.Kind.Equals("sqlite", StringComparison.Ordinal)) != 1)
            throw new InvalidDataException("Backup manifest does not contain its SQLite database.");
        return totalBytes;
    }

    private static async Task PreflightRestoreSourcesAsync(
        string sourceRoot,
        IReadOnlyList<BackupManifestEntry> entries,
        CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = ResolveBeneath(sourceRoot, entry.RelativePath);
            var info = new FileInfo(sourcePath);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                info.Length != entry.Length)
                throw new InvalidDataException(
                    $"Backup file failed length/type preflight: {entry.RelativePath}");
            var hash = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(hash, entry.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Backup file failed SHA-256 preflight: {entry.RelativePath}");
        }
    }

    private static void EnsureRestoreCapacity(string destination, long declaredTotalBytes)
    {
        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Restore destination has no parent directory.");
        var root = Path.GetPathRoot(Path.GetFullPath(parent))
            ?? throw new InvalidOperationException("Restore destination has no volume root.");
        var required = checked(declaredTotalBytes + RestoreFreeSpaceReserveBytes);
        if (new DriveInfo(root).AvailableFreeSpace < required)
            throw new IOException("Restore destination does not have enough free space for the attested backup.");
    }

    private static void EnsureBackupCapacity(string stagingRoot, long remainingContentBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(stagingRoot))
            ?? throw new InvalidOperationException("Backup destination has no volume root.");
        var required = checked(remainingContentBytes + RestoreFreeSpaceReserveBytes);
        if (new DriveInfo(root).AvailableFreeSpace < required)
            throw new IOException("Backup destination does not have enough free space for the cataloged store.");
    }

    private static async Task<BackupManifestEntry> CopyAndHashNewAsync(
        string sourcePath,
        string destinationPath,
        string relativePath,
        string kind,
        CancellationToken cancellationToken)
    {
        var sourceInfo = new FileInfo(sourcePath);
        if (!sourceInfo.Exists || (sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Backup source file is missing or is a reparse point.");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        await using var input = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            length = checked(length + read);
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new BackupManifestEntry(
            NormalizeRelative(relativePath), length,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), kind);
    }

    private static async Task<BackupManifestEntry> DescribeFileAsync(
        string path,
        string relativePath,
        string kind,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        return new BackupManifestEntry(
            NormalizeRelative(relativePath), info.Length,
            await HashFileAsync(path, cancellationToken).ConfigureAwait(false), kind);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static async Task WriteRiskAcceptanceAsync(
        string protectedRoot,
        ValidatedRiskAcceptanceAttestation attestation,
        CancellationToken cancellationToken)
    {
        var bytes = attestation.Utf8Json;
        if (bytes.Length is <= 0 or > FileUnencryptedVolumeRiskAcceptanceProbe.MaximumFileBytes ||
            !string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(bytes.Span)),
                attestation.Sha256,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "The captured unencrypted-volume risk acceptance failed its in-memory attestation.");
        var destination = Path.Combine(
            protectedRoot, FileUnencryptedVolumeRiskAcceptanceProbe.FileName);
        await using var output = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            FileUnencryptedVolumeRiskAcceptanceProbe.MaximumFileBytes,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateOptionalRiskAcceptanceMetadata(string backupRoot)
    {
        var path = Path.Combine(
            backupRoot, FileUnencryptedVolumeRiskAcceptanceProbe.FileName);
        if (!File.Exists(path)) return;
        ReparsePointPolicy.EnsureNoTraversal(path, requireLeafExists: true);
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > FileUnencryptedVolumeRiskAcceptanceProbe.MaximumFileBytes)
            throw new InvalidDataException(
                "Backup protection metadata is empty or exceeds its bounded size contract.");
    }

    private static string ValidateNewLocalRoot(string path)
    {
        var canonical = ValidateLocalPath(path);
        if (Directory.Exists(canonical) || File.Exists(canonical))
            throw new IOException("Backup/restore destination must not already exist; overwrite is prohibited.");
        return canonical;
    }

    private static void EnsurePilotProtectedRoot(
        string path,
        string failureMessage,
        IBitLockerProtectionProbe bitLocker)
    {
        var result = new PilotEnrollmentPreflight(
            bitLocker,
            new CurrentUserOnlyAclProbe()).Inspect(path);
        if (!result.Passed) throw new InvalidOperationException(failureMessage);
    }

    private static ValidatedRiskAcceptanceAttestation? EnsureProtectedDestinationVolume(
        string destination,
        IBitLockerProtectionProbe bitLockerProbe)
    {
        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Backup/restore destination has no parent directory.");
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException(
                "Backup/restore requires an existing user-selected parent directory.");
        var canonicalParent = ReparsePointPolicy.EnsureNoTraversal(parent, requireLeafExists: true);
        var parentAcl = CurrentUserOnlyAclProbe.InspectRootOnly(canonicalParent);
        if (!parentAcl.Passed)
            throw new InvalidOperationException(
                "Protected backup/restore requires an existing current-user-only parent directory.");
        var volumeRoot = Path.GetPathRoot(canonicalParent)
            ?? throw new InvalidOperationException("Backup/restore destination has no volume root.");
        var drive = new DriveInfo(volumeRoot);
        if (drive.DriveType != DriveType.Fixed ||
            !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Protected backup/restore requires a fixed local NTFS volume.");
        var bitLocker = bitLockerProbe.Inspect(canonicalParent);
        if (bitLocker.Passed) return null;
        var riskAcceptance = new FileUnencryptedVolumeRiskAcceptanceProbe()
            .InspectWithAttestation(canonicalParent);
        if (!riskAcceptance.Result.Passed || riskAcceptance.Attestation is null)
            throw new InvalidOperationException(
                "Protected backup/restore requires validated BitLocker protection or a valid " +
                "development-only unencrypted-volume risk acceptance in the selected parent.");
        return riskAcceptance.Attestation;
    }

    private static string ValidateExistingLocalRoot(string path)
    {
        var canonical = ValidateLocalPath(path);
        if (!Directory.Exists(canonical))
            throw new DirectoryNotFoundException($"Backup root does not exist: {canonical}");
        RejectReparseAncestors(canonical);
        foreach (var entry in Directory.EnumerateFileSystemEntries(canonical, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Backup source cannot contain reparse points.");
        }
        return canonical;
    }

    private static string ValidateLocalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(canonical)
            ?? throw new InvalidOperationException("Backup/restore path has no volume root.");
        var drive = new DriveInfo(root);
        if (drive.DriveType is DriveType.Network or DriveType.Removable or DriveType.CDRom or DriveType.NoRootDirectory)
            throw new InvalidOperationException("Backup/restore requires fixed local storage.");
        RejectReparseAncestors(Path.GetDirectoryName(canonical)!);
        return canonical;
    }

    private static void RejectReparseAncestors(string path)
    {
        DirectoryInfo? current = new(path);
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Backup/restore path cannot traverse a reparse point.");
            current = current.Parent;
        }
    }

    private static void EnsureSeparateTrees(string first, string second, string message)
    {
        if (IsWithin(first, second) || IsWithin(second, first)) throw new InvalidOperationException(message);
    }

    private static bool IsWithin(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative) &&
            !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string ResolveBeneath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Backup relative path is invalid.");
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var resolved = Path.GetFullPath(Path.Combine(
            canonicalRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(resolved, canonicalRoot) || resolved.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Backup relative path escaped its root.");
        return resolved;
    }

    private static void EnsureNoReparsePath(string root, string path)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var relative = Path.GetRelativePath(canonicalRoot, path);
        var current = canonicalRoot;
        foreach (var component in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Backup source path cannot traverse a reparse point.");
        }
    }

    private static string NormalizeRelative(string path) => path.Replace('\\', '/');

    private sealed record BackupManifest(
        int SchemaVersion,
        DateTimeOffset CreatedUtc,
        string DatabaseRelativePath,
        IReadOnlyList<BackupManifestEntry> Entries);

    private sealed record BackupManifestEntry(
        string RelativePath,
        long Length,
        string Sha256,
        string Kind);

    private sealed record ContentCatalogEntry(
        string Sha256,
        long Length,
        string SourcePath,
        string ManifestRelativePath);
}
