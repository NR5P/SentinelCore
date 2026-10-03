import SwiftUI

// The pieces every Music screen is built from: rows, tiles, shelves and the context menu that
// hangs off every track and album.

// Loves and ratings changed on this device, so every row showing that track agrees at once -
// without each screen having to reload. The server stays the source of truth on the next load.
@MainActor
@Observable
final class MusicState {
    static let shared = MusicState()
    private(set) var loved: [String: Bool] = [:]
    private(set) var ratings: [String: Int] = [:]
    private(set) var albumLoved: [String: Bool] = [:]
    private(set) var artistLoved: [String: Bool] = [:]

    func isLoved(_ track: MusicTrack) -> Bool { loved[track.id] ?? track.isLoved }
    func rating(_ track: MusicTrack) -> Int { ratings[track.id] ?? track.rating ?? 0 }
    func isLoved(album: MusicAlbum) -> Bool { albumLoved[album.id] ?? (album.loved == true) }
    func isLoved(artist: MusicArtist) -> Bool { artistLoved[artist.id] ?? (artist.loved == true) }

    func toggleLove(_ track: MusicTrack) {
        let value = !isLoved(track)
        loved[track.id] = value
        var updated = track
        updated.loved = value
        AudioPlayer.shared.updateTrack(updated)
        Task { _ = try? await AppModel.shared.client.setLoved("tracks", id: track.id, loved: value) }
        AudioToast.shared.show(value ? "Loved" : "Removed from Loved")
    }

    func rate(_ track: MusicTrack, _ value: Int) {
        ratings[track.id] = value
        var updated = track
        updated.rating = value
        AudioPlayer.shared.updateTrack(updated)
        Task { _ = try? await AppModel.shared.client.setRating("tracks", id: track.id, rating: value) }
    }

    func toggleLove(album: MusicAlbum) {
        let value = !isLoved(album: album)
        albumLoved[album.id] = value
        Task { _ = try? await AppModel.shared.client.setLoved("albums", id: album.id, loved: value) }
        AudioToast.shared.show(value ? "Loved" : "Removed from Loved")
    }

    func rate(album: MusicAlbum, _ value: Int) {
        Task { _ = try? await AppModel.shared.client.setRating("albums", id: album.id, rating: value) }
    }

    func toggleLove(artist: MusicArtist) {
        let value = !isLoved(artist: artist)
        artistLoved[artist.id] = value
        Task { _ = try? await AppModel.shared.client.setLoved("artists", id: artist.id, loved: value) }
        AudioToast.shared.show(value ? "Loved" : "Removed from Loved")
    }

    // Sheets that any row can ask for, presented once at the root.
    var addToPlaylist: AddToPlaylistRequest?
}

struct AddToPlaylistRequest: Identifiable {
    let id = UUID()
    let trackIds: [String]
    let title: String
}

// Starting things playing, from anywhere.
@MainActor
enum MusicActions {
    static var client: SentinelClient { AppModel.shared.client }

    static func play(album: MusicAlbum, shuffle: Bool = false, startAt trackId: String? = nil) {
        Task {
            guard let detail = try? await client.musicAlbum(id: album.id) else { return }
            let start = trackId.flatMap { id in detail.tracks.firstIndex { $0.id == id } } ?? 0
            AudioPlayer.shared.play(tracks: detail.tracks, startAt: start,
                                    context: AudioPlayContext(type: "album", id: album.id, name: album.title), shuffle: shuffle)
        }
    }

    static func queue(album: MusicAlbum, next: Bool) {
        Task {
            guard let detail = try? await client.musicAlbum(id: album.id) else { return }
            let items = detail.tracks.map(AudioItem.init(track:))
            next ? AudioPlayer.shared.playNext(items) : AudioPlayer.shared.playLater(items)
        }
    }

    static func play(playlist: MusicPlaylist, shuffle: Bool = false) {
        Task {
            guard let detail = try? await client.musicPlaylist(id: playlist.id) else { return }
            AudioPlayer.shared.play(tracks: detail.tracks,
                                    context: AudioPlayContext(type: "playlist", id: playlist.id, name: playlist.name), shuffle: shuffle)
        }
    }

    static func queue(playlist: MusicPlaylist, next: Bool) {
        Task {
            guard let detail = try? await client.musicPlaylist(id: playlist.id) else { return }
            let items = detail.tracks.map(AudioItem.init(track:))
            next ? AudioPlayer.shared.playNext(items) : AudioPlayer.shared.playLater(items)
        }
    }

    static func play(mix: MusicMix) {
        Task {
            do {
                let result = try await client.musicMix(id: mix.id)
                AudioPlayer.shared.play(tracks: result.tracks, context: AudioPlayContext(type: "mix", id: mix.id, name: result.name ?? mix.name))
            } catch {
                AudioToast.shared.show("Couldn't start \(mix.name)")
            }
        }
    }

    // An endless radio from any seed; the player tops it up as it runs low (see AudioPlayer).
    static func startRadio(seedType: String, seedId: String, name: String) {
        Task {
            do {
                let result = try await client.musicRadio(seedType: seedType, seedId: seedId)
                guard !result.tracks.isEmpty else {
                    AudioToast.shared.show("Nothing to play for that radio yet")
                    return
                }
                AudioPlayer.shared.play(tracks: result.tracks,
                                        context: AudioPlayContext(type: "radio", id: "\(seedType):\(seedId)", name: result.name ?? "\(name) Radio"))
            } catch {
                AudioToast.shared.show("Couldn't start the radio")
            }
        }
    }

    static func shuffleAll() {
        Task {
            do {
                let tracks = try await client.musicShuffle()
                AudioPlayer.shared.play(tracks: tracks, context: AudioPlayContext(type: "queue", id: "shuffle", name: "Shuffle All"))
            } catch {
                AudioToast.shared.show("Couldn't shuffle the library")
            }
        }
    }

    static func download(album: MusicAlbum) {
        Task {
            guard let detail = try? await client.musicAlbum(id: album.id) else { return }
            AudioDownloads.shared.save(tracks: detail.tracks)
        }
    }

    static func download(playlist: MusicPlaylist) {
        Task {
            guard let detail = try? await client.musicPlaylist(id: playlist.id) else { return }
            AudioDownloads.shared.save(tracks: detail.tracks)
        }
    }
}

// MARK: - Track rows

struct TrackRow: View {
    let track: MusicTrack
    var showArt = true
    var number: Int? = nil
    var showAlbum = true
    var onRemove: (() -> Void)? = nil
    let onTap: () -> Void

    var body: some View {
        let player = AudioPlayer.shared
        let isCurrent = player.current?.track?.id == track.id
        let state = MusicState.shared
        HStack(spacing: 12) {
            if showArt {
                ArtworkImage(path: track.artUrl, size: 44, corner: 5)
                    .overlay {
                        if isCurrent { NowPlayingBars(playing: player.isPlaying).padding(10).background(.black.opacity(0.45)).clipShape(RoundedRectangle(cornerRadius: 5)) }
                    }
            } else {
                Group {
                    if isCurrent {
                        NowPlayingBars(playing: player.isPlaying)
                    } else {
                        Text(number.map(String.init) ?? "").foregroundStyle(.secondary).monospacedDigit()
                    }
                }
                .frame(width: 26, alignment: .trailing)
                .font(.subheadline)
            }
            VStack(alignment: .leading, spacing: 2) {
                Text(track.title)
                    .foregroundStyle(isCurrent ? SentinelTheme.green : .primary)
                    .lineLimit(1)
                Text(subtitle).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer(minLength: 6)
            OfflineBadge(kind: "track", id: track.id)
            if state.isLoved(track) {
                Image(systemName: "heart.fill").font(.caption).foregroundStyle(.pink).accessibilityLabel("Loved")
            }
            Text(AudioFormat.duration(track.duration)).font(.caption.monospacedDigit()).foregroundStyle(.secondary)
            Menu {
                TrackMenuItems(track: track, onRemove: onRemove)
            } label: {
                Image(systemName: "ellipsis").frame(width: 24, height: 28).contentShape(Rectangle())
            }
            .menuStyle(.button)
            .buttonStyle(.plain)
            .menuIndicator(.hidden)
            .foregroundStyle(.secondary)
            .accessibilityLabel("More for \(track.title)")
        }
        .contentShape(Rectangle())
        .onTapGesture(perform: onTap)
        .contextMenu { TrackMenuItems(track: track, onRemove: onRemove) }
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
        .accessibilityAction(named: "Play", onTap)
    }

    private var subtitle: String {
        var parts = [track.artistLine]
        if showAlbum, let album = track.album, showArt { parts.append(album) }
        return parts.joined(separator: " · ")
    }
}

// Three animated bars: the "this is the one playing" mark.
struct NowPlayingBars: View {
    let playing: Bool

    var body: some View {
        TimelineView(.animation(minimumInterval: 0.12, paused: !playing)) { context in
            let t = context.date.timeIntervalSinceReferenceDate
            HStack(alignment: .bottom, spacing: 2) {
                ForEach(0..<3) { i in
                    RoundedRectangle(cornerRadius: 1)
                        .fill(SentinelTheme.green)
                        .frame(width: 3, height: playing ? 4 + 10 * abs(sin(t * (2.2 + Double(i) * 0.7) + Double(i))) : 4)
                }
            }
            .frame(height: 14, alignment: .bottom)
        }
        .accessibilityLabel(playing ? "Now playing" : "Paused")
    }
}

struct TrackMenuItems: View {
    let track: MusicTrack
    var onRemove: (() -> Void)? = nil

    var body: some View {
        let state = MusicState.shared
        Button("Play Next", systemImage: "text.line.first.and.arrowtriangle.forward") {
            AudioPlayer.shared.playNext([AudioItem(track: track)])
        }
        Button("Play Later", systemImage: "text.line.last.and.arrowtriangle.forward") {
            AudioPlayer.shared.playLater([AudioItem(track: track)])
        }
        Button("Add to Playlist…", systemImage: "text.badge.plus") {
            state.addToPlaylist = AddToPlaylistRequest(trackIds: [track.id], title: track.title)
        }
        Button("Start Radio", systemImage: "dot.radiowaves.left.and.right") {
            MusicActions.startRadio(seedType: "track", seedId: track.id, name: track.title)
        }
        Divider()
        if let albumId = track.albumId {
            Button("Go to Album", systemImage: "square.stack") { AudioRouter.shared.open(.album(albumId)) }
        }
        if let artistId = track.artistId {
            Button("Go to Artist", systemImage: "music.mic") { AudioRouter.shared.open(.artist(artistId)) }
        }
        Divider()
        Button(state.isLoved(track) ? "Unlove" : "Love", systemImage: state.isLoved(track) ? "heart.slash" : "heart") {
            state.toggleLove(track)
        }
        Menu("Rating", systemImage: "star") {
            ForEach((0...5).reversed(), id: \.self) { value in
                Button {
                    state.rate(track, value)
                } label: {
                    if state.rating(track) == value {
                        Label(value == 0 ? "None" : String(repeating: "★", count: value), systemImage: "checkmark")
                    } else {
                        Text(value == 0 ? "None" : String(repeating: "★", count: value))
                    }
                }
            }
        }
        if AudioDownloads.shared.isSaved(track: track) {
            Button("Remove Download", systemImage: "trash") { AudioDownloads.shared.remove("track", track.id) }
        } else {
            Button("Download", systemImage: "arrow.down.circle") { AudioDownloads.shared.save(tracks: [track]) }
        }
        if let onRemove {
            Divider()
            Button("Remove from Playlist", systemImage: "minus.circle", role: .destructive, action: onRemove)
        }
    }
}

// MARK: - Tiles

struct AlbumTile: View {
    let album: MusicAlbum
    var width: CGFloat? = 150

    var body: some View {
        NavigationLink {
            MusicAlbumView(albumId: album.id, initial: album)
        } label: {
            VStack(alignment: .leading, spacing: 5) {
                ArtworkImage(path: album.artUrl, size: width, corner: 8)
                    .shadow(color: .black.opacity(0.15), radius: 4, y: 2)
                Text(album.title).font(.subheadline.weight(.medium)).lineLimit(1)
                Text(album.artist ?? "").font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            .frame(width: width)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .contextMenu { AlbumMenuItems(album: album) }
        .accessibilityLabel("\(album.title) by \(album.artist ?? "unknown artist")")
    }
}

struct AlbumMenuItems: View {
    let album: MusicAlbum

    var body: some View {
        let state = MusicState.shared
        Button("Play", systemImage: "play.fill") { MusicActions.play(album: album) }
        Button("Shuffle", systemImage: "shuffle") { MusicActions.play(album: album, shuffle: true) }
        Button("Play Next", systemImage: "text.line.first.and.arrowtriangle.forward") { MusicActions.queue(album: album, next: true) }
        Button("Play Later", systemImage: "text.line.last.and.arrowtriangle.forward") { MusicActions.queue(album: album, next: false) }
        Button("Start Radio", systemImage: "dot.radiowaves.left.and.right") {
            MusicActions.startRadio(seedType: "album", seedId: album.id, name: album.title)
        }
        Button("Add to Playlist…", systemImage: "text.badge.plus") {
            Task {
                guard let detail = try? await AppModel.shared.client.musicAlbum(id: album.id) else { return }
                state.addToPlaylist = AddToPlaylistRequest(trackIds: detail.tracks.map(\.id), title: album.title)
            }
        }
        Divider()
        if let artistId = album.artistId {
            Button("Go to Artist", systemImage: "music.mic") { AudioRouter.shared.open(.artist(artistId)) }
        }
        Button(state.isLoved(album: album) ? "Unlove" : "Love", systemImage: state.isLoved(album: album) ? "heart.slash" : "heart") {
            state.toggleLove(album: album)
        }
        Button("Download", systemImage: "arrow.down.circle") { MusicActions.download(album: album) }
    }
}

struct ArtistTile: View {
    let artist: MusicArtist
    var width: CGFloat = 120

    var body: some View {
        NavigationLink {
            MusicArtistView(artistId: artist.id, initial: artist)
        } label: {
            VStack(spacing: 6) {
                ArtworkImage(path: artist.artUrl, size: width, placeholder: "music.mic", circle: true)
                Text(artist.name).font(.subheadline).lineLimit(1)
            }
            .frame(width: width)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .contextMenu {
            Button("Start Radio", systemImage: "dot.radiowaves.left.and.right") {
                MusicActions.startRadio(seedType: "artist", seedId: artist.id, name: artist.name)
            }
            Button(MusicState.shared.isLoved(artist: artist) ? "Unlove" : "Love", systemImage: "heart") {
                MusicState.shared.toggleLove(artist: artist)
            }
        }
    }
}

struct PlaylistTile: View {
    let playlist: MusicPlaylist
    var width: CGFloat? = 150

    var body: some View {
        NavigationLink {
            MusicPlaylistView(playlistId: playlist.id, initial: playlist)
        } label: {
            VStack(alignment: .leading, spacing: 5) {
                MosaicArtwork(paths: playlist.artUrls ?? [], size: width, placeholder: playlist.isSmart ? "gearshape.2" : "music.note.list")
                    .shadow(color: .black.opacity(0.15), radius: 4, y: 2)
                HStack(spacing: 4) {
                    if playlist.pinned == true { Image(systemName: "pin.fill").font(.caption2).foregroundStyle(SentinelTheme.green) }
                    Text(playlist.name).font(.subheadline.weight(.medium)).lineLimit(1)
                }
                Text(playlistSubtitle(playlist)).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            .frame(width: width)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .contextMenu {
            Button("Play", systemImage: "play.fill") { MusicActions.play(playlist: playlist) }
            Button("Shuffle", systemImage: "shuffle") { MusicActions.play(playlist: playlist, shuffle: true) }
            Button("Play Next", systemImage: "text.line.first.and.arrowtriangle.forward") { MusicActions.queue(playlist: playlist, next: true) }
            Button("Play Later", systemImage: "text.line.last.and.arrowtriangle.forward") { MusicActions.queue(playlist: playlist, next: false) }
            Button("Start Radio", systemImage: "dot.radiowaves.left.and.right") {
                MusicActions.startRadio(seedType: "playlist", seedId: playlist.id, name: playlist.name)
            }
            Button("Download", systemImage: "arrow.down.circle") { MusicActions.download(playlist: playlist) }
        }
    }
}

func playlistSubtitle(_ playlist: MusicPlaylist) -> String {
    var parts: [String] = []
    if playlist.isSmart { parts.append("Smart") }
    if playlist.collaborative == true { parts.append("Collaborative") } else if playlist.shared == true { parts.append("Shared") }
    if playlist.isMine == false, let owner = playlist.ownerName { parts.append("by \(owner)") }
    if let count = playlist.trackCount { parts.append("\(count) song\(count == 1 ? "" : "s")") }
    return parts.joined(separator: " · ")
}

// A mix card: a vivid gradient over its covers, the name on top.
struct MixTile: View {
    let mix: MusicMix
    var width: CGFloat = 160

    var body: some View {
        Button {
            MusicActions.play(mix: mix)
        } label: {
            VStack(alignment: .leading, spacing: 5) {
                ZStack(alignment: .bottomLeading) {
                    MosaicArtwork(paths: mix.artUrls ?? [], size: width, corner: 10, placeholder: "sparkles")
                    LinearGradient(colors: [mixColor.opacity(0.95), mixColor.opacity(0.15)], startPoint: .bottom, endPoint: .top)
                        .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
                    VStack(alignment: .leading, spacing: 2) {
                        Text(mix.name).font(.headline).foregroundStyle(.white).lineLimit(2)
                        Image(systemName: "play.circle.fill").font(.title2).foregroundStyle(.white.opacity(0.9))
                    }
                    .padding(10)
                }
                .frame(width: width, height: width)
                Text(mix.description ?? "").font(.caption).foregroundStyle(.secondary).lineLimit(2)
                    .frame(width: width, alignment: .leading)
            }
        }
        .buttonStyle(.plain)
        .contextMenu {
            Button("Play", systemImage: "play.fill") { MusicActions.play(mix: mix) }
            NavigationLink {
                MusicMixView(mix: mix)
            } label: {
                Label("Show Songs", systemImage: "list.bullet")
            }
        }
        .accessibilityLabel("Play \(mix.name)")
    }

    private var mixColor: Color {
        let palette: [Color] = [.pink, .orange, .purple, .blue, .teal, .indigo, .red, .mint]
        let hash = mix.id.unicodeScalars.reduce(0) { ($0 &* 31 &+ Int($1.value)) & 0xFFFF }
        return palette[hash % palette.count]
    }
}

// MARK: - Shelves

struct AudioShelf<Content: View, Destination: View>: View {
    let title: String
    var subtitle: String? = nil
    @ViewBuilder let destination: () -> Destination
    @ViewBuilder let content: () -> Content
    var hasDestination = true

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .firstTextBaseline) {
                VStack(alignment: .leading, spacing: 1) {
                    Text(title).font(.title3.bold())
                    if let subtitle { Text(subtitle).font(.caption).foregroundStyle(.secondary) }
                }
                Spacer()
                if hasDestination {
                    NavigationLink("See All", destination: destination)
                        .font(.subheadline)
                }
            }
            .padding(.horizontal)
            ScrollView(.horizontal, showsIndicators: false) {
                LazyHStack(alignment: .top, spacing: 14) {
                    content()
                }
                .padding(.horizontal)
            }
        }
    }
}

extension AudioShelf where Destination == EmptyView {
    init(title: String, subtitle: String? = nil, @ViewBuilder content: @escaping () -> Content) {
        self.title = title
        self.subtitle = subtitle
        self.destination = { EmptyView() }
        self.content = content
        self.hasDestination = false
    }
}

// Songs on a shelf: Apple Music's four-row grid that pages sideways.
struct TrackGridShelf: View {
    let title: String
    var subtitle: String? = nil
    let tracks: [MusicTrack]
    let context: AudioPlayContext

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .firstTextBaseline) {
                VStack(alignment: .leading, spacing: 1) {
                    Text(title).font(.title3.bold())
                    if let subtitle { Text(subtitle).font(.caption).foregroundStyle(.secondary) }
                }
                Spacer()
                Button {
                    AudioPlayer.shared.play(tracks: tracks, context: context)
                } label: {
                    Label("Play", systemImage: "play.fill").font(.subheadline)
                }
                .buttonStyle(.borderless)
                NavigationLink("See All") {
                    TrackListScreen(title: title, tracks: tracks, context: context)
                }
                .font(.subheadline)
            }
            .padding(.horizontal)
            ScrollView(.horizontal, showsIndicators: false) {
                LazyHGrid(rows: Array(repeating: GridItem(.fixed(52), spacing: 4), count: min(4, max(1, tracks.count))), spacing: 18) {
                    ForEach(Array(tracks.enumerated()), id: \.offset) { offset, track in
                        TrackRow(track: track) {
                            AudioPlayer.shared.play(tracks: tracks, startAt: offset, context: context)
                        }
                        .frame(width: 320)
                    }
                }
                .padding(.horizontal)
            }
        }
    }
}

// A plain screen of songs with Play / Shuffle at the top.
struct TrackListScreen: View {
    let title: String
    let tracks: [MusicTrack]
    let context: AudioPlayContext

    var body: some View {
        List {
            PlayShuffleButtons(
                play: { AudioPlayer.shared.play(tracks: tracks, context: context) },
                shuffle: { AudioPlayer.shared.play(tracks: tracks, context: context, shuffle: true) })
            ForEach(Array(tracks.enumerated()), id: \.offset) { offset, track in
                TrackRow(track: track) { AudioPlayer.shared.play(tracks: tracks, startAt: offset, context: context) }
            }
        }
        .listStyle(.plain)
        .navigationTitle(title)
    }
}

struct PlayShuffleButtons: View {
    let play: () -> Void
    let shuffle: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            Button(action: play) {
                Label("Play", systemImage: "play.fill").frame(maxWidth: .infinity)
            }
            Button(action: shuffle) {
                Label("Shuffle", systemImage: "shuffle").frame(maxWidth: .infinity)
            }
        }
        .buttonStyle(.bordered)
        .controlSize(.large)
        .tint(SentinelTheme.green)
        .listRowSeparator(.hidden)
    }
}

// MARK: - Add to playlist

struct AddToPlaylistSheet: View {
    let request: AddToPlaylistRequest
    @Environment(\.dismiss) private var dismiss
    @State private var playlists: [MusicPlaylist] = []
    @State private var loading = true
    @State private var creating = false
    @State private var errorMessage: String?

    var body: some View {
        NavigationStack {
            List {
                Section {
                    Button {
                        creating = true
                    } label: {
                        Label("New Playlist…", systemImage: "plus")
                    }
                }
                Section {
                    if loading { ProgressView() }
                    ForEach(playlists.filter(\.canEditTracks)) { playlist in
                        Button {
                            Task { await add(to: playlist) }
                        } label: {
                            HStack {
                                MosaicArtwork(paths: playlist.artUrls ?? [], size: 44, corner: 5)
                                VStack(alignment: .leading) {
                                    Text(playlist.name).foregroundStyle(.primary)
                                    Text(playlistSubtitle(playlist)).font(.caption).foregroundStyle(.secondary)
                                }
                            }
                        }
                        .buttonStyle(.plain)
                    }
                } footer: {
                    if let errorMessage { Text(errorMessage).foregroundStyle(SentinelTheme.red) }
                }
            }
            .navigationTitle("Add \u{201C}\(request.title)\u{201D}")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar { ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } } }
            .task {
                playlists = (try? await AppModel.shared.client.musicPlaylists()) ?? []
                loading = false
            }
            .sheet(isPresented: $creating) {
                PlaylistEditorView(existing: nil, initialTrackIds: request.trackIds) { _ in
                    dismiss()
                }
            }
        }
        #if os(macOS)
        .frame(minWidth: 440, minHeight: 520)
        #endif
    }

    private func add(to playlist: MusicPlaylist) async {
        do {
            try await AppModel.shared.client.addToPlaylist(id: playlist.id, trackIds: request.trackIds)
            AudioToast.shared.show("Added to \(playlist.name)")
            NotificationCenter.default.post(name: .audioLibraryChanged, object: nil)
            dismiss()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

// A row of five stars that can be tapped (or clicked) to rate.
struct StarRatingControl: View {
    let rating: Int
    let onRate: (Int) -> Void

    var body: some View {
        HStack(spacing: 3) {
            ForEach(1...5, id: \.self) { star in
                Image(systemName: star <= rating ? "star.fill" : "star")
                    .foregroundStyle(star <= rating ? Color.orange : .secondary)
                    .onTapGesture { onRate(star == rating ? 0 : star) }
            }
        }
        .font(.caption)
        .accessibilityElement()
        .accessibilityLabel("Rating")
        .accessibilityValue(rating == 0 ? "None" : "\(rating) stars")
        .accessibilityAdjustableAction { direction in
            switch direction {
            case .increment: onRate(min(5, rating + 1))
            case .decrement: onRate(max(0, rating - 1))
            @unknown default: break
            }
        }
    }
}

// Loading / failed / empty, the same everywhere.
struct AudioLoadState: View {
    let loading: Bool
    let error: String?
    var empty: (title: String, symbol: String, message: String)? = nil
    var retry: (() -> Void)? = nil

    var body: some View {
        if loading {
            ProgressView().frame(maxWidth: .infinity, minHeight: 120)
        } else if let error {
            ContentUnavailableView {
                Label("Couldn't load this", systemImage: "exclamationmark.triangle")
            } description: {
                Text(error)
            } actions: {
                if let retry { Button("Try Again", action: retry) }
            }
        } else if let empty {
            ContentUnavailableView(empty.title, systemImage: empty.symbol, description: Text(empty.message))
        }
    }
}

extension Error {
    var isCancellation: Bool {
        self is CancellationError || (self as? URLError)?.code == .cancelled
    }
}
