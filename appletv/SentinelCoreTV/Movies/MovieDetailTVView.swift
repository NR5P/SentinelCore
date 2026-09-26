import SwiftUI

// A film or show, the way the Apple TV app draws one: the backdrop fills the screen behind a dark
// wash, the title and facts sit on the left, the buttons under them, then the episodes of the
// chosen season, then the cast. Always dark, whatever the TV's appearance - it is drawn over a
// picture.
struct MovieDetailTVView: View {
    let route: MediaRoute
    @EnvironmentObject private var model: AppModel
    @State private var detail: MediaItemDetail?
    @State private var errorMessage: String?
    @State private var seasonNumber: Int?
    @State private var playing: MoviePlayRequest?
    @State private var working = false

    var body: some View {
        ZStack(alignment: .topLeading) {
            backdrop
            ScrollView {
                if let detail {
                    content(detail)
                        .padding(.horizontal, 80)
                        .padding(.top, 260)
                        .padding(.bottom, 60)
                }
            }
            .scrollClipDisabled()
        }
        .environment(\.colorScheme, .dark)
        .overlay {
            if detail == nil {
                if let errorMessage {
                    ContentUnavailableView(route.title, systemImage: "exclamationmark.triangle", description: Text(errorMessage))
                } else {
                    ProgressView("Loading…")
                }
            }
        }
        .task(id: model.activeServerURL) {
            guard !model.activeServerURL.isEmpty else { return }
            await load()
        }
        .fullScreenCover(item: $playing, onDismiss: { Task { await load() } }) { request in
            MoviePlayerTVView(request: request, client: model.client)
        }
    }

    // The film's picture edge to edge, with a wash from the left for the text and from the
    // bottom for the shelves.
    private var backdrop: some View {
        ZStack {
            Color.black
            if let url = model.client.mediaURL(detail?.summary.backdropUrl) {
                PosterImage(url: url, title: "", maxPixel: 1920)
            }
            LinearGradient(colors: [.black.opacity(0.92), .black.opacity(0.55), .black.opacity(0.1)], startPoint: .leading, endPoint: .trailing)
            LinearGradient(colors: [.clear, .black.opacity(0.9)], startPoint: .center, endPoint: .bottom)
        }
        .ignoresSafeArea()
    }

    @ViewBuilder
    private func content(_ detail: MediaItemDetail) -> some View {
        let item = detail.summary
        VStack(alignment: .leading, spacing: 44) {
            HStack(alignment: .top, spacing: 50) {
                PosterImage(url: model.client.mediaURL(item.posterUrl), title: item.title, subtitle: item.yearLabel, maxPixel: 600)
                    .frame(width: 300, height: 450)
                    .clipShape(RoundedRectangle(cornerRadius: 12))
                    .shadow(radius: 20)
                header(detail)
            }
            actions(detail)
            if item.isShow {
                seasonSection(detail)
            }
            if let cast = detail.cast, !cast.isEmpty {
                castSection(cast)
            }
        }
        .foregroundStyle(.white)
    }

    private func header(_ detail: MediaItemDetail) -> some View {
        let item = detail.summary
        return VStack(alignment: .leading, spacing: 16) {
            if item.isEpisode, let show = item.showTitle {
                Text(show).font(.headline).foregroundStyle(.white.opacity(0.7))
            }
            Text(item.isEpisode ? [item.episodeCode, item.title].compactMap { $0 }.joined(separator: " · ") : item.title)
                .font(.system(size: 60, weight: .bold))
                .lineLimit(2)
                .minimumScaleFactor(0.6)
            HStack(spacing: 16) {
                if !item.metaLine.isEmpty { Text(item.metaLine) }
                if let certification = item.certification, !certification.isEmpty {
                    CertificationBadge(text: certification)
                }
                if item.isWatched {
                    Label("Watched", systemImage: "checkmark.circle.fill").foregroundStyle(SentinelTheme.green)
                }
            }
            .font(.callout)
            .foregroundStyle(.white.opacity(0.8))
            if let genres = item.genres, !genres.isEmpty {
                Text(genres.joined(separator: " · ")).font(.caption).foregroundStyle(.white.opacity(0.7))
            }
            if let tagline = detail.tagline, !tagline.isEmpty {
                Text(tagline).font(.callout.italic()).foregroundStyle(.white.opacity(0.85))
            }
            if let overview = detail.overview, !overview.isEmpty {
                Text(overview)
                    .font(.callout)
                    .foregroundStyle(.white.opacity(0.85))
                    .lineLimit(6)
                    .frame(maxWidth: 1100, alignment: .leading)
            }
            if let file = detail.file?.summaryLine {
                Text(file).font(.caption2).foregroundStyle(.white.opacity(0.55))
            }
        }
    }

    // Play / Resume / Start Over / Mark Watched. A show plays its next episode.
    @ViewBuilder
    private func actions(_ detail: MediaItemDetail) -> some View {
        let item = detail.summary
        HStack(spacing: 30) {
            if item.isShow {
                if let next = detail.nextEpisode {
                    Button {
                        playing = MoviePlayRequest(itemId: next.id, start: nil)
                    } label: {
                        Label(
                            "\(next.canResume ? "Resume" : "Play") \(next.episodeCode ?? next.title)",
                            systemImage: "play.fill")
                    }
                    if next.canResume {
                        Button {
                            playing = MoviePlayRequest(itemId: next.id, start: 0)
                        } label: {
                            Label("Start Over", systemImage: "backward.end.fill")
                        }
                    }
                }
            } else {
                Button {
                    playing = MoviePlayRequest(itemId: item.id, start: nil, overview: detail.overview)
                } label: {
                    Label(item.canResume ? "Resume" : "Play", systemImage: "play.fill")
                }
                if item.canResume {
                    Button {
                        playing = MoviePlayRequest(itemId: item.id, start: 0, overview: detail.overview)
                    } label: {
                        Label("Start Over", systemImage: "backward.end.fill")
                    }
                }
            }
            Button {
                Task { await setWatched(!item.isWatched, on: item.id) }
            } label: {
                Label(item.isWatched ? "Mark as Unwatched" : "Mark as Watched", systemImage: item.isWatched ? "eye.slash" : "checkmark")
            }
            .disabled(working)
            if item.canResume, let remaining = item.remainingLabel {
                Text(remaining).font(.caption).foregroundStyle(.white.opacity(0.7))
            }
        }
    }

    @ViewBuilder
    private func seasonSection(_ detail: MediaItemDetail) -> some View {
        let seasons = detail.seasonList
        let selected = seasons.first { $0.number == seasonNumber } ?? defaultSeason(detail)
        VStack(alignment: .leading, spacing: 24) {
            HStack(spacing: 30) {
                if seasons.count > 1 {
                    if seasons.count <= 8 {
                        Picker("Season", selection: Binding(get: { selected?.number ?? 0 }, set: { seasonNumber = $0 })) {
                            ForEach(seasons) { Text($0.title).tag($0.number) }
                        }
                        .pickerStyle(.segmented)
                        .frame(maxWidth: CGFloat(seasons.count) * 220)
                    } else {
                        Menu {
                            ForEach(seasons) { season in
                                Button(season.title) { seasonNumber = season.number }
                            }
                        } label: {
                            Label(selected?.title ?? "Season", systemImage: "list.number")
                        }
                    }
                } else if let selected {
                    Text(selected.title).font(.title3.bold())
                }
                if let selected {
                    let allWatched = (selected.watchedCount ?? 0) >= (selected.episodeCount ?? selected.episodeList.count)
                    Button {
                        Task { await setSeasonWatched(!allWatched, season: selected.number, showId: detail.id) }
                    } label: {
                        Label(allWatched ? "Mark Season Unwatched" : "Mark Season Watched", systemImage: allWatched ? "eye.slash" : "checkmark")
                    }
                    .disabled(working)
                }
            }
            if let selected {
                ScrollView(.horizontal) {
                    LazyHStack(alignment: .top, spacing: 40) {
                        ForEach(selected.episodeList) { episode in
                            MediaLandscapeTileTV(
                                item: episode, title: episode.episodeRowTitle,
                                subtitle: episode.remainingLabel ?? episode.runtimeLabel,
                                width: 440,
                                action: { playing = MoviePlayRequest(itemId: episode.id, start: nil) })
                            .contextMenu {
                                Button("Play", systemImage: "play.fill") { playing = MoviePlayRequest(itemId: episode.id, start: nil) }
                                if episode.canResume {
                                    Button("Start Over", systemImage: "backward.end.fill") { playing = MoviePlayRequest(itemId: episode.id, start: 0) }
                                }
                                Button(episode.isWatched ? "Mark as Unwatched" : "Mark as Watched", systemImage: "checkmark") {
                                    Task { await setWatched(!episode.isWatched, on: episode.id) }
                                }
                            }
                        }
                    }
                    .padding(.vertical, 20)
                }
                .scrollClipDisabled()
            }
        }
    }

    private func castSection(_ cast: [MediaCastMember]) -> some View {
        VStack(alignment: .leading, spacing: 20) {
            Text("Cast").font(.title3.bold())
            ScrollView(.horizontal) {
                LazyHStack(spacing: 30) {
                    ForEach(Array(cast.prefix(30).enumerated()), id: \.offset) { _, member in
                        // Focusable only so the remote can scroll down to it; there is nothing to open.
                        Button {} label: {
                            VStack(alignment: .leading, spacing: 6) {
                                Text(member.name).font(.caption.bold()).lineLimit(1)
                                if let character = member.character, !character.isEmpty {
                                    Text(character).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                                }
                            }
                            .frame(width: 280, alignment: .leading)
                            .padding(20)
                        }
                        .buttonStyle(.card)
                    }
                }
                .padding(.vertical, 20)
            }
            .scrollClipDisabled()
        }
    }

    // The season holding the episode to play next, else the first.
    private func defaultSeason(_ detail: MediaItemDetail) -> MediaSeason? {
        let seasons = detail.seasonList
        if let next = detail.nextEpisode, let season = seasons.first(where: { $0.episodeList.contains { $0.id == next.id } }) {
            return season
        }
        return seasons.first { $0.number > 0 } ?? seasons.first
    }

    private func load() async {
        do {
            detail = try await model.client.mediaItem(id: route.id)
            errorMessage = nil
        } catch {
            if error is CancellationError || (error as? URLError)?.code == .cancelled { return }
            errorMessage = error.localizedDescription
        }
    }

    private func setWatched(_ watched: Bool, on id: String) async {
        working = true
        defer { working = false }
        _ = try? await model.client.setMediaWatched(id: id, watched: watched)
        await load()
    }

    private func setSeasonWatched(_ watched: Bool, season: Int, showId: String) async {
        working = true
        defer { working = false }
        try? await model.client.setMediaSeasonWatched(showId: showId, season: season, watched: watched)
        await load()
    }
}
