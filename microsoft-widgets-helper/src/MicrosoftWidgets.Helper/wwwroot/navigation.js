(() => {
  const shell = document.querySelector('#setup-shell');
  const instructions = document.querySelector('#tray-instructions');
  const settings = document.querySelector('#view-settings');
  settings.append(document.querySelector('#updates'), document.querySelector('footer'));
  const links = [...document.querySelectorAll('.sidebar a')];
  function navigate() {
    const name = location.hash === '#updates' ? 'settings' : location.hash.slice(1);
    const active = ['overview', 'planner', 'outlook', 'settings'].includes(name) ? name : 'overview';
    for (const view of document.querySelectorAll('.view')) view.hidden = view.id !== `view-${active}`;
    for (const link of links) {
      if (link.hash === `#${active}`) link.setAttribute('aria-current', 'page');
      else link.removeAttribute('aria-current');
    }
    const message = document.querySelector('#message');
    document.querySelector(active === 'settings' ? '#connection-settings' : '#view-planner').append(message);
  }
  function mirror(source, target) {
    const from = document.querySelector(source);
    const to = document.querySelector(target);
    const update = () => { to.textContent = from.textContent; };
    new MutationObserver(update).observe(from, {childList:true, subtree:true, characterData:true});
    update();
  }
  mirror('#account', '#overview-account');
  mirror('#sign-in', '#overview-planner');
  mirror('#outlook-status', '#overview-outlook');
  mirror('#update-status', '#overview-update');
  const pairings = ['planner', 'outlook'].map(scope => ({
    list: document.querySelector(`#${scope}-pairings`),
    summary: document.querySelector(`#overview-${scope}-approvals`)
  }));
  const approvals = () => {
    for (const { list, summary } of pairings) {
      const count = list.children.length;
      summary.textContent = count ? `${count} widget connection${count === 1 ? '' : 's'} awaiting approval` : '';
    }
  };
  for (const { list } of pairings) new MutationObserver(approvals).observe(list, {childList:true});
  document.querySelector('#client-id').addEventListener('input', () => {
    document.querySelector('#app-configuration').open = true;
  });
  window.addEventListener('hashchange', navigate);
  window.helperApi.ready.then(allowed => {
    instructions.hidden = allowed;
    shell.hidden = !allowed;
    if (allowed) navigate();
  });
})();
