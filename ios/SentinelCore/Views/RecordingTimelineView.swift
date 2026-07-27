import SwiftUI

struct RecordingTimelineView: View {
    @EnvironmentObject private var model: AppModel
    @State private var selectedCamera = ""
    @State private var availableCameras: [String] = []
    @State private var selectedDate = Calendar.current.startOfDay(for: .now)
    @State private var day: RecordingTimelineDay?
    @State private var cursor = Date.now
    @State private var isPlaying = false
    @State private var selectedCategories = Set(["person", "vehicle", "animal", "package", "plate", "other"])
    @State private var selectedSeverities = Set(["critical", "high", "medium", "low"])
    @State private var reviewFilter = ReviewFilter.all
    @State private var errorMessage: String?

    private var cameraNames: [String] {
        Array(Set(availableCameras + model.events.map(\.camera))).sorted {
            $0.localizedCaseInsensitiveCompare($1) == .orderedAscending
        }
    }

    private var filteredEvents: [TimelineSecurityEvent] {
        (day?.events ?? []).filter {
            selectedCategories.contains($0.category) &&
            selectedSeverities.contains($0.severity) &&
            reviewFilter.includes($0.reviewed)
        }
    }

    var body: some View {
        ScrollView {
            VStack(spacing: 14) {
                controls
                playbackPanel
                filterBar
                eventList
            }
            .padding()
            .frame(maxWidth: 1100)
            .frame(maxWidth: .infinity)
        }
        .background(SentinelTheme.background)
        .navigationTitle("DAY_TIMELINE")
        .task {
            availableCameras = (try? await model.client.timelineCameras()) ?? []
            if selectedCamera.isEmpty { selectedCamera = cameraNames.first ?? "" }
        }
        .task(id: "\(selectedCamera)|\(selectedDate.timeIntervalSinceReferenceDate)") {
            await loadDay()
        }
        .task(id: isPlaying) {
            guard isPlaying else { return }
            while !Task.isCancelled && isPlaying {
                try? await Task.sleep(for: .milliseconds(500))
                advancePlayback()
            }
        }
    }

    private var controls: some View {
        VStack(spacing: 10) {
            HStack {
                Picker("CAMERA", selection: $selectedCamera) {
                    if cameraNames.isEmpty {
                        Text("NO EVENT CAMERAS").tag("")
                    }
                    ForEach(cameraNames, id: \.self) { Text($0).tag($0) }
                }
                .pickerStyle(.menu)
                Spacer()
                Button {
                    selectedDate = Calendar.current.date(byAdding: .day, value: -1, to: selectedDate) ?? selectedDate
                } label: { Image(systemName: "chevron.left") }
                DatePicker("DATE", selection: $selectedDate, in: ...Date.now, displayedComponents: .date)
                    .labelsHidden()
                Button {
                    selectedDate = min(
                        Calendar.current.date(byAdding: .day, value: 1, to: selectedDate) ?? selectedDate,
                        Date.now
                    )
                } label: { Image(systemName: "chevron.right") }
                .disabled(Calendar.current.isDateInToday(selectedDate))
            }
            if let recording = day?.recording {
                HStack {
                    Label(
                        recording.available
                            ? "\(recording.segments.count) TRIGGER CLIPS"
                            : "TRIGGER CLIPS UNAVAILABLE",
                        systemImage: recording.available ? "externaldrive.fill.badge.checkmark" : "externaldrive.badge.xmark"
                    )
                    .foregroundStyle(recording.available ? SentinelTheme.green : SentinelTheme.amber)
                    Spacer()
                    Text("\(filteredEvents.count) OF \(day?.events.count ?? 0) AI EVENTS")
                        .foregroundStyle(SentinelTheme.textDim)
                }
                .font(.caption)
            }
        }
        .sentinelPanel()
    }

    private var playbackPanel: some View {
        VStack(spacing: 12) {
            ZStack {
                Rectangle().fill(.black)
                if let frameURL {
                    SentinelRemoteImage(url: frameURL, contentMode: .fit)
                        .id(frameURL)
                } else {
                    ContentUnavailableView(
                        "No saved trigger clip",
                        systemImage: "video.slash",
                        description: Text(day?.recording.error ?? "Choose a green trigger window on the timeline.")
                    )
                    .foregroundStyle(SentinelTheme.textDim)
                }
            }
            .aspectRatio(16 / 9, contentMode: .fit)
            .clipShape(RoundedRectangle(cornerRadius: 8))

            if let day {
                TimelineRail(day: day, events: filteredEvents, cursor: $cursor)
                    .frame(height: 54)
                HStack {
                    Button { jumpEvent(direction: -1) } label: {
                        Image(systemName: "backward.end.fill")
                    }
                    Button { isPlaying.toggle() } label: {
                        Image(systemName: isPlaying ? "pause.fill" : "play.fill")
                            .frame(width: 24)
                    }
                    Button { jumpEvent(direction: 1) } label: {
                        Image(systemName: "forward.end.fill")
                    }
                    Text(cursor.formatted(date: .omitted, time: .standard))
                        .foregroundStyle(SentinelTheme.cyan)
                    Spacer()
                    Button("NOW") {
                        selectedDate = Calendar.current.startOfDay(for: .now)
                        cursor = .now
                    }
                }
                .buttonStyle(.bordered)
            } else if selectedCamera.isEmpty {
                Text("AI events will provide the camera choices for day review.")
                    .foregroundStyle(SentinelTheme.textDim)
            } else {
                ProgressView("Loading day…")
            }

            if let errorMessage {
                Label(errorMessage, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(SentinelTheme.amber)
                    .font(.caption)
            }
        }
        .sentinelPanel()
    }

    private var filterBar: some View {
        HStack {
            Menu("OBJECTS \(selectedCategories.count)") {
                ForEach(["person", "vehicle", "animal", "package", "plate", "other"], id: \.self) { value in
                    Toggle(value.capitalized, isOn: membership(value, in: $selectedCategories))
                }
            }
            Menu("SEVERITY \(selectedSeverities.count)") {
                ForEach(["critical", "high", "medium", "low"], id: \.self) { value in
                    Toggle(value.capitalized, isOn: membership(value, in: $selectedSeverities))
                }
            }
            Picker("REVIEW", selection: $reviewFilter) {
                ForEach(ReviewFilter.allCases) { Text($0.label).tag($0) }
            }
            .pickerStyle(.menu)
            Spacer()
            Button("RESET FILTERS") {
                selectedCategories = Set(["person", "vehicle", "animal", "package", "plate", "other"])
                selectedSeverities = Set(["critical", "high", "medium", "low"])
                reviewFilter = .all
            }
        }
        .buttonStyle(.bordered)
    }

    private var eventList: some View {
        LazyVStack(spacing: 10) {
            ForEach(filteredEvents) { event in
                HStack(spacing: 12) {
                    Button {
                        cursor = clipStart(for: event) ?? event.occurredAt
                        isPlaying = false
                    } label: {
                        HStack(spacing: 12) {
                        SentinelRemoteImage(
                            url: event.hasImage
                                ? model.client.url(path: "/api/events/\(event.id.uuidString)/image")
                                : nil
                        )
                        .frame(width: 112, height: 68)
                        .clipShape(RoundedRectangle(cornerRadius: 5))
                        VStack(alignment: .leading, spacing: 5) {
                            HStack {
                                Text(event.occurredAt.formatted(date: .omitted, time: .shortened))
                                    .foregroundStyle(SentinelTheme.cyan)
                                Text(event.category.uppercased())
                                    .foregroundStyle(categoryColor(event.category))
                                Text(event.severity.uppercased())
                                    .foregroundStyle(severityColor(event.severity))
                                if !event.hasClip {
                                    Text("NO CLIP")
                                        .foregroundStyle(SentinelTheme.textDim)
                                }
                            }
                            .font(.caption)
                            Text(event.summary.isEmpty ? event.eventType : event.summary)
                                .foregroundStyle(SentinelTheme.text)
                                .lineLimit(2)
                        }
                        Spacer()
                        }
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    Button {
                        Task { await toggleReviewed(event) }
                    } label: {
                        Image(systemName: event.reviewed ? "checkmark.circle.fill" : "circle")
                            .font(.title2)
                            .foregroundStyle(event.reviewed ? SentinelTheme.green : SentinelTheme.textDim)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(event.reviewed ? "Mark unreviewed" : "Mark reviewed")
                }
                .sentinelPanel()
            }
        }
    }

    private var frameURL: URL? {
        guard let day,
              day.recording.available,
              let segment = day.recording.segments.first(where: {
                  $0.start <= cursor && $0.end >= cursor
              }) else { return nil }
        return model.client.url(
            path: "/api/timeline/frame",
            query: [
                .init(name: "eventId", value: segment.eventId.uuidString),
                .init(name: "at", value: cursor.ISO8601Format()),
                .init(name: "width", value: "1280"),
                .init(name: "height", value: "720")
            ]
        )
    }

    private func loadDay() async {
        guard !selectedCamera.isEmpty else { day = nil; return }
        isPlaying = false
        day = nil
        errorMessage = nil
        do {
            let value = try await model.client.timeline(
                camera: selectedCamera,
                date: selectedDate.formatted(.iso8601.year().month().day())
            )
            day = value
            if let last = value.recording.segments.last {
                cursor = last.start
            } else if let lastEvent = value.events.last {
                cursor = lastEvent.occurredAt
            } else {
                cursor = value.start.addingTimeInterval(12 * 60 * 60)
            }
        } catch {
            day = nil
            errorMessage = error.localizedDescription
        }
    }

    private func toggleReviewed(_ event: TimelineSecurityEvent) async {
        do {
            try await model.client.setReviewed(eventId: event.id, reviewed: !event.reviewed)
            guard var value = day,
                  let index = value.events.firstIndex(where: { $0.id == event.id }) else { return }
            value.events[index].reviewed.toggle()
            day = value
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func jumpEvent(direction: Int) {
        let ordered = filteredEvents.sorted { $0.occurredAt < $1.occurredAt }
        let target = direction > 0
            ? ordered.first(where: { $0.occurredAt > cursor.addingTimeInterval(0.5) })
            : ordered.last(where: { $0.occurredAt < cursor.addingTimeInterval(-0.5) })
        if let target { cursor = clipStart(for: target) ?? target.occurredAt }
    }

    private func advancePlayback() {
        guard let day else { return }
        let next = cursor.addingTimeInterval(0.5)
        if next >= day.end {
            isPlaying = false
            return
        }
        if let segment = day.recording.segments.first(where: { $0.start <= next && $0.end > next }) {
            cursor = min(next, segment.end)
            return
        }
        if let nextSegment = day.recording.segments.first(where: { $0.start > next }) {
            cursor = nextSegment.start
        } else {
            isPlaying = false
        }
    }

    private func membership(_ value: String, in set: Binding<Set<String>>) -> Binding<Bool> {
        Binding(
            get: { set.wrappedValue.contains(value) },
            set: { enabled in
                if enabled { set.wrappedValue.insert(value) }
                else { set.wrappedValue.remove(value) }
            }
        )
    }

    private func clipStart(for event: TimelineSecurityEvent) -> Date? {
        day?.recording.segments.first(where: { $0.eventId == event.id })?.start
    }

    private func categoryColor(_ value: String) -> Color {
        switch value {
        case "person": SentinelTheme.cyan
        case "vehicle": SentinelTheme.green
        case "plate": SentinelTheme.amber
        case "package": .purple
        case "animal": .orange
        default: SentinelTheme.textDim
        }
    }

    private func severityColor(_ value: String) -> Color {
        switch value {
        case "critical": SentinelTheme.red
        case "high": SentinelTheme.amber
        case "medium": SentinelTheme.cyan
        default: SentinelTheme.textDim
        }
    }
}

private struct TimelineRail: View {
    let day: RecordingTimelineDay
    let events: [TimelineSecurityEvent]
    @Binding var cursor: Date

    var body: some View {
        GeometryReader { geometry in
            ZStack(alignment: .leading) {
                RoundedRectangle(cornerRadius: 6).fill(SentinelTheme.panel)
                ForEach(day.recording.segments, id: \.self) { segment in
                    RoundedRectangle(cornerRadius: 2)
                        .fill(SentinelTheme.greenDim.opacity(0.65))
                        .frame(width: max(2, geometry.size.width * fraction(segment.end.timeIntervalSince(segment.start))))
                        .offset(x: geometry.size.width * position(segment.start))
                }
                ForEach(events) { event in
                    Rectangle()
                        .fill(markerColor(event.severity))
                        .frame(width: event.severity == "critical" ? 4 : 2, height: 28)
                        .offset(x: geometry.size.width * position(event.occurredAt))
                }
                Rectangle()
                    .fill(.white)
                    .frame(width: 2, height: 42)
                    .offset(x: geometry.size.width * position(cursor))
                Slider(
                    value: Binding(
                        get: { max(0, cursor.timeIntervalSince(day.start)) },
                        set: { cursor = day.start.addingTimeInterval($0) }
                    ),
                    in: 0...max(1, day.end.timeIntervalSince(day.start) - 1)
                )
                .tint(.clear)
            }
        }
        .accessibilityLabel("24 hour recording timeline")
    }

    private func position(_ date: Date) -> Double {
        min(1, max(0, date.timeIntervalSince(day.start) / day.end.timeIntervalSince(day.start)))
    }

    private func fraction(_ duration: TimeInterval) -> Double {
        min(1, max(0, duration / day.end.timeIntervalSince(day.start)))
    }

    private func markerColor(_ severity: String) -> Color {
        severity == "critical" ? SentinelTheme.red :
        severity == "high" ? SentinelTheme.amber :
        severity == "medium" ? SentinelTheme.cyan : SentinelTheme.textDim
    }
}

private enum ReviewFilter: String, CaseIterable, Identifiable {
    case all, unreviewed, reviewed
    var id: Self { self }
    var label: String { rawValue.uppercased() }
    func includes(_ reviewed: Bool) -> Bool {
        self == .all || (self == .reviewed && reviewed) || (self == .unreviewed && !reviewed)
    }
}
