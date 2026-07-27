import Foundation

@MainActor
final class SentinelClient {
    private unowned let model: AppModel
    private var session: URLSession

    init(model: AppModel) {
        self.model = model
        session = Self.makeSession()
    }

    func resetSession() {
        session.invalidateAndCancel()
        session = Self.makeSession()
        model.log("Created a fresh network session")
    }

    private static func makeSession() -> URLSession {
        let configuration = URLSessionConfiguration.default
        configuration.httpCookieStorage = .shared
        configuration.httpShouldSetCookies = true
        configuration.timeoutIntervalForRequest = 30
        configuration.timeoutIntervalForResource = 120
        configuration.waitsForConnectivity = true
        return URLSession(configuration: configuration)
    }

    func authStatus() async throws -> AuthStatus {
        try await get("/api/auth/status")
    }

    func events(search: String? = nil, notifiedOnly: Bool = false) async throws -> [SecurityEvent] {
        var items = [URLQueryItem(name: "limit", value: "100")]
        if let search, !search.isEmpty { items.append(.init(name: "search", value: search)) }
        if notifiedOnly { items.append(.init(name: "notifiedOnly", value: "true")) }
        return try await get("/api/events", query: items)
    }

    func triggers() async throws -> [TriggerEvent] {
        try await get("/api/triggers", query: [.init(name: "limit", value: "100")])
    }

    func cameras() async throws -> [Camera] {
        try await get("/api/cameras")
    }

    func login(username: String, password: String) async throws {
        let _: EmptyResponse = try await send(
            "/api/auth/login",
            method: "POST",
            body: LoginBody(username: username, password: password)
        )
    }

    func logout() async throws {
        let _: EmptyResponse = try await send("/api/auth/logout", method: "POST", body: Optional<String>.none)
    }

    func url(path: String) -> URL? {
        guard var components = URLComponents(string: model.serverURL.trimmingCharacters(in: .whitespacesAndNewlines)) else {
            return nil
        }
        components.path = path
        components.query = nil
        return components.url
    }

    private func get<T: Decodable>(_ path: String, query: [URLQueryItem] = []) async throws -> T {
        guard var components = URLComponents(url: try baseURL(), resolvingAgainstBaseURL: false) else {
            throw ClientError.invalidServerAddress
        }
        components.path = path
        components.queryItems = query.isEmpty ? nil : query
        guard let url = components.url else { throw ClientError.invalidServerAddress }
        return try await perform(URLRequest(url: url))
    }

    private func send<T: Decodable, Body: Encodable>(
        _ path: String,
        method: String,
        body: Body?
    ) async throws -> T {
        guard let url = url(path: path) else { throw ClientError.invalidServerAddress }
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        if let body { request.httpBody = try JSONEncoder().encode(body) }
        return try await perform(request)
    }

    private func perform<T: Decodable>(_ request: URLRequest) async throws -> T {
        let started = ContinuousClock.now
        let method = request.httpMethod ?? "GET"
        let displayURL = request.url?.absoluteString ?? "(invalid URL)"
        model.log("\(method) \(displayURL)")

        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await session.data(for: request)
        } catch {
            model.log("\(method) failed after \(elapsed(from: started)): \(describe(error))", level: .error)
            throw error
        }

        guard let http = response as? HTTPURLResponse else { throw ClientError.invalidResponse }
        model.log("\(method) \(http.statusCode) in \(elapsed(from: started))")
        guard (200..<300).contains(http.statusCode) else {
            let message = (try? JSONDecoder.sentinel.decode(APIError.self, from: data))
                .flatMap { $0.error ?? $0.errors?.joined(separator: "\n") }
            if http.statusCode == 401 { throw ClientError.authenticationRequired }
            throw ClientError.server(status: http.statusCode, message: message)
        }
        if T.self == EmptyResponse.self { return EmptyResponse() as! T }
        do {
            return try JSONDecoder.sentinel.decode(T.self, from: data)
        } catch {
            model.log("Could not decode \(displayURL): \(error)", level: .error)
            throw error
        }
    }

    private func baseURL() throws -> URL {
        guard let url = URL(string: model.serverURL.trimmingCharacters(in: .whitespacesAndNewlines)),
              url.scheme == "http" || url.scheme == "https",
              url.host != nil else {
            throw ClientError.invalidServerAddress
        }
        return url
    }

    private func elapsed(from start: ContinuousClock.Instant) -> String {
        let duration = start.duration(to: .now)
        return duration.formatted(.units(allowed: [.seconds, .milliseconds], width: .abbreviated))
    }

    private func describe(_ error: Error) -> String {
        if let urlError = error as? URLError {
            return "URLError \(urlError.code.rawValue) (\(urlError.code)): \(urlError.localizedDescription)"
        }
        return "\(type(of: error)): \(error.localizedDescription)"
    }
}

struct EmptyResponse: Decodable {}

enum ClientError: LocalizedError {
    case invalidServerAddress
    case invalidResponse
    case authenticationRequired
    case server(status: Int, message: String?)

    var errorDescription: String? {
        switch self {
        case .invalidServerAddress: "Enter a valid server address, such as http://192.168.1.50:3320."
        case .invalidResponse: "The server returned an invalid response."
        case .authenticationRequired: "This server requires you to sign in."
        case let .server(status, message): message ?? "Server request failed (HTTP \(status))."
        }
    }
}
