import SwiftUI

// What the player needs from the top of the window: the Now Playing sheet (iPhone/iPad - the Mac
// opens a window instead), the shared Add to Playlist sheet, and the small confirmation toast.
struct AudioRootPresenter: ViewModifier {
    @Binding var selection: AppSection?

    func body(content: Content) -> some View {
        content
            .onReceive(NotificationCenter.default.publisher(for: .sentinelOpenSection)) { note in
                if let name = note.object as? String, let section = AppSection(rawValue: name) { selection = section }
            }
            .overlay(alignment: .bottom) {
                AudioToastOverlay()
                    .animation(.spring(response: 0.35, dampingFraction: 0.8), value: AudioToast.shared.message)
                    .allowsHitTesting(false)
            }
            #if os(iOS)
            .sheet(isPresented: Binding(get: { AudioPlayer.shared.showNowPlaying }, set: { AudioPlayer.shared.showNowPlaying = $0 })) {
                NowPlayingView()
                    .presentationDragIndicator(.hidden)
                    .presentationBackground(.black)
            }
            #endif
            .sheet(item: Binding(get: { MusicState.shared.addToPlaylist }, set: { MusicState.shared.addToPlaylist = $0 })) { request in
                AddToPlaylistSheet(request: request)
            }
    }
}

#if os(macOS)
// Controls → Play/Pause (space), Next (⌘→), Previous (⌘←), and the rest, like Music.app.
struct AudioCommands: Commands {
    @Environment(\.openWindow) private var openWindow

    var body: some Commands {
        CommandMenu("Controls") {
            Button("Play/Pause") { playPause() }
                .keyboardShortcut(.space, modifiers: [])
            Button("Next") { if !editing(#selector(NSResponder.moveToEndOfLine(_:))) { AudioPlayer.shared.next() } }
                .keyboardShortcut(.rightArrow, modifiers: .command)
            Button("Previous") { if !editing(#selector(NSResponder.moveToBeginningOfLine(_:))) { AudioPlayer.shared.previous() } }
                .keyboardShortcut(.leftArrow, modifiers: .command)
            Divider()
            Button("Skip Back 15 Seconds") { AudioPlayer.shared.skip(by: -15) }
                .keyboardShortcut(.leftArrow, modifiers: [.command, .option])
            Button("Skip Forward 30 Seconds") { AudioPlayer.shared.skip(by: 30) }
                .keyboardShortcut(.rightArrow, modifiers: [.command, .option])
            Divider()
            Button("Shuffle") { AudioPlayer.shared.toggleShuffle() }
                .keyboardShortcut("s", modifiers: [.command, .control])
            Button("Repeat") { AudioPlayer.shared.cycleRepeat() }
                .keyboardShortcut("r", modifiers: [.command, .control])
            Button("Love Current Song") { AudioPlayer.shared.toggleLoveCurrent() }
                .keyboardShortcut("l", modifiers: [.command, .control])
            Divider()
            Button("Volume Up") { if !editing(#selector(NSResponder.moveToBeginningOfDocument(_:))) { AudioPlayer.shared.setUserVolume(AudioPlayer.shared.userVolume + 0.1) } }
                .keyboardShortcut(.upArrow, modifiers: .command)
            Button("Volume Down") { if !editing(#selector(NSResponder.moveToEndOfDocument(_:))) { AudioPlayer.shared.setUserVolume(AudioPlayer.shared.userVolume - 0.1) } }
                .keyboardShortcut(.downArrow, modifiers: .command)
            Divider()
            Button("Now Playing") { openWindow(id: NowPlayingView.windowId) }
                .keyboardShortcut("p", modifiers: [.command, .option])
            Button("Morning Briefing") { AudioPlayer.shared.playBriefing() }
        }
    }

    // Space is Play/Pause everywhere except while typing, where it must still type a space - menu
    // key equivalents are matched before the text field sees the key.
    private func playPause() {
        if let editor = NSApp.keyWindow?.firstResponder as? NSTextView, editor.isEditable {
            editor.insertText(" ", replacementRange: editor.selectedRange())
            return
        }
        AudioPlayer.shared.togglePlayPause()
    }

    // The same for ⌘ + arrows: in a text field they move the cursor, as they always have.
    private func editing(_ selector: Selector) -> Bool {
        guard let editor = NSApp.keyWindow?.firstResponder as? NSTextView else { return false }
        editor.doCommand(by: selector)
        return true
    }
}
#endif
