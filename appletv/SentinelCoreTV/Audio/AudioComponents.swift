import SwiftUI

// Pieces both the Music and Podcasts tabs are built from: routes, square artwork tiles, track and
// episode rows, the mini player and the "Continue from…" banner.

// Every page reachable from the two tabs. Registered once at each tab's root (audioDestinations).
enum AudioRoute: Hashable {
    case album(String)
    case artist(String)
    case playlist(String)
    case genre(String)
    case decade(Int, String)
    case lovedTracks
    case podcast(String)
    case episode(String)
    case directoryShow(DirectoryShow)
    case stats
    case alarms
    case speakers
    case bookmarks
    case discover
}

// Pushes a page from somewhere a NavigationLink can't live (a context menu, Now Playing). The
// active tab's root takes the request and pushes it - see AudioTabRoot.
@MainActor
final class AudioNavigator: ObservableObject {
    static let shared = AudioNavigator()
    @Published var request: AudioRoute?

    func open(_ route: AudioRoute) {
        AudioCenter.shared.showNowPlaying = false
        request = route
    }
}

// What both tab roots carry: every page's destination, pushes requested through the navigator,
// and the new-playlist prompt.
struct AudioTabRoot: ViewModifier {
    let isActive: Bool
    @ObservedObject private var navigator = AudioNavigator.shared
    @State private var pushed: AudioRoute?

    func body(content: Content) -> some View {
        content
            .audioDestinations()
            .navigationDestination(item: $pushed) { route in AudioRouteView(route: route) }
            .modifier(NewPlaylistAlert())
            .onChange(of: navigator.request) { _, _ in take() }
            // A request made before this root was on screen (launch, tab switch) is picked up too.
            .onAppear { take() }
            .onChange(of: isActive) { _, _ in take() }
    }

    private func take() {
        guard isActive, let request = navigator.request else { return }
        navigator.request = nil
        pushed = request
    }
}

extension View {
    func audioDestinations() -> some View {
        navigationDestination(for: AudioRoute.self) { AudioRouteView(route: $0) }
    }
}

struct AudioRouteView: View {
    let route: AudioRoute

    var body: some View {
            switch route {
            case .album(let id): AlbumTVView(albumId: id)
            case .artist(let id): ArtistTVView(artistId: id)
            case .playlist(let id): PlaylistTVView(playlistId: id)
            case .genre(let name): AlbumGridTVView(title: name, genre: name, decade: nil)
            case .decade(let decade, let label): AlbumGridTVView(title: label, genre: nil, decade: decade)
            case .lovedTracks: LovedTracksTVView()
            case .podcast(let id): PodcastShowTVView(podcastId: id)
            case .episode(let id): EpisodeTVView(episodeId: id)
            case .directoryShow(let show): DirectoryPreviewTVView(show: show)
            case .stats: StatsTVView()
            case .alarms: WakeAlarmsTVView()
            case .speakers: SpeakersTVView()
            case .bookmarks: BookmarksTVView()
            case .discover: DiscoverTVView()
            }
    }
}

// The label colour for a standard tvOS button: light until focused, black once the button fills
// with the bright tint (a white or tinted label vanishes into it). Goes inside the label.
struct AudioFocusLabel: ViewModifier {
    @Environment(\.isFocused) private var isFocused
    var unfocused: Color = .primary

    func body(content: Content) -> some View {
        content.foregroundStyle(isFocused ? Color.black : unfocused)
    }
}

extension View {
    func audioFocusLabel(_ unfocused: Color = .primary) -> some View { modifier(AudioFocusLabel(unfocused: unfocused)) }
}

// A list row as one big button: a faint plate at rest, white with dark text under the remote -
// never the green-tinted platter, whose focused mint swallows coloured or white labels.
struct AudioRowButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        Row(configuration: configuration)
    }

    private struct Row: View {
        let configuration: ButtonStyle.Configuration
        @Environment(\.isFocused) private var isFocused
        @Environment(\.colorScheme) private var colorScheme

        var body: some View {
            configuration.label
                .padding(.horizontal, 24)
                .padding(.vertical, 12)
                .frame(maxWidth: .infinity, alignment: .leading)
                .environment(\.colorScheme, isFocused ? .light : colorScheme)
                .foregroundStyle(isFocused ? Color.black : Color.primary)
                .background(isFocused ? Color.white : Color.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: 14))
                .scaleEffect(isFocused ? (configuration.isPressed ? 1.0 : 1.02) : 1)
                .shadow(color: .black.opacity(isFocused ? 0.35 : 0), radius: 14, y: 8)
                .animation(.easeOut(duration: 0.15), value: isFocused)
        }
    }
}

// Several buttons side by side inside a List row. A tvOS List turns a row into ONE focusable cell,
// and selecting that cell fires every button in it - "Play Latest" also unfollowed the show,
// marked everything played and refreshed it. Inside a horizontal scroll view each button is its
// own focus target again, as it would be outside a List.
struct TVButtonRow<Content: View>: View {
    var spacing: CGFloat = 24
    @ViewBuilder let content: () -> Content

    var body: some View {
        ScrollView(.horizontal) {
            HStack(spacing: spacing) { content() }
                .padding(.vertical, 14)
                .padding(.horizontal, 6)
        }
        .scrollClipDisabled()
        .focusSection()
        .listRowBackground(Color.clear)
        .listRowInsets(EdgeInsets(top: 0, leading: 0, bottom: 0, trailing: 0))
    }
}

// A standard button whose title stays readable under the green tint when focused.
struct AudioButton: View {
    let title: String
    let systemImage: String
    var role: ButtonRole? = nil
    let action: () -> Void

    init(_ title: String, systemImage: String, role: ButtonRole? = nil, action: @escaping () -> Void) {
        self.title = title
        self.systemImage = systemImage
        self.role = role
        self.action = action
    }

    var body: some View {
        Button(role: role, action: action) {
            Label(title, systemImage: systemImage)
                .audioFocusLabel(role == .destructive ? SentinelTheme.red : .primary)
        }
    }
}

// MARK: - Artwork

struct AudioArt: View {
    let path: String?
    let title: String
    var size: Int = 600
    @EnvironmentObject private var audio: AudioCenter

    var body: some View {
        PosterImage(url: audio.api?.art(path, size: size), title: title, maxPixel: CGFloat(size))
    }
}

// Up to four album covers as a 2x2 mosaic (one fills the square), for playlists and mixes.
struct ArtMosaic: View {
    let paths: [String]
    let title: String
    var size: Int = 400

    var body: some View {
        let unique = Array(NSOrderedSet(array: paths).array.compactMap { $0 as? String }.prefix(4))
        if unique.count < 4 {
            AudioArt(path: unique.first, title: title, size: size)
        } else {
            Grid(horizontalSpacing: 0, verticalSpacing: 0) {
                GridRow {
                    AudioArt(path: unique[0], title: title, size: size / 2)
                    AudioArt(path: unique[1], title: title, size: size / 2)
                }
                GridRow {
                    AudioArt(path: unique[2], title: title, size: size / 2)
                    AudioArt(path: unique[3], title: title, size: size / 2)
                }
            }
        }
    }
}

enum AudioTileSize {
    static let square: CGFloat = 280
    static let small: CGFloat = 220
    static let episodeWidth: CGFloat = 420
}

// A square card with the title and a line under it. Either a link to a route or an action.
struct AudioSquareTile<Art: View>: View {
    let title: String
    var subtitle: String?
    var route: AudioRoute?
    var size: CGFloat = AudioTileSize.square
    var circle = false
    var action: (() -> Void)?
    @ViewBuilder let art: Art

    var body: some View {
        VStack(alignment: circle ? .center : .leading, spacing: 12) {
            Group {
                if let route {
                    NavigationLink(value: route) { artwork }
                } else {
                    Button { action?() } label: { artwork }
                }
            }
            .buttonStyle(.card)
            VStack(alignment: circle ? .center : .leading, spacing: 2) {
                Text(title).font(.caption).lineLimit(1)
                if let subtitle, !subtitle.isEmpty {
                    Text(subtitle).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                }
            }
            .frame(width: size, alignment: circle ? .center : .leading)
        }
        .accessibilityElement(children: .combine)
    }

    private var artwork: some View {
        art
            .frame(width: size, height: size)
            .clipShape(circle ? AnyShape(Circle()) : AnyShape(RoundedRectangle(cornerRadius: 12)))
    }
}

struct AlbumTile: View {
    let album: Album
    var size: CGFloat = AudioTileSize.square

    var body: some View {
        AudioSquareTile(title: album.title, subtitle: [album.artist, album.year.map(String.init)].compactMap { $0 }.joined(separator: " · "),
                        route: .album(album.id), size: size) {
            AudioArt(path: album.artUrl ?? "/api/music/albums/\(album.id)/art", title: album.title)
        }
    }
}

struct ArtistTile: View {
    let artist: Artist
    var size: CGFloat = AudioTileSize.small

    var body: some View {
        AudioSquareTile(title: artist.name, subtitle: artist.albumCount.map { "\($0) album\($0 == 1 ? "" : "s")" },
                        route: .artist(artist.id), size: size, circle: true) {
            AudioArt(path: artist.artUrl ?? "/api/music/artists/\(artist.id)/art", title: artist.name, size: 400)
        }
    }
}

struct PlaylistTile: View {
    let playlist: Playlist
    var size: CGFloat = AudioTileSize.square

    var body: some View {
        AudioSquareTile(title: playlist.name, subtitle: playlistSubtitle, route: .playlist(playlist.id), size: size) {
            ArtMosaic(paths: playlist.artUrls ?? [], title: playlist.name)
        }
    }

    private var playlistSubtitle: String {
        var parts: [String] = []
        if playlist.smart == true { parts.append("Smart") }
        if let count = playlist.trackCount { parts.append("\(count) songs") }
        if playlist.mine == false, let owner = playlist.ownerName { parts.append(owner) }
        return parts.joined(separator: " · ")
    }
}

struct MixTile: View {
    let mix: Mix
    var size: CGFloat = AudioTileSize.square
    let play: () -> Void

    var body: some View {
        AudioSquareTile(title: mix.name, subtitle: mix.description, size: size, action: play) {
            ZStack(alignment: .bottomLeading) {
                // No title on the no-artwork card: the name is drawn over it just below.
                ArtMosaic(paths: mix.artUrls ?? [], title: "")
                LinearGradient(colors: [.black.opacity(0.75), .clear], startPoint: .bottom, endPoint: .center)
                Text(mix.name)
                    .font(.headline)
                    .foregroundStyle(.white)
                    .padding(14)
            }
        }
    }
}

struct PodcastTile: View {
    let podcast: Podcast
    var size: CGFloat = AudioTileSize.square

    var body: some View {
        AudioSquareTile(title: podcast.title, subtitle: podcast.author, route: .podcast(podcast.id), size: size) {
            AudioArt(path: podcast.artUrl ?? "/api/podcasts/\(podcast.id)/art", title: podcast.title)
                .overlay(alignment: .topTrailing) {
                    if let unplayed = podcast.unplayedCount, unplayed > 0 {
                        Text("\(unplayed)")
                            .font(.caption2.bold())
                            .foregroundStyle(.black)
                            .padding(.horizontal, 12)
                            .padding(.vertical, 4)
                            .background(SentinelTheme.green, in: Capsule())
                            .padding(10)
                    }
                }
        }
    }
}

// An episode as a wide card: art on the left, show/title/time on the right, progress underneath.
struct EpisodeCard: View {
    let episode: Episode
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(alignment: .top, spacing: 18) {
                AudioArt(path: episode.artUrl, title: episode.podcastTitle ?? episode.title, size: 300)
                    .frame(width: 150, height: 150)
                    .clipShape(RoundedRectangle(cornerRadius: 10))
                VStack(alignment: .leading, spacing: 6) {
                    Text(episode.podcastTitle ?? "").font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                    Text(episode.title).font(.callout.weight(.semibold)).lineLimit(2)
                    Spacer(minLength: 0)
                    HStack(spacing: 10) {
                        Text(EpisodeText.meta(episode)).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                        if episode.isDownloaded { Image(systemName: "arrow.down.circle.fill").font(.caption2).foregroundStyle(.secondary) }
                    }
                    if let fraction = episode.progressFraction {
                        ProgressView(value: fraction).tint(SentinelTheme.green)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .padding(18)
            .frame(width: AudioTileSize.episodeWidth, height: 186)
        }
        .buttonStyle(.card)
    }
}

enum EpisodeText {
    static func meta(_ episode: Episode) -> String {
        var parts: [String] = []
        if let date = AudioDate.parse(episode.publishedAt) {
            let sameYear = Calendar.current.isDate(date, equalTo: Date(), toGranularity: .year)
            parts.append(sameYear ? date.formatted(.dateTime.month(.abbreviated).day()) : date.formatted(.dateTime.month(.abbreviated).day().year()))
        }
        if episode.isPlayed {
            parts.append("Played")
        } else if let fraction = episode.progressFraction, let total = episode.durationSeconds {
            parts.append("\(AudioFormat.minutes(total * (1 - fraction)) ?? "") left")
        } else if let length = AudioFormat.minutes(episode.durationSeconds) {
            parts.append(length)
        }
        return parts.joined(separator: " · ")
    }
}

// A track in a list: number or art, title, artist, length, heart. The whole row is the button.
struct TrackRow: View {
    let track: Track
    var number: Int?
    var showArt = true
    var showAlbum = false
    let play: () -> Void
    @EnvironmentObject private var audio: AudioCenter
    @ObservedObject private var player = AudioCenter.shared.player
    @State private var loved: Bool?

    var body: some View {
        Button(action: play) {
            HStack(spacing: 20) {
                if let number {
                    Text("\(number)").font(.callout.monospacedDigit()).foregroundStyle(.secondary).frame(width: 50, alignment: .trailing)
                }
                if showArt {
                    AudioArt(path: track.artUrl, title: track.album ?? track.title, size: 120)
                        .frame(width: 64, height: 64)
                        .clipShape(RoundedRectangle(cornerRadius: 6))
                }
                VStack(alignment: .leading, spacing: 2) {
                    HStack(spacing: 8) {
                        if player.current?.itemId == track.id {
                            Image(systemName: player.isPlaying ? "speaker.wave.2.fill" : "speaker.fill")
                                .foregroundStyle(SentinelTheme.green)
                        }
                        Text(track.title).lineLimit(1)
                    }
                    Text(showAlbum ? [track.artistLine, track.album].compactMap { $0 }.joined(separator: " — ") : track.artistLine)
                        .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
                Spacer()
                if loved ?? track.isLoved {
                    Image(systemName: "heart.fill").foregroundStyle(SentinelTheme.red)
                }
                Text(AudioFormat.clock(track.durationSeconds)).font(.caption.monospacedDigit()).foregroundStyle(.secondary)
            }
        }
        .buttonStyle(AudioRowButtonStyle())
        .contextMenu { TrackMenu(track: track, loved: $loved) }
    }
}

// What a long press on a track offers.
struct TrackMenu: View {
    let track: Track
    @Binding var loved: Bool?
    @EnvironmentObject private var audio: AudioCenter

    var body: some View {
        Button("Play Next", systemImage: "text.insert") { audio.player.addNext([AudioEntry(track: track)]) }
        Button("Add to Queue", systemImage: "text.append") { audio.player.addLast([AudioEntry(track: track)]) }
        Button("Start Radio", systemImage: "dot.radiowaves.left.and.right") {
            Task { try? await audio.startRadio(seedType: "track", seedId: track.id, name: track.title) }
        }
        let isLoved = loved ?? track.isLoved
        Button(isLoved ? "Unlove" : "Love", systemImage: isLoved ? "heart.slash" : "heart") {
            Task {
                if let result = try? await audio.api?.love(kind: "tracks", id: track.id, loved: !isLoved) { loved = result }
            }
        }
        AddToPlaylistMenu(trackIds: [track.id])
        if let albumId = track.albumId {
            Button("Go to Album", systemImage: "square.stack") { AudioNavigator.shared.open(AudioRoute.album(albumId)) }
        }
        if let artistId = track.artistId {
            Button("Go to Artist", systemImage: "music.mic") { AudioNavigator.shared.open(AudioRoute.artist(artistId)) }
        }
    }
}

// "Add to Playlist" as a submenu of the user's own playlists, plus a new one.
struct AddToPlaylistMenu: View {
    let trackIds: [String]
    @EnvironmentObject private var audio: AudioCenter
    @State private var playlists: [Playlist] = []

    var body: some View {
        Menu {
            Button("New Playlist…", systemImage: "plus") { NewPlaylistPrompt.shared.ask(trackIds: trackIds) }
            ForEach(playlists.filter { $0.mine != false && $0.smart != true || $0.collaborative == true }) { playlist in
                Button(playlist.name) {
                    Task { _ = try? await audio.api?.addToPlaylist(id: playlist.id, trackIds: trackIds) }
                }
            }
        } label: {
            Label("Add to Playlist", systemImage: "music.note.list")
        }
        .task { playlists = (try? await audio.api?.playlists()) ?? [] }
    }
}

// The new-playlist name prompt lives at the tab root (an alert with a text field); menus ask it.
@MainActor
final class NewPlaylistPrompt: ObservableObject {
    static let shared = NewPlaylistPrompt()
    @Published var pendingTrackIds: [String]?
    @Published var name = ""
    @Published var lastCreated: String?

    func ask(trackIds: [String]) {
        name = ""
        pendingTrackIds = trackIds
    }

    func create(api: AudioAPI?) async {
        guard let ids = pendingTrackIds, let api else { return }
        pendingTrackIds = nil
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        if let playlist = try? await api.createPlaylist(PlaylistInput(name: trimmed, trackIds: ids)) {
            lastCreated = playlist.name
        }
    }
}

struct NewPlaylistAlert: ViewModifier {
    @ObservedObject private var prompt = NewPlaylistPrompt.shared
    @EnvironmentObject private var audio: AudioCenter

    func body(content: Content) -> some View {
        content.alert("New Playlist", isPresented: Binding(get: { prompt.pendingTrackIds != nil }, set: { if !$0 { prompt.pendingTrackIds = nil } })) {
            TextField("Name", text: $prompt.name)
            Button("Create") { Task { await prompt.create(api: audio.api) } }
            Button("Cancel", role: .cancel) { prompt.pendingTrackIds = nil }
        } message: {
            Text("The songs are added to it straight away.")
        }
    }
}

// An episode in a show's list.
struct EpisodeRow: View {
    let episode: Episode
    let play: () -> Void
    var showPodcast = false

    var body: some View {
        Button(action: play) {
            HStack(alignment: .top, spacing: 20) {
                Image(systemName: episode.isPlayed ? "checkmark.circle" : (episode.progressFraction != nil ? "circle.lefthalf.filled" : "circle.fill"))
                    .font(.caption)
                    .foregroundStyle(episode.isPlayed ? Color.secondary : SentinelTheme.green)
                    .padding(.top, 8)
                VStack(alignment: .leading, spacing: 4) {
                    if showPodcast, let podcast = episode.podcastTitle {
                        Text(podcast).font(.caption2).foregroundStyle(.secondary)
                    }
                    Text(episode.title).lineLimit(2)
                    if let summary = episode.summary, !summary.isEmpty {
                        Text(summary).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                    }
                    HStack(spacing: 14) {
                        Text(EpisodeText.meta(episode))
                        if episode.isDownloaded { Label("Downloaded", systemImage: "arrow.down.circle") }
                        if episode.hasTranscript == true { Label("Transcript", systemImage: "text.quote") }
                        if episode.hasChapters == true { Label("Chapters", systemImage: "list.bullet") }
                        if episode.inQueue == true { Label("Up Next", systemImage: "text.line.first.and.arrowtriangle.forward") }
                    }
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                    if let fraction = episode.progressFraction {
                        ProgressView(value: fraction).tint(SentinelTheme.green).frame(maxWidth: 400)
                    }
                }
                Spacer()
            }
        }
        .buttonStyle(AudioRowButtonStyle())
        .contextMenu { EpisodeMenu(episode: episode) }
    }
}

struct EpisodeMenu: View {
    let episode: Episode
    @EnvironmentObject private var audio: AudioCenter

    var body: some View {
        Button("Play Next", systemImage: "text.insert") {
            audio.player.addNext([AudioEntry(episode: episode)])
            Task { _ = try? await audio.api?.enqueue(episodeId: episode.id, next: true) }
        }
        Button("Add to Up Next", systemImage: "text.append") {
            Task { _ = try? await audio.api?.enqueue(episodeId: episode.id, next: false) }
        }
        Button(episode.isPlayed ? "Mark as Unplayed" : "Mark as Played", systemImage: episode.isPlayed ? "circle" : "checkmark.circle") {
            Task { try? await audio.api?.setPlayed(episodeId: episode.id, played: !episode.isPlayed) }
        }
        if !episode.isDownloaded {
            Button("Download to Server", systemImage: "arrow.down.circle") {
                Task { _ = try? await audio.api?.downloadEpisode(id: episode.id) }
            }
        }
        Button("Episode Details", systemImage: "info.circle") { AudioNavigator.shared.open(AudioRoute.episode(episode.id)) }
        if let podcastId = episode.podcastId {
            Button("Go to Show", systemImage: "square.stack") { AudioNavigator.shared.open(AudioRoute.podcast(podcastId)) }
        }
    }
}

// MARK: - Mini player and Continue banner (top of both tabs)

struct AudioTopBar: View {
    @EnvironmentObject private var audio: AudioCenter
    @ObservedObject private var player = AudioCenter.shared.player

    var body: some View {
        if player.current != nil || audio.continueOffer != nil {
            HStack(spacing: 30) {
                if let entry = player.current {
                    Button {
                        audio.showNowPlaying = true
                    } label: {
                        HStack(spacing: 18) {
                            AudioArt(path: entry.artPath, title: entry.title, size: 120)
                                .frame(width: 60, height: 60)
                                .clipShape(RoundedRectangle(cornerRadius: 6))
                            VStack(alignment: .leading, spacing: 2) {
                                Text(entry.title).font(.callout.weight(.semibold)).lineLimit(1)
                                Text(player.spokenLine ?? entry.subtitle).font(.caption).lineLimit(1).opacity(0.7)
                            }
                            .frame(maxWidth: 600, alignment: .leading)
                            Image(systemName: player.isPlaying ? "waveform" : "pause.fill")
                                .symbolEffect(.variableColor.iterative, isActive: player.isPlaying)
                        }
                        .audioFocusLabel()
                    }
                    Button {
                        player.togglePlayPause()
                    } label: {
                        Image(systemName: player.isPlaying ? "pause.fill" : "play.fill").audioFocusLabel()
                    }
                    .accessibilityLabel(player.isPlaying ? "Pause" : "Play")
                } else if let offer = audio.continueOffer, let entry = offer.current {
                    Button {
                        audio.acceptContinue()
                    } label: {
                        HStack(spacing: 18) {
                            AudioArt(path: entry.artPath, title: entry.title, size: 120)
                                .frame(width: 60, height: 60)
                                .clipShape(RoundedRectangle(cornerRadius: 6))
                            VStack(alignment: .leading, spacing: 2) {
                                Text("Continue from \(offer.nowPlaying.deviceName ?? "another device")")
                                    .font(.caption.weight(.semibold))
                                Text("\(entry.title) — \(entry.artist) · \(AudioFormat.clock(offer.nowPlaying.positionSeconds))")
                                    .font(.caption).lineLimit(1).opacity(0.7)
                            }
                            Image(systemName: "arrow.right.circle.fill")
                        }
                        .audioFocusLabel()
                    }
                    Button {
                        audio.dismissContinue()
                    } label: {
                        Image(systemName: "xmark").audioFocusLabel()
                    }
                    .accessibilityLabel("Not now")
                }
                Spacer()
            }
            .padding(.horizontal, 60)
        }
    }
}

// An empty/loading/error overlay shared by the screens.
struct AudioStateOverlay: View {
    let loading: Bool
    let error: String?
    let isEmpty: Bool
    var emptyTitle = "Nothing here yet"
    var emptyIcon = "music.note"
    var emptyMessage: String?

    var body: some View {
        if isEmpty {
            if loading {
                ProgressView("Loading…")
            } else if let error {
                ContentUnavailableView("Couldn't load this", systemImage: "exclamationmark.triangle", description: Text(error))
            } else {
                ContentUnavailableView(emptyTitle, systemImage: emptyIcon, description: emptyMessage.map { Text($0) })
            }
        }
    }
}

struct AudioSectionHeader: View {
    let title: String
    var body: some View {
        Text(title).font(.title3.bold()).padding(.horizontal, 60)
    }
}
