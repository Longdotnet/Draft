using VolleyDraft.Api.Services;
using Xunit;

namespace VolleyDraft.Api.Tests.Fuzzing;

public sealed class ZaloPollScheduleFuzzTests
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private static readonly string[] SundayTokens =
        ["CN", "cn", "Chủ nhật", "chủ nhật", "Chu nhat", "CHỦ NHẬT"];
    private static readonly string[] DateSeparators = ["/", "-", "."];
    private static readonly string[] Joiners = [" ", "  ", "\t", "\n", " - "];
    private static readonly string[] Prefixes = ["", "lịch ", "kèo "];
    private static readonly string[] Suffixes = ["", " nha", " lúc 17:45", " 17h45"];
    private static readonly string[] SeparatorSpacing = ["", " ", "  "];

    [Fact]
    public void Production_incident_seed_keeps_explicit_sunday_date_authoritative_across_mutations()
    {
        var created = new DateTimeOffset(2026, 9, 5, 20, 0, 0, VietnamOffset);
        var now = new DateTimeOffset(2026, 9, 5, 21, 0, 0, VietnamOffset);
        var expected = new DateTimeOffset(2026, 9, 13, 17, 45, 0, VietnamOffset);

        for (var seed = 1; seed <= 256; seed += 1)
        {
            var optionText = MutateSundayDateOption(seed, 13, 9, 2026);
            var poll = BuildPoll(created, optionText);

            var extraction = ZaloPollScheduleParser.ExtractSchedule(
                poll,
                new ZaloTrackedGroupData(),
                now);

            Assert.True(
                extraction.Issues.Count == 0,
                $"Expected no schedule issue. {Reproducer(seed, optionText)}; issues={string.Join(',', extraction.Issues.Select(issue => issue.Code))}");
            Assert.True(
                extraction.Candidates.Count == 1,
                $"Expected exactly one candidate. {Reproducer(seed, optionText)}; count={extraction.Candidates.Count}");

            var candidate = extraction.Candidates[0];
            Assert.True(
                candidate.DayKey == "CN" && candidate.StartTime == expected,
                $"Explicit date drifted. {Reproducer(seed, optionText)}; actual={candidate.DayKey}:{candidate.StartTime:O}");
            Assert.True(
                ZaloPollScheduleParser.ValidateCandidateConsistency(poll, candidate, out var reason),
                $"Preview candidate stopped matching its authoritative source. {Reproducer(seed, optionText)}; reason={reason}");
        }
    }

    [Fact]
    public void Sunday_weekday_conflict_fails_closed_across_the_same_mutation_family()
    {
        var created = new DateTimeOffset(2026, 9, 5, 20, 0, 0, VietnamOffset);
        var now = new DateTimeOffset(2026, 9, 5, 21, 0, 0, VietnamOffset);

        for (var seed = 1; seed <= 192; seed += 1)
        {
            var optionText = MutateSundayDateOption(seed, 12, 9, 2026);
            var extraction = ZaloPollScheduleParser.ExtractSchedule(
                BuildPoll(created, optionText),
                new ZaloTrackedGroupData(),
                now);

            Assert.True(
                extraction.Candidates.Count == 0,
                $"Conflicting weekday/date produced a candidate. {Reproducer(seed, optionText)}");
            Assert.True(
                extraction.Issues.Count == 1 && extraction.Issues[0].Code == "weekday_date_conflict",
                $"Expected weekday_date_conflict. {Reproducer(seed, optionText)}; issues={string.Join(',', extraction.Issues.Select(issue => issue.Code))}");
        }
    }

    [Fact]
    public void Invalid_explicit_date_fails_closed_across_the_same_mutation_family()
    {
        var created = new DateTimeOffset(2026, 9, 5, 20, 0, 0, VietnamOffset);
        var now = new DateTimeOffset(2026, 9, 5, 21, 0, 0, VietnamOffset);

        for (var seed = 1; seed <= 192; seed += 1)
        {
            var optionText = MutateSundayDateOption(seed, 31, 2, 2026);
            var extraction = ZaloPollScheduleParser.ExtractSchedule(
                BuildPoll(created, optionText),
                new ZaloTrackedGroupData(),
                now);

            Assert.True(
                extraction.Candidates.Count == 0,
                $"Invalid calendar date produced a candidate. {Reproducer(seed, optionText)}");
            Assert.True(
                extraction.Issues.Count == 1 && extraction.Issues[0].Code == "invalid_explicit_date",
                $"Expected invalid_explicit_date. {Reproducer(seed, optionText)}; issues={string.Join(',', extraction.Issues.Select(issue => issue.Code))}");
        }
    }

    [Fact]
    public void Yearless_new_year_seed_resolves_forward_across_separator_and_year_form_mutations()
    {
        var created = new DateTimeOffset(2026, 12, 31, 20, 0, 0, VietnamOffset);
        var now = new DateTimeOffset(2026, 12, 31, 21, 0, 0, VietnamOffset);
        var expected = new DateTimeOffset(2027, 1, 2, 17, 45, 0, VietnamOffset);

        for (var seed = 1; seed <= 128; seed += 1)
        {
            var optionText = MutateDateOnlyOption(seed, 2, 1, 2027);
            var extraction = ZaloPollScheduleParser.ExtractSchedule(
                BuildPoll(created, optionText),
                new ZaloTrackedGroupData(),
                now);

            Assert.True(
                extraction.Issues.Count == 0 && extraction.Candidates.Count == 1,
                $"New-year date mutation failed extraction. {Reproducer(seed, optionText)}; issues={string.Join(',', extraction.Issues.Select(issue => issue.Code))}");
            Assert.True(
                extraction.Candidates[0].StartTime == expected,
                $"New-year date resolved to the wrong occurrence. {Reproducer(seed, optionText)}; actual={extraction.Candidates[0].StartTime:O}");
        }
    }

    private static string MutateSundayDateOption(int seed, int day, int month, int year)
    {
        var random = new StableFuzzRandom(seed);
        var dayToken = random.Pick(SundayTokens);
        var date = MutateDate(random, day, month, year);
        var joiner = random.Pick(Joiners);
        var body = random.NextBool()
            ? $"{dayToken}{joiner}{date}"
            : $"{date}{joiner}{dayToken}";
        return $"{random.Pick(Prefixes)}{body}{random.Pick(Suffixes)}";
    }

    private static string MutateDateOnlyOption(int seed, int day, int month, int year)
    {
        var random = new StableFuzzRandom(seed);
        return $"{random.Pick(Prefixes)}{MutateDate(random, day, month, year)}{random.Pick(Suffixes)}";
    }

    private static string MutateDate(StableFuzzRandom random, int day, int month, int year)
    {
        var separator = random.Pick(DateSeparators);
        var leftSpace = random.Pick(SeparatorSpacing);
        var rightSpace = random.Pick(SeparatorSpacing);
        var date = $"{day}{leftSpace}{separator}{rightSpace}{month}";

        return random.NextInt(3) switch
        {
            0 => date,
            1 => $"{date}{leftSpace}{separator}{rightSpace}{year % 100:00}",
            _ => $"{date}{leftSpace}{separator}{rightSpace}{year}"
        };
    }

    private static string Reproducer(int seed, string optionText) =>
        $"seed={seed}; option=[{optionText.Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal)}]";

    private static BridgePoll BuildPoll(DateTimeOffset created, string optionText) => new(
        "poll-fuzz-ute-next-week",
        "Vote sân UTE tuần sau. Max 18 slots/sân. 17:45-22:00",
        "leader-1",
        [new BridgePollOption("o1", optionText, 2, [])],
        true,
        false,
        false,
        false,
        2,
        created.ToUnixTimeMilliseconds(),
        created.ToUnixTimeMilliseconds(),
        0);
}
