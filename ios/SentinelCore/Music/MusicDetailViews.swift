import SwiftUI

// Album, artist and playlist pages. Each opens on what the tile already knew (`initial`) so the
// header is there at once, then fills in from the server.

// MARK: - Album

struct MusicAlbumView: View {
    let albumId: String
    var initial: MusicAlbum? = nil
    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
    @State private var detail: MusicAlbumDetail?
    @State private var error: String?
    @State private var rating = 0

    private var album: MusicAlbum? { detail?.album ?? initial }
    private var tracks: [MusicTrack] { detail?.tracks ?? [] }
    private var context: AudioPlayContext { AudioPlayContext(type: "album", id: albumId, name: album?.title) }

    var body: some View {
        List {
            header
                .listRowSeparator(.hidden)
                .listRowInsets(EdgeInsets(top: 12, leading: 16, bottom: 12, trailing: 16))
            let discs = Set(tracks.map { $0.discNumber ?? 1 }).sorted()
            ForEach(discs, id: \.self) { disc in
                Section {
                    ForEach(tracks.filter { ($0.discNumber ?? 1) == disc }) { track in
                        TrackRow(track: track, showArt: false, number: track.trackNumber) {
                            AudioPlayer.shared.play(tracks: tracks, startAt: tracks.firstIndex(of: track) ?? 0, context: context)
                        }
                    }
                } header: {
                    if discs.count > 1 { Text("Disc \(disc)") }
                }
            }
            if let detail {
                footer(detail)
                    .listRowSeparator(.hidden)
                if let more = detail.moreByArtist, !more.isEmpty {
                    VStack(alignment: .leading, spacing: 10) {
                        Text("More by \(detail.album.artist ?? "this artist")").font(.title3.bold())
                        ScrollView(.horizontal, showsIndicators: false) {
                            LazyHStack(alignment: .top, spacing: 14) {
                                ForEach(more) { AlbumTile(album: $0, width: 140) }
                            }
                        }
                    }
                    .listRowSeparator(.hidden)
                    .padding(.vertical, 8)
                }
            }
            if let error { Text(error).foregroundStyle(SentinelTheme.red) }
        }
        .listStyle(.plain)
        .navigationTitle(album?.title ?? "Album")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                if let album {
                    Menu {
                        AlbumMenuItems(album: album)
                    } label: {
                        Label("More", systemImage: "ellipsis.circle")
                    }
                }
            }
        }
        .task { await load() }
    }

    @ViewBuilder
    private var header: some View {
        let compact = horizontalSizeClass == .compact
        let layout = compact ? AnyLayout(VStackLayout(spacing: 14)) : AnyLayout(HStackLayout(alignment: .bottom, spacing: 24))
        layout {
            ArtworkImage(path: album?.artUrl, size: compact ? 240 : 220, corner: 12)
                .shadow(color: .black.opacity(0.25), radius: 14, y: 8)
            VStack(alignment: compact ? .center : .leading, spacing: 6) {
                Text(album?.title ?? "").font(.title2.bold()).multilineTextAlignment(compact ? .center : .leading)
                if let artistId = album?.artistId, let artist = album?.artist {
                    // A button, not a NavigationLink: inside a List row a link takes the whole row's
                    // width and a disclosure chevron, which pulled the name off-centre.
                    Button {
                        AudioRouter.shared.open(.artist(artistId))
                    } label: {
                        Text(artist).font(.title3).foregroundStyle(SentinelTheme.green)
                    }
                    .buttonStyle(.borderless)
                } else if let artist = album?.artist {
                    Text(artist).font(.title3).foregroundStyle(.secondary)
                }
                Text(metaLine).font(.caption).foregroundStyle(.secondary)
                HStack(spacing: 14) {
                    if let album {
                        Button {
                            MusicState.shared.toggleLove(album: album)
                        } label: {
                            Image(systemName: MusicState.shared.isLoved(album: album) ? "heart.fill" : "heart")
                                .foregroundStyle(.pink)
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel(MusicState.shared.isLoved(album: album) ? "Unlove album" : "Love album")
                        StarRatingControl(rating: rating) { value in
                            rating = value
                            MusicState.shared.rate(album: album, value)
                        }
                    }
                }
                PlayShuffleButtons(
                    play: { AudioPlayer.shared.play(tracks: tracks, context: context) },
                    shuffle: { AudioPlayer.shared.play(tracks: tracks, context: context, shuffle: true) })
                    .disabled(tracks.isEmpty)
                    .frame(maxWidth: 360)
                    .padding(.top, 4)
            }
            .frame(maxWidth: compact ? .infinity : nil, alignment: compact ? .center : .leading)
        }
        .frame(maxWidth: .infinity)
    }

    private var metaLine: String {
        var parts: [String] = []
        if let genre = album?.genres?.first { parts.append(genre.uppercased()) }
        if let year = album?.year { parts.append(String(year)) }
        if let format = detail?.formats?.first { parts.append(format) }
        else if tracks.contains(where: \.isLossless) { parts.append("Lossless") }
        return parts.joined(separator: " · ")
    }

    private func footer(_ detail: MusicAlbumDetail) -> some View {
        let total = detail.tracks.reduce(0) { $0 + $1.duration }
        var parts = ["\(detail.tracks.count) song\(detail.tracks.count == 1 ? "" : "s")", AudioFormat.duration(total, style: .long)]
        if let bytes = detail.totalBytes { parts.append(AudioFormat.bytes(bytes)) }
        return VStack(alignment: .leading, spacing: 4) {
            Text(parts.joined(separator: " · "))
            if let formats = detail.formats, formats.count > 1 { Text(formats.joined(separator: ", ")) }
            if let added = AudioFormat.relative(detail.album.addedAt) { Text("Added \(added)") }
        }
        .font(.caption)
        .foregroundStyle(.secondary)
        .padding(.top, 6)
    }

    private func load() async {
        do {
            let loaded = try await AppModel.shared.client.musicAlbum(id: albumId)
            detail = loaded
            rating = loaded.album.rating ?? 0
            error = nil
        } catch {
            if !error.isCancellation { self.error = error.localizedDescription }
        }
    }
}

// MARK: - Artist

struct MusicArtistView: View {
    let artistId: String
    var initial: MusicArtist? = nil
    @State private var detail: MusicArtistDetail?
    @State private var error: String?

    private var artist: MusicArtist? { detail?.artist ?? initial }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 26) {
                header
                if let top = detail?.topTracks, !top.isEmpty {
                    VStack(alignment: .leading, spacing: 6) {
                        HStack {
                            Text("Top Songs").font(.title3.bold())
                            Spacer()
                            NavigationLink("See All") {
                                ArtistSongsView(artist: artist)
                            }
                        }
                        ForEach(Array(top.prefix(5).enumerated()), id: \.element.id) { offset, track in
                            TrackRow(track: track) {
                                AudioPlayer.shared.play(tracks: top, startAt: offset, context: AudioPlayContext(type: "artist", id: artistId, name: artist?.name))
                            }
                            Divider()
                        }
                    }
                    .padding(.horizontal)
                }
                if let albums = detail?.albums, !albums.isEmpty {
                    AudioShelf(title: "Albums") { ForEach(albums) { AlbumTile(album: $0) } }
                }
                if let albums = detail?.appearsOn, !albums.isEmpty {
                    AudioShelf(title: "Appears On") { ForEach(albums) { AlbumTile(album: $0) } }
                }
                if let similar = detail?.similar, !similar.isEmpty {
                    AudioShelf(title: "Similar Artists") { ForEach(similar) { ArtistTile(artist: $0) } }
                }
                if let error { Text(error).foregroundStyle(SentinelTheme.red).padding() }
            }
            .padding(.vertical)
        }
        .navigationTitle(artist?.name ?? "Artist")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .task {
            do { detail = try await AppModel.shared.client.musicArtist(id: artistId) }
            catch { if !error.isCancellation { self.error = error.localizedDescription } }
        }
    }

    private var header: some View {
        VStack(spacing: 10) {
            ArtworkImage(path: artist?.artUrl, size: 180, placeholder: "music.mic", circle: true)
                .shadow(color: .black.opacity(0.25), radius: 12, y: 6)
            HStack(spacing: 8) {
                Text(artist?.name ?? "").font(.largeTitle.bold()).multilineTextAlignment(.center)
                if let artist {
                    Button {
                        MusicState.shared.toggleLove(artist: artist)
                    } label: {
                        Image(systemName: MusicState.shared.isLoved(artist: artist) ? "heart.fill" : "heart").foregroundStyle(.pink)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("Love artist")
                }
            }
            if let artist {
                Text([artist.genres?.prefix(3).joined(separator: ", "), "\(artist.albumCount ?? 0) albums", "\(artist.trackCount ?? 0) songs"]
                    .compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: " · "))
                    .font(.caption).foregroundStyle(.secondary)
            }
            HStack(spacing: 12) {
                Button {
                    playAll(shuffle: false)
                } label: {
                    Label("Play", systemImage: "play.fill").frame(maxWidth: .infinity)
                }
                Button {
                    playAll(shuffle: true)
                } label: {
                    Label("Shuffle", systemImage: "shuffle").frame(maxWidth: .infinity)
                }
                Button {
                    MusicActions.startRadio(seedType: "artist", seedId: artistId, name: artist?.name ?? "Artist")
                } label: {
                    Label("Radio", systemImage: "dot.radiowaves.left.and.right").frame(maxWidth: .infinity)
                }
            }
            .buttonStyle(.bordered)
            .controlSize(.large)
            .tint(SentinelTheme.green)
            .frame(maxWidth: 520)
        }
        .frame(maxWidth: .infinity)
        .padding(.horizontal)
    }

    private func playAll(shuffle: Bool) {
        Task {
            guard let page = try? await AppModel.shared.client.musicTracks(artistId: artistId, sort: shuffle ? "title" : "album", order: "asc", limit: 1000) else { return }
            AudioPlayer.shared.play(tracks: page.items, context: AudioPlayContext(type: "artist", id: artistId, name: artist?.name), shuffle: shuffle)
        }
    }
}

private struct ArtistSongsView: View {
    let artist: MusicArtist?
    @State private var tracks: [MusicTrack] = []

    var body: some View {
        TrackListScreen(title: artist?.name ?? "Songs", tracks: tracks,
                        context: AudioPlayContext(type: "artist", id: artist?.id, name: artist?.name))
            .task {
                guard let id = artist?.id else { return }
                tracks = (try? await AppModel.shared.client.musicTracks(artistId: id, sort: "plays", order: "desc", limit: 500).items) ?? []
            }
    }
}

// MARK: - Playlist

struct MusicPlaylistView: View {
    let playlistId: String
    var initial: MusicPlaylist? = nil
    @Environment(\.dismiss) private var dismiss
    @State private var detail: MusicPlaylistDetail?
    @State private var error: String?
    @State private var editing = false
    @State private var confirmingDelete = false
    #if os(iOS)
    @State private var editMode: EditMode = .inactive
    #endif

    private var playlist: MusicPlaylist? { detail?.playlist ?? initial }
    private var tracks: [MusicTrack] { detail?.tracks ?? [] }
    private var context: AudioPlayContext { AudioPlayContext(type: "playlist", id: playlistId, name: playlist?.name) }
    private var canEdit: Bool { playlist?.canEditTracks == true }

    var body: some View {
        List {
            header
                .listRowSeparator(.hidden)
            ForEach(Array(tracks.enumerated()), id: \.element.rowKey) { offset, track in
                TrackRow(track: track, onRemove: removeAction(track)) {
                    AudioPlayer.shared.play(tracks: tracks, startAt: offset, context: context)
                }
            }
            .onMove(perform: moveAction)
            .onDelete(perform: deleteAction)
            if let error { Text(error).foregroundStyle(SentinelTheme.red) }
            if detail != nil && tracks.isEmpty {
                Text(playlist?.isSmart == true ? "No songs match these rules yet." : "No songs yet. Use Add to Playlist on any song or album.")
                    .foregroundStyle(.secondary)
            }
        }
        .listStyle(.plain)
        #if os(iOS)
        .environment(\.editMode, $editMode)
        #endif
        .navigationTitle(playlist?.name ?? "Playlist")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItemGroup(placement: .primaryAction) {
                #if os(iOS)
                if canEdit && !tracks.isEmpty {
                    Button(editMode.isEditing ? "Done" : "Reorder") {
                        withAnimation { editMode = editMode.isEditing ? .inactive : .active }
                    }
                }
                #endif
                Menu {
                    if let playlist { menuItems(playlist) }
                } label: {
                    Label("More", systemImage: "ellipsis.circle")
                }
            }
        }
        .sheet(isPresented: $editing) {
            if let playlist {
                PlaylistEditorView(existing: playlist) { _ in Task { await load() } }
            }
        }
        .confirmationDialog("Delete \u{201C}\(playlist?.name ?? "")\u{201D}?", isPresented: $confirmingDelete, titleVisibility: .visible) {
            Button("Delete Playlist", role: .destructive) {
                Task {
                    do {
                        try await AppModel.shared.client.deletePlaylist(id: playlistId)
                        NotificationCenter.default.post(name: .audioLibraryChanged, object: nil)
                        dismiss()
                    } catch { self.error = error.localizedDescription }
                }
            }
        } message: {
            Text("The songs stay in your library.")
        }
        .task { await load() }
        .refreshable { await load() }
    }

    @ViewBuilder
    private func menuItems(_ playlist: MusicPlaylist) -> some View {
        if playlist.isMine {
            Button("Edit Details", systemImage: "pencil") { editing = true }
        }
        Button(playlist.pinned == true ? "Unpin from Home" : "Pin to Home", systemImage: playlist.pinned == true ? "pin.slash" : "pin") {
            Task { await setPinned(playlist.pinned != true) }
        }
        Button("Duplicate", systemImage: "plus.square.on.square") {
            Task { await duplicate() }
        }
        Button("Play Next", systemImage: "text.line.first.and.arrowtriangle.forward") {
            AudioPlayer.shared.playNext(tracks.map(AudioItem.init(track:)))
        }
        Button("Start Radio", systemImage: "dot.radiowaves.left.and.right") {
            MusicActions.startRadio(seedType: "playlist", seedId: playlistId, name: playlist.name)
        }
        Button("Download", systemImage: "arrow.down.circle") { AudioDownloads.shared.save(tracks: tracks) }
        if playlist.isMine {
            Divider()
            Button("Delete Playlist", systemImage: "trash", role: .destructive) { confirmingDelete = true }
        }
    }

    private func setPinned(_ pinned: Bool) async {
        try? await AppModel.shared.client.pinPlaylist(id: playlistId, pinned: pinned)
        await load()
        NotificationCenter.default.post(name: .audioLibraryChanged, object: nil)
    }

    private func duplicate() async {
        guard let copy = try? await AppModel.shared.client.duplicatePlaylist(id: playlistId) else { return }
        AudioToast.shared.show("Made \u{201C}\(copy.name)\u{201D}")
        NotificationCenter.default.post(name: .audioLibraryChanged, object: nil)
        AudioRouter.shared.open(.playlist(copy.id))
    }

    private var header: some View {
        VStack(spacing: 10) {
            MosaicArtwork(paths: playlist?.artUrls ?? [], size: 200, corner: 12, placeholder: playlist?.isSmart == true ? "gearshape.2" : "music.note.list")
                .shadow(color: .black.opacity(0.25), radius: 12, y: 6)
            Text(playlist?.name ?? "").font(.title2.bold()).multilineTextAlignment(.center)
            if let description = playlist?.description, !description.isEmpty {
                Text(description).font(.subheadline).foregroundStyle(.secondary).multilineTextAlignment(.center)
            }
            HStack(spacing: 6) {
                if playlist?.isSmart == true { badge("Smart", "gearshape.2") }
                if playlist?.collaborative == true { badge("Collaborative", "person.2") }
                else if playlist?.shared == true { badge("Shared", "person.2") }
                if let owner = playlist?.ownerName, playlist?.isMine == false { badge(owner, "person") }
            }
            if !tracks.isEmpty {
                Text("\(tracks.count) songs · \(AudioFormat.duration(tracks.reduce(0) { $0 + $1.duration }, style: .long))")
                    .font(.caption).foregroundStyle(.secondary)
            }
            PlayShuffleButtons(
                play: { AudioPlayer.shared.play(tracks: tracks, context: context) },
                shuffle: { AudioPlayer.shared.play(tracks: tracks, context: context, shuffle: true) })
                .disabled(tracks.isEmpty)
                .frame(maxWidth: 360)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 8)
    }

    private func badge(_ text: String, _ icon: String) -> some View {
        Label(text, systemImage: icon)
            .font(.caption2.weight(.semibold))
            .padding(.horizontal, 8)
            .padding(.vertical, 3)
            .background(SentinelTheme.green.opacity(0.15), in: Capsule())
            .foregroundStyle(SentinelTheme.green)
    }

    private func load() async {
        do {
            detail = try await AppModel.shared.client.musicPlaylist(id: playlistId)
            error = nil
        } catch {
            if !error.isCancellation { self.error = error.localizedDescription }
        }
    }

    private func removeAction(_ track: MusicTrack) -> (() -> Void)? {
        guard canEdit else { return nil }
        return { remove(track) }
    }

    private var deleteAction: ((IndexSet) -> Void)? {
        guard canEdit else { return nil }
        return { offsets in offsets.map { tracks[$0] }.forEach(remove) }
    }

    private var moveAction: ((IndexSet, Int) -> Void)? {
        guard canEdit else { return nil }
        return { move(from: $0, to: $1) }
    }

    private func remove(_ track: MusicTrack) {
        guard let entryId = track.entryId else { return }
        detail?.tracks.removeAll { $0.entryId == entryId }
        Task {
            do { try await AppModel.shared.client.removeFromPlaylist(id: playlistId, entryId: entryId) }
            catch { self.error = error.localizedDescription; await load() }
        }
    }

    private func move(from source: IndexSet, to destination: Int) {
        detail?.tracks.move(fromOffsets: source, toOffset: destination)
        let order = tracks.compactMap(\.entryId)
        Task {
            do { try await AppModel.shared.client.reorderPlaylist(id: playlistId, entryIds: order) }
            catch { self.error = error.localizedDescription; await load() }
        }
    }
}
