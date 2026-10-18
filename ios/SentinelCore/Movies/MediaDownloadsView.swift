import SwiftUI

// Movie downloads: the queue the Movies tab's "+ Add" feeds. The server searches the household's
// torrent sites through a VPN, downloads, and files the result into the library, where it shows
// up under Recently Added. Contract: core/docs/MOVIE_DOWNLOADS.md; the dashboard's version is the
// "downloads (+ add)" section of wwwroot/movies.js.

// What the Movies tab knows about downloads: whether the feature is on for this account (status)
// and the queue, kept current while anything is moving or the Downloads screen is open.
@MainActor
final class MediaDownloadsTracker: ObservableObject {
    @Published private(set) var status: MediaDownloadsStatus?
    @Published private(set) var list: MediaDownloadList?
    @Published private(set) var listError: String?
    // Bumped each time a download reaches the library, so Home can reload Recently Added.
    @Published private(set) var landed = 0

    private var states: [String: String] = [:]
    static let pollInterval: Duration = .milliseconds(2500)

    var canDownload: Bool { status?.canDownload == true }
    var canManageSites: Bool { status?.canManageSites == true }
    var active: [MediaDownload] { list?.active ?? [] }
    var recent: [MediaDownload] { list?.recent ?? [] }
    // The VPN and services as last heard - from /status, a queue poll or a VPN test, whichever
    // answered most recently. Drives the light and the Downloads screen's VPN line.
    @Published private(set) var serviceStatus: MediaDownloadServiceStatus?
    var vpnLook: MediaVpnLook { MediaVpnLook(serviceStatus) }
    var vpnDisabled: Bool { serviceStatus?.vpn?.state == "disabled" }

    // A VPN test's answer: show the fresh state straight away.
    func apply(_ service: MediaDownloadServiceStatus?) {
        if let service { serviceStatus = service }
    }

    // Runs while the Movies tab's root is on screen (the banner on Home): reads the status, then
    // follows the queue every 2.5 s while something is downloading. The Downloads screen, which
    // takes the root off screen and so cancels this, runs its own loop.
    func run(client: SentinelClient) async {
        await refreshStatus(client: client)
        guard canDownload else { return }
        while !Task.isCancelled {
            if list == nil || !active.isEmpty {
                await refreshList(client: client)
            }
            try? await Task.sleep(for: Self.pollInterval)
        }
    }

    func refreshStatus(client: SentinelClient) async {
        do {
            let fresh = try await client.mediaDownloadsStatus()
            status = fresh
            if let service = fresh.status { serviceStatus = service }
        } catch {
            if Self.isCancellation(error) { return }
            // 404 is a server from before downloads; anything else hides "+ Add" until next time.
            status = nil
        }
    }

    func refreshList(client: SentinelClient) async {
        do {
            let fresh = try await client.mediaDownloads()
            var didLand = false
            for download in (fresh.active ?? []) + (fresh.recent ?? []) {
                guard let id = download.id, let state = download.state else { continue }
                if let before = states[id], before != state, state == "done" { didLand = true }
                states[id] = state
            }
            list = fresh
            if let service = fresh.status { serviceStatus = service }
            listError = nil
            if didLand { landed += 1 }
        } catch {
            if Self.isCancellation(error) { return }
            listError = error.localizedDescription
        }
    }

    // A download just started from the Add sheet: remember its state so its landing is noticed.
    func noteStarted(_ download: MediaDownload) {
        if let id = download.id, let state = download.state { states[id] = state }
        var current = list ?? MediaDownloadList(status: nil, active: [], recent: [])
        if !(current.active ?? []).contains(where: { $0.id == download.id }) {
            current.active = (current.active ?? []) + [download]
        }
        list = current
    }

    func reset() {
        status = nil
        list = nil
        serviceStatus = nil
        listError = nil
        states = [:]
    }

    static func isCancellation(_ error: Error) -> Bool {
        error is CancellationError || (error as? URLError)?.code == .cancelled
    }
}

// MARK: - Home banner

// "Downloading Bluey S02E05 · 42%" across the top of Movies Home while anything is in the queue.
struct MediaDownloadsBanner: View {
    @ObservedObject var downloads: MediaDownloadsTracker
    let open: () -> Void

    var body: some View {
        let active = downloads.active
        if !active.isEmpty {
            Button(action: open) {
                HStack(spacing: 10) {
                    Image(systemName: "arrow.down.circle.fill")
                        .foregroundStyle(SentinelTheme.green)
                        .symbolEffect(.pulse)
                    // The title gives way on a narrow screen; the percentage never does.
                    if active.count == 1, let first = active.first {
                        Text("Downloading \(first.headline)")
                            .lineLimit(1)
                        Text("· \(Int((first.fraction * 100).rounded()))%")
                            .monospacedDigit()
                            .fixedSize()
                    } else {
                        Text("\(active.count) downloads in progress")
                            .lineLimit(1)
                    }
                    Spacer(minLength: 8)
                    HStack(spacing: 3) {
                        Text("Downloads")
                        Image(systemName: "chevron.right")
                    }
                    .font(.caption.weight(.medium))
                    .foregroundStyle(.secondary)
                }
                .font(.subheadline)
                .padding(.horizontal, 14)
                .padding(.vertical, 10)
                .background(.quaternary.opacity(0.6), in: RoundedRectangle(cornerRadius: 10))
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .padding(.horizontal)
        }
    }
}

// MARK: - Downloads screen

struct MediaDownloadsView: View {
    @ObservedObject var downloads: MediaDownloadsTracker
    @EnvironmentObject private var model: AppModel
    @State private var adding = false
    @State private var importing = false
    @State private var confirmingCancel: MediaDownload?
    @State private var openRoute: MediaRoute?
    @State private var actionError: String?
    @State private var working: Set<String> = []

    var body: some View {
        List {
            if downloads.serviceStatus != nil {
                Section {
                    MediaVpnStatusRow(downloads: downloads)
                    if downloads.canManageSites {
                        NavigationLink {
                            MediaDownloadSitesView()
                        } label: {
                            Label("Sites", systemImage: "globe")
                        }
                    }
                }
            }

            Section {
                if downloads.list == nil, let error = downloads.listError {
                    Text(error).foregroundStyle(SentinelTheme.red)
                } else if downloads.list == nil {
                    HStack { ProgressView(); Text("Loading…").foregroundStyle(.secondary) }
                } else if downloads.active.isEmpty {
                    Text("Nothing downloading. Press Add to find a movie or TV episodes.")
                        .foregroundStyle(.secondary)
                }
                ForEach(downloads.active) { row($0) }
                Button {
                    adding = true
                } label: {
                    Label("Add a Movie or Show", systemImage: "plus")
                }
            } header: {
                Text("Downloading")
            }

            MediaImportListsSection(kind: .movies) { importing = true }

            if !downloads.recent.isEmpty {
                Section("Recent") {
                    ForEach(downloads.recent) { row($0) }
                }
            }
        }
        #if os(macOS)
        .listStyle(.inset)
        #endif
        .navigationTitle("Downloads")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .refreshable { await downloads.refreshList(client: model.client) }
        // Its own loop: pushing this screen takes the Movies tab's root off screen, which cancels
        // the tab's task. Follows the queue every 2.5 s for as long as the screen is showing.
        .task {
            while !Task.isCancelled {
                await downloads.refreshList(client: model.client)
                try? await Task.sleep(for: MediaDownloadsTracker.pollInterval)
            }
        }
        .navigationDestination(item: $openRoute) { route in
            MovieDetailView(route: route)
        }
        .mediaAddSheet(isPresented: $adding, downloads: downloads) { _ in }
        .sheet(isPresented: $importing) {
            MediaImportSheet(kind: .movies) { _ in }
                .environmentObject(model)
        }
        .confirmationDialog(
            "Cancel this download?",
            isPresented: Binding(get: { confirmingCancel != nil }, set: { if !$0 { confirmingCancel = nil } }),
            titleVisibility: .visible,
            presenting: confirmingCancel
        ) { download in
            Button("Cancel Download", role: .destructive) {
                Task { await remove(download) }
            }
            Button("Keep Downloading", role: .cancel) {}
        } message: { _ in
            Text("What has downloaded so far is deleted.")
        }
        .alert("Downloads", isPresented: Binding(get: { actionError != nil }, set: { if !$0 { actionError = nil } })) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(actionError ?? "")
        }
    }

    @ViewBuilder
    private func row(_ download: MediaDownload) -> some View {
        MediaDownloadRow(
            download: download,
            working: working.contains(download.id ?? ""),
            open: download.isDone && download.itemId != nil
                ? { openRoute = MediaRoute(id: download.itemId ?? "", title: download.displayTitle) } : nil,
            retry: download.isFailed ? { Task { await retry(download) } } : nil,
            remove: {
                if download.isActive { confirmingCancel = download } else { Task { await remove(download) } }
            })
        .swipeActions(edge: .trailing) {
            Button(role: .destructive) {
                if download.isActive { confirmingCancel = download } else { Task { await remove(download) } }
            } label: {
                Label(download.isActive ? "Cancel" : "Remove", systemImage: download.isActive ? "xmark" : "trash")
            }
        }
        .contextMenu {
            if download.isDone, download.itemId != nil {
                Button("Open", systemImage: "play.rectangle") {
                    openRoute = MediaRoute(id: download.itemId ?? "", title: download.displayTitle)
                }
            }
            if download.isFailed {
                Button("Retry", systemImage: "arrow.clockwise") { Task { await retry(download) } }
            }
            Button(download.isActive ? "Cancel Download" : "Remove from List", systemImage: download.isActive ? "xmark" : "trash", role: .destructive) {
                if download.isActive { confirmingCancel = download } else { Task { await remove(download) } }
            }
        }
    }

    private func remove(_ download: MediaDownload) async {
        guard let id = download.id else { return }
        working.insert(id)
        defer { working.remove(id) }
        do {
            try await model.client.deleteMediaDownload(id: id)
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            actionError = error.localizedDescription
        }
        await downloads.refreshList(client: model.client)
    }

    private func retry(_ download: MediaDownload) async {
        guard let id = download.id else { return }
        working.insert(id)
        defer { working.remove(id) }
        do {
            _ = try await model.client.retryMediaDownload(id: id)
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            actionError = error.localizedDescription
        }
        await downloads.refreshList(client: model.client)
    }
}

// One download: poster, title, release, progress and what can be done with it.
private struct MediaDownloadRow: View {
    let download: MediaDownload
    let working: Bool
    let open: (() -> Void)?
    let retry: (() -> Void)?
    let remove: () -> Void
    @EnvironmentObject private var model: AppModel

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            PosterImage(url: model.client.mediaURL(download.posterUrl), title: download.displayTitle, maxPixel: 200)
                .frame(width: 52, height: 78)
                .clipShape(RoundedRectangle(cornerRadius: 5))
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 6) {
                    Text(download.displayTitle).font(.headline).lineLimit(1)
                    if let year = download.year {
                        Text(String(year)).font(.caption).foregroundStyle(.secondary)
                    }
                    if let subtitle = download.subtitle, !subtitle.isEmpty {
                        MediaChip(text: subtitle)
                    }
                }
                if let release = download.releaseTitle, !release.isEmpty {
                    Text([release, download.site].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: " · "))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
                if download.isActive {
                    ProgressView(value: download.barFraction)
                        .tint(SentinelTheme.green)
                }
                Text(download.factsLine)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
                if let note = download.error, !note.isEmpty {
                    Text(note)
                        .font(.caption)
                        .foregroundStyle(download.isFailed ? SentinelTheme.red : .orange)
                }
                HStack(spacing: 14) {
                    if let open {
                        Button(action: open) { Label("Open", systemImage: "play.rectangle") }
                    }
                    if let retry {
                        Button(action: retry) { Label("Retry", systemImage: "arrow.clockwise") }
                    }
                    Button(role: download.isActive ? .destructive : nil, action: remove) {
                        if download.isActive {
                            Text("Cancel")
                        } else {
                            Label("Remove", systemImage: "xmark")
                        }
                    }
                    .foregroundStyle(download.isActive ? SentinelTheme.red : .secondary)
                    if working { ProgressView().controlSize(.small) }
                }
                .font(.caption.weight(.medium))
                .buttonStyle(.borderless)
                .disabled(working)
                .padding(.top, 2)
            }
        }
        .padding(.vertical, 4)
    }
}

// A small outlined label: "S02E05", "1080p", "public".
struct MediaChip: View {
    let text: String
    var highlighted = false

    var body: some View {
        Text(text)
            .font(.caption2.weight(.semibold))
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            // Black on green, like the unwatched-count badge on a poster.
            .foregroundStyle(highlighted ? AnyShapeStyle(.black) : AnyShapeStyle(.secondary))
            .background(highlighted ? AnyShapeStyle(SentinelTheme.green) : AnyShapeStyle(.quaternary.opacity(0.5)),
                        in: RoundedRectangle(cornerRadius: 4))
    }
}

// MARK: - The VPN light

extension MediaVpnLook.Tone {
    var color: Color {
        switch self {
        case .on: SentinelTheme.green
        case .busy: SentinelTheme.cyan
        case .idle: .gray
        case .off: SentinelTheme.amber
        case .bad: SentinelTheme.red
        }
    }
}

extension MediaVpnAccountLine.Tone {
    var style: AnyShapeStyle {
        switch self {
        case .plain: AnyShapeStyle(.secondary)
        case .warning: AnyShapeStyle(SentinelTheme.amber)
        case .problem: AnyShapeStyle(SentinelTheme.red)
        }
    }
}

// The coloured dot; it pulses while the VPN is coming up or being checked.
struct MediaVpnDot: View {
    let tone: MediaVpnLook.Tone
    var size: CGFloat = 9

    var body: some View {
        Circle()
            .fill(tone.color)
            .frame(width: size, height: size)
            .phaseAnimator(tone == .busy ? [1.0, 0.3] : [1.0]) { dot, opacity in
                dot.opacity(opacity)
            } animation: { _ in .easeInOut(duration: 0.8) }
            .accessibilityHidden(true)
    }
}

// The small capsule next to "+ Add": "VPN · SE", "VPN idle", "VPN off"... Opens the Downloads
// screen, where the long wording and the Test button are.
struct MediaVpnLight: View {
    @ObservedObject var downloads: MediaDownloadsTracker
    let open: () -> Void

    var body: some View {
        let look = downloads.vpnLook
        Button(action: open) {
            HStack(spacing: 6) {
                MediaVpnDot(tone: look.tone, size: 8)
                Text(look.short)
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.primary)
                    .fixedSize()
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 5)
            .background(look.tone.color.opacity(0.16), in: Capsule())
            .overlay(Capsule().stroke(look.tone.color.opacity(0.45), lineWidth: 1))
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .help(look.text)
        .accessibilityLabel(look.short)
        .accessibilityHint(look.text)
    }
}

// The Downloads screen's VPN line: the dot, the long wording, and Test, which connects now and
// proves the sites see the VPN's address rather than the home one.
struct MediaVpnStatusRow: View {
    @ObservedObject var downloads: MediaDownloadsTracker
    @EnvironmentObject private var model: AppModel
    @State private var testing = false
    @State private var result: (ok: Bool, message: String)?

    var body: some View {
        let look = downloads.vpnLook
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                MediaVpnDot(tone: look.tone)
                VStack(alignment: .leading, spacing: 2) {
                    Text("VPN").font(.subheadline.weight(.semibold))
                    Text(look.text)
                        .font(.subheadline)
                        .foregroundStyle(look.tone == .off || look.tone == .bad ? AnyShapeStyle(look.tone.color) : AnyShapeStyle(.secondary))
                        .textSelection(.enabled)
                    // "Mullvad paid until Oct 27 · 26 days left"; amber when running out, red once out.
                    if let account = MediaVpnAccountLine(downloads.serviceStatus?.vpn?.account) {
                        Text(account.text)
                            .font(.caption)
                            .foregroundStyle(account.tone.style)
                            .textSelection(.enabled)
                    }
                }
                Spacer(minLength: 8)
                Button {
                    Task { await test() }
                } label: {
                    if testing {
                        HStack(spacing: 6) {
                            ProgressView().controlSize(.small)
                            Text("Testing…")
                        }
                    } else {
                        Label("Test", systemImage: "checkmark.shield")
                    }
                }
                .buttonStyle(.bordered)
                .disabled(testing)
                .help("Connect now and prove the sites see the VPN's address, not your home's")
            }
            if let result {
                Label(result.message, systemImage: result.ok ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                    .font(.caption)
                    .foregroundStyle(result.ok ? SentinelTheme.green : SentinelTheme.red)
                    .textSelection(.enabled)
            }
        }
    }

    private func test() async {
        testing = true
        result = nil
        defer { testing = false }
        do {
            let answer = try await model.client.testMediaDownloadsVpn()
            downloads.apply(answer.status)
            let ok = answer.ok == true
            result = (ok, answer.message ?? (ok ? "The VPN works." : "The VPN test failed."))
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            result = (false, error.localizedDescription)
        }
    }
}

// MARK: - Sites (admin)

// Retries `operation` every 3 s for as long as the server says the VPN is still coming up,
// telling `waiting` what to show meanwhile (nil once it answers). Ends with the view's task.
@MainActor
func mediaDownloadsWhileStarting<T>(
    waiting: (String?) -> Void, _ operation: () async throws -> T
) async throws -> T {
    while true {
        do {
            let result = try await operation()
            waiting(nil)
            return result
        } catch let starting as MediaDownloadsStartingError {
            waiting(starting.message)
            try await Task.sleep(for: .seconds(3))
        } catch {
            waiting(nil)
            throw error
        }
    }
}

struct MediaDownloadSitesView: View {
    @EnvironmentObject private var model: AppModel
    @State private var sites: [MediaDownloadSite]?
    @State private var waiting: String? = "Connecting to the VPN..."
    @State private var errorMessage: String?
    @State private var reloadToken = 0
    @State private var showingCatalog = false
    @State private var confirmingRemove: MediaDownloadSite?
    @State private var busy: Set<Int> = []

    var body: some View {
        Form {
            Section {
                Text("The sites searched when anyone adds a movie or show. Prowlarr keeps them working; everything goes through the VPN.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            if let waiting {
                Section {
                    HStack(spacing: 10) {
                        ProgressView()
                        Text(waiting).foregroundStyle(.secondary)
                    }
                }
            }
            if let errorMessage {
                Section {
                    Text(errorMessage).foregroundStyle(SentinelTheme.red)
                }
            }
            if let sites {
                Section("Your Sites") {
                    if sites.isEmpty {
                        Text("No sites yet. Add one to start searching.").foregroundStyle(.secondary)
                    }
                    ForEach(sites) { site in
                        siteRow(site)
                    }
                }
                Section {
                    Button {
                        showingCatalog = true
                    } label: {
                        Label("Add a Site", systemImage: "plus")
                    }
                }
            }
        }
        .formStyle(.grouped)
        .navigationTitle("Torrent Sites")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .task(id: reloadToken) { await load() }
        .navigationDestination(isPresented: $showingCatalog) {
            MediaDownloadSiteCatalogView {
                showingCatalog = false
                reloadToken += 1
            }
        }
        .confirmationDialog(
            "Remove \(confirmingRemove?.displayName ?? "this site")?",
            isPresented: Binding(get: { confirmingRemove != nil }, set: { if !$0 { confirmingRemove = nil } }),
            titleVisibility: .visible,
            presenting: confirmingRemove
        ) { site in
            Button("Remove Site", role: .destructive) {
                Task { await remove(site) }
            }
        } message: { _ in
            Text("It is no longer searched.")
        }
    }

    private func siteRow(_ site: MediaDownloadSite) -> some View {
        HStack(alignment: .top, spacing: 10) {
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 6) {
                    Text(site.displayName)
                    if let privacy = site.privacy, !privacy.isEmpty { MediaChip(text: privacy) }
                }
                if let description = site.description, !description.isEmpty {
                    Text(description).font(.caption).foregroundStyle(.secondary).lineLimit(3)
                }
            }
            Spacer(minLength: 8)
            Toggle("On", isOn: Binding(
                get: { site.enabled == true },
                set: { newValue in Task { await setEnabled(site, newValue) } }))
                .labelsHidden()
                .disabled(site.id.map { busy.contains($0) } ?? true)
            Button {
                confirmingRemove = site
            } label: {
                Image(systemName: "trash")
            }
            .buttonStyle(.borderless)
            .foregroundStyle(SentinelTheme.red)
            .accessibilityLabel("Remove \(site.displayName)")
        }
    }

    private func load() async {
        errorMessage = nil
        if sites == nil { waiting = "Connecting to the VPN..." }
        do {
            sites = try await mediaDownloadsWhileStarting(waiting: { waiting = $0 }) {
                try await model.client.mediaDownloadSites()
            }
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            waiting = nil
            errorMessage = error.localizedDescription
        }
    }

    private func setEnabled(_ site: MediaDownloadSite, _ enabled: Bool) async {
        guard let id = site.id else { return }
        busy.insert(id)
        defer { busy.remove(id) }
        do {
            try await mediaDownloadsWhileStarting(waiting: { waiting = $0 }) {
                try await model.client.setMediaDownloadSiteEnabled(id: id, enabled: enabled)
            }
            if let index = sites?.firstIndex(where: { $0.id == id }) { sites?[index].enabled = enabled }
            errorMessage = nil
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            errorMessage = error.localizedDescription
        }
    }

    private func remove(_ site: MediaDownloadSite) async {
        guard let id = site.id else { return }
        do {
            try await mediaDownloadsWhileStarting(waiting: { waiting = $0 }) {
                try await model.client.deleteMediaDownloadSite(id: id)
            }
            errorMessage = nil
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            errorMessage = error.localizedDescription
        }
        reloadToken += 1
    }
}

// Every site Prowlarr knows, public ones first, with a search box.
private struct MediaDownloadSiteCatalogView: View {
    let onAdded: () -> Void
    @EnvironmentObject private var model: AppModel
    @State private var query = ""
    @State private var catalog: [MediaDownloadSiteDefinition]?
    @State private var waiting: String?
    @State private var errorMessage: String?
    @State private var adding: String?
    @State private var settingUp: MediaDownloadSiteDefinition?

    var body: some View {
        List {
            if let waiting {
                HStack(spacing: 10) {
                    ProgressView()
                    Text(waiting).foregroundStyle(.secondary)
                }
            }
            if let errorMessage {
                Text(errorMessage).foregroundStyle(SentinelTheme.red)
            }
            if let catalog {
                if catalog.isEmpty {
                    Text("No site by that name.").foregroundStyle(.secondary)
                }
                ForEach(catalog) { definition in
                    row(definition)
                }
            }
        }
        #if os(macOS)
        .listStyle(.inset)
        #endif
        .navigationTitle("Add a Site")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .searchable(text: $query, prompt: "Site name")
        .task(id: query) {
            // Let the typing settle before asking.
            if catalog != nil { try? await Task.sleep(for: .milliseconds(400)) }
            guard !Task.isCancelled else { return }
            await load()
        }
        .navigationDestination(item: $settingUp) { definition in
            MediaDownloadSiteFormView(definition: definition, onAdded: onAdded)
        }
    }

    private func row(_ definition: MediaDownloadSiteDefinition) -> some View {
        HStack(alignment: .top, spacing: 10) {
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 6) {
                    Text(definition.displayName)
                    if let privacy = definition.privacy, !privacy.isEmpty { MediaChip(text: privacy) }
                    if let language = definition.language, !language.isEmpty {
                        Text(language).font(.caption2).foregroundStyle(.secondary)
                    }
                }
                if let description = definition.description, !description.isEmpty {
                    Text(description).font(.caption).foregroundStyle(.secondary).lineLimit(3)
                }
            }
            Spacer(minLength: 8)
            if adding == definition.id {
                ProgressView().controlSize(.small)
            } else {
                Button(definition.needsSetup == true ? "Set Up" : "Add") {
                    if definition.needsSetup == true {
                        settingUp = definition
                    } else {
                        Task { await add(definition) }
                    }
                }
                .buttonStyle(.bordered)
                .disabled(adding != nil)
            }
        }
    }

    private func load() async {
        errorMessage = nil
        if catalog == nil { waiting = "Loading the site list…" }
        do {
            let q = query.trimmingCharacters(in: .whitespaces)
            catalog = try await mediaDownloadsWhileStarting(waiting: { waiting = $0 }) {
                try await model.client.mediaDownloadSiteCatalog(query: q)
            }
            waiting = nil
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            waiting = nil
            errorMessage = error.localizedDescription
        }
    }

    private func add(_ definition: MediaDownloadSiteDefinition) async {
        guard let key = definition.definition else { return }
        adding = definition.id
        defer { adding = nil }
        do {
            _ = try await mediaDownloadsWhileStarting(waiting: { waiting = $0 }) {
                try await model.client.addMediaDownloadSite(MediaDownloadSiteInput(definition: key, name: nil, fields: [:]))
            }
            onAdded()
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            errorMessage = error.localizedDescription
        }
    }
}

// A site that needs something typed in first (a username, a cookie...), built from its `fields`.
private struct MediaDownloadSiteFormView: View {
    let definition: MediaDownloadSiteDefinition
    let onAdded: () -> Void
    @EnvironmentObject private var model: AppModel
    @State private var texts: [String: String] = [:]
    @State private var flags: [String: Bool] = [:]
    @State private var saving = false
    @State private var waiting: String?
    @State private var errorMessage: String?

    init(definition: MediaDownloadSiteDefinition, onAdded: @escaping () -> Void) {
        self.definition = definition
        self.onAdded = onAdded
        var texts: [String: String] = [:]
        var flags: [String: Bool] = [:]
        for field in definition.fields ?? [] {
            guard let name = field.name else { continue }
            if field.type == "checkbox" {
                flags[name] = field.value?.boolValue ?? false
            } else if field.type == "select" {
                texts[name] = field.value?.textValue ?? field.options?.first?.value ?? ""
            } else {
                texts[name] = field.value?.textValue ?? ""
            }
        }
        _texts = State(initialValue: texts)
        _flags = State(initialValue: flags)
    }

    private var fields: [MediaDownloadSiteField] { (definition.fields ?? []).filter { $0.name != nil } }

    var body: some View {
        Form {
            if let description = definition.description, !description.isEmpty {
                Section {
                    Text(description).font(.callout).foregroundStyle(.secondary)
                }
            }
            Section {
                ForEach(fields) { field in
                    VStack(alignment: .leading, spacing: 4) {
                        input(for: field)
                        if let help = field.help, !help.isEmpty {
                            Text(help).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
            }
            Section {
                Button {
                    Task { await save() }
                } label: {
                    HStack {
                        Text("Add Site")
                        if saving { Spacer(); ProgressView() }
                    }
                }
                .disabled(saving)
                if let waiting {
                    Text(waiting).foregroundStyle(.secondary)
                }
                if let errorMessage {
                    Text(errorMessage).foregroundStyle(SentinelTheme.red)
                }
            } footer: {
                Text("The server tries the site before saving it.")
            }
        }
        .formStyle(.grouped)
        .navigationTitle(definition.displayName)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
    }

    @ViewBuilder
    private func input(for field: MediaDownloadSiteField) -> some View {
        let name = field.name ?? ""
        switch field.type {
        case "checkbox":
            Toggle(field.displayLabel, isOn: Binding(get: { flags[name] ?? false }, set: { flags[name] = $0 }))
        case "select":
            Picker(field.displayLabel, selection: Binding(get: { texts[name] ?? "" }, set: { texts[name] = $0 })) {
                ForEach(field.options ?? []) { option in
                    Text(option.label).tag(option.value)
                }
            }
        default:
            SentinelTextField(
                label: field.displayLabel,
                prompt: field.type == "number" ? "Number" : SentinelInput.typeVerb,
                text: Binding(get: { texts[name] ?? "" }, set: { texts[name] = $0 }),
                secure: field.type == "password")
                .autocorrectionDisabled()
                #if os(iOS)
                .textInputAutocapitalization(.never)
                .keyboardType(field.type == "number" ? .numberPad : .default)
                #endif
        }
    }

    // The values in the types Prowlarr expects: checkboxes as true/false, numbers and numeric
    // choices as numbers, everything else as text - the dashboard's submitSiteForm.
    private func values() -> [String: MediaDownloadFieldValue] {
        var result: [String: MediaDownloadFieldValue] = [:]
        for field in fields {
            guard let name = field.name else { continue }
            switch field.type {
            case "checkbox":
                result[name] = .bool(flags[name] ?? false)
            case "number":
                let text = (texts[name] ?? "").trimmingCharacters(in: .whitespaces)
                if text.isEmpty { result[name] = .null }
                else if let number = Int(text) { result[name] = .int(number) }
                else if let number = Double(text) { result[name] = .double(number) }
                else { result[name] = .string(text) }
            case "select":
                let text = texts[name] ?? ""
                result[name] = Int(text).map { .int($0) } ?? .string(text)
            default:
                result[name] = .string(texts[name] ?? "")
            }
        }
        return result
    }

    private func save() async {
        guard let key = definition.definition else { return }
        saving = true
        errorMessage = nil
        defer { saving = false }
        let input = MediaDownloadSiteInput(definition: key, name: nil, fields: values())
        do {
            _ = try await mediaDownloadsWhileStarting(waiting: { waiting = $0 }) {
                try await model.client.addMediaDownloadSite(input)
            }
            onAdded()
        } catch {
            if MediaDownloadsTracker.isCancellation(error) { return }
            errorMessage = error.localizedDescription
        }
    }
}
