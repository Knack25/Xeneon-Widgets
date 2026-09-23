(() => {
  const ownerHeader = 'X-Microsoft-Widgets-Owner';
  const ownerReplacementHeader = 'X-Microsoft-Widgets-Owner-Replacement';
  const bootstrapHeader = 'X-Microsoft-Widgets-Bootstrap';
  const sessionKey = 'microsoft-widgets-owner-session';
  const nativeFetch = window.fetch.bind(window);
  let ownerSession = sessionStorage.getItem(sessionKey);

  function sectionFromFragment(fragment) {
    const section = fragment.get('section');
    return ['overview', 'planner', 'outlook', 'settings', 'updates'].includes(section) ? section : null;
  }

  function removeBootstrapFragment(section) {
    history.replaceState(null, '', `${location.pathname}${location.search}${section ? `#${section}` : ''}`);
  }

  const ready = (async () => {
    const fragment = new URLSearchParams(location.hash.slice(1));
    const bootstrap = fragment.get('access');
    const section = sectionFromFragment(fragment);
    if (!bootstrap) return Boolean(ownerSession);
    try {
      const response = await nativeFetch('/api/local-access/session', {
        method: 'POST',
        headers: { [bootstrapHeader]: bootstrap },
        cache: 'no-store'
      });
      const result = await response.json().catch(() => ({}));
      if (!response.ok || !result.token) return false;
      ownerSession = result.token;
      sessionStorage.setItem(sessionKey, ownerSession);
      return true;
    } finally {
      removeBootstrapFragment(section);
    }
  })();

  async function ownerFetch(path, options = {}) {
    if (!await ready || !ownerSession) throw new Error('Open Microsoft Widgets Setup from the notification area or Start menu to continue.');
    const headers = new Headers(options.headers);
    headers.set(ownerHeader, ownerSession);
    const response = await nativeFetch(path, { ...options, headers });
    const replacement = response.headers.get(ownerReplacementHeader);
    if (replacement) {
      ownerSession = replacement;
      sessionStorage.setItem(sessionKey, ownerSession);
    }
    return response;
  }

  window.helperApi = { fetch: ownerFetch, ready };
  window.fetch = ownerFetch;
})();
