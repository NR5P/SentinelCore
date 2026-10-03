import SwiftUI

// "+ Add" on the Movies tab: find a movie or show on TMDB, pick a season or episode, pick one of
// the releases the household's sites offer, and the server downloads it through the VPN and files
// it into the library. The dashboard's version is openAdd/drawAdd in wwwroot/movies.js; the
// contract is core/docs/MOVIE_DOWNLOADS.md.

extension View {
    // The Add sheet. `onStarted` runs after a download has been started and the sheet has closed.
    func mediaAddSheet(
        isPresented: Binding<Bool>, downloads: MediaDownloadsTracker, onStarted: @escaping (MediaDownload) -> Void
    ) -> some View {
        modifier(MediaAddSheetModifier(isPresented: isPresented, downloads: downloads, onStarted: onStarted))
    }

    // The Add sheet opened at a starting point - what a library search's offer does.
    func mediaAddSheet(
        start: Binding<MediaAddStart?>, downloads: MediaDownloadsTracker, onStarted: @escaping (MediaDownload) -> Void
    ) -> some View {
        modifier(MediaAddStartSheetModifier(start: start, downloads: downloads, onStarted: onStarted))
    }
}

// Where "+ Add" opens when a library search that found nothing offers a download: already at a
// picked title, or at a site search for exactly what was typed (MOVIE_DOWNLOADS.md, "Search offers
// a download").
struct MediaAddStart: Identifiable {
    let kind: String
    let title: String
    var pick: MediaDownloadLookupResult?
    let id = UUID()
}

private struct MediaAddSheetModifier: ViewModifier {
    @Binding var isPresented: Bool
    let downloads: MediaDownloadsTracker
    let onStarted: (MediaDownload) -> Void
    @EnvironmentObject private var model: AppModel

    func body(content: Content) -> some View {
        content.sheet(isPresented: $isPresented) {
            MediaAddView(status: downloads.status, vpnDisabled: downloads.vpnDisabled) { download in
                downloads.noteStarted(download)
                isPresented = false
                onStarted(download)
            }
            .environmentObject(model)
            #if os(macOS)
            .frame(minWidth: 560, minHeight: 620)
            #endif
        }
    }
}

private struct MediaAddStartSheetModifier: ViewModifier {
    @Binding var start: MediaAddStart?
    let downloads: MediaDownloadsTracker
    let onStarted: (MediaDownload) -> Void
    @EnvironmentObject private var model: AppModel

    func body(content: Content) -> some View {
        content.sheet(item: $start) { start in
            MediaAddView(status: downloads.status, vpnDisabled: downloads.vpnDisabled, start: start) { download in
                downloads.noteStarted(download)
                self.start = nil
                onStarted(download)
            }
            .environmentObject(model)
            #if os(macOS)
            .frame(minWidth: 560, minHeight: 620)
            #endif
        }
    }
}

private enum MediaAddRoute: Hashable {
    case show(MediaDownloadLookupResult)
    case releases(MediaDownloadSearchInput)
}

struct MediaAddView: View {
    let status: MediaDownloadsStatus?
    // Use VPN is off in Settings: say so before anyone searches.
    var vpnDisabled = false
    var start: MediaAddStart?
    let onStarted: (MediaDownload) -> Void
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var path: [MediaAddRoute] = []
    @State private var startApplied = false
    @State private var kind = "movie"
    @State private var query = ""
    // The text the results are for, so switching Movie / TV show searches again.
    @State private var searchedQuery = ""
    @State private var results: [MediaDownloadLookupResult]?
    @State private var tmdbConfigured = true
    @State private var searching = false
    @State private var errorMessage: String?
    @State private var directSeason = ""
    @State private var directEpisode = ""

    private var tmdbMissing: Bool { status?.tmdbConfigured == false || !tmdbConfigured }
    private var trimmed: String { query.trimmingCharacters(in: .whitespacesAndNewlines) }

    var body: some View {
        NavigationStack(path: $path) {
            Form {
                if vpnDisabled {
                    Section {
                        Label("The VPN is turned off in Settings, so the sites and everyone sharing will see your home address.",
                              systemImage: "exclamationmark.shield.fill")
                            .font(.callout)
                            .foregroundStyle(SentinelTheme.amber)
                    }
                }
                Section {
                    Picker("Kind", selection: $kind) {
                        Text("Movie").tag("movie")
                        Text("TV show").tag("show")
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                    HStack(spacing: 8) {
                        TextField("", text: $query, prompt: Text(kind == "movie" ? "Movie title" : "Show title"))
                            .labelsHidden()
                            .textFieldStyle(.roundedBorder)
                            .autocorrectionDisabled()
                            .onSubmit { Task { await lookup() } }
                            #if os(iOS)
                            .submitLabel(.search)
                            #endif
                        Button {
                            Task { await lookup() }
                        } label: {
                            Label("Find", systemImage: "magnifyingglass")
                        }
                        .buttonStyle(.bordered)
                        .disabled(trimmed.isEmpty || searching)
                    }
                } footer: {
                    if let warning = noLibraryWarning {
                        Text(warning).foregroundStyle(.orange)
                    }
                }

                if tmdbMissing, !trimmed.isEmpty {
                    directSection
                }

                if searching {
                    Section {
                        HStack(spacing: 10) { ProgressView(); Text("Searching…").foregroundStyle(.secondary) }
                    }
                } else if let errorMessage {
                    Section {
                        Text(errorMessage).foregroundStyle(SentinelTheme.red)
                    }
                } else if let results, !tmdbMissing {
                    Section {
                        if results.isEmpty {
                            Text("Nothing on TMDB called that. Try fewer words.").foregroundStyle(.secondary)
                        }
                        ForEach(results) { result in
                            Button {
                                pick(result)
                            } label: {
                                MediaLookupRow(result: result)
                            }
                            .buttonStyle(.plain)
                        }
                    }
                }
            }
            .formStyle(.grouped)
            .navigationTitle("Add a Movie or Show")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Close") { dismiss() }
                }
            }
            .onChange(of: kind) { _, _ in
                results = nil
                errorMessage = nil
                if !searchedQuery.isEmpty, !trimmed.isEmpty { Task { await lookup(searchSitesWithoutTmdb: false) } }
            }
            .onAppear {
                guard let start, !startApplied else { return }
                startApplied = true
                kind = start.kind
                query = start.title
                if let picked = start.pick {
                    pick(picked)
                } else {
                    path = [.releases(MediaDownloadSearchInput(kind: start.kind, title: start.title))]
                }
            }
            .onChange(of: path) { _, newPath in
                // Back from where a search offer opened the sheet: Find has never looked the
                // typed text up, so it does now.
                if newPath.isEmpty, results == nil, !searching, !trimmed.isEmpty {
                    Task { await lookup(searchSitesWithoutTmdb: false) }
                }
            }
            .navigationDestination(for: MediaAddRoute.self) { route in
                switch route {
                case .show(let result):
                    MediaAddShowView(result: result) { path.append(.releases($0)) }
                case .releases(let input):
                    MediaReleaseSearchView(input: input, onStarted: onStarted)
                }
            }
        }
    }

    private var noLibraryWarning: String? {
        guard let status else { return nil }
        if kind == "movie", status.movieLibrary == false {
            return "There is no movie library on the server's own disks to save into yet. Add one under Settings → Movies & TV Libraries first."
        }
        if kind == "show", status.showLibrary == false {
            return "There is no TV library on the server's own disks to save into yet. Add one (kind: TV Shows) under Settings → Movies & TV Libraries first."
        }
        return nil
    }

    // No TMDB key: no posters or episode lists, so the sites are searched for exactly what was typed.
    private var directSection: some View {
        Section {
            if kind == "show" {
                HStack(spacing: 8) {
                    TextField("", text: $directSeason, prompt: Text("Season"))
                        .labelsHidden()
                        .textFieldStyle(.roundedBorder)
                        #if os(iOS)
                        .keyboardType(.numberPad)
                        #endif
                    TextField("", text: $directEpisode, prompt: Text("Episode (blank = whole season)"))
                        .labelsHidden()
                        .textFieldStyle(.roundedBorder)
                        #if os(iOS)
                        .keyboardType(.numberPad)
                        #endif
                }
            }
            Button {
                searchDirectly()
            } label: {
                Label("Search Sites for “\(trimmed)”", systemImage: "magnifyingglass")
            }
        } footer: {
            Text("No TMDB key, so there are no posters or episode lists - the sites are searched for exactly what you type.")
        }
    }

    // Find (and Return in the title box). With no TMDB key there are no posters to pick from, so
    // it searches the sites for the typed text straight away rather than stopping at the "Search
    // Sites for …" button - the dashboard's lookup() -> directSearch(). Switching Movie / TV show
    // re-runs the lookup without that, so flipping the picker never starts a search by itself.
    private func lookup(searchSitesWithoutTmdb: Bool = true) async {
        let q = trimmed
        guard !q.isEmpty else { return }
        searching = true
        errorMessage = nil
        defer { searching = false }
        do {
            let found = try await model.client.mediaDownloadLookup(query: q, kind: kind)
            guard q == trimmed else { return }
            tmdbConfigured = found.tmdbConfigured != false
            results = tmdbConfigured ? (found.results ?? []) : []
            searchedQuery = q
            if !tmdbConfigured, searchSitesWithoutTmdb { searchDirectly() }
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            errorMessage = error.localizedDescription
        }
    }

    private func pick(_ result: MediaDownloadLookupResult) {
        if result.isShow {
            path.append(.show(result))
        } else {
            path.append(.releases(MediaDownloadSearchInput(
                kind: "movie", title: result.displayTitle, year: result.year,
                tmdbId: result.tmdbId, posterPath: result.posterPath)))
        }
    }

    private func searchDirectly() {
        let season = kind == "show" ? Int(directSeason.trimmingCharacters(in: .whitespaces)) : nil
        let episode = season != nil ? Int(directEpisode.trimmingCharacters(in: .whitespaces)) : nil
        path.append(.releases(MediaDownloadSearchInput(kind: kind, title: trimmed, season: season, episode: episode)))
    }
}

struct MediaLookupRow: View {
    let result: MediaDownloadLookupResult
    // The search offer mixes movies and shows, so its rows say which each is.
    var showsKind = false
    @EnvironmentObject private var model: AppModel

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            PosterImage(url: model.client.mediaURL(result.posterUrl), title: result.displayTitle, maxPixel: 200)
                .frame(width: 56, height: 84)
                .clipShape(RoundedRectangle(cornerRadius: 5))
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 6) {
                    Text(result.displayTitle).font(.headline).lineLimit(2)
                    if let year = result.year {
                        Text(String(year)).font(.caption).foregroundStyle(.secondary)
                    }
                    if showsKind, let label = MediaSearchOffer.label(for: result.kind) {
                        MediaChip(text: label)
                    }
                    if result.inLibrary == true {
                        MediaChip(text: "In library", highlighted: true)
                    }
                }
                Text(result.overview?.isEmpty == false ? result.overview! : "No description.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(3)
            }
            Spacer(minLength: 0)
            Image(systemName: "chevron.right").font(.caption).foregroundStyle(.tertiary)
        }
        .contentShape(Rectangle())
    }
}

// MARK: - Search offers a download

// A library search that found nothing offers to download what was typed instead
// (MOVIE_DOWNLOADS.md, "Search offers a download"). The dashboard's version is showOffer in movies.js.
struct MediaSearchOffer: View {
    let query: String
    let tmdbConfigured: Bool
    let start: (MediaAddStart) -> Void
    @EnvironmentObject private var model: AppModel
    @State private var results: [MediaDownloadLookupResult]?
    @State private var tmdbAnswered = true
    @State private var errorMessage: String?

    // Music gets a row here when it can be downloaded.
    static let kinds: [(kind: String, label: String)] = [("movie", "Movie"), ("show", "TV show")]
    private static let maxResults = 6

    static func label(for kind: String?) -> String? { kinds.first { $0.kind == kind }?.label }

    var body: some View {
        VStack(spacing: 14) {
            VStack(spacing: 4) {
                Text("Not in your library").font(.title3.weight(.semibold))
                Text("Want to download “\(query)”?").foregroundStyle(.secondary)
            }
            .multilineTextAlignment(.center)
            if tmdbConfigured, tmdbAnswered {
                found
            }
            VStack(spacing: 8) {
                Text("Search the sites for “\(query)” as a")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                HStack(spacing: 10) {
                    ForEach(Self.kinds, id: \.kind) { entry in
                        Button(entry.label) { start(MediaAddStart(kind: entry.kind, title: query)) }
                            .buttonStyle(.bordered)
                    }
                }
            }
            .padding(.top, 4)
        }
        .frame(maxWidth: 560)
        .frame(maxWidth: .infinity)
        .padding(.vertical, 24)
        .task(id: query) { await lookup() }
    }

    @ViewBuilder
    private var found: some View {
        if let errorMessage {
            Text(errorMessage).font(.caption).foregroundStyle(SentinelTheme.red).multilineTextAlignment(.center)
        } else if let results {
            if results.isEmpty {
                Text("Nothing on TMDB called “\(query)”.").foregroundStyle(.secondary)
            }
            VStack(spacing: 8) {
                ForEach(results) { result in
                    Group {
                        if result.inLibrary == true, let itemId = result.itemId {
                            NavigationLink {
                                MovieDetailView(route: MediaRoute(id: itemId, title: result.displayTitle))
                            } label: {
                                MediaLookupRow(result: result, showsKind: true)
                            }
                        } else {
                            Button {
                                start(MediaAddStart(kind: result.kind ?? "movie", title: query, pick: result))
                            } label: {
                                MediaLookupRow(result: result, showsKind: true)
                            }
                        }
                    }
                    .buttonStyle(.plain)
                    .padding(10)
                    .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 10))
                }
            }
        } else {
            HStack(spacing: 10) { ProgressView(); Text("Looking it up…").foregroundStyle(.secondary) }
        }
    }

    private func lookup() async {
        guard tmdbConfigured else { return }
        results = nil
        errorMessage = nil
        do {
            let found = try await model.client.mediaDownloadLookup(query: query, kind: "any")
            tmdbAnswered = found.tmdbConfigured != false
            results = Array((found.results ?? []).filter { Self.label(for: $0.kind) != nil }.prefix(Self.maxResults))
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            errorMessage = error.localizedDescription
        }
    }
}

// MARK: - A show's seasons and episodes

private struct MediaAddShowView: View {
    let result: MediaDownloadLookupResult
    let search: (MediaDownloadSearchInput) -> Void
    @EnvironmentObject private var model: AppModel
    @State private var show: MediaDownloadShow?
    @State private var seasonNumber: Int?
    @State private var errorMessage: String?

    private var seasons: [MediaDownloadShowSeason] { show?.seasons ?? [] }
    private var season: MediaDownloadShowSeason? { seasons.first { $0.number == seasonNumber } }

    var body: some View {
        Form {
            if let errorMessage {
                Section {
                    Text(errorMessage).foregroundStyle(SentinelTheme.red)
                }
            } else if show == nil {
                Section {
                    HStack(spacing: 10) { ProgressView(); Text("Loading seasons…").foregroundStyle(.secondary) }
                }
            } else if seasons.isEmpty {
                Section {
                    Text("TMDB lists no seasons for this show.").foregroundStyle(.secondary)
                }
            } else {
                Section {
                    Picker("Season", selection: $seasonNumber) {
                        ForEach(seasons) { season in
                            Text(season.isComplete ? "\(season.title) ✓" : season.title).tag(season.number)
                        }
                    }
                    if let season {
                        HStack {
                            Text(seasonSummary(season))
                                .font(.callout)
                                .foregroundStyle(.secondary)
                            Spacer()
                            Button("Get Whole Season") { search(input(episode: nil)) }
                                .buttonStyle(.borderedProminent)
                        }
                    }
                }
                if let season {
                    Section("Episodes") {
                        ForEach(season.episodes ?? []) { episode in
                            episodeRow(episode)
                        }
                    }
                }
            }
        }
        .formStyle(.grouped)
        .navigationTitle(result.displayTitle)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .task {
            guard show == nil, let tmdbId = result.tmdbId else {
                if result.tmdbId == nil { errorMessage = "TMDB does not know that show." }
                return
            }
            do {
                let loaded = try await model.client.mediaDownloadShow(tmdbId: tmdbId)
                show = loaded
                seasonNumber = loaded.firstSeasonWithGap
            } catch {
                if MediaDownloadsTracker.isCancellation(error) { return }
                errorMessage = error.localizedDescription
            }
        }
    }

    private func episodeRow(_ episode: MediaDownloadShowEpisode) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Text(episode.number.map(String.init) ?? "–")
                .font(.callout.monospacedDigit())
                .foregroundStyle(.secondary)
                .frame(minWidth: 24, alignment: .trailing)
            VStack(alignment: .leading, spacing: 2) {
                Text(episode.name?.isEmpty == false ? episode.name! : "Episode \(episode.number ?? 0)")
                    .foregroundStyle(episode.inLibrary == true ? .secondary : .primary)
                let facts = [
                    episode.airDate.flatMap(airDateLabel),
                    episode.inLibrary == true ? "in library" : nil,
                    episode.isOut ? nil : "not out yet",
                ].compactMap { $0 }
                if !facts.isEmpty {
                    Text(facts.joined(separator: " · ")).font(.caption).foregroundStyle(.secondary)
                }
            }
            Spacer(minLength: 8)
            Button("Get") { search(input(episode: episode.number)) }
                .buttonStyle(.bordered)
                .disabled(!episode.isOut || episode.number == nil)
        }
    }

    private func seasonSummary(_ season: MediaDownloadShowSeason) -> String {
        let count = season.episodeCount ?? season.episodes?.count ?? 0
        var text = count == 1 ? "1 episode" : "\(count) episodes"
        if let owned = season.ownedCount, owned > 0 { text += " · \(owned) in library" }
        return text
    }

    private func input(episode: Int?) -> MediaDownloadSearchInput {
        MediaDownloadSearchInput(
            kind: "show", title: show?.title ?? result.displayTitle, year: show?.year ?? result.year,
            tmdbId: show?.tmdbId ?? result.tmdbId, posterPath: show?.posterPath ?? result.posterPath,
            season: seasonNumber, episode: episode)
    }

    // "2018-10-01" → "Oct 1, 2018". A bare calendar date, read in UTC so it never shifts a day.
    private func airDateLabel(_ value: String) -> String? {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(identifier: "UTC")
        formatter.dateFormat = "yyyy-MM-dd"
        guard let date = formatter.date(from: value) else { return value }
        var style = Date.FormatStyle(date: .abbreviated, time: .omitted)
        style.timeZone = TimeZone(identifier: "UTC")!
        return date.formatted(style)
    }
}

// MARK: - Releases

// Asks the server to search the sites, follows the search every second, then lists what it found.
private struct MediaReleaseSearchView: View {
    let input: MediaDownloadSearchInput
    let onStarted: (MediaDownload) -> Void
    @EnvironmentObject private var model: AppModel
    @State private var search: MediaDownloadSearch?
    @State private var failure: String?
    @State private var attempt = 0
    @State private var showOthers = false
    @State private var grabbing: String?
    @State private var grabError: String?
    @State private var alreadyHave: MediaReplaceQuestion?

    var body: some View {
        List {
            Section {
                Text(input.headline).font(.headline)
            }
            if let failure {
                Section {
                    Text(failure).foregroundStyle(SentinelTheme.red)
                    Button {
                        attempt += 1
                    } label: {
                        Label("Try Again", systemImage: "arrow.clockwise")
                    }
                }
            } else if let search, search.phase == "done" {
                results(search)
            } else {
                Section {
                    VStack(spacing: 10) {
                        ProgressView()
                        Text(search?.message ?? "Connecting to the VPN...")
                            .foregroundStyle(.secondary)
                        Text(search?.phase == "searching"
                             ? "Asking each of your sites."
                             : "The first search after a while starts the VPN; it takes a few seconds.")
                            .font(.caption)
                            .foregroundStyle(.tertiary)
                            .multilineTextAlignment(.center)
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 16)
                }
            }
        }
        #if os(macOS)
        .listStyle(.inset)
        #endif
        .navigationTitle("Pick a Download")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .task(id: attempt) { await run() }
        .alert("Couldn't Start the Download", isPresented: Binding(get: { grabError != nil }, set: { if !$0 { grabError = nil } })) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(grabError ?? "")
        }
        .alert(alreadyHave?.duplicate.errorDescription ?? "", isPresented: Binding(get: { alreadyHave != nil }, set: { if !$0 { alreadyHave = nil } }), presenting: alreadyHave) { question in
            if question.duplicate.canReplace {
                Button("Replace It", role: .destructive) { Task { await grab(question.release, replace: true) } }
            }
            Button("Keep Both") { Task { await grab(question.release, replace: false) } }
            Button("Cancel", role: .cancel) {}
        } message: { question in
            Text(question.message)
        }
    }

    @ViewBuilder
    private func results(_ search: MediaDownloadSearch) -> some View {
        let best = search.results ?? []
        let others = search.others ?? []
        Section {
            if best.isEmpty {
                Text(others.isEmpty
                     ? (search.message ?? "Nothing found on your sites.")
                     : "None of the releases found are clearly this title with people sharing it.")
                    .foregroundStyle(.secondary)
            }
            ForEach(Array(best.enumerated()), id: \.offset) { index, release in
                releaseRow(release, best: index == 0)
            }
        }
        if !others.isEmpty {
            Section {
                DisclosureGroup(isExpanded: $showOthers) {
                    ForEach(Array(others.enumerated()), id: \.offset) { _, release in
                        releaseRow(release, best: false)
                    }
                } label: {
                    Text(showOthers
                         ? "Hide \(others.count) other result\(others.count == 1 ? "" : "s")"
                         : "Show \(others.count) other result\(others.count == 1 ? "" : "s")")
                }
            } footer: {
                Text("A different title, year or episode, or nobody sharing it right now.")
            }
        }
    }

    private func releaseRow(_ release: MediaRelease, best: Bool) -> some View {
        HStack(alignment: .top, spacing: 10) {
            VStack(alignment: .leading, spacing: 4) {
                let chips = release.chips
                if best || !chips.isEmpty {
                    HStack(spacing: 4) {
                        if best { MediaChip(text: "best match", highlighted: true) }
                        ForEach(chips, id: \.self) { MediaChip(text: $0) }
                    }
                }
                Text(release.title ?? "Untitled release")
                    .font(.callout)
                    .lineLimit(2)
                    .truncationMode(.middle)
                    .textSelection(.enabled)
                Text(release.factsLine)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                if let warning = release.warning, !warning.isEmpty {
                    Text(warning).font(.caption).foregroundStyle(.orange)
                }
            }
            Spacer(minLength: 8)
            if grabbing != nil, grabbing == release.id {
                ProgressView().controlSize(.small)
            } else {
                Group {
                    if best {
                        Button("Download") { Task { await grab(release) } }
                            .buttonStyle(.borderedProminent)
                    } else {
                        Button("Download") { Task { await grab(release) } }
                            .buttonStyle(.bordered)
                    }
                }
                .disabled(grabbing != nil || release.id == nil)
            }
        }
        .padding(.vertical, 4)
        .listRowBackground(best ? SentinelTheme.green.opacity(0.10) : nil)
    }

    private func run() async {
        failure = nil
        search = nil
        showOthers = false
        do {
            var current = try await model.client.startMediaDownloadSearch(input)
            search = current
            while !current.isFinished {
                guard let id = current.id else { throw ClientError.invalidResponse }
                try await Task.sleep(for: .seconds(1))
                current = try await model.client.mediaDownloadSearch(id: id)
                search = current
            }
            if current.phase == "failed" {
                failure = current.message ?? current.error ?? "The search failed."
            }
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            failure = error.localizedDescription
        }
    }

    // `replace` nil the first time; a movie already in the library comes back as a question.
    private func grab(_ release: MediaRelease, replace: Bool? = nil) async {
        guard let searchId = search?.id, let releaseId = release.id else { return }
        grabbing = releaseId
        defer { grabbing = nil }
        do {
            let download = try await model.client.startMediaDownload(searchId: searchId, releaseId: releaseId, replace: replace)
            onStarted(download)
        } catch let duplicate as MediaAlreadyInLibrary {
            alreadyHave = MediaReplaceQuestion(release: release, duplicate: duplicate)
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            grabError = error.localizedDescription
        }
    }
}
