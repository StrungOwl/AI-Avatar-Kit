const OPENAI_ALLOWED = ["/openai/v1/chat/completions","/openai/v1/audio/transcriptions","/openai/v1/audio/speech","/openai/v1/embeddings","/openai/v1/realtime/client_secrets"];
const SIGNED_URL = "/eleven/v1/convai/conversation/get-signed-url";
const CORS = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
  "Access-Control-Allow-Headers": "content-type, x-app-key",
  "Access-Control-Max-Age": "86400",
};
const reply = (text, status) => new Response(text, { status, headers: CORS });

export default {
  async fetch(request, env) {
    // The browser's safety check. It carries no password, so answer it first.
    if (request.method === "OPTIONS") return new Response(null, { status: 204, headers: CORS });

    const url = new URL(request.url);
    const path = url.pathname;
    const isSignedUrl = request.method === "GET" && path === SIGNED_URL;
    if (request.headers.get("x-app-key") !== env.APP_KEY ||
        (request.method !== "POST" && !isSignedUrl)) return reply("Forbidden", 403);

    const headers = new Headers(request.headers);
    headers.delete("x-app-key");
    headers.delete("host");
    headers.delete("origin");
    let target;
    if (OPENAI_ALLOWED.includes(path)) {
      target = "https://api.openai.com" + path.slice(7) + url.search;
      headers.set("Authorization", "Bearer " + env.OPENAI_API_KEY);
    } else if (isSignedUrl || path.startsWith("/eleven/v1/text-to-speech/")) {
      target = "https://api.elevenlabs.io" + path.slice(7) + url.search;
      headers.set("xi-api-key", env.ELEVENLABS_API_KEY);
    } else return reply("Not found", 404);

    const res = await fetch(target, {
      method: request.method,
      headers,
      body: request.method === "POST" ? request.body : undefined,
    });
    const out = new Response(res.body, res);
    for (const k in CORS) out.headers.set(k, CORS[k]);
    return out;
  },
};
