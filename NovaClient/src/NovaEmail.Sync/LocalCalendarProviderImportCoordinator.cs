using System.Security.Cryptography;
using System.Text;
using NovaEmail.Storage;

namespace NovaEmail.Sync;

/// <summary>
/// A provider snapshot that has already been captured by either a loopback-only
/// test double or an explicitly selected local file. This contract deliberately
/// has no OAuth, HTTP, push, or provider-write operation.
/// </summary>
public sealed record LocalCalendarProviderSnapshotBatch(
    CalendarEventSourceKind SourceKind,
    string ProviderAccountIdentitySha256,
    IReadOnlyList<CalendarProviderEventSnapshotImport> Snapshots,
    bool IsLocalProtocolDouble,
    bool NetworkActionsInvoked,
    bool ProductionEndpointsInvoked,
    bool IsExplicitLocalFileImport = false,
    IReadOnlyList<CalendarProviderSeriesSnapshotImport>? SeriesSnapshots = null);

public interface ILocalCalendarProviderSnapshotSource
{
    Task<LocalCalendarProviderSnapshotBatch> ReadSnapshotBatchAsync(
        CancellationToken cancellationToken = default);
}

public sealed record LocalCalendarProviderImportResult(
    CalendarEventSourceKind SourceKind,
    string ProviderAccountIdentitySha256,
    int Observed,
    int Applied,
    int AlreadyObserved,
    int Added,
    int Updated,
    int Unchanged,
    int Tombstoned,
    int AlreadyTombstoned,
    int MissingDeletionRetained,
    int ResurrectionConflictRetained,
    bool ExplicitLocalFileRead,
    bool ExternalMutationsAttempted,
    bool NetworkActionsInvoked,
    bool ProductionEndpointsInvoked,
    int SeriesSnapshots = 0,
    int RemovedOccurrencesRetained = 0);

/// <summary>
/// Imports Google or Apple calendar snapshots from exactly one approved local origin:
/// a protocol double or an explicitly selected file. Provider deletion observations
/// create retained tombstones; this coordinator has no operation that can purge
/// history or mutate an external calendar.
/// </summary>
public sealed class LocalCalendarProviderImportCoordinator
{
    public const int MaximumSnapshotsPerBatch = 500;

    private readonly ModernMailStore _store;

    public LocalCalendarProviderImportCoordinator(ModernMailStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<LocalCalendarProviderImportResult> ImportAsync(
        ILocalCalendarProviderSnapshotSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var batch = await source.ReadSnapshotBatchAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The local calendar provider source returned no batch.");
        ValidateBatch(batch);

        var seriesSnapshots = batch.SeriesSnapshots ?? [];
        var sourceSnapshotsByEvent = batch.Snapshots.ToDictionary(
            snapshot => snapshot.ProviderEventIdentitySha256,
            StringComparer.Ordinal);
        var derivedRemovals = new List<CalendarProviderEventSnapshotImport>();
        foreach (var series in seriesSnapshots)
        {
            var current = await _store.ReadLatestCalendarProviderSeriesSnapshotAsync(
                series.SourceKind,
                series.ProviderAccountIdentitySha256,
                series.ProviderSeriesIdentitySha256,
                cancellationToken).ConfigureAwait(false);
            var normalizedMembers = ModernMailStore
                .ValidateAndNormalizeCalendarProviderSeriesSnapshot(series);
            if (current is not null)
            {
                if (string.Equals(
                        current.ProviderSeriesRevisionIdentitySha256,
                        series.ProviderSeriesRevisionIdentitySha256,
                        StringComparison.Ordinal))
                {
                    if (!current.MemberEventIdentitySha256s.SequenceEqual(
                            normalizedMembers, StringComparer.Ordinal))
                    {
                        throw new InvalidDataException(
                            "A provider series revision identity was reused with different occurrence membership.");
                    }
                }
                else if (series.ObservedUtc <= current.ObservedUtc)
                {
                    throw new InvalidDataException(
                        "A changed provider series snapshot is stale or ambiguously ordered.");
                }

                var currentMembers = normalizedMembers.ToHashSet(StringComparer.Ordinal);
                foreach (var removedIdentity in current.MemberEventIdentitySha256s
                             .Where(identity => !currentMembers.Contains(identity)))
                {
                    if (sourceSnapshotsByEvent.TryGetValue(removedIdentity, out var explicitRemoval))
                    {
                        if (!explicitRemoval.IsDeleted || !string.Equals(
                                explicitRemoval.ProviderSeriesIdentitySha256,
                                series.ProviderSeriesIdentitySha256,
                                StringComparison.Ordinal))
                        {
                            throw new InvalidDataException(
                                "A removed series occurrence conflicts with an explicit current observation.");
                        }
                        continue;
                    }
                    derivedRemovals.Add(new CalendarProviderEventSnapshotImport(
                        series.SourceKind,
                        series.ProviderAccountIdentitySha256,
                        removedIdentity,
                        HashIdentity(string.Concat(
                            "NovaEmail calendar series removal\0",
                            series.ProviderSeriesRevisionIdentitySha256,
                            "\0",
                            removedIdentity)),
                        Title: string.Empty,
                        StartLocal: default,
                        EndLocal: default,
                        AllDay: false,
                        Location: string.Empty,
                        Notes: string.Empty,
                        IsDeleted: true,
                        series.ObservedUtc,
                        series.ProviderSeriesIdentitySha256));
                }
            }
        }
        if (batch.Snapshots.Count + derivedRemovals.Count > MaximumSnapshotsPerBatch * 2)
            throw new InvalidDataException(
                "Calendar provider series reconciliation exceeds its 1,000-observation limit.");

        var outcomes = new Dictionary<CalendarProviderObservationOutcome, int>();
        var alreadyObserved = 0;
        foreach (var snapshot in batch.Snapshots.Concat(derivedRemovals))
        {
            var applied = await _store.ApplyCalendarProviderEventSnapshotAsync(
                snapshot, cancellationToken).ConfigureAwait(false);
            if (applied.WasAlreadyObserved)
            {
                alreadyObserved++;
            }
            else
            {
                outcomes[applied.Observation.Outcome] =
                    outcomes.GetValueOrDefault(applied.Observation.Outcome) + 1;
            }
        }

        foreach (var series in seriesSnapshots)
        {
            await _store.SaveCalendarProviderSeriesSnapshotAsync(
                series, cancellationToken).ConfigureAwait(false);
        }

        var totalObserved = batch.Snapshots.Count + derivedRemovals.Count;

        return new LocalCalendarProviderImportResult(
            batch.SourceKind,
            batch.ProviderAccountIdentitySha256,
            totalObserved,
            totalObserved - alreadyObserved,
            alreadyObserved,
            Count(CalendarProviderObservationOutcome.Added),
            Count(CalendarProviderObservationOutcome.Updated),
            Count(CalendarProviderObservationOutcome.Unchanged),
            Count(CalendarProviderObservationOutcome.Tombstoned),
            Count(CalendarProviderObservationOutcome.AlreadyTombstoned),
            Count(CalendarProviderObservationOutcome.MissingDeletionRetained),
            Count(CalendarProviderObservationOutcome.ResurrectionConflictRetained),
            ExplicitLocalFileRead: batch.IsExplicitLocalFileImport,
            ExternalMutationsAttempted: false,
            NetworkActionsInvoked: false,
            ProductionEndpointsInvoked: false,
            SeriesSnapshots: seriesSnapshots.Count,
            RemovedOccurrencesRetained: derivedRemovals.Count);

        int Count(CalendarProviderObservationOutcome outcome) =>
            outcomes.GetValueOrDefault(outcome);
    }

    private static void ValidateBatch(LocalCalendarProviderSnapshotBatch batch)
    {
        if (batch.IsLocalProtocolDouble == batch.IsExplicitLocalFileImport ||
            batch.NetworkActionsInvoked ||
            batch.ProductionEndpointsInvoked)
        {
            throw new InvalidOperationException(
                "Calendar provider import requires exactly one local protocol-double or explicit local-file origin with no network or production actions.");
        }
        if (batch.SourceKind is not CalendarEventSourceKind.Google and
            not CalendarEventSourceKind.Apple)
            throw new InvalidDataException("Only Google or Apple calendar snapshots are supported.");
        ValidateLowercaseSha256(
            batch.ProviderAccountIdentitySha256,
            nameof(batch.ProviderAccountIdentitySha256));
        ArgumentNullException.ThrowIfNull(batch.Snapshots);
        if (batch.Snapshots.Count > MaximumSnapshotsPerBatch)
            throw new InvalidDataException("Calendar provider snapshot batch exceeds its 500-item limit.");

        var eventKeys = new HashSet<string>(StringComparer.Ordinal);
        var revisionKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in batch.Snapshots)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            ModernMailStore.ValidateCalendarProviderEventSnapshotImport(snapshot);
            if (snapshot.SourceKind != batch.SourceKind ||
                !string.Equals(
                    snapshot.ProviderAccountIdentitySha256,
                    batch.ProviderAccountIdentitySha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Every calendar snapshot must match its batch provider and account identity.");
            }
            if (!eventKeys.Add(snapshot.ProviderEventIdentitySha256))
                throw new InvalidDataException(
                    "Calendar provider snapshot batch contains more than one current observation for an event identity.");
            var revisionKey = string.Concat(
                snapshot.ProviderEventIdentitySha256,
                ":",
                snapshot.ProviderRevisionIdentitySha256);
            if (!revisionKeys.Add(revisionKey))
                throw new InvalidDataException(
                    "Calendar provider snapshot batch contains a duplicate event revision identity.");
        }

        var seriesSnapshots = batch.SeriesSnapshots ?? [];
        if (seriesSnapshots.Count > MaximumSnapshotsPerBatch)
            throw new InvalidDataException(
                "Calendar provider snapshot batch exceeds its 500-series limit.");
        var seriesByIdentity = new Dictionary<string, CalendarProviderSeriesSnapshotImport>(
            StringComparer.Ordinal);
        var memberSeries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var series in seriesSnapshots)
        {
            ArgumentNullException.ThrowIfNull(series);
            var normalizedMembers = ModernMailStore
                .ValidateAndNormalizeCalendarProviderSeriesSnapshot(series);
            if (series.SourceKind != batch.SourceKind ||
                !string.Equals(
                    series.ProviderAccountIdentitySha256,
                    batch.ProviderAccountIdentitySha256,
                    StringComparison.Ordinal) ||
                !seriesByIdentity.TryAdd(series.ProviderSeriesIdentitySha256, series))
            {
                throw new InvalidDataException(
                    "Every calendar series snapshot must uniquely match its batch provider and account identity.");
            }
            foreach (var member in normalizedMembers)
            {
                if (!memberSeries.TryAdd(member, series.ProviderSeriesIdentitySha256))
                    throw new InvalidDataException(
                        "A provider occurrence cannot belong to more than one complete series snapshot.");
            }
        }

        foreach (var snapshot in batch.Snapshots)
        {
            if (snapshot.ProviderSeriesIdentitySha256 is null)
            {
                if (memberSeries.ContainsKey(snapshot.ProviderEventIdentitySha256))
                    throw new InvalidDataException(
                        "A standalone provider event cannot be listed as a recurring-series occurrence.");
                continue;
            }
            if (!seriesByIdentity.ContainsKey(snapshot.ProviderSeriesIdentitySha256))
                throw new InvalidDataException(
                    "A recurring occurrence requires one complete series snapshot in the same batch.");
            var isMember = memberSeries.TryGetValue(
                snapshot.ProviderEventIdentitySha256, out var memberSeriesIdentity);
            if (snapshot.IsDeleted)
            {
                if (isMember)
                    throw new InvalidDataException(
                        "A deleted provider occurrence cannot remain in current series membership.");
            }
            else if (!isMember || !string.Equals(
                         memberSeriesIdentity,
                         snapshot.ProviderSeriesIdentitySha256,
                         StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Every active recurring occurrence must be listed in its complete series snapshot.");
            }
        }
        foreach (var member in memberSeries)
        {
            if (!eventKeys.Contains(member.Key))
                throw new InvalidDataException(
                    "A complete provider series snapshot references an occurrence absent from its batch.");
        }
    }

    private static string HashIdentity(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void ValidateLowercaseSha256(string value, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(value, fieldName);
        if (value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
            throw new InvalidDataException($"{fieldName} must be a lowercase SHA-256 value.");
    }
}
