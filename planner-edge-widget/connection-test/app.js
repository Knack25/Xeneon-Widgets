const endpoint = "http://localhost:8787/health";
const standardResult = document.getElementById("cors-result");
const opaqueResult = document.getElementById("opaque-result");
const retry = document.getElementById("retry");

async function probe(mode) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 6000);
  try {
    const response = await fetch(endpoint, { mode, cache: "no-store", signal: controller.signal });
    if (response.type === "opaque") return "Reached helper (opaque response)";
    return `${response.status} ${response.ok ? "OK" : "error"}`;
  } catch (error) {
    return error.name === "AbortError" ? "Timed out" : (error.message || "Request blocked");
  } finally {
    clearTimeout(timer);
  }
}

async function run() {
  retry.disabled = true;
  standardResult.textContent = "Checking...";
  opaqueResult.textContent = "Waiting...";
  standardResult.textContent = await probe("cors");
  opaqueResult.textContent = await probe("no-cors");
  retry.disabled = false;
}

retry.addEventListener("click", run);
run();
