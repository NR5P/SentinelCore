package com.sentinelcore.android.data

// How the Live grid's tiles are arranged for the signed-in account. The order itself lives on the
// server (see LiveOrderEndpoints.cs) so it follows a person between this app, the dashboard, and
// their other devices - and so one household member rearranging their grid never moves anyone
// else's.
//
// The merge rule is repeated here rather than simply trusting the server's answer because the
// local camera list can be ahead of the last order fetched from it: a camera added moments ago,
// or a tile the person has just dragged and whose save hasn't come back yet.
object LiveCameraOrder {
    // Saved placements first (any pointing at a camera that no longer exists dropped), then
    // cameras the order has never seen - added since - in the camera list's own order, at the end.
    // New cameras landing last is deliberate: appearing at the end is predictable, whereas slotting
    // them in by name would shove carefully placed tiles sideways every time one is added.
    fun arrange(order: List<String>, cameras: List<Camera>): List<Camera> {
        val remaining = cameras.associateByTo(LinkedHashMap(), Camera::id)
        val arranged = ArrayList<Camera>(cameras.size)
        for (id in order) {
            val camera = remaining.remove(id) ?: continue
            arranged.add(camera)
        }
        arranged.addAll(remaining.values)
        return arranged
    }

    // Dropping a dragged tile onto another one: the dragged camera takes that tile's place and the
    // rest close up behind it. Returns the list unchanged when either id isn't in it, so a drag
    // that raced a camera being deleted is a no-op rather than a crash.
    fun move(ids: List<String>, movingId: String, targetId: String): List<String> {
        val from = ids.indexOf(movingId)
        val to = ids.indexOf(targetId)
        if (from < 0 || to < 0 || from == to) return ids

        val moved = ids.toMutableList()
        moved.removeAt(from)
        moved.add(to, movingId)
        return moved
    }
}
