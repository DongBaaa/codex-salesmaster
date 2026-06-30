import { spawn, execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";
import { chromium } from "playwright";

const scriptDir = dirname(fileURLToPath(import.meta.url));
const projectRoot = resolve(scriptDir, "..");
const testUrl = "http://127.0.0.1:5188";

const server = spawn("npx vite --host 127.0.0.1 --port 5188 --strictPort", {
  cwd: projectRoot,
  stdio: "pipe",
  shell: true,
});

const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
async function waitForServer(url, timeoutMs = 20000) {
  const started = Date.now();
  while (Date.now() - started < timeoutMs) {
    try {
      const response = await fetch(url);
      if (response.ok) return;
    } catch {
      await wait(350);
    }
  }
  throw new Error(`Timed out waiting for ${url}`);
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

let browser;
try {
  await waitForServer(testUrl);
  browser = await chromium.launch({ headless: true });
  const page = await browser.newPage({ viewport: { width: 1440, height: 1100 } });
  await page.goto(testUrl, { waitUntil: "networkidle" });

  const summary = await page.locator(".summaryCard").first().innerText();
  if (!summary.includes("5")) throw new Error(`Expected 5 passed candidates, got: ${summary}`);

  await page.locator(".topActions .primaryButton").click();
  const runState = await page.locator(".runState").innerText();
  if (!runState.includes(":")) throw new Error(`Run state did not update with time: ${runState}`);

  await page.locator(".searchBox input").fill("DEMO-004");
  const filteredRows = await page.locator("tbody tr").count();
  if (filteredRows !== 1) throw new Error(`Expected 1 filtered row, got: ${filteredRows}`);
  const detailAfterSearch = await page.locator(".drawerTitle").innerText();
  if (!detailAfterSearch.includes("DEMO-004")) throw new Error(`Detail did not follow search result: ${detailAfterSearch}`);

  await page.locator(".searchBox input").fill("");
  await page.locator("tbody tr", { hasText: "DEMO-005" }).click();
  const detailAfterClick = await page.locator(".drawerTitle").innerText();
  if (!detailAfterClick.includes("DEMO-005")) throw new Error(`Detail did not update after row click: ${detailAfterClick}`);

  const pegInput = page.locator(".filterGrid .field").nth(3).locator("input");
  await pegInput.fill("1.2");
  await page.locator(".filterPanel .secondaryButton").click();
  const pegValue = await pegInput.inputValue();
  if (pegValue !== "1.5") throw new Error(`Reset did not restore PEG: ${pegValue}`);

  console.log("interaction-smoke: passed");
} finally {
  if (browser) await browser.close();
  stopServerTree();
}
