package top.rootu.lampa.browser

import android.annotation.SuppressLint
import android.content.ActivityNotFoundException
import android.content.Intent
import android.graphics.Bitmap
import android.net.Uri
import android.net.http.SslError
import android.os.Build
import android.util.Log
import android.view.View
import android.view.ViewGroup
import android.webkit.ConsoleMessage
import android.webkit.JsResult
import android.webkit.SslErrorHandler
import android.webkit.WebChromeClient
import android.webkit.CookieManager
import android.webkit.WebResourceResponse
import top.rootu.lampa.ProfileRuntime
import top.rootu.lampa.helpers.Prefs.appUrl
import java.net.HttpURLConnection
import java.net.URL
import java.io.ByteArrayInputStream
import java.util.concurrent.atomic.AtomicLong
import android.webkit.WebResourceRequest
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebView.setWebContentsDebuggingEnabled
import androidx.annotation.RequiresApi
import androidx.webkit.WebResourceErrorCompat
import androidx.webkit.WebViewClientCompat
import androidx.webkit.WebViewFeature
import top.rootu.lampa.App
import top.rootu.lampa.BuildConfig
import top.rootu.lampa.MainActivity
import top.rootu.lampa.R
import top.rootu.lampa.helpers.Helpers.isTelegramInstalled
import top.rootu.lampa.helpers.Helpers.debugLog
import top.rootu.lampa.helpers.getNetworkErrorString
import top.rootu.lampa.helpers.isAttachedToWindowCompat

// https://developer.android.com/develop/ui/views/layout/webapps/webview#kotlin
class SysView(override val mainActivity: MainActivity, override val viewResId: Int) : Browser {
    private var browser: WebView? = null
    private val profileLoadGeneration = AtomicLong()
    private fun isHomeRoot(url: String): Boolean = try {
        val a = Uri.parse(mainActivity.appUrl); val b = Uri.parse(url)
        BuildConfig.FLAVOR.contains("mc", true) && a.scheme == b.scheme && a.authority == b.authority && a.path.orEmpty().trimEnd('/') == b.path.orEmpty().trimEnd('/')
    } catch (_: Exception) { false }
    override var isDestroyed = false

    companion object {
        const val LOG_TAG = "WEB CONSOLE"
    }

    @SuppressLint("SetJavaScriptEnabled")
    override fun initialize() {
        browser = mainActivity.findViewById(viewResId)
        browser?.apply {
            isFocusable = true
            isFocusableInTouchMode = true
            keepScreenOn = true
            scrollBarStyle = View.SCROLLBARS_INSIDE_OVERLAY
            // No Scrollbars at ALL
            isVerticalScrollBarEnabled = false
            isHorizontalScrollBarEnabled = false
            isScrollContainer = false
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.KITKAT) {
                setWebContentsDebuggingEnabled(true)
            }
        }
        setFocus()
        // Процесс отрисовки страницы не должен умирать, пока открыт наш плеер (иначе после просмотра Lampa перезагружается с главной,
        // а запись о просмотре теряется): по умолчанию, когда окно WebView не видно, Android снимает привязку к процессу и убивает его.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            browser?.setRendererPriorityPolicy(WebView.RENDERER_PRIORITY_IMPORTANT, false)
        }

        val settings = browser?.settings
        @Suppress("DEPRECATION")
        settings?.apply {
            javaScriptEnabled = true
            builtInZoomControls = false
            domStorageEnabled = true
            databaseEnabled = true
            loadWithOverviewMode = true
            useWideViewPort = true
            // обычный режим кеша: статика и постеры не качаются заново; без сети страница открывается из кеша (см. tryOfflineCache)
            cacheMode = WebSettings.LOAD_DEFAULT
            setRenderPriority(WebSettings.RenderPriority.HIGH)
            setNeedInitialFocus(false)
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
            settings?.mixedContentMode = WebSettings.MIXED_CONTENT_ALWAYS_ALLOW
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.JELLY_BEAN_MR1) {
            settings?.mediaPlaybackRequiresUserGesture = false
        }

        browser?.webViewClient = object : WebViewClientCompat() {
            override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest): WebResourceResponse? {
                if (!request.isForMainFrame || !isHomeRoot(request.url.toString()) || !ProfileRuntime.enabled()) return null
                val owner = ProfileRuntime.snapshot()
                if (!owner.optBoolean("ready", false)) return WebResourceResponse("text/html", "UTF-8", ByteArrayInputStream("<html><body>Profile locked</body></html>".toByteArray()))
                var connection: HttpURLConnection? = null
                return try {
                    val c = URL(request.url.toString()).openConnection() as HttpURLConnection
                    connection = c; c.connectTimeout = 5000; c.readTimeout = 10000
                    c.setRequestProperty("User-Agent", request.requestHeaders["User-Agent"] ?: "LampaMediaCenter")
                    c.setRequestProperty("X-Home-Session", owner.optString("token"))
                    val code = c.responseCode
                    if (code !in 200..299) throw java.io.IOException("Root HTTP $code")
                    val html = c.inputStream.bufferedReader(Charsets.UTF_8).use { it.readText() }
                    val script = mainActivity.assets.open("profile-start.js").bufferedReader().use { it.readText() }
                    val bootstrap = "<script>window.__home_scope=" + owner.toString().replace("<", "\\u003c") + ";" + script + "</script>"
                    val match = Regex("<head[^>]*>", RegexOption.IGNORE_CASE).find(html)
                    val result = if (match != null) html.substring(0, match.range.last + 1) + bootstrap + html.substring(match.range.last + 1) else bootstrap + html
                    WebResourceResponse("text/html", "UTF-8", 200, "OK", mapOf("Cache-Control" to "no-store"), ByteArrayInputStream(result.toByteArray(Charsets.UTF_8)))
                } catch (_: Exception) {
                    // Never fall through to an unscoped/cached adult page after a failed bootstrap.
                    WebResourceResponse("text/html", "UTF-8", ByteArrayInputStream("<html><body>Server unavailable. Return to launcher and retry.</body></html>".toByteArray()))
                } finally { connection?.disconnect() }
            }

            override fun onPageStarted(view: WebView?, url: String?, favicon: Bitmap?) {
                super.onPageStarted(view, url, favicon)
                if (url != null && isHomeRoot(url)) mainActivity.mcBeginDocument()
            }

            override fun onPageFinished(view: WebView, url: String) {
                super.onPageFinished(view, url)
                mainActivity.onBrowserPageFinished(view, url)
            }

            // https://developer.android.com/reference/android/webkit/WebViewClient#shouldOverrideUrlLoading(android.webkit.WebView,%20java.lang.String)
            @Deprecated("Deprecated in Java")
            override fun shouldOverrideUrlLoading(view: WebView?, url: String?): Boolean {
                debugLog("shouldOverrideUrlLoading(view, url) view $view url $url")
                url?.let {
                    // tg:// — Handle Telegram link
                    if (it.startsWith("tg://")) {
                        if (isTelegramInstalled(mainActivity)) {
                            val intent = Intent(Intent.ACTION_VIEW, Uri.parse(url))
                            mainActivity.startActivity(intent)
                        } else {
                            // App.toast("Telegram app is not installed. Get in on Google Play.")
                            redirectToTelegramPlayStore()
                        }
                        return true // Indicate that the URL has been handled
                    }
                    // market:// — open system store chooser (Play/F-Droid/…)
                    if (it.startsWith("market://")) {
                        try {
                            mainActivity.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)))
                        } catch (_: ActivityNotFoundException) {
                            // fallback: web version of Play Store
                            mainActivity.startActivity(
                                Intent(
                                    Intent.ACTION_VIEW,
                                    Uri.parse("https://play.google.com/store/apps/details?id=org.telegram.messenger")
                                )
                            )
                        }
                        return true
                    }
                    // intent:// — standard intent-URI parsing (Play Store pages redirect to them)
                    if (it.startsWith("intent://")) {
                        try {
                            val intent = Intent.parseUri(url, Intent.URI_INTENT_SCHEME)
                            mainActivity.startActivity(intent)
                        } catch (_: Exception) {
                            // ignore — WebView will continue as it can
                        }
                        return true
                    }
                }
                return false // Load the URL in the WebView for other links
                // this will fail for non-http(s) links like lampa:// intent:// etc
                //return super.shouldOverrideUrlLoading(view, url)
            }

            // https://developer.android.com/reference/android/webkit/WebViewClient#shouldOverrideUrlLoading(android.webkit.WebView,%20android.webkit.WebResourceRequest)
            @RequiresApi(Build.VERSION_CODES.LOLLIPOP)
            override fun shouldOverrideUrlLoading(
                view: WebView,
                request: WebResourceRequest
            ): Boolean {
                debugLog("shouldOverrideUrlLoading(view, request) view $view request $request")
                val url = request.url.toString()
                // tg:// — Handle Telegram link
                if (url.startsWith("tg://")) {
                    if (isTelegramInstalled(mainActivity)) {
                        val intent = Intent(Intent.ACTION_VIEW, request.url)
                        mainActivity.startActivity(intent)
                    } else {
                        // App.toast("Telegram app is not installed. Get in on Google Play.")
                        redirectToTelegramPlayStore()
                    }
                    return true // Indicate that the URL has been handled
                }
                // market:// — open system store chooser (Play/F-Droid/…)
                if (url.startsWith("market://")) {
                    try {
                        mainActivity.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)))
                    } catch (_: ActivityNotFoundException) {
                        // fallback: web version of Play Store
                        mainActivity.startActivity(
                            Intent(
                                Intent.ACTION_VIEW,
                                Uri.parse("https://play.google.com/store/apps/details?id=org.telegram.messenger")
                            )
                        )
                    }
                    return true
                }
                // intent:// — standard intent-URI parsing (Play Store pages redirect to them)
                if (url.startsWith("intent://")) {
                    try {
                        val intent = Intent.parseUri(url, Intent.URI_INTENT_SCHEME)
                        mainActivity.startActivity(intent)
                    } catch (_: Exception) {
                        // ignore — WebView will continue as it can
                    }
                    return true
                }
                return false // Load the URL in the WebView for other links
                // this will fail for non-http(s) links like lampa:// intent:// etc
                //return super.shouldOverrideUrlLoading(view, request)
            }


            private fun redirectToTelegramPlayStore() {
                // Open the Telegram app page on the Play Store
                val playStoreIntent = Intent(
                    Intent.ACTION_VIEW,
                    Uri.parse("https://play.google.com/store/apps/details?id=org.telegram.messenger")
                )
                try {
                    mainActivity.startActivity(
                        Intent(
                            Intent.ACTION_VIEW,
                            Uri.parse("market://details?id=org.telegram.messenger")
                        )
                    )
                } catch (_: ActivityNotFoundException) {
                    mainActivity.startActivity(playStoreIntent)
                }
            }

            @RequiresApi(Build.VERSION_CODES.LOLLIPOP)
            override fun onReceivedError( // Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP
                view: WebView,
                request: WebResourceRequest,
                error: WebResourceErrorCompat
            ) {
                if (WebViewFeature.isFeatureSupported(
                        WebViewFeature.WEB_RESOURCE_ERROR_GET_CODE
                    ) &&
                    WebViewFeature.isFeatureSupported(
                        WebViewFeature.WEB_RESOURCE_ERROR_GET_DESCRIPTION
                    )
                ) {
                    debugLog("ERROR ${error.errorCode} ${error.description} on load ${request.url}")
                    if (request.url.toString().trimEnd('/')
                            .equals(MainActivity.LAMPA_URL, true)
                    ) {
                        // нет сети или сервер недоступен: сначала пробуем открыть Lampa из кеша, а не окно ввода адреса
                        if (mainActivity.tryOfflineCache(view)) return
                        view.loadUrl("about:blank")
                        // net::ERR_NAME_NOT_RESOLVED [-2]
                        // net::ERR_TIMED_OUT [-8]
                        // ...
                        val reason =
                            view.context.getNetworkErrorString(error.description.toString())
                        val msg = "${
                            view.context.getString(R.string.download_failed_message)
                        } ${MainActivity.LAMPA_URL} – $reason"
                        // net::ERR_INTERNET_DISCONNECTED [-2]
                        val noInternetErr = "net::ERR_INTERNET_DISCONNECTED"
                        if (error.description == noInternetErr) {
                            val html =
                                createErrorHtmlPage(view.context.getNetworkErrorString(noInternetErr))
                            view.loadDataWithBaseURL(null, html, "text/html", "UTF-8", null)
                            view.invalidate()
                        } else
                            mainActivity.showUrlInputDialog(msg)
                    }
                }
            }

            @Deprecated("Deprecated in Java")
            override fun onReceivedError( // Build.VERSION.SDK_INT < Build.VERSION_CODES.LOLLIPOP
                view: WebView?,
                errorCode: Int,
                description: String?,
                failingUrl: String?
            ) {
                debugLog("ERROR $errorCode $description on load $failingUrl")
                if (failingUrl.toString().trimEnd('/').equals(MainActivity.LAMPA_URL, true)) {
                    if (view != null && mainActivity.tryOfflineCache(view)) return
                    view?.loadUrl("about:blank")
                    val reason = App.context.getNetworkErrorString(description.toString())
                    val msg =
                        "${App.context.getString(R.string.download_failed_message)} ${MainActivity.LAMPA_URL} – $reason"
                    val noInternetErr = "net::ERR_INTERNET_DISCONNECTED"
                    if (description == noInternetErr) {
                        view?.apply {
                            val html =
                                createErrorHtmlPage(this.context.getNetworkErrorString(noInternetErr))
                            loadDataWithBaseURL(null, html, "text/html", "UTF-8", null)
                            invalidate()
                        }
                    } else
                        mainActivity.showUrlInputDialog(msg)
                }
            }

            override fun onReceivedSslError(
                view: WebView?,
                handler: SslErrorHandler?,
                error: SslError?
            ) {
                handler?.cancel()
                // This callback also covers images, scripts and frames. Reject the
                // resource without replacing the working local HTTP application.
                val host = runCatching { Uri.parse(error?.url.orEmpty()).host }.getOrNull().orEmpty()
                Log.w("HomeWeb", "ssl_resource_rejected code=${error?.primaryError} host=$host")
            }
        }

        if (BuildConfig.DEBUG)
            browser?.webChromeClient = object : WebChromeClient() {
                override fun onConsoleMessage(consoleMessage: ConsoleMessage): Boolean {
                    when (consoleMessage.messageLevel()) {
                        ConsoleMessage.MessageLevel.LOG -> Log.v(LOG_TAG, consoleMessage.message())
                        ConsoleMessage.MessageLevel.WARNING -> Log.w(LOG_TAG, consoleMessage.message())
                        ConsoleMessage.MessageLevel.ERROR -> Log.e(LOG_TAG, consoleMessage.message())
                        ConsoleMessage.MessageLevel.DEBUG -> Log.d(LOG_TAG, consoleMessage.message())
                        else -> Log.i(LOG_TAG,  consoleMessage.message())
                    }
                    return true
                }

                override fun onJsAlert(
                    view: WebView?,
                    url: String?,
                    message: String?,
                    result: JsResult?
                ): Boolean {
                    if (message != null) {
                        App.toast(message)
                    }
                    return false
                }
            }
        

        mainActivity.onBrowserInitCompleted()
    }

    override fun setUserAgentString(ua: String?) {
        browser?.settings?.userAgentString = ua
    }

    override fun getUserAgentString(): String? {
        return browser?.settings?.userAgentString
    }

    @SuppressLint("AddJavascriptInterface", "JavascriptInterface")
    override fun addJavascriptInterface(jsObject: Any, name: String) {
        browser?.addJavascriptInterface(jsObject, name)
    }

    override fun loadUrl(url: String) {
        if (!isHomeRoot(url)) { browser?.loadUrl(url); return }
        val generation = profileLoadGeneration.incrementAndGet()
        Thread {
            ProfileRuntime.bootstrap(mainActivity, mainActivity.appUrl)
            val owner = ProfileRuntime.snapshot()
            mainActivity.runOnUiThread {
                if (generation != profileLoadGeneration.get() || isDestroyed || !owner.optBoolean("ready", false)) return@runOnUiThread
                val cookie = if (owner.optBoolean("profiles_enabled", false)) "home_session=" + owner.optString("token") + "; Path=/; HttpOnly; SameSite=Lax" else "home_session=; Path=/; Max-Age=0"
                CookieManager.getInstance().setCookie(url, cookie) { if(generation==profileLoadGeneration.get()&&!isDestroyed)browser?.loadUrl(url) }
            }
        }.start()
    }

    override fun pauseTimers() {
        if (!isDestroyed)
            browser?.pauseTimers()
    }

    override fun resumeTimers() {
        browser?.resumeTimers()
    }

    @RequiresApi(Build.VERSION_CODES.KITKAT)
    override fun evaluateJavascript(script: String, resultCallback: (String) -> Unit) {
        browser?.evaluateJavascript(script, resultCallback)
    }

    override fun clearCache(includeDiskFiles: Boolean) {
        browser?.clearCache(includeDiskFiles)
    }

    override fun setKeepVisible(keep: Boolean) {
        (browser as? LampaWebView)?.keepVisible = keep
    }

    override fun destroy() {
        browser?.let {
            // Remove from parent if attached
            if (it.isAttachedToWindowCompat())
                (it.parent as? ViewGroup)?.removeView(it)
        }
        browser?.stopLoading()
        browser?.destroy()
        isDestroyed = true
    }

    override fun setBackgroundColor(color: Int) {
        browser?.setBackgroundColor(color)
    }

    override fun canGoBack(): Boolean {
        return browser?.canGoBack() == true
    }

    override fun goBack() {
        browser?.goBack()
    }

    override fun setFocus() {
        browser?.requestFocus(View.FOCUS_DOWN)
    }

    override fun getView(): View? {
        return browser
    }

    private fun createErrorHtmlPage(
        errorMessage: String,
        iconColor: String = "#D72828",
        textColor: String = "#E6E6E6"
    ): String {
        return """
        <html>
            <body style="margin:0;padding:0;overflow:hidden;">
                <div style="display:table;width:100%;height:100vh;overflow:hidden;">
                    <div align="center" style="display:table-cell;vertical-align:middle;">
                        <svg width="120" height="120" 
                             style="overflow:visible;enable-background:new 0 0 120 120" 
                             viewBox="0 0 32 32" 
                             xmlns="http://www.w3.org/2000/svg">
                            <g>
                                <circle cx="16" cy="16" r="16" style="fill:$iconColor;"/>
                                <path d="M14.5,25h3v-3h-3V25z M14.5,6v13h3V6H14.5z" 
                                      style="fill:$textColor;"/>
                            </g>
                        </svg>
                        <br/><br/>
                        <p style="color:$textColor;">$errorMessage</p>
                    </div>
                </div>
            </body>
        </html>
        """.trimIndent()
    }
}
