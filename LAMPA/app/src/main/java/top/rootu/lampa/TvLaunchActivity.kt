package top.rootu.lampa

import android.app.Activity
import android.content.Intent
import android.os.Bundle

/** Ожидающая команда «включить ТВ»: -1 — нет, 0 — последний канал, N — канал с номером N. Забирает страница Lampa. */
object TvLaunch {
    @Volatile var channel = -1
    @Volatile var at = 0L   // когда запущено через TvLaunchActivity (плеер в течение 2 минут работает в режиме «Назад из списка — выход»)
}

/**
 * Отдельная точка входа «Телевидение»: её можно запускать из программ автоматизации как обычную активити приложения
 * (или по действию app.lampac.home.action.TV). Экрана не имеет: передаёт команду странице Lampa, та включает эфир теми же
 * настройками, что и обычный запуск ТВ (последний канал устройства, выбранный движок, буфер и т.д.).
 * Необязательный extra «channel» (целое) — номер канала в списке; без него — последний просмотренный.
 */
class TvLaunchActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        TvLaunch.channel = maxOf(0, intent.getIntExtra("channel", 0))
        TvLaunch.at = System.currentTimeMillis()
        try {
            startActivity(
                Intent(this, MainActivity::class.java)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_REORDER_TO_FRONT)
            )
        } catch (e: Exception) {
            android.util.Log.i("TvLaunch", "не удалось открыть Lampa: ${e.message}")
        }
        finish()
        overridePendingTransition(0, 0)
    }
}
