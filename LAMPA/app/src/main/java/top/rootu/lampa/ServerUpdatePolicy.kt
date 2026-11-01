package top.rootu.lampa

/** Elapsed time, per running application; a restart intentionally offers again. */
class ServerUpdatePolicy {
    private var declinedVersion = 0
    private var nextOfferAt = 0L
    fun shouldOffer(installed: Int, offered: Int, now: Long): Boolean =
        offered > installed && (declinedVersion == 0 || now >= nextOfferAt)
    fun decline(version: Int, now: Long) {
        declinedVersion = version
        nextOfferAt = now + 3 * 60 * 60 * 1000L
    }
}
