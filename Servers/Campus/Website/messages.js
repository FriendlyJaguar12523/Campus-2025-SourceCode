// ───── MESSAGES PAGE (/messages) ─────
// DMs and group chats, backed by SiteChatController -> the same ChatDB the game client uses. A message sent here shows up
// in-game (ChatDB.Push notifies the game's hub) and a message sent in-game shows up here on the next poll.
(function () {
  const threadsEl = document.getElementById('msgThreads');
  if (!threadsEl) return;

  const convEl = document.getElementById('msgConv');
  const placeholderEl = document.getElementById('msgPlaceholder');
  const listEl = document.getElementById('msgList');

  let openId = null;
  let lastMessageId = 0;
  let pollTimer = null;
  let listTimer = null;
  let picked = [];   // [{id, username, display_name, profile_image}]
  let meId = null;   // fetched fresh, not read from app.js's module-scoped CAMPUS_ME
  let meUser = null; // same shape as a thread member (id/username/display_name/profile_image), for the avatar on my own bubbles

  function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }
  // profile_image comes back as either a ready /img/ URL or a bare filename (same shape /api/site/profile/{u} uses - see app.js's myPfpUrl).
  function avatar(u) {
    const raw = u && u.profile_image;
    if (raw) return /^https?:|^\//.test(raw) ? raw : (((window.CAMPUS_DATA && window.CAMPUS_DATA.imgBase) || '/img/') + raw);
    return `https://api.dicebear.com/9.x/thumbs/svg?seed=${encodeURIComponent((u && u.username) || 'player')}`;
  }
  function name(u) { return (u && (u.display_name || u.username)) || 'Player'; }

  function timeAgo(iso) {
    if (!iso) return '';
    const ms = Date.now() - new Date(iso).getTime();
    const m = Math.floor(ms / 60000);
    if (m < 1) return 'now';
    if (m < 60) return m + 'm';
    const h = Math.floor(m / 60);
    if (h < 24) return h + 'h';
    const d = Math.floor(h / 24);
    if (d < 7) return d + 'd';
    return new Date(iso).toLocaleDateString();
  }

  async function api(path, opts) {
    const r = await fetch('/api/site/chat/' + path, Object.assign({ credentials: 'same-origin' }, opts));
    if (r.status === 401) { location.href = '/login'; throw new Error('signed out'); }
    const d = await r.json().catch(() => ({ ok: false }));
    return d;
  }

  function threadTitle(t) {
    if (t.isGroup) return t.name || t.members.map(name).join(', ');
    return t.members.length ? name(t.members[0]) : 'Player';
  }

  // ---------------- thread list ----------------

  async function loadThreads(keepScroll) {
    const d = await api('thread');
    if (!d.ok) { threadsEl.innerHTML = '<div class="msg-empty">Could not load your messages.</div>'; return; }
    if (!d.threads.length) { threadsEl.innerHTML = '<div class="msg-empty">No conversations yet. Click "New message" to start one.</div>'; return; }
    threadsEl.innerHTML = d.threads.map(t => {
      const m = t.members[0];
      const second = t.isGroup && t.members[1] ? `<img class="msg-av-2" src="${avatar(t.members[1])}" alt="">` : '';
      return `<button class="msg-thread${t.unread ? ' unread' : ''}${t.id === openId ? ' active' : ''}" data-id="${t.id}">
        <span class="msg-thread-avs"><img src="${avatar(m)}" alt="">${second}</span>
        <span class="msg-thread-meta">
          <span class="msg-thread-name">${esc(threadTitle(t))}</span>
          <span class="msg-thread-prev">${t.latestText ? esc(t.latestText) : 'No messages yet'}${t.latestAt ? ' · ' + timeAgo(t.latestAt) : ''}</span>
        </span>
      </button>`;
    }).join('');
    threadsEl.querySelectorAll('.msg-thread').forEach(b => b.addEventListener('click', () => openThread(Number(b.dataset.id))));
  }

  // ---------------- conversation ----------------

  // System notices carry raw "Player <@U123>" tags (the format the game client itself resolves, e.g. "Player <@U6> left") -
  // replace them with a name using whoever is in this thread, so the website doesn't leak the internal tag to a reader.
  function resolveTags(text, thread) {
    const who = idStr => { const id = Number(idStr); if (id === meId) return 'You'; const u = thread.members.find(m => m.id === id); return u ? name(u) : 'A player'; };
    return text.replace(/Player <@U(\d+)>/g, (whole, idStr) => who(idStr))   // "Player <@U6> left" -> "You left" / "Alice left"
                .replace(/<@U(\d+)>/g, (whole, idStr) => who(idStr));        // any tag not preceded by "Player " on its own
  }

  function renderMessages(thread, messages) {
    const body = document.getElementById('msgBody');
    if (!body) return;
    const atBottom = body.scrollHeight - body.scrollTop - body.clientHeight < 60;
    body.innerHTML = messages.map(m => {
      if (m.system) return `<div class="msg-bubble-row system"><div class="msg-bubble">${esc(resolveTags(m.text, thread))}</div></div>`;
      const mine = meId != null && m.senderId === meId;
      const who = mine ? meUser : m.sender;
      return `<div class="msg-bubble-row${mine ? ' mine' : ''}">
        <img class="msg-bubble-av" src="${avatar(who)}" alt="">
        <div class="msg-bubble-col">
          ${thread.isGroup && !mine ? `<span class="msg-bubble-sender">${esc(name(m.sender))}</span>` : ''}
          <div class="msg-bubble">${esc(m.text)}</div>
          <span class="msg-bubble-time">${timeAgo(m.at)}</span>
        </div>
      </div>`;
    }).join('');
    if (atBottom || messages.length <= 20) body.scrollTop = body.scrollHeight;
  }

  async function openThread(id) {
    openId = id;
    listEl.classList.add('has-open');
    convEl.classList.add('open');
    const d = await api('thread/' + id);
    if (!d.ok) { convEl.innerHTML = `<div class="msg-conv-placeholder">Could not open that conversation.</div>`; return; }
    lastMessageId = d.messages.length ? d.messages[d.messages.length - 1].id : 0;
    convEl.innerHTML = `
      <div class="msg-conv-head"><button class="msg-conv-back" id="msgBack" aria-label="Back">&larr;</button>${esc(threadTitle(d.thread))}</div>
      <div class="msg-conv-body" id="msgBody"></div>
      <div class="msg-conv-foot">
        <textarea id="msgInput" placeholder="Message..." rows="1"></textarea>
        <button id="msgSend">Send</button>
      </div>`;
    renderMessages(d.thread, d.messages);
    document.getElementById('msgBack').addEventListener('click', closeConv);
    const input = document.getElementById('msgInput');
    const send = async () => {
      const text = input.value.trim();
      if (!text) return;
      input.value = ''; input.style.height = 'auto';
      const r = await api('thread/' + id + '/message', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ text }) });
      // Don't bump lastMessageId here - pollOnce() below reads it to ask "what's newer than this?", so setting it to the
      // message we just sent would make that message look already-seen and it would never get rendered.
      if (r.ok) { pollOnce(d.thread); loadThreads(); }
    };
    input.addEventListener('keydown', e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(); } });
    input.addEventListener('input', () => { input.style.height = 'auto'; input.style.height = Math.min(120, input.scrollHeight) + 'px'; });
    document.getElementById('msgSend').addEventListener('click', send);

    loadThreads();
    restartPoll(d.thread);
  }

  function closeConv() {
    openId = null;
    listEl.classList.remove('has-open');
    convEl.classList.remove('open');
    convEl.innerHTML = `<div class="msg-conv-placeholder" id="msgPlaceholder">Pick a conversation, or start a new one.</div>`;
    clearTimeout(pollTimer);
  }

  async function pollOnce(thread) {
    if (!openId) return;
    const d = await api('thread/' + openId + '/since/' + lastMessageId);
    if (d.ok && d.messages.length) {
      lastMessageId = d.messages[d.messages.length - 1].id;
      const r = await api('thread/' + openId);
      if (r.ok) renderMessages(r.thread, r.messages);
      loadThreads();
    }
  }

  function restartPoll(thread) {
    clearTimeout(pollTimer);
    const tick = () => { pollOnce(thread).finally(() => { if (openId) pollTimer = setTimeout(tick, 4000); }); };
    pollTimer = setTimeout(tick, 4000);
  }

  // ---------------- new message ----------------

  const panel = document.getElementById('msgNewPanel');
  const toInput = document.getElementById('msgNewTo');
  const suggestEl = document.getElementById('msgSuggest');
  const chipsEl = document.getElementById('msgChips');
  const nameInput = document.getElementById('msgNewName');
  const nameLabel = document.getElementById('msgNewNameLabel');
  const errEl = document.getElementById('msgNewErr');

  function renderChips() {
    chipsEl.innerHTML = picked.map(u => `<span class="msg-chip"><img src="${avatar(u)}" alt="" style="width:18px;height:18px;border-radius:50%;">${esc(name(u))}<button type="button" data-id="${u.id}">&times;</button></span>`).join('');
    chipsEl.querySelectorAll('button').forEach(b => b.addEventListener('click', () => { picked = picked.filter(u => u.id != b.dataset.id); renderChips(); toggleGroupName(); }));
  }
  function toggleGroupName() {
    const show = picked.length > 1;
    nameInput.hidden = !show; nameLabel.hidden = !show;
  }
  function pick(u) {
    if (picked.some(p => p.id === u.id)) return;
    picked.push(u); renderChips(); toggleGroupName();
    toInput.value = ''; suggestEl.classList.remove('open'); suggestEl.innerHTML = '';
  }

  let suggestTimer = null;
  toInput.addEventListener('input', () => {
    clearTimeout(suggestTimer);
    const q = toInput.value.trim();
    if (!q) { suggestEl.classList.remove('open'); return; }
    suggestTimer = setTimeout(async () => {
      const d = await api('lookup?username=' + encodeURIComponent(q));
      const rows = [];
      if (d.ok && d.user) rows.push(d.user);
      suggestEl.innerHTML = rows.length ? rows.map(u => `<div class="msg-suggest-row" data-id="${u.id}"><img src="${avatar(u)}" alt="">${esc(name(u))} <span style="opacity:.6;">@${esc(u.username)}</span></div>`).join('')
        : `<div class="msg-suggest-row" style="opacity:.6; cursor:default;">No player found</div>`;
      suggestEl.classList.add('open');
      suggestEl.querySelectorAll('.msg-suggest-row[data-id]').forEach(row => row.addEventListener('click', () => {
        const u = rows.find(r => String(r.id) === row.dataset.id);
        if (u) pick(u);
      }));
    }, 250);
  });

  async function loadFriendsIntoSuggest() {
    const d = await api('friends');
    if (!d.ok || !d.friends.length) return;
    suggestEl.innerHTML = d.friends.map(u => `<div class="msg-suggest-row" data-id="${u.id}"><img src="${avatar(u)}" alt="">${esc(name(u))} <span style="opacity:.6;">@${esc(u.username)}</span></div>`).join('');
    suggestEl.classList.add('open');
    suggestEl.querySelectorAll('.msg-suggest-row[data-id]').forEach(row => row.addEventListener('click', () => {
      const u = d.friends.find(r => String(r.id) === row.dataset.id);
      if (u) pick(u);
    }));
  }

  document.getElementById('msgNewBtn').addEventListener('click', () => {
    picked = []; renderChips(); toggleGroupName();
    toInput.value = ''; nameInput.value = ''; document.getElementById('msgNewText').value = ''; errEl.textContent = '';
    panel.classList.add('open');
    loadFriendsIntoSuggest();
  });
  document.getElementById('msgNewCancel').addEventListener('click', () => panel.classList.remove('open'));
  panel.addEventListener('click', e => { if (e.target === panel) panel.classList.remove('open'); });

  document.getElementById('msgNewSend').addEventListener('click', async () => {
    if (!picked.length) { errEl.textContent = 'Add at least one person.'; return; }
    const btn = document.getElementById('msgNewSend');
    btn.disabled = true; errEl.textContent = '';
    const body = { Ids: picked.map(u => u.id), Name: nameInput.value.trim() || null, Message: document.getElementById('msgNewText').value.trim() || null };
    const d = await api('thread', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    btn.disabled = false;
    if (!d.ok) { errEl.textContent = d.error || 'Could not start that conversation.'; return; }
    panel.classList.remove('open');
    await loadThreads();
    openThread(d.thread.id);
  });

  // ---------------- boot ----------------

  fetch('/api/site/me', { credentials: 'same-origin' }).then(r => r.ok ? r.json() : null).then(d => {
    if (!d || !d.ok) return;
    meId = d.user.id; meUser = d.user;
    const open = document.getElementById('msgBody'); if (open && openId) openThread(openId);   // repaint "mine" bubbles once we know who "me" is
  }).catch(() => {});
  loadThreads();
  listTimer = setInterval(() => { if (!document.hidden) loadThreads(); }, 15000);
  window.addEventListener('beforeunload', () => { clearInterval(listTimer); clearTimeout(pollTimer); });
})();
