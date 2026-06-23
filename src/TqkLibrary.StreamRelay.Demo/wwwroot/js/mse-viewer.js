// Minimal MSE viewer: streams the relay's fragmented-MP4 endpoint into a
// MediaSource SourceBuffer. The server emits an init segment (ftyp+moov) first,
// then media fragments (moof+mdat), which is exactly what appendBuffer expects.
(function () {
  "use strict";

  const video = document.getElementById("player");
  const logEl = document.getElementById("log");
  const streamInput = document.getElementById("streamId");
  const playBtn = document.getElementById("play");
  const stopBtn = document.getElementById("stop");

  let abortController = null;

  function log(msg) {
    const line = `[${new Date().toLocaleTimeString()}] ${msg}`;
    logEl.textContent = (logEl.textContent + "\n" + line).split("\n").slice(-200).join("\n");
    logEl.scrollTop = logEl.scrollHeight;
  }

  // H.264 + AAC in MP4. Browsers negotiate the exact profile from the init
  // segment, so a generic codec string is enough to open the SourceBuffer.
  const MIME = 'video/mp4; codecs="avc1.640029,mp4a.40.2"';
  const MIME_VIDEO_ONLY = 'video/mp4; codecs="avc1.640029"';

  function pickMime() {
    if (MediaSource.isTypeSupported(MIME)) return MIME;
    if (MediaSource.isTypeSupported(MIME_VIDEO_ONLY)) return MIME_VIDEO_ONLY;
    return MIME;
  }

  async function play() {
    stop();
    const id = (streamInput.value || "").trim();
    if (!id) { log("enter a stream GUID first."); return; }

    const mediaSource = new MediaSource();
    video.src = URL.createObjectURL(mediaSource);
    abortController = new AbortController();

    mediaSource.addEventListener("sourceopen", async () => {
      let sourceBuffer;
      try {
        sourceBuffer = mediaSource.addSourceBuffer(pickMime());
        sourceBuffer.mode = "sequence";
      } catch (e) {
        log("addSourceBuffer failed: " + e.message);
        return;
      }

      const queue = [];
      let updating = false;

      function pump() {
        if (updating || queue.length === 0 || sourceBuffer.updating) return;
        updating = true;
        try {
          sourceBuffer.appendBuffer(queue.shift());
        } catch (e) {
          log("appendBuffer error: " + e.message);
          updating = false;
        }
      }
      sourceBuffer.addEventListener("updateend", () => { updating = false; pump(); });

      try {
        const url = `/relay/view/${id}.mp4`;
        log("fetching " + url);
        const resp = await fetch(url, { signal: abortController.signal });
        if (!resp.ok) { log("server returned " + resp.status); return; }
        log("streaming...");

        const reader = resp.body.getReader();
        while (true) {
          const { done, value } = await reader.read();
          if (done) { log("stream ended."); break; }
          if (value && value.byteLength) { queue.push(value); pump(); }
        }
      } catch (e) {
        if (e.name !== "AbortError") log("fetch error: " + e.message);
      } finally {
        try { if (mediaSource.readyState === "open") mediaSource.endOfStream(); } catch (_) {}
      }
    });
  }

  function stop() {
    if (abortController) { abortController.abort(); abortController = null; }
    try { video.removeAttribute("src"); video.load(); } catch (_) {}
  }

  playBtn.addEventListener("click", play);
  stopBtn.addEventListener("click", stop);
})();
