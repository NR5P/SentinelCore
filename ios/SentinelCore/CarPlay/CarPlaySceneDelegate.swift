#if os(iOS)
import CarPlay
import UIKit

// CarPlay. Two of Apple's CarPlay categories fit this app, and each needs its own entitlement
// (docs/CARPLAY_ENTITLEMENT_REQUEST.md):
//
//   - Audio (com.apple.developer.carplay-audio): Music and Podcasts - browse, play, Now Playing.
//     AudioPlayer already feeds MPNowPlayingInfoCenter and MPRemoteCommandCenter, which is what
//     the CarPlay Now Playing screen reads and drives; this file only adds the browsing.
//   - Driving Task (com.apple.developer.carplay-driving-task): the Home tab (alarm status + arm,
//     Device Hub switches, garage doors, scenes) and Recent Alerts.
//
// The Home and Alerts tabs are behind the Info.plist key SentinelCarPlayControls, so that if
// Apple grants only the audio entitlement the app can ship audio-only by flipping one value -
// CarPlay review rejects templates the granted category doesn't cover.
//
// Driving rules that shape it: no disarm (that needs the PIN, and the car has no keyboard),
// no setup of any kind, no camera snapshots, anything else says to use the phone when it's safe.
final class CarPlaySceneDelegate: UIResponder, CPTemplateApplicationSceneDelegate {
    func templateApplicationScene(_ templateApplicationScene: CPTemplateApplicationScene,
                                  didConnect interfaceController: CPInterfaceController) {
        MainActor.assumeIsolated {
            CarPlayController.shared.connect(interfaceController)
        }
    }

    func templateApplicationScene(_ templateApplicationScene: CPTemplateApplicationScene,
                                  didDisconnectInterfaceController interfaceController: CPInterfaceController) {
        MainActor.assumeIsolated {
            CarPlayController.shared.disconnect()
        }
    }
}

@MainActor
final class CarPlayController: NSObject, CPNowPlayingTemplateObserver, CPTabBarTemplateDelegate, CPInterfaceControllerDelegate {
    static let shared = CarPlayController()

    static var controlsEnabled: Bool {
        (Bundle.main.object(forInfoDictionaryKey: "SentinelCarPlayControls") as? Bool) ?? false
    }

    private var interface: CPInterfaceController?
    private var model: AppModel { AppModel.shared }
    private var client: SentinelClient { model.client }
    private var player: AudioPlayer { AudioPlayer.shared }

    // Made afresh on every connect: a template belongs to the interface controller it was first
    // shown on, so one kept from an earlier connection (a wireless adapter that connects, drops
    // and reconnects while the car starts up) takes updates that never reach the car's screen,
    // which then says "Loading..." forever.
    private var musicTab = CPListTemplate(title: "Music", sections: [])
    private var podcastsTab = CPListTemplate(title: "Podcasts", sections: [])
    private var homeTab = CPListTemplate(title: "Home", sections: [])
    private var alertsTab = CPListTemplate(title: "Alerts", sections: [])
    private var allTabs: [CPListTemplate] { [musicTab, podcastsTab, homeTab, alertsTab] }
    private var pollTask: Task<Void, Never>?
    private var nowPlayingTask: Task<Void, Never>?

    func connect(_ interface: CPInterfaceController) {
        model.log("CarPlay connected (\(model.connectionState.label))")
        pollTask?.cancel()
        pollTask = nil
        self.interface = interface
        musicTab = CPListTemplate(title: "Music", sections: [])
        podcastsTab = CPListTemplate(title: "Podcasts", sections: [])
        homeTab = CPListTemplate(title: "Home", sections: [])
        homeLogged = false
        alertsTab = CPListTemplate(title: "Alerts", sections: [])
        musicTab.tabImage = UIImage(systemName: "music.note")
        podcastsTab.tabImage = UIImage(systemName: "mic")
        homeTab.tabImage = UIImage(systemName: "house")
        alertsTab.tabImage = UIImage(systemName: "bell")
        for tab in allTabs {
            tab.emptyViewTitleVariants = ["Loading\u{2026}"]
        }
        var tabs = [musicTab, podcastsTab]
        if Self.controlsEnabled { tabs += [homeTab, alertsTab] }
        let root = CPTabBarTemplate(templates: tabs)
        root.delegate = self
        tabBar = root
        interface.delegate = self
        configureNowPlaying()
        // Loading starts once the car has the tabs on screen. Sections set on the visible tab
        // before then were dropped: the first tab sat on "Loading" until another was picked.
        interface.setRootTemplate(root, animated: false) { [weak self] _, _ in
            MainActor.assumeIsolated {
                if self?.pollTask == nil { self?.startPolling() }
            }
        }
        // Should the car never confirm, load anyway rather than show "Loading" for good.
        Task { [weak self] in
            try? await Task.sleep(for: .seconds(2))
            guard let self, self.interface === interface, self.pollTask == nil else { return }
            self.startPolling()
        }
    }

    private var tabBar: CPTabBarTemplate?

    private func startPolling() {
        pollTask?.cancel()
        pollTask = Task { [weak self] in
            // Until connected, retried every 15 seconds - the car often comes up before the
            // phone has signal. Once connected, Music and Podcasts load once (re-read when their
            // tab is picked) and Home and Alerts keep themselves current: a garage door shown
            // "open" shouldn't stay that way after it closes.
            var audioLoaded = false
            var tick = 0
            while !Task.isCancelled, let self {
                guard await self.ready() else {
                    audioLoaded = false
                    try? await Task.sleep(for: .seconds(15))
                    continue
                }
                if !audioLoaded {
                    async let music: Void = self.loadMusic()
                    async let podcasts: Void = self.loadPodcasts()
                    _ = await (music, podcasts)
                    audioLoaded = true
                    // Belt and braces for the same dropped first draw: hand the tab on screen
                    // its sections again a moment later, so nobody has to tap to see them.
                    try? await Task.sleep(for: .seconds(1))
                    self.redrawVisibleTab()
                }
                guard Self.controlsEnabled else { return }
                // Alerts in a task of its own, so a slow Home can never hold it back.
                if tick % 6 == 0 { Task { await self.loadAlerts() } }
                await self.loadHome()
                tick += 1
                try? await Task.sleep(for: .seconds(5))
            }
        }
    }

    private func redrawVisibleTab() {
        guard let visible = tabBar?.selectedTemplate as? CPListTemplate else { return }
        visible.updateSections(visible.sections)
    }

    // A tab coming on screen (the first one included, which no tab pick reports) is re-read.
    nonisolated func templateDidAppear(_ aTemplate: CPTemplate, animated: Bool) {
        MainActor.assumeIsolated {
            let shown = (aTemplate as? CPTabBarTemplate)?.selectedTemplate ?? aTemplate
            guard pollTask != nil else { return }
            Task {
                if shown === musicTab { await loadMusic() }
                else if shown === podcastsTab { await loadPodcasts() }
                else if shown === homeTab { await loadHome() }
                else if shown === alertsTab { await loadAlerts() }
            }
        }
    }

    func disconnect() {
        model.log("CarPlay disconnected")
        pollTask?.cancel()
        nowPlayingTask?.cancel()
        interface = nil
    }

    func tabBarTemplate(_ tabBarTemplate: CPTabBarTemplate, didSelect selectedTemplate: CPTemplate) {
        // A cheap re-read so "Up next" and "Recently played" follow what was played on the phone,
        // and Home and Alerts are current the moment they're looked at.
        Task {
            if selectedTemplate === musicTab { await loadMusic() }
            else if selectedTemplate === podcastsTab { await loadPodcasts() }
            else if selectedTemplate === homeTab { await loadHome() }
            else if selectedTemplate === alertsTab { await loadAlerts() }
        }
    }

    // MARK: - Connection

    // CarPlay can be the first thing to launch the app (phone in a pocket), so nothing may have
    // connected yet. The wait is bounded: connect() loads the whole app (events, cameras, push),
    // each request with a 60-second timeout, and the car shouldn't sit on "Loading" through that.
    private func ready() async -> Bool {
        if model.connectionState == .online { return true }
        guard model.isConfigured else {
            showMessage("Sentinel Core isn't set up yet. When it's safe, open it on your iPhone.")
            return false
        }
        // Unstructured, so the connection carries on (and the phone app gets it) if the wait
        // below gives up first. connect() coalesces with one already in flight.
        Task { await self.model.connect() }
        let deadline = ContinuousClock.now + .seconds(20)
        while ContinuousClock.now < deadline, !Task.isCancelled {
            try? await Task.sleep(for: .milliseconds(250))
            if model.connectionState == .online { return true }
            if !model.isConnecting, model.connectionState != .checking { break }
        }
        model.log("CarPlay: not connected after 20s (\(model.connectionState.label)); retrying in 15s", level: .notice)
        showMessage(model.connectionState == .authenticationRequired
                    ? "Signed out. When it's safe, sign in on your iPhone."
                    : "Can't reach your Sentinel Core server. Trying again\u{2026}")
        return false
    }

    // The empty-view text before the (empty) sections: CarPlay redraws on updateSections, so the
    // other order can leave the old text showing.
    private func showMessage(_ text: String) {
        for tab in allTabs {
            tab.emptyViewTitleVariants = [text]
            tab.updateSections([])
        }
    }

    private var listLimit: Int { max(12, CPListTemplate.maximumItemCount) }

    // MARK: - Music

    private func loadMusic() async {
        let enabled = (try? await client.audioEnabled())?.music ?? model.musicEnabled
        guard enabled else {
            musicTab.emptyViewTitleVariants = ["Music is turned off on your server."]
            musicTab.updateSections([])
            return
        }
        let home: MusicHome?
        do { home = try await client.musicHome() } catch {
            home = nil
            model.log("CarPlay: Music couldn't load: \(error.localizedDescription)", level: .error)
        }
        var sections: [CPListSection] = []

        let shuffle = item("Shuffle All", detail: "\(home?.trackCount ?? 0) songs", symbol: "shuffle") { [weak self] in
            self?.start { try await $0.client.musicShuffle() } play: { tracks, controller in
                controller.player.play(tracks: tracks, context: AudioPlayContext(type: "queue", id: "shuffle", name: "Shuffle All"))
            }
        }
        sections.append(CPListSection(items: [shuffle]))

        let recent = (home?.recentlyPlayed ?? []).prefix(6).compactMap { recentItem($0) }
        if !recent.isEmpty { sections.append(CPListSection(items: Array(recent), header: "Recently Played", sectionIndexTitle: nil)) }

        if let pinned = home?.pinned, !pinned.isEmpty {
            sections.append(CPListSection(items: pinned.prefix(6).map(playlistItem), header: "Pinned", sectionIndexTitle: nil))
        }

        let browse: [CPListItem] = [
            folder("Playlists", symbol: "music.note.list") { [weak self] in await self?.playlistsTemplate() },
            folder("Albums", symbol: "square.stack") { [weak self] in await self?.albumsTemplate() },
            folder("Artists", symbol: "music.mic") { [weak self] in await self?.artistsTemplate() },
            folder("Mixes", symbol: "dot.radiowaves.left.and.right") { [weak self] in await self?.mixesTemplate() },
            item("Loved Songs", symbol: "heart") { [weak self] in
                self?.start { try await $0.client.musicTracks(loved: true, limit: 500).items } play: { tracks, controller in
                    controller.player.play(tracks: tracks, context: AudioPlayContext(type: "queue", id: "loved", name: "Loved Songs"))
                }
            },
        ]
        sections.append(CPListSection(items: browse, header: "Library", sectionIndexTitle: nil))
        musicTab.updateSections(sections)
        model.log("CarPlay: Music loaded (\(sections.count) sections)")
        if home == nil { musicTab.emptyViewTitleVariants = ["Can't reach your Sentinel Core server."] }
    }

    private func recentItem(_ recent: MusicRecentItem) -> CPListItem? {
        if let album = recent.album { return albumItem(album) }
        if let playlist = recent.playlist { return playlistItem(playlist) }
        if let mix = recent.mix { return mixItem(mix) }
        if let artist = recent.artist { return artistItem(artist) }
        return nil
    }

    private func albumItem(_ album: MusicAlbum) -> CPListItem {
        let row = folder(album.title, detail: album.artist, art: album.artUrl) { [weak self] in
            await self?.albumTemplate(album)
        }
        return row
    }

    private func playlistItem(_ playlist: MusicPlaylist) -> CPListItem {
        folder(playlist.name, detail: playlist.trackCount.map { "\($0) songs" }, art: playlist.artUrls?.first) { [weak self] in
            await self?.playlistTemplate(playlist)
        }
    }

    private func mixItem(_ mix: MusicMix) -> CPListItem {
        item(mix.name, detail: "Mix", art: mix.artUrls?.first) { [weak self] in
            self?.start { try await $0.client.musicMix(id: mix.id).tracks } play: { tracks, controller in
                controller.player.play(tracks: tracks, context: AudioPlayContext(type: "mix", id: mix.id, name: mix.name))
            }
        }
    }

    private func artistItem(_ artist: MusicArtist) -> CPListItem {
        folder(artist.name, art: artist.artUrl) { [weak self] in await self?.artistTemplate(artist) }
    }

    private func playlistsTemplate() async -> CPTemplate {
        let playlists = (try? await client.musicPlaylists()) ?? []
        return list("Playlists", playlists.prefix(listLimit).map(playlistItem), empty: "No playlists yet.")
    }

    private func albumsTemplate() async -> CPTemplate {
        let albums = (try? await client.musicAlbums(limit: listLimit))?.items ?? []
        return list("Albums", albums.map(albumItem), empty: "No albums yet.")
    }

    private func artistsTemplate() async -> CPTemplate {
        let artists = (try? await client.musicArtists(albumArtistsOnly: true, limit: listLimit))?.items ?? []
        return list("Artists", artists.map(artistItem), empty: "No artists yet.")
    }

    private func mixesTemplate() async -> CPTemplate {
        let mixes = (try? await client.musicHome())?.mixes ?? []
        return list("Mixes", mixes.prefix(listLimit).map(mixItem), empty: "No mixes yet.")
    }

    private func artistTemplate(_ artist: MusicArtist) async -> CPTemplate {
        let detail = try? await client.musicArtist(id: artist.id)
        var sections: [CPListSection] = []
        let radio = item("\(artist.name) Radio", symbol: "dot.radiowaves.left.and.right") { [weak self] in
            self?.start { try await $0.client.musicRadio(seedType: "artist", seedId: artist.id).tracks } play: { tracks, controller in
                controller.player.play(tracks: tracks, context: AudioPlayContext(type: "radio", id: "artist:\(artist.id)", name: "\(artist.name) Radio"))
            }
        }
        sections.append(CPListSection(items: [radio]))
        if let top = detail?.topTracks, !top.isEmpty {
            sections.append(CPListSection(items: trackItems(Array(top.prefix(5)), context: AudioPlayContext(type: "artist", id: artist.id, name: artist.name)),
                                          header: "Top Songs", sectionIndexTitle: nil))
        }
        if let albums = detail?.albums, !albums.isEmpty {
            sections.append(CPListSection(items: albums.prefix(listLimit).map(albumItem), header: "Albums", sectionIndexTitle: nil))
        }
        return CPListTemplate(title: artist.name, sections: sections)
    }

    private func albumTemplate(_ album: MusicAlbum) async -> CPTemplate {
        let tracks = (try? await client.musicAlbum(id: album.id))?.tracks ?? []
        let context = AudioPlayContext(type: "album", id: album.id, name: album.title)
        return trackList(album.title, tracks, context: context)
    }

    private func playlistTemplate(_ playlist: MusicPlaylist) async -> CPTemplate {
        let tracks = (try? await client.musicPlaylist(id: playlist.id))?.tracks ?? []
        let context = AudioPlayContext(type: "playlist", id: playlist.id, name: playlist.name)
        return trackList(playlist.name, tracks, context: context)
    }

    private func trackList(_ title: String, _ tracks: [MusicTrack], context: AudioPlayContext) -> CPTemplate {
        guard !tracks.isEmpty else { return list(title, [], empty: "Nothing to play.") }
        let shuffle = item("Shuffle", symbol: "shuffle") { [weak self] in
            self?.player.play(tracks: tracks, context: context, shuffle: true)
            self?.showNowPlaying()
        }
        return CPListTemplate(title: title, sections: [
            CPListSection(items: [shuffle]),
            CPListSection(items: trackItems(Array(tracks.prefix(listLimit)), all: tracks, context: context)),
        ])
    }

    private func trackItems(_ shown: [MusicTrack], all: [MusicTrack]? = nil, context: AudioPlayContext) -> [CPListItem] {
        let queue = all ?? shown
        return shown.enumerated().map { index, track in
            let row = item(track.title, detail: track.artistLine, art: track.artUrl) { [weak self] in
                self?.player.play(tracks: queue, startAt: index, context: context)
                self?.showNowPlaying()
            }
            row.isPlaying = player.current?.track?.id == track.id
            return row
        }
    }

    // MARK: - Podcasts

    private func loadPodcasts() async {
        let enabled = (try? await client.audioEnabled())?.podcasts ?? model.podcastsEnabled
        guard enabled else {
            podcastsTab.emptyViewTitleVariants = ["Podcasts are turned off on your server."]
            podcastsTab.updateSections([])
            return
        }
        let loaded: PodcastHome?
        do { loaded = try await client.podcastHome() } catch {
            loaded = nil
            model.log("CarPlay: Podcasts couldn't load: \(error.localizedDescription)", level: .error)
        }
        guard let home = loaded else {
            podcastsTab.emptyViewTitleVariants = ["Can't reach your Sentinel Core server."]
            podcastsTab.updateSections([])
            return
        }
        var sections: [CPListSection] = []
        let upNext = home.upNext ?? []
        if !upNext.isEmpty {
            sections.append(CPListSection(items: episodeItems(Array(upNext.prefix(8)), queue: upNext,
                                                              context: AudioPlayContext(type: "queue", id: "upnext", name: "Up Next")),
                                          header: "Up Next", sectionIndexTitle: nil))
        }
        let continuing = (home.continueListening ?? []).filter { e in !upNext.contains { $0.id == e.id } }
        if !continuing.isEmpty {
            sections.append(CPListSection(items: episodeItems(Array(continuing.prefix(5)), queue: continuing, context: nil),
                                          header: "Continue Listening", sectionIndexTitle: nil))
        }
        let newest = home.newEpisodes ?? []
        if !newest.isEmpty {
            sections.append(CPListSection(items: episodeItems(Array(newest.prefix(8)), queue: newest, context: nil),
                                          header: "New Episodes", sectionIndexTitle: nil))
        }
        let shows = home.subscriptions ?? []
        if !shows.isEmpty {
            sections.append(CPListSection(items: shows.prefix(listLimit).map(showItem), header: "Shows", sectionIndexTitle: nil))
        }
        podcastsTab.emptyViewTitleVariants = ["No podcasts yet. Subscribe on your phone."]
        podcastsTab.updateSections(sections)
    }

    private func showItem(_ show: Podcast) -> CPListItem {
        let detail = (show.unplayedCount ?? 0) > 0 ? "\(show.unplayedCount ?? 0) unplayed" : show.author
        return folder(show.title, detail: detail, art: show.artUrl) { [weak self] in
            guard let self else { return nil }
            let episodes = (try? await self.client.podcastEpisodes(id: show.id, sort: nil, filter: nil, q: nil, season: nil, limit: self.listLimit))?.items ?? []
            return self.list(show.title,
                             self.episodeItems(episodes, queue: episodes, context: AudioPlayContext(type: "podcast", id: show.id, name: show.title)),
                             empty: "No episodes yet.")
        }
    }

    private func episodeItems(_ shown: [PodcastEpisode], queue: [PodcastEpisode], context: AudioPlayContext?) -> [CPListItem] {
        shown.map { episode in
            let detail = [episode.podcastTitle, episode.remainingLabel].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: " · ")
            let row = item(episode.title, detail: detail, art: episode.artUrl) { [weak self] in
                guard let self else { return }
                let rest = queue.drop { $0.id != episode.id }.dropFirst()
                self.player.play(episode: episode, queue: Array(rest), context: context)
                self.showNowPlaying()
            }
            if episode.isInProgress, let progress = episode.progress, let position = progress.positionSeconds,
               let duration = episode.durationSeconds, duration > 0 {
                row.playbackProgress = CGFloat(min(1, position / duration))
            }
            row.isPlaying = player.current?.episode?.id == episode.id
            return row
        }
    }

    // MARK: - Now Playing

    private func configureNowPlaying() {
        let nowPlaying = CPNowPlayingTemplate.shared
        nowPlaying.isUpNextButtonEnabled = true
        nowPlaying.upNextTitle = "Up Next"
        nowPlaying.isAlbumArtistButtonEnabled = false
        nowPlaying.add(self)
        // Music gets shuffle and repeat; podcasts get the speed button. Follows the player.
        nowPlayingTask?.cancel()
        nowPlayingTask = Task { [weak self] in
            var lastEpisode: Bool?
            while !Task.isCancelled, let self {
                let isEpisode = self.player.isEpisode
                if isEpisode != lastEpisode {
                    lastEpisode = isEpisode
                    self.updateNowPlayingButtons(episode: isEpisode)
                }
                try? await Task.sleep(for: .seconds(1))
            }
        }
    }

    private func updateNowPlayingButtons(episode: Bool) {
        let buttons: [CPNowPlayingButton]
        if episode {
            buttons = [CPNowPlayingPlaybackRateButton { _ in
                MainActor.assumeIsolated {
                    let speeds: [Double] = [1, 1.25, 1.5, 2, 0.8]
                    let now = AudioPlayer.shared.speed
                    let next = speeds.first { $0 > now + 0.01 } ?? speeds[0]
                    AudioPlayer.shared.setSpeed(next == 0.8 && now < 1 ? 1 : next)
                }
            }]
        } else {
            buttons = [
                CPNowPlayingShuffleButton { _ in MainActor.assumeIsolated { AudioPlayer.shared.toggleShuffle() } },
                CPNowPlayingRepeatButton { _ in MainActor.assumeIsolated { AudioPlayer.shared.cycleRepeat() } },
            ]
        }
        CPNowPlayingTemplate.shared.updateNowPlayingButtons(buttons)
    }

    // Now Playing goes straight on top of the tab bar, whatever was browsed to get there - the
    // way Apple's own audio apps do it, and it keeps the stack inside the depth limit below.
    private func showNowPlaying() {
        guard let interface else { return }
        if interface.topTemplate === CPNowPlayingTemplate.shared { return }
        if interface.templates.count > 1 {
            interface.popToRootTemplate(animated: false, completion: nil)
        }
        interface.pushTemplate(CPNowPlayingTemplate.shared, animated: true, completion: nil)
    }

    // CarPlay ends the app (clientExceededHierarchyDepthLimit) once the stack gets deeper than
    // its category allows. With the Driving Task entitlement that is three templates, the tab
    // bar included - stricter than Audio's five, and it applies to the music browsing too.
    // So at the limit the top list is swapped for the new one instead of stacked on it: Artists,
    // an artist, then an album shows Artists -> the album, and Back still makes sense.
    static let maximumDepth = 3

    private func push(_ template: CPTemplate) {
        guard let interface else { return }
        if interface.templates.count >= Self.maximumDepth {
            interface.popTemplate(animated: false, completion: nil)
        }
        interface.pushTemplate(template, animated: true, completion: nil)
    }

    nonisolated func nowPlayingTemplateUpNextButtonTapped(_ nowPlayingTemplate: CPNowPlayingTemplate) {
        MainActor.assumeIsolated {
            let start = player.index
            let items = player.queue.enumerated().dropFirst(start).prefix(listLimit).map { offset, entry in
                let row = item(entry.title, detail: entry.subtitle, art: entry.artPath) { [weak self] in
                    self?.player.jump(to: offset)
                    self?.interface?.popTemplate(animated: true, completion: nil)
                }
                row.isPlaying = offset == start
                return row
            }
            push(list("Up Next", Array(items), empty: "Nothing queued."))
        }
    }

    nonisolated func nowPlayingTemplateAlbumArtistButtonTapped(_ nowPlayingTemplate: CPNowPlayingTemplate) {}

    // MARK: - Home (driving task)

    // The four reads side by side, each cut off after 8 seconds (the client's own timeout is 60),
    // so one slow answer can't leave the tab on "Loading". Whatever came back is shown.
    private func loadHome() async {
        let started = ContinuousClock.now
        let alarmOn = model.alarmEnabled
        async let alarmRead = timed("alarm") { [client] in
            alarmOn ? try await client.alarmStatus() : nil
        }
        async let hubRead = timed("device hub") { [client] in try await client.deviceHubEnabled().enabled }
        async let devicesRead = timed("devices") { [client] in try await client.devices() }
        async let scenesRead = timed("scenes") { [client] in try await client.schedules() }
        let (alarm, hub, devices, schedules) = await (alarmRead, hubRead, devicesRead, scenesRead)

        var sections: [CPListSection] = []
        if let alarm = alarm ?? nil, alarm.enabled {
            sections.append(CPListSection(items: alarmItems(alarm), header: "Alarm", sectionIndexTitle: nil))
        }
        let hubOn = hub ?? false
        if hubOn, let devices {
            // Every enabled Device Hub (MQTT) device that takes commands.
            let shown = devices.filter { $0.enabled && $0.commandable }
                .sorted { ($0.sortOrder ?? 0, $0.room, $0.name) < ($1.sortOrder ?? 0, $1.room, $1.name) }
            if !shown.isEmpty {
                sections.append(CPListSection(items: shown.prefix(listLimit).map(deviceItem), header: "Devices", sectionIndexTitle: nil))
            }
        }
        if hubOn, let schedules {
            let scenes = schedules.filter { $0.enabled && ["device", "mqtt", "webhook"].contains($0.action.type) }
                .sorted { $0.name < $1.name }
            if !scenes.isEmpty {
                sections.append(CPListSection(items: scenes.prefix(listLimit).map(sceneItem), header: "Scenes", sectionIndexTitle: nil))
            }
        }
        sections = Array(sections.prefix(max(1, CPListTemplate.maximumSectionCount)))
        let failed = alarm == nil && hub == nil && devices == nil && schedules == nil
        homeTab.emptyViewTitleVariants = [failed ? "Can't reach your Sentinel Core server." : "No alarm or devices are set up."]
        homeTab.updateSections(sections)
        if !homeLogged {
            homeLogged = true
            let rows = sections.reduce(0) { $0 + $1.items.count }
            model.log("CarPlay: Home loaded in \(started.duration(to: .now).formatted(.units(allowed: [.seconds, .milliseconds], width: .abbreviated))) - \(sections.count) sections, \(rows) rows (hub \(hub.map { $0 ? "on" : "off" } ?? "unknown"), \(devices?.count.description ?? "no") devices)")
        }
    }
    private var homeLogged = false

    // nil when the read failed or took longer than 8 seconds; either is logged.
    private func timed<T: Sendable>(_ what: String, _ read: @escaping @Sendable () async throws -> T) async -> T? {
        await withTaskGroup(of: Result<T, Error>?.self) { group in
            group.addTask {
                do { return .success(try await read()) } catch { return .failure(error) }
            }
            group.addTask {
                try? await Task.sleep(for: .seconds(8))
                return nil
            }
            let first = await group.next() ?? nil
            group.cancelAll()
            switch first {
            case .success(let value)?:
                return value
            case .failure(let error)?:
                model.log("CarPlay: \(what) couldn't load: \(error.localizedDescription)", level: .error)
                return nil
            case nil:
                model.log("CarPlay: \(what) took longer than 8s", level: .error)
                return nil
            }
        }
    }

    private func alarmItems(_ alarm: AlarmStatus) -> [CPListItem] {
        let open = (alarm.sensors ?? []).filter { $0.isOpen && !$0.bypassed }.map(\.name).joined(separator: ", ")
        var rows = [CPListItem(text: alarmLine(alarm), detailText: open.isEmpty ? alarm.issues?.first : "Open: \(open)",
                               image: UIImage(systemName: alarm.isTriggered ? "light.beacon.max" : alarm.isArmed ? "lock.shield" : "shield"))]
        if (alarm.status ?? "disarmed") == "disarmed" {
            if alarm.config?.requirePinToArm == true {
                rows.append(CPListItem(text: "Arming needs the PIN", detailText: "When it's safe, use your iPhone."))
            } else {
                for (mode, detail) in [("home", "Doors and windows"), ("night", "Doors, windows, night zones"), ("away", "Everything, after the exit delay")] {
                    rows.append(item("Arm \(AlarmMode.label(for: mode).lowercased())", detail: detail, symbol: "lock") { [weak self] in
                        self?.arm(mode)
                    })
                }
            }
        } else {
            // Disarming takes the PIN - not something to type on a car screen.
            rows.append(CPListItem(text: "To disarm", detailText: "When it's safe, use your iPhone."))
        }
        return rows
    }

    private func alarmLine(_ alarm: AlarmStatus) -> String {
        let mode = AlarmMode.label(for: alarm.mode ?? "").lowercased()
        switch alarm.status {
        case "triggered": return "ALARM GOING OFF" + (alarm.triggeredBy.map { " - \($0)" } ?? "")
        case "pending": return "Entry delay - about to go off"
        case "arming": return "Arming \(mode)"
        case "armed": return "Armed \(mode)"
        default: return "Disarmed"
        }
    }

    private func arm(_ mode: String) {
        Task {
            do {
                let reply = try await client.armAlarm(mode: mode, force: false, pin: nil)
                let said = mode == "away" ? "Arming away" : "Armed \(mode)"
                alert([said, reply.note].compactMap { $0 }.joined(separator: ". "))
            } catch let ClientError.server(status, message) {
                alert(status == 403 ? "Arming needs the PIN. When it's safe, use your iPhone." : (message ?? "The alarm couldn't be armed."))
            } catch {
                alert("Can't reach your Sentinel Core server.")
            }
            await loadHome()
        }
    }

    private func deviceItem(_ device: HubDevice) -> CPListItem {
        let state: String
        if !device.available { state = "Unavailable" }
        else if device.isMomentary { state = "Tap to press" }
        else if device.isLock { state = ["on": "Locked", "off": "Unlocked"][device.power] ?? "Unknown" }
        else if device.isCoverLike { state = ["on": "Open", "off": "Closed"][device.power] ?? "Unknown" }
        else { state = ["on": "On", "off": "Off"][device.power] ?? "Unknown" }
        let detail = [device.room.isEmpty ? nil : device.room, state].compactMap { $0 }.joined(separator: " · ")
        let row = item(device.name, detail: detail, symbol: device.systemImage) { [weak self] in
            guard let self else { return }
            if device.isMomentary {
                self.send(device, "press", said: "\(device.name): pressed")
            } else if device.isCoverLike || device.isLock {
                self.deviceSheet(device)
            } else {
                self.send(device, "toggle", said: nil)
            }
        }
        if !device.isMomentary && !device.isCoverLike && !device.isLock {
            row.accessoryType = .none
            row.setAccessoryImage(UIImage(systemName: device.isOn ? "togglepower" : "poweroff"))
        }
        return row
    }

    // Covers, gates, garage doors and locks: their two or three actions, one tap each.
    private func deviceSheet(_ device: HubDevice) {
        let choices: [(String, String)] = device.isLock
            ? [("lock", "Lock"), ("unlock", "Unlock")]
            : [("open", "Open"), ("close", "Close")] + (device.hasStop ? [("stop", "Stop")] : [])
        var actions = choices.map { action, label in
            CPAlertAction(title: label, style: action == "stop" ? .destructive : .default) { _ in
                MainActor.assumeIsolated {
                    self.interface?.dismissTemplate(animated: true, completion: nil)
                    self.send(device, action, said: "\(device.name): \(label.lowercased())")
                }
            }
        }
        actions.append(CPAlertAction(title: "Cancel", style: .cancel) { _ in
            MainActor.assumeIsolated { self.interface?.dismissTemplate(animated: true, completion: nil) }
        })
        let sheet = CPActionSheetTemplate(title: device.name, message: nil, actions: actions)
        interface?.presentTemplate(sheet, animated: true, completion: nil)
    }

    private func send(_ device: HubDevice, _ action: String, said: String?) {
        Task {
            do {
                let result = try await client.sendDeviceCommand(id: device.id, action: action)
                if !result.sent { alert("\(device.name) didn't get the command.") }
                else if let said { alert(said) }
            } catch let ClientError.server(_, message) {
                alert(message ?? "\(device.name) didn't get the command.")
            } catch {
                alert("Can't reach your Sentinel Core server.")
            }
            await loadHome()
        }
    }

    private func sceneItem(_ schedule: Schedule) -> CPListItem {
        item(schedule.name, detail: "Tap to run", symbol: "sparkles") { [weak self] in
            Task {
                guard let self else { return }
                do {
                    let result = try await self.client.runSchedule(id: schedule.id)
                    self.alert(result.fired ? "\(schedule.name): done" : (result.error ?? result.detail ?? "\(schedule.name) didn't run."))
                } catch let ClientError.server(status, message) {
                    self.alert(status == 403 ? "Only an admin can run scenes." : (message ?? "\(schedule.name) didn't run."))
                } catch {
                    self.alert("Can't reach your Sentinel Core server.")
                }
            }
        }
    }

    // MARK: - Alerts (driving task)

    private func loadAlerts() async {
        guard let activity = await timed("alerts", { [client] in try await client.activity(notifiedOnly: true) }) else {
            // Left as it was if alerts have shown before; never left on "Loading".
            if alertsTab.sections.isEmpty {
                alertsTab.emptyViewTitleVariants = ["Couldn't load recent alerts."]
                alertsTab.updateSections([])
            }
            return
        }
        let recent = activity.filter(\.notified).sorted { $0.occurredAt > $1.occurredAt }.prefix(listLimit)
        let formatter = RelativeDateTimeFormatter()
        formatter.unitsStyle = .abbreviated
        // Text only: no camera snapshots on a car screen.
        let rows = recent.map { event in
            let title = event.eventType.flatMap { $0.isEmpty ? nil : "\(event.camera): \($0.replacingOccurrences(of: "_", with: " "))" } ?? event.camera
            let when = formatter.localizedString(for: event.occurredAt, relativeTo: Date())
            let detail = [event.summary.map { String($0.prefix(100)) }, when].compactMap { $0 }.joined(separator: " · ")
            return CPListItem(text: title, detailText: detail)
        }
        alertsTab.emptyViewTitleVariants = ["No recent alerts."]
        alertsTab.updateSections(rows.isEmpty ? [] : [CPListSection(items: Array(rows))])
    }

    // MARK: - Building blocks

    private func list(_ title: String, _ items: [CPListItem], empty: String) -> CPListTemplate {
        let template = CPListTemplate(title: title, sections: [CPListSection(items: items)])
        template.emptyViewTitleVariants = [empty]
        return template
    }

    private func item(_ text: String, detail: String? = nil, art: String? = nil, symbol: String? = nil,
                      action: @escaping @MainActor () -> Void) -> CPListItem {
        let row = CPListItem(text: text, detailText: detail, image: symbol.flatMap { UIImage(systemName: $0) })
        row.handler = { _, completion in
            MainActor.assumeIsolated { action() }
            completion()
        }
        loadArt(art, into: row)
        return row
    }

    // A row that opens another list. The list is fetched before it's pushed, with the row's
    // spinner showing meanwhile (CarPlay shows it until the completion is called).
    private func folder(_ text: String, detail: String? = nil, art: String? = nil, symbol: String? = nil,
                        next: @escaping @MainActor () async -> CPTemplate?) -> CPListItem {
        let row = CPListItem(text: text, detailText: detail, image: symbol.flatMap { UIImage(systemName: $0) },
                             accessoryImage: nil, accessoryType: .disclosureIndicator)
        row.handler = { [weak self] _, completion in
            MainActor.assumeIsolated {
                Task { @MainActor in
                    if let template = await next() {
                        self?.push(template)
                    }
                    completion()
                }
            }
        }
        loadArt(art, into: row)
        return row
    }

    private func loadArt(_ path: String?, into row: CPListItem) {
        guard let path, let url = client.audioArtURL(path, size: 120) else { return }
        Task { @MainActor in
            if let cg = await ArtworkLoader.cgImage(url, maxPixel: 120) {
                row.setImage(UIImage(cgImage: cg))
            }
        }
    }

    // Load-then-play for things that need a fetch first (shuffle, mixes, loved songs).
    private func start<T>(_ fetch: @escaping @MainActor (CarPlayController) async throws -> [T],
                          play: @escaping @MainActor ([T], CarPlayController) -> Void) {
        Task { @MainActor in
            do {
                let items = try await fetch(self)
                guard !items.isEmpty else { return alert("Nothing to play yet.") }
                play(items, self)
                showNowPlaying()
            } catch {
                alert("Couldn't start that. Can't reach your Sentinel Core server.")
            }
        }
    }

    private func alert(_ text: String) {
        guard let interface else { return }
        let ok = CPAlertAction(title: "OK", style: .cancel) { _ in
            MainActor.assumeIsolated { CarPlayController.shared.interface?.dismissTemplate(animated: true, completion: nil) }
        }
        let template = CPAlertTemplate(titleVariants: [text], actions: [ok])
        if interface.presentedTemplate != nil {
            interface.dismissTemplate(animated: false, completion: nil)
        }
        interface.presentTemplate(template, animated: true, completion: nil)
        // Confirmations clear themselves; nobody should have to tap OK at a light.
        Task { @MainActor in
            try? await Task.sleep(for: .seconds(3))
            if interface.presentedTemplate === template {
                interface.dismissTemplate(animated: true, completion: nil)
            }
        }
    }
}
#endif
