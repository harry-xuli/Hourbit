using System.IO.Compression;
using System.Text.Json;
using Moment.Core.Domain;
using Moment.Infrastructure.Backup;
using Moment.Infrastructure.Data;
using Moment.TestSupport;

namespace Moment.Infrastructure.Tests.Backup;

public sealed class BackupServiceTests
{
    [Fact]
    public async Task Export_and_restore_round_trips_and_rejects_tampering()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path);
        var todo = new TodoItem(
            Guid.NewGuid(),
            "备份中的待办",
            DateTimeOffset.Parse("2026-07-29T01:00:00Z"),
            new DateOnly(2026, 8, 5),
            ReminderImportance.Important,
            IsCompleted: false,
            CompletedAt: null);
        var todos = await SqliteTodoRepository.OpenAsync(
            TestBackupFactory.DatabasePath(temp.Path), default);
        await todos.SaveAsync(todo, default);
        var deletedTodo = new TodoItem(
            Guid.NewGuid(),
            "备份中的已删除待办",
            todo.CreatedAt,
            null,
            ReminderImportance.Normal,
            IsCompleted: false,
            CompletedAt: null);
        var deletedAt = DateTimeOffset.Parse("2026-07-29T02:00:00Z");
        await todos.SaveAsync(deletedTodo, default);
        await todos.DeleteAsync(deletedTodo.Id, deletedAt, default);
        var service = TestBackupFactory.Create(temp.Path);
        var path = Path.Combine(temp.Path, "data.moment-backup");
        await service.ExportAsync(path, default);
        await TestBackupFactory.ChangeDatabaseAsync(temp.Path);
        await todos.DeleteAsync(todo.Id, default);
        await using (var connection = await DatabaseMigrator.OpenConnectionAsync(
                         TestBackupFactory.DatabasePath(temp.Path), default))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM todos WHERE id = $id;";
            command.Parameters.AddWithValue(
                "$id", deletedTodo.Id.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        await service.RestoreAsync(path, default);

        Assert.Equal("original", await TestBackupFactory.ReadMarkerAsync(temp.Path));
        var restoredTodos = await (await SqliteTodoRepository.OpenAsync(
                TestBackupFactory.DatabasePath(temp.Path), default))
            .GetAllAsync(default);
        Assert.Equal(todo, Assert.Single(restoredTodos));
        await using (var connection = await DatabaseMigrator.OpenConnectionAsync(
                         TestBackupFactory.DatabasePath(temp.Path), default))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT deleted_at FROM todos WHERE id = $id;";
            command.Parameters.AddWithValue(
                "$id", deletedTodo.Id.ToString("D"));
            Assert.Equal(
                deletedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                await command.ExecuteScalarAsync());
        }

        await TestBackupFactory.TamperWithDatabaseEntryAsync(path);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.RestoreAsync(path, default));
    }

    [Fact]
    public async Task Export_contains_only_database_and_utf8_manifest()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path);
        var path = Path.Combine(temp.Path, "data.moment-backup");

        await TestBackupFactory.Create(temp.Path).ExportAsync(path, default);

        using var archive = ZipFile.OpenRead(path);
        Assert.Equal(
            ["manifest.json", "moment.db"],
            archive.Entries.Select(entry => entry.FullName).Order().ToArray());
        var manifestEntry = Assert.Single(
            archive.Entries, entry => entry.FullName == "manifest.json");
        await using var stream = manifestEntry.Open();
        using var document = await JsonDocument.ParseAsync(stream);
        Assert.Equal(1, document.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal(4, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-29T01:02:03Z"),
            document.RootElement.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Matches(
            "^[0-9a-f]{64}$",
            document.RootElement.GetProperty("sha256").GetString());
    }

    [Theory]
    [InlineData("DROP TABLE todos;")]
    [InlineData("DROP TABLE todos; CREATE TABLE todos (id TEXT PRIMARY KEY, title TEXT NOT NULL);")]
    public async Task Export_rejects_a_logically_corrupt_schema_version_three_database(
        string corruptionSql)
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path);
        await using (var connection = await DatabaseMigrator.OpenConnectionAsync(
                         TestBackupFactory.DatabasePath(temp.Path), default))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = corruptionSql;
            await command.ExecuteNonQueryAsync();
        }
        var destination = Path.Combine(temp.Path, "corrupt.moment-backup");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            TestBackupFactory.Create(temp.Path).ExportAsync(destination, default));

        Assert.False(File.Exists(destination));
    }

    [Theory]
    [InlineData("DROP TABLE action_log;")]
    [InlineData("DROP INDEX ix_occurrences_active_state_due_at_utc; CREATE INDEX ix_occurrences_active_state_due_at_utc ON occurrences(due_at_utc, state) WHERE deleted_at IS NULL;")]
    [InlineData("DELETE FROM schema_info WHERE version = 2;")]
    public async Task Export_rejects_an_incomplete_or_malformed_version_four_schema(
        string corruptionSql)
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path);
        await using (var connection = await DatabaseMigrator.OpenConnectionAsync(
                         TestBackupFactory.DatabasePath(temp.Path), default))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = corruptionSql;
            await command.ExecuteNonQueryAsync();
        }
        var destination = Path.Combine(temp.Path, "corrupt-v4.moment-backup");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            TestBackupFactory.Create(temp.Path).ExportAsync(destination, default));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task Restore_rejects_a_hash_valid_version_four_package_missing_action_log()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path);
        var path = Path.Combine(temp.Path, "missing-action-log.moment-backup");
        var service = TestBackupFactory.Create(temp.Path);
        await service.ExportAsync(path, default);
        await TestBackupFactory.MutateDatabaseEntryAsync(
            path, "DROP TABLE action_log;");
        await TestBackupFactory.ChangeDatabaseAsync(temp.Path, "unchanged");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.RestoreAsync(path, default));

        Assert.Equal("unchanged",
            await TestBackupFactory.ReadMarkerAsync(temp.Path));
    }

    public static TheoryData<string> QuotedGlobCorruptions =>
        new()
        {
            "'[0-9] [0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'",
            "'[0-9];[0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'"
        };

    [Theory]
    [MemberData(nameof(QuotedGlobCorruptions))]
    public async Task Export_rejects_whitespace_or_semicolons_inside_the_due_date_glob_literal(
        string malformedGlob)
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path);
        await using (var connection = await DatabaseMigrator.OpenConnectionAsync(
                         TestBackupFactory.DatabasePath(temp.Path), default))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                DROP TABLE todos;
                CREATE TABLE todos (
                    id TEXT PRIMARY KEY,
                    title TEXT NOT NULL CHECK(length(trim(title)) BETWEEN 1 AND 200),
                    created_at TEXT NOT NULL,
                    due_date TEXT NULL CHECK(
                        due_date IS NULL OR (
                            length(due_date) = 10 AND
                            due_date GLOB {malformedGlob}
                        )
                    ),
                    importance INTEGER NOT NULL CHECK(importance IN (0, 1)),
                    is_completed INTEGER NOT NULL CHECK(is_completed IN (0, 1)),
                    completed_at TEXT NULL,
                    CHECK(
                        (is_completed = 0 AND completed_at IS NULL) OR
                        (is_completed = 1 AND completed_at IS NOT NULL)
                    )
                );
                """;
            await command.ExecuteNonQueryAsync();
        }
        var destination = Path.Combine(temp.Path, "quoted-corrupt.moment-backup");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            TestBackupFactory.Create(temp.Path).ExportAsync(destination, default));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task Export_accepts_uppercase_unquoted_primary_key_identifier()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path);
        var databasePath = TestBackupFactory.DatabasePath(temp.Path);
        await using (var connection =
                     await DatabaseMigrator.OpenConnectionAsync(databasePath, default))
        {
            string canonicalSql;
            await using (var select = connection.CreateCommand())
            {
                select.CommandText = """
                    SELECT sql FROM sqlite_master
                    WHERE type = 'table' AND name = 'todos';
                    """;
                canonicalSql = (string)(await select.ExecuteScalarAsync())!;
            }
            var mixedCaseSql = canonicalSql
                .Replace("id TEXT PRIMARY KEY", "ID TEXT PRIMARY KEY",
                    StringComparison.Ordinal);
            await using var rebuild = connection.CreateCommand();
            rebuild.CommandText = $"""
                DROP TABLE todos;
                {mixedCaseSql};
                CREATE INDEX ix_todos_active_due_date
                    ON todos(due_date, id) WHERE deleted_at IS NULL;
                CREATE INDEX ix_todos_deleted_due_date
                    ON todos(deleted_at, due_date);
                CREATE INDEX ix_todos_deleted_completed_at
                    ON todos(deleted_at, completed_at);
                """;
            await rebuild.ExecuteNonQueryAsync();
        }
        var destination = Path.Combine(temp.Path, "mixed-case.moment-backup");

        await TestBackupFactory.Create(temp.Path).ExportAsync(destination, default);

        Assert.True(File.Exists(destination));
    }

    [Fact]
    public async Task Automatic_backups_keep_the_seven_newest_successful_packages()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path);
        var now = DateTimeOffset.Parse("2026-07-20T01:02:03Z");
        var service = TestBackupFactory.Create(temp.Path, () => now);

        for (var index = 0; index < 9; index++)
        {
            now = now.AddDays(1);
            await service.CreateDailyBackupAsync(default);
        }

        var names = Directory.GetFiles(
                TestBackupFactory.BackupDirectory(temp.Path),
                "*.moment-backup")
            .Select(Path.GetFileName)
            .Order()
            .ToArray();
        Assert.Equal(7, names.Length);
        Assert.Equal("moment-20260723T010203000Z.moment-backup", names[0]);
        Assert.Equal("moment-20260729T010203000Z.moment-backup", names[^1]);
    }

    [Fact]
    public async Task Retention_ignores_manual_and_malformed_moment_packages()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path);
        Directory.CreateDirectory(TestBackupFactory.BackupDirectory(temp.Path));
        var manual = Path.Combine(
            TestBackupFactory.BackupDirectory(temp.Path),
            "moment-export-user.moment-backup");
        var malformed = Path.Combine(
            TestBackupFactory.BackupDirectory(temp.Path),
            "moment-99999999T999999999Z.moment-backup");
        await TestBackupFactory.Create(temp.Path).ExportAsync(manual, default);
        File.Copy(manual, malformed);
        var now = DateTimeOffset.Parse("2026-07-20T01:02:03Z");
        var service = TestBackupFactory.Create(temp.Path, () => now);

        for (var index = 0; index < 9; index++)
        {
            now = now.AddDays(1);
            await service.CreateDailyBackupAsync(default);
        }

        Assert.True(File.Exists(manual));
        Assert.True(File.Exists(malformed));
        var automatic = Directory.GetFiles(
                TestBackupFactory.BackupDirectory(temp.Path),
                "moment-202607*T010203000Z.moment-backup")
            .Select(Path.GetFileName)
            .Order()
            .ToArray();
        Assert.Equal(7, automatic.Length);
        Assert.Equal("moment-20260723T010203000Z.moment-backup", automatic[0]);
    }

    [Fact]
    public async Task Daily_backup_skips_a_second_package_on_the_same_local_date()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path);
        var now = DateTimeOffset.Parse("2026-07-29T01:02:03Z");
        var service = TestBackupFactory.Create(temp.Path, () => now);
        var first = await service.CreateDailyBackupAsync(default);
        now = DateTimeOffset.Parse("2026-07-29T15:59:59Z");

        var second = await service.CreateDailyBackupAsync(default);

        Assert.Equal(first, second);
        Assert.Single(Directory.GetFiles(
            TestBackupFactory.BackupDirectory(temp.Path),
            "*.moment-backup"));
    }

    [Fact]
    public async Task Cancellation_after_package_commit_does_not_duplicate_same_day_backup()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path);
        var now = DateTimeOffset.Parse("2026-07-29T01:02:03Z");
        using var cancellation = new CancellationTokenSource();
        var storage = new CancelAfterPackageCommitStorage(cancellation);
        var service = new BackupService(
            TestBackupFactory.DatabasePath(temp.Path),
            TestBackupFactory.BackupDirectory(temp.Path),
            utcNow: () => now,
            localZone: TestBackupFactory.LocalZone,
            storageOperations: storage);

        var first = await service.CreateDailyBackupAsync(cancellation.Token);
        now = now.AddHours(1);
        var second = await service.CreateDailyBackupAsync(default);

        Assert.Equal(first, second);
        Assert.Single(StrictAutomaticBackups(temp.Path));
    }

    [Fact]
    public async Task Settings_failure_after_package_commit_does_not_duplicate_same_day_backup()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path);
        var now = DateTimeOffset.Parse("2026-07-29T01:02:03Z");
        var maintenance = new FaultingDailyMaintenance
        {
            FailNextWrite = true
        };
        var service = new BackupService(
            TestBackupFactory.DatabasePath(temp.Path),
            TestBackupFactory.BackupDirectory(temp.Path),
            utcNow: () => now,
            localZone: TestBackupFactory.LocalZone,
            dailyMaintenance: maintenance);

        await Assert.ThrowsAsync<IOException>(
            () => service.CreateDailyBackupAsync(default));
        now = now.AddHours(1);
        var recovered = await service.CreateDailyBackupAsync(default);

        Assert.True(File.Exists(recovered));
        Assert.Single(StrictAutomaticBackups(temp.Path));
    }

    [Fact]
    public async Task Retention_failure_surfaces_after_success_marker_and_does_not_duplicate()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path);
        var now = DateTimeOffset.Parse("2026-07-29T01:02:03Z");
        var maintenance = new FaultingDailyMaintenance
        {
            FailNextRetention = true
        };
        var service = new BackupService(
            TestBackupFactory.DatabasePath(temp.Path),
            TestBackupFactory.BackupDirectory(temp.Path),
            utcNow: () => now,
            localZone: TestBackupFactory.LocalZone,
            dailyMaintenance: maintenance);

        await Assert.ThrowsAsync<IOException>(
            () => service.CreateDailyBackupAsync(default));
        now = now.AddHours(1);
        var recovered = await service.CreateDailyBackupAsync(default);

        Assert.True(File.Exists(recovered));
        Assert.Single(StrictAutomaticBackups(temp.Path));
    }

    [Fact]
    public async Task Restore_rejects_unsafe_or_incompatible_packages_before_stopping()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path);
        var valid = Path.Combine(temp.Path, "valid.moment-backup");
        var service = TestBackupFactory.Create(temp.Path);
        await service.ExportAsync(valid, default);
        var mutations = new Func<string, Task>[]
        {
            TestBackupFactory.AddUnexpectedEntryAsync,
            TestBackupFactory.AddTraversalEntryAsync,
            TestBackupFactory.AddDuplicateDatabaseEntryAsync,
            path => TestBackupFactory.SetManifestSchemaVersionAsync(path, 999)
        };

        foreach (var mutate in mutations)
        {
            var path = Path.Combine(temp.Path, $"{Guid.NewGuid():N}.moment-backup");
            File.Copy(valid, path);
            await mutate(path);
            var lifecycle = new RecordingLifecycle();
            var guarded = TestBackupFactory.Create(temp.Path, lifecycle: lifecycle);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => guarded.RestoreAsync(path, default));

            Assert.Empty(lifecycle.Events);
        }

        var corrupt = Path.Combine(temp.Path, "corrupt.moment-backup");
        await File.WriteAllBytesAsync(corrupt, [1, 2, 3, 4]);
        var corruptLifecycle = new RecordingLifecycle();
        await Assert.ThrowsAsync<InvalidDataException>(
            () => TestBackupFactory.Create(
                temp.Path, lifecycle: corruptLifecycle).RestoreAsync(corrupt, default));
        Assert.Empty(corruptLifecycle.Events);
    }

    [Fact]
    public async Task Restore_rejects_database_declared_over_one_gib_before_stopping()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path);
        var path = Path.Combine(temp.Path, "oversized.moment-backup");
        await TestBackupFactory.Create(temp.Path).ExportAsync(path, default);
        await TestBackupFactory.DeclareDatabaseEntryLargerThanLimitAsync(path);
        var lifecycle = new RecordingLifecycle();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => TestBackupFactory.Create(
                temp.Path, lifecycle: lifecycle).RestoreAsync(path, default));

        Assert.Contains("1 GiB", exception.Message, StringComparison.Ordinal);
        Assert.Empty(lifecycle.Events);
    }

    [Fact]
    public async Task Restore_rejects_extra_manifest_property_before_stopping()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path);
        var path = Path.Combine(temp.Path, "extra-manifest.moment-backup");
        await TestBackupFactory.Create(temp.Path).ExportAsync(path, default);
        await TestBackupFactory.AddUnexpectedManifestPropertyAsync(path);
        var lifecycle = new RecordingLifecycle();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => TestBackupFactory.Create(
                temp.Path, lifecycle: lifecycle).RestoreAsync(path, default));

        Assert.Empty(lifecycle.Events);
    }

    [Fact]
    public async Task Restore_stream_limits_manifest_when_zip_length_is_forged()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path);
        var path = Path.Combine(temp.Path, "oversized-manifest.moment-backup");
        await TestBackupFactory.Create(temp.Path).ExportAsync(path, default);
        await TestBackupFactory.ForgeOversizedManifestLengthAsync(path);
        var lifecycle = new RecordingLifecycle();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => TestBackupFactory.Create(
                temp.Path, lifecycle: lifecycle).RestoreAsync(path, default));

        Assert.Contains(
            "manifest is too large",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(lifecycle.Events);
    }

    [Fact]
    public async Task Restore_normalizes_wrong_manifest_property_type_before_stopping()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path);
        var path = Path.Combine(temp.Path, "wrong-type.moment-backup");
        await TestBackupFactory.Create(temp.Path).ExportAsync(path, default);
        await TestBackupFactory.SetManifestFormatVersionToStringAsync(path);
        var lifecycle = new RecordingLifecycle();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => TestBackupFactory.Create(
                temp.Path, lifecycle: lifecycle).RestoreAsync(path, default));

        Assert.Empty(lifecycle.Events);
    }

    [Fact]
    public async Task Failed_export_preserves_existing_destination_and_cleans_staging_files()
    {
        using var temp = new TempDirectory();
        var destination = Path.Combine(temp.Path, "existing.moment-backup");
        var existingBytes = new byte[] { 9, 8, 7, 6 };
        await File.WriteAllBytesAsync(destination, existingBytes);
        await File.WriteAllBytesAsync(
            TestBackupFactory.DatabasePath(temp.Path),
            [1, 2, 3, 4]);

        await Assert.ThrowsAnyAsync<Exception>(
            () => TestBackupFactory.Create(temp.Path)
                .ExportAsync(destination, default));

        Assert.Equal(existingBytes, await File.ReadAllBytesAsync(destination));
        Assert.Empty(
            Directory.GetFiles(temp.Path, ".moment-*.tmp"));
        Assert.Empty(
            Directory.GetFiles(temp.Path, ".moment-*.snapshot.db"));
    }

    [Fact]
    public async Task Restore_rolls_back_database_and_restarts_when_refresh_fails()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path, "backup");
        var path = Path.Combine(temp.Path, "data.moment-backup");
        await TestBackupFactory.Create(temp.Path).ExportAsync(path, default);
        await TestBackupFactory.ChangeDatabaseAsync(temp.Path, "current");
        var lifecycle = new RecordingLifecycle { FailFirstRefresh = true };
        var service = TestBackupFactory.Create(temp.Path, lifecycle: lifecycle);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RestoreAsync(path, default));

        Assert.Equal("current", await TestBackupFactory.ReadMarkerAsync(temp.Path));
        Assert.Equal(
            ["stop", "start", "refresh", "stop", "start", "refresh"],
            lifecycle.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Partial_safety_snapshot_is_never_installed_when_creation_fails(
        bool cancelDuringSnapshot)
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path, "backup");
        var path = Path.Combine(temp.Path, "data.moment-backup");
        await TestBackupFactory.Create(temp.Path).ExportAsync(path, default);
        await TestBackupFactory.ChangeDatabaseAsync(temp.Path, "current");
        var lifecycle = new RecordingLifecycle();
        var storage = new PartialSnapshotStorage(
            cancelDuringSnapshot
                ? new OperationCanceledException("snapshot cancelled")
                : new IOException("snapshot failed"));
        var service = new BackupService(
            TestBackupFactory.DatabasePath(temp.Path),
            TestBackupFactory.BackupDirectory(temp.Path),
            lifecycle,
            () => DateTimeOffset.Parse("2026-07-29T01:02:03Z"),
            TestBackupFactory.LocalZone,
            storage);

        await Assert.ThrowsAnyAsync<Exception>(
            () => service.RestoreAsync(path, default));

        Assert.Equal("current", await TestBackupFactory.ReadMarkerAsync(temp.Path));
        Assert.Equal(["stop", "start", "refresh"], lifecycle.Events);
        Assert.False(storage.AtomicReplaceCalled);
    }

    [Fact]
    public async Task Refresh_failure_stops_restarted_lifecycle_before_rollback()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path, "backup");
        var path = Path.Combine(temp.Path, "data.moment-backup");
        await TestBackupFactory.Create(temp.Path).ExportAsync(path, default);
        await TestBackupFactory.ChangeDatabaseAsync(temp.Path, "current");
        var lifecycle = new StatefulLifecycle(failFirstRefresh: true);
        var storage = new LifecycleAwareStorage(lifecycle);
        var service = new BackupService(
            TestBackupFactory.DatabasePath(temp.Path),
            TestBackupFactory.BackupDirectory(temp.Path),
            lifecycle,
            () => DateTimeOffset.Parse("2026-07-29T01:02:03Z"),
            TestBackupFactory.LocalZone,
            storage);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RestoreAsync(path, default));

        Assert.Equal("current", await TestBackupFactory.ReadMarkerAsync(temp.Path));
        Assert.Equal(
            ["stop", "start", "refresh", "stop", "start", "refresh"],
            lifecycle.Events);
        Assert.Empty(Directory.GetFiles(
            temp.Path, ".moment-*.restore-safety.db"));
    }

    [Fact]
    public async Task Rollback_replace_failure_retains_valid_safety_and_does_not_restart()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path, "backup");
        var path = Path.Combine(temp.Path, "data.moment-backup");
        await TestBackupFactory.Create(temp.Path).ExportAsync(path, default);
        await TestBackupFactory.ChangeDatabaseAsync(temp.Path, "current");
        var lifecycle = new StatefulLifecycle(failFirstRefresh: true);
        var storage = new LifecycleAwareStorage(lifecycle)
        {
            FailSafetyReplace = true
        };
        var service = new BackupService(
            TestBackupFactory.DatabasePath(temp.Path),
            TestBackupFactory.BackupDirectory(temp.Path),
            lifecycle,
            () => DateTimeOffset.Parse("2026-07-29T01:02:03Z"),
            TestBackupFactory.LocalZone,
            storage);

        await Assert.ThrowsAsync<AggregateException>(
            () => service.RestoreAsync(path, default));

        Assert.Equal(
            ["stop", "start", "refresh", "stop"],
            lifecycle.Events);
        var safetyPath = Assert.Single(Directory.GetFiles(
            temp.Path, ".moment-*.restore-safety.db"));
        Assert.Equal("current", await ReadMarkerAsync(safetyPath));
        Assert.Equal("backup", await TestBackupFactory.ReadMarkerAsync(temp.Path));
    }

    [Fact]
    public async Task Export_is_a_consistent_sqlite_snapshot_while_wal_is_active()
    {
        using var temp = new TempDirectory();
        await TestBackupFactory.InitializeAsync(temp.Path, "before");
        var path = Path.Combine(temp.Path, "wal.moment-backup");
        await using (var writer = new Microsoft.Data.Sqlite.SqliteConnection(
                         $"Data Source={TestBackupFactory.DatabasePath(temp.Path)};Pooling=False"))
        {
            await writer.OpenAsync();
            await using (var wal = writer.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                await wal.ExecuteNonQueryAsync();
            }
            await using (var update = writer.CreateCommand())
            {
                update.CommandText =
                    "UPDATE settings SET value = 'in-wal' WHERE key = 'test_marker';";
                await update.ExecuteNonQueryAsync();
            }

            await TestBackupFactory.Create(temp.Path).ExportAsync(path, default);
        }

        await TestBackupFactory.ChangeDatabaseAsync(temp.Path, "changed");
        await TestBackupFactory.Create(temp.Path).RestoreAsync(path, default);

        Assert.Equal("in-wal", await TestBackupFactory.ReadMarkerAsync(temp.Path));
    }

    private sealed class RecordingLifecycle : IBackupRestoreLifecycle
    {
        private bool _refreshFailed;
        public List<string> Events { get; } = [];
        public bool FailFirstRefresh { get; init; }

        public Task StopAsync(CancellationToken ct)
        {
            Events.Add("stop");
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken ct)
        {
            Events.Add("start");
            return Task.CompletedTask;
        }

        public Task RefreshAsync(CancellationToken ct)
        {
            Events.Add("refresh");
            if (FailFirstRefresh && !_refreshFailed)
            {
                _refreshFailed = true;
                throw new InvalidOperationException("refresh failed");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class PartialSnapshotStorage(Exception failure) :
        IBackupStorageOperations
    {
        public bool AtomicReplaceCalled { get; private set; }

        public Task CreateSqliteSnapshotAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken ct)
        {
            File.WriteAllBytes(destinationPath, [1, 2, 3, 4]);
            return Task.FromException(failure);
        }

        public void AtomicReplace(string sourcePath, string destinationPath)
        {
            AtomicReplaceCalled = true;
            File.Replace(sourcePath, destinationPath, null);
        }
    }

    private sealed class StatefulLifecycle(bool failFirstRefresh) :
        IBackupRestoreLifecycle
    {
        private bool _refreshFailed;
        public bool IsRunning { get; private set; } = true;
        public List<string> Events { get; } = [];

        public Task StopAsync(CancellationToken ct)
        {
            Events.Add("stop");
            IsRunning = false;
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken ct)
        {
            Events.Add("start");
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task RefreshAsync(CancellationToken ct)
        {
            Events.Add("refresh");
            if (failFirstRefresh && !_refreshFailed)
            {
                _refreshFailed = true;
                throw new InvalidOperationException("refresh failed");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class LifecycleAwareStorage(StatefulLifecycle lifecycle) :
        IBackupStorageOperations
    {
        public bool FailSafetyReplace { get; init; }

        public async Task CreateSqliteSnapshotAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken ct)
        {
            await using var connection =
                await Moment.Infrastructure.Data.DatabaseMigrator
                    .OpenConnectionAsync(sourcePath, ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $destination;";
            command.Parameters.AddWithValue("$destination", destinationPath);
            await command.ExecuteNonQueryAsync(ct);
        }

        public void AtomicReplace(string sourcePath, string destinationPath)
        {
            var isRollback = sourcePath.EndsWith(
                                 ".restore-safety.db",
                                 StringComparison.Ordinal) ||
                             sourcePath.EndsWith(
                                 ".rollback-install.db",
                                 StringComparison.Ordinal);
            if (isRollback && lifecycle.IsRunning)
                throw new IOException("database locked by running scheduler");
            if (isRollback && FailSafetyReplace)
                throw new IOException("rollback replace failed");
            if (File.Exists(destinationPath))
                File.Replace(sourcePath, destinationPath, null);
            else
                File.Move(sourcePath, destinationPath);
        }
    }

    private static async Task<string?> ReadMarkerAsync(string databasePath)
    {
        await using var connection =
            await Moment.Infrastructure.Data.DatabaseMigrator
                .OpenConnectionAsync(databasePath, default);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT value FROM settings WHERE key = 'test_marker';";
        return (string?)await command.ExecuteScalarAsync();
    }

    private static string[] StrictAutomaticBackups(string root) =>
        Directory.GetFiles(
            TestBackupFactory.BackupDirectory(root),
            "moment-????????T?????????Z.moment-backup");

    private sealed class CancelAfterPackageCommitStorage(
        CancellationTokenSource cancellation) : IBackupStorageOperations
    {
        public async Task CreateSqliteSnapshotAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken ct)
        {
            await using var connection =
                await Moment.Infrastructure.Data.DatabaseMigrator
                    .OpenConnectionAsync(sourcePath, ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $destination;";
            command.Parameters.AddWithValue("$destination", destinationPath);
            await command.ExecuteNonQueryAsync(ct);
        }

        public void AtomicReplace(string sourcePath, string destinationPath)
        {
            if (File.Exists(destinationPath))
                File.Replace(sourcePath, destinationPath, null);
            else
                File.Move(sourcePath, destinationPath);
            if (destinationPath.EndsWith(
                    ".moment-backup", StringComparison.Ordinal))
            {
                cancellation.Cancel();
            }
        }
    }

    private sealed class FaultingDailyMaintenance :
        IBackupDailyMaintenance
    {
        public bool FailNextWrite { get; set; }
        public bool FailNextRetention { get; set; }

        public async Task<string?> ReadLastSuccessfulLocalDateAsync(
            string databasePath,
            CancellationToken ct)
        {
            await using var connection =
                await Moment.Infrastructure.Data.DatabaseMigrator
                    .OpenConnectionAsync(databasePath, ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT value FROM settings
                WHERE key = 'last_successful_local_backup_date';
                """;
            return (string?)await command.ExecuteScalarAsync(ct);
        }

        public async Task WriteLastSuccessfulLocalDateAsync(
            string databasePath,
            string value,
            CancellationToken ct)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new IOException("settings write failed");
            }
            await using var connection =
                await Moment.Infrastructure.Data.DatabaseMigrator
                    .OpenConnectionAsync(databasePath, ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO settings(key, value)
                VALUES ('last_successful_local_backup_date', $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$value", value);
            await command.ExecuteNonQueryAsync(ct);
        }

        public void RetainNewestAutomaticBackups(
            IReadOnlyList<string> paths,
            int keep)
        {
            if (FailNextRetention)
            {
                FailNextRetention = false;
                throw new IOException("retention failed");
            }
            foreach (var path in paths.Skip(keep))
                File.Delete(path);
        }
    }
}
