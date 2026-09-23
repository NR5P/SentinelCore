package com.sentinelcore.android.ui

// Which sections get a tab in the phone's bottom bar, and which are one tap further in, under
// More. The same rules as the iPhone app's TabBarLayout, so someone who uses both sees the same
// bar on each.
//
// A bottom bar has room for five before the labels start to crush. The person picks the tabs (see
// TabBarEditorScreen). Until they do, every section gets one when they all fit, and otherwise the
// first four in AppSection order do and the rest go under More. The navigation rail on a tablet
// has room for everything and ignores all of this.
internal data class TabBarLayout(
    val pinned: List<AppSection>?,
    val visible: List<AppSection>,
) {
    val bar: List<AppSection>
    val more: List<AppSection>
    val showsMore: Boolean get() = more.isNotEmpty()

    init {
        // A pinned section can since have been switched off server-side. It just drops out, and
        // is back in its old place if it's switched on again.
        var chosen = pinned.orEmpty().filter { it in visible }.distinct()
        if (chosen.isEmpty()) chosen = visible.take(limit(visible))
        if (chosen.size == visible.size && chosen.size <= MAX_TABS) {
            bar = chosen
            more = emptyList()
        } else {
            val tabs = chosen.take(MAX_TABS - 1)
            bar = tabs
            more = visible.filter { it !in tabs }
        }
    }

    companion object {
        const val MAX_TABS = 5

        // How many sections can have a tab: all five slots when nothing is left over for a More
        // item, otherwise four, the fifth being More itself.
        fun limit(visible: List<AppSection>): Int =
            if (visible.size <= MAX_TABS) MAX_TABS else MAX_TABS - 1

        // Stored as a comma-separated list of routes; blank means "never customized". Anything
        // unrecognised - a section a later version renamed, say - is skipped rather than failing.
        fun decode(stored: String?): List<AppSection>? {
            val sections = stored.orEmpty().split(",").mapNotNull { name ->
                AppSection.entries.firstOrNull { it.route == name.trim() }
            }
            return sections.ifEmpty { null }
        }

        fun encode(sections: List<AppSection>): String = sections.joinToString(",") { it.route }
    }
}
