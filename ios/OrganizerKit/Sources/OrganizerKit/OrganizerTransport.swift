import Foundation

public struct OrganizerHTTPResponse: Sendable {
    public var status: Int
    public var data: Data

    public init(status: Int, data: Data) {
        self.status = status
        self.data = data
    }
}

// How the engine reaches the server. The apps hand it one backed by their own URLSession, so the
// Organizer shares the signed-in cookie, the local/remote address choice and the certificate
// handling everything else already has; the tests hand it one pointed at a server they started.
public protocol OrganizerTransport: Sendable {
    func send(method: String, path: String, query: [URLQueryItem], body: Data?, contentType: String?, timeout: TimeInterval?) async throws -> OrganizerHTTPResponse
}

public enum OrganizerError: LocalizedError, Equatable {
    case offline
    case signInRequired
    case turnedOff
    case server(status: Int, message: String?)

    public var errorDescription: String? {
        switch self {
        case .offline: "Can't reach the server."
        case .signInRequired: "Sign in to use the Organizer."
        case .turnedOff: "The Organizer is turned off in the server's settings."
        case let .server(status, message): message ?? "The server answered with an error (HTTP \(status))."
        }
    }
}

public struct URLSessionOrganizerTransport: OrganizerTransport {
    public let baseURL: @Sendable () -> URL?
    public let session: URLSession

    public init(session: URLSession = .shared, baseURL: @escaping @Sendable () -> URL?) {
        self.session = session
        self.baseURL = baseURL
    }

    public func send(method: String, path: String, query: [URLQueryItem], body: Data?, contentType: String?, timeout: TimeInterval?) async throws -> OrganizerHTTPResponse {
        guard let base = baseURL(), var components = URLComponents(url: base, resolvingAgainstBaseURL: false) else {
            throw OrganizerError.offline
        }
        components.path = path
        components.queryItems = query.isEmpty ? nil : query
        guard let url = components.url else { throw OrganizerError.offline }
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.httpBody = body
        if let contentType { request.setValue(contentType, forHTTPHeaderField: "Content-Type") }
        if let timeout { request.timeoutInterval = timeout }
        do {
            let (data, response) = try await session.data(for: request)
            return OrganizerHTTPResponse(status: (response as? HTTPURLResponse)?.statusCode ?? 0, data: data)
        } catch is CancellationError {
            throw CancellationError()
        } catch let error as URLError where error.code == .cancelled {
            throw CancellationError()
        } catch {
            throw OrganizerError.offline
        }
    }
}
