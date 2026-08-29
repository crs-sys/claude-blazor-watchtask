// claude-watch dev reload client. Served by the watcher; only loaded when the app
// was launched by claude-watch (CLAUDE_WATCH_PORT env var gates the script include).
// Title-glyph wait indicator, error overlay, and checkmark toast adapted from
// dotnet-watch's aspnetcore-browser-refresh.js (MIT, .NET Foundation).
(function () {
  "use strict";
  if (window.__claudeWatchReload) return; // guard against double inclusion
  window.__claudeWatchReload = true;

  var origin = new URL(document.currentScript.src).origin;
  var retryMs = 1000;
  var TOAST_FLAG = "__claudeWatchReloadToast";

  // ---- wait indicator (animated tab title) ----
  var baseTitle = null;
  var titleTimer = null;

  function startBuilding() {
    clearErrors();
    if (titleTimer) return;
    baseTitle = document.title;
    var glyphs = ["☱", "☲", "☴"]; // ☱ ☲ ☴
    var i = 0;
    titleTimer = setInterval(function () {
      document.title = glyphs[i++ % glyphs.length] + " " + baseTitle;
    }, 240);
  }

  function stopBuilding(failed) {
    if (titleTimer) {
      clearInterval(titleTimer);
      titleTimer = null;
      document.title = failed ? "❌ " + baseTitle : baseTitle; // ❌ prefix on failure
    } else if (failed && !document.title.startsWith("❌")) {
      document.title = "❌ " + document.title;
    }
  }

  // ---- build-error overlay ----
  function clearErrors() {
    var el = document.getElementById("claude-watch-error-overlay");
    if (el) el.remove();
    if (baseTitle && document.title.startsWith("❌")) document.title = baseTitle;
  }

  function showErrors(data) {
    clearErrors();
    var overlay = document.createElement("div");
    overlay.id = "claude-watch-error-overlay";
    overlay.setAttribute("style",
      "z-index:1000000; position:fixed; inset:0; background:rgba(0,0,0,0.6); color:#111;" +
      "overflow:auto; padding:24px; font-family:Consolas,monospace; font-size:13px; cursor:pointer;");
    overlay.title = "Click to dismiss";
    overlay.addEventListener("click", clearErrors);

    var header = overlay.appendChild(document.createElement("div"));
    header.setAttribute("style",
      "background:#b91c1c; color:#fff; font-weight:bold; padding:10px 14px; border-radius:6px 6px 0 0;");
    header.textContent = "claude-watch: round " + (data.round ?? "?") + " failed — app may be down until the next successful round";

    var body = overlay.appendChild(document.createElement("div"));
    body.setAttribute("style", "background:#fee2e2; padding:8px 0; border-radius:0 0 6px 6px;");
    (data.errors || []).forEach(function (err) {
      var item = body.appendChild(document.createElement("div"));
      item.setAttribute("style", "border-left:4px solid #b91c1c; margin:8px 14px; padding:6px 10px; background:#fff;");
      var loc = item.appendChild(document.createElement("div"));
      loc.setAttribute("style", "font-weight:bold;");
      loc.textContent = err.file + (err.line ? "(" + err.line + ")" : "") + ": " + err.code;
      item.appendChild(document.createElement("div")).textContent = err.message;
    });
    document.body.appendChild(overlay);
  }

  // ---- success toast (animated checkmark) ----
  function showToast() {
    if (document.getElementById("claude-watch-toast")) return;
    var el = document.createElement("div");
    el.id = "claude-watch-toast";
    el.setAttribute("style", "z-index:1000000; position:fixed; top:8px; left:8px; width:44px; height:44px;");
    el.innerHTML =
      '<svg viewBox="0 0 52 52" style="filter:drop-shadow(0 1px 2px rgba(0,0,0,0.4));">' +
      '<style>' +
      '#cw-toast-circle{stroke-dasharray:166;stroke-dashoffset:166;animation:cw-stroke .4s cubic-bezier(.65,0,.45,1) forwards}' +
      '#cw-toast-check{stroke-dasharray:48;stroke-dashoffset:48;animation:cw-stroke .3s cubic-bezier(.65,0,.45,1) .35s forwards}' +
      '@keyframes cw-stroke{100%{stroke-dashoffset:0}}' +
      '</style>' +
      '<circle id="cw-toast-circle" cx="26" cy="26" r="24" fill="#22c55e" stroke="#16a34a" stroke-width="2"/>' +
      '<path id="cw-toast-check" fill="none" stroke="#fff" stroke-width="5" stroke-linecap="round" d="M14 27l8 8 16-17"/>' +
      "</svg>";
    document.body.appendChild(el);
    setTimeout(function () { el.remove(); }, 2000);
  }

  // toast after a round-driven full reload (flag survives the navigation)
  try {
    if (sessionStorage.getItem(TOAST_FLAG)) {
      sessionStorage.removeItem(TOAST_FLAG);
      if (document.body) showToast();
      else document.addEventListener("DOMContentLoaded", showToast);
    }
  } catch { /* storage unavailable — skip the toast */ }

  // ---- SSE connection ----
  function connect() {
    var source = new EventSource(origin + "/events");

    source.addEventListener("building", function () {
      startBuilding();
    });

    source.addEventListener("build-error", function (e) {
      stopBuilding(true);
      var data;
      try { data = JSON.parse(e.data); } catch { data = { errors: [] }; }
      showErrors(data);
    });

    source.addEventListener("reload", function () {
      stopBuilding(false);
      try { sessionStorage.setItem(TOAST_FLAG, "1"); } catch { }
      location.reload();
    });

    // css-only round: swap the matching <link> to the watcher-served fresh copy —
    // no page reload, Blazor circuit stays alive. Clone-then-remove-on-load avoids
    // any unstyled flash.
    source.addEventListener("update-css", function (e) {
      var data;
      try { data = JSON.parse(e.data); } catch { return; }
      if (!data || !data.path || !data.url) return;

      var links = document.querySelectorAll('link[rel="stylesheet"]');
      var target = null;
      for (var i = 0; i < links.length; i++) {
        var pathname = new URL(links[i].href, document.baseURI).pathname.replace(/^\//, "");
        // match the app-served route or a previous watcher-served override of it
        if (pathname === data.path || pathname === "asset/" + data.path) { target = links[i]; break; }
      }
      if (!target || target.__claudeWatchLoading) {
        if (!target) console.debug("claude-watch: no stylesheet matches", data.path);
        stopBuilding(false);
        return;
      }

      var fresh = target.cloneNode();
      fresh.href = data.url + "?nonce=" + Date.now();
      target.__claudeWatchLoading = true;
      fresh.addEventListener("load", function () {
        target.remove();
        stopBuilding(false);
        clearErrors();
        if (!data.replay) showToast(); // no toast when a fresh tab is just self-healing
      });
      fresh.addEventListener("error", function () {
        target.__claudeWatchLoading = false;
        fresh.remove();
        stopBuilding(true);
      });
      target.parentNode.insertBefore(fresh, target.nextSibling);
    });

    source.onopen = function () {
      retryMs = 1000;
    };
    source.onerror = function () {
      // watcher restarting or gone — back off and retry
      source.close();
      setTimeout(connect, retryMs);
      retryMs = Math.min(retryMs * 2, 10000);
    };
  }

  connect();
})();
