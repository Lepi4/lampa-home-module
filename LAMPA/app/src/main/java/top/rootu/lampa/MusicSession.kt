package top.rootu.lampa

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.media.MediaMetadata
import android.media.session.MediaSession
import android.media.session.PlaybackState
import android.os.Build
import android.os.SystemClock
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import kotlin.concurrent.thread

/**
 * Системная медиа-сессия для музыки из плагина (Яндекс Музыка): показывает «что играет» в системе и
 * принимает кнопки ⏮ ⏭ ⏯ с пульта, когда приложение свёрнуто. Команды уходят обратно в плагин.
 */
object MusicSession {
    private const val CHANNEL_ID = "music_now_playing"
    private const val NOTIF_ID = 43

    private var session: MediaSession? = null
    private var coverUrl: String? = null
    private var cover: Bitmap? = null
    private var lastPlayPause = 0L

    // На пульте Shield нет кнопок ⏮⏭, есть только Play/Pause. Режим задаёт плагин:
    //  "next"  — одно нажатие = следующий трек (через 0,45 с, ждём возможное второе), двойное = пауза/продолжить
    //  "pause" — одно нажатие = пауза/продолжить сразу, двойное = следующий трек
    private var ppMode = "next"
    private val ppHandler = android.os.Handler(android.os.Looper.getMainLooper())
    private var ppPending: Runnable? = null

    private fun playPause(activity: MainActivity) {
        if (ppMode == "next") {
            val pending = ppPending
            if (pending != null) {
                ppHandler.removeCallbacks(pending)
                ppPending = null
                send(activity, "toggle")
            } else {
                val r = Runnable { ppPending = null; send(activity, "next") }
                ppPending = r
                ppHandler.postDelayed(r, 450)
            }
        } else {
            val now = SystemClock.elapsedRealtime()
            if (now - lastPlayPause < 450) {
                send(activity, "toggle")
                send(activity, "next")
                lastPlayPause = 0
            } else {
                send(activity, "toggle")
                lastPlayPause = now
            }
        }
    }

    private fun send(activity: MainActivity, cmd: String) {
        activity.runOnUiThread { activity.runVoidJsFunc("window.__ymCmd", "'$cmd'") }
    }

    private fun ensure(activity: MainActivity): MediaSession {
        session?.let { return it }
        val s = MediaSession(activity.applicationContext, "LampaHomeMusic")
        @Suppress("DEPRECATION")
        s.setFlags(MediaSession.FLAG_HANDLES_MEDIA_BUTTONS or MediaSession.FLAG_HANDLES_TRANSPORT_CONTROLS)
        s.setCallback(object : MediaSession.Callback() {
            // любые «вперёд/назад» с разных пультов: стандартный разбор знает не все коды
            override fun onMediaButtonEvent(intent: Intent): Boolean {
                val e = if (Build.VERSION.SDK_INT >= 33) intent.getParcelableExtra(Intent.EXTRA_KEY_EVENT, android.view.KeyEvent::class.java)
                else @Suppress("DEPRECATION") intent.getParcelableExtra<android.view.KeyEvent>(Intent.EXTRA_KEY_EVENT)
                if (e == null) return false
                if (e.action == android.view.KeyEvent.ACTION_DOWN && e.repeatCount == 0) {
                    when (e.keyCode) {
                        android.view.KeyEvent.KEYCODE_MEDIA_NEXT, android.view.KeyEvent.KEYCODE_MEDIA_FAST_FORWARD,
                        android.view.KeyEvent.KEYCODE_MEDIA_SKIP_FORWARD, android.view.KeyEvent.KEYCODE_MEDIA_STEP_FORWARD,
                        android.view.KeyEvent.KEYCODE_CHANNEL_UP -> send(activity, "next")
                        android.view.KeyEvent.KEYCODE_MEDIA_PREVIOUS, android.view.KeyEvent.KEYCODE_MEDIA_REWIND,
                        android.view.KeyEvent.KEYCODE_MEDIA_SKIP_BACKWARD, android.view.KeyEvent.KEYCODE_MEDIA_STEP_BACKWARD,
                        android.view.KeyEvent.KEYCODE_CHANNEL_DOWN -> send(activity, "prev")
                        android.view.KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE, android.view.KeyEvent.KEYCODE_HEADSETHOOK -> playPause(activity)
                        android.view.KeyEvent.KEYCODE_MEDIA_PLAY -> send(activity, "play")
                        android.view.KeyEvent.KEYCODE_MEDIA_PAUSE -> send(activity, "pause")
                        android.view.KeyEvent.KEYCODE_MEDIA_STOP -> send(activity, "stop")
                        else -> return false
                    }
                }
                return true
            }

            override fun onPlay() = send(activity, "play")
            override fun onPause() = send(activity, "pause")
            override fun onSkipToNext() = send(activity, "next")
            override fun onSkipToPrevious() = send(activity, "prev")
            override fun onFastForward() = send(activity, "next")
            override fun onRewind() = send(activity, "prev")
            override fun onStop() = send(activity, "stop")
        })
        // нажатие на уведомление открывает приложение
        val launch = Intent(activity, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP)
        s.setSessionActivity(PendingIntent.getActivity(activity, 0, launch, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT))
        session = s
        return s
    }

    /** json: {title, artist, album, cover, playing, pos, dur}; пустой/off — убрать сессию */
    fun update(activity: MainActivity, json: String) {
        val o = try { JSONObject(json) } catch (_: Exception) { return }
        if (!o.optBoolean("active", true)) { release(activity); return }

        val s = ensure(activity)
        ppMode = if (o.optString("pp") == "pause") "pause" else "next"
        val playing = o.optBoolean("playing", false)
        activity.setMusicPlaying(playing)
        val posMs = (o.optDouble("pos", 0.0) * 1000).toLong()
        val durMs = (o.optDouble("dur", 0.0) * 1000).toLong()
        val title = o.optString("title")
        val artist = o.optString("artist")
        val url = o.optString("cover").takeIf { it.isNotBlank() }

        val md = MediaMetadata.Builder()
            .putString(MediaMetadata.METADATA_KEY_TITLE, title)
            .putString(MediaMetadata.METADATA_KEY_ARTIST, artist)
            .putString(MediaMetadata.METADATA_KEY_ALBUM, o.optString("album"))
            .putLong(MediaMetadata.METADATA_KEY_DURATION, durMs)
        if (url == coverUrl && cover != null) md.putBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART, cover)
        s.setMetadata(md.build())

        val actions = PlaybackState.ACTION_PLAY or PlaybackState.ACTION_PAUSE or PlaybackState.ACTION_PLAY_PAUSE or
            PlaybackState.ACTION_SKIP_TO_NEXT or PlaybackState.ACTION_SKIP_TO_PREVIOUS or
            PlaybackState.ACTION_FAST_FORWARD or PlaybackState.ACTION_REWIND or PlaybackState.ACTION_STOP
        s.setPlaybackState(
            PlaybackState.Builder()
                .setActions(actions)
                .setState(if (playing) PlaybackState.STATE_PLAYING else PlaybackState.STATE_PAUSED, posMs, if (playing) 1f else 0f, SystemClock.elapsedRealtime())
                .build()
        )
        s.isActive = true
        notify(activity, title, artist, playing, s)

        // обложка подгружается отдельно и обновляет метаданные
        if (url != null && url != coverUrl) {
            coverUrl = url
            cover = null
            thread {
                val bmp = try {
                    val c = URL(url).openConnection() as HttpURLConnection
                    c.connectTimeout = 8000; c.readTimeout = 8000
                    c.inputStream.use { BitmapFactory.decodeStream(it) }
                } catch (_: Exception) { null }
                if (bmp != null && coverUrl == url) {
                    cover = bmp
                    activity.runOnUiThread {
                        session?.let { sess ->
                            sess.setMetadata(
                                MediaMetadata.Builder()
                                    .putString(MediaMetadata.METADATA_KEY_TITLE, title)
                                    .putString(MediaMetadata.METADATA_KEY_ARTIST, artist)
                                    .putString(MediaMetadata.METADATA_KEY_ALBUM, o.optString("album"))
                                    .putLong(MediaMetadata.METADATA_KEY_DURATION, durMs)
                                    .putBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART, bmp)
                                    .build()
                            )
                        }
                    }
                }
            }
        }
    }

    private fun notify(ctx: Context, title: String, artist: String, playing: Boolean, s: MediaSession) {
        val nm = ctx.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && nm.getNotificationChannel(CHANNEL_ID) == null) {
            nm.createNotificationChannel(NotificationChannel(CHANNEL_ID, "Музыка", NotificationManager.IMPORTANCE_LOW).apply { setShowBadge(false) })
        }
        val b = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) Notification.Builder(ctx, CHANNEL_ID)
        else @Suppress("DEPRECATION") Notification.Builder(ctx)
        val n = b.setSmallIcon(R.drawable.lampa_icon)
            .setContentTitle(title.ifBlank { "Яндекс Музыка" })
            .setContentText(artist)
            .setOngoing(playing)
            .setShowWhen(false)
            .setVisibility(Notification.VISIBILITY_PUBLIC)
            .setStyle(Notification.MediaStyle().setMediaSession(s.sessionToken))
            .also { bb -> s.controller.sessionActivity?.let { bb.setContentIntent(it) } }
            .build()
        nm.notify(NOTIF_ID, n)
    }

    fun release(ctx: Context) {
        session?.let {
            it.isActive = false
            it.release()
        }
        session = null
        coverUrl = null
        cover = null
        try {
            (ctx.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager).cancel(NOTIF_ID)
        } catch (_: Exception) {
        }
    }
}
