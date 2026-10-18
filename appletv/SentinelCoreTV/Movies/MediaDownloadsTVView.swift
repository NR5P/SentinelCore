import SwiftUI

// Movie downloads on the television: the Movies tab's Downloads pane. The server searches the
// household's torrent sites through a VPN, downloads, and files the result into the library,
// where it shows up under Recently Added. Contract: core/docs/MOVIE_DOWNLOADS.md; the iPhone's
// version is ios/SentinelCore/Movies/MediaDownloadsView.swift. Managing the sites stays on the
// other apps, where typing is practical.

// What the Movies tab knows about downloads: whether the feature is on for this account (status)
// and the queue, kept current while anything is moving or the Downloads pane is showing.
@MainActor
final class MediaDownloadsTracker: ObservableObject {
    @Published private(set) var status: MediaDownloadsStatus?
    @Published private(set) var list: MediaDownloadList?
    @Published private(set) var listError: String?
    // Bumped each time a download reaches the library, so Home can reload Recently Added.
    @Published private(set) var landed = 0
    // The VPN and services as last heard - from /status, a queue poll or a VPN test, whichever
    // answered most recently. Drives the Downloads pane's VPN line.
    @Published private(set) var serviceStatus: MediaDownloadServiceStatus?

    private var states: [String: String] = [:]
    static let pollInterval: Duration = .milliseconds(2500)

    var canDownload: Bool { status?.canDownload == true }
    var active: [MediaDownload] { list?.active ?? [] }
    var recent: [MediaDownload] { list?.recent ?? [] }
    var vpnLook: MediaVpnLook { MediaVpnLook(serviceStatus) }
    var vpnDisabled: Bool { serviceStatus?.vpn?.state == "disabled" }

    // A VPN test's answer: show the fresh state straight away.
    func apply(_ service: MediaDownloadServiceStatus?) {
        if let service { serviceStatus = service }
    }

    // Runs while the Movies tab is on screen: reads the status, then follows the queue every
    // 2.5 s - always while the Downloads pane shows (`following`), otherwise only while something
    // is downloading, for the banner on Home.
    func run(client: SentinelClient, following: Bool) async {
        await refreshStatus(client: client)
        guard canDownload else { return }
        while !Task.isCancelled {
            if following || list == nil || !active.isEmpty {
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
            // 404 is a server from before downloads; anything else hides Downloads until next time.
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

    // A download just started from the Add screen: remember its state so its landing is noticed.
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

// "Downloading Bluey S02E05 · 42%" on Movies Home while anything is in the queue; opens the
// Downloads pane.
struct MediaDownloadsBannerTV: View {
    @ObservedObject var downloads: MediaDownloadsTracker
    let open: () -> Void

    var body: some View {
        let active = downloads.active
        if !active.isEmpty {
            Button(action: open) {
                HStack(spacing: 14) {
                    Image(systemName: "arrow.down.circle.fill")
                        .symbolEffect(.pulse)
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
                }
            }
        }
    }
}

// MARK: - Downloads pane

struct MediaDownloadsTVView: View {
    @ObservedObject var downloads: MediaDownloadsTracker
    let open: (MediaRoute) -> Void
    @EnvironmentObject private var model: AppModel
    @State private var adding = false
    // The download whose actions are showing (Open, Retry, Cancel, Remove).
    @State private var acting: MediaDownload?
    @State private var actionError: String?
    @State private var working: Set<String> = []

    var body: some View {
        List {
            if downloads.serviceStatus != nil {
                Section {
                    MediaVpnStatusTV(downloads: downloads)
                }
            }

            Section {
                Button {
                    adding = true
                } label: {
                    Label("Add a Movie or Show", systemImage: "plus")
                }
                if downloads.list == nil, let error = downloads.listError {
                    Text(error).foregroundStyle(SentinelTheme.red)
                } else if downloads.list == nil {
                    HStack(spacing: 16) { ProgressView(); Text("Loading…").foregroundStyle(.secondary) }
                } else if downloads.active.isEmpty {
                    Text("Nothing downloading. Choose Add to find a movie or TV episodes.")
                        .foregroundStyle(.secondary)
                }
                ForEach(downloads.active) { row($0) }
            } header: {
                Text("Downloading")
            }

            MediaImportListsTVSection(kind: .movies)

            if !downloads.recent.isEmpty {
                Section("Recent") {
                    ForEach(downloads.recent) { row($0) }
                }
            }
        }
        .buttonStyle(.automatic) // rows, not the app-wide FocusContrastButtonStyle
        .padding(.horizontal, 60)
        #if DEBUG
        // Simulator testing without a remote: `-debugMediaAddQuery <title>` opens Add and finds it
        // (see MediaAddTVView).
        .task {
            if UserDefaults.standard.string(forKey: "debugMediaAddQuery") != nil { adding = true }
        }
        #endif
        // Full screen: a tvOS sheet is a narrow card, too small for release names. Menu closes it.
        .fullScreenCover(isPresented: $adding) {
            MediaAddTVView(status: downloads.status, vpnDisabled: downloads.vpnDisabled) { download in
                downloads.noteStarted(download)
                adding = false
            }
            .environmentObject(model)
            // A tvOS full-screen cover is see-through until given a background.
            .background { Color.black.ignoresSafeArea() }
        }
        .confirmationDialog(
            acting?.headline ?? "Download",
            isPresented: Binding(get: { acting != nil }, set: { if !$0 { acting = nil } }),
            titleVisibility: .visible,
            presenting: acting
        ) { download in
            if download.isDone, let itemId = download.itemId {
                Button("Open") { open(MediaRoute(id: itemId, title: download.displayTitle)) }
            }
            if download.isFailed {
                Button("Retry") { Task { await retry(download) } }
            }
            if download.isActive {
                Button("Cancel Download", role: .destructive) { Task { await remove(download) } }
            } else {
                Button("Remove from List", role: .destructive) { Task { await remove(download) } }
            }
            Button(download.isActive ? "Keep Downloading" : "Close", role: .cancel) {}
        } message: { download in
            Text(download.isActive ? "Canceling deletes what has downloaded so far." : download.factsLine)
        }
        .alert("Downloads", isPresented: Binding(get: { actionError != nil }, set: { if !$0 { actionError = nil } })) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(actionError ?? "")
        }
    }

    private func row(_ download: MediaDownload) -> some View {
        Button {
            acting = download
        } label: {
            MediaDownloadRowTV(download: download, working: working.contains(download.id ?? ""))
        }
        .disabled(working.contains(download.id ?? ""))
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

// One download: poster, title, release, progress and how it is going.
private struct MediaDownloadRowTV: View {
    let download: MediaDownload
    let working: Bool
    @EnvironmentObject private var model: AppModel

    var body: some View {
        HStack(alignment: .top, spacing: 24) {
            PosterImage(url: model.client.mediaURL(download.posterUrl), title: download.displayTitle, maxPixel: 300)
                .frame(width: 100, height: 150)
                .clipShape(RoundedRectangle(cornerRadius: 8))
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 12) {
                    Text(download.displayTitle).font(.headline).lineLimit(1)
                    if let year = download.year {
                        Text(String(year)).font(.caption).foregroundStyle(.secondary)
                    }
                    if let subtitle = download.subtitle, !subtitle.isEmpty {
                        MediaChipTV(text: subtitle)
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
                HStack(spacing: 12) {
                    Text(download.factsLine)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .monospacedDigit()
                    if working { ProgressView() }
                }
                if let note = download.error, !note.isEmpty {
                    Text(note)
                        .font(.caption)
                        .foregroundStyle(download.isFailed ? SentinelTheme.red : SentinelTheme.amber)
                }
            }
            Spacer(minLength: 0)
        }
        .padding(.vertical, 8)
    }
}

// A small label: "S02E05", "1080p", "best match".
struct MediaChipTV: View {
    let text: String
    var highlighted = false

    var body: some View {
        Text(text)
            .font(.caption2.weight(.semibold))
            .padding(.horizontal, 10)
            .padding(.vertical, 3)
            // Black on green, like the unwatched-count badge on a poster.
            .foregroundStyle(highlighted ? AnyShapeStyle(.black) : AnyShapeStyle(.secondary))
            .background(highlighted ? AnyShapeStyle(SentinelTheme.green) : AnyShapeStyle(.quaternary.opacity(0.5)),
                        in: RoundedRectangle(cornerRadius: 6))
    }
}

// MARK: - The VPN line

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
struct MediaVpnDotTV: View {
    let tone: MediaVpnLook.Tone
    var size: CGFloat = 16

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

// The dot and the long wording, then Test VPN, which connects now and proves the sites see the
// VPN's address rather than the home one.
struct MediaVpnStatusTV: View {
    @ObservedObject var downloads: MediaDownloadsTracker
    @EnvironmentObject private var model: AppModel
    @State private var testing = false
    @State private var result: (ok: Bool, message: String)?

    var body: some View {
        let look = downloads.vpnLook
        HStack(alignment: .firstTextBaseline, spacing: 16) {
            MediaVpnDotTV(tone: look.tone)
            VStack(alignment: .leading, spacing: 4) {
                Text(look.short).font(.headline)
                Text(look.text)
                    .font(.callout)
                    .foregroundStyle(look.tone == .off || look.tone == .bad ? AnyShapeStyle(look.tone.color) : AnyShapeStyle(.secondary))
                // "Mullvad paid until Oct 27 · 26 days left"; amber when running out, red once out.
                if let account = MediaVpnAccountLine(downloads.serviceStatus?.vpn?.account) {
                    Text(account.text)
                        .font(.caption)
                        .foregroundStyle(account.tone.style)
                }
            }
        }
        .accessibilityElement(children: .combine)
        Button {
            Task { await test() }
        } label: {
            if testing {
                HStack(spacing: 16) {
                    ProgressView()
                    Text("Testing the VPN…")
                }
            } else {
                Label("Test VPN", systemImage: "checkmark.shield")
            }
        }
        .disabled(testing)
        if let result {
            Label(result.message, systemImage: result.ok ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                .font(.callout)
                .foregroundStyle(result.ok ? SentinelTheme.green : SentinelTheme.red)
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
            if (error as? URLError)?.code == .timedOut {
                result = (false, "No answer after 90 seconds - the server couldn't reach the VPN or the download services. Check they're running on the server.")
            } else {
                result = (false, error.localizedDescription)
            }
        }
    }
}
