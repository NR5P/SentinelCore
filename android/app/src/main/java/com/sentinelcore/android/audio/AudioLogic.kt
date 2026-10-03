package com.sentinelcore.android.audio

import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.util.Locale
import kotlin.math.abs
import kotlin.math.pow
import kotlin.random.Random

// The player's rules, kept free of Android so they can be unit-tested: queue editing, shuffle,
// lyric/transcript/chapter lookup by time, Sound Check, and the reporting thresholds.

// ---------------- Queue ----------------

object QueueOps {
    // Moves one entry and keeps the playing index pointing at the same entry.
    fun <T> move(list: List<T>, from: Int, to: Int, current: Int): Pair<List<T>, Int> {
        if (from !in list.indices || to !in list.indices || from == to) return list to current
        val result = list.toMutableList()
        val item = result.removeAt(from)
        result.add(to, item)
        val newCurrent = when {
            current == from -> to
            from < current && to >= current -> current - 1
            from > current && to <= current -> current + 1
            else -> current
        }
        return result to newCurrent
    }

    fun <T> remove(list: List<T>, index: Int, current: Int): Pair<List<T>, Int> {
        if (index !in list.indices) return list to current
        val result = list.toMutableList().apply { removeAt(index) }
        val newCurrent = when {
            result.isEmpty() -> 0
            index < current -> current - 1
            index == current -> current.coerceAtMost(result.lastIndex)
            else -> current
        }
        return result to newCurrent
    }

    // "Play next": straight after the playing entry.
    fun <T> insertNext(list: List<T>, items: List<T>, current: Int): List<T> {
        if (list.isEmpty()) return items
        val at = (current + 1).coerceIn(0, list.size)
        return list.toMutableList().apply { addAll(at, items) }
    }

    // Shuffle keeps the playing entry first and mixes what follows; the original order is kept by
    // the caller so turning shuffle off can put it back.
    fun <T> shuffled(list: List<T>, current: Int, random: Random = Random.Default): List<T> {
        if (list.size < 2) return list
        val playing = list.getOrNull(current) ?: return list.shuffled(random)
        val rest = list.toMutableList().apply { removeAt(current) }.shuffled(random)
        return listOf(playing) + rest
    }

    // Shuffle off: back to the original order, still on the same entry. Entries added while
    // shuffled (not in the original) keep their place after the playing one.
    fun <T> unshuffled(shuffledList: List<T>, original: List<T>, playing: T?): Pair<List<T>, Int> {
        val known = original.filter { it in shuffledList }
        val extras = shuffledList.filter { it !in original }
        val combined = known + extras
        val index = playing?.let { combined.indexOf(it) }?.takeIf { it >= 0 } ?: 0
        return combined to index
    }

    // The server keeps at most 500 entries around the index (§7). Returns the window and the
    // index inside it.
    fun <T> window(list: List<T>, index: Int, max: Int = 500): Pair<List<T>, Int> {
        if (list.size <= max) return list to index
        val start = (index - max / 2).coerceIn(0, list.size - max)
        return list.subList(start, start + max) to (index - start)
    }
}

// ---------------- Lyrics, transcripts, chapters ----------------

object TimedText {
    private val stamp = Regex("""\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?]""")

    // A full LRC file (or embedded lyrics carrying [mm:ss.xx] stamps) into timed lines. Lines with
    // several stamps repeat; metadata tags like [ar:...] are skipped; unstamped text becomes an
    // untimed line. Sorted by time.
    fun parseLrc(text: String): List<LyricLine> {
        val timed = mutableListOf<LyricLine>()
        val untimed = mutableListOf<LyricLine>()
        var offsetSeconds = 0.0
        for (raw in text.lines()) {
            val line = raw.trim()
            if (line.isEmpty()) continue
            Regex("""^\[offset:\s*([+-]?\d+)]$""", RegexOption.IGNORE_CASE).find(line)?.let {
                offsetSeconds = (it.groupValues[1].toDoubleOrNull() ?: 0.0) / 1000.0
                return@let
            }
            val stamps = stamp.findAll(line).toList()
            if (stamps.isEmpty()) {
                if (!Regex("""^\[[a-zA-Z]+:.*]$""").matches(line)) untimed.add(LyricLine(null, line))
                continue
            }
            val words = line.substring(stamps.last().range.last + 1).trim()
            for (match in stamps) {
                val minutes = match.groupValues[1].toInt()
                val seconds = match.groupValues[2].toInt()
                val fraction = match.groupValues[3].takeIf { it.isNotEmpty() }?.let { "0.$it".toDouble() } ?: 0.0
                timed.add(LyricLine(minutes * 60 + seconds + fraction, words))
            }
        }
        if (timed.isEmpty()) return untimed
        return timed.map { it.copy(time = ((it.time ?: 0.0) + offsetSeconds).coerceAtLeast(0.0)) }.sortedBy { it.time }
    }

    // The line playing at `position`: the last one whose time has come. -1 before the first.
    fun activeLyric(lines: List<LyricLine>, position: Double): Int {
        var found = -1
        for ((index, line) in lines.withIndex()) {
            val time = line.time ?: continue
            if (time <= position + 0.05) found = index else break
        }
        return found
    }

    fun activeSegment(segments: List<TranscriptSegment>, position: Double): Int {
        if (segments.isEmpty()) return -1
        var low = 0
        var high = segments.lastIndex
        var found = -1
        while (low <= high) {
            val mid = (low + high) / 2
            if (segments[mid].start <= position) {
                found = mid
                low = mid + 1
            } else {
                high = mid - 1
            }
        }
        return found
    }

    fun activeChapter(chapters: List<Chapter>, position: Double): Int {
        var found = -1
        for ((index, chapter) in chapters.withIndex()) {
            if (chapter.startSeconds <= position + 0.25) found = index else break
        }
        return found
    }

    // Transcript search inside one episode: indexes of segments containing every word typed.
    fun search(segments: List<TranscriptSegment>, query: String): List<Int> {
        val words = query.lowercase(Locale.ROOT).split(Regex("\\s+")).filter { it.isNotBlank() }
        if (words.isEmpty()) return emptyList()
        return segments.indices.filter { index ->
            val text = segments[index].text.lowercase(Locale.ROOT)
            words.all { text.contains(it) }
        }
    }
}

// ---------------- Sound Check ----------------

object SoundCheck {
    // §11: volume = 10^(gain/20), clamped to 1.0; album gain when an album plays in order. A peak
    // keeps a positive gain from clipping. No ReplayGain at all leaves the volume alone.
    fun volume(gain: ReplayGainInfo?, preferAlbum: Boolean): Float {
        gain ?: return 1f
        val db = (if (preferAlbum) gain.albumGain ?: gain.trackGain else gain.trackGain ?: gain.albumGain) ?: return 1f
        val peak = if (preferAlbum) gain.albumPeak ?: gain.trackPeak else gain.trackPeak ?: gain.albumPeak
        var linear = 10.0.pow(db / 20.0)
        if (peak != null && peak > 0) linear = minOf(linear, 1.0 / peak)
        return linear.coerceIn(0.05, 1.0).toFloat()
    }
}

// ---------------- Reporting ----------------

object PlayReporting {
    // §4: a play counts at 30 s listened and (half the track, or 4 minutes); a skip is a track
    // left inside its first 30 s. The server decides; this only decides whether to send `skipped`.
    fun isSkip(secondsListened: Double, completed: Boolean, userSkipped: Boolean): Boolean =
        userSkipped && !completed && secondsListened < 30.0

    fun countsAsPlay(secondsListened: Double, durationSeconds: Double): Boolean =
        secondsListened >= 30.0 && (durationSeconds <= 0 || secondsListened >= durationSeconds * 0.5 || secondsListened >= 240.0)

    // §6: under 10 s nothing is saved, so there is no point sending it.
    fun worthSavingProgress(positionSeconds: Double): Boolean = positionSeconds >= 10.0

    // Played when within 30 s of the end or at 95% - used to show the episode as done right away,
    // before the server answers.
    fun episodeFinished(positionSeconds: Double, durationSeconds: Double): Boolean =
        durationSeconds > 0 && (durationSeconds - positionSeconds <= 30.0 || positionSeconds / durationSeconds >= 0.95)
}

// ---------------- Formatting ----------------

object AudioFormat {
    // "1 album", "3 albums" - for the count lines on artist and genre pages.
    fun count(n: Int, noun: String): String = if (n == 1) "1 $noun" else "$n ${noun}s"

    fun clock(seconds: Double): String {
        val total = seconds.coerceAtLeast(0.0).toLong()
        val h = total / 3600
        val m = (total % 3600) / 60
        val s = total % 60
        return if (h > 0) "%d:%02d:%02d".format(h, m, s) else "%d:%02d".format(m, s)
    }

    // "1 hr 12 min", "43 min", "52 sec".
    fun duration(seconds: Double): String {
        val total = seconds.coerceAtLeast(0.0).toLong()
        val h = total / 3600
        val m = (total % 3600) / 60
        return when {
            h > 0 && m > 0 -> "$h hr $m min"
            h > 0 -> "$h hr"
            m > 0 -> "$m min"
            else -> "$total sec"
        }
    }

    fun minutesLeft(seconds: Double): String = if (seconds < 60) "Almost done" else "${duration(seconds)} left"

    fun bytes(bytes: Long): String {
        if (bytes < 1024) return "$bytes B"
        val units = listOf("KB", "MB", "GB", "TB")
        var value = bytes / 1024.0
        var unit = 0
        while (value >= 1024 && unit < units.lastIndex) {
            value /= 1024
            unit++
        }
        return if (value >= 100) "%.0f %s".format(value, units[unit]) else "%.1f %s".format(value, units[unit])
    }

    fun speed(speed: Double): String {
        // Rounded, not truncated: speeds arrive as Floats, and 1.8f is 1.7999999523 as a Double,
        // which truncation printed as "1.79×".
        val rounded = Math.round(speed * 100) / 100.0
        return if (abs(rounded - rounded.toLong()) < 0.001) "${rounded.toLong()}×" else "${rounded.toString().trimEnd('0')}×"
    }

    fun parseInstant(value: String?): Instant? = value?.let { runCatching { Instant.parse(it) }.getOrNull() ?: runCatching { java.time.OffsetDateTime.parse(it).toInstant() }.getOrNull() }

    // "Today", "Yesterday", "Mon", "Sep 14", "Sep 14, 2024".
    fun day(value: String?, now: Instant = Instant.now(), zone: ZoneId = ZoneId.systemDefault()): String {
        val instant = parseInstant(value) ?: return ""
        val date = instant.atZone(zone).toLocalDate()
        val today = now.atZone(zone).toLocalDate()
        val days = Duration.between(date.atStartOfDay(zone), today.atStartOfDay(zone)).toDays()
        return when {
            days == 0L -> "Today"
            days == 1L -> "Yesterday"
            days in 2..6 -> date.dayOfWeek.getDisplayName(java.time.format.TextStyle.FULL, Locale.getDefault())
            date.year == today.year -> date.format(DateTimeFormatter.ofPattern("MMM d", Locale.getDefault()))
            else -> date.format(DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM))
        }
    }

    fun trackQuality(track: Track): String? {
        val codec = track.codec?.uppercase(Locale.ROOT) ?: return null
        val lossless = codec in setOf("FLAC", "ALAC", "WAV", "AIFF", "APE", "WV", "DSF", "DFF")
        return if (lossless && track.bitDepth != null && track.sampleRate != null) {
            "$codec ${track.bitDepth}/${"%.1f".format(track.sampleRate / 1000.0).removeSuffix(".0")}"
        } else if (track.bitrate != null && track.bitrate > 0) {
            "$codec ${track.bitrate / 1000} kbps"
        } else codec
    }
}
