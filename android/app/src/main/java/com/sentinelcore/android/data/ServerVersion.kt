package com.sentinelcore.android.data

// The Kotlin counterpart of the Apple apps' ServerVersion.swift, kept in step by hand - the same
// arrangement Models.kt already has with Models.swift.

// The server's build identity as /api/health reports it - see BuildInfo.cs, which produces these
// exact field names. That endpoint is deliberately unauthenticated, so this is readable even while
// the app is sitting at the login screen.
data class ServerBuild(
    val version: String,
    val commit: String,
    val isDirty: Boolean,
    val display: String,
)

// Just the numeric major.minor.patch, which is all this app ever needs to order two builds. SemVer
// pre-release and build metadata ("1.2.0-beta.1", "1.0.0+ab12cd.dirty") are dropped rather than
// parsed: the commit is already reported separately, and nothing here should refuse to compare two
// versions because one of them was built from a dirty tree.
data class ServerVersion(
    val major: Int,
    val minor: Int,
    val patch: Int,
) : Comparable<ServerVersion> {

    override fun compareTo(other: ServerVersion): Int =
        compareValuesBy(this, other, { it.major }, { it.minor }, { it.patch })

    override fun toString(): String = "$major.$minor.$patch"

    companion object {
        fun parse(text: String): ServerVersion? {
            val numeric = text.trim().takeWhile { it.isDigit() || it == '.' }
            val parts = numeric.split('.').filter { it.isNotEmpty() }
            val major = parts.getOrNull(0)?.toIntOrNull() ?: return null
            return ServerVersion(
                major = major,
                minor = parts.getOrNull(1)?.toIntOrNull() ?: 0,
                patch = parts.getOrNull(2)?.toIntOrNull() ?: 0,
            )
        }

        // The oldest server this app is known to work against. Bump this in the same change that
        // starts relying on a server feature the previous release didn't have - that is the entire
        // point of it, and a bump with no such dependency will only nag people for no reason.
        //
        // Worth knowing: every core assembly shares one VersionPrefix (core/Directory.Build.props),
        // and it has sat at 1.0.0 since the beginning. Until releases actually carry distinct
        // version numbers, the only mismatch this can detect is a server old enough to report no
        // build at all.
        val MINIMUM_SUPPORTED = ServerVersion(1, 0, 0)
    }
}

// Advisory only, deliberately. A version mismatch between an app and a server on the same LAN is
// usually survivable - most endpoints don't change between releases - and an app that refuses to
// start is far more disruptive than a slightly wrong screen. So this never blocks: it explains what
// is mismatched and leaves the decision to the person holding the phone.
sealed interface ServerCompatibility {
    data object Ok : ServerCompatibility

    // Reported a version older than MINIMUM_SUPPORTED.
    data class Outdated(val serverVersion: String) : ServerCompatibility

    // Answered, but reported no build at all - older than version reporting itself, so older than
    // anything this app has been built against.
    data object PredatesVersionReporting : ServerCompatibility

    // /api/health didn't answer, or didn't parse. Says nothing conclusive about the server's
    // version, so it stays silent rather than guessing - a transient blip on a working connection
    // must not raise a scary banner.
    data object Unknown : ServerCompatibility

    // null when there is nothing worth telling the user, which is the overwhelmingly common case.
    val advisory: String?
        get() = when (this) {
            is Ok, is Unknown -> null
            is Outdated ->
                "This server is running version $serverVersion, older than the " +
                    "${ServerVersion.MINIMUM_SUPPORTED} this app expects. Most things still work, " +
                    "but some screens may be wrong or empty until the server is updated."
            is PredatesVersionReporting ->
                "This server is too old to report its version, which means it predates several " +
                    "features this app uses. Most things still work, but some screens may be " +
                    "wrong or empty until the server is updated."
        }

    companion object {
        fun evaluate(build: ServerBuild?): ServerCompatibility {
            if (build == null) return PredatesVersionReporting
            val version = ServerVersion.parse(build.version) ?: return Unknown
            return if (version < ServerVersion.MINIMUM_SUPPORTED) Outdated(build.version) else Ok
        }
    }
}
