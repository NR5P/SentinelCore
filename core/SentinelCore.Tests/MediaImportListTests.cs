using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// "Import a list": the exports people actually have, read into albums and movies.
public sealed class MediaImportListTests
{
    private static MediaImportList.Result Parse(string name, string text) => MediaImportList.Parse([new(name, text)]);

    [Fact]
    public void Exportify_GroupsTracksIntoAlbums_UnderTheAlbumArtist_WithoutEditionWords()
    {
        var result = Parse("Liked_Songs.csv", "﻿" + """
            "Track URI","Track Name","Artist Name(s)","Album Name","Album Artist Name(s)","Album Release Date"
            "spotify:track:1","Dreams - 2004 Remaster","Fleetwood Mac","Rumours (Super Deluxe)","Fleetwood Mac","1977-02-04"
            "spotify:track:2","Go Your Own Way","Fleetwood Mac","Rumours","Fleetwood Mac","1977"
            "spotify:track:3","Mr. Blue Sky","Electric Light Orchestra","Out of the Blue","Electric Light Orchestra","1977-10-03"
            "spotify:track:4","Under Pressure - Remastered 2011","Queen,David Bowie","Hot Space (2011 Remaster)","Queen","1982"
            "spotify:track:5","Bohemian Rhapsody","Queen","A Night At The Opera - 2011 Remaster","Queen","1975"
            "spotify:track:6","Smells Like Teen Spirit","Nirvana","Now That's What I Call Music, Vol. 1","Various Artists","2000"
            "spotify:track:7","See You Again","Tyler, The Creator,Kali Uchis","Flower Boy","Tyler, The Creator","2017"
            "spotify:track:8","Loose Song","Someone","","Someone",""
            """);
        Assert.Equal("Exportify", Assert.Single(result.Sources));
        Assert.Equal(MediaImportList.KindMusic, result.Kind);
        Assert.Equal(8, result.Tracks);
        Assert.Equal(1, result.NoAlbum);
        var albums = result.Albums.ToDictionary(a => a.Title);
        Assert.Equal(["A Night At The Opera", "Flower Boy", "Hot Space", "Now That's What I Call Music, Vol. 1", "Out of the Blue", "Rumours"], albums.Keys.Order());
        Assert.Equal(2, albums["Rumours"].TrackCount);
        Assert.Equal(1977, albums["Rumours"].Year);
        Assert.Equal("Queen", albums["Hot Space"].Artist);
        Assert.Equal("Various Artists", albums["Now That's What I Call Music, Vol. 1"].Artist);
        Assert.Equal("Tyler, The Creator", albums["Flower Boy"].Artist);
    }

    [Fact]
    public void SpotifyDataExport_ReadsLikedSongsSavedAlbumsAndPlaylists_AndSkipsPodcasts()
    {
        var library = """
            { "tracks": [ { "artist": "Daft Punk", "album": "Discovery", "track": "One More Time", "uri": "spotify:track:a" },
                          { "artist": "Daft Punk", "album": "Discovery", "track": "Digital Love", "uri": "spotify:track:b" } ],
              "albums": [ { "artist": "Radiohead", "album": "OK Computer", "uri": "spotify:album:c" } ],
              "artists": [ { "name": "Björk" } ] }
            """;
        var playlists = """
            { "playlists": [ { "name": "Road trip", "items": [
                { "track": { "trackName": "Africa", "artistName": "TOTO", "albumName": "Toto IV", "trackUri": "spotify:track:d" }, "episode": null },
                { "track": null, "episode": { "episodeName": "A podcast" } },
                { "track": { "trackName": "Digital Love", "artistName": "Daft Punk", "albumName": "Discovery", "trackUri": "spotify:track:b" } } ] } ] }
            """;
        var result = MediaImportList.Parse([new("YourLibrary.json", library), new("Playlist1.json", playlists)]);
        Assert.Equal(["Spotify data export"], result.Sources.Distinct());
        Assert.Equal(["Discovery", "OK Computer", "Toto IV"], result.Albums.Select(a => a.Title).Order());
        Assert.Equal(2, result.Albums.Single(a => a.Title == "Discovery").TrackCount);
        Assert.Equal(1, result.Unreadable);
    }

    [Fact]
    public void TuneMyMusic_AndSemicolonCsv_AreRead()
    {
        var tune = Parse("My Spotify Library.csv", """
            Track name,Artist name,Album,Playlist name,Type,ISRC
            Hey Jude,The Beatles,Hey Jude,Liked Songs,Liked Songs,GBAYE0601736
            """);
        Assert.Equal("TuneMyMusic", Assert.Single(tune.Sources));
        // A one-song release named after the song.
        Assert.True(Assert.Single(tune.Albums).Single);

        var semicolons = Parse("export.csv", "Artist;Album;Title\nPink Floyd;The Wall;Hey You\n");
        Assert.Equal("The Wall", Assert.Single(semicolons.Albums).Title);
        Assert.False(semicolons.Albums[0].Single);
    }

    [Fact]
    public void Letterboxd_AndImdb_AreMovieLists_AndTvShowsAreLeftOut()
    {
        var letterboxd = Parse("watchlist.csv", """
            Date,Name,Year,Letterboxd URI
            2025-01-02,"Crouching Tiger, Hidden Dragon",2000,https://boxd.it/1
            2025-01-03,Sintel,2010,https://boxd.it/2
            2025-01-03,Sintel,2010,https://boxd.it/2
            """);
        Assert.Equal(MediaImportList.KindMovies, letterboxd.Kind);
        Assert.Equal("Letterboxd", Assert.Single(letterboxd.Sources));
        Assert.Equal(["Crouching Tiger, Hidden Dragon", "Sintel"], letterboxd.Movies.Select(m => m.Title));
        Assert.Equal(2000, letterboxd.Movies[0].Year);

        var imdb = Parse("ratings.csv", """
            Const,Your Rating,Date Rated,Title,Original Title,URL,Title Type,IMDb Rating,Runtime (mins),Year,Genres
            tt0133093,10,2024-01-01,The Matrix,The Matrix,https://www.imdb.com/title/tt0133093/,Movie,8.7,136,1999,Action
            tt0903747,10,2024-01-01,Breaking Bad,Breaking Bad,https://www.imdb.com/title/tt0903747/,TV Series,9.5,49,2008,Drama
            tt0185906,9,2024-01-01,Band of Brothers,Band of Brothers,https://x,TV Mini Series,9.4,594,2001,Drama
            """);
        Assert.Equal("IMDb", Assert.Single(imdb.Sources));
        var matrix = Assert.Single(imdb.Movies);
        Assert.Equal(("The Matrix", 1999, "tt0133093"), (matrix.Title, matrix.Year, matrix.ImdbId));
        Assert.Equal(2, imdb.Shows);
    }

    [Fact]
    public void AFileWithNoUsableColumns_SaysWhat_ItNeeds()
    {
        var result = Parse("notes.csv", "Colour,Size\nred,4\n");
        Assert.Empty(result.Albums);
        Assert.Empty(result.Movies);
        Assert.Contains("Artist,Album,Track", Assert.Single(result.Problems));
    }

    [Theory]
    [InlineData("Abbey Road (Remastered 2019)", "Abbey Road")]
    [InlineData("Rumours (Super Deluxe)", "Rumours")]
    [InlineData("Nevermind (30th Anniversary Edition)", "Nevermind")]
    [InlineData("A Night At The Opera - 2011 Remaster", "A Night At The Opera")]
    [InlineData("Led Zeppelin IV (Deluxe Edition) [Remaster]", "Led Zeppelin IV")]
    [InlineData("(What's the Story) Morning Glory?", "(What's the Story) Morning Glory?")]
    [InlineData("Live at Leeds", "Live at Leeds")]
    public void AlbumTitles_LoseEditionWordsOnly(string spotify, string shared) =>
        Assert.Equal(shared, MediaImportList.CleanAlbumTitle(spotify));

    [Theory]
    [InlineData("Queen,David Bowie", "Queen")]
    [InlineData("Simon & Garfunkel", "Simon & Garfunkel")]
    [InlineData("Tyler, The Creator,Kali Uchis", "Tyler, The Creator")]
    [InlineData("Daft Punk;Pharrell Williams", "Daft Punk")]
    public void FirstArtist_IsTheOneTheAlbumIsSharedUnder(string artists, string first) =>
        Assert.Equal(first, MediaImportList.FirstArtist(artists));
}
