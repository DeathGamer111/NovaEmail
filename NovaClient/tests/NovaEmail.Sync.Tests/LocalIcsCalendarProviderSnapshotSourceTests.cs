using System.Security.Cryptography;
using System.Text;
using NovaEmail.Storage;

namespace NovaEmail.Sync.Tests;

public sealed class LocalIcsCalendarProviderSnapshotSourceTests
{
    private static readonly DateTimeOffset ObservedUtc = DateTimeOffset.UtcNow.AddMinutes(-1);

    [Fact]
    public async Task ExplicitAppleFileImportsFoldedAllDayEventIdempotentlyWithoutNetwork()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var accountIdentity = Sha256("apple-local-file-account");
            var source = Source(CalendarEventSourceKind.Apple, accountIdentity, """
                BEGIN:VCALENDAR
                VERSION:2.0
                PRODID:-//NovaEmail//Local import test//EN
                BEGIN:VEVENT
                UID:apple-event-001@example.test
                DTSTAMP:20260823T120000Z
                DTSTART;VALUE=DATE:20261224
                DTEND;VALUE=DATE:20261226
                SUMMARY:Holiday planning with
                 colleagues
                DESCRIPTION:Line one\nLine two
                LOCATION:HQ\, Main
                END:VEVENT
                END:VCALENDAR
                """);
            var coordinator = new LocalCalendarProviderImportCoordinator(store);

            var batch = await source.ReadSnapshotBatchAsync();
            var first = await coordinator.ImportAsync(source);
            var repeated = await coordinator.ImportAsync(source);

            var snapshot = Assert.Single(batch.Snapshots);
            Assert.False(batch.IsLocalProtocolDouble);
            Assert.True(batch.IsExplicitLocalFileImport);
            Assert.False(batch.NetworkActionsInvoked);
            Assert.False(batch.ProductionEndpointsInvoked);
            Assert.Equal("Holiday planning withcolleagues", snapshot.Title);
            Assert.Equal("Line one\nLine two", snapshot.Notes);
            Assert.Equal("HQ, Main", snapshot.Location);
            Assert.True(snapshot.AllDay);
            Assert.Equal(new DateTime(2026, 12, 24), snapshot.StartLocal);
            Assert.Equal(new DateTime(2026, 12, 26), snapshot.EndLocal);
            Assert.Equal(accountIdentity, snapshot.ProviderAccountIdentitySha256);
            Assert.DoesNotContain("apple-event-001", snapshot.ProviderEventIdentitySha256);
            Assert.True(first.ExplicitLocalFileRead);
            Assert.Equal(1, first.Added);
            Assert.Equal(1, repeated.AlreadyObserved);
            Assert.False(first.ExternalMutationsAttempted);
            Assert.False(first.NetworkActionsInvoked);
            Assert.False(first.ProductionEndpointsInvoked);
            Assert.Single(await store.ReadLocalCalendarAgendaAsync(
                new DateTime(2026, 12, 24), new DateTime(2026, 12, 27)));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GoogleFileConvertsDeclaredTimeZoneToDisplayTimeZone()
    {
        var source = Source(CalendarEventSourceKind.Google, Sha256("google-local-file-account"), """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:google-event-001@example.test
            DTSTART;TZID=Central Standard Time:20270215T090000
            DTEND;TZID=Central Standard Time:20270215T103000
            SUMMARY:Provider sync review
            END:VEVENT
            END:VCALENDAR
            """
            , TimeZoneInfo.Utc);

        var batch = await source.ReadSnapshotBatchAsync();

        var snapshot = Assert.Single(batch.Snapshots);
        Assert.Equal(new DateTime(2027, 2, 15, 15, 0, 0), snapshot.StartLocal);
        Assert.Equal(new DateTime(2027, 2, 15, 16, 30, 0), snapshot.EndLocal);
        Assert.False(snapshot.AllDay);
    }

    [Fact]
    public async Task CancellationCreatesRetainedTombstoneForPreviouslyImportedEvent()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var coordinator = new LocalCalendarProviderImportCoordinator(store);
            var accountIdentity = Sha256("apple-cancellation-account");
            var active = Source(CalendarEventSourceKind.Apple, accountIdentity, """
                BEGIN:VCALENDAR
                VERSION:2.0
                BEGIN:VEVENT
                UID:event-to-cancel@example.test
                DTSTART:20270304T170000Z
                DTEND:20270304T180000Z
                SUMMARY:Candidate interview
                END:VEVENT
                END:VCALENDAR
                """
                , TimeZoneInfo.Utc);
            var cancelled = Source(CalendarEventSourceKind.Apple, accountIdentity, """
                BEGIN:VCALENDAR
                VERSION:2.0
                METHOD:CANCEL
                BEGIN:VEVENT
                UID:event-to-cancel@example.test
                STATUS:CANCELLED
                END:VEVENT
                END:VCALENDAR
                """
                , TimeZoneInfo.Utc);

            var added = await coordinator.ImportAsync(active);
            var removed = await coordinator.ImportAsync(cancelled);

            Assert.Equal(1, added.Added);
            Assert.Equal(1, removed.Tombstoned);
            Assert.Empty(await store.ReadLocalCalendarAgendaAsync(
                new DateTime(2027, 3, 4), new DateTime(2027, 3, 5)));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FiniteWeeklyRecurrenceExpandsCompleteSeriesAndRetainsRemovedOccurrences()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var coordinator = new LocalCalendarProviderImportCoordinator(store);
            var accountIdentity = Sha256("recurring-account");
            var initial = Source(CalendarEventSourceKind.Google, accountIdentity, """
                BEGIN:VCALENDAR
                VERSION:2.0
                BEGIN:VEVENT
                UID:recurring-event@example.test
                DTSTART:20270104T150000Z
                DTEND:20270104T160000Z
                RRULE:FREQ=WEEKLY;COUNT=4
                RDATE:20270201T150000Z
                EXDATE:20270118T150000Z
                SUMMARY:Weekly review
                END:VEVENT
                BEGIN:VEVENT
                UID:recurring-event@example.test
                RECURRENCE-ID:20270111T150000Z
                DTSTART:20270112T170000Z
                DTEND:20270112T180000Z
                SUMMARY:Rescheduled weekly review
                END:VEVENT
                BEGIN:VEVENT
                UID:recurring-event@example.test
                RECURRENCE-ID:20270118T150000Z
                DTSTART:20270119T170000Z
                DTEND:20270119T180000Z
                SUMMARY:Excluded occurrence must remain excluded
                END:VEVENT
                END:VCALENDAR
                """
                , TimeZoneInfo.Utc);
            var revised = Source(CalendarEventSourceKind.Google, accountIdentity, """
                BEGIN:VCALENDAR
                VERSION:2.0
                BEGIN:VEVENT
                UID:recurring-event@example.test
                DTSTART:20270104T150000Z
                DTEND:20270104T160000Z
                RRULE:FREQ=WEEKLY;COUNT=2
                SUMMARY:Weekly review revised
                END:VEVENT
                END:VCALENDAR
                """,
                TimeZoneInfo.Utc,
                ObservedUtc.AddSeconds(1));

            var batch = await initial.ReadSnapshotBatchAsync();
            var first = await coordinator.ImportAsync(initial);
            var repeated = await coordinator.ImportAsync(initial);
            var second = await coordinator.ImportAsync(revised);

            var series = Assert.Single(batch.SeriesSnapshots!);
            Assert.Equal(4, batch.Snapshots.Count);
            Assert.Equal(4, series.MemberEventIdentitySha256s.Count);
            Assert.All(batch.Snapshots, snapshot =>
                Assert.Equal(series.ProviderSeriesIdentitySha256,
                    snapshot.ProviderSeriesIdentitySha256));
            Assert.Equal(
                [
                    new DateTime(2027, 1, 4, 15, 0, 0),
                    new DateTime(2027, 1, 12, 17, 0, 0),
                    new DateTime(2027, 1, 25, 15, 0, 0),
                    new DateTime(2027, 2, 1, 15, 0, 0),
                ],
                batch.Snapshots.Select(snapshot => snapshot.StartLocal));
            Assert.DoesNotContain(batch.Snapshots, snapshot =>
                snapshot.StartLocal == new DateTime(2027, 1, 18, 15, 0, 0));
            Assert.Equal("Rescheduled weekly review", batch.Snapshots[1].Title);
            Assert.Equal(4, first.Added);
            Assert.Equal(4, repeated.AlreadyObserved);
            Assert.Equal(2, second.Updated);
            Assert.Equal(2, second.Tombstoned);
            Assert.Equal(2, second.RemovedOccurrencesRetained);
            Assert.False(first.NetworkActionsInvoked);
            Assert.False(first.ExternalMutationsAttempted);

            var agenda = await store.ReadLocalCalendarAgendaAsync(
                new DateTime(2027, 1, 1), new DateTime(2027, 2, 2));
            Assert.Equal(2, agenda.Count);
            Assert.Equal(
                [new DateTime(2027, 1, 4, 15, 0, 0), new DateTime(2027, 1, 11, 15, 0, 0)],
                agenda.Select(item => item.StartLocal));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task MonthlyAllDayAndZonedWeeklyRecurrencesAreExpandedWithoutLosingWallTime()
    {
        var allDay = Source(CalendarEventSourceKind.Apple, Sha256("all-day-series"), """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:last-friday@example.test
            DTSTART;VALUE=DATE:20270129
            DTEND;VALUE=DATE:20270130
            RRULE:FREQ=MONTHLY;COUNT=3;BYDAY=-1FR
            SUMMARY:Last Friday
            END:VEVENT
            END:VCALENDAR
            """,
            TimeZoneInfo.Utc);
        var zoned = Source(CalendarEventSourceKind.Google, Sha256("dst-series"), """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:dst-weekly@example.test
            DTSTART;TZID=America/Chicago:20270308T090000
            DTEND;TZID=America/Chicago:20270308T100000
            RRULE:FREQ=WEEKLY;UNTIL=20270322T140000Z
            SUMMARY:DST-safe weekly
            END:VEVENT
            END:VCALENDAR
            """,
            TimeZoneInfo.Utc);

        var allDayBatch = await allDay.ReadSnapshotBatchAsync();
        var zonedBatch = await zoned.ReadSnapshotBatchAsync();

        Assert.Equal(
            [new DateTime(2027, 1, 29), new DateTime(2027, 2, 26), new DateTime(2027, 3, 26)],
            allDayBatch.Snapshots.Select(snapshot => snapshot.StartLocal));
        Assert.All(allDayBatch.Snapshots, snapshot => Assert.True(snapshot.AllDay));
        Assert.Equal(
            [
                new DateTime(2027, 3, 8, 15, 0, 0),
                new DateTime(2027, 3, 15, 14, 0, 0),
                new DateTime(2027, 3, 22, 14, 0, 0),
            ],
            zonedBatch.Snapshots.Select(snapshot => snapshot.StartLocal));
        Assert.All(zonedBatch.Snapshots, snapshot =>
            Assert.Equal(TimeSpan.FromHours(1), snapshot.EndLocal - snapshot.StartLocal));
    }

    [Fact]
    public async Task DailyAndYearlyRulesEnumerateTheirCompleteFiniteSets()
    {
        var source = Source(CalendarEventSourceKind.Google, Sha256("daily-yearly-series"), """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:every-other-day@example.test
            DTSTART:20270101T090000Z
            DTEND:20270101T100000Z
            RRULE:FREQ=DAILY;COUNT=3;INTERVAL=2
            SUMMARY:Every other day
            END:VEVENT
            BEGIN:VEVENT
            UID:yearly-date@example.test
            DTSTART;VALUE=DATE:20270704
            DTEND;VALUE=DATE:20270705
            RRULE:FREQ=YEARLY;COUNT=3;BYMONTH=7;BYMONTHDAY=4
            SUMMARY:Annual date
            END:VEVENT
            END:VCALENDAR
            """,
            TimeZoneInfo.Utc);

        var batch = await source.ReadSnapshotBatchAsync();

        Assert.Equal(6, batch.Snapshots.Count);
        Assert.Equal(2, batch.SeriesSnapshots!.Count);
        Assert.Equal(
            [
                new DateTime(2027, 1, 1, 9, 0, 0),
                new DateTime(2027, 1, 3, 9, 0, 0),
                new DateTime(2027, 1, 5, 9, 0, 0),
                new DateTime(2027, 7, 4),
                new DateTime(2028, 7, 4),
                new DateTime(2029, 7, 4),
            ],
            batch.Snapshots.Select(snapshot => snapshot.StartLocal).Order());
    }

    [Fact]
    public async Task UnboundedOrUnsupportedRecurrenceFailsBeforeAnyStorageMutation()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var coordinator = new LocalCalendarProviderImportCoordinator(store);
            var accountIdentity = Sha256("unsafe-recurring-account");
            var rules = new[]
            {
                "FREQ=WEEKLY",
                "FREQ=WEEKLY;COUNT=501",
                "FREQ=MINUTELY;COUNT=4",
                "FREQ=WEEKLY;COUNT=4;BYSECOND=0",
                "FREQ=WEEKLY;COUNT=4;BYMONTHDAY=1",
                "FREQ=YEARLY;COUNT=4;BYDAY=MO",
            };

            foreach (var rule in rules)
            {
                var uid = $"unsafe-{Sha256(rule)}@example.test";
                var source = Source(CalendarEventSourceKind.Google, accountIdentity, $$"""
                    BEGIN:VCALENDAR
                    VERSION:2.0
                    BEGIN:VEVENT
                    UID:{{uid}}
                    DTSTART:20270104T150000Z
                    DTEND:20270104T160000Z
                    RRULE:{{rule}}
                    SUMMARY:Unsafe recurrence
                    END:VEVENT
                    END:VCALENDAR
                    """,
                    TimeZoneInfo.Utc);
                await Assert.ThrowsAsync<InvalidDataException>(
                    () => coordinator.ImportAsync(source));
                Assert.Null(await store.ReadLatestCalendarProviderSeriesSnapshotAsync(
                    CalendarEventSourceKind.Google,
                    accountIdentity,
                    Sha256($"Google\0{uid}\0series")));
            }

            Assert.Empty(await store.ReadLocalCalendarAgendaAsync(
                new DateTime(2027, 1, 1), new DateTime(2028, 1, 1)));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task UnsupportedRecurrenceParametersPeriodsAndIdentityDomainsFailClosed()
    {
        var accountIdentity = Sha256("unsafe-recurrence-shapes");
        var documents = new[]
        {
            """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:rrule-parameter@example.test
            DTSTART:20270104T150000Z
            DTEND:20270104T160000Z
            RRULE;X-UNSAFE=1:FREQ=WEEKLY;COUNT=2
            SUMMARY:Unsupported parameter
            END:VEVENT
            END:VCALENDAR
            """,
            """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:rdate-period@example.test
            DTSTART:20270104T150000Z
            DTEND:20270104T160000Z
            RDATE;VALUE=PERIOD:20270111T150000Z/20270111T160000Z
            SUMMARY:Unsupported period
            END:VEVENT
            END:VCALENDAR
            """,
            """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:range@example.test
            DTSTART:20270104T150000Z
            DTEND:20270104T160000Z
            RRULE:FREQ=WEEKLY;COUNT=2
            SUMMARY:Unsupported range
            END:VEVENT
            BEGIN:VEVENT
            UID:range@example.test
            RECURRENCE-ID;RANGE=THISANDFUTURE:20270111T150000Z
            DTSTART:20270112T150000Z
            DTEND:20270112T160000Z
            SUMMARY:Unsupported range exception
            END:VEVENT
            END:VCALENDAR
            """,
            """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:mixed-domain@example.test
            DTSTART:20270104T150000Z
            DTEND:20270104T160000Z
            RRULE:FREQ=WEEKLY;COUNT=2
            SUMMARY:Mixed recurrence domain
            END:VEVENT
            BEGIN:VEVENT
            UID:mixed-domain@example.test
            RECURRENCE-ID:20270111T150000
            DTSTART:20270112T150000Z
            DTEND:20270112T160000Z
            SUMMARY:Mixed domain exception
            END:VEVENT
            END:VCALENDAR
            """,
        };

        foreach (var document in documents)
        {
            var source = Source(
                CalendarEventSourceKind.Google,
                accountIdentity,
                document,
                TimeZoneInfo.Utc);
            await Assert.ThrowsAsync<InvalidDataException>(
                () => source.ReadSnapshotBatchAsync());
        }
    }

    [Fact]
    public async Task InvalidEncodingAmbiguousTimeAndOversizedInputFailClosed()
    {
        var accountIdentity = Sha256("invalid-account");
        var invalidUtf8 = new LocalIcsCalendarProviderSnapshotSource(
            CalendarEventSourceKind.Google,
            accountIdentity,
            new byte[] { 0xff, 0xfe, 0xfd },
            ObservedUtc,
            TimeZoneInfo.Utc);
        var ambiguousTime = Source(CalendarEventSourceKind.Google, accountIdentity, """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:ambiguous-event@example.test
            DTSTART;TZID=Central Standard Time:20271107T013000
            DTEND;TZID=Central Standard Time:20271107T023000
            SUMMARY:Ambiguous local time
            END:VEVENT
            END:VCALENDAR
            """
            , TimeZoneInfo.Utc);
        var tooManyLines = new LocalIcsCalendarProviderSnapshotSource(
            CalendarEventSourceKind.Google,
            accountIdentity,
            Encoding.UTF8.GetBytes(string.Join(
                "\r\n",
                Enumerable.Repeat(
                    "X-NOVAEMAIL-BOUND:0",
                    LocalIcsCalendarProviderSnapshotSource.MaximumPhysicalLines + 1))),
            ObservedUtc,
            TimeZoneInfo.Utc);
        var oversizedUnfoldedLine = Source(
            CalendarEventSourceKind.Google,
            accountIdentity,
            string.Concat(
                "BEGIN:VCALENDAR\n",
                "X-NOVAEMAIL-BOUND:",
                new string('a',
                    LocalIcsCalendarProviderSnapshotSource.MaximumUnfoldedLineUtf8Bytes),
                "\nEND:VCALENDAR"),
            TimeZoneInfo.Utc);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => invalidUtf8.ReadSnapshotBatchAsync());
        await Assert.ThrowsAsync<InvalidDataException>(
            () => ambiguousTime.ReadSnapshotBatchAsync());
        await Assert.ThrowsAsync<InvalidDataException>(
            () => tooManyLines.ReadSnapshotBatchAsync());
        await Assert.ThrowsAsync<InvalidDataException>(
            () => oversizedUnfoldedLine.ReadSnapshotBatchAsync());
        Assert.Throws<InvalidDataException>(() => new LocalIcsCalendarProviderSnapshotSource(
            CalendarEventSourceKind.Google,
            accountIdentity,
            new byte[LocalIcsCalendarProviderSnapshotSource.MaximumDocumentBytes + 1],
            ObservedUtc,
            TimeZoneInfo.Utc));
    }

    private static LocalIcsCalendarProviderSnapshotSource Source(
        CalendarEventSourceKind sourceKind,
        string accountIdentity,
        string document,
        TimeZoneInfo? displayTimeZone = null,
        DateTimeOffset? observedUtc = null) =>
        new(
            sourceKind,
            accountIdentity,
            Encoding.UTF8.GetBytes(document
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Replace("\n", "\r\n", StringComparison.Ordinal)),
            observedUtc ?? ObservedUtc,
            displayTimeZone);

    private static string Sha256(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task<ModernMailStore> CreateStoreAsync(string testRoot)
    {
        var dataRoot = Path.Combine(testRoot, "data");
        var store = new ModernMailStore(Path.Combine(dataRoot, "mail.db"), dataRoot);
        await store.InitializeAsync();
        return store;
    }

    private static string CreateTestRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "NovaClient")) &&
                Directory.Exists(Path.Combine(directory.FullName, "TestLab")))
            {
                var root = Path.Combine(
                    directory.FullName, ".artifacts", "sync-tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                return root;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}
