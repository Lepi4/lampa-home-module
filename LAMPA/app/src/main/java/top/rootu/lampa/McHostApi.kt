package top.rootu.lampa

import android.content.Intent
import android.view.KeyEvent

/**
 * Lampa Media Center: оболочка живёт в том же окне, что и Lampa (поверх её WebView), реализация есть только в сборке mc
 * (top.rootu.lampa.shell.ShellHost, создаётся через рефлексию). В остальных сборках интерфейс не используется.
 */
interface McHostApi {
    val shown: Boolean
    fun attach(startHidden: Boolean)
    fun show()
    fun hide()
    fun onHome()
    fun onResume()
    fun onPause()
    fun onResult(requestCode: Int, resultCode: Int, data: Intent?)
    fun back()
    fun dispatchKey(event: KeyEvent): Boolean
    fun destroy()
}
