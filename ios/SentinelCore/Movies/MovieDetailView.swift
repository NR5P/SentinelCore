import SwiftUI

// A film or show: backdrop across the top with the poster over its lower edge, the facts, Play /
// Resume / Start Over / Mark Watched / My List, then for a show the season picker and its episodes, then the
// cast and the file.
struct MovieDetailView: View {
    let route: MediaRoute
    @EnvironmentObject private var model: AppModel
    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
    @State private var detail: MediaItemDetail?
    @State private var errorMessage: String?
    @State private var seasonNumber: Int?
    @State private var playing: MoviePlayRequest?
    @State private var working = false
    // A My List change the server turned down; the button has already been put back.
    @State private var listError: String?
    @Environment(\.dismiss) private var dismiss
    @State private var confirmingDelete = false
    @State private var deleteError: String?

    private var compact: Bool { horizontalSizeClass == .compact }

    var body: some View {
        ScrollView {
            if let detail {
                content(detail)
                    .frame(maxWidth: 1000, alignment: .leading)
                    .frame(maxWidth: .infinity)
            }
        }
        .navigationTitle(detail?.summary.title ?? route.title)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .overlay {
            if detail == nil {
                if let errorMessage {
                    ContentUnavailableView(route.title, systemImage: "exclamationmark.triangle", description: Text(errorMessage))
                } else {
                    ProgressView("Loading…")
                }
            }
        }
        .refreshable { await load() }
        .task(id: model.activeServerURL) {
            guard !model.activeServerURL.isEmpty else { return }
            await load()
        }
        .moviePlayer($playing, client: model.client) { Task { await load() } }
        .alert("My List", isPresented: Binding(get: { listError != nil }, set: { if !$0 { listError = nil } })) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(listError ?? "")
        }
        .toolbar {
            if detail?.canDelete == true {
                ToolbarItem(placement: .primaryAction) {
                    Button(role: .destructive) {
                        confirmingDelete = true
                    } label: {
                        Label("Delete Movie", systemImage: "trash")
                    }
                    .disabled(working)
                }
            }
        }
        .confirmationDialog(
            "Delete \(detail?.summary.title ?? route.title)?",
            isPresented: $confirmingDelete,
            titleVisibility: .visible
        ) {
            Button("Delete", role: .destructive) { Task { await deleteMovie() } }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("The movie's file is deleted from the server's disk for everyone. This can't be undone.")
        }
        .alert("Couldn't Delete", isPresented: Binding(get: { deleteError != nil }, set: { if !$0 { deleteError = nil } })) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(deleteError ?? "")
        }
    }

    @ViewBuilder
    private func content(_ detail: MediaItemDetail) -> some View {
        let item = detail.summary
        VStack(alignment: .leading, spacing: 20) {
            header(detail)
            VStack(alignment: .leading, spacing: 20) {
                actions(detail)
                if let tagline = detail.tagline, !tagline.isEmpty {
                    Text(tagline).font(.callout.italic()).foregroundStyle(.secondary)
                }
                if let overview = detail.overview, !overview.isEmpty {
                    Text(overview).font(.body).fixedSize(horizontal: false, vertical: true)
                }
                if item.isShow {
                    seasonSection(detail)
                }
                if let cast = detail.cast, !cast.isEmpty {
                    castSection(cast)
                }
                if let file = detail.file {
                    fileSection(file)
                }
            }
            .padding(.horizontal)
        }
        .padding(.bottom, 30)
    }

    private func header(_ detail: MediaItemDetail) -> some View {
        let item = detail.summary
        let posterWidth: CGFloat = compact ? 100 : 150
        return VStack(alignment: .leading, spacing: 0) {
            PosterImage(url: model.client.mediaURL(item.backdropUrl), title: "", maxPixel: 1600)
                .frame(maxWidth: .infinity)
                .frame(height: compact ? 220 : 340)
                .overlay {
                    LinearGradient(colors: [.clear, .black.opacity(0.35)], startPoint: .center, endPoint: .bottom)
                }
            HStack(alignment: .top, spacing: 16) {
                PosterImage(url: model.client.mediaURL(item.posterUrl), title: item.title, subtitle: item.yearLabel, maxPixel: 450)
                    .frame(width: posterWidth, height: posterWidth * 1.5)
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                    .shadow(radius: 8)
                VStack(alignment: .leading, spacing: 6) {
                    if item.isEpisode, let show = item.showTitle {
                        Text(show).font(.subheadline).foregroundStyle(.secondary)
                    }
                    Text(item.isEpisode ? [item.episodeCode, item.title].compactMap { $0 }.joined(separator: " · ") : item.title)
                        .font(compact ? .title2.bold() : .largeTitle.bold())
                        .lineLimit(3)
                    HStack(spacing: 8) {
                        if !item.detailMetaLine.isEmpty { Text(item.detailMetaLine) }
                        if let certification = item.certification, !certification.isEmpty {
                            CertificationBadge(text: certification)
                        }
                    }
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    if let genres = item.genres, !genres.isEmpty {
                        Text(genres.joined(separator: " · ")).font(.caption).foregroundStyle(.secondary)
                    }
                    if item.isWatched {
                        Label("Watched", systemImage: "checkmark.circle.fill")
                            .font(.caption)
                            .foregroundStyle(SentinelTheme.green)
                    }
                }
                // Starts where the backdrop ends, so the text never sits on the picture.
                .padding(.top, posterWidth * 1.5 * 0.4 + 8)
                Spacer(minLength: 0)
            }
            .padding(.horizontal)
            // The poster rides up over the backdrop's lower edge.
            .padding(.top, -posterWidth * 1.5 * 0.4)
        }
    }

    // Play / Resume / Start Over / Mark Watched / My List. A show plays its next episode.
    @ViewBuilder
    private func actions(_ detail: MediaItemDetail) -> some View {
        let item = detail.summary
        let playTarget = item.isShow ? detail.nextEpisode : item
        VStack(alignment: .leading, spacing: 8) {
            // Four buttons outgrow an iPhone's width; the ones after Play drop to their icons.
            ViewThatFits(in: .horizontal) {
                actionButtons(detail, playTarget: playTarget, iconOnly: false)
                actionButtons(detail, playTarget: playTarget, iconOnly: true)
            }
            .controlSize(compact ? .regular : .large)
            if let target = playTarget, target.canResume, let remaining = target.remainingLabel {
                Text(remaining).font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    private func actionButtons(_ detail: MediaItemDetail, playTarget: MediaItemSummary?, iconOnly: Bool) -> some View {
        let item = detail.summary
        return HStack(spacing: 10) {
            if let target = playTarget {
                Button {
                    playing = MoviePlayRequest(itemId: target.id, start: nil, overview: item.isShow ? nil : detail.overview)
                } label: {
                    Label(playTitle(target, isShow: item.isShow), systemImage: "play.fill")
                        .labelStyle(.titleAndIcon)
                        .frame(minWidth: compact ? 0 : 120)
                }
                .buttonStyle(.borderedProminent)
                if target.canResume {
                    Button {
                        playing = MoviePlayRequest(itemId: target.id, start: 0, overview: item.isShow ? nil : detail.overview)
                    } label: {
                        Label("Start Over", systemImage: "backward.end.fill")
                    }
                    .buttonStyle(.bordered)
                    .accessibilityLabel("Start Over")
                }
            }
            Button {
                Task { await setWatched(!item.isWatched, on: item.id) }
            } label: {
                Label(item.isWatched ? "Unwatched" : "Watched", systemImage: item.isWatched ? "eye.slash" : "checkmark")
            }
            .buttonStyle(.bordered)
            .disabled(working)
            .accessibilityLabel(item.isWatched ? "Mark as unwatched" : "Mark as watched")
            // Episodes are never on My List; the server saves an episode's show instead.
            if !item.isEpisode {
                Button {
                    Task { await toggleMyList() }
                } label: {
                    Label("My List", systemImage: item.isInList ? "checkmark" : "plus")
                }
                .buttonStyle(.bordered)
                .accessibilityLabel(item.isInList ? "Remove from My List" : "Add to My List")
            }
        }
        .labelStyle(ActionLabelStyle(iconOnly: iconOnly))
    }

    private func playTitle(_ target: MediaItemSummary, isShow: Bool) -> String {
        let verb = target.canResume ? "Resume" : "Play"
        guard isShow, let code = target.episodeCode else { return verb }
        return "\(verb) \(code)"
    }

    @ViewBuilder
    private func seasonSection(_ detail: MediaItemDetail) -> some View {
        let seasons = detail.seasonList
        let selected = seasons.first { $0.number == seasonNumber } ?? defaultSeason(detail)
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                if seasons.count > 1 {
                    Picker("Season", selection: Binding(get: { selected?.number ?? 0 }, set: { seasonNumber = $0 })) {
                        ForEach(seasons) { Text($0.title).tag($0.number) }
                    }
                    .pickerStyle(.menu)
                    .labelsHidden()
                    .fixedSize()
                } else if let selected {
                    Text(selected.title).font(.title3.bold())
                }
                Spacer()
                if let selected {
                    let allWatched = (selected.watchedCount ?? 0) >= (selected.episodeCount ?? selected.episodeList.count)
                    Button {
                        Task { await setSeasonWatched(!allWatched, season: selected.number, showId: detail.id) }
                    } label: {
                        Label(allWatched ? "Mark Season Unwatched" : "Mark Season Watched", systemImage: allWatched ? "eye.slash" : "checkmark")
                            .font(.callout)
                    }
                    .buttonStyle(.borderless)
                    .disabled(working)
                }
            }
            if let selected {
                LazyVStack(alignment: .leading, spacing: 14) {
                    ForEach(selected.episodeList) { episode in
                        episodeRow(episode)
                    }
                }
            }
        }
    }

    private func episodeRow(_ episode: MediaItemSummary) -> some View {
        let stillWidth: CGFloat = compact ? 140 : 200
        return Button {
            playing = MoviePlayRequest(itemId: episode.id, start: nil)
        } label: {
            HStack(alignment: .top, spacing: 12) {
                PosterImage(url: model.client.mediaURL(episode.thumbUrl), title: episode.episodeCode ?? episode.title, maxPixel: 480)
                    .frame(width: stillWidth, height: stillWidth * 9 / 16)
                    .overlay(alignment: .bottom) { MediaProgressBar(fraction: episode.progressFraction) }
                    .overlay {
                        Image(systemName: "play.circle.fill")
                            .font(.title)
                            .foregroundStyle(.white.opacity(0.85))
                            .shadow(radius: 4)
                    }
                    .clipShape(RoundedRectangle(cornerRadius: 6))
                VStack(alignment: .leading, spacing: 3) {
                    Text(episode.episodeRowTitle)
                        .font(.subheadline.weight(.semibold))
                        .lineLimit(2)
                    if let line = episode.remainingLabel ?? episode.runtimeLabel {
                        Text(line).font(.caption).foregroundStyle(.secondary)
                    }
                    if let airDate = episode.airDate, !airDate.isEmpty {
                        Text(airDate).font(.caption2).foregroundStyle(.tertiary)
                    }
                }
                Spacer(minLength: 0)
                if episode.isWatched {
                    Image(systemName: "checkmark.circle.fill")
                        .foregroundStyle(SentinelTheme.green)
                        .accessibilityLabel("Watched")
                }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .contextMenu {
            Button("Play", systemImage: "play.fill") { playing = MoviePlayRequest(itemId: episode.id, start: nil) }
            if episode.canResume {
                Button("Start Over", systemImage: "backward.end.fill") { playing = MoviePlayRequest(itemId: episode.id, start: 0) }
            }
            Button(episode.isWatched ? "Mark as Unwatched" : "Mark as Watched", systemImage: episode.isWatched ? "eye.slash" : "checkmark") {
                Task { await setWatched(!episode.isWatched, on: episode.id) }
            }
        }
    }

    private func castSection(_ cast: [MediaCastMember]) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Cast").font(.headline)
            ScrollView(.horizontal, showsIndicators: false) {
                LazyHStack(alignment: .top, spacing: 10) {
                    ForEach(Array(cast.prefix(30).enumerated()), id: \.offset) { _, member in
                        VStack(alignment: .leading, spacing: 2) {
                            Text(member.name).font(.caption.weight(.semibold)).lineLimit(1)
                            if let character = member.character, !character.isEmpty {
                                Text(character).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                            }
                        }
                        .frame(width: 130, alignment: .leading)
                        .padding(10)
                        .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 8))
                    }
                }
            }
        }
    }

    private func fileSection(_ file: MediaFileInfo) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("File").font(.headline)
            if let line = file.summaryLine {
                Text(line).font(.caption).foregroundStyle(.secondary)
            }
            if let size = file.sizeBytes, size > 0 {
                Text(ByteCountFormatter.string(fromByteCount: size, countStyle: .file) + (file.container.map { " · \($0.uppercased())" } ?? ""))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            // Only an Admin is sent the path.
            if let path = file.path, !path.isEmpty {
                Text(path)
                    .font(.caption2.monospaced())
                    .foregroundStyle(.tertiary)
                    .textSelection(.enabled)
            }
        }
    }

    // The season holding the episode to play next, else the first real season.
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

    // Flips the button at once and puts it back if the server says no.
    private func toggleMyList() async {
        guard let current = detail?.summary.isInList else { return }
        detail?.summary.inList = !current
        do {
            let result = try await model.client.setMediaInList(id: route.id, inList: !current)
            detail?.summary.inList = result.inList
        } catch {
            detail?.summary.inList = current
            if error is CancellationError || (error as? URLError)?.code == .cancelled { return }
            listError = error.localizedDescription
        }
    }

    private func deleteMovie() async {
        working = true
        defer { working = false }
        do {
            try await model.client.deleteMediaItem(id: route.id)
            NotificationCenter.default.post(name: .mediaItemDeleted, object: route.id)
            dismiss()
        } catch {
            if error is CancellationError || (error as? URLError)?.code == .cancelled { return }
            deleteError = error.localizedDescription
        }
    }

    private func setSeasonWatched(_ watched: Bool, season: Int, showId: String) async {
        working = true
        defer { working = false }
        try? await model.client.setMediaSeasonWatched(showId: showId, season: season, watched: watched)
        await load()
    }
}

// The detail's buttons: icon and title, or just the icon when the row is short of room. Play
// keeps its title either way - it is the button the page is for.
private struct ActionLabelStyle: LabelStyle {
    let iconOnly: Bool

    @ViewBuilder
    func makeBody(configuration: Configuration) -> some View {
        if iconOnly {
            configuration.icon
        } else {
            Label(configuration)
        }
    }
}
