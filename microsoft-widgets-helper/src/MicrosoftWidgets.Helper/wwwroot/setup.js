const account = document.querySelector('#account');
const board = document.querySelector('#board');
const message = document.querySelector('#message');
const signIn = document.querySelector('#sign-in');
const signOut = document.querySelector('#sign-out');
const enableTaskChat = document.querySelector('#enable-task-chat');
const enableNames = document.querySelector('#enable-names');
const enableMembers = document.querySelector('#enable-members');
const cancelSignIn = document.querySelector('#cancel-sign-in');
const save = document.querySelector('#save');
const hideCompleted = document.querySelector('#hide-completed');
const clientId = document.querySelector('#client-id');
const tenantId = document.querySelector('#tenant-id');
const saveConnection = document.querySelector('#save-connection');
let plans = [];
let signInRequest = null;
let capabilities = {};
let signedIn = false;
let permissionCheck = 0;
let permissionPending = false;
let permissionActionPending = false;

function renderPermissions() {
  for (const [key, button, available, request, retry] of [
    ['planner', signIn, 'Planner connected', 'Connect Planner', 'Retry Planner'],
    ['taskChat', enableTaskChat, 'Task chat enabled', 'Enable task chat', 'Retry task chat'],
    ['assigneeNames', enableNames, 'Assignee names enabled', 'Show assignee names', 'Retry assignee names'],
    ['boardMembers', enableMembers, 'Board members enabled', 'Enable board members', 'Retry board members']
  ]) {
    const feature = capabilities[key];
    button.hidden = key !== 'planner' && !signedIn;
    button.disabled = !clientId.value || feature?.state === 'available' || permissionActionPending || permissionPending;
    button.textContent = feature?.state === 'available' ? available
      : feature?.state === 'unavailable' ? retry : request;
    button.title = feature?.message || '';
  }
}
async function checkPermissions() {
  const ticket = ++permissionCheck;
  permissionPending = true;
  renderPermissions();
  try { const value = await api('/auth/capabilities'); if (ticket === permissionCheck) capabilities = value; }
  catch(error) {
    if (ticket === permissionCheck) capabilities = Object.fromEntries(['planner','taskChat','assigneeNames','boardMembers'].map(key => [key,{state:'unavailable',message:error.message}]));
  } finally { if (ticket === permissionCheck) { permissionPending = false; renderPermissions(); } }
}

async function loadInstallation() {
  const installation = await api('/installation');
  document.querySelector('#helper-version').textContent = `Microsoft Widgets Helper ${installation.version}`;
  document.querySelector('#download-widget').hidden = !installation.plannerWidgetAvailable;
  document.querySelector('#widget-package-status').textContent = installation.plannerWidgetAvailable
    ? 'Planner widget package is ready.' : 'Install the latest Microsoft Widgets release to get the widget package.';
}
async function downloadPackage(link, status) {
  const response = await window.helperApi.fetch(link.href, { cache: 'no-store' });
  if (!response.ok) {
    const result = await response.json().catch(() => ({}));
    throw new Error(result.message || 'Unable to download the Planner widget package.');
  }
  const disposition = response.headers.get('Content-Disposition') || '';
  const filename = disposition.match(/filename="([^"]+)"/i)?.[1] || disposition.match(/filename=([^;]+)/i)?.[1]?.trim();
  if (!filename) throw new Error('The Planner widget package filename is missing.');
  const objectUrl = URL.createObjectURL(await response.blob());
  const trigger = document.createElement('a');
  trigger.href = objectUrl;
  trigger.download = filename;
  trigger.hidden = true;
  document.body.append(trigger);
  try { trigger.click(); }
  finally { trigger.remove(); URL.revokeObjectURL(objectUrl); }
  status.textContent = 'Planner widget package downloaded.';
}
document.querySelector('#download-widget').addEventListener('click', async event => {
  event.preventDefault();
  const link = event.currentTarget;
  const status = document.querySelector('#widget-package-status');
  link.setAttribute('aria-busy', 'true');
  try { await downloadPackage(link, status); }
  catch (error) { status.textContent = error.message; }
  finally { link.removeAttribute('aria-busy'); }
});
document.querySelector('#stop-helper').addEventListener('click', async () => {
  if (!confirm('Stop the helper? Microsoft widgets will stop refreshing until you start it again.')) return;
  const response = await fetch('/host/stop', {method:'POST'}).catch(() => null);
  if (!response?.ok) { message.textContent = 'Unable to stop the helper. Try again.'; return; }
  document.querySelectorAll('button').forEach(button => { button.disabled = true; });
  message.textContent = 'Helper stopped. Open Microsoft Widgets Setup from the Start menu to restart it.';
});

async function api(path, options) {
  const response = await fetch(path, options);
  if (response.status === 204) return null;
  const body = await response.json();
  if (!response.ok) throw Object.assign(new Error(body.message || 'Request failed'), {status:response.status,code:body.code});
  return body;
}
async function refresh() {
  message.textContent = '';
  const configuration = await api('/configuration');
  clientId.value = configuration.clientId || '';
  if (!clientId.value) {
    document.querySelector('#app-configuration').open = true;
    if (!location.hash || location.hash === '#overview') location.hash = 'settings';
  }
  tenantId.value = configuration.tenant || '';
  signIn.disabled = !configuration.clientId;
  const status = await api('/auth/status');
  signedIn = status.isSignedIn;
  account.textContent = status.isSignedIn ? `Signed in as ${status.displayName}` : 'Not signed in';
  if (status.error) message.textContent = status.error.message;
  signIn.hidden = false;
  signOut.hidden = !status.isSignedIn;
  enableTaskChat.hidden = !status.isSignedIn;
  enableNames.hidden = !status.isSignedIn;
  enableMembers.hidden = !status.isSignedIn;
  board.disabled = !status.isSignedIn;
  save.disabled = !status.isSignedIn;
  if (!status.isSignedIn) { ++permissionCheck; permissionPending = false; capabilities = {}; renderPermissions(); board.replaceChildren(new Option('Sign in to load boards', '')); return; }
  await checkPermissions();
  if (capabilities.planner?.state !== 'available') {
    board.disabled = true; save.disabled = true;
    board.replaceChildren(new Option('Connect Planner to load boards', ''));
    return;
  }
  try {
    const [available, settings] = await Promise.all([api('/plans'), api('/settings')]);
    plans = available;
    board.replaceChildren(new Option('Select a board', ''));
    for (const plan of plans) board.add(new Option(`${plan.title}${plan.groupName ? ` (${plan.groupName})` : ''}`, plan.planId));
    board.value = settings.selectedPlanId || '';
    hideCompleted.checked = settings.hideCompletedTasks;
  } catch(error) {
    board.disabled = true; save.disabled = true;
    board.replaceChildren(new Option('Connect Planner to load boards', ''));
    signIn.hidden = false;
    capabilities.planner = {state:'unavailable',message:error.message};
    renderPermissions();
    message.textContent = `Planner: ${error.message}`;
  }
}
saveConnection.addEventListener('click', async () => {
  saveConnection.disabled = true;
  try {
    await api('/configuration', {method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({
      clientId:clientId.value.trim(),tenant:tenantId.value.trim() || 'organizations'
    })});
    await refresh();
    document.dispatchEvent(new CustomEvent('microsoft-configuration-changed'));
    message.textContent = 'Connection saved. Sign in with Microsoft to continue.';
  } catch(error) { message.textContent = error.message; }
  finally { saveConnection.disabled = false; }
});
signIn.addEventListener('click', async () => {
  if (signIn.disabled) return;
  if (capabilities.planner?.state === 'unavailable') { await refresh(); return; }
  signIn.disabled = true;
  cancelSignIn.hidden = false;
  signInRequest = new AbortController();
  message.textContent = 'Complete sign-in in the Microsoft window. If it shows AADSTS900971, open the sign-in help above.';
  try { await api('/auth/sign-in', {method:'POST',signal:signInRequest.signal}); await refresh(); }
  catch(error) { message.textContent = error.name === 'AbortError' ? 'Sign-in cancelled. You can try again.' : error.message; }
  finally { signInRequest = null; cancelSignIn.hidden = true; renderPermissions(); }
});
cancelSignIn.addEventListener('click', () => signInRequest?.abort());
enableTaskChat.addEventListener('click', async () => {
  if (enableTaskChat.disabled) return;
  if (capabilities.taskChat?.state === 'unavailable') { await refresh(); return; }
  permissionActionPending = true;
  renderPermissions();
  enableTaskChat.disabled = true;
  message.textContent = 'Complete the Microsoft permission prompt to enable task chat.';
  try { await api('/auth/enable-task-chat', {method:'POST'}); await refresh(); message.textContent = 'Task chat is enabled.'; }
  catch(error) { message.textContent = error.message; }
  finally { permissionActionPending = false; renderPermissions(); }
});
enableNames.addEventListener('click', async () => {
  if (enableNames.disabled) return;
  if (capabilities.assigneeNames?.state === 'unavailable') { await refresh(); return; }
  permissionActionPending = true;
  renderPermissions();
  enableNames.disabled = true;
  message.textContent = 'Complete the Microsoft permission prompt to show assignee names.';
  try { await api('/auth/enable-assignee-names', {method:'POST'}); await refresh(); message.textContent = 'Assignee names are enabled.'; }
  catch(error) { message.textContent = error.message; }
  finally { permissionActionPending = false; renderPermissions(); }
});
enableMembers.addEventListener('click', async () => {
  if (enableMembers.disabled) return;
  if (capabilities.boardMembers?.state === 'unavailable') { await refresh(); return; }
  permissionActionPending = true;
  renderPermissions();
  enableMembers.disabled = true;
  message.textContent = 'Complete the Microsoft permission prompt to edit task assignees.';
  try { await api('/auth/enable-board-members', {method:'POST'}); await refresh(); message.textContent = 'Board members are enabled.'; }
  catch(error) { message.textContent = error.message; }
  finally { permissionActionPending = false; renderPermissions(); }
});
signOut.addEventListener('click', async () => {
  try { await api('/auth/sign-out', {method:'POST'}); location.reload(); }
  catch(error) { message.textContent = error.message; }
});
save.addEventListener('click', async () => {
  const selected = plans.find(plan => plan.planId === board.value);
  if (!selected) { message.textContent = 'Select a board first.'; return; }
  try {
    await api('/settings', {method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({
      selectedPlanId:selected.planId,selectedPlanTitle:selected.title,hideCompletedTasks:hideCompleted.checked
    })});
    message.textContent = 'Board saved.';
  } catch(error) { message.textContent = error.message; }
});
refresh().catch(error => { account.textContent = 'Unable to load setup'; message.textContent = error.message; });
document.addEventListener('microsoft-account-changed', () => refresh().catch(error => { message.textContent = error.message; }));
setInterval(async () => {
  if (document.hidden || !signedIn || signInRequest || permissionPending || permissionActionPending) return;
  const previous = capabilities.planner?.state;
  await checkPermissions();
  if (capabilities.planner?.state === 'signed_out' || previous !== 'available' && capabilities.planner?.state === 'available') await refresh().catch(error => { message.textContent = error.message; });
}, 30000);
loadInstallation().catch(() => { document.querySelector('#widget-package-status').textContent = 'Unable to check the widget package.'; });
