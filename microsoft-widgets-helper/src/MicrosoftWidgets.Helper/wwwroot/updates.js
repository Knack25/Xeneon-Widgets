const status = document.querySelector('#update-status');
const check = document.querySelector('#check-updates');
const install = document.querySelector('#install-update');
let current;
let requestPending = false;
let restarting = false;

async function request(path, method = 'GET', body) {
  const response = await fetch(path, {
    method, headers: { 'X-Microsoft-Widgets-Update': '1', 'Content-Type': 'application/json' },
    body: body ? JSON.stringify(body) : undefined
  });
  const result = await response.json();
  if (!response.ok) throw new Error(result.message || 'Unable to contact the update service.');
  return result;
}

function render(value) {
  current = value;
  const busy = requestPending || ['checking', 'downloading', 'installing'].includes(value.state);
  check.disabled = busy;
  install.disabled = busy;
  install.hidden = !value.availableVersion || !value.canInstall;
  const messages = {
    idle: 'Updates are checked automatically at startup and daily.',
    checking: 'Checking for updates...',
    current: `Microsoft Widgets ${value.currentVersion} is up to date.`,
    available: `Microsoft Widgets ${value.availableVersion} is available (installed: ${value.currentVersion}).`,
    downloading: 'Downloading and verifying the update...',
    installing: 'Installing update. Waiting for the helper to restart...',
    error: value.message || 'Unable to check for updates. Try again.'
  };
  status.textContent = messages[value.state] || messages.idle;
  if (value.availableVersion && !value.canInstall) status.textContent += ' Use the Windows installer once to enable in-app updates.';
  document.querySelector('#update-last-checked').textContent = value.lastChecked ? `Last checked: ${new Date(value.lastChecked).toLocaleString()}` : '';
  document.querySelector('#update-notes').hidden = !value.notes;
  document.querySelector('#update-notes-text').textContent = value.notes || '';
  if (value.state === 'installing') restarting = true;
  else if (restarting) { restarting = false; location.reload(); }
}

async function perform(path, body) {
  requestPending = true;
  check.disabled = true;
  install.disabled = true;
  status.textContent = body ? 'Downloading and verifying the update...' : 'Checking for updates...';
  try {
    const result = await request(path, 'POST', body);
    requestPending = false;
    render(result);
  } catch (error) {
    status.textContent = error.message;
    check.disabled = false;
    install.disabled = false;
  } finally { requestPending = false; }
}

check.addEventListener('click', () => perform('/updates/check'));
install.addEventListener('click', () => {
  const version = current?.availableVersion;
  if (!version || !confirm(`Install Microsoft Widgets ${version}? The helper will restart briefly. Your account and board settings will be kept.`)) return;
  perform('/updates/install', { version });
});

async function refresh() {
  if (requestPending) return;
  try { render(await request('/updates')); }
  catch { status.textContent = restarting ? 'Waiting for the helper to restart...' : 'Unable to contact the helper. Open Microsoft Widgets Setup from the Start menu or try again.'; }
}
request('/updates/result').then(value => { document.querySelector('#update-result').textContent = value.message || ''; }).catch(() => {});
refresh();
setInterval(refresh, 3000);
