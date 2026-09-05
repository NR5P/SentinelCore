import Foundation

// The AI verdict strings the server puts on an activity row (ActivityEndpoints.ConfirmationStates)
// and what each one is called on screen. Four values, not three: "unverified" means nothing ever
// judged the frame - the analysis failed or the forward was refused - and it used to be lumped in
// with "pending" as "awaiting a verdict", which promised an answer that was never coming. The web
// dashboard has told the two apart since the state was added; this is the same split, in the same
// words.
enum ActivityVerdict {
    static func symbol(for confirmation: String) -> String {
        switch confirmation {
        case "rejected": "exclamationmark.triangle.fill"
        case "unverified": "questionmark.circle"
        default: "ellipsis.circle"
        }
    }

    // Short enough for a row.
    static func shortLabel(for confirmation: String) -> String {
        switch confirmation {
        case "rejected": "AI found nothing"
        case "unverified": "Never checked by AI"
        default: "Awaiting AI verdict"
        }
    }

    // The detail screen's "AI verdict" line.
    static func detailLabel(for confirmation: String) -> String {
        switch confirmation {
        case "rejected": "Nothing found in frame"
        case "unverified": "Never checked - the analysis failed or was refused"
        default: "Awaiting verdict"
        }
    }
}
