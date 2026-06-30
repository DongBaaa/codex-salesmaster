import { spawn, execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";

const scriptDir = dirname(fileURLToPath(import.meta.url));
const projectRoot = resolve(scriptDir, "..");
const port = 5189;
const baseUrl = `http://127.0.0.1:${port}`;

const server = spawn("node server/index.mjs", {
  cwd: projectRoot,
  stdio: "pipe",
  shell: true,
  env: {
    ...process.env,
    PORT: String(port),
    APP_BASE_URL: baseUrl,
    AUTH_REQUIRED: "false",
    OAUTH_ENABLED: "false",
    OPENAI_API_KEY: "",
    SESSION_SECRET: "server-smoke-session-secret",
  },
});

const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function waitForServer(timeoutMs = 20000) {
  const started = Date.now();
  while (Date.now() - started < timeoutMs) {
    try {
      const response = await fetch(`${baseUrl}/healthz`);
      if (response.ok) return;
    } catch {
      await wait(250);
    }
  }
  throw new Error(`Timed out waiting for ${baseUrl}`);
}

function stopServerTree() {
  if (!server.pid) return;
  try {
    if (process.platform === "win32") {
      execFileSync("taskkill", ["/pid", String(server.pid), "/T", "/F"], { stdio: "ignore" });
    } else {
      server.kill("SIGTERM");
    }
  } catch {
    try { server.kill("SIGKILL"); } catch {}
  }
}

try {
  await waitForServer();
  const health = await fetch(`${baseUrl}/healthz`).then((r) => r.text());
  if (health.trim() !== "ok") throw new Error(`Unexpected health body: ${health}`);

  const status = await fetch(`${baseUrl}/api/auth/status`).then((r) => r.json());
  if (status.oauthConfigured !== false) throw new Error("OAuth should be disabled in smoke test");
  if (status.aiConfigured !== false) throw new Error("OpenAI should be missing in smoke test");

  const analyze = await fetch(`${baseUrl}/api/research/analyze`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ filters: {}, settings: {}, candidates: [] }),
  });
  if (analyze.status !== 503) throw new Error(`Expected OpenAI 503, got ${analyze.status}`);

  console.log("server-smoke: passed");
} finally {
  stopServerTree();
}
