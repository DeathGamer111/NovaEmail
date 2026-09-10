using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaEmail.Safety;

public sealed record ProtectionProbeResult(bool Passed, string Detail);

public sealed record PilotPreflightResult(
    bool Passed,
    ProtectionProbeResult LocalFixedNtfsVolume,
    ProtectionProbeResult BitLocker,
    ProtectionProbeResult CurrentUserAcl,
    ProtectionProbeResult UnencryptedVolumeRiskAcceptance)
{
    public bool BitLockerRiskExceptionApplied =>
        !BitLocker.Passed && UnencryptedVolumeRiskAcceptance.Passed;
}

public interface IBitLockerProtectionProbe
{
    ProtectionProbeResult Inspect(string canonicalDataRoot);
}

public interface ICurrentUserAclProbe
{
    ProtectionProbeResult Inspect(string canonicalDataRoot);
}

public interface IUnencryptedVolumeRiskAcceptanceProbe
{
    ProtectionProbeResult Inspect(string canonicalDataRoot);
}

public sealed class PilotEnrollmentPreflight
{
    private readonly IBitLockerProtectionProbe _bitLocker;
    private readonly ICurrentUserAclProbe _acl;
    private readonly IUnencryptedVolumeRiskAcceptanceProbe _riskAcceptance;

    public PilotEnrollmentPreflight(IBitLockerProtectionProbe bitLocker, ICurrentUserAclProbe acl)
        : this(bitLocker, acl, new FileUnencryptedVolumeRiskAcceptanceProbe())
    {
    }

    public PilotEnrollmentPreflight(
        IBitLockerProtectionProbe bitLocker,
        ICurrentUserAclProbe acl,
        IUnencryptedVolumeRiskAcceptanceProbe riskAcceptance)
    {
        _bitLocker = bitLocker ?? throw new ArgumentNullException(nameof(bitLocker));
        _acl = acl ?? throw new ArgumentNullException(nameof(acl));
        _riskAcceptance = riskAcceptance ?? throw new ArgumentNullException(nameof(riskAcceptance));
    }

    public PilotPreflightResult Inspect(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var canonical = ReparsePointPolicy.EnsureNoTraversal(dataRoot, requireLeafExists: true);
        var root = Path.GetPathRoot(canonical) ?? throw new InvalidDataException("Data root has no volume root.");
        var drive = new DriveInfo(root);
        var localVolume = drive.DriveType == DriveType.Fixed &&
            string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)
            ? new ProtectionProbeResult(true, "Data root is on a fixed NTFS volume.")
            : new ProtectionProbeResult(false, "Pilot data must be on a fixed NTFS volume.");
        var bitLocker = _bitLocker.Inspect(canonical);
        var acl = _acl.Inspect(canonical);
        var riskAcceptance = bitLocker.Passed
            ? new ProtectionProbeResult(false,
                "An unencrypted-volume exception is unnecessary because BitLocker is enabled.")
            : _riskAcceptance.Inspect(canonical);
        return new PilotPreflightResult(
            localVolume.Passed && acl.Passed && (bitLocker.Passed || riskAcceptance.Passed),
            localVolume,
            bitLocker,
            acl,
            riskAcceptance);
    }
}

public sealed class FileUnencryptedVolumeRiskAcceptanceProbe : IUnencryptedVolumeRiskAcceptanceProbe
{
    public const string FileName = ".novaemail-unencrypted-volume-risk-acceptance.json";
    public const string RequiredScope = "DevelopmentLocalStoreOnly";
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromDays(30);
    internal const int MaximumFileBytes = 4 * 1024;
    private readonly TimeProvider _timeProvider;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public FileUnencryptedVolumeRiskAcceptanceProbe(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ProtectionProbeResult Inspect(string canonicalDataRoot)
        => InspectWithAttestation(canonicalDataRoot).Result;

    internal RiskAcceptanceInspection InspectWithAttestation(string canonicalDataRoot)
    {
        try
        {
            var path = ReparsePointPolicy.EnsureNoTraversal(
                Path.Combine(canonicalDataRoot, FileName), requireLeafExists: true);
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                MaximumFileBytes, FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumFileBytes)
                return RejectedInspection(
                    "No valid unencrypted-volume risk acceptance is present.");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.Position != stream.Length)
                return RejectedInspection(
                    "The unencrypted-volume risk acceptance changed while it was read.");
            using (var document = JsonDocument.Parse(
                       bytes,
                       new JsonDocumentOptions
                       {
                           AllowTrailingCommas = false,
                           CommentHandling = JsonCommentHandling.Disallow,
                           MaxDepth = 8,
                       }))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    ContainsDuplicatePropertyName(document.RootElement))
                    return RejectedInspection(
                        "The unencrypted-volume risk acceptance contains an ambiguous JSON object.");
            }
            var acceptance = JsonSerializer.Deserialize<RiskAcceptanceDocument>(bytes, JsonOptions);
            var now = _timeProvider.GetUtcNow();
            if (acceptance is null || acceptance.SchemaVersion != 1 || !acceptance.Accepted ||
                acceptance.ProductionDeploymentAuthorized ||
                !string.Equals(acceptance.Scope, RequiredScope, StringComparison.Ordinal) ||
                !ValidIdentifier(acceptance.ApprovalId) ||
                !ValidJustification(acceptance.Justification) ||
                acceptance.ApprovedUtc > now.AddMinutes(5) ||
                acceptance.ExpiresUtc <= now ||
                acceptance.ExpiresUtc <= acceptance.ApprovedUtc ||
                acceptance.ExpiresUtc - acceptance.ApprovedUtc > MaximumDuration)
            {
                return RejectedInspection(
                    "The unencrypted-volume risk acceptance is invalid or expired.");
            }
            var result = new ProtectionProbeResult(
                true,
                $"Explicit development-only unencrypted-volume risk acceptance is valid until " +
                $"{acceptance.ExpiresUtc:O}; BitLocker remains recommended.");
            return new RiskAcceptanceInspection(
                result,
                new ValidatedRiskAcceptanceAttestation(
                    bytes,
                    Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    acceptance.ExpiresUtc));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or SecurityException)
        {
            return RejectedInspection(
                "The unencrypted-volume risk acceptance could not be validated.");
        }
    }

    private static ProtectionProbeResult Rejected(string detail) => new(false, detail);

    private static RiskAcceptanceInspection RejectedInspection(string detail) =>
        new(Rejected(detail), null);

    private static bool ContainsDuplicatePropertyName(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || ContainsDuplicatePropertyName(property.Value))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                if (ContainsDuplicatePropertyName(child)) return true;
            }
        }
        return false;
    }

    private static bool ValidIdentifier(string? value) =>
        value is not null &&
        value.Length is >= 3 and <= 64 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool ValidJustification(string? value) =>
        value is not null &&
        value.Length is >= 20 and <= 500 && !value.Any(char.IsControl);

    private sealed record RiskAcceptanceDocument(
        int SchemaVersion,
        bool Accepted,
        string Scope,
        string ApprovalId,
        string Justification,
        DateTimeOffset ApprovedUtc,
        DateTimeOffset ExpiresUtc,
        bool ProductionDeploymentAuthorized);
}

internal sealed record ValidatedRiskAcceptanceAttestation(
    ReadOnlyMemory<byte> Utf8Json,
    string Sha256,
    DateTimeOffset ExpiresUtc);

internal sealed record RiskAcceptanceInspection(
    ProtectionProbeResult Result,
    ValidatedRiskAcceptanceAttestation? Attestation);

public sealed class WindowsBitLockerProtectionProbe : IBitLockerProtectionProbe
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(15);

    public ProtectionProbeResult Inspect(string canonicalDataRoot)
    {
        if (!OperatingSystem.IsWindows()) return new(false, "BitLocker validation is only supported on Windows.");
        var volume = Path.GetPathRoot(canonicalDataRoot);
        if (string.IsNullOrWhiteSpace(volume)) return new(false, "Unable to resolve the data volume.");
        var executable = Path.Combine(Environment.SystemDirectory, "manage-bde.exe");
        if (!File.Exists(executable)) return new(false, "The Windows BitLocker status tool is unavailable.");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-status");
        process.StartInfo.ArgumentList.Add(volume.TrimEnd(Path.DirectorySeparatorChar));
        process.StartInfo.ArgumentList.Add("-protectionaserrorlevel");
        if (!process.Start()) return new(false, "Unable to start BitLocker status validation.");
        if (!process.WaitForExit(StatusTimeout))
        {
            process.Kill(entireProcessTree: true);
            return new(false, "BitLocker status validation timed out.");
        }
        return process.ExitCode == 0
            ? new(true, "BitLocker protection is enabled for the data volume.")
            : new(false, "BitLocker protection is off or could not be validated.");
    }
}

public sealed class CurrentUserOnlyAclProbe : ICurrentUserAclProbe
{
    private const int MaximumInspectedEntries = 1_000_000;
    private static readonly HashSet<string> InfrastructurePrincipals =
    [
        "S-1-5-18",       // LocalSystem
        "S-1-5-32-544",   // Built-in Administrators
    ];

    public ProtectionProbeResult Inspect(string canonicalDataRoot)
    {
        if (!OperatingSystem.IsWindows()) return new(false, "Windows ACL validation is unavailable.");
        if (!Directory.Exists(canonicalDataRoot)) return new(false, "The pilot data root does not exist.");
        var currentSid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(currentSid)) return new(false, "The current Windows user SID is unavailable.");
        var canonicalRoot = Path.GetFullPath(canonicalDataRoot);
        var pending = new Stack<string>();
        pending.Push(canonicalRoot);
        var inspectedEntries = 0;
        try
        {
            while (pending.Count != 0)
            {
                var path = pending.Pop();
                if (++inspectedEntries > MaximumInspectedEntries)
                    return new(false, "Pilot data tree exceeds the ACL inspection safety limit.");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return new(false, "Pilot data contains a reparse point and cannot be ACL-validated safely.");
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                FileSystemSecurity security = isDirectory
                    ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
                    : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
                var validationFailure = ValidateDacl(
                    security,
                    currentSid,
                    requireProtectedDacl: string.Equals(
                        path, canonicalRoot, StringComparison.OrdinalIgnoreCase));
                if (validationFailure is not null)
                    return new(false, validationFailure);

                if (!isDirectory) continue;
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                    pending.Push(child);
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or SecurityException)
        {
            return new(false, "Pilot data ACLs could not be fully inspected.");
        }

        return new(true,
            $"ACL grants access only to the current user and Windows infrastructure principals on {inspectedEntries:N0} local entries.");
    }

    internal static ProtectionProbeResult InspectRootOnly(string canonicalDataRoot)
    {
        if (!OperatingSystem.IsWindows()) return new(false, "Windows ACL validation is unavailable.");
        if (!Directory.Exists(canonicalDataRoot)) return new(false, "The protected parent does not exist.");
        var currentSid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(currentSid))
            return new(false, "The current Windows user SID is unavailable.");
        try
        {
            var canonical = ReparsePointPolicy.EnsureNoTraversal(
                canonicalDataRoot, requireLeafExists: true);
            var attributes = File.GetAttributes(canonical);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) !=
                FileAttributes.Directory)
                return new(false, "The protected parent is absent or is a reparse point.");
            var security = new DirectoryInfo(canonical)
                .GetAccessControl(AccessControlSections.Access);
            var validationFailure = ValidateDacl(
                security, currentSid, requireProtectedDacl: true);
            return validationFailure is null
                ? new(true,
                    "The protected parent grants access only to the current user and Windows infrastructure principals.")
                : new(false, validationFailure);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or SecurityException)
        {
            return new(false, "The protected parent ACL could not be validated.");
        }
    }

    private static string? ValidateDacl(
        FileSystemSecurity security,
        string currentSid,
        bool requireProtectedDacl)
    {
        if (requireProtectedDacl && !security.AreAccessRulesProtected)
            return "The pilot data root inherits ACL entries; a protected DACL is required.";
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        if (rules.Any(rule => rule.AccessControlType == AccessControlType.Deny))
            return "The pilot data contains deny ACL entries; an explicit allow-only DACL is required.";
        var unauthorized = rules
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .Select(rule => ((SecurityIdentifier)rule.IdentityReference).Value)
            .Where(sid => !string.Equals(sid, currentSid, StringComparison.Ordinal) &&
                !InfrastructurePrincipals.Contains(sid))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (unauthorized.Length != 0)
            return $"Pilot data ACL grants access to {unauthorized.Length} additional principal(s).";
        var currentUserHasFullControl = rules.Any(rule =>
            rule.AccessControlType == AccessControlType.Allow &&
            string.Equals(((SecurityIdentifier)rule.IdentityReference).Value, currentSid, StringComparison.Ordinal) &&
            (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);
        return currentUserHasFullControl
            ? null
            : "The current user does not have FullControl on all pilot data entries.";
    }
}
