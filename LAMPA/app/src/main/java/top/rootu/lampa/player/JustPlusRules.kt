package top.rootu.lampa.player

/**
 * Что отчёт Just+ реально доказывает о просмотре. Серия отмечается только по собственной позиции из отчёта:
 * — позиция < 0 или нет длины: серию плеер не открывал — она не меняется (диапазон индексов старт→текущая не является доказательством просмотра);
 * — позиция в конце (или после порога «титров»): серия досмотрена;
 * — иначе — начата, с настоящей позицией.
 */
object JustPlusRules {
    data class Item(val index: Int, val posSec: Int, val durSec: Int, val ended: Boolean)

    fun items(positions: List<Int>, durations: List<Int>, completedPercent: Int): List<Item> {
        val out = ArrayList<Item>()
        for (i in positions.indices) {
            val pos = positions[i]
            val dur = durations.getOrElse(i) { -1 }
            if (pos < 0 || dur <= 0) continue
            val ended = pos >= dur || pos * 1000L >= dur * 1000L * completedPercent / 100
            out.add(Item(i, pos, dur, ended))
        }
        return out
    }
}
