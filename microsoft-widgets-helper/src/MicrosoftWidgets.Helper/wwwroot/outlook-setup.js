(() => {
  const $ = selector => document.querySelector(selector);
  const connect = $('#outlook-connect');
  const status = $('#outlook-status');
  const message = $('#outlook-message');
  let pending = false;
  let controller;
  let refreshPending = false;
  let connectMode = 'retry';
  let catalogLoaded = false;

  async function request(path, method = 'GET', body, signal, base = '/api/outlook') {
    const response = await window.helperApi.fetch(`${base}${path}`, {
      method, cache: 'no-store', signal,
      headers: { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body)
    });
    if (response.status === 204) return null;
    const result = await response.json().catch(() => ({}));
    if (!response.ok) {
      throw new Error(result.message || result.error?.message || 'Outlook could not complete the request.');
    }
    return result;
  }

  function row(text, action, label, feedback = message) {
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
        try { await action(); } catch (error) { feedback.textContent = error.message; }
        finally { button.disabled = false; }
      });
      item.append(button);
    }
    return item;
  }

  const pairingRequest = (path = '', method = 'GET', body) => request(path, method, body, undefined, '/api/local-access/pairings');
  const scopeLabel = pair => pair.scope === 'planner' ? 'Planner' : pair.scope === 'outlook' ? 'Outlook' : 'Unknown scope';

  async function loadPairings() {
    const [requests, paired] = await Promise.all([pairingRequest(), pairingRequest('/paired')]);
    for (const scope of ['planner', 'outlook']) {
      const feedback = scope === 'planner' ? $('#message') : message;
      const pending = requests.filter(pair => pair.scope === scope);
      $(`#${scope}-pairings`).replaceChildren(...pending.map(pair => row(
        `${scopeLabel(pair)} - ${pair.code} - ${pair.instanceId}`, async () => {
          if (!confirm(`Approve ${scopeLabel(pair)} widget ${pair.instanceId}? Confirm code ${pair.code} matches the code on your display.`)) return;
          await pairingRequest(`/${encodeURIComponent(pair.id)}/approve`, 'POST', {});
          await loadPairings();
        }, 'Approve', feedback)));
      $(`#${scope}-pairing-empty`).hidden = pending.length > 0;
      $(`#${scope}-paired`).replaceChildren(...paired.filter(pair => pair.scope === scope).map(pair => row(`${scopeLabel(pair)} - ${pair.instanceId}`, async () => {
        if (!confirm(`Disconnect ${scopeLabel(pair)} widget ${pair.instanceId}?`)) return;
        await pairingRequest('/revoke', 'POST', { credentialId: pair.credentialId });
        await loadPairings();
      }, 'Disconnect', feedback)));
    }
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
      await loadPairings();
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
  $('#outlook-download').addEventListener('click', async event => {
    event.preventDefault();
    const link = event.currentTarget;
    const packageStatus = $('#outlook-package-status');
    link.setAttribute('aria-busy', 'true');
    try {
      const response = await window.helperApi.fetch(link.href, { cache: 'no-store' });
      if (!response.ok) {
        const result = await response.json().catch(() => ({}));
        throw new Error(result.message || 'Unable to download the Outlook widget package.');
      }
      const disposition = response.headers.get('Content-Disposition') || '';
      const filename = disposition.match(/filename="([^"]+)"/i)?.[1] || disposition.match(/filename=([^;]+)/i)?.[1]?.trim();
      if (!filename) throw new Error('The Outlook widget package filename is missing.');
      const objectUrl = URL.createObjectURL(await response.blob());
      const trigger = document.createElement('a');
      trigger.href = objectUrl;
      trigger.download = filename;
      trigger.hidden = true;
      document.body.append(trigger);
      try { trigger.click(); }
      finally { trigger.remove(); URL.revokeObjectURL(objectUrl); }
      packageStatus.textContent = 'Outlook widget package downloaded.';
    } catch (error) { packageStatus.textContent = error.message; }
    finally { link.removeAttribute('aria-busy'); }
  });
  document.addEventListener('microsoft-configuration-changed', () => { catalogLoaded = false; refresh(true); });
  document.addEventListener('microsoft-account-changed', () => { catalogLoaded = false; refresh(true); });
  window.helperApi.fetch('/installation', { cache: 'no-store' }).then(r => r.json()).then(value => {
    $('#outlook-download').hidden = !value.outlookWidgetAvailable;
    $('#outlook-package-status').textContent = value.outlookWidgetAvailable ? 'Outlook widget package is ready.' : 'The Outlook widget package is not included in this build.';
  }).catch(() => { $('#outlook-package-status').textContent = 'Unable to check the Outlook package.'; });
  refresh(true);
  setInterval(() => refresh(false), 10000);
})();
