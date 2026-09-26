package com.sentinelcore.android.organizer

// A note's checklist, worked straight on its Markdown body: an item is a line like "- [ ] milk" or
// "- [x] milk" (also "*" and "X", any leading spaces), so nothing about the synced note changes.
// The dashboard, Apple and Windows apps do exactly the same - the cases they all have to agree on
// are in core/docs/note-checklist-cases.json.
object NoteChecklist {
    data class Item(val done: Boolean, val text: String)
    data class Progress(val done: Int, val total: Int)

    // What pressing Enter at the end of a line should do: carry the list on with a fresh marker,
    // or (on an empty item) end the list by taking the marker away.
    sealed class Continuation {
        data class Insert(val prefix: String) : Continuation()
        data object EndList : Continuation()
    }

    private val itemLine = Regex("""^\s*[-*]\s+\[( |x|X)\]\s+(.*)$""")
    private val itemParts = Regex("""^(\s*)([-*])\s+\[( |x|X)\]\s+(.*)$""")
    private val box = Regex("""^(\s*[-*]\s+\[)( |x|X)(\].*)$""")
    private val bullet = Regex("""^(\s*)([-*])\s+(.*)$""")
    private val numbered = Regex("""^(\s*)\d+[.)]\s+(.*)$""")
    private val heading = Regex("""^#{1,6}\s""")
    private val quote = Regex("""^\s*>""")

    /** The item on this one line, ignoring code fences - callers that have a whole body use [lines]. */
    fun item(line: String): Item? = itemLine.matchEntire(line)?.let { Item(it.groupValues[1] != " ", it.groupValues[2]) }

    /** Which lines are code: the ``` fence lines themselves and everything between them. */
    fun codeLines(lines: List<String>): BooleanArray {
        var inside = false
        return BooleanArray(lines.size) { index ->
            if (lines[index].trim().startsWith("```")) {
                inside = !inside
                true
            } else inside
        }
    }

    /** Every line of [body] with its item, or null for a line that isn't one (code never is). */
    fun lines(body: String): List<Pair<String, Item?>> {
        val lines = body.split("\n")
        val code = codeLines(lines)
        return lines.mapIndexed { index, line -> line to (if (code[index]) null else item(line)) }
    }

    fun progress(body: String): Progress {
        val items = lines(body).mapNotNull { it.second }
        return Progress(items.count { it.done }, items.size)
    }

    /** Adds "- [ ] text" under the last item, so it lands in the list rather than after any notes below it. */
    fun addItem(body: String, text: String): String {
        val trimmed = text.trim()
        if (trimmed.isEmpty()) return body
        val line = "- [ ] $trimmed"
        val last = lines(body).indexOfLast { it.second != null }
        if (last < 0) return if (body.isBlank()) line else body.trimEnd('\n') + "\n" + line
        val lines = body.split("\n").toMutableList()
        lines.add(last + 1, line)
        return lines.joinToString("\n")
    }

    fun removeLine(body: String, index: Int): String {
        val lines = body.split("\n").toMutableList()
        if (index !in lines.indices) return body
        lines.removeAt(index)
        return lines.joinToString("\n")
    }

    fun clearChecked(body: String): String =
        lines(body).filterNot { it.second?.done == true }.joinToString("\n") { it.first }

    /** Ticks or unticks the item on line [index]; any other line is left alone. */
    fun toggle(body: String, index: Int): String {
        val lines = lines(body)
        if (index !in lines.indices || lines[index].second == null) return body
        return lines.mapIndexed { i, (line, _) ->
            if (i != index) line
            else box.replace(line) { it.groupValues[1] + (if (it.groupValues[2] == " ") "x" else " ") + it.groupValues[3] }
        }.joinToString("\n")
    }

    /** Turns a plain list (or just lines of words) into tickable items, leaving headings, quotes, code and blank lines be. */
    fun makeChecklist(body: String): String {
        val lines = body.split("\n")
        val code = codeLines(lines)
        return lines.mapIndexed { index, line ->
            val keep = code[index] || line.isBlank() || heading.containsMatchIn(line) || quote.containsMatchIn(line) || item(line) != null
            if (keep) return@mapIndexed line
            val list = bullet.matchEntire(line) ?: numbered.matchEntire(line)
            if (list != null) "${list.groupValues[1]}- [ ] ${list.groupValues.last()}" else "- [ ] ${line.trim()}"
        }.joinToString("\n")
    }

    /** What Enter after [line] should do, or null for an ordinary line. */
    fun continueOnEnter(line: String): Continuation? {
        itemParts.matchEntire(line)?.let {
            val (indent, marker, _, text) = it.destructured
            return if (text.isBlank()) Continuation.EndList else Continuation.Insert("$indent$marker [ ] ")
        }
        bullet.matchEntire(line)?.let {
            val (indent, marker, text) = it.destructured
            return if (text.isBlank()) Continuation.EndList else Continuation.Insert("$indent$marker ")
        }
        return null
    }
}
