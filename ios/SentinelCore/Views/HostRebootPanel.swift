import Combine
import SwiftUI
#if os(iOS)
import UIKit
#elseif os(macOS)
import AppKit
#endif

// The REBOOT SERVER settings section's live half.
//
// The section's own two fields (on/off, undo window) render generically like every other
// section, because they are ordinary stored settings. This panel exists because the third thing
// the feature needs cannot be stored anywhere: permission from the operating system on the
// server. Only the server can say whether it has that permission, only it knows which of the
// three platforms it is on, and the answer can stop being true without anything in this app
// changing - a rebuilt box, an OS upgrade that swept /etc/sudoers.d, a service moved to a
// different account.
//
// So this never caches a verdict. Every appearance re-probes, and the reboot button exists only
// while that fresh probe says it will work.
struct HostRebootPanel: View {
    @EnvironmentObject private var model: AppModel

    @State private var probe: HostRebootProbe?
    @State private var loadError: String?
    @State private var busy = false
    @State private var confirmingReboot = false
    @State private var message: String?
    @State private var secondsRemaining: Int?

    @State private var ticker = Timer.publish(every: 1, on: .main, in: .common).autoconnect()

    var body: some View {
        Section {
            if let probe {
                statusRow(probe)

                if let loadError {
                    Text(loadError).foregroundStyle(SentinelTheme.red)
                }

                if secondsRemaining == nil, let script = probe.setupScript, !script.isEmpty {
                    setupBlock(script: script, note: probe.setupNote)
                }

                // Offered only while there is still a window to cancel into. Once it reaches
                // zero the machine is on its way down and the server refuses this too - a button
                // that promises an undo it can't deliver is worse than no button.
                if (secondsRemaining ?? 0) > 0 {
                    Button("Cancel Reboot") { Task { await cancel() } }
                        .disabled(busy)
                } else if secondsRemaining == nil, probe.canReboot {
                    Button("Reboot Server", role: .destructive) { confirmingReboot = true }
                        .disabled(busy)
                        .confirmationDialog(
                            rebootPrompt(probe),
                            isPresented: $confirmingReboot,
                            titleVisibility: .visible
                        ) {
                            Button("Reboot", role: .destructive) { Task { await reboot() } }
                            Button("Cancel", role: .cancel) {}
                        }
                }

                Button("Check Again") { Task { await refresh() } }
                    .disabled(busy)
            } else if let loadError {
                Text(loadError).foregroundStyle(SentinelTheme.red)
                Button("Retry") { Task { await refresh() } }
            } else {
                ProgressView()
            }
        } header: {
            Text("Rebooting this machine").textCase(nil)
        } footer: {
            if let message {
                Text(message).foregroundStyle(SentinelTheme.green)
            }
        }
        .task { await refresh() }
        .onReceive(ticker) { _ in tick() }
    }

    @ViewBuilder
    private func statusRow(_ probe: HostRebootProbe) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            if let secondsRemaining {
                Text(secondsRemaining > 0
                     ? "Rebooting in \(secondsRemaining)s. Cancel now to call it off."
                     : "Rebooting now - the server will be gone for a minute or two.")
                    .foregroundStyle(SentinelTheme.red)
            } else {
                Text(headline(probe)).foregroundStyle(colour(probe))
            }

            Text("\(probe.platformLabel), running as \(probe.runningAs)")
                .font(.caption2)
                .foregroundStyle(.secondary)
        }
    }

    // Switched-off-but-ready is called out separately rather than folded into the summary: it's
    // the one state the person can fix from this screen, and burying it under a wall of server
    // prose would send them to a terminal they don't need to open.
    private func headline(_ probe: HostRebootProbe) -> String {
        if probe.isReady && !probe.enabled {
            return "Ready, but switched off above. Turn on Enabled and save to use it."
        }
        return probe.summary
    }

    private func colour(_ probe: HostRebootProbe) -> Color {
        if probe.isReady { return probe.enabled ? SentinelTheme.green : .secondary }
        if probe.needsSetup { return SentinelTheme.amber }
        return .secondary
    }

    private func rebootPrompt(_ probe: HostRebootProbe) -> String {
        probe.delaySeconds > 0
            ? "Reboot the whole server machine? Everything stops for a minute or two - analysis, this app, notifications. You'll have \(probe.delaySeconds) seconds to cancel."
            : "Reboot the whole server machine now? Everything stops for a minute or two - analysis, this app, notifications."
    }

    @ViewBuilder
    private func setupBlock(script: String, note: String?) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(script)
                .font(.system(.caption2, design: .monospaced))
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(8)
                .background(.quaternary)
                .clipShape(RoundedRectangle(cornerRadius: 4))

            // Copying here and pasting into a terminal on the server is the actual workflow -
            // nobody is going to retype a sudoers line off a phone screen correctly.
            Button {
                #if os(macOS)
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(script, forType: .string)
                #else
                UIPasteboard.general.string = script
                #endif
                message = "Commands copied. Run them on the server, then Check Again."
            } label: {
                Label("Copy Commands", systemImage: "doc.on.doc")
            }

            if let note, !note.isEmpty {
                Text(note).font(.caption2).foregroundStyle(.secondary)
            }
        }
    }

    private func tick() {
        guard let current = secondsRemaining, current > 0 else { return }
        secondsRemaining = current - 1
    }

    private func refresh() async {
        busy = true
        defer { busy = false }
        do {
            let fresh = try await model.client.hostRebootProbe()
            probe = fresh
            loadError = nil
            secondsRemaining = fresh.secondsRemaining
        } catch {
            loadError = error.localizedDescription
        }
    }

    private func reboot() async {
        busy = true
        defer { busy = false }
        message = nil
        do {
            let outcome = try await model.client.rebootHost()
            message = outcome.message
            secondsRemaining = outcome.secondsRemaining
            // A stale failure from an earlier Check Again would otherwise sit under a countdown
            // that is working perfectly well.
            loadError = nil
        } catch {
            // The server's refusals are written to be read - "the sudoers rule is gone", "this is
            // switched off" - so they're shown verbatim rather than replaced with a generic line.
            loadError = error.localizedDescription
        }
    }

    private func cancel() async {
        busy = true
        defer { busy = false }
        do {
            let outcome = try await model.client.cancelHostReboot()
            message = outcome.message
            secondsRemaining = nil
            // Only on success: refresh() clears loadError, so re-probing after a failed cancel
            // would wipe the very message explaining why it failed.
            await refresh()
        } catch {
            loadError = error.localizedDescription
        }
    }
}
