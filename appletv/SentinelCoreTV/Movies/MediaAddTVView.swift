import SwiftUI

// Add a Movie or Show, on the television: find the title on TMDB, pick a season or episode, pick
// one of the releases the household's sites offer, and the server downloads it through the VPN
// and files it into the library. The iPhone's version is ios/SentinelCore/Movies/MediaAddView.swift;
// here every row is one big button (no Download button inside a row - the remote reaches whole
// rows), and a release is confirmed before it starts, since a click on a remote is easy to make.

private enum MediaAddRoute: Hashable {
    case show(MediaDownloadLookupResult)
    case releases(MediaDownloadSearchInput)
}

struct MediaAddTVView: View {
    let status: MediaDownloadsStatus?
    // Use VPN is off in Settings: say so before anyone searches.
    var vpnDisabled = false
    let onStarted: (MediaDownload) -> Void
    @EnvironmentObject private var model: AppModel
    @State private var path: [MediaAddRoute] = []
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
            List {
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
                        Text("TV Show").tag("show")
                    }
                    .pickerStyle(.segmented)
                    TextField(kind == "movie" ? "Movie title" : "Show title", text: $query)
                        .autocorrectionDisabled()
                        .onSubmit { Task { await lookup() } }
                    Button {
                        Task { await lookup() }
                    } label: {
                        Label("Find", systemImage: "magnifyingglass")
                    }
                    .disabled(trimmed.isEmpty || searching)
                } footer: {
                    if let warning = noLibraryWarning {
                        Text(warning).foregroundStyle(SentinelTheme.amber)
                    }
                }

                if tmdbMissing, !trimmed.isEmpty {
                    directSection
                }

                if searching {
                    Section {
                        HStack(spacing: 16) { ProgressView(); Text("Searching…").foregroundStyle(.secondary) }
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
                                MediaLookupRowTV(result: result)
                            }
                        }
                    }
                }
            }
            .buttonStyle(.automatic) // rows, not the app-wide FocusContrastButtonStyle
            .navigationTitle("Add a Movie or Show")
            #if DEBUG
            // Simulator testing without a remote: `-debugMediaAddQuery <title>` types and finds
            // it; add `-debugMediaAddPick 1` to open the first result's releases (or seasons).
            .task {
                let defaults = UserDefaults.standard
                guard let typed = defaults.string(forKey: "debugMediaAddQuery"), query.isEmpty else { return }
                query = typed
                await lookup()
                if defaults.bool(forKey: "debugMediaAddPick"), let first = results?.first { pick(first) }
            }
            #endif
            .onChange(of: kind) { _, _ in
                results = nil
                errorMessage = nil
                if !searchedQuery.isEmpty, !trimmed.isEmpty { Task { await lookup(searchSitesWithoutTmdb: false) } }
            }
            .navigationDestination(for: MediaAddRoute.self) { route in
                switch route {
                case .show(let result):
                    MediaAddShowTVView(result: result) { path.append(.releases($0)) }
                case .releases(let input):
                    MediaReleaseSearchTVView(input: input, onStarted: onStarted)
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
                TextField("Season", text: $directSeason)
                    .keyboardType(.numberPad)
                TextField("Episode (blank = whole season)", text: $directEpisode)
                    .keyboardType(.numberPad)
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

    // Find (and Done on the keyboard). With no TMDB key there are no posters to pick from, so it
    // searches the sites for the typed text straight away - the iPhone's lookup(). Switching
    // Movie / TV Show re-runs the lookup without that, so flipping the picker never starts a search.
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

private struct MediaLookupRowTV: View {
    let result: MediaDownloadLookupResult
    @EnvironmentObject private var model: AppModel

    var body: some View {
        HStack(alignment: .top, spacing: 24) {
            PosterImage(url: model.client.mediaURL(result.posterUrl), title: result.displayTitle, maxPixel: 300)
                .frame(width: 100, height: 150)
                .clipShape(RoundedRectangle(cornerRadius: 8))
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 12) {
                    Text(result.displayTitle).font(.headline).lineLimit(2)
                    if let year = result.year {
                        Text(String(year)).font(.caption).foregroundStyle(.secondary)
                    }
                    if result.inLibrary == true {
                        MediaChipTV(text: "In library", highlighted: true)
                    }
                }
                Text(result.overview?.isEmpty == false ? result.overview! : "No description.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(3)
            }
            Spacer(minLength: 0)
        }
        .padding(.vertical, 8)
    }
}

// MARK: - A show's seasons and episodes

private struct MediaAddShowTVView: View {
    let result: MediaDownloadLookupResult
    let search: (MediaDownloadSearchInput) -> Void
    @EnvironmentObject private var model: AppModel
    @State private var show: MediaDownloadShow?
    @State private var seasonNumber: Int?
    @State private var errorMessage: String?

    private var seasons: [MediaDownloadShowSeason] { show?.seasons ?? [] }
    private var season: MediaDownloadShowSeason? { seasons.first { $0.number == seasonNumber } }

    var body: some View {
        List {
            if let errorMessage {
                Section {
                    Text(errorMessage).foregroundStyle(SentinelTheme.red)
                }
            } else if show == nil {
                Section {
                    HStack(spacing: 16) { ProgressView(); Text("Loading seasons…").foregroundStyle(.secondary) }
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
                        Button {
                            search(input(episode: nil))
                        } label: {
                            LabeledContent {
                                Text(seasonSummary(season))
                            } label: {
                                Label("Get Whole Season", systemImage: "square.stack.3d.down.right")
                            }
                        }
                    }
                }
                if let season {
                    Section("Episodes") {
                        ForEach(season.episodes ?? []) { episode in
                            Button {
                                search(input(episode: episode.number))
                            } label: {
                                episodeRow(episode)
                            }
                            .disabled(!episode.isOut || episode.number == nil)
                        }
                    }
                }
            }
        }
        .buttonStyle(.automatic) // rows, not the app-wide FocusContrastButtonStyle
        .navigationTitle(result.displayTitle)
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
        HStack(alignment: .firstTextBaseline, spacing: 20) {
            Text(episode.number.map(String.init) ?? "–")
                .font(.callout.monospacedDigit())
                .foregroundStyle(.secondary)
                .frame(minWidth: 44, alignment: .trailing)
            VStack(alignment: .leading, spacing: 4) {
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
            Spacer(minLength: 0)
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
private struct MediaReleaseSearchTVView: View {
    let input: MediaDownloadSearchInput
    let onStarted: (MediaDownload) -> Void
    @EnvironmentObject private var model: AppModel
    @State private var search: MediaDownloadSearch?
    @State private var failure: String?
    @State private var attempt = 0
    @State private var showOthers = false
    @State private var confirming: MediaRelease?
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
                    VStack(spacing: 16) {
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
                    .padding(.vertical, 30)
                }
            }
        }
        .buttonStyle(.automatic) // rows, not the app-wide FocusContrastButtonStyle
        .navigationTitle("Pick a Download")
        .task(id: attempt) { await run() }
        .confirmationDialog(
            "Download this?",
            isPresented: Binding(get: { confirming != nil }, set: { if !$0 { confirming = nil } }),
            titleVisibility: .visible,
            presenting: confirming
        ) { release in
            Button("Download") { Task { await grab(release) } }
            Button("Cancel", role: .cancel) {}
        } message: { release in
            Text([release.title, release.factsLine].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: "\n"))
        }
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
                // No DisclosureGroup on tvOS: a button opens and closes the rest.
                Button {
                    showOthers.toggle()
                } label: {
                    Label(showOthers
                          ? "Hide \(others.count) other result\(others.count == 1 ? "" : "s")"
                          : "Show \(others.count) other result\(others.count == 1 ? "" : "s")",
                          systemImage: showOthers ? "chevron.up" : "chevron.down")
                }
                if showOthers {
                    ForEach(Array(others.enumerated()), id: \.offset) { _, release in
                        releaseRow(release, best: false)
                    }
                }
            } footer: {
                Text("A different title, year or episode, or nobody sharing it right now.")
            }
        }
    }

    private func releaseRow(_ release: MediaRelease, best: Bool) -> some View {
        Button {
            confirming = release
        } label: {
            HStack(alignment: .top, spacing: 20) {
                VStack(alignment: .leading, spacing: 8) {
                    let chips = release.chips
                    if best || !chips.isEmpty {
                        HStack(spacing: 8) {
                            if best { MediaChipTV(text: "best match", highlighted: true) }
                            ForEach(chips, id: \.self) { MediaChipTV(text: $0) }
                        }
                    }
                    Text(release.title ?? "Untitled release")
                        .font(.callout)
                        .lineLimit(2)
                        .truncationMode(.middle)
                    Text(release.factsLine)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    if let warning = release.warning, !warning.isEmpty {
                        Text(warning).font(.caption).foregroundStyle(SentinelTheme.amber)
                    }
                }
                Spacer(minLength: 0)
                if grabbing != nil, grabbing == release.id {
                    ProgressView()
                } else {
                    Image(systemName: "arrow.down.circle")
                        .font(.title3)
                        .foregroundStyle(best ? SentinelTheme.green : .secondary)
                }
            }
            .padding(.vertical, 8)
        }
        .disabled(grabbing != nil || release.id == nil)
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
