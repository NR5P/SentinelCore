import SwiftUI

// "Suggested for you": albums by artists like the ones this person listens to, not in the library
// yet (core/docs/MUSIC_AND_PODCASTS.md "Suggestions"). A row on Music Home and a full screen
// ("See All", or More → Suggested for You). Only shown when the server says canDownload.
// Downloading one makes an imported list called "Suggested for you", which then shows under
// Album Downloads → Imported Lists.

// What the row and the screen share, so a download or a "not interested" on one shows on the other.
@MainActor
@Observable
final class MusicSuggestionsStore {
    static let shared = MusicSuggestionsStore()

    private(set) var suggestions: MusicSuggestions?
    private(set) var loadError: String?
    private(set) var loading = false
    // Downloads started here since the last read said so.
    private var queuedHere: Set<String> = []
    // Not-interested ones, hidden at once rather than at the next read.
    private var hidden: Set<String> = []
    private var hiddenArtists: Set<String> = []
    @ObservationIgnored private var server = ""

    static let emptyMessage = "Love some songs or albums, or import your Spotify likes, and suggestions will show up here."

    var albums: [MusicSuggestedAlbum] {
        (suggestions?.albums ?? []).compactMap { album in
            if hidden.contains(album.id) || hiddenArtists.contains(album.displayArtist.lowercased()) { return nil }
            var shown = album
            if queuedHere.contains(album.id) { shown.queued = true }
            return shown
        }
    }

    var isWorking: Bool { suggestions?.isWorking == true }

    // Reads the suggestions, then again every 3 s while the server is still building them.
    func follow(refresh: Bool = false) async {
        await load(refresh: refresh)
        while isWorking, !Task.isCancelled {
            try? await Task.sleep(for: .seconds(3))
            guard !Task.isCancelled else { return }
            await load()
        }
    }

    func load(refresh: Bool = false) async {
        let current = AppModel.shared.activeServerURL
        if current != server {
            server = current
            suggestions = nil
            queuedHere = []
            hidden = []
            hiddenArtists = []
        }
        loading = suggestions == nil
        defer { loading = false }
        do {
            let fresh = try await AppModel.shared.client.musicSuggestions(refresh: refresh)
            suggestions = fresh
            loadError = nil
            // What the server now knows about replaces what was remembered here.
            let freshIds = Set((fresh.albums ?? []).map(\.id))
            queuedHere = queuedHere.intersection(freshIds)
            hidden = hidden.intersection(freshIds)
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            loadError = error.localizedDescription
        }
    }

    // One album for "Download", the ticked ones for "Download all ticked".
    func download(_ albums: [MusicSuggestedAlbum]) async throws {
        let wanted = albums.filter { $0.queued != true }
        guard !wanted.isEmpty else { return }
        _ = try await AppModel.shared.client.startMediaImport(kind: .music, musicSuggestionImport(wanted))
        for album in wanted { queuedHere.insert(album.id) }
        NotificationCenter.default.post(name: .mediaImportsChanged, object: MediaImportKind.music.rawValue)
        let what = wanted.count == 1 ? wanted[0].displayTitle : MediaImportKind.music.count(wanted.count)
        AudioToast.shared.show("Downloading \(what). Watch it under Album Downloads.")
    }

    // "Not interested" in one album, or (`wholeArtist`) in everything by its artist.
    func dismiss(_ album: MusicSuggestedAlbum, wholeArtist: Bool) async throws {
        guard let artist = album.artist, !artist.isEmpty else { return }
        try await AppModel.shared.client.dismissMusicSuggestion(artist: artist, title: wholeArtist ? nil : album.title)
        if wholeArtist { hiddenArtists.insert(artist.lowercased()) } else { hidden.insert(album.id) }
    }
}

// MARK: - Home row

struct MusicSuggestionsShelf: View {
    @EnvironmentObject private var model: AppModel
    @State private var actionError: String?
    private var store: MusicSuggestionsStore { MusicSuggestionsStore.shared }

    var body: some View {
        let albums = store.albums
        Group {
            if !albums.isEmpty || store.isWorking {
                AudioShelf(title: "Suggested for You", subtitle: store.isWorking ? "Finding albums you might like…" : "Albums you don't have yet") {
                    MusicSuggestionsView()
                } content: {
                    if albums.isEmpty {
                        ProgressView().frame(width: 150, height: 150)
                    }
                    ForEach(albums.prefix(16)) { album in
                        MusicSuggestionTile(album: album, onError: { actionError = $0 })
                    }
                }
            }
        }
        // Loaded by MusicHomeView: this row is empty until there is something to show, and an
        // empty view never appears, so a .task here would never run.
        .alert("Suggestions", isPresented: Binding(get: { actionError != nil }, set: { if !$0 { actionError = nil } })) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(actionError ?? "")
        }
    }
}

// Album art from the Cover Art Archive (may 404), or a placeholder.
struct MusicSuggestionCover: View {
    let album: MusicSuggestedAlbum

    var body: some View {
        AsyncImage(url: album.artUrl.flatMap(URL.init(string:))) { phase in
            if let image = phase.image {
                image.resizable().scaledToFill()
            } else {
                ArtworkPlaceholder(seed: album.id, symbol: "opticaldisc")
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: 8))
    }
}

private struct MusicSuggestionTile: View {
    let album: MusicSuggestedAlbum
    let onError: (String) -> Void
    @State private var busy = false
    private var store: MusicSuggestionsStore { MusicSuggestionsStore.shared }

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            MusicSuggestionCover(album: album)
                .frame(width: 150, height: 150)
            Text(album.displayTitle).font(.subheadline).lineLimit(1)
            Text(album.displayArtist).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            if let because = album.becauseLine {
                Text(because).font(.caption2).foregroundStyle(.secondary).lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
            }
            if album.queued == true {
                Label("On its way", systemImage: "checkmark.circle.fill")
                    .font(.caption.weight(.medium))
                    .foregroundStyle(SentinelTheme.green)
            } else {
                Button {
                    Task { await run { try await store.download([album]) } }
                } label: {
                    if busy { ProgressView().controlSize(.small) } else { Label("Download", systemImage: "arrow.down.circle") }
                }
                .font(.caption.weight(.medium))
                .buttonStyle(.borderless)
                .disabled(busy)
            }
        }
        .frame(width: 150, alignment: .leading)
        .contextMenu { MusicSuggestionMenu(album: album, run: { action in Task { await run(action) } }) }
    }

    private func run(_ action: () async throws -> Void) async {
        busy = true
        defer { busy = false }
        do { try await action() } catch {
            if !MediaDownloadsTracker.isCancellation(error) { onError(error.localizedDescription) }
        }
    }
}

// Download / Not Interested / Not Interested in <artist>, for context menus.
private struct MusicSuggestionMenu: View {
    let album: MusicSuggestedAlbum
    let run: (@escaping () async throws -> Void) -> Void
    private var store: MusicSuggestionsStore { MusicSuggestionsStore.shared }

    var body: some View {
        if album.queued != true {
            Button("Download", systemImage: "arrow.down.circle") { run { try await store.download([album]) } }
        }
        Button("Not Interested", systemImage: "hand.thumbsdown") { run { try await store.dismiss(album, wholeArtist: false) } }
        if !album.displayArtist.isEmpty {
            Button("Not Interested in \(album.displayArtist)", systemImage: "person.slash") {
                run { try await store.dismiss(album, wholeArtist: true) }
            }
        }
    }
}

// MARK: - The full screen

struct MusicSuggestionsView: View {
    @EnvironmentObject private var model: AppModel
    @State private var ticked: Set<String> = []
    @State private var busy: Set<String> = []
    @State private var downloadingTicked = false
    @State private var actionError: String?
    @State private var refreshToken = 0
    private var store: MusicSuggestionsStore { MusicSuggestionsStore.shared }

    var body: some View {
        let albums = store.albums
        let tickable = albums.filter { ticked.contains($0.id) && $0.queued != true }
        List {
            Section {
                if let based = store.suggestions?.basedOnLine {
                    Text(based).font(.callout).foregroundStyle(.secondary)
                }
                if store.isWorking || store.loading {
                    HStack(spacing: 10) {
                        ProgressView().controlSize(.small)
                        Text("Finding albums you might like…").foregroundStyle(.secondary)
                    }
                }
                if store.suggestions?.isError == true, let message = store.suggestions?.message, !message.isEmpty {
                    Label(message, systemImage: "exclamationmark.triangle.fill")
                        .font(.callout)
                        .foregroundStyle(.orange)
                }
                if let error = store.loadError {
                    Text(error).font(.callout).foregroundStyle(SentinelTheme.red)
                }
                if store.suggestions != nil, !store.isWorking, albums.isEmpty {
                    Text(MusicSuggestionsStore.emptyMessage).foregroundStyle(.secondary)
                }
            }
            if !albums.isEmpty {
                Section {
                    ForEach(albums) { album in
                        row(album)
                    }
                } header: {
                    Text("\(MediaImportKind.music.count(albums.count))\(ticked.isEmpty ? "" : " · \(tickable.count) ticked")")
                } footer: {
                    Text("Downloading makes an imported list called \u{201C}Suggested for you\u{201D}; it shows under Album Downloads.")
                }
            }
        }
        #if os(macOS)
        .listStyle(.inset)
        #endif
        .navigationTitle("Suggested for You")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItemGroup(placement: .primaryAction) {
                Button {
                    refreshToken += 1
                } label: {
                    Label("Refresh", systemImage: "arrow.clockwise")
                }
                .help("Work the suggestions out again")
                .disabled(store.isWorking)
                Button {
                    Task { await downloadTicked(tickable) }
                } label: {
                    if downloadingTicked {
                        ProgressView().controlSize(.small)
                    } else {
                        Text(tickable.isEmpty ? "Download All Ticked" : "Download \(tickable.count) Ticked")
                    }
                }
                .disabled(tickable.isEmpty || downloadingTicked)
            }
        }
        .refreshable { await store.load(refresh: true) }
        .task(id: "\(model.activeServerURL)|\(refreshToken)") {
            guard !model.activeServerURL.isEmpty else { return }
            await store.follow(refresh: refreshToken > 0)
        }
        .alert("Suggestions", isPresented: Binding(get: { actionError != nil }, set: { if !$0 { actionError = nil } })) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(actionError ?? "")
        }
    }

    private func row(_ album: MusicSuggestedAlbum) -> some View {
        let queued = album.queued == true
        let isTicked = ticked.contains(album.id) && !queued
        return HStack(spacing: 12) {
            Button {
                if isTicked { ticked.remove(album.id) } else { ticked.insert(album.id) }
            } label: {
                Image(systemName: queued ? "checkmark.circle" : isTicked ? "checkmark.circle.fill" : "circle")
                    .font(.title3)
                    .foregroundStyle(isTicked ? AnyShapeStyle(SentinelTheme.green) : AnyShapeStyle(.secondary))
            }
            .buttonStyle(.borderless)
            .disabled(queued)
            .accessibilityLabel(isTicked ? "Untick \(album.displayTitle)" : "Tick \(album.displayTitle)")
            MusicSuggestionCover(album: album)
                .frame(width: 56, height: 56)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(album.displayTitle).lineLimit(1)
                    if let year = album.year {
                        Text(String(year)).font(.caption).foregroundStyle(.secondary)
                    }
                }
                Text(album.displayArtist).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                if let because = album.becauseLine {
                    Text(because).font(.caption2).foregroundStyle(.secondary).lineLimit(2)
                }
            }
            Spacer(minLength: 8)
            if queued {
                Label("On its way", systemImage: "checkmark.circle.fill")
                    .font(.caption.weight(.medium))
                    .foregroundStyle(SentinelTheme.green)
            } else if busy.contains(album.id) {
                ProgressView().controlSize(.small)
            } else {
                Button("Download") { Task { await run(album) { try await store.download([album]) } } }
                    .buttonStyle(.bordered)
                    .controlSize(.small)
            }
        }
        .padding(.vertical, 2)
        .contextMenu {
            MusicSuggestionMenu(album: album, run: { action in Task { await run(album, action) } })
        }
        #if os(iOS)
        .swipeActions(edge: .trailing) {
            Button {
                Task { await run(album) { try await store.dismiss(album, wholeArtist: false) } }
            } label: {
                Label("Not Interested", systemImage: "hand.thumbsdown")
            }
            .tint(.gray)
            if !album.displayArtist.isEmpty {
                Button {
                    Task { await run(album) { try await store.dismiss(album, wholeArtist: true) } }
                } label: {
                    Label("Not Interested in \(album.displayArtist)", systemImage: "person.slash")
                }
                .tint(.orange)
            }
        }
        .swipeActions(edge: .leading) {
            if !queued {
                Button {
                    Task { await run(album) { try await store.download([album]) } }
                } label: {
                    Label("Download", systemImage: "arrow.down.circle")
                }
                .tint(SentinelTheme.green)
            }
        }
        #endif
    }

    private func run(_ album: MusicSuggestedAlbum, _ action: () async throws -> Void) async {
        busy.insert(album.id)
        defer { busy.remove(album.id) }
        do {
            try await action()
            ticked.remove(album.id)
        } catch {
            if !MediaDownloadsTracker.isCancellation(error) { actionError = error.localizedDescription }
        }
    }

    private func downloadTicked(_ albums: [MusicSuggestedAlbum]) async {
        guard !albums.isEmpty else { return }
        downloadingTicked = true
        defer { downloadingTicked = false }
        do {
            try await store.download(albums)
            ticked.subtract(albums.map(\.id))
        } catch {
            if !MediaDownloadsTracker.isCancellation(error) { actionError = error.localizedDescription }
        }
    }
}
