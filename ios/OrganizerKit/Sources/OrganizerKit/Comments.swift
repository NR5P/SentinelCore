import Foundation

// Comments on a board card - see "comment" in ORGANIZER_SYNC.md. Each is its own item under the
// card, posted the moment it's written rather than with the card's Save. Only its author may
// change the body (the server enforces it); anyone who can see the card may delete one.

extension OrganizerItem {
    // Whether `username` may edit this comment: they wrote it, or login is off (no username), or
    // nobody knows who wrote it.
    public func isAuthored(by username: String?) -> Bool {
        guard let username, !username.isEmpty, let author = createdBy, !author.isEmpty else { return true }
        return author.caseInsensitiveCompare(username) == .orderedSame
    }

    // Set when the author changed the body after posting.
    public var editedAt: Date? { string("editedAt").flatMap(OrganizerDates.parseTimestamp) }
}

extension OrganizerEngine {
    // A card's comments, oldest first.
    public func comments(on cardId: String) -> [OrganizerItem] {
        children(of: cardId, kind: .comment).sorted { a, b in
            switch (a.createdAt, b.createdAt) {
            case let (x?, y?) where x != y: x < y
            case (nil, _?): false
            case (_?, nil): true
            default: a.id < b.id
            }
        }
    }

    @discardableResult
    public func postComment(_ body: String, on cardId: String) -> OrganizerItem? {
        let text = body.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, item(cardId) != nil else { return nil }
        return create(.comment, fields: ["body": .string(text)], parentId: cardId)
    }

    // Changes the body and stamps editedAt. False when there was nothing to change.
    @discardableResult
    public func editComment(_ id: String, body: String) -> Bool {
        let text = body.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, let comment = item(id), comment.kind == .comment, comment.string("body") != text else { return false }
        update(id, ["body": .string(text), "editedAt": .string(OrganizerDates.timestamp(Date()))])
        return true
    }
}
