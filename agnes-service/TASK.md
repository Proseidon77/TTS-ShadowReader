# Task: Agnes API service + simple frontend on this machine (77B)

You are Claude Code running on the instabox machine "77B" (Debian 13, 4 vCPU, Python 3.13.5, Node 22). Your operator is another Muse (A001) who will verify your work, run deployment steps that your permissions deny, and report to the user. Work in `/data/workspace/agnes-service/` (create it).

The user (sword) asked for: a service on this machine that wraps the Agnes AI API behind an easy-to-use interface to generate TEXT, IMAGES and VIDEO, plus a simple frontend page that calls that interface and displays the returned text / image / video.

## Verified facts about the Agnes API (from the operator, already tested 2026-11-18)

- Base: `https://apihub.agnes-ai.com`, OpenAI-compatible, auth `Authorization: Bearer <API KEY>`.
- Models list (GET /v1/models, 10 models):
  - Text: agnes-2.5-flash, agnes-2.5-pro, agnes-2.5-pro-alpha, agnes-2.5-pro-beta, agnes-3.0-flash
  - Image: agnes-image-2.0-flash, agnes-image-2.1-flash, agnes-image-2.5-flash
  - Video: agnes-video-2.5, agnes-video-2.5-flash
- Chat: POST /v1/chat/completions with {model, messages} — verified 200 with agnes-2.5-flash. Response is OpenAI-style; message also carries `reasoning_content`. Note: a bare "Hello!" cost 286 prompt tokens (provider adds prompt overhead).
- Image: POST /v1/images/generations with {model, prompt, size, extra_body:{response_format:"url"|"b64_json"}} — verified 200 in 13.6s with agnes-image-2.5-flash, size "1K", format url. Sizes are tiers "1K"/"2K"/"3K"/"4K". `response_format` MUST go inside `extra_body`, not top level. Image-to-image input goes in `extra_body.image`. Returned data[0].url was a PNG (1024x1024 for 1K).
- Video: NOT yet tested by anyone. You must research the actual request/response format before implementing: read the official docs starting at https://www.agnes-ai.com/en/docs/quickstart and follow links to any video/image API pages; if the format cannot be confirmed from docs, design the video endpoint as a thin pass-through matching the most likely OpenAI-style/async-task pattern AND clearly mark in DESIGN.md and in your final report what is confirmed vs assumed. Do not present a guess as verified.

## API key — critical constraint

- The real Agnes API key is NOT available on this machine and must NEVER be hardcoded, committed, or printed.
- The service must read it from environment variable `AGNES_API_KEY`, loaded from a local file `/data/workspace/agnes-service/.env` (format `AGNES_API_KEY=...`, permissions 600) that is NOT part of any repo. Ship a `.env.example` with a placeholder.
- If the key is absent, the service must still start: `/health` and the frontend work, generation endpoints return a clear JSON error like {"error":"AGNES_API_KEY not configured"} with HTTP 503. This lets us deploy and smoke-test everything except real generation; the operator will arrange the key afterwards and re-test.

## Deliverables

1. `DESIGN.md` — short design note first: stack choice and why, endpoint list, request/response JSON shapes for our own API, how video was researched and what was found (confirmed vs assumed), error handling, timeout choices (image/video generation can take 60–360s — our endpoints must not time out prematurely; consider async job + polling for video, and say why), security notes (key handling; the published page will be public — note the risk that anyone with the URL can spend the user's Agnes quota, and implement a simple optional access token for generation endpoints if it can be done simply: e.g. header `X-Access-Token` checked against env `SERVICE_TOKEN` when that env var is set; frontend has a token field stored in localStorage).
2. Backend service (choose the lightest stack that fits this machine; strongly prefer Python 3.13 standard library only — no pip installs are guaranteed to be permitted in this environment; if you choose a framework, justify it and provide a stdlib fallback note):
   - `GET /` → serves the frontend (single HTML file, inline CSS/JS is fine)
   - `GET /health` → {"status":"ok", "key_configured": true/false, ...}
   - `GET /api/models` → the known model list grouped by type (may proxy Agnes /v1/models when key present, else return the static known list)
   - `POST /api/text` {prompt, model?, system?} → {text, reasoning?, usage?, model}
   - `POST /api/image` {prompt, model?, size?} → {url or b64, model, size} (prefer returning the Agnes URL plus, when format is b64, pass it through)
   - `POST /api/video` {prompt, model?} → per your research (sync URL or job id); plus `GET /api/video/status/<id>` if async
   - All generation endpoints: JSON in/out, proper error propagation (include Agnes HTTP status and first part of error body on failure), generous upstream timeouts, no key in logs.
3. Frontend (single page, served by the backend): three tabs/sections — Text, Image, Video — each with prompt input, model selector (populated sensibly), generate button, loading indicator with elapsed-time hint for image/video, and result area rendering text / <img> / <video controls>. Plain, clean, usable on mobile. Chinese UI labels are fine (user is Chinese-speaking); keep it simple.
4. `agnes-service.service` — a systemd unit file (in the project dir) for running the service on port **8793** (8080, 8791, 8792 are already used on this machine), binding 0.0.0.0, WorkingDirectory = project dir, EnvironmentFile = the .env, Restart=on-failure. Plus `install.sh` that copies the unit, daemon-reloads, enables and starts it. You may not be able to run sudo/systemd yourself — if a command is denied, leave it for the operator and say so in your report.
5. `smoke.sh` or `smoke.py` — checks /health, /, /api/models locally, and (only if key configured) one real text generation. Also a `README.md` with how to run manually (`python3 server.py` or equivalent), how to configure the key, and the endpoint list.

## Rules

- Do not touch anything outside `/data/workspace/agnes-service/` except reading docs on the web. Existing services (dashboard on 8080, tts-web on 8791, dbapi on 8792, PostgreSQL) must not be disturbed.
- If Bash commands are denied by permissions, do NOT silently skip: finish all file work, list exactly which commands were denied in your final report, and leave scripts for the operator to run.
- Test what you can: at minimum syntax-check the Python (py_compile) and, if Bash allows, start the server briefly and curl /health and /.
- Final report (your reply text) must include: design summary, file list, what you tested and the actual results, what is blocked on the API key, what is blocked on operator deployment steps, and the confirmed-vs-assumed status of the video endpoint.
