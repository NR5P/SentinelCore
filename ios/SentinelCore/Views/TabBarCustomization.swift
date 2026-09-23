import SwiftUI

// Which sections get a tab on an iPhone, and which are one tap further in, under More.
//
// An iPhone tab bar holds five items; a sixth makes UIKit collapse the overflow into its own
// "More" navigation controller, which cannot host these sections - a NavigationStack inside it
// crashes outright ("attempt to nest wrapped navigation controllers"), and without one the
// section renders blank. So the More tab here is our own: a plain list that pushes the section
// inside our own NavigationStack.
//
// The person picks the tabs (see TabBarEditor). Until they do, every section gets one when they
// all fit, and otherwise the first four in AppSection order do and the rest go under More.
struct TabBarLayout: Equatable {
    static let maxTabs = 5
    static let storageKey = "tabBarSections"

    let bar: [AppSection]
    let more: [AppSection]
    var showsMore: Bool { !more.isEmpty }

    // How many sections can have a tab: all five slots when nothing is left over for a More tab,
    // otherwise four, the fifth being More itself.
    static func limit(visible: [AppSection]) -> Int {
        visible.count <= maxTabs ? maxTabs : maxTabs - 1
    }

    init(pinned: [AppSection]?, visible: [AppSection]) {
        var chosen: [AppSection] = []
        for section in pinned ?? [] where visible.contains(section) && !chosen.contains(section) {
            chosen.append(section)
        }
        if chosen.isEmpty {
            chosen = Array(visible.prefix(Self.limit(visible: visible)))
        }
        if chosen.count == visible.count && chosen.count <= Self.maxTabs {
            bar = chosen
            more = []
        } else {
            let bar = Array(chosen.prefix(Self.maxTabs - 1))
            self.bar = bar
            more = visible.filter { !bar.contains($0) }
        }
    }

    // Stored as a comma-separated list of AppSection raw values; empty means "never customized".
    static func decode(_ stored: String) -> [AppSection]? {
        let sections = stored.split(separator: ",").compactMap { AppSection(rawValue: String($0)) }
        return sections.isEmpty ? nil : sections
    }

    static func encode(_ sections: [AppSection]) -> String {
        sections.map(\.rawValue).joined(separator: ",")
    }
}

// The More tab: everything without a tab of its own, plus the way into TabBarEditor.
struct MoreSectionsView: View {
    let sections: [AppSection]
    // The editor is presented from above the TabView (see RootView), not from here: saving a
    // change rebuilds the tabs, More included, and a sheet owned by this view went with it.
    let onEdit: () -> Void

    var body: some View {
        List {
            Section {
                ForEach(sections) { section in
                    NavigationLink(value: section) {
                        Label(section.title, systemImage: section.icon)
                    }
                }
            }
            Section {
                Button(action: onEdit) {
                    Label("Edit Tab Bar", systemImage: "square.grid.2x2")
                }
            } footer: {
                Text("Choose which sections have a tab at the bottom of the screen. The rest stay here.")
            }
        }
        .navigationTitle("More")
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button("Edit", action: onEdit)
            }
        }
    }
}

// Two lists: the tabs, in order, and everything else. Removing a tab moves it to More; adding one
// takes it out of More until the bar is full.
struct TabBarEditor: View {
    let visible: [AppSection]
    // A sheet from More needs its own way out; pushed from Settings, Back is that.
    var inSheet = false
    @AppStorage(TabBarLayout.storageKey) private var stored = ""
    @Environment(\.dismiss) private var dismiss

    private var layout: TabBarLayout {
        TabBarLayout(pinned: TabBarLayout.decode(stored), visible: visible)
    }

    // What the person has pinned, which is the whole bar when nothing is left over for More.
    private var tabs: [AppSection] { layout.bar }
    private var others: [AppSection] { visible.filter { !tabs.contains($0) } }
    private var limit: Int { TabBarLayout.limit(visible: visible) }

    var body: some View {
        List {
            Section {
                // A button of its own rather than onDelete: the system's swipe-to-confirm says
                // "Delete", which reads as losing the section rather than moving it to More.
                ForEach(tabs) { section in
                    HStack {
                        Button {
                            save(tabs.filter { $0 != section })
                        } label: {
                            Image(systemName: "minus.circle.fill")
                                .foregroundStyle(tabs.count > 1 ? SentinelTheme.red : .secondary)
                        }
                        .buttonStyle(.borderless)
                        .disabled(tabs.count <= 1)
                        .accessibilityLabel("Move \(section.title) to More")
                        Label(section.title, systemImage: section.icon)
                    }
                }
                .onMove { from, to in
                    var updated = tabs
                    updated.move(fromOffsets: from, toOffset: to)
                    save(updated)
                }
            } header: {
                Text("Tab Bar")
            } footer: {
                Text(visible.count > TabBarLayout.maxTabs
                     ? "Up to \(limit) sections, plus More for the rest."
                     : "Up to \(limit) sections. Anything you remove moves to More.")
            }

            if !others.isEmpty {
                Section("In More") {
                    ForEach(others) { section in
                        Button {
                            save(tabs + [section])
                        } label: {
                            HStack {
                                Image(systemName: "plus.circle.fill")
                                    .foregroundStyle(tabs.count < limit ? SentinelTheme.green : .secondary)
                                // Spelled out so the title reads like the tab rows above rather
                                // than taking the button's tint.
                                Label {
                                    Text(section.title)
                                        .foregroundStyle(tabs.count < limit ? .primary : .secondary)
                                } icon: {
                                    Image(systemName: section.icon)
                                        .foregroundStyle(SentinelTheme.green)
                                }
                            }
                        }
                        .disabled(tabs.count >= limit)
                        .accessibilityLabel("Add \(section.title) to the tab bar")
                    }
                }
            }

            Section {
                Button("Reset to Default") { stored = "" }
                    .disabled(stored.isEmpty)
            }
        }
        #if os(iOS)
        .environment(\.editMode, .constant(.active))
        #endif
        .navigationTitle("Tab Bar")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            if inSheet {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") { dismiss() }
                }
            }
        }
    }

    private func save(_ sections: [AppSection]) {
        stored = TabBarLayout.encode(Array(sections.prefix(limit)))
    }
}
