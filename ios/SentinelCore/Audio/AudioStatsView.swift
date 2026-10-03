import Charts
import SwiftUI

// Listening stats (§9 Stats) for the week, month, year or all time, and the year as a
// swipeable "Wrapped" story.
struct AudioStatsView: View {
    @Environment(\.dismiss) private var dismiss
    @State private var period = "month"
    @State private var year = Calendar.current.component(.year, from: Date())
    @State private var stats: AudioStats?
    @State private var loading = false
    @State private var errorMessage: String?
    @State private var showingWrapped = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                Picker("Period", selection: $period) {
                    Text("Week").tag("week")
                    Text("Month").tag("month")
                    Text("Year").tag("year")
                    Text("All Time").tag("all")
                }
                .pickerStyle(.segmented)
                if period == "year" {
                    Stepper("\(String(year))", value: $year, in: 2015...Calendar.current.component(.year, from: Date()))
                }
                Button {
                    showingWrapped = true
                } label: {
                    HStack {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("Your \(String(year)) Wrapped").font(.title3.bold())
                            Text("The year in music and podcasts, as a story").font(.caption).opacity(0.85)
                        }
                        Spacer()
                        Image(systemName: "sparkles").font(.title)
                    }
                    .foregroundStyle(.white)
                    .padding()
                    .background(LinearGradient(colors: [.pink, .purple, .indigo], startPoint: .topLeading, endPoint: .bottomTrailing),
                                in: RoundedRectangle(cornerRadius: 16))
                }
                .buttonStyle(.plain)

                if let stats {
                    if let music = stats.music { musicSection(music) }
                    if let podcasts = stats.podcasts { podcastSection(podcasts) }
                } else {
                    AudioLoadState(loading: loading, error: errorMessage)
                }
            }
            .padding()
            .frame(maxWidth: 820)
            .frame(maxWidth: .infinity)
        }
        .navigationTitle("Listening Stats")
        .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } } }
        .task(id: "\(period)|\(year)") { await load() }
        #if os(iOS)
        .fullScreenCover(isPresented: $showingWrapped) { WrappedView(year: year) }
        #else
        .sheet(isPresented: $showingWrapped) { WrappedView(year: year).frame(minWidth: 520, minHeight: 760) }
        #endif
    }

    private func load() async {
        loading = true
        defer { loading = false }
        do {
            stats = try await AppModel.shared.client.audioStats(period: period, year: period == "year" ? year : nil)
            errorMessage = nil
        } catch {
            if !error.isCancellation { errorMessage = error.localizedDescription }
        }
    }

    private func musicSection(_ music: MusicStats) -> some View {
        VStack(alignment: .leading, spacing: 16) {
            Label("Music", systemImage: "music.note").font(.title2.bold())
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 140), spacing: 12)], spacing: 12) {
                StatTile(value: hours(music.minutes), label: "listened")
                StatTile(value: "\(music.plays ?? 0)", label: "plays")
                StatTile(value: "\(music.distinctArtists ?? 0)", label: "artists · \(music.newArtists ?? 0) new")
                StatTile(value: "\(music.longestStreakDays ?? 0)", label: "day streak")
            }
            if let artists = music.topArtists, !artists.isEmpty {
                VStack(alignment: .leading, spacing: 8) {
                    Text("Top Artists").font(.headline)
                    Chart(artists.prefix(8), id: \.artist.id) { entry in
                        BarMark(x: .value("Plays", entry.plays ?? 0), y: .value("Artist", entry.artist.name))
                            .foregroundStyle(SentinelTheme.green.gradient)
                            .annotation(position: .trailing) { Text("\(entry.plays ?? 0)").font(.caption2).foregroundStyle(.secondary) }
                    }
                    .chartXAxis(.hidden)
                    .frame(height: CGFloat(min(artists.count, 8)) * 30 + 10)
                }
            }
            if let tracks = music.topTracks, !tracks.isEmpty {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Top Songs").font(.headline)
                    ForEach(Array(tracks.prefix(10).enumerated()), id: \.offset) { offset, entry in
                        HStack {
                            Text("\(offset + 1)").font(.headline.monospacedDigit()).foregroundStyle(.secondary).frame(width: 24)
                            TrackRow(track: entry.track) {
                                AudioPlayer.shared.play(tracks: tracks.map(\.track), startAt: offset,
                                                        context: AudioPlayContext(type: "queue", id: "top", name: "Top Songs"))
                            }
                        }
                    }
                }
            }
            if let albums = music.topAlbums, !albums.isEmpty {
                VStack(alignment: .leading, spacing: 8) {
                    Text("Top Albums").font(.headline)
                    ScrollView(.horizontal, showsIndicators: false) {
                        HStack(alignment: .top, spacing: 14) {
                            ForEach(albums, id: \.album.id) { AlbumTile(album: $0.album, width: 120) }
                        }
                    }
                }
            }
            if let genres = music.topGenres, !genres.isEmpty {
                VStack(alignment: .leading, spacing: 8) {
                    Text("Genres").font(.headline)
                    Chart(genres.prefix(8), id: \.genre) { entry in
                        SectorMark(angle: .value("Minutes", entry.minutes ?? 0), innerRadius: .ratio(0.55), angularInset: 1.5)
                            .foregroundStyle(by: .value("Genre", entry.genre))
                    }
                    .frame(height: 220)
                }
            }
            clockCharts(byHour: music.byHour, byWeekday: music.byWeekday, tint: SentinelTheme.green)
        }
    }

    private func podcastSection(_ podcasts: PodcastStats) -> some View {
        VStack(alignment: .leading, spacing: 16) {
            Label("Podcasts", systemImage: "mic").font(.title2.bold())
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 140), spacing: 12)], spacing: 12) {
                StatTile(value: hours(podcasts.minutes), label: "listened")
                StatTile(value: "\(podcasts.episodesFinished ?? 0)", label: "episodes finished")
                StatTile(value: hours(podcasts.timeSavedMinutes), label: "saved by speed & trims")
            }
            if let shows = podcasts.topShows, !shows.isEmpty {
                VStack(alignment: .leading, spacing: 8) {
                    Text("Top Shows").font(.headline)
                    ForEach(shows, id: \.podcast.id) { entry in
                        HStack(spacing: 12) {
                            ArtworkImage(path: entry.podcast.artUrl, size: 44, corner: 6, placeholder: "mic")
                            VStack(alignment: .leading) {
                                Text(entry.podcast.title)
                                Text("\(hours(entry.minutes)) · \(entry.episodes ?? 0) episodes").font(.caption).foregroundStyle(.secondary)
                            }
                        }
                    }
                }
            }
            clockCharts(byHour: podcasts.byHour, byWeekday: podcasts.byWeekday, tint: .orange)
        }
    }

    @ViewBuilder
    private func clockCharts(byHour: [Double]?, byWeekday: [Double]?, tint: Color) -> some View {
        if let byHour, byHour.count == 24, byHour.contains(where: { $0 > 0 }) {
            VStack(alignment: .leading, spacing: 8) {
                Text("When You Listen").font(.headline)
                Chart(Array(byHour.enumerated()), id: \.offset) { hour, minutes in
                    BarMark(x: .value("Hour", hour), y: .value("Minutes", minutes))
                        .foregroundStyle(tint.gradient)
                }
                .chartXAxis {
                    AxisMarks(values: [0, 6, 12, 18]) { value in
                        AxisValueLabel { Text(hourLabel(value.as(Int.self) ?? 0)) }
                        AxisGridLine()
                    }
                }
                .frame(height: 140)
            }
        }
        if let byWeekday, byWeekday.count == 7, byWeekday.contains(where: { $0 > 0 }) {
            let names = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]
            Chart(Array(byWeekday.enumerated()), id: \.offset) { day, minutes in
                BarMark(x: .value("Day", names[day]), y: .value("Minutes", minutes))
                    .foregroundStyle(tint.opacity(0.7).gradient)
            }
            .frame(height: 120)
        }
    }
}

private func hours(_ minutes: Double?) -> String {
    let m = minutes ?? 0
    if m >= 120 { return "\(Int((m / 60).rounded())) hr" }
    return "\(Int(m)) min"
}

private func hourLabel(_ hour: Int) -> String {
    switch hour {
    case 0: "12a"
    case 12: "12p"
    default: hour < 12 ? "\(hour)a" : "\(hour - 12)p"
    }
}

private struct StatTile: View {
    let value: String
    let label: String

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(value).font(.title.bold()).minimumScaleFactor(0.6).lineLimit(1)
            Text(label).font(.caption).foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(12)
        .background(.quaternary.opacity(0.6), in: RoundedRectangle(cornerRadius: 12))
    }
}

// MARK: - Wrapped

// The year, one card at a time: swipe (or click the arrows on a Mac) through big numbers, the
// top artist, songs, genre, your listening clock, the streak and the podcast side.
struct WrappedView: View {
    let year: Int
    @Environment(\.dismiss) private var dismiss
    @State private var stats: AudioStats?
    @State private var page = 0
    @State private var errorMessage: String?

    private var cards: [WrappedCard] {
        guard let stats else { return [] }
        var cards: [WrappedCard] = [.intro]
        if let music = stats.music, (music.plays ?? 0) > 0 {
            cards.append(.minutes(music))
            if let top = music.topArtists?.first { cards.append(.topArtist(top, music.topArtists ?? [])) }
            if let songs = music.topTracks, !songs.isEmpty { cards.append(.topSongs(songs)) }
            if let genre = music.topGenres?.first { cards.append(.genre(genre, music.topGenres ?? [])) }
            if let byHour = music.byHour, byHour.count == 24 { cards.append(.clock(byHour)) }
            if (music.longestStreakDays ?? 0) > 1 || music.firstPlay != nil { cards.append(.streak(music)) }
        }
        if let podcasts = stats.podcasts, (podcasts.minutes ?? 0) > 0 { cards.append(.podcasts(podcasts)) }
        cards.append(.outro)
        return cards
    }

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            if stats == nil {
                if let errorMessage {
                    Text(errorMessage).foregroundStyle(.white).padding()
                } else {
                    ProgressView().tint(.white)
                }
            } else {
                #if os(iOS)
                TabView(selection: $page) {
                    ForEach(Array(cards.enumerated()), id: \.offset) { index, card in
                        WrappedCardView(card: card, year: year).tag(index)
                    }
                }
                .tabViewStyle(.page(indexDisplayMode: .always))
                .ignoresSafeArea()
                #else
                let all = cards
                WrappedCardView(card: all[min(page, all.count - 1)], year: year)
                    .id(page)
                    .transition(.asymmetric(insertion: .move(edge: .trailing), removal: .move(edge: .leading)))
                    .overlay(alignment: .bottom) {
                        HStack {
                            Button { withAnimation { page = max(0, page - 1) } } label: { Image(systemName: "chevron.left.circle.fill").font(.largeTitle) }
                                .keyboardShortcut(.leftArrow, modifiers: [])
                                .disabled(page == 0)
                            Spacer()
                            Text("\(page + 1) of \(all.count)").foregroundStyle(.white.opacity(0.7))
                            Spacer()
                            Button { withAnimation { page = min(all.count - 1, page + 1) } } label: { Image(systemName: "chevron.right.circle.fill").font(.largeTitle) }
                                .keyboardShortcut(.rightArrow, modifiers: [])
                                .disabled(page >= all.count - 1)
                        }
                        .buttonStyle(.plain)
                        .foregroundStyle(.white)
                        .padding(24)
                    }
                #endif
            }
        }
        .overlay(alignment: .topTrailing) {
            Button { dismiss() } label: {
                Image(systemName: "xmark.circle.fill").font(.title).symbolRenderingMode(.hierarchical).foregroundStyle(.white)
            }
            .buttonStyle(.plain)
            .padding()
            .accessibilityLabel("Close")
        }
        .environment(\.colorScheme, .dark)
        .task {
            do { stats = try await AppModel.shared.client.audioStats(period: "year", year: year) }
            catch { errorMessage = error.localizedDescription }
        }
    }
}

enum WrappedCard {
    case intro
    case minutes(MusicStats)
    case topArtist(StatsArtistEntry, [StatsArtistEntry])
    case topSongs([StatsTrackEntry])
    case genre(StatsGenreEntry, [StatsGenreEntry])
    case clock([Double])
    case streak(MusicStats)
    case podcasts(PodcastStats)
    case outro
}

private struct WrappedCardView: View {
    let card: WrappedCard
    let year: Int
    @State private var appeared = false

    var body: some View {
        ZStack {
            LinearGradient(colors: colors, startPoint: .topLeading, endPoint: .bottomTrailing).ignoresSafeArea()
            content
                .foregroundStyle(.white)
                .padding(32)
                .frame(maxWidth: 560)
                .scaleEffect(appeared ? 1 : 0.92)
                .opacity(appeared ? 1 : 0)
        }
        .onAppear { withAnimation(.spring(response: 0.6, dampingFraction: 0.8)) { appeared = true } }
        .onDisappear { appeared = false }
    }

    private var colors: [Color] {
        switch card {
        case .intro: [.purple, .indigo]
        case .minutes: [.green, .teal]
        case .topArtist: [.pink, .orange]
        case .topSongs: [.indigo, .blue]
        case .genre: [.orange, .red]
        case .clock: [.teal, .indigo]
        case .streak: [.red, .purple]
        case .podcasts: [.orange, .pink]
        case .outro: [.purple, .pink]
        }
    }

    @ViewBuilder
    private var content: some View {
        switch card {
        case .intro:
            VStack(spacing: 16) {
                Image(systemName: "sparkles").font(.system(size: 64))
                Text("Your \(String(year))").font(.system(size: 48, weight: .black))
                Text("Wrapped").font(.system(size: 40, weight: .heavy)).opacity(0.85)
                Text("Swipe to see what you listened to").font(.headline).opacity(0.8).padding(.top)
            }
        case .minutes(let music):
            VStack(alignment: .leading, spacing: 12) {
                Text("You listened to music for").font(.title2.weight(.semibold))
                Text("\(Int(music.minutes ?? 0).formatted())").font(.system(size: 76, weight: .black)).minimumScaleFactor(0.5)
                Text("minutes").font(.title.bold())
                Text("That's \(music.plays ?? 0) plays of \(music.distinctTracks ?? 0) different songs by \(music.distinctArtists ?? 0) artists - \(music.newArtists ?? 0) of them new to you.")
                    .font(.title3).opacity(0.9)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        case .topArtist(let top, let all):
            VStack(spacing: 18) {
                Text("Your top artist").font(.title2.weight(.semibold))
                ArtworkImage(path: top.artist.artUrl, size: 200, placeholder: "music.mic", circle: true)
                    .shadow(radius: 20)
                Text(top.artist.name).font(.system(size: 40, weight: .black)).multilineTextAlignment(.center)
                Text("\(top.plays ?? 0) plays · \(Int(top.minutes ?? 0)) minutes").font(.headline).opacity(0.9)
                VStack(alignment: .leading, spacing: 4) {
                    ForEach(Array(all.dropFirst().prefix(4).enumerated()), id: \.offset) { offset, entry in
                        Text("\(offset + 2). \(entry.artist.name)").font(.title3.weight(.semibold))
                    }
                }
                .opacity(0.85)
            }
        case .topSongs(let songs):
            VStack(alignment: .leading, spacing: 14) {
                Text("Your top songs").font(.title.bold())
                ForEach(Array(songs.prefix(5).enumerated()), id: \.offset) { offset, entry in
                    HStack(spacing: 14) {
                        Text("\(offset + 1)").font(.title.weight(.black)).frame(width: 34)
                        ArtworkImage(path: entry.track.artUrl, size: 56, corner: 6)
                        VStack(alignment: .leading) {
                            Text(entry.track.title).font(.headline).lineLimit(1)
                            Text("\(entry.track.artistLine) · \(entry.plays ?? 0) plays").font(.subheadline).opacity(0.8).lineLimit(1)
                        }
                    }
                }
                Button {
                    AudioPlayer.shared.play(tracks: songs.map(\.track), context: AudioPlayContext(type: "queue", id: "wrapped", name: "Your Top Songs \(year)"))
                } label: {
                    Label("Play Them", systemImage: "play.fill").font(.headline)
                        .padding(.horizontal, 18).padding(.vertical, 10)
                        .background(.white.opacity(0.25), in: Capsule())
                }
                .buttonStyle(.plain)
                .padding(.top)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        case .genre(let top, let all):
            VStack(spacing: 16) {
                Text("Your sound was").font(.title2.weight(.semibold))
                Text(top.genre).font(.system(size: 56, weight: .black)).minimumScaleFactor(0.5)
                Chart(all.prefix(6), id: \.genre) { entry in
                    SectorMark(angle: .value("Minutes", entry.minutes ?? 0), innerRadius: .ratio(0.6), angularInset: 2)
                        .foregroundStyle(by: .value("Genre", entry.genre))
                }
                .chartLegend(position: .bottom)
                .frame(height: 260)
            }
        case .clock(let byHour):
            let peak = byHour.enumerated().max { $0.element < $1.element }?.offset ?? 0
            VStack(spacing: 16) {
                Text("You listen most around").font(.title2.weight(.semibold))
                Text(hourLabel(peak).replacingOccurrences(of: "a", with: " am").replacingOccurrences(of: "p", with: " pm"))
                    .font(.system(size: 64, weight: .black))
                Chart(Array(byHour.enumerated()), id: \.offset) { hour, minutes in
                    BarMark(x: .value("Hour", hour), y: .value("Minutes", minutes))
                        .foregroundStyle(hour == peak ? Color.white : Color.white.opacity(0.45))
                }
                .chartYAxis(.hidden)
                .chartXAxis {
                    AxisMarks(values: [0, 6, 12, 18]) { value in AxisValueLabel { Text(hourLabel(value.as(Int.self) ?? 0)).foregroundStyle(.white) } }
                }
                .frame(height: 180)
            }
        case .streak(let music):
            VStack(spacing: 16) {
                if let days = music.longestStreakDays, days > 1 {
                    Text("Your longest streak").font(.title2.weight(.semibold))
                    Text("\(days)").font(.system(size: 96, weight: .black))
                    Text("days in a row").font(.title.bold())
                }
                if let first = music.firstPlay {
                    VStack(spacing: 6) {
                        Text("Your first song of the year").font(.headline).opacity(0.85)
                        Text("\(first.track.title) · \(first.track.artistLine)").font(.title3.bold()).multilineTextAlignment(.center)
                        if let date = AudioDate.parse(first.playedAt) {
                            Text(date.formatted(date: .long, time: .shortened)).font(.subheadline).opacity(0.8)
                        }
                    }
                    .padding(.top, 24)
                }
            }
        case .podcasts(let podcasts):
            VStack(alignment: .leading, spacing: 14) {
                Text("And podcasts…").font(.title2.weight(.semibold))
                Text("\(Int((podcasts.minutes ?? 0) / 60)) hours").font(.system(size: 64, weight: .black))
                Text("\(podcasts.episodesFinished ?? 0) episodes finished. Faster playback and trimmed silences saved you \(Int(podcasts.timeSavedMinutes ?? 0)) minutes.")
                    .font(.title3)
                if let top = podcasts.topShows?.first {
                    HStack(spacing: 14) {
                        ArtworkImage(path: top.podcast.artUrl, size: 80, corner: 10, placeholder: "mic")
                        VStack(alignment: .leading) {
                            Text("Top show").font(.subheadline).opacity(0.8)
                            Text(top.podcast.title).font(.title2.bold())
                        }
                    }
                    .padding(.top)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        case .outro:
            VStack(spacing: 16) {
                Image(systemName: "heart.fill").font(.system(size: 64))
                Text("That was your \(String(year))").font(.system(size: 40, weight: .black)).multilineTextAlignment(.center)
                Text("Here's to the next one.").font(.title3).opacity(0.9)
            }
        }
    }
}
