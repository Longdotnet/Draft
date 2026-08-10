from pathlib import Path


def replace_once(path: str, old: str, new: str):
    p = Path(path)
    text = p.read_text(encoding='utf-8')
    if new in text:
        return
    if old not in text:
        raise SystemExit(f'anchor not found in {path}: {old[:160]!r}')
    p.write_text(text.replace(old, new, 1), encoding='utf-8')


service = 'server/VolleyDraft.Api/Services/ZaloTeamCardService.cs'
replace_once(
    service,
    'using VolleyDraft.Api.Models;\n',
    'using VolleyDraft.Api.Models;\nusing VolleyDraft.Api.Services.Posters;\n')
replace_once(
    service,
    '''        if (session is null) return null;\n\n        var hydrated = await zaloIntegration.HydrateMissingMemberAvatarsAsync(session.AdminUserId, session.Id);''',
    '''        if (session is null) return null;\n\n        var posterTemplateId = 1;\n        try\n        {\n            var assignment = await TeamPosterRotationStore.EnsureAssignedAsync(db, session.Id, cancellationToken);\n            posterTemplateId = assignment.TemplateId;\n        }\n        catch (Exception exception)\n        {\n            // Keep image generation available even if the poster-deck persistence layer\n            // is temporarily unavailable. Template 1 is the safe visual fallback.\n            logger.LogWarning(exception,\n                "Could not assign poster template for Session={SessionId}; using Neon Arena fallback",\n                session.Id);\n        }\n\n        var hydrated = await zaloIntegration.HydrateMissingMemberAvatarsAsync(session.AdminUserId, session.Id);''')
replace_once(
    service,
    '''            poster = TournamentTeamPosterPng.Render(\n                session.Name,\n                session.StartTime,\n                session.Location,\n                teams);''',
    '''            poster = TeamPosterRendererRegistry.Render(\n                posterTemplateId,\n                session.Name,\n                session.StartTime,\n                session.Location,\n                teams);''')
replace_once(
    service,
    '''                "Tournament team poster render failed for Session={SessionId}; falling back to legacy card",\n                session.Id);''',
    '''                "Tournament team poster render failed for Session={SessionId} Template={TemplateId}; falling back to legacy card",\n                session.Id,\n                posterTemplateId);''')

store = 'server/VolleyDraft.Api/Services/TeamPosterRotationStore.cs'
p = Path(store)
text = p.read_text(encoding='utf-8')
text = text.replace('    private static int _schemaReady;\n    private static readonly SemaphoreSlim SchemaGate = new(1, 1);\n', '')
old_schema = '''    private static async Task EnsureSchemaAsync(VolleyDraftDbContext db, CancellationToken cancellationToken)\n    {\n        if (Volatile.Read(ref _schemaReady) == 1) return;\n        await SchemaGate.WaitAsync(cancellationToken);\n        try\n        {\n            if (_schemaReady == 1) return;\n            var connection = db.Database.GetDbConnection();\n            if (connection.State != ConnectionState.Open)\n                await connection.OpenAsync(cancellationToken);\n            var postgres = (db.Database.ProviderName ?? string.Empty).Contains("Npgsql", StringComparison.OrdinalIgnoreCase);\n            var timestampType = postgres ? "timestamp with time zone" : "TEXT";\n\n            await ExecuteNonQueryAsync(connection, null, $"""\n                CREATE TABLE IF NOT EXISTS "TeamPosterAssignments" (\n                    "SessionId" TEXT PRIMARY KEY,\n                    "ZaloConnectionId" TEXT NULL,\n                    "GroupId" TEXT NULL,\n                    "TemplateId" INTEGER NOT NULL,\n                    "CycleNumber" INTEGER NOT NULL,\n                    "AssignedAt" {timestampType} NOT NULL\n                );\n                """, cancellationToken);\n            await ExecuteNonQueryAsync(connection, null, $"""\n                CREATE TABLE IF NOT EXISTS "TeamPosterRotationStates" (\n                    "RotationKey" TEXT PRIMARY KEY,\n                    "ZaloConnectionId" TEXT NOT NULL,\n                    "GroupId" TEXT NOT NULL,\n                    "RemainingTemplateIdsJson" TEXT NOT NULL,\n                    "LastAssignedTemplateId" INTEGER NULL,\n                    "CycleNumber" INTEGER NOT NULL,\n                    "UpdatedAt" {timestampType} NOT NULL\n                );\n                """, cancellationToken);\n            await ExecuteNonQueryAsync(connection, null,\n                "CREATE INDEX IF NOT EXISTS \\\"IX_TeamPosterAssignments_Group_Assigned\\\" ON \\\"TeamPosterAssignments\\\" (\\\"ZaloConnectionId\\\", \\\"GroupId\\\", \\\"AssignedAt\\\");",\n                cancellationToken);\n            Volatile.Write(ref _schemaReady, 1);\n        }\n        finally\n        {\n            SchemaGate.Release();\n        }\n    }'''
new_schema = '''    private static async Task EnsureSchemaAsync(VolleyDraftDbContext db, CancellationToken cancellationToken)\n    {\n        // CREATE IF NOT EXISTS is deliberately cheap and connection-local. Avoid a process-wide\n        // "schema ready" flag because tests and multi-database deployments can use different DBs\n        // inside the same process.\n        var connection = db.Database.GetDbConnection();\n        if (connection.State != ConnectionState.Open)\n            await connection.OpenAsync(cancellationToken);\n        var postgres = (db.Database.ProviderName ?? string.Empty).Contains("Npgsql", StringComparison.OrdinalIgnoreCase);\n        var timestampType = postgres ? "timestamp with time zone" : "TEXT";\n\n        await ExecuteNonQueryAsync(connection, null, $"""\n            CREATE TABLE IF NOT EXISTS "TeamPosterAssignments" (\n                "SessionId" TEXT PRIMARY KEY,\n                "ZaloConnectionId" TEXT NULL,\n                "GroupId" TEXT NULL,\n                "TemplateId" INTEGER NOT NULL,\n                "CycleNumber" INTEGER NOT NULL,\n                "AssignedAt" {timestampType} NOT NULL\n            );\n            """, cancellationToken);\n        await ExecuteNonQueryAsync(connection, null, $"""\n            CREATE TABLE IF NOT EXISTS "TeamPosterRotationStates" (\n                "RotationKey" TEXT PRIMARY KEY,\n                "ZaloConnectionId" TEXT NOT NULL,\n                "GroupId" TEXT NOT NULL,\n                "RemainingTemplateIdsJson" TEXT NOT NULL,\n                "LastAssignedTemplateId" INTEGER NULL,\n                "CycleNumber" INTEGER NOT NULL,\n                "UpdatedAt" {timestampType} NOT NULL\n            );\n            """, cancellationToken);\n        await ExecuteNonQueryAsync(connection, null,\n            "CREATE INDEX IF NOT EXISTS \\\"IX_TeamPosterAssignments_Group_Assigned\\\" ON \\\"TeamPosterAssignments\\\" (\\\"ZaloConnectionId\\\", \\\"GroupId\\\", \\\"AssignedAt\\\");",\n            cancellationToken);\n    }'''
if old_schema not in text:
    raise SystemExit('schema method anchor not found')
text = text.replace(old_schema, new_schema, 1)
# Simplify deck RNG signature; cryptographic GetInt32 already provides unbiased shuffle.
text = text.replace('public static IReadOnlyList<int> BuildShuffledDeck(int? lastAssignedTemplateId, RandomNumberGenerator? rng = null)', 'public static IReadOnlyList<int> BuildShuffledDeck(int? lastAssignedTemplateId)')
text = text.replace('''        var ownsRng = rng is null;\n        rng ??= RandomNumberGenerator.Create();\n        try\n        {\n            for (var index = values.Length - 1; index > 0; index -= 1)\n            {\n                var target = RandomNumberGenerator.GetInt32(index + 1);\n                (values[index], values[target]) = (values[target], values[index]);\n            }\n        }\n        finally\n        {\n            if (ownsRng) rng.Dispose();\n        }''', '''        for (var index = values.Length - 1; index > 0; index -= 1)\n        {\n            var target = RandomNumberGenerator.GetInt32(index + 1);\n            (values[index], values[target]) = (values[target], values[index]);\n        }''')
p.write_text(text, encoding='utf-8')

# Tests: use public registry dimensions and export every poster when requested.
tests = 'server/VolleyDraft.Api.Tests/TeamPosterCollectionTests.cs'
replace_once(tests, 'AssertPng(bytes);\n            hashes.Add', 'AssertPng(bytes);\n            WritePreviewIfRequested(templateId, bytes);\n            hashes.Add')
replace_once(tests, 'Assert.Equal(PosterDrawing.Width, bitmap.Width);\n        Assert.Equal(PosterDrawing.Height, bitmap.Height);', 'Assert.Equal(TeamPosterRendererRegistry.Width, bitmap.Width);\n        Assert.Equal(TeamPosterRendererRegistry.Height, bitmap.Height);')
replace_once(
    tests,
    '''    private static void AssertPng(byte[] bytes)\n    {''',
    '''    private static void WritePreviewIfRequested(int templateId, byte[] bytes)\n    {\n        var directory = Environment.GetEnvironmentVariable("TEAM_POSTER_PREVIEW_DIR");\n        if (string.IsNullOrWhiteSpace(directory)) return;\n        Directory.CreateDirectory(directory);\n        File.WriteAllBytes(Path.Combine(directory, $"poster-{templateId:00}.png"), bytes);\n    }\n\n    private static void AssertPng(byte[] bytes)\n    {''')

print('poster collection integration patch applied')
