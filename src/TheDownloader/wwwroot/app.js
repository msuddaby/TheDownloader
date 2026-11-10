(() => {
  "use strict";

  const $ = (id) => document.getElementById(id);
  const els = {
    form: $("url-form"), url: $("url"), paste: $("paste"), go: $("go"),
    error: $("error"), info: $("info"), title: $("title"), byline: $("byline"),
    items: $("items"), progress: $("progress"), progressLabel: $("progress-label"),
    barFill: $("bar-fill"), progressDetail: $("progress-detail"),
    done: $("done"), doneName: $("done-name"), doneDetail: $("done-detail"),
    share: $("share"), download: $("download"), shareHint: $("share-hint"),
    expires: $("expires"), again: $("again"),
  };

  // Files above this are not buffered in memory for the share sheet.
  const MAX_SHARE_BYTES = 1.5 * 1024 ** 3;

  const isTouch = navigator.maxTouchPoints > 0;
  const isIOS = /iPad|iPhone|iPod/.test(navigator.userAgent) || (navigator.platform === "MacIntel" && isTouch);

  let currentUrl = null;
  let pollTimer = null;
  let expiryTimer = null;
  let sharedFile = null;

  // ---------- helpers ----------
  function show(...sections) {
    for (const el of [els.error, els.info, els.progress, els.done]) el.hidden = !sections.includes(el);
  }

  function showError(msg) {
    els.error.textContent = msg;
    els.error.hidden = false;
  }

  function fmtBytes(n) {
    if (n == null) return "";
    const u = ["B", "KB", "MB", "GB"];
    let i = 0;
    while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
    return `${n.toFixed(n < 10 && i > 0 ? 1 : 0)} ${u[i]}`;
  }

  function fmtDuration(s) {
    if (!s) return "";
    s = Math.round(s);
    const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
    return (h ? `${h}:${String(m).padStart(2, "0")}` : `${m}`) + `:${String(sec).padStart(2, "0")}`;
  }

  async function api(path, opts = {}) {
    const res = await fetch(path, {
      headers: opts.body ? { "Content-Type": "application/json" } : undefined,
      ...opts,
    });
    const data = res.status === 204 ? null : await res.json().catch(() => null);
    if (!res.ok) throw new Error(data?.error || `Request failed (${res.status})`);
    return data;
  }

  function setBusy(busy) {
    els.go.disabled = busy;
    els.go.textContent = busy ? "Looking up…" : "Get options";
  }

  // ---------- step 1: look up ----------
  async function lookup(url) {
    currentUrl = url;
    reset(false);
    setBusy(true);
    try {
      const info = await api("api/info", { method: "POST", body: JSON.stringify({ url }) });
      renderInfo(info);
    } catch (e) {
      show();
      showError(e.message);
    } finally {
      setBusy(false);
    }
  }

  function renderInfo(info) {
    const multi = info.items.length > 1;
    els.title.textContent = info.title;
    els.byline.textContent = [info.uploader, info.site,
      multi ? `${info.items.length} videos` : fmtDuration(info.items[0].duration)].filter(Boolean).join(" · ");

    els.items.replaceChildren(...info.items.map((item, i) => {
      const section = document.createElement("section");
      section.className = "item";

      const head = document.createElement("div");
      head.className = "item-head";
      const img = document.createElement("img");
      img.alt = "";
      img.referrerPolicy = "no-referrer";
      img.loading = "lazy";
      if (item.thumbnail) img.src = item.thumbnail;
      const text = document.createElement("div");
      const strong = document.createElement("strong");
      strong.textContent = multi ? `Video ${i + 1} of ${info.items.length}` : "Choose a quality";
      const sub = document.createElement("span");
      sub.textContent = [fmtDuration(item.duration),
        multi && item.title !== info.title ? item.title : ""].filter(Boolean).join(" · ");
      text.append(strong, sub);
      head.append(img, text);

      const ul = document.createElement("ul");
      ul.className = "options";
      ul.append(...item.options.map((o) => renderOption(o, item.index)));

      section.append(head, ul);
      return section;
    }));

    show(els.info);
  }

  function renderOption(o, index) {
    const li = document.createElement("li");
    const btn = document.createElement("button");
    btn.type = "button";

    const label = document.createElement("span");
    label.className = "opt-label";
    label.textContent = o.label;

    const meta = document.createElement("span");
    meta.className = "opt-meta";
    const bits = [];
    if (o.approxBytes) bits.push(`~${fmtBytes(o.approxBytes)}`);
    if (o.codec && !o.audioOnly) bits.push(o.codec);
    if (bits.length) meta.append(bits.join(" · "));
    if (!o.audioOnly && o.height) {
      const b = document.createElement("span");
      b.className = "badge " + (o.iosCompatible ? "ok" : "warn");
      b.textContent = o.iosCompatible ? "✓ Photos" : "Converts";
      b.title = o.iosCompatible ? "Already H.264/HEVC - ready for Photos" : "Will be re-encoded to H.264 for Photos (takes longer)";
      meta.append(b);
    }

    btn.append(label, meta);
    btn.addEventListener("click", () => startDownload(o.id, index));
    li.append(btn);
    return li;
  }

  // ---------- step 2: download on the server ----------
  async function startDownload(option, index) {
    show(els.progress);
    setProgress("Starting download…", null, "");
    try {
      const job = await api("api/download", {
        method: "POST",
        body: JSON.stringify({ url: currentUrl, option, index }),
      });
      poll(job.id);
    } catch (e) {
      show(els.info);
      showError(e.message);
    }
  }

  function setProgress(label, pct, detail) {
    els.progressLabel.textContent = label;
    els.progressDetail.textContent = detail || "";
    if (pct == null) {
      els.barFill.classList.add("indeterminate");
      els.barFill.style.width = "";
    } else {
      els.barFill.classList.remove("indeterminate");
      els.barFill.style.width = `${pct}%`;
    }
  }

  function poll(id) {
    clearTimeout(pollTimer);
    const tick = async () => {
      let job;
      try {
        job = await api(`api/jobs/${id}`);
      } catch (e) {
        show();
        showError(e.message);
        return;
      }
      switch (job.status) {
        case "queued":
          setProgress("Waiting for a free slot…", null, "");
          break;
        case "downloading": {
          const part = job.part > 1 ? ` (part ${job.part})` : "";
          setProgress(`Downloading${part}… ${job.progress.toFixed(0)}%`, job.progress,
            [job.speed, job.eta && `${job.eta} left`].filter(Boolean).join(" · "));
          break;
        }
        case "processing":
          setProgress("Processing…", null, "Preparing MP4");
          break;
        case "converting":
          setProgress(`Converting to MP4… ${job.progress.toFixed(0)}%`, job.progress,
            "Re-encoding to H.264 so Photos accepts it");
          break;
        case "done":
          return onDone(job);
        case "error":
          show(els.info);
          showError(job.error || "Download failed.");
          return;
      }
      pollTimer = setTimeout(tick, 800);
    };
    tick();
  }

  // ---------- step 3: get it onto the device ----------
  function canShareFiles() {
    if (!window.isSecureContext || !navigator.canShare) return false;
    try {
      return navigator.canShare({ files: [new File([""], "x.mp4", { type: "video/mp4" })] });
    } catch { return false; }
  }

  async function onDone(job) {
    sharedFile = null;
    els.doneName.textContent = job.fileName;
    els.doneDetail.textContent = fmtBytes(job.fileSize);
    els.download.href = job.fileUrl;
    els.download.setAttribute("download", job.fileName);
    startExpiryCountdown(job.expiresAt);

    const isAudio = job.mimeType.startsWith("audio/");
    els.share.textContent = isAudio ? "Save / Share…" : "Save to Photos";

    // The iOS share sheet ("Save Video") needs the file in memory first, and navigator.share()
    // must be called directly from a tap - so we buffer the file now, then enable the button.
    const useShare = canShareFiles() && isTouch && job.fileSize <= MAX_SHARE_BYTES;
    els.share.hidden = !useShare;
    els.shareHint.hidden = true;

    if (!useShare && isIOS && !window.isSecureContext) {
      els.shareHint.hidden = false;
      els.shareHint.textContent =
        "Tip: open this site over HTTPS to get a one-tap “Save to Photos” button. " +
        "For now, tap Download, then open it from the Downloads list and use Share → Save Video.";
    }

    show(els.progress);
    if (useShare) {
      try {
        await bufferFile(job);
      } catch (e) {
        els.share.hidden = true;
        els.shareHint.hidden = false;
        els.shareHint.textContent = `Couldn't prepare the file for sharing (${e.message}). Use Download instead.`;
      }
    }
    show(els.done);
  }

  async function bufferFile(job) {
    setProgress("Preparing file for your device… 0%", 0, fmtBytes(job.fileSize));
    const res = await fetch(job.fileUrl + "?inline=true");
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const total = Number(res.headers.get("Content-Length")) || job.fileSize || 0;
    const reader = res.body.getReader();
    const chunks = [];
    let received = 0;
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      chunks.push(value);
      received += value.length;
      if (total) {
        const pct = Math.min(100, (received / total) * 100);
        setProgress(`Preparing file for your device… ${pct.toFixed(0)}%`, pct,
          `${fmtBytes(received)} of ${fmtBytes(total)}`);
      }
    }
    sharedFile = new File(chunks, job.fileName, { type: job.mimeType });
  }

  els.share.addEventListener("click", async () => {
    if (!sharedFile) return;
    try {
      // Files only - adding title/text makes iOS share them as separate items.
      await navigator.share({ files: [sharedFile] });
    } catch (e) {
      if (e.name !== "AbortError") {
        els.shareHint.hidden = false;
        els.shareHint.textContent = `Sharing failed (${e.message}). Use Download instead.`;
      }
    }
  });

  function startExpiryCountdown(expiresAt) {
    clearInterval(expiryTimer);
    const end = new Date(expiresAt).getTime();
    const update = () => {
      const left = Math.max(0, Math.round((end - Date.now()) / 1000));
      els.expires.textContent = left > 0
        ? `Available on the server for ${Math.floor(left / 60)}:${String(left % 60).padStart(2, "0")}`
        : "Expired on the server.";
      if (left <= 0) {
        clearInterval(expiryTimer);
        els.download.removeAttribute("href");
      }
    };
    update();
    expiryTimer = setInterval(update, 1000);
  }

  // ---------- wiring ----------
  function reset(clearInput = true) {
    clearTimeout(pollTimer);
    clearInterval(expiryTimer);
    sharedFile = null;
    if (clearInput) els.url.value = "";
    show();
  }

  els.form.addEventListener("submit", (e) => {
    e.preventDefault();
    const url = els.url.value.trim();
    if (url) lookup(url);
  });

  els.url.addEventListener("paste", () => setTimeout(() => {
    const url = els.url.value.trim();
    if (/^https?:\/\//i.test(url)) lookup(url);
  }, 0));

  if (navigator.clipboard?.readText) {
    els.paste.addEventListener("click", async () => {
      try {
        const text = (await navigator.clipboard.readText()).trim();
        if (text) { els.url.value = text; lookup(text); }
      } catch {
        els.url.focus();
      }
    });
  } else {
    els.paste.hidden = true;
  }

  els.again.addEventListener("click", () => { reset(); els.url.focus(); });

  // Support /?url=... so an iOS Shortcut / bookmarklet can hand links straight in.
  const params = new URLSearchParams(location.search);
  const incoming = params.get("url") || params.get("text");
  if (incoming) {
    const match = incoming.match(/https?:\/\/\S+/i);
    if (match) {
      els.url.value = match[0];
      history.replaceState(null, "", location.pathname);
      lookup(match[0]);
    }
  }
})();
