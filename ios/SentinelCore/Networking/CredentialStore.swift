import Foundation
import Security

// The server uses a long-lived cookie, but cookies can still disappear after an app reinstall,
// server-address change, session invalidation, or an iOS networking reset. Keep the credentials
// in the system Keychain so the app can establish a new cookie without asking the person to type
// the same password again. This-device-only prevents the password from being restored onto a
// different device as part of a backup.
struct StoredCredential: Codable {
    let username: String
    let password: String
}

enum CredentialStore {
    private static let service = "com.sentinelcore.client.server-login"

    static func load(for server: String) -> StoredCredential? {
        var query = baseQuery(for: server)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne

        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data else { return nil }
        return try? JSONDecoder().decode(StoredCredential.self, from: data)
    }

    static func save(_ credential: StoredCredential, for server: String) throws {
        let data = try JSONEncoder().encode(credential)
        let query = baseQuery(for: server)
        let update = [kSecValueData as String: data]

        let status = SecItemUpdate(query as CFDictionary, update as CFDictionary)
        if status == errSecItemNotFound {
            var item = query
            item[kSecValueData as String] = data
            item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
            let addStatus = SecItemAdd(item as CFDictionary, nil)
            guard addStatus == errSecSuccess else { throw KeychainError(status: addStatus) }
        } else if status != errSecSuccess {
            throw KeychainError(status: status)
        }
    }

    static func delete(for server: String) {
        SecItemDelete(baseQuery(for: server) as CFDictionary)
    }

    private static func baseQuery(for server: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: normalizedServer(server)
        ]
    }

    private static func normalizedServer(_ value: String) -> String {
        value.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
    }
}

private struct KeychainError: LocalizedError {
    let status: OSStatus
    var errorDescription: String? { "The login could not be saved securely (Keychain error \(status))." }
}
