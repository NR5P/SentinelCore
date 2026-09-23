// swift-tools-version: 6.0
import PackageDescription

// The Organizer's client side for Apple platforms - the on-device copy, the sync engine and the
// calendar maths - shared by the iPhone/iPad/Mac app (ios/) and the Apple TV app (appletv/).
// The wire protocol it speaks is core/docs/ORGANIZER_SYNC.md.
//
// A package rather than files in each app because the TV app has always been a copy of the phone
// app's code, and this is the one part that must never drift between the two: a sync rule that
// differs between devices is how data gets lost. It also gives the logic a test suite that runs
// with `swift test`, which neither app target has.
let package = Package(
    name: "OrganizerKit",
    platforms: [.iOS(.v17), .macOS(.v14), .tvOS(.v17)],
    products: [
        .library(name: "OrganizerKit", targets: ["OrganizerKit"]),
    ],
    targets: [
        .target(name: "OrganizerKit", linkerSettings: [.linkedLibrary("sqlite3")]),
        .testTarget(name: "OrganizerKitTests", dependencies: ["OrganizerKit"]),
    ]
)
