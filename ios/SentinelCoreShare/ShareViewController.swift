import Observation
import OrganizerKit
import SwiftUI
import UIKit
import UniformTypeIdentifiers

// "Sentinel Core" in the iPhone and iPad share sheet: text or a link from any app - Notes, Safari,
// Messages - becomes a note in the person's own Organizer space.
//
// An extension can't reach the app's copy of the Organizer, so this only leaves what was shared in
// the App Group container (SharedNoteInbox). The app makes the note: straight away if it's running
// (the Darwin notification below), otherwise the next time it opens or refreshes in the background.
final class ShareViewController: UIViewController {
    override func viewDidLoad() {
        super.viewDidLoad()
        let model = ShareModel(context: extensionContext)
        let host = UIHostingController(rootView: ShareView(model: model))
        addChild(host)
        host.view.frame = view.bounds
        host.view.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        view.addSubview(host.view)
        host.didMove(toParent: self)
        Task { await model.load() }
    }
}

@MainActor
@Observable
final class ShareModel {
    var title = ""
    var text = ""
    private(set) var url: String?
    private(set) var loading = true
    private(set) var problem: String?
    private let context: NSExtensionContext?

    init(context: NSExtensionContext?) {
        self.context = context
    }

    var canSave: Bool { !SharedNote(text: text, url: url).isEmpty }

    func load() async {
        let items = context?.inputItems.compactMap { $0 as? NSExtensionItem } ?? []
        for item in items {
            if title.isEmpty, let given = item.attributedTitle?.string { title = given }
            if text.isEmpty, let content = item.attributedContentText?.string { text = content }
            for provider in item.attachments ?? [] {
                if url == nil, provider.hasItemConformingToTypeIdentifier(UTType.url.identifier),
                   let shared = await Self.load(URL.self, from: provider), !shared.isFileURL {
                    url = shared.absoluteString
                }
                if text.isEmpty, provider.hasItemConformingToTypeIdentifier(UTType.plainText.identifier),
                   let shared = await Self.load(String.self, from: provider) {
                    text = shared
                }
            }
        }
        loading = false
    }

    func save() {
        guard let inbox = SharedNoteInbox() else {
            problem = "Couldn't reach Sentinel Core's storage on this device."
            return
        }
        inbox.add(SharedNote(text: text, subject: title, url: url))
        CFNotificationCenterPostNotification(
            CFNotificationCenterGetDarwinNotifyCenter(), CFNotificationName(SharedNoteInbox.posted as CFString), nil, nil, true)
        context?.completeRequest(returningItems: nil)
    }

    func cancel() {
        context?.cancelRequest(withError: NSError(domain: NSCocoaErrorDomain, code: NSUserCancelledError))
    }

    // The item provider's completion-handler form. The provider itself never leaves the main
    // actor; only the loaded value, which is Sendable, comes back from the provider's queue.
    private static func load<T: _ObjectiveCBridgeable & Sendable>(_ type: T.Type, from provider: NSItemProvider) async -> T?
        where T._ObjectiveCType: NSItemProviderReading {
        await withCheckedContinuation { continuation in
            _ = provider.loadObject(ofClass: type) { @Sendable value, _ in
                continuation.resume(returning: value)
            }
        }
    }
}

struct ShareView: View {
    @Bindable var model: ShareModel

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("Title (optional)", text: $model.title)
                    TextEditor(text: $model.text)
                        .frame(minHeight: 140)
                    if let url = model.url {
                        Text(url)
                            .font(.footnote)
                            .foregroundStyle(.secondary)
                            .lineLimit(2)
                    }
                } footer: {
                    Text(model.problem ?? "Saved to your own notes in the Organizer. It shows up the next time Sentinel Core opens.")
                }
            }
            .overlay { if model.loading { ProgressView() } }
            .navigationTitle("New Note")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { model.cancel() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Save") { model.save() }
                        .disabled(model.loading || !model.canSave)
                }
            }
        }
    }
}
