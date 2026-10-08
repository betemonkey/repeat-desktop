package com.betemonkey.onrepeat

import android.annotation.SuppressLint
import android.app.Activity
import android.app.PictureInPictureParams
import android.content.ActivityNotFoundException
import android.content.Intent
import android.content.res.Configuration
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.util.Log
import android.util.Rational
import android.view.WindowManager
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.webkit.WebViewAssetLoader
import androidx.webkit.WebViewCompat
import androidx.webkit.WebViewFeature
import org.json.JSONObject

/** On repeat for Android: the phone page (mobile/, bundled as the app's
 *  assets) in a WebView. The page is served from an https origin inside the
 *  app, because YouTube refuses embeds from pages that send no referrer (the
 *  desktop app does the same with on-repeat.example).
 *
 *  What the app adds to the web version:
 *  - Share → On repeat from YouTube (or any app) plays that video on a loop;
 *  - while a video plays, the screen stays on and leaving the app shrinks it
 *    to picture-in-picture, where the page keeps running so loops still count;
 *  - when the screen locks (or the small window is closed) the video pauses,
 *    and it carries on when the app comes back. Playing on with the screen
 *    off is a YouTube Premium feature, so the app doesn't do it. */
class MainActivity : Activity() {
    private lateinit var web: WebView
    private var pageReady = false
    private var playing = false
    private var resumeOnStart = false

    private val assets by lazy {
        WebViewAssetLoader.Builder()
            .setDomain(HOST)
            .addPathHandler("/", WebViewAssetLoader.AssetsPathHandler(this))
            .build()
    }

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        web = WebView(this)
        setContentView(web)
        web.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = true                  // counts, playlists, the search key
            mediaPlaybackRequiresUserGesture = false  // a shared video starts at once
            setSupportMultipleWindows(false)
            allowFileAccess = false
            allowContentAccess = false
        }
        // the page says when it plays; only the app's own page can say it
        // (not YouTube's frame), the same rule the desktop window keeps
        if (WebViewFeature.isFeatureSupported(WebViewFeature.WEB_MESSAGE_LISTENER)) {
            WebViewCompat.addWebMessageListener(web, "OnRepeatAndroid", setOf(ORIGIN)) { _, message, _, isMainFrame, _ ->
                if (isMainFrame) onPlaying(message.data == "playing")
            }
        }
        web.webViewClient = object : WebViewClient() {
            override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest): WebResourceResponse? =
                assets.shouldInterceptRequest(request.url)

            // the window never leaves the app's page: other links open outside
            override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
                if (!request.isForMainFrame || request.url.host == HOST) return false
                openOutside(request.url)
                return true
            }

            override fun onPageFinished(view: WebView, url: String) {
                pageReady = true
            }
        }
        load(startUrl(sharedText(intent)))
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        val text = sharedText(intent) ?: return
        if (pageReady) web.evaluateJavascript("openShared(${JSONObject.quote(text)})", null)
        else load(startUrl(text))
    }

    private fun load(url: String) {
        pageReady = false
        web.loadUrl(url)
    }

    private fun startUrl(shared: String?) =
        if (shared == null) "$ORIGIN/index.html" else "$ORIGIN/index.html?share=${Uri.encode(shared)}"

    /** YouTube shares "Title https://youtu.be/…?si=…"; the page's own link
     *  parser takes it from there, so only the link is passed on. */
    private fun sharedText(intent: Intent?): String? {
        if (intent?.action != Intent.ACTION_SEND) return null
        val text = intent.getStringExtra(Intent.EXTRA_TEXT) ?: return null
        return LINK.find(text)?.value ?: text.trim()
    }

    private fun openOutside(url: Uri) {
        if (url.scheme != "https" && url.scheme != "http") return
        try {
            startActivity(Intent(Intent.ACTION_VIEW, url))
        } catch (_: ActivityNotFoundException) {
        }
    }

    // ---------- playing: screen on, picture-in-picture ----------

    private fun onPlaying(now: Boolean) {
        Log.d(TAG, "page says playing=$now")
        playing = now
        if (now) window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        else window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) setPictureInPictureParams(pipParams())
    }

    private fun pipParams(): PictureInPictureParams {
        val b = PictureInPictureParams.Builder().setAspectRatio(Rational(16, 9))
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) b.setAutoEnterEnabled(playing)
        return b.build()
    }

    override fun onUserLeaveHint() {
        super.onUserLeaveHint()
        // Android 12+ shrinks on its own (setAutoEnterEnabled); older ones are asked here
        if (playing && Build.VERSION.SDK_INT < Build.VERSION_CODES.S) enterPictureInPictureMode(pipParams())
    }

    override fun onPictureInPictureModeChanged(isInPictureInPictureMode: Boolean, newConfig: Configuration) {
        super.onPictureInPictureModeChanged(isInPictureInPictureMode, newConfig)
        web.evaluateJavascript("document.body.classList.toggle('pip', $isInPictureInPictureMode)", null)
    }

    // ---------- screen locked or window closed: pause, then carry on ----------

    override fun onStop() {
        super.onStop()
        Log.d(TAG, "onStop playing=$playing")
        resumeOnStart = playing
        if (playing) web.evaluateJavascript(PAUSE, null)
    }

    override fun onStart() {
        super.onStart()
        Log.d(TAG, "onStart resume=$resumeOnStart")
        if (resumeOnStart) {
            resumeOnStart = false
            web.evaluateJavascript(PLAY, null)
        }
    }

    override fun onDestroy() {
        web.destroy()
        super.onDestroy()
    }

    companion object {
        private const val TAG = "OnRepeat"
        private const val HOST = "appassets.androidplatform.net"
        private const val ORIGIN = "https://$HOST"
        private val LINK = Regex("https?://\\S+")
        private const val PAUSE = "if (typeof player !== 'undefined' && player && player.pauseVideo) player.pauseVideo()"
        // onStart comes before the page is visible again, and a hidden page's
        // play is ignored (measured on the emulator), so it waits to be seen
        private const val PLAY = "(function go() {" +
            " if (typeof player === 'undefined' || !player || !player.playVideo) return;" +
            " if (document.visibilityState === 'visible') player.playVideo();" +
            " else document.addEventListener('visibilitychange', go, {once: true}); })()"
    }
}
