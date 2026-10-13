// ---- SETTINGS > QUICK CHAT: your own phrases for the in-game quick chat menu ----
// Backed by SiteQuickChatController (/api/site/quickchat). Four sets, five phrases each, matching the four tabs of the in-game panel.
// An empty box keeps the original phrase. Plain ASCII only, like every website write.
(function () {
  const setSel = document.getElementById('qcSet');
  if (!setSel) return;   // not on this page

  const slotsBox = document.getElementById('qcSlots');
  const msg = document.getElementById('qcMsg');
  const saveBtn = document.getElementById('qcSave');
  const resetBtn = document.getElementById('qcReset');
  let data = null;       // the last answer from the server

  function say(text, bad) {
    msg.textContent = text || '';
    msg.style.color = bad ? '#c0392b' : '';
  }

  function current() { return data && data.sets.find(s => s.key === setSel.value); }

  function render() {
    const s = current();
    slotsBox.innerHTML = '';
    if (!s) return;
    for (let i = 0; i < s.defaults.length; i++) {
      const wrap = document.createElement('div');
      wrap.className = 'sfield';
      const label = document.createElement('label');
      label.textContent = 'Phrase ' + (i + 1);
      const input = document.createElement('input');
      input.type = 'text';
      input.maxLength = data.maxLength;
      input.id = 'qcSlot' + i;
      input.placeholder = s.defaults[i];
      input.value = s.mine[i] || '';
      wrap.appendChild(label);
      wrap.appendChild(input);
      slotsBox.appendChild(wrap);
    }
    say('');
  }

  async function load() {
    try {
      const r = await fetch('/api/site/quickchat', { credentials: 'same-origin' });
      const d = await r.json();
      if (!d.ok) { say('Log in to change your quick chat.', true); saveBtn.disabled = true; resetBtn.disabled = true; return; }
      data = d;
      setSel.innerHTML = '';
      d.sets.forEach(s => {
        const o = document.createElement('option');
        o.value = s.key;
        o.textContent = s.label;
        setSel.appendChild(o);
      });
      render();
    } catch (e) { say('Could not load your quick chat.', true); }
  }

  async function post(url, body) {
    const r = await fetch(url, {
      method: 'POST', credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
    });
    return r.json();
  }

  saveBtn.addEventListener('click', async () => {
    const s = current();
    if (!s) return;
    const phrases = [];
    for (let i = 0; i < s.defaults.length; i++) phrases.push((document.getElementById('qcSlot' + i) || {}).value || '');
    const d = await post('/api/site/quickchat', { key: s.key, phrases });
    if (!d.ok) { say(d.error || 'Could not save.', true); return; }
    s.mine = d.mine;
    render();
    say('Saved. It shows the next time you start the game.');
  });

  resetBtn.addEventListener('click', async () => {
    const s = current();
    if (!s) return;
    const d = await post('/api/site/quickchat/reset', { key: s.key });
    if (!d.ok) { say(d.error || 'Could not reset.', true); return; }
    s.mine = d.mine;
    render();
    say('Back to the original phrases. It shows the next time you start the game.');
  });

  setSel.addEventListener('change', render);
  load();
})();
