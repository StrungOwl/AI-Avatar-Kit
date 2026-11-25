// LIVE TALK IN A WEB BROWSER.  (AvatarKit 2)
//
// On a computer or a phone, AvatarLive.cs opens the live line, reads the
// microphone and plays her voice, all from C#. A browser does not let Unity
// do any of the three. So in a web build this file does them, with the
// browser's own tools, and AvatarLive.cs only reads and writes the messages.
//
//   the line        the browser's WebSocket
//   the microphone  getUserMedia (with the browser's echo removal switched
//                   on, so she does not hear herself through the speakers)
//   her voice       Web Audio: each piece of sound is queued to play right
//                   after the one before it
//
// Unity packs any ".jslib" file in a Plugins folder into the web build.
var AvatarLiveWebLib = {

  $ALW: {
    ws: null, go: "", kind: 0,
    ctx: null, micStream: null, micNode: null, micSource: null, analyser: null, sink: null,
    sources: [], nextTime: 0, level: 0, micLevel: 0,
    dropping: false, dropUpTo: -1,
    rateIn: 24000, rateOut: 24000, prefix: "", suffix: "",
    pending: null, levelBuf: null,

    tell: function (method, text) {
      try { SendMessage(ALW.go, method, text || ""); } catch (e) { }
    },

    b64ToBytes: function (b64) {
      var bin = atob(b64), n = bin.length, out = new Uint8Array(n);
      for (var i = 0; i < n; i++) out[i] = bin.charCodeAt(i);
      return out;
    },

    bytesToB64: function (bytes) {
      var s = "", chunk = 0x8000;
      for (var i = 0; i < bytes.length; i += chunk)
        s += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
      return btoa(s);
    },

    // one piece of her voice: 16-bit numbers -> a buffer -> queued to play
    play: function (b64) {
      if (!ALW.ctx || ALW.dropping) return;
      var bytes = ALW.b64ToBytes(b64), n = bytes.length >> 1;
      if (n === 0) return;
      var buf = ALW.ctx.createBuffer(1, n, ALW.rateOut), ch = buf.getChannelData(0);
      for (var i = 0; i < n; i++) {
        var v = bytes[i * 2] | (bytes[i * 2 + 1] << 8);
        if (v >= 32768) v -= 65536;
        ch[i] = v / 32768;
      }
      var src = ALW.ctx.createBufferSource();
      src.buffer = buf;
      src.connect(ALW.analyser);
      var now = ALW.ctx.currentTime;
      if (ALW.nextTime < now + 0.03) ALW.nextTime = now + 0.03;
      src.start(ALW.nextTime);
      ALW.nextTime += buf.duration;
      ALW.sources.push(src);
      src.onended = function () {
        var k = ALW.sources.indexOf(src);
        if (k >= 0) ALW.sources.splice(k, 1);
      };
    },

    clear: function () {
      for (var i = 0; i < ALW.sources.length; i++) { try { ALW.sources[i].onended = null; ALW.sources[i].stop(); } catch (e) { } }
      ALW.sources = [];
      ALW.nextTime = 0;
    },

    // every message from the line comes through here first
    onMessage: function (text) {
      var m = null;
      try { m = JSON.parse(text); } catch (e) { return; }
      if (!m || !m.type) return;

      if (ALW.kind === 0) {                       // OpenAI
        if (m.type === "response.output_audio.delta") { if (m.delta) ALW.play(m.delta); return; }
        if (m.type === "response.output_audio_transcript.delta") return;      // not needed, and there are many
        if (m.type === "response.created") ALW.dropping = false;
        if (m.type === "input_audio_buffer.speech_started" && ALW.nextTime > (ALW.ctx ? ALW.ctx.currentTime : 0)) {
          ALW.dropping = true; ALW.clear();       // you started to talk over her
        }
      } else {                                    // ElevenLabs agent
        if (m.type === "audio") {
          var a = m.audio_event;
          if (a && a.audio_base_64 && !(a.event_id <= ALW.dropUpTo)) ALW.play(a.audio_base_64);
          return;
        }
        if (m.type === "user_transcript") ALW.dropping = false;
        if (m.type === "interruption") {
          if (m.interruption_event) ALW.dropUpTo = m.interruption_event.event_id;
          ALW.dropping = false; ALW.clear();
        }
      }
      ALW.tell("WebOnMessage", text);
    },

    // the microphone: any rate in -> the rate the service wants -> 16-bit -> text
    onMic: function (input, inRate) {
      var sum = 0;
      for (var i = 0; i < input.length; i++) sum += input[i] * input[i];
      ALW.micLevel = Math.sqrt(sum / input.length);
      if (!ALW.ws || ALW.ws.readyState !== 1) return;

      var ratio = inRate / ALW.rateIn, n = Math.floor(input.length / ratio);
      var bytes = new Uint8Array(n * 2);
      for (var j = 0; j < n; j++) {
        var p = j * ratio, i0 = Math.floor(p), i1 = Math.min(i0 + 1, input.length - 1), f = p - i0;
        var s = input[i0] * (1 - f) + input[i1] * f;
        s = Math.max(-1, Math.min(1, s));
        var v = Math.round(s * 32767);
        bytes[j * 2] = v & 255;
        bytes[j * 2 + 1] = (v >> 8) & 255;
      }
      ALW.ws.send(ALW.prefix + ALW.bytesToB64(bytes) + ALW.suffix);
    }
  },

  AvatarLiveWeb_Open: function (urlPtr, protocolsPtr, goPtr, kind) {
    var url = UTF8ToString(urlPtr), protocols = UTF8ToString(protocolsPtr);
    ALW.go = UTF8ToString(goPtr);
    ALW.kind = kind;
    ALW.dropping = false; ALW.dropUpTo = -1;
    try {
      ALW.ws = protocols ? new WebSocket(url, protocols.split(",")) : new WebSocket(url);
    } catch (e) { ALW.tell("WebOnClosed", "could not open the line: " + e); return; }
    var ws = ALW.ws;
    ws.onopen = function () { if (ALW.ws === ws) ALW.tell("WebOnOpen", ""); };
    ws.onmessage = function (ev) { if (ALW.ws === ws && typeof ev.data === "string") ALW.onMessage(ev.data); };
    ws.onclose = function (ev) { if (ALW.ws === ws) { ALW.ws = null; ALW.tell("WebOnClosed", "the other side hung up (" + ev.code + ") " + (ev.reason || "")); } };
    ws.onerror = function () { };
  },

  AvatarLiveWeb_Send: function (textPtr) {
    if (ALW.ws && ALW.ws.readyState === 1) ALW.ws.send(UTF8ToString(textPtr));
  },

  AvatarLiveWeb_StartAudio: function (rateIn, rateOut, prefixPtr, suffixPtr) {
    ALW.rateIn = rateIn; ALW.rateOut = rateOut;
    ALW.prefix = UTF8ToString(prefixPtr); ALW.suffix = UTF8ToString(suffixPtr);
    var AC = window.AudioContext || window.webkitAudioContext;
    if (!ALW.ctx) {
      ALW.ctx = new AC();
      ALW.analyser = ALW.ctx.createAnalyser();
      ALW.analyser.fftSize = 512;
      ALW.analyser.connect(ALW.ctx.destination);
      ALW.levelBuf = new Float32Array(ALW.analyser.fftSize);
      // a browser keeps sound off until the person touches the page
      var wake = function () { if (ALW.ctx && ALW.ctx.state === "suspended") ALW.ctx.resume(); };
      document.addEventListener("pointerdown", wake);
      document.addEventListener("keydown", wake);
    }
    if (ALW.ctx.state === "suspended") ALW.ctx.resume();
    if (ALW.micStream) return;

    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
      ALW.tell("WebOnMicProblem", "This page can not use the microphone. Open it over https.");
      return;
    }
    navigator.mediaDevices.getUserMedia({
      audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true, channelCount: 1 }, video: false
    }).then(function (stream) {
      ALW.micStream = stream;
      ALW.micSource = ALW.ctx.createMediaStreamSource(stream);
      ALW.micNode = ALW.ctx.createScriptProcessor(2048, 1, 1);
      ALW.micNode.onaudioprocess = function (ev) { ALW.onMic(ev.inputBuffer.getChannelData(0), ALW.ctx.sampleRate); };
      ALW.sink = ALW.ctx.createGain();
      ALW.sink.gain.value = 0;                    // the mic must reach an output to run, but must not be heard
      ALW.micSource.connect(ALW.micNode);
      ALW.micNode.connect(ALW.sink);
      ALW.sink.connect(ALW.ctx.destination);
    }).catch(function (err) {
      ALW.tell("WebOnMicProblem", "The browser did not give the microphone: " + err);
    });
  },

  AvatarLiveWeb_Clear: function (drop) {
    if (drop) ALW.dropping = true;
    ALW.clear();
  },

  AvatarLiveWeb_IsPlaying: function () {
    return ALW.ctx && ALW.nextTime > ALW.ctx.currentTime + 0.01 ? 1 : 0;
  },

  // how loud her voice is right now (for the lip sync)
  AvatarLiveWeb_Level: function () {
    if (!ALW.analyser || !ALW.ctx || ALW.nextTime <= ALW.ctx.currentTime) return 0;
    ALW.analyser.getFloatTimeDomainData(ALW.levelBuf);
    var sum = 0;
    for (var i = 0; i < ALW.levelBuf.length; i++) sum += ALW.levelBuf[i] * ALW.levelBuf[i];
    return Math.sqrt(sum / ALW.levelBuf.length);
  },

  AvatarLiveWeb_MicLevel: function () { return ALW.micLevel; },

  AvatarLiveWeb_Close: function () {
    if (ALW.ws) { var ws = ALW.ws; ALW.ws = null; try { ws.close(); } catch (e) { } }
    ALW.clear();
    if (ALW.micNode) { try { ALW.micNode.disconnect(); ALW.micSource.disconnect(); ALW.sink.disconnect(); } catch (e) { } ALW.micNode = null; }
    if (ALW.micStream) { ALW.micStream.getTracks().forEach(function (t) { t.stop(); }); ALW.micStream = null; }
    ALW.micLevel = 0;
  }
};

autoAddDeps(AvatarLiveWebLib, '$ALW');
mergeInto(LibraryManager.library, AvatarLiveWebLib);
