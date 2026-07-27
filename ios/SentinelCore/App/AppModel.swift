import Combine
import Foundation
import Network

@MainActor
final class AppModel: ObservableObject {
    @Published var serverURL: String {
        didSet { UserDefaults.standard.set(serverURL, forKey: "serverURL") }
    }
    @Published var connectionState: ConnectionState = .checking
    @Published var authStatus: AuthStatus?
    @Published var events: [SecurityEvent] = []
    @Published var triggers: [TriggerEvent] = []
    @Published var cameras: [Camera] = []
    @Published var isLoading = false
    @Published var lastError: String?
    @Published var diagnostics: [DiagnosticEntry] = []
    @Published var lastUpdated: Date?

    private(set) lazy var client = SentinelClient(model: self)
    private var localNetworkBrowser: NWBrowser?
    private var isConnecting = false

    init() {
        serverURL = UserDefaults.standard.string(forKey: "serverURL") ?? ""
    }

    var isConfigured: Bool { URL(string: serverURL)?.host != nil }
    var requiresLogin: Bool { authStatus?.enabled == true && authStatus?.authenticated == false }

    func connect(resetNetworkSession: Bool = false) async {
        guard isConfigured else {
            connectionState = .notConfigured
            return
        }
        guard !isConnecting else {
            log("Connection request skipped because one is already active", level: .notice)
            return
        }

        isConnecting = true
        defer { isConnecting = false }
        log("Connecting to \(serverURL)")
        let wasOffline = connectionState == .offline
        if connectionState != .online {
            connectionState = .checking
        }
        // A URLSession created while local-network access was denied can retain the old
        // unsatisfied network path. Recreate it on each explicit connection attempt so a
        // newly-granted privacy decision takes effect immediately.
        if resetNetworkSession || wasOffline {
            client.resetSession()
        }
        startLocalNetworkPermissionProbe()
        // Give macOS a moment to present/register the local-network permission before the
        // first HTTP request. The browser stays alive for the app's lifetime.
        try? await Task.sleep(for: .milliseconds(500))
        do {
            authStatus = try await client.authStatus()
            if requiresLogin {
                log("Server requires dashboard authentication")
                connectionState = .authenticationRequired
                return
            }
            try await refreshAll()
            connectionState = .online
            log("Connection established; loaded \(events.count) events")
        } catch {
            if Self.isCancellation(error) {
                log("Connection task cancelled by the app lifecycle", level: .notice)
                return
            }
            connectionState = .offline
            lastError = error.localizedDescription
            log("Connection failed: \(error.localizedDescription)", level: .error)
        }
    }

    func refreshAll() async throws {
        isLoading = true
        defer { isLoading = false }

        // The alert feed is the primary screen. Load each resource independently so an
        // unavailable camera stream or an older server without trigger endpoints cannot hide
        // events that were returned successfully.
        var firstError: Error?
        do {
            events = try await client.events()
        } catch {
            if Self.isCancellation(error) { throw error }
            firstError = error
        }
        do {
            triggers = try await client.triggers()
        } catch {
            if Self.isCancellation(error) { throw error }
            firstError = firstError ?? error
        }
        do {
            cameras = try await client.cameras()
        } catch {
            if Self.isCancellation(error) { throw error }
            firstError = firstError ?? error
        }

        if let firstError {
            lastError = firstError.localizedDescription
            if events.isEmpty { throw firstError }
        } else {
            lastError = nil
        }
        lastUpdated = .now
    }

    private static func isCancellation(_ error: Error) -> Bool {
        if error is CancellationError { return true }
        return (error as? URLError)?.code == .cancelled
    }

    func log(_ message: String, level: DiagnosticLevel = .info) {
        diagnostics.insert(DiagnosticEntry(timestamp: .now, level: level, message: message), at: 0)
        if diagnostics.count > 100 {
            diagnostics.removeLast(diagnostics.count - 100)
        }
        print("[SentinelCore] [\(level.rawValue)] \(message)")
    }

    func clearDiagnostics() {
        diagnostics.removeAll()
    }

    private func startLocalNetworkPermissionProbe() {
        guard localNetworkBrowser == nil else { return }

        let browser = NWBrowser(
            for: .bonjour(type: "_http._tcp", domain: nil),
            using: .tcp
        )
        browser.stateUpdateHandler = { [weak self] state in
            Task { @MainActor in
                guard let self else { return }
                switch state {
                case .ready:
                    self.log("Local-network discovery is ready")
                    if self.connectionState != .online {
                        await self.connect(resetNetworkSession: true)
                    }
                case let .waiting(error):
                    self.log("Waiting for local-network permission: \(error)", level: .notice)
                case let .failed(error):
                    self.log("Local-network discovery failed: \(error)", level: .error)
                case .cancelled:
                    self.log("Local-network discovery was cancelled", level: .notice)
                case .setup:
                    self.log("Requesting local-network access")
                @unknown default:
                    self.log("Local-network discovery entered an unknown state", level: .notice)
                }
            }
        }
        browser.browseResultsChangedHandler = { [weak self] results, _ in
            Task { @MainActor in
                self?.log("Local-network access active; discovered \(results.count) HTTP service(s)")
            }
        }
        localNetworkBrowser = browser
        browser.start(queue: DispatchQueue(label: "com.sentinelcore.local-network-permission"))
    }

    func signIn(username: String, password: String) async throws {
        try await client.login(username: username, password: password)
        authStatus = try await client.authStatus()
        try await refreshAll()
        connectionState = .online
    }

    func signOut() async {
        try? await client.logout()
        authStatus = try? await client.authStatus()
        connectionState = requiresLogin ? .authenticationRequired : .online
    }
}

struct DiagnosticEntry: Identifiable {
    let id = UUID()
    let timestamp: Date
    let level: DiagnosticLevel
    let message: String
}

enum DiagnosticLevel: String {
    case info = "INFO"
    case notice = "NOTICE"
    case error = "ERROR"
}

enum ConnectionState: Equatable {
    case notConfigured, checking, online, authenticationRequired, offline

    var label: String {
        switch self {
        case .notConfigured: "SET SERVER"
        case .checking: "CONNECTING"
        case .online: "LINK ESTABLISHED"
        case .authenticationRequired: "SIGN IN"
        case .offline: "LINK LOST"
        }
    }
}
