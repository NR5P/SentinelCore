import SwiftUI

// Listening stats as a big-screen story (core/docs/MUSIC_AND_PODCASTS.md §9 Stats): full-width
// cards to swipe through - the year's minutes, top artist, songs, albums, genres, when you
// listen, streaks and firsts, and podcasts. "This Year" is the Wrapped.
struct StatsTVView: View {
    enum Period: String, CaseIterable, Identifiable {
        case week, month, year, all
        var id: Self { self }
        var title: String {
            switch self {
            case .week: "This Week"
            case .month: "This Month"
            case .year: "This Year"
            case .all: "All Time"
            }
        }
    }

    @EnvironmentObject private var audio: AudioCenter
    @State private var period: Period = .year
    @State private var year = Calendar.current.component(.year, from: Date())
    @State private var stats: AudioStats?
    @State private var errorMessage: String?
    @State private var loading = false

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            HStack(spacing: 30) {
                Picker("Period", selection: $period) {
                    ForEach(Period.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                .frame(maxWidth: 900)
                if period == .year {
                    Menu {
                        let now = Calendar.current.component(.year, from: Date())
                        ForEach((now - 5...now).reversed(), id: \.self) { y in Button(String(y)) { year = y } }
                    } label: {
                        Label(String(year), systemImage: "calendar").audioFocusLabel()
                    }
                }
                Spacer()
            }
            .padding(.horizontal, 60)
            ScrollView(.horizontal) {
                LazyHStack(spacing: 50) {
                    if let stats {
                        ForEach(cards(stats), id: \.0) { _, card in
                            card
                                .frame(width: 1400, height: 590)
                                .background(.black.opacity(0.001))
                                .focusable()
                        }
                    }
                }
                .scrollTargetLayout()
                .padding(.horizontal, 150)
                .padding(.vertical, 20)
            }
            .scrollTargetBehavior(.viewAligned)
            .scrollClipDisabled()
        }

        .overlay { AudioStateOverlay(loading: loading, error: errorMessage, isEmpty: stats == nil) }
        .task(id: "\(period.rawValue)|\(year)") {
            guard let api = audio.api else { return }
            loading = true
            defer { loading = false }
            do {
                stats = try await api.stats(period: period.rawValue, year: period == .year ? year : nil)
                errorMessage = nil
            } catch {
                if !AudioAPI.isCancellation(error) { errorMessage = error.localizedDescription }
            }
        }
    }

    private var periodPhrase: String {
        switch period {
        case .week: "this week"
        case .month: "this month"
        case .year: "in \(year)"
        case .all: "all time"
        }
    }

    private func cards(_ stats: AudioStats) -> [(String, AnyView)] {
        var cards: [(String, AnyView)] = []
        let music = stats.music
        let pods = stats.podcasts
        let totalMinutes = Int((music?.minutes ?? 0) + (pods?.minutes ?? 0))
        cards.append(("intro", AnyView(StoryCard(colors: [.purple, .indigo]) {
            VStack(spacing: 24) {
                Text(period == .year ? "Your \(String(year)) Wrapped" : "Your listening \(periodPhrase)").font(.title2).opacity(0.8)
                Text(totalMinutes.formatted()).font(.system(size: 130, weight: .black)).minimumScaleFactor(0.5)
                Text("minutes of music and podcasts").font(.title3)
                HStack(spacing: 60) {
                    BigStat(value: (music?.plays ?? 0).formatted(), label: "plays")
                    BigStat(value: (music?.distinctTracks ?? 0).formatted(), label: "different songs")
                    BigStat(value: (music?.distinctArtists ?? 0).formatted(), label: "artists")
                    BigStat(value: (pods?.episodesFinished ?? 0).formatted(), label: "episodes finished")
                }
                .padding(.top, 10)
            }
        })))
        if let top = music?.topArtists, let first = top.first {
            cards.append(("artist", AnyView(StoryCard(colors: [.pink, .orange]) {
                HStack(spacing: 70) {
                    AudioArt(path: first.artist.artUrl ?? "/api/music/artists/\(first.artist.id)/art", title: first.artist.name, size: 800)
                        .frame(width: 380, height: 380).clipShape(Circle()).shadow(radius: 30)
                    VStack(alignment: .leading, spacing: 16) {
                        Text("Your top artist").font(.title3).opacity(0.8)
                        Text(first.artist.name).font(.system(size: 80, weight: .black)).lineLimit(2).minimumScaleFactor(0.5)
                        Text("\(first.plays ?? 0) plays · \(Int(first.minutes ?? 0)) minutes").font(.title3)
                        VStack(alignment: .leading, spacing: 8) {
                            ForEach(Array(top.dropFirst().prefix(4).enumerated()), id: \.offset) { i, item in
                                Text("\(i + 2). \(item.artist.name)").font(.title3.weight(.semibold)).opacity(0.85)
                            }
                        }
                        .padding(.top, 20)
                    }
                }
            })))
        }
        if let tracks = music?.topTracks, !tracks.isEmpty {
            cards.append(("tracks", AnyView(StoryCard(colors: [.teal, .blue]) {
                VStack(alignment: .leading, spacing: 18) {
                    Text("Your top songs").font(.title2.bold())
                    ForEach(Array(tracks.prefix(4).enumerated()), id: \.offset) { i, item in
                        HStack(spacing: 24) {
                            Text("\(i + 1)").font(.system(size: 50, weight: .black)).frame(width: 70)
                            AudioArt(path: item.track.artUrl, title: item.track.title, size: 200)
                                .frame(width: 84, height: 84).clipShape(RoundedRectangle(cornerRadius: 8))
                            VStack(alignment: .leading) {
                                Text(item.track.title).font(.title3.bold()).lineLimit(1)
                                Text(item.track.artistLine).opacity(0.8)
                            }
                            Spacer()
                            Text("\(item.plays ?? 0) plays").opacity(0.8)
                        }
                    }
                }
            })))
        }
        if let albums = music?.topAlbums, !albums.isEmpty {
            cards.append(("albums", AnyView(StoryCard(colors: [.green, .teal]) {
                VStack(spacing: 30) {
                    Text("Albums on repeat").font(.title2.bold())
                    HStack(spacing: 30) {
                        ForEach(Array(albums.prefix(5).enumerated()), id: \.offset) { _, item in
                            VStack(spacing: 10) {
                                AudioArt(path: item.album.artUrl, title: item.album.title, size: 500)
                                    .frame(width: 240, height: 240).clipShape(RoundedRectangle(cornerRadius: 12)).shadow(radius: 16)
                                Text(item.album.title).font(.caption.bold()).lineLimit(1).frame(width: 240)
                                Text("\(item.plays ?? 0) plays").font(.caption2).opacity(0.8)
                            }
                        }
                    }
                }
            })))
        }
        if let genres = music?.topGenres, !genres.isEmpty {
            let maxMinutes = genres.map { $0.minutes ?? 0 }.max() ?? 1
            cards.append(("genres", AnyView(StoryCard(colors: [.orange, .red]) {
                VStack(alignment: .leading, spacing: 16) {
                    Text("Your sound").font(.title2.bold())
                    ForEach(Array(genres.prefix(6).enumerated()), id: \.offset) { _, genre in
                        HStack(spacing: 20) {
                            Text(genre.genre).font(.title3.weight(.semibold)).frame(width: 300, alignment: .leading)
                            GeometryReader { geo in
                                Capsule().fill(.white.opacity(0.85))
                                    .frame(width: max(20, geo.size.width * (genre.minutes ?? 0) / max(1, maxMinutes)))
                            }
                            .frame(height: 30)
                            Text("\(Int(genre.minutes ?? 0)) min").frame(width: 160, alignment: .trailing)
                        }
                    }
                }
            })))
        }
        let hours = zip(music?.byHour ?? Array(repeating: 0, count: 24), pods?.byHour ?? Array(repeating: 0, count: 24)).map { $0 + $1 }
        if hours.contains(where: { $0 > 0 }) {
            let days = zip(music?.byWeekday ?? Array(repeating: 0, count: 7), pods?.byWeekday ?? Array(repeating: 0, count: 7)).map { $0 + $1 }
            cards.append(("when", AnyView(StoryCard(colors: [.indigo, .black]) {
                VStack(alignment: .leading, spacing: 30) {
                    Text("When you listen").font(.title2.bold())
                    if let peak = hours.indices.max(by: { hours[$0] < hours[$1] }) {
                        Text("Your loudest hour is \(Self.hourLabel(peak)).").font(.title3).opacity(0.85)
                    }
                    BarChart(values: hours, labels: (0..<24).map { $0 % 6 == 0 ? Self.hourLabel($0) : "" })
                        .frame(height: 180)
                    BarChart(values: days, labels: ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"])
                        .frame(height: 110)
                }
            })))
        }
        if let music, (music.longestStreakDays ?? 0) > 0 || music.firstPlay != nil || (music.newArtists ?? 0) > 0 {
            cards.append(("firsts", AnyView(StoryCard(colors: [.mint, .cyan]) {
                HStack(spacing: 80) {
                    BigStat(value: "\(music.longestStreakDays ?? 0)", label: "days in a row - your longest streak")
                    BigStat(value: "\(music.newArtists ?? 0)", label: "new artists discovered")
                    BigStat(value: "\(music.skips ?? 0)", label: "songs skipped (no judgement)")
                    if let first = music.firstPlay {
                        VStack(spacing: 14) {
                            Text("It started with").font(.title3).opacity(0.8)
                            AudioArt(path: first.track.artUrl, title: first.track.title, size: 400)
                                .frame(width: 200, height: 200).clipShape(RoundedRectangle(cornerRadius: 12))
                            Text(first.track.title).font(.title3.bold()).lineLimit(1)
                            Text(first.track.artistLine).opacity(0.8)
                        }
                        .frame(width: 320)
                    }
                }
            })))
        }
        if let pods, (pods.minutes ?? 0) > 0 {
            cards.append(("podcasts", AnyView(StoryCard(colors: [.purple, .pink]) {
                VStack(spacing: 30) {
                    HStack(spacing: 70) {
                        BigStat(value: Int(pods.minutes ?? 0).formatted(), label: "podcast minutes")
                        BigStat(value: (pods.episodesFinished ?? 0).formatted(), label: "episodes finished")
                        BigStat(value: Int(pods.timeSavedMinutes ?? 0).formatted(), label: "minutes saved by speed and trimming")
                    }
                    HStack(spacing: 30) {
                        ForEach(Array((pods.topShows ?? []).prefix(5).enumerated()), id: \.offset) { _, show in
                            VStack(spacing: 8) {
                                AudioArt(path: show.podcast.artUrl, title: show.podcast.title, size: 400)
                                    .frame(width: 200, height: 200).clipShape(RoundedRectangle(cornerRadius: 12))
                                Text(show.podcast.title).font(.caption.bold()).lineLimit(1).frame(width: 200)
                                Text("\(Int(show.minutes ?? 0)) min").font(.caption2).opacity(0.8)
                            }
                        }
                    }
                }
            })))
        }
        return cards
    }

    private static func hourLabel(_ hour: Int) -> String {
        let h = hour % 12 == 0 ? 12 : hour % 12
        return "\(h) \(hour < 12 ? "am" : "pm")"
    }
}

private struct StoryCard<Content: View>: View {
    let colors: [Color]
    @ViewBuilder let content: Content

    var body: some View {
        ZStack {
            LinearGradient(colors: colors, startPoint: .topLeading, endPoint: .bottomTrailing)
            content.padding(50).foregroundStyle(.white)
        }
        .clipShape(RoundedRectangle(cornerRadius: 40))
        .shadow(color: .black.opacity(0.4), radius: 30, y: 16)
    }
}

private struct BigStat: View {
    let value: String
    let label: String

    var body: some View {
        VStack(spacing: 6) {
            Text(value).font(.system(size: 72, weight: .black)).minimumScaleFactor(0.5).lineLimit(1)
            Text(label).font(.callout).opacity(0.85).multilineTextAlignment(.center)
        }
        .frame(maxWidth: 320)
    }
}

private struct BarChart: View {
    let values: [Double]
    let labels: [String]

    var body: some View {
        let top = max(1, values.max() ?? 1)
        HStack(alignment: .bottom, spacing: 6) {
            ForEach(values.indices, id: \.self) { i in
                VStack(spacing: 6) {
                    GeometryReader { geo in
                        VStack {
                            Spacer(minLength: 0)
                            RoundedRectangle(cornerRadius: 4)
                                .fill(.white.opacity(values[i] == top ? 1 : 0.6))
                                .frame(height: max(3, geo.size.height * values[i] / top))
                        }
                    }
                    Text(labels[safe: i] ?? "").font(.caption2).opacity(0.8).lineLimit(1).fixedSize()
                }
            }
        }
    }
}
