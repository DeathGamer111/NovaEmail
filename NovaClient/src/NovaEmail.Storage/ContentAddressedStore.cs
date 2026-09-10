using System.Security.Cryptography;
using NovaEmail.Safety;

namespace NovaEmail.Storage;

internal sealed class ContentAddressedStore
{
    private const long MaximumContentBytes = 512L * 1024L * 1024L;
    private readonly string _root;
    private readonly Action<long>? _beforeContentWrite;

    public ContentAddressedStore(string dataRoot, Action<long>? beforeContentWrite = null)
    {
        _root = Path.Combine(dataRoot, "content");
        _beforeContentWrite = beforeContentWrite;
        Directory.CreateDirectory(_root);
        ReparsePointPolicy.EnsureNoTraversal(_root, requireLeafExists: true);
    }

    public async Task<StoredContent> ImportBytesAsync(
        ReadOnlyMemory<byte> content,
        string extension,
        CancellationToken cancellationToken)
    {
        if (content.Length > MaximumContentBytes)
            throw new InvalidDataException("Content exceeds the configured storage limit.");
        if (string.IsNullOrWhiteSpace(extension) || extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Content extension is invalid.", nameof(extension));
        _beforeContentWrite?.Invoke(content.Length);
        var digest = Convert.ToHexString(SHA256.HashData(content.Span)).ToLowerInvariant();
        var directory = Path.Combine(_root, digest[..2]);
        Directory.CreateDirectory(directory);
        ReparsePointPolicy.EnsureNoTraversal(directory, requireLeafExists: true);
        var finalPath = Path.Combine(directory, digest + "." + extension.TrimStart('.'));
        if (File.Exists(finalPath))
        {
            await VerifyExistingContentAsync(
                finalPath, digest, content.Length, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var temporaryPath = Path.Combine(_root, $".write-{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var destination = new FileStream(
                    temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
                {
                    await destination.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                try
                {
                    File.Move(temporaryPath, finalPath);
                }
                catch (IOException) when (File.Exists(finalPath))
                {
                    await VerifyExistingContentAsync(
                        finalPath, digest, content.Length, cancellationToken).ConfigureAwait(false);
                    File.Delete(temporaryPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        return new StoredContent(digest, content.Length, Path.GetRelativePath(_root, finalPath));
    }

    private static async Task VerifyExistingContentAsync(
        string path,
        string expectedSha256,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        var canonicalPath = ReparsePointPolicy.EnsureNoTraversal(path, requireLeafExists: true);
        var info = new FileInfo(canonicalPath);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                "Existing content-addressed data is missing or redirected.");

        await using var stream = new FileStream(
            canonicalPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != expectedBytes)
            throw new InvalidDataException(
                "Existing content-addressed data length differs from its SHA-derived identity.");
        var actualSha256 = Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
            throw new InvalidDataException(
                "Existing content-addressed data SHA-256 differs from its SHA-derived identity.");
    }

    public Task VerifyCatalogEntryAsync(
        string relativePath,
        string expectedSha256,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        if (expectedSha256.Length != 64 ||
            expectedSha256.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("Content catalog contains an invalid SHA-256 identity.");
        var extension = Path.GetExtension(relativePath);
        if (extension is not (".mime" or ".attachment"))
            throw new InvalidDataException("Content catalog contains an unsupported storage extension.");
        var expectedRelativePath = Path.Combine(
            expectedSha256[..2], expectedSha256 + extension);
        if (!string.Equals(
                Path.GetFullPath(Path.Combine(_root, relativePath)),
                Path.GetFullPath(Path.Combine(_root, expectedRelativePath)),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Content catalog path differs from its SHA-derived identity.");
        return VerifyExistingContentAsync(
            GetAbsolutePath(relativePath), expectedSha256, expectedBytes, cancellationToken);
    }

    public string GetAbsolutePath(string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_root, relativePath));
        var rootWithSeparator = Path.GetFullPath(_root) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Content path escaped the modern content root.");
        }
        ReparsePointPolicy.EnsureNoTraversal(fullPath, requireLeafExists: false);
        return fullPath;
    }
}

internal sealed record StoredContent(string Sha256, long Bytes, string RelativePath);
