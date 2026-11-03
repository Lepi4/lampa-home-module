package top.rootu.lampa.player

import android.app.Activity
import android.app.AlertDialog
import android.content.Context
import android.widget.Toast
import top.rootu.lampa.ProfileRuntime

/** Edits the same device preferences as the launcher; no per-title overrides. */
object PlayerSettingsDialog {
    fun show(activity: Activity, onClosed: () -> Unit = {}) {
        if (ProfileRuntime.isChild()) {
            Toast.makeText(activity, "Общие настройки доступны во взрослом профиле", Toast.LENGTH_SHORT).show()
            onClosed(); return
        }
        val prefs = activity.getSharedPreferences("home_player", Context.MODE_PRIVATE)
        fun label(value: Boolean) = if (value) "Вкл" else "Выкл"
        fun open() {
            if (activity.isFinishing || activity.isDestroyed) return
            val output = AudioOutputSettings.read(activity)
            val mode = prefs.getInt("drc", 0).coerceIn(0, 2)
            val rows = arrayOf(
                "АФР: ${label(PlayerSettingsStore.afr(activity))}",
                "Проброс звука на ресивер: ${label(output.passthrough)}",
                "DRC: ${arrayOf("Выключена", "Мягкая", "Ночная")[mode]}${if (output.passthrough) " · не применяется" else ""}",
                "Туннелирование A/V (ExoPlayer): ${label(prefs.getBoolean("tunnel", false))}${if (output.effectiveDrc != 0) " · не применяется при DRC" else ""}"
            )
            var changing = false
            AlertDialog.Builder(activity, android.R.style.Theme_Material_Dialog_Alert)
                .setTitle("Все источники · все профили\nСо следующего запуска плеера")
                .setItems(rows) { dialog, index ->
                    changing = true
                    when (index) {
                        0 -> PlayerSettingsStore.chooseAfr(activity, !PlayerSettingsStore.afr(activity))
                        1 -> prefs.edit().putBoolean("audio_passthrough", !output.passthrough).apply()
                        2 -> prefs.edit().putInt("drc", (mode + 1) % 3).apply()
                        3 -> prefs.edit().putBoolean("tunnel", !prefs.getBoolean("tunnel", false)).apply()
                    }
                    dialog.dismiss()
                    activity.window.decorView.post { open() }
                }
                .setNegativeButton("Готово", null)
                .setOnDismissListener { if (!changing) onClosed() }
                .show()
        }
        open()
    }
}
