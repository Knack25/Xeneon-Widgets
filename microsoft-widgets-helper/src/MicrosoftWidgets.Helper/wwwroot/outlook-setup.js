(() => {
  const $ = selector => document.querySelector(selector);
  const connect = $('#outlook-connect');
  const status = $('#outlook-status');
  const message = $('#outlook-message');
  let session;
  let pending = false;
  let controller;
  let refreshPending = false;
  let connectMode = 'retry';
  let catalogLoaded = false;

  async function request(path, method = 'GET', body, signal) {
    if (!session) {
      const response = await fetch('/api/outlook/session', { cache: 'no-store' });
      const result = await response.json();
      if (!response.ok) throw new Error(result.message || 'Unable to open Outlook setup.');
      session = result.token;
    }
    const response = await fetch(`/api/outlook${path}`, {
      method, cache: 'no-store', signal,
      headers: { 'X-Outlook-Session': session, 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body)
    });
    if (response.status === 204) return null;
    const result = await response.json().catch(() => ({}));
    if (!response.ok) {
      if (response.status === 401) session = null;
      throw new Error(result.message || result.error?.message || 'Outlook could not complete the request.');
    }
    return result;
  }

  function row(text, action, label) {
    const item = document.createElement('li');
    const name = document.createElement('span');
    name.textContent = text;
    item.append(name);
    if (action) {
      const button = document.createElement('button');
      button.className = 'secondary';
      button.textContent = label;
      button.addEventListener('click', async () => {
        button.disabled = true;
        try { await action(); } catch (error) { message.textContent = error.message; }
        finally { button.disabled = false; }
      });
      item.append(button);
    }
    return item;
  }

  async function loadPairings() {
    const [requests, paired] = await Promise.all([request('/pairings'), request('/paired')]);
    $('#outlook-pairings').replaceChildren(...requests.map(pair => row(
      `${pair.code} - ${pair.instanceId}`, async () => {
        if (!confirm(`Approve Outlook widget ${pair.instanceId}? Confirm code ${pair.code} matches the code on your display.`)) return;
        await request(`/pairings/${encodeURIComponent(pair.id)}/approve`, 'POST', {});
        await loadPairings();
      }, 'Approve')));
    $('#outlook-pairing-empty').hidden = requests.length > 0;
    $('#outlook-paired').replaceChildren(...paired.map(pair => row(pair.instanceId, async () => {
      if (!confirm(`Disconnect Outlook widget ${pair.instanceId}?`)) return;
      await request('/pairings/revoke', 'POST', { credentialId: pair.credentialId });
      await loadPairings();
    }, 'Disconnect')));
  }

  async function loadCalendars() {
    const calendars = await request('/calendars');
    $('#outlook-calendars').replaceChildren(...calendars.map(calendar => row(
      `${calendar.name}${calendar.owner ? ` - ${calendar.owner}` : ''} (${calendar.kind})`,
      calendar.isLocalReference ? async () => {
        if (!confirm(`Remove ${calendar.name} from this helper? This does not delete the calendar in Outlook.`)) return;
        await request(`/sources/${encodeURIComponent(calendar.key)}/remove`, 'POST', {});
        await loadCalendars();
      } : null, 'Remove')));
    $('#outlook-calendar-empty').hidden = calendars.length > 0;
  }

  async function refresh(loadCatalog = false) {
    if (pending || refreshPending) return;
    refreshPending = true;
    try {
      const value = await request('/status');
      connectMode = value.ready ? 'available' : !value.signedIn || ['consent_required', 'sign_in_required'].includes(value.error?.code) ? 'connect' : 'retry';
      connect.disabled = !value.configured || value.ready;
      connect.textContent = value.ready ? 'Outlook connected' : connectMode === 'retry' ? 'Retry Outlook' : 'Connect Outlook';
      status.textContent = value.ready ? 'Outlook is connected.'
        : value.error?.message || (!value.configured ? 'Save your Microsoft connection above first.'
          : value.signedIn ? 'Outlook permission approval is required.' : 'Connect Outlook to sign in.');
      const warnings = [...new Set((value.discoveryErrors || []).map(error => error.message).filter(Boolean))];
      if (warnings.length) status.textContent += ` Some Outlook data is unavailable: ${warnings.join(' ')}`;
      $('#outlook-add-source').disabled = !value.ready;
      if (value.signedIn) await loadPairings();
      else {
        $('#outlook-pairings').replaceChildren();
        $('#outlook-paired').replaceChildren();
      }
      if (value.ready && (loadCatalog || !catalogLoaded)) { await loadCalendars(); catalogLoaded = true; }
      if (!value.ready) { catalogLoaded = false; $('#outlook-calendars').replaceChildren(); }
    } catch (error) { status.textContent = error.message; connectMode = 'retry'; connect.textContent = 'Retry Outlook'; connect.disabled = false; }
    finally { refreshPending = false; }
  }

  connect.addEventListener('click', async () => {
    if (pending || connect.disabled || connectMode === 'available') return;
    if (connectMode === 'retry') { await refresh(true); return; }
    pending = true;
    connect.disabled = true;
    controller = new AbortController();
    $('#outlook-cancel').hidden = false;
    message.textContent = 'Complete the Outlook permission request in the Microsoft window.';
    try {
      await request('/connect', 'POST', {}, controller.signal);
      session = null;
      message.textContent = 'Outlook connected.';
      document.dispatchEvent(new CustomEvent('microsoft-account-changed'));
    } catch (error) { message.textContent = error.name === 'AbortError' ? 'Outlook sign-in cancelled.' : error.message; }
    finally {
      pending = false; controller = null; connect.disabled = false; $('#outlook-cancel').hidden = true;
      await refresh(true);
    }
  });
  $('#outlook-cancel').addEventListener('click', () => controller?.abort());
  $('#outlook-refresh').addEventListener('click', () => refresh(true));
  $('#outlook-add-source').addEventListener('click', async () => {
    const input = $('#outlook-owner');
    const ownerEmail = input.value.trim();
    if (!ownerEmail) { message.textContent = 'Enter the calendar owner email address.'; return; }
    const button = $('#outlook-add-source');
    button.disabled = true;
    try {
      await request('/sources', 'POST', { ownerEmail });
      input.value = '';
      message.textContent = 'Shared calendar added.';
      await loadCalendars();
    } catch (error) { message.textContent = error.message; }
    finally { button.disabled = false; }
  });
  document.addEventListener('microsoft-configuration-changed', () => { session = null; catalogLoaded = false; refresh(true); });
  document.addEventListener('microsoft-account-changed', () => { session = null; catalogLoaded = false; refresh(true); });
  fetch('/installation', { cache: 'no-store' }).then(r => r.json()).then(value => {
    $('#outlook-download').hidden = !value.outlookWidgetAvailable;
    $('#outlook-package-status').textContent = value.outlookWidgetAvailable ? 'Outlook widget package is ready.' : 'The Outlook widget package is not included in this build.';
  }).catch(() => { $('#outlook-package-status').textContent = 'Unable to check the Outlook package.'; });
  refresh(true);
  setInterval(() => refresh(false), 10000);
})();
