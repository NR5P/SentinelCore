import Foundation

// A note's checklist, read from and written back into its Markdown body. A note stays plain text -
// an item is just a line like "- [ ] milk" or "- [x] milk" - so these only ever rewrite lines, and
// nothing about how notes sync changes.
//
// The dashboard (note-checklist.js), Android and Windows have the same rules, and every test suite
// checks the same examples in core/docs/note-checklist-cases.json. Change them together.
public enum NoteChecklist {
    public struct Item: Equatable, Sendable {
        public var done: Bool
        public var text: String
    }

    // One line of a body as the checklist sees it. Lines in a ``` code block (and the fences
    // themselves) are code: shown as written, never ticked or rewritten.
    public struct Line: Equatable, Sendable {
        public var text: String
        public var item: Item?
        public var isCode: Bool
    }

    public enum Continuation: Equatable, Sendable {
        // Return after this line should start the next with this text.
        case insert(String)
        // Return on an empty item or bullet ends the list instead.
        case endList
    }

    public static func lines(_ body: String) -> [Line] {
        var inCode = false
        return body.components(separatedBy: "\n").map { text in
            if isFence(text) {
                inCode.toggle()
                return Line(text: text, item: nil, isCode: true)
            }
            return Line(text: text, item: inCode ? nil : item(text), isCode: inCode)
        }
    }

    // The item on a single line, ignoring code blocks - callers with a whole body use `lines`.
    public static func item(_ line: String) -> Item? {
        guard let groups = match(itemPattern, line) else { return nil }
        return Item(done: groups[1] != " ", text: groups[2])
    }

    public static func progress(_ body: String) -> (done: Int, total: Int) {
        let items = lines(body).compactMap(\.item)
        return (items.filter(\.done).count, items.count)
    }

    // A new unticked item goes after the last one, so it joins the list rather than landing under
    // whatever notes follow it; a body with no list yet gets it at the end.
    public static func addItem(_ body: String, text: String) -> String {
        let text = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return body }
        let line = "- [ ] \(text)"
        var all = body.components(separatedBy: "\n")
        if let last = lines(body).lastIndex(where: { $0.item != nil }) {
            all.insert(line, at: last + 1)
            return all.joined(separator: "\n")
        }
        if body.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return line }
        var kept = body
        while kept.hasSuffix("\n") { kept.removeLast() }
        return kept + "\n" + line
    }

    public static func removeLine(_ body: String, index: Int) -> String {
        var all = body.components(separatedBy: "\n")
        guard all.indices.contains(index) else { return body }
        all.remove(at: index)
        return all.joined(separator: "\n")
    }

    public static func clearChecked(_ body: String) -> String {
        lines(body).filter { $0.item?.done != true }.map(\.text).joined(separator: "\n")
    }

    // Flips one item between ticked and not, keeping the rest of the line exactly as written.
    public static func toggle(_ body: String, line index: Int) -> String {
        let parsed = lines(body)
        guard parsed.indices.contains(index), let item = parsed[index].item,
              let box = parsed[index].text.range(of: #"\[( |x|X)\]"#, options: .regularExpression)
        else { return body }
        var all = body.components(separatedBy: "\n")
        all[index].replaceSubrange(box, with: item.done ? "[ ]" : "[x]")
        return all.joined(separator: "\n")
    }

    // Turns a note written as a plain list - one thing per line, or bullets, or numbers - into
    // items. Headings, quotes, blank lines, code and lines that are already items are left alone.
    public static func makeChecklist(_ body: String) -> String {
        lines(body).map { line in
            let text = line.text
            if line.isCode || line.item != nil || text.trimmingCharacters(in: .whitespaces).isEmpty
                || match(#"^#{1,6}\s"#, text) != nil || match(#"^\s*>"#, text) != nil {
                return text
            }
            if let groups = match(#"^(\s*)[-*]\s+(.*)$"#, text) ?? match(#"^(\s*)\d+[.)]\s+(.*)$"#, text) {
                return "\(groups[1])- [ ] \(groups[2])"
            }
            return "- [ ] " + text.trimmingCharacters(in: .whitespaces)
        }
        .joined(separator: "\n")
    }

    // What Return at the end of `line` should do in a plain text box: carry the list on, or end it.
    public static func continueOnEnter(_ line: String) -> Continuation? {
        if let groups = match(#"^(\s*)([-*])\s+\[( |x|X)\]\s+(.*)$"#, line) {
            return groups[4].trimmingCharacters(in: .whitespaces).isEmpty ? .endList : .insert("\(groups[1])\(groups[2]) [ ] ")
        }
        if let groups = match(#"^(\s*)([-*])\s+(.*)$"#, line) {
            return groups[3].trimmingCharacters(in: .whitespaces).isEmpty ? .endList : .insert("\(groups[1])\(groups[2]) ")
        }
        return nil
    }

    private static let itemPattern = #"^\s*[-*]\s+\[( |x|X)\]\s+(.*)$"#

    private static func isFence(_ line: String) -> Bool {
        line.trimmingCharacters(in: .whitespaces).hasPrefix("```")
    }

    // The whole match and each capture group, or nil when the line doesn't match.
    private static func match(_ pattern: String, _ line: String) -> [String]? {
        let regex = try! NSRegularExpression(pattern: pattern)
        guard let result = regex.firstMatch(in: line, range: NSRange(line.startIndex..., in: line)) else { return nil }
        return (0..<result.numberOfRanges).map { index in
            Range(result.range(at: index), in: line).map { String(line[$0]) } ?? ""
        }
    }
}
