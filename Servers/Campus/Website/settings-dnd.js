// ---- SETTINGS > DO NOT DISTURB ----
// Backed by SiteDndController (/api/site/dnd). While it is on, invites from people you have not favourited are refused and
// "friend is online" notices stop. It ends by itself. Plain ASCII only.
(function () {
  const box = document.getElementById('dndStatus');
  if (!box) return;   // not on this page
  const msg = document.getElementById('dndMsg');
  const buttons = Array.from(document.querySelectorAll('[data-dnd]'));
  let timer = null;

  function describe(s) {
    if (!s.active) return 'Off. You get invites and friend notices as normal.';
    const m = s.minutesLeft;
    const left = m >= 60 ? Math.floor(m / 60) + ' h ' + (m % 60) + ' min' : m + ' min';
    return 'On - ' + left + ' left. Only your favourites can invite you.';
  }

  function show(s) {
    box.textContent = describe(s);
    box.style.fontWeight = s.active ? '600' : '';
    clearTimeout(timer);
    if (s.active) timer = setTimeout(load, 30000);   // keep the countdown honest
  }

  async function call(method, body) {
    const r = await fetch('/api/site/dnd', {
      method, credentials: 'same-origin',
      headers: body ? { 'Content-Type': 'application/json' } : undefined,
      body: body ? JSON.stringify(body) : undefined
    });
    return r.json();
  }

  async function load() {
    try {
      const s = await call('GET');
      if (!s.ok) { box.textContent = 'Log in to use Do Not Disturb.'; buttons.forEach(b => b.disabled = true); return; }
      show(s);
    } catch (e) { box.textContent = 'Could not load Do Not Disturb.'; }
  }

  buttons.forEach(b => b.addEventListener('click', async () => {
    msg.textContent = '';
    try {
      const s = await call('POST', { minutes: parseInt(b.dataset.dnd, 10) });
      if (!s.ok) { msg.textContent = s.error || 'Could not change it.'; return; }
      show(s);
    } catch (e) { msg.textContent = 'Could not change it.'; }
  }));

  load();
})();
