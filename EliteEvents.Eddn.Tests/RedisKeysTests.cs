using EliteEvents.Eddn.Storage;

namespace EliteEvents.Eddn.Tests;

/// <summary>
/// These tests exist because <see cref="RedisKeys"/> is a wire format, not an implementation
/// detail. Ingestion and the web tier are separate containers that only agree on the shape of
/// these strings; a change here that looks harmless splits the keyspace in two, and nothing
/// fails loudly — the writer keeps writing, the reader keeps finding nothing, and the site just
/// goes empty. So the literals are asserted verbatim rather than rebuilt from the same
/// interpolation the production code uses, which would only test that C# can concatenate.
/// </summary>
public class RedisKeysTests
{
    // ---- normalization ---------------------------------------------------------------------

    [Theory]
    [InlineData("Sol", "SOL")]
    [InlineData("SOL", "SOL")]
    [InlineData("Shinrarta Dezhra", "SHINRARTA DEZHRA")]
    [InlineData("Hyades Sector DB-X d1-112", "HYADES SECTOR DB-X D1-112")]
    public void NormalizeSystem_uppercases(string input, string expected)
        => Assert.Equal(expected, RedisKeys.NormalizeSystem(input));

    [Fact]
    public void NormalizeSystem_does_not_trim()
    {
        // Deliberate: system names arrive from EDDN, already clean, and are used to build the
        // key a search later has to match. Only NormalizeQuery trims, because only that input
        // came from a text box.
        Assert.Equal(" SOL ", RedisKeys.NormalizeSystem(" Sol "));
    }

    [Theory]
    [InlineData("x9k-4bt", "X9K-4BT")]
    [InlineData("X9K-4BT", "X9K-4BT")]
    public void NormalizeCarrier_uppercases(string input, string expected)
        => Assert.Equal(expected, RedisKeys.NormalizeCarrier(input));

    [Theory]
    [InlineData("  sol  ", "SOL")]
    [InlineData("\tshinrarta\n", "SHINRARTA")]
    [InlineData("Sol", "SOL")]
    public void NormalizeQuery_uppercases_and_trims(string input, string expected)
        => Assert.Equal(expected, RedisKeys.NormalizeQuery(input));

    [Fact]
    public void NormalizeQuery_uses_invariant_casing()
    {
        // Guards the Turkish-i problem: under tr-TR, "i".ToUpper() is "İ" (U+0130), so a host
        // with that culture would build keys no other pod could match. ToUpperInvariant is the
        // fix; this test is what stops someone "simplifying" it to ToUpper().
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            Assert.Equal("SIRIUS", RedisKeys.NormalizeQuery("Sirius"));
            Assert.Equal("SIRIUS", RedisKeys.NormalizeSystem("Sirius"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    // ---- key formats -----------------------------------------------------------------------

    [Fact]
    public void Station_key_format()
        => Assert.Equal("system:SOL:station:Abraham Lincoln", RedisKeys.Station("SOL", "Abraham Lincoln"));

    [Fact]
    public void Station_preserves_station_name_casing()
    {
        // Stations are stored verbatim because they are only ever read back out of the
        // system:{NAME}:stations index, never rebuilt from user input.
        Assert.Equal("system:SOL:station:Abraham Lincoln", RedisKeys.Station("SOL", "Abraham Lincoln"));
        Assert.NotEqual(RedisKeys.Station("SOL", "abraham lincoln"), RedisKeys.Station("SOL", "Abraham Lincoln"));
    }

    [Fact]
    public void SystemStations_key_format()
        => Assert.Equal("system:SOL:stations", RedisKeys.SystemStations("SOL"));

    [Fact]
    public void CarrierDaily_key_format()
        => Assert.Equal("carrier:X9K-4BT:daily:2026-07-30", RedisKeys.CarrierDaily("X9K-4BT", "2026-07-30"));

    [Fact]
    public void CarrierDays_key_format()
        => Assert.Equal("carrier:X9K-4BT:days", RedisKeys.CarrierDays("X9K-4BT"));

    [Fact]
    public void DateFormat_matches_the_date_component_of_CarrierDaily()
    {
        var date = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);
        var formatted = date.ToString(RedisKeys.DateFormat);

        Assert.Equal("2026-07-30", formatted);
        Assert.Equal("carrier:X9K-4BT:daily:2026-07-30", RedisKeys.CarrierDaily("X9K-4BT", formatted));
    }

    [Fact]
    public void DateFormat_is_culture_invariant_in_practice()
    {
        // "yyyy-MM-dd" has no culture-sensitive separators, but a locale with a non-Gregorian
        // default calendar (th-TH is Buddhist: 2569, not 2026) would still shift the year.
        // Every caller formats with it against a UTC DateTime; this pins that the pattern itself
        // is what makes the day roll at UTC midnight, and flags the calendar trap.
        var date = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal("2026-07-30", date.ToString(RedisKeys.DateFormat, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Global_key_names()
    {
        Assert.Equal("systems:visits", RedisKeys.SystemVisits);
        Assert.Equal("cache:system:count", RedisKeys.SystemCountCache);
        Assert.Equal("heartbeat:eddn", RedisKeys.EddnHeartbeat);
        Assert.Equal("eddn:events", RedisKeys.EventsChannel.ToString());
    }

    [Fact]
    public void Hash_field_names()
    {
        Assert.Equal("count", RedisKeys.StationCountField);
        Assert.Equal("type", RedisKeys.StationTypeField);
        Assert.Equal("last_seen", RedisKeys.StationLastSeenField);
    }

    // ---- TTLs ------------------------------------------------------------------------------

    [Fact]
    public void Expirations_are_the_documented_durations()
    {
        Assert.Equal(TimeSpan.FromDays(30), RedisKeys.DataExpiration);
        Assert.Equal(TimeSpan.FromSeconds(60), RedisKeys.SystemCountCacheDuration);
        Assert.Equal(TimeSpan.FromSeconds(5), RedisKeys.HeartbeatWriteInterval);
        Assert.Equal(TimeSpan.FromHours(1), RedisKeys.HeartbeatExpiration);
    }

    [Fact]
    public void Heartbeat_expiration_outlives_its_write_interval()
    {
        // The heartbeat TTL has to be comfortably longer than the write interval, or the key
        // expires between writes and readiness flaps on a perfectly healthy ingestion service.
        Assert.True(RedisKeys.HeartbeatExpiration > RedisKeys.HeartbeatWriteInterval * 10,
            "heartbeat TTL must leave wide margin over the write interval");
    }

    // ---- scan patterns ---------------------------------------------------------------------

    [Fact]
    public void AllSystemStationsPattern_matches_the_index_key_and_not_the_station_hashes()
    {
        Assert.Equal("system:*:stations", RedisKeys.AllSystemStationsPattern);

        // The system count is a SCAN over this pattern, so it must hit exactly one key per
        // system. Matching the per-station hashes too would inflate the count by the number of
        // stations rather than systems.
        Assert.True(GlobMatches(RedisKeys.AllSystemStationsPattern, RedisKeys.SystemStations("SOL")));
        Assert.False(GlobMatches(RedisKeys.AllSystemStationsPattern, RedisKeys.Station("SOL", "Abraham Lincoln")));
    }

    [Fact]
    public void SystemSearchPattern_is_a_substring_match()
    {
        Assert.Equal("system:*SOL*", RedisKeys.SystemSearchPattern("SOL"));
        Assert.True(GlobMatches(RedisKeys.SystemSearchPattern("SOL"), RedisKeys.SystemStations("SOL")));
        Assert.True(GlobMatches(RedisKeys.SystemSearchPattern("OL"), RedisKeys.SystemStations("SOL")));
    }

    [Fact]
    public void SystemSearchPattern_also_matches_the_station_segment()
    {
        // Long-standing quirk, preserved deliberately: the pattern has no anchor between the
        // system and station segments, so searching a station name surfaces its system. Pinned
        // here so it is a decision rather than a surprise — if this ever becomes unwanted, this
        // is the test that should fail and be deleted on purpose.
        var key = RedisKeys.Station("SOL", "ABRAHAM LINCOLN");
        Assert.True(GlobMatches(RedisKeys.SystemSearchPattern("LINCOLN"), key));
    }

    [Fact]
    public void CarrierSearchPattern_is_a_substring_match()
    {
        Assert.Equal("carrier:*X9K*", RedisKeys.CarrierSearchPattern("X9K"));
        Assert.True(GlobMatches(RedisKeys.CarrierSearchPattern("X9K"), RedisKeys.CarrierDays("X9K-4BT")));
    }

    [Fact]
    public void Search_patterns_do_not_cross_namespaces()
    {
        // A carrier search must never surface a system key, whatever the query.
        Assert.False(GlobMatches(RedisKeys.CarrierSearchPattern("SOL"), RedisKeys.SystemStations("SOL")));
        Assert.False(GlobMatches(RedisKeys.SystemSearchPattern("X9K"), RedisKeys.CarrierDays("X9K-4BT")));
    }

    [Fact]
    public void DataKeyPatterns_cover_real_data_and_exclude_the_cache()
    {
        // The health check counts data keys. Including cache:* would let an empty Redis look
        // populated the moment anything warmed the system-count cache.
        Assert.Equal(["system:*", "carrier:*"], RedisKeys.DataKeyPatterns);

        Assert.Contains(RedisKeys.DataKeyPatterns, p => GlobMatches(p, RedisKeys.SystemStations("SOL")));
        Assert.Contains(RedisKeys.DataKeyPatterns, p => GlobMatches(p, RedisKeys.CarrierDays("X9K-4BT")));
        Assert.DoesNotContain(RedisKeys.DataKeyPatterns, p => GlobMatches(p, RedisKeys.SystemCountCache));
        Assert.DoesNotContain(RedisKeys.DataKeyPatterns, p => GlobMatches(p, RedisKeys.EddnHeartbeat));
    }

    // ---- ExtractName -----------------------------------------------------------------------

    [Fact]
    public void ExtractName_round_trips_every_key_a_scan_can_return()
    {
        // This is the invariant that actually matters: DockingReader SCANs for keys and pulls
        // the name back out of each one. Build → extract has to be lossless for names
        // containing the characters Elite actually uses.
        foreach (var name in new[] { "SOL", "SHINRARTA DEZHRA", "HYADES SECTOR DB-X D1-112", "COL 285 SECTOR AA-A A1" })
        {
            Assert.Equal(name, RedisKeys.ExtractName(RedisKeys.SystemStations(name)));
            Assert.Equal(name, RedisKeys.ExtractName(RedisKeys.Station(name, "Some Station")));
        }

        Assert.Equal("X9K-4BT", RedisKeys.ExtractName(RedisKeys.CarrierDays("X9K-4BT")));
        Assert.Equal("X9K-4BT", RedisKeys.ExtractName(RedisKeys.CarrierDaily("X9K-4BT", "2026-07-30")));
    }

    [Fact]
    public void ExtractName_returns_null_when_there_is_no_name_segment()
    {
        Assert.Null(RedisKeys.ExtractName("system"));
        Assert.Null(RedisKeys.ExtractName(""));
    }

    [Fact]
    public void ExtractName_takes_segment_one_of_anything()
    {
        // Known quirk: it is a blind split, so the global keys "parse" to nonsense. Unreachable
        // in production — those keys are never returned by the scans that feed ExtractName —
        // but pinned so the behaviour is documented rather than assumed.
        Assert.Equal("visits", RedisKeys.ExtractName(RedisKeys.SystemVisits));
        Assert.Equal("system", RedisKeys.ExtractName(RedisKeys.SystemCountCache));
        Assert.Equal("eddn", RedisKeys.ExtractName(RedisKeys.EddnHeartbeat));
    }

    /// <summary>
    /// Redis glob semantics, limited to the <c>*</c> the patterns above actually use — enough to
    /// assert what a SCAN would and would not return without needing a Redis to ask.
    /// </summary>
    private static bool GlobMatches(string pattern, string key)
    {
        var regex = "^" + string.Join(".*", pattern.Split('*').Select(System.Text.RegularExpressions.Regex.Escape)) + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(key, regex);
    }
}
