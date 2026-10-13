// ───── WEEKLY CHALLENGES PAGE (/challenges) ─────
// Data from GET /api/site/weekly (WeeklyChallengeController). Signed in, every challenge carries
// the player's own progress; signed out, the page shows the challenges and asks them to log in.
(function () {
  const list = document.getElementById('wcList');
  if (!list) return;

  function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  // Store rarity -> the name the game shows on its boxes.
  function rarityName(r) {
    if (r >= 50) return ['Legendary', 'legendary'];
    if (r >= 30) return ['Epic', 'epic'];
    if (r >= 20) return ['Rare', 'rare'];
    if (r >= 10) return ['Uncommon', 'uncommon'];
    return ['Common', 'common'];
  }

  function countdown(iso) {
    const ms = new Date(iso) - Date.now();
    if (!(ms > 0)) return 'any moment';
    const d = Math.floor(ms / 86400000), h = Math.floor(ms % 86400000 / 3600000), m = Math.floor(ms % 3600000 / 60000);
    return d > 0 ? d + 'd ' + h + 'h' : h > 0 ? h + 'h ' + m + 'm' : m + 'm';
  }

  async function load() {
    let d = null;
    try {
      const r = await fetch('/api/site/weekly', { credentials: 'same-origin' });
      if (r.ok) d = await r.json();
    } catch { }
    if (!d || !d.ok) {
      list.innerHTML = '<div class="wc-card wc-loading">Couldn\'t load this week\'s challenges. Try again in a moment.</div>';
      return;
    }

    document.getElementById('wcNeed').textContent = d.required;

    // ---- challenges ----
    list.innerHTML = d.challenges.map(c => {
      const n = Math.min(c.progress, c.target);
      const done = d.signedIn && n >= c.target;
      const pct = d.signedIn ? Math.round(100 * n / c.target) : 0;
      const room = c.roomId ? '<a class="wc-room" href="/room/' + encodeURIComponent(c.roomId) + '">^' + esc(c.roomName || 'Room') + '</a>' : '<span class="wc-room wc-any">Any room</span>';
      return '<article class="wc-card' + (done ? ' wc-done' : '') + '">'
        + '<div class="wc-row"><div class="wc-text">' + esc(c.text) + '</div>'
        +   (d.signedIn ? '<span class="wc-count">' + (done ? 'Done' : n + ' / ' + c.target) + '</span>' : '') + '</div>'
        + '<div class="wc-meta">' + room + '</div>'
        + (d.signedIn ? '<div class="wc-bar" role="progressbar" aria-valuemin="0" aria-valuemax="' + c.target + '" aria-valuenow="' + n + '" aria-label="' + esc(c.text) + '"><span style="width:' + pct + '%"></span></div>' : '')
        + '</article>';
    }).join('');

    // ---- the progress ring in the header ----
    const need = d.required, have = d.signedIn ? Math.min(d.completed, need) : 0;
    const fill = document.getElementById('wcMeterFill');
    const C = 2 * Math.PI * 52;
    fill.style.strokeDasharray = C;
    fill.style.strokeDashoffset = C * (1 - have / need);
    document.getElementById('wcMeterNum').textContent = d.signedIn ? have : '?';
    document.getElementById('wcMeterOf').textContent = 'of ' + need;
    document.getElementById('wcMeter').setAttribute('aria-label', d.signedIn ? have + ' of ' + need + ' challenges done' : 'Log in to see your progress');
    document.getElementById('wcMeter').classList.toggle('wc-meter-full', d.signedIn && (d.rewardSent || have >= need));

    // ---- reward ----
    const img = document.getElementById('wcRewardImg');
    const r = d.reward;
    if (r) {
      if (r.image) { img.onerror = () => { img.hidden = true; }; img.src = r.image + '?width=360'; img.alt = r.name; img.hidden = false; }
      document.getElementById('wcRewardName').textContent = r.name;
      document.getElementById('wcRewardKind').textContent = r.kind === 'skin' ? 'Equipment skin' : r.kind === 'clothing' ? 'Clothing' : '';
      const [rn, rc] = rarityName(r.rarity || 0);
      const badge = document.getElementById('wcRarity');
      badge.textContent = rn; badge.hidden = false;
      document.getElementById('wcReward').classList.add('wc-r-' + rc);
    } else {
      document.getElementById('wcRewardName').textContent = 'A surprise';
    }

    const st = document.getElementById('wcStatus');
    if (!d.signedIn) {
      st.innerHTML = '<a href="/login" class="wc-login">Log in</a> to track your progress';
    } else if (d.rewardSent) {
      st.className = 'wc-reward-status wc-earned';
      st.textContent = 'Earned! ' + (d.rewardSentName && r && d.rewardSentName !== r.name ? 'You already had it, so you got a ' + d.rewardSentName + ' instead. ' : '') + 'Open your gifts in game.';
    } else {
      const left = Math.max(0, d.required - d.completed);
      st.textContent = left > 0 ? left + ' more challenge' + (left === 1 ? '' : 's') + ' to unlock' : 'Unlocked - check your gifts in game';
    }

    const timer = document.getElementById('wcTimer');
    const tick = () => { timer.textContent = countdown(d.endsAt); };
    tick();
    setInterval(tick, 30000);
  }

  load();
})();
