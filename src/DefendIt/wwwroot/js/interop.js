// DefendIt browser interop: speech synthesis, speech recognition, charts, localStorage, self-view camera.
window.defendit = (() => {
  // ---------- Speech synthesis ----------
  let voicesCache = [];
  function loadVoices() {
    voicesCache = window.speechSynthesis ? speechSynthesis.getVoices() : [];
    return voicesCache;
  }
  if (window.speechSynthesis) {
    loadVoices();
    speechSynthesis.onvoiceschanged = loadVoices;
  }

  function pickVoice(lang, index) {
    const all = voicesCache.length ? voicesCache : loadVoices();
    const prefix = lang.slice(0, 2).toLowerCase();
    let matches = all.filter(v => v.lang && v.lang.toLowerCase().startsWith(prefix));
    // Prefer natural/online voices (Google, Microsoft Online) — they sound far less robotic.
    matches.sort((a, b) => score(b) - score(a));
    if (!matches.length) return null;
    return matches[index % matches.length];
  }
  function score(v) {
    const n = v.name.toLowerCase();
    let s = 0;
    if (n.includes('natural') || n.includes('online')) s += 3;
    if (n.includes('google')) s += 2;
    if (v.lang === 'en-GB' || v.lang === 'fr-FR') s += 1;
    return s;
  }

  let keepAlive = null;
  function speak(text, lang, pitch, rate, voiceIndex) {
    return new Promise(resolve => {
      if (!window.speechSynthesis) { resolve(false); return; }
      speechSynthesis.cancel();
      const u = new SpeechSynthesisUtterance(text);
      u.lang = lang;
      u.pitch = pitch;
      u.rate = rate;
      const v = pickVoice(lang, voiceIndex);
      if (v) u.voice = v;
      let done = false;
      const finish = ok => { if (!done) { done = true; clearInterval(keepAlive); resolve(ok); } };
      u.onend = () => finish(true);
      u.onerror = () => finish(false);
      // Chrome pauses long utterances after ~15s; nudge it.
      clearInterval(keepAlive);
      keepAlive = setInterval(() => { if (speechSynthesis.speaking) { speechSynthesis.pause(); speechSynthesis.resume(); } }, 10000);
      // Safety net in case onend never fires.
      setTimeout(() => finish(true), Math.max(6000, text.split(/\s+/).length * 600));
      speechSynthesis.speak(u);
    });
  }
  function stopSpeaking() { if (window.speechSynthesis) speechSynthesis.cancel(); }

  // ---------- Speech recognition ----------
  const SR = window.SpeechRecognition || window.webkitSpeechRecognition;
  let rec = null, active = false, finalText = '', dotnet = null, silenceTimer = null, heardSomething = false;

  function recognitionSupported() { return !!SR; }

  function resetSilence() {
    clearTimeout(silenceTimer);
    silenceTimer = setTimeout(() => {
      if (active && heardSomething && dotnet) dotnet.invokeMethodAsync('OnSilence');
    }, 4000);
  }

  function startListening(ref, lang) {
    if (!SR) return false;
    stopListening();
    dotnet = ref;
    finalText = '';
    heardSomething = false;
    active = true;
    rec = new SR();
    rec.lang = lang;
    rec.continuous = true;
    rec.interimResults = true;
    rec.onresult = e => {
      let interim = '';
      for (let i = e.resultIndex; i < e.results.length; i++) {
        const r = e.results[i];
        if (r.isFinal) finalText += (finalText ? ' ' : '') + r[0].transcript.trim();
        else interim += r[0].transcript;
      }
      heardSomething = true;
      resetSilence();
      dotnet.invokeMethodAsync('OnTranscript', finalText, interim);
    };
    rec.onerror = e => {
      if (e.error === 'no-speech' || e.error === 'aborted') return;
      active = false;
      dotnet && dotnet.invokeMethodAsync('OnSpeechError', e.error);
    };
    // Chrome ends continuous recognition on its own after a pause; restart while we are still listening.
    rec.onend = () => { if (active) { try { rec.start(); } catch { } } };
    try { rec.start(); } catch { return false; }
    resetSilence();
    return true;
  }

  function stopListening() {
    active = false;
    clearTimeout(silenceTimer);
    if (rec) { try { rec.onend = null; rec.stop(); } catch { } rec = null; }
    return finalText;
  }

  // ---------- Camera self-view (preview only: never recorded, never analyzed) ----------
  let camStream = null;
  async function startCamera(videoId) {
    try {
      camStream = await navigator.mediaDevices.getUserMedia({ video: true, audio: false });
      const el = document.getElementById(videoId);
      if (el) el.srcObject = camStream;
      return true;
    } catch { return false; }
  }
  function stopCamera() {
    if (camStream) camStream.getTracks().forEach(t => t.stop());
    camStream = null;
  }

  async function micPermission() {
    try {
      const s = await navigator.mediaDevices.getUserMedia({ audio: true });
      s.getTracks().forEach(t => t.stop());
      return true;
    } catch { return false; }
  }

  // ---------- Charts ----------
  const charts = {};
  function css(name) { return getComputedStyle(document.documentElement).getPropertyValue(name).trim(); }

  function renderRadar(canvasId, labels, values) {
    const el = document.getElementById(canvasId);
    if (!el || !window.Chart) return;
    charts[canvasId]?.destroy();
    const accent = css('--accent');
    charts[canvasId] = new Chart(el, {
      type: 'radar',
      data: {
        labels,
        datasets: [{
          data: values,
          backgroundColor: accent + '33',
          borderColor: accent,
          pointBackgroundColor: accent,
          borderWidth: 2,
          pointRadius: 4,
        }],
      },
      options: {
        responsive: true,
        maintainAspectRatio: true,
        plugins: { legend: { display: false } },
        scales: {
          r: {
            min: 0, max: 10,
            ticks: { stepSize: 2, backdropColor: 'transparent', color: css('--muted'), font: { size: 10 } },
            grid: { color: css('--rule') },
            angleLines: { color: css('--rule') },
            pointLabels: { color: css('--ink'), font: { size: 13, family: css('--font-sans') } },
          },
        },
      },
    });
  }

  function renderTrend(canvasId, labels, values) {
    const el = document.getElementById(canvasId);
    if (!el || !window.Chart) return;
    charts[canvasId]?.destroy();
    const accent = css('--accent');
    charts[canvasId] = new Chart(el, {
      type: 'line',
      data: { labels, datasets: [{ data: values, borderColor: accent, backgroundColor: accent, tension: 0.25, pointRadius: 4 }] },
      options: {
        plugins: { legend: { display: false } },
        scales: {
          y: { min: 0, max: 100, grid: { color: css('--rule') }, ticks: { color: css('--muted') } },
          x: { grid: { display: false }, ticks: { color: css('--muted') } },
        },
      },
    });
  }

  // ---------- localStorage (sessions live only on this device) ----------
  const KEY = 'defendit.sessions.v1';
  function readAll() {
    try { return JSON.parse(localStorage.getItem(KEY) || '{}'); } catch { return {}; }
  }
  function writeAll(o) {
    try { localStorage.setItem(KEY, JSON.stringify(o)); return true; } catch { return false; }
  }
  function saveSession(id, json) { const all = readAll(); all[id] = json; return writeAll(all); }
  function getSession(id) { return readAll()[id] ?? null; }
  function listSessions() { return Object.values(readAll()); }
  function deleteSession(id) { const all = readAll(); delete all[id]; return writeAll(all); }
  function deleteAll() { try { localStorage.removeItem(KEY); } catch { } return true; }

  function printPage() { window.print(); }
  function scrollToId(id) { document.getElementById(id)?.scrollIntoView({ behavior: 'smooth', block: 'start' }); }

  return {
    speak, stopSpeaking, recognitionSupported, startListening, stopListening,
    startCamera, stopCamera, micPermission,
    renderRadar, renderTrend,
    saveSession, getSession, listSessions, deleteSession, deleteAll,
    printPage, scrollToId,
  };
})();
