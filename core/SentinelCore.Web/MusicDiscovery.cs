using System.Globalization;
using System.Text.Json;

namespace SentinelCore.Web;

// Smart playlist rules (MUSIC_AND_PODCASTS.md section 3): which field takes which comparison,
// checking a rule set before it is saved, and running one over the catalog.
public static class MusicSmartPlaylists
{
    public const int MaxLimit = 5000;

    private static readonly string[] TextFields = ["title", "artist", "album", "genre", "composer"];
    private static readonly string[] NumberFields = ["year", "rating", "playCount", "durationSeconds", "bpm"];
    private static readonly string[] DayFields = ["added", "lastPlayed"];
    private static readonly string[] Sorts = ["random", "title", "artist", "album", "year", "added", "plays", "lastPlayed", "rating"];

    public static string[] OpsFor(string field) => field switch
    {
        _ when TextFields.Contains(field) => ["is", "isNot", "contains", "notContains"],
        _ when NumberFields.Contains(field) => ["is", "gte", "lte"],
        "loved" => ["is"],
        _ when DayFields.Contains(field) => ["inLast", "notInLast"],
        "codec" or "library" => ["is", "isNot"],
        _ => []
    };

    // Plain-English problems with a rule set, or none.
    public static List<string> Validate(MusicSmartRules rules)
    {
        var errors = new List<string>();
        if (rules.Match is not ("all" or "any")) errors.Add("Match must be all or any.");
        if (!Sorts.Contains(rules.Sort)) errors.Add($"Sort must be one of {string.Join(", ", Sorts)}.");
        if (rules.Order is not ("asc" or "desc")) errors.Add("Order must be asc or desc.");
        if (rules.Limit < 0) errors.Add("Limit can't be negative; use 0 for no limit.");
        for (var i = 0; i < rules.Rules.Count; i++)
        {
            var rule = rules.Rules[i];
            var ops = OpsFor(rule.Field);
            if (ops.Length == 0) { errors.Add($"Rule {i + 1}: \"{rule.Field}\" is not something a smart playlist can match on."); continue; }
            if (!ops.Contains(rule.Op)) { errors.Add($"Rule {i + 1}: {rule.Field} can be matched with {string.Join(", ", ops)}, not \"{rule.Op}\"."); continue; }
            var ok = rule.Field switch
            {
                "loved" => Bool(rule.Value) is not null,
                "library" => Guid.TryParse(Text(rule.Value), out _),
                _ when NumberFields.Contains(rule.Field) || DayFields.Contains(rule.Field) => Number(rule.Value) is not null,
                _ => Text(rule.Value) is { Length: > 0 }
            };
            if (!ok) errors.Add($"Rule {i + 1}: {rule.Field} needs a {(rule.Field == "loved" ? "true or false" : NumberFields.Contains(rule.Field) || DayFields.Contains(rule.Field) ? "number" : "value")}.");
        }
        return errors;
    }

    public static List<MusicTrack> Evaluate(MusicView view, MusicSmartRules rules, DateTimeOffset now, Random random)
    {
        var matching = view.Catalog.Tracks.Where(t => rules.Rules.Count == 0
            || (rules.Match == "any" ? rules.Rules.Any(r => Matches(view, t, r, now)) : rules.Rules.All(r => Matches(view, t, r, now)))).ToList();

        var descending = rules.Order == "desc";
        IEnumerable<MusicTrack> ordered = rules.Sort switch
        {
            "title" => Order(matching, t => MusicText.SortKey(t.Title), descending),
            "artist" => Order(matching, t => MusicText.SortKey(t.PrimaryArtist), descending),
            "album" => Order(matching, t => MusicText.SortKey(t.Album), descending),
            "year" => Order(matching, t => t.Year ?? 0, descending),
            "added" => Order(matching, t => t.AddedAt, descending),
            "plays" => Order(matching, t => view.User.PlayCount(t.Id), descending),
            "lastPlayed" => Order(matching, t => view.User.LastPlayed(t.Id) ?? DateTimeOffset.MinValue, descending),
            "rating" => Order(matching, t => view.User.RatingOf(MusicLoveKinds.Track, t.Id), descending),
            _ => matching.OrderBy(_ => random.Next())
        };
        var limit = rules.Limit <= 0 ? MaxLimit : Math.Min(rules.Limit, MaxLimit);
        return ordered.Take(limit).ToList();
    }

    private static IOrderedEnumerable<MusicTrack> Order<TKey>(IEnumerable<MusicTrack> tracks, Func<MusicTrack, TKey> key, bool descending) =>
        descending
            ? tracks.OrderByDescending(key).ThenBy(t => MusicText.SortKey(t.Title), StringComparer.Ordinal)
            : tracks.OrderBy(key).ThenBy(t => MusicText.SortKey(t.Title), StringComparer.Ordinal);

    public static bool Matches(MusicView view, MusicTrack track, MusicSmartRule rule, DateTimeOffset now)
    {
        switch (rule.Field)
        {
            case "title": return TextMatch([track.Title], rule);
            case "artist": return TextMatch([track.ArtistText, .. track.Artists], rule);
            case "album": return TextMatch([track.Album], rule);
            case "genre": return TextMatch(track.Genres, rule);
            case "composer": return TextMatch(track.Composer is null ? [] : [track.Composer], rule);
            case "year": return NumberMatch(track.Year, rule);
            case "rating": return NumberMatch(view.User.RatingOf(MusicLoveKinds.Track, track.Id), rule);
            case "playCount": return NumberMatch(view.User.PlayCount(track.Id), rule);
            case "durationSeconds": return NumberMatch(track.DurationSeconds, rule);
            case "bpm": return NumberMatch(track.Bpm, rule);
            case "loved": return view.User.IsLoved(MusicLoveKinds.Track, track.Id) == (Bool(rule.Value) ?? true);
            case "added": return DaysMatch(track.AddedAt, rule, now, neverMatchesNotInLast: false);
            case "lastPlayed": return DaysMatch(view.User.LastPlayed(track.Id), rule, now, neverMatchesNotInLast: true);
            case "codec":
            {
                var same = string.Equals(track.Codec, Text(rule.Value), StringComparison.OrdinalIgnoreCase);
                return rule.Op == "isNot" ? !same : same;
            }
            case "library":
            {
                var same = Guid.TryParse(Text(rule.Value), out var library) && track.LibraryId == library;
                return rule.Op == "isNot" ? !same : same;
            }
            default: return false;
        }
    }

    // Case and accents do not count: "beyonce" is "Beyoncé".
    private static bool TextMatch(IReadOnlyCollection<string> values, MusicSmartRule rule)
    {
        var wanted = MusicText.Key(Text(rule.Value));
        var keys = values.Select(MusicText.Key).ToList();
        return rule.Op switch
        {
            "is" => keys.Contains(wanted),
            "isNot" => !keys.Contains(wanted),
            "contains" => keys.Any(k => k.Contains(wanted, StringComparison.Ordinal)),
            "notContains" => !keys.Any(k => k.Contains(wanted, StringComparison.Ordinal)),
            _ => false
        };
    }

    private static bool NumberMatch(double? value, MusicSmartRule rule)
    {
        if (value is not { } v || Number(rule.Value) is not { } wanted) return false;
        return rule.Op switch
        {
            "is" => Math.Abs(v - wanted) < 0.5,
            "gte" => v >= wanted,
            "lte" => v <= wanted,
            _ => false
        };
    }

    // "lastPlayed notInLast 90" also matches what was never played at all.
    private static bool DaysMatch(DateTimeOffset? when, MusicSmartRule rule, DateTimeOffset now, bool neverMatchesNotInLast)
    {
        if (Number(rule.Value) is not { } days) return false;
        var since = now - TimeSpan.FromDays(days);
        return rule.Op switch
        {
            "inLast" => when is { } w && w >= since,
            "notInLast" => when is { } w ? w < since : neverMatchesNotInLast,
            _ => false
        };
    }

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null
    };

    private static double? Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null
    };

    private static bool? Bool(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
        _ => null
    };
}

// Radio, mixes, shuffle-all and the Home rows: everything that picks music for someone rather
// than listing what they asked for.
public static class MusicDiscovery
{
    public const int RowSize = 20;
    public const int ArtistSpacing = 3;
    private static readonly TimeSpan RecentlyHeard = TimeSpan.FromHours(1);

    public sealed record RadioSeed(
        IReadOnlySet<Guid> Artists,
        IReadOnlySet<string> Genres,
        IReadOnlySet<int> Decades,
        // Tracks that must come first (a track radio's seed), in order.
        IReadOnlyList<MusicTrack> Leading,
        // When set, only these tracks are candidates (a genre or decade station).
        Func<MusicTrack, bool>? Pool = null,
        // A mix from a small pool (Loved, say) would come out empty straight after its tracks
        // were played; mixes may fall back on them, at the end. A radio never does.
        bool RecentAsLastResort = false);

    // A household-library radio: every track scored by how much it shares with the seed (artist,
    // genre, decade), how this person feels about it (loves, ratings, plays against skips) and a
    // pinch of randomness, then laid out so no artist comes round again within three tracks.
    // Nothing heard in the last hour is played, nor anything in `exclude` (what the client
    // already has, when it asks for more to keep an endless radio going).
    public static List<MusicTrack> Radio(MusicView view, RadioSeed seed, IReadOnlySet<Guid> exclude, int limit, DateTimeOffset now, Random random)
    {
        var leading = seed.Leading.Where(t => !exclude.Contains(t.Id)).ToList();
        var skip = new HashSet<Guid>(exclude);
        foreach (var t in seed.Leading) skip.Add(t.Id);

        var scored = new List<(MusicTrack Track, double Score)>();
        var others = new List<(MusicTrack Track, double Score)>();
        var recent = new List<(MusicTrack Track, double Score)>();
        foreach (var track in view.Catalog.Tracks)
        {
            if (skip.Contains(track.Id)) continue;
            if (seed.Pool is { } pool && !pool(track)) continue;
            var relevance = Relevance(track, seed);
            var score = relevance + Taste(view, track) + random.NextDouble() * 1.5;
            if (view.User.LastHeard(track.Id) is { } heard && heard > now - RecentlyHeard) recent.Add((track, score));
            else (relevance > 0 || seed.Pool is not null ? scored : others).Add((track, score));
        }
        var ordered = scored.OrderByDescending(s => s.Score).Select(s => s.Track).ToList();
        // A small library still gets a full station: unrelated tracks follow the related ones.
        if (ordered.Count < limit) ordered.AddRange(others.OrderByDescending(s => s.Score).Select(s => s.Track));
        if (ordered.Count < limit && seed.RecentAsLastResort) ordered.AddRange(recent.OrderByDescending(s => s.Score).Select(s => s.Track));
        return Spaced(ordered, limit, leading);
    }

    private static double Relevance(MusicTrack track, RadioSeed seed)
    {
        var score = 0.0;
        if (seed.Artists.Contains(track.ArtistId)) score += 3;
        else if (track.Artists.Any(a => seed.Artists.Contains(MusicIds.Artist(a)))) score += 2;
        if (seed.Genres.Count > 0)
            score += Math.Min(3, track.Genres.Count(g => seed.Genres.Contains(MusicText.Key(g)))) * 1.5;
        if (MusicCatalog.Decade(track.Year) is { } decade && seed.Decades.Count > 0)
        {
            if (seed.Decades.Contains(decade)) score += 1;
            else if (seed.Decades.Contains(decade - 10) || seed.Decades.Contains(decade + 10)) score += 0.4;
        }
        return score;
    }

    // How this person feels about a track, from what they have done with it.
    private static double Taste(MusicView view, MusicTrack track)
    {
        var user = view.User;
        var score = 0.0;
        if (user.IsLoved(MusicLoveKinds.Track, track.Id)) score += 1.5;
        if (user.IsLoved(MusicLoveKinds.Album, track.AlbumId)) score += 0.75;
        if (user.IsLoved(MusicLoveKinds.Artist, track.ArtistId)) score += 0.75;
        var rating = user.RatingOf(MusicLoveKinds.Track, track.Id);
        if (rating == 0) rating = user.RatingOf(MusicLoveKinds.Album, track.AlbumId);
        if (rating > 0) score += (rating - 3) * 0.6;
        var plays = user.PlayCount(track.Id);
        var skips = user.SkipCount(track.Id);
        score += Math.Log(1 + plays) * 0.4 - Math.Min(skips, 5) * 0.5;
        if (skips >= 3 && skips > plays * 2) score -= 2;
        return score;
    }

    // Takes tracks in order, holding back any whose artist was one of the last three, until
    // `limit` are placed or nothing left can go next.
    public static List<MusicTrack> Spaced(IReadOnlyList<MusicTrack> ordered, int limit, IReadOnlyList<MusicTrack>? leading = null)
    {
        var result = new List<MusicTrack>(leading ?? []);
        var remaining = new LinkedList<MusicTrack>(ordered);
        while (result.Count < limit && remaining.Count > 0)
        {
            var recent = result.Skip(Math.Max(0, result.Count - ArtistSpacing)).Select(t => t.ArtistId).ToHashSet();
            var node = remaining.First;
            while (node is not null && recent.Contains(node.Value.ArtistId)) node = node.Next;
            if (node is null) break;
            result.Add(node.Value);
            remaining.Remove(node);
        }
        return result.Count > limit ? result.GetRange(0, limit) : result;
    }

    // "Shuffle all": every track, in a random order weighted away from what was heard lately
    // and what keeps being skipped, and towards what is loved and rated well.
    public static List<MusicTrack> Shuffle(MusicView view, int limit, DateTimeOffset now, Random random)
    {
        var keyed = new List<(MusicTrack Track, double Key)>(view.Catalog.Tracks.Count);
        foreach (var track in view.Catalog.Tracks)
        {
            var weight = 1.0;
            if (view.User.LastHeard(track.Id) is { } heard)
            {
                if (heard > now.AddDays(-1)) weight *= 0.1;
                else if (heard > now.AddDays(-7)) weight *= 0.4;
            }
            weight /= 1 + Math.Min(view.User.SkipCount(track.Id), 6) * 0.7;
            if (view.User.IsLoved(MusicLoveKinds.Track, track.Id)) weight *= 1.5;
            var rating = view.User.RatingOf(MusicLoveKinds.Track, track.Id);
            if (rating >= 4) weight *= 1.3;
            else if (rating is > 0 and <= 2) weight *= 0.5;
            // Weighted sampling without replacement (Efraimidis-Spirakis): the smallest keys win.
            var u = random.NextDouble();
            keyed.Add((track, -Math.Log(u <= 0 ? double.Epsilon : u) / weight));
        }
        return keyed.OrderBy(k => k.Key).Take(limit).Select(k => k.Track).ToList();
    }

    // MARK: - Mixes

    public sealed record Mix(MusicMixDto Dto, Func<int, IReadOnlySet<Guid>, Random, List<MusicTrack>> Tracks);

    // Mixes made for this person: up to four Daily Mixes from clusters of the genres they play
    // most, their favourite decades, everything they love, and albums they have never played.
    // Each comes out the same all day (a seed from the date) and different tomorrow.
    public static List<Mix> Mixes(MusicView view, DateTimeOffset now)
    {
        var mixes = new List<Mix>();
        var clusters = GenreClusters(view, now);
        for (var i = 0; i < clusters.Count; i++)
        {
            var cluster = clusters[i];
            var keys = cluster.Select(MusicText.Key).ToHashSet();
            var id = $"daily-{i + 1}";
            var topArtists = view.User.Plays.Where(p => !p.Skipped && view.Catalog.TracksById.TryGetValue(p.TrackId, out var t)
                    && t.Genres.Any(g => keys.Contains(MusicText.Key(g))))
                .GroupBy(p => view.Catalog.TracksById[p.TrackId].ArtistId).OrderByDescending(g => g.Count()).Take(5).Select(g => g.Key).ToHashSet();
            var seed = new RadioSeed(topArtists, keys, new HashSet<int>(), [], t => t.Genres.Any(g => keys.Contains(MusicText.Key(g))));
            var description = cluster.Count switch
            {
                1 => cluster[0],
                2 => $"{cluster[0]} and {cluster[1]}",
                _ => $"{cluster[0]}, {cluster[1]} and more"
            };
            mixes.Add(Build(view, id, $"Daily Mix {i + 1}", description, "genre", MusicText.Key(cluster[0]), seed, now));
        }

        foreach (var decade in FavouriteDecades(view).Take(3))
        {
            var label = MusicCatalog.DecadeLabel(decade);
            var seed = new RadioSeed(new HashSet<Guid>(), new HashSet<string>(), new HashSet<int> { decade }, [], t => MusicCatalog.Decade(t.Year) == decade);
            mixes.Add(Build(view, $"decade-{decade}", $"{label} Mix", $"Your {label}", "decade", decade.ToString(CultureInfo.InvariantCulture), seed, now));
        }

        var user = view.User;
        bool IsLovedTrack(MusicTrack t) => user.IsLoved(MusicLoveKinds.Track, t.Id) || user.IsLoved(MusicLoveKinds.Album, t.AlbumId)
            || t.Artists.Any(a => user.IsLoved(MusicLoveKinds.Artist, MusicIds.Artist(a)));
        if (view.Catalog.Tracks.Any(IsLovedTrack))
            mixes.Add(Build(view, "loved", "Loved Mix", "Tracks, albums and artists you love", "loved", "loved",
                new RadioSeed(new HashSet<Guid>(), new HashSet<string>(), new HashSet<int>(), [], IsLovedTrack), now));

        var played = user.Plays.Where(p => !p.Skipped).Select(p => view.Catalog.TracksById.GetValueOrDefault(p.TrackId)?.AlbumId)
            .OfType<Guid>().ToHashSet();
        var favouriteGenres = clusters.SelectMany(c => c).Select(MusicText.Key).ToHashSet();
        if (view.Catalog.Tracks.Any(t => !played.Contains(t.AlbumId)))
            mixes.Add(Build(view, "discover", "Discover Mix", "Albums you haven't played yet", "discover", "discover",
                new RadioSeed(new HashSet<Guid>(), favouriteGenres, new HashSet<int>(), [], t => !played.Contains(t.AlbumId)), now));
        return mixes;
    }

    private static Mix Build(MusicView view, string id, string name, string description, string seedType, string seedId, RadioSeed seed, DateTimeOffset now)
    {
        var mixSeed = seed with { RecentAsLastResort = true };
        List<MusicTrack> Tracks(int limit, IReadOnlySet<Guid> exclude, Random random) => Radio(view, mixSeed, exclude, limit, now, random);
        var preview = Tracks(12, new HashSet<Guid>(), DailyRandom(view.User.UserKey, id, now));
        return new Mix(new MusicMixDto(id, name, description, view.ArtUrls(preview), seedType, seedId), Tracks);
    }

    // The same "random" all day for the same person and mix.
    public static Random DailyRandom(string userKey, string salt, DateTimeOffset now)
    {
        var day = now.ToLocalTime().Date;
        var text = $"{userKey}|{salt}|{day:yyyy-MM-dd}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return new Random(BitConverter.ToInt32(hash, 0));
    }

    // Up to four groups of genres, the person's most played first. A genre joins an earlier
    // group when the two turn up on the same albums (Rock with Classic Rock), so each Daily Mix
    // sounds of a piece. Without any plays yet, the library's own biggest genres stand in.
    public static List<List<string>> GenreClusters(MusicView view, DateTimeOffset now)
    {
        var weights = new Dictionary<string, (string Name, double Weight)>(StringComparer.Ordinal);
        void Weigh(string genre, double weight)
        {
            var key = MusicText.Key(genre);
            weights[key] = weights.TryGetValue(key, out var w) ? (w.Name, w.Weight + weight) : (genre, weight);
        }
        var since = now.AddDays(-180);
        foreach (var play in view.User.Plays.Where(p => !p.Skipped && p.PlayedAt >= since))
            if (view.Catalog.TracksById.TryGetValue(play.TrackId, out var track))
                foreach (var genre in track.Genres) Weigh(genre, 1);
        foreach (var (kind, id) in view.User.Loved)
            if (kind == MusicLoveKinds.Track && view.Catalog.TracksById.TryGetValue(id, out var track))
                foreach (var genre in track.Genres) Weigh(genre, 2);
        if (weights.Count == 0)
            foreach (var track in view.Catalog.Tracks)
                foreach (var genre in track.Genres) Weigh(genre, 0.01);

        var albumsByGenre = view.Catalog.Albums.SelectMany(a => a.Genres.Select(g => (Key: MusicText.Key(g), a.Id)))
            .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToHashSet());
        bool Related(string a, string b)
        {
            if (!albumsByGenre.TryGetValue(a, out var left) || !albumsByGenre.TryGetValue(b, out var right)) return false;
            var shared = left.Count(right.Contains);
            return shared > 0 && shared >= Math.Min(left.Count, right.Count) * 0.3;
        }

        var clusters = new List<List<string>>();
        var seeds = new List<string>();
        foreach (var (key, (name, _)) in weights.OrderByDescending(w => w.Value.Weight).ThenBy(w => w.Key, StringComparer.Ordinal).Take(16))
        {
            var home = seeds.FindIndex(seed => Related(seed, key));
            if (home >= 0) clusters[home].Add(name);
            else if (clusters.Count < 4) { clusters.Add([name]); seeds.Add(key); }
        }
        return clusters;
    }

    // The decades this person plays most, else the library's biggest.
    public static List<int> FavouriteDecades(MusicView view)
    {
        var counts = view.User.Plays.Where(p => !p.Skipped)
            .Select(p => MusicCatalog.Decade(view.Catalog.TracksById.GetValueOrDefault(p.TrackId)?.Year))
            .OfType<int>().GroupBy(d => d).ToDictionary(g => g.Key, g => g.Count());
        if (counts.Count == 0)
            counts = view.Catalog.Tracks.Select(t => MusicCatalog.Decade(t.Year)).OfType<int>().GroupBy(d => d).ToDictionary(g => g.Key, g => g.Count());
        return counts.OrderByDescending(c => c.Value).ThenByDescending(c => c.Key).Select(c => c.Key).ToList();
    }

    // MARK: - Home

    public static object Home(MusicView view, IReadOnlyList<MusicPlaylist> visiblePlaylists, IReadOnlySet<Guid> pinned,
        Func<MusicPlaylist, IReadOnlyList<MusicTrack>> playlistTracks, DateTimeOffset now)
    {
        var catalog = view.Catalog;
        var user = view.User;
        var plays = user.Plays.Where(p => !p.Skipped && catalog.TracksById.ContainsKey(p.TrackId)).ToList();
        MusicPlaylistDto PlaylistDto(MusicPlaylist p) => view.Playlist(p, playlistTracks(p), pinned.Contains(p.Id));

        var playedAlbums = plays.Select(p => catalog.TracksById[p.TrackId].AlbumId).ToHashSet();
        var monthAgo = now.AddDays(-30);
        var heavy = plays.Where(p => p.PlayedAt >= monthAgo)
            .GroupBy(p => catalog.TracksById[p.TrackId].AlbumId)
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(p => p.PlayedAt))
            .Take(RowSize).Select(g => view.Album(catalog.AlbumsById[g.Key])).ToList();

        var quarterAgo = now.AddDays(-90);
        var day = now.ToLocalTime().DayOfYear;
        var forgotten = catalog.Tracks
            .Where(t => (user.PlayCount(t.Id) >= 5 || user.IsLoved(MusicLoveKinds.Track, t.Id))
                && !(user.LastPlayed(t.Id) is { } last && last >= quarterAgo))
            .OrderByDescending(t => user.PlayCount(t.Id)).ThenBy(t => Shuffle(t.Id, day))
            .Take(RowSize).Select(t => view.Track(t)).ToList();

        var discover = catalog.Albums.Where(a => !playedAlbums.Contains(a.Id))
            .OrderBy(a => Shuffle(a.Id, day)).Take(RowSize).Select(view.Album).ToList();

        var loved = user.LovedAt.Where(l => l.Key.Kind == MusicLoveKinds.Track && catalog.TracksById.ContainsKey(l.Key.Id))
            .OrderByDescending(l => l.Value).Take(RowSize).Select(l => view.Track(catalog.TracksById[l.Key.Id])).ToList();

        var decades = catalog.Tracks.Select(t => MusicCatalog.Decade(t.Year)).OfType<int>().GroupBy(d => d).OrderBy(g => g.Key)
            .Select(g => new { decade = g.Key, label = MusicCatalog.DecadeLabel(g.Key), trackCount = g.Count() }).ToList();

        return new
        {
            pinned = visiblePlaylists.Where(p => pinned.Contains(p.Id)).Select(PlaylistDto).ToList(),
            recentlyPlayed = RecentlyPlayed(view, visiblePlaylists, PlaylistDto).Take(RowSize).ToList(),
            recentlyAdded = catalog.Albums.OrderByDescending(a => a.AddedAt).ThenBy(a => MusicText.SortKey(a.Title), StringComparer.Ordinal)
                .Take(RowSize).Select(view.Album).ToList(),
            heavyRotation = heavy,
            mixes = Mixes(view, now).Select(m => m.Dto).ToList(),
            forgottenFavorites = forgotten,
            onThisDay = OnThisDay(view, now),
            discover,
            lovedTracks = loved,
            decades,
            trackCount = catalog.Tracks.Count,
            albumCount = catalog.Albums.Count,
            artistCount = catalog.Artists.Count
        };
    }

    // What the person played lately, as the things they would tap to play again. Plays carry no
    // record of what started them, so they are read back in sittings (plays less than half an
    // hour apart): a sitting from one album is that album, one whose tracks all sit in one
    // playlist is that playlist, one from one artist is that artist, and anything else is the
    // album of each track.
    private static IEnumerable<object> RecentlyPlayed(MusicView view, IReadOnlyList<MusicPlaylist> playlists, Func<MusicPlaylist, MusicPlaylistDto> playlistDto)
    {
        var catalog = view.Catalog;
        var plays = view.User.Plays.Where(p => !p.Skipped && catalog.TracksById.ContainsKey(p.TrackId))
            .OrderByDescending(p => p.PlayedAt).Take(500).ToList();
        var seen = new HashSet<(string, Guid)>();
        var sitting = new List<MusicTrack>();
        var items = new List<object>();
        void Emit((string Kind, Guid Id) key, object item)
        {
            if (seen.Add(key)) items.Add(item);
        }
        void Close()
        {
            if (sitting.Count == 0) return;
            var albums = sitting.Select(t => t.AlbumId).Distinct().ToList();
            var playlist = sitting.Count >= 2 && albums.Count > 1
                ? playlists.FirstOrDefault(p => !p.Smart && sitting.All(t => p.Entries.Any(e => e.TrackId == t.Id)))
                : null;
            if (playlist is not null) Emit(("playlist", playlist.Id), new { kind = "playlist", playlist = playlistDto(playlist) });
            else if (albums.Count > 1 && sitting.Select(t => t.ArtistId).Distinct().Count() == 1
                     && catalog.ArtistsById.TryGetValue(sitting[0].ArtistId, out var artist))
                Emit(("artist", artist.Id), new { kind = "artist", artist = view.Artist(artist) });
            else
                foreach (var album in albums)
                    Emit(("album", album), new { kind = "album", album = view.Album(catalog.AlbumsById[album]) });
            sitting.Clear();
        }
        DateTimeOffset? previous = null;
        foreach (var play in plays)
        {
            if (previous is { } p && p - play.PlayedAt > TimeSpan.FromMinutes(30)) Close();
            sitting.Add(catalog.TracksById[play.TrackId]);
            previous = play.PlayedAt;
            if (items.Count >= RowSize) break;
        }
        Close();
        return items;
    }

    // What this person played on today's date in an earlier year - the most recent such year.
    private static object? OnThisDay(MusicView view, DateTimeOffset now)
    {
        var today = now.ToLocalTime();
        var earlier = view.User.Plays
            .Where(p => !p.Skipped && view.Catalog.TracksById.ContainsKey(p.TrackId))
            .Select(p => (Play: p, Local: p.PlayedAt.ToLocalTime()))
            .Where(x => x.Local.Month == today.Month && x.Local.Day == today.Day && x.Local.Year < today.Year)
            .ToList();
        if (earlier.Count == 0) return null;
        var year = earlier.Max(x => x.Local.Year);
        var tracks = earlier.Where(x => x.Local.Year == year)
            .GroupBy(x => x.Play.TrackId).OrderByDescending(g => g.Count()).ThenBy(g => g.Min(x => x.Play.PlayedAt))
            .Take(RowSize).Select(g => view.Track(view.Catalog.TracksById[g.Key])).ToList();
        var date = new DateTime(year, today.Month, today.Day);
        return new { label = date.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture), tracks };
    }

    // A stable shuffle for the day: the same order all day, a different one tomorrow.
    public static uint Shuffle(Guid id, int day)
    {
        var x = unchecked((uint)id.GetHashCode() ^ (uint)day * 2654435761u);
        x ^= x >> 16;
        x = unchecked(x * 0x7feb352du);
        x ^= x >> 15;
        x = unchecked(x * 0x846ca68bu);
        return x ^ (x >> 16);
    }

    // MARK: - Stats

    // The "music" object of GET /api/audio/stats for [from, to): this person only.
    public static object Stats(MusicView view, DateTimeOffset from, DateTimeOffset to)
    {
        var catalog = view.Catalog;
        var all = view.User.Plays;
        var inRange = all.Where(p => p.PlayedAt >= from && p.PlayedAt < to).ToList();
        var plays = inRange.Where(p => !p.Skipped).ToList();
        var known = plays.Where(p => catalog.TracksById.ContainsKey(p.TrackId)).Select(p => (Play: p, Track: catalog.TracksById[p.TrackId])).ToList();

        // An artist is new when this person's first play of them ever falls in the range.
        var firstHeard = new Dictionary<Guid, DateTimeOffset>();
        foreach (var play in all.Where(p => !p.Skipped))
            if (catalog.TracksById.TryGetValue(play.TrackId, out var track) && !firstHeard.ContainsKey(track.ArtistId))
                firstHeard[track.ArtistId] = play.PlayedAt;

        var byHour = new double[24];
        var byWeekday = new double[7];
        foreach (var play in plays)
        {
            var local = play.PlayedAt.ToLocalTime();
            byHour[local.Hour] += play.SecondsListened / 60;
            byWeekday[((int)local.DayOfWeek + 6) % 7] += play.SecondsListened / 60;
        }

        var days = plays.Select(p => p.PlayedAt.ToLocalTime().Date).Distinct().OrderBy(d => d).ToList();
        var longest = 0;
        var run = 0;
        DateTime? previousDay = null;
        foreach (var d in days)
        {
            run = previousDay is { } prev && (d - prev).TotalDays == 1 ? run + 1 : 1;
            longest = Math.Max(longest, run);
            previousDay = d;
        }

        var first = known.OrderBy(k => k.Play.PlayedAt).FirstOrDefault();
        return new
        {
            minutes = (int)Math.Round(plays.Sum(p => p.SecondsListened) / 60),
            plays = plays.Count,
            skips = inRange.Count(p => p.Skipped),
            distinctTracks = plays.Select(p => p.TrackId).Distinct().Count(),
            distinctArtists = known.Select(k => k.Track.ArtistId).Distinct().Count(),
            newArtists = firstHeard.Count(f => f.Value >= from && f.Value < to),
            topArtists = known.GroupBy(k => k.Track.ArtistId)
                .Where(g => catalog.ArtistsById.ContainsKey(g.Key))
                .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Sum(k => k.Play.SecondsListened)).Take(10)
                .Select(g => new { artist = view.Artist(catalog.ArtistsById[g.Key]), plays = g.Count(), minutes = (int)Math.Round(g.Sum(k => k.Play.SecondsListened) / 60) })
                .ToList(),
            topAlbums = known.GroupBy(k => k.Track.AlbumId)
                .Where(g => catalog.AlbumsById.ContainsKey(g.Key))
                .OrderByDescending(g => g.Count()).Take(10)
                .Select(g => new { album = view.Album(catalog.AlbumsById[g.Key]), plays = g.Count() }).ToList(),
            topTracks = known.GroupBy(k => k.Track.Id)
                .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(k => k.Play.PlayedAt)).Take(10)
                .Select(g => new { track = view.Track(g.First().Track), plays = g.Count() }).ToList(),
            topGenres = known.SelectMany(k => k.Track.Genres.Select(g => (Genre: g, k.Play.SecondsListened)))
                .GroupBy(x => MusicText.Key(x.Genre))
                .OrderByDescending(g => g.Sum(x => x.SecondsListened)).Take(10)
                .Select(g => new { genre = g.First().Genre, minutes = (int)Math.Round(g.Sum(x => x.SecondsListened) / 60) }).ToList(),
            byHour = byHour.Select(m => (int)Math.Round(m)).ToArray(),
            byWeekday = byWeekday.Select(m => (int)Math.Round(m)).ToArray(),
            longestStreakDays = longest,
            firstPlay = first.Track is null ? null : new { track = view.Track(first.Track), playedAt = first.Play.PlayedAt }
        };
    }
}
