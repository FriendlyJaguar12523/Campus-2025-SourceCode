/* Campus admin panel: the main website's look and behaviour (served at /admin/home_page.js).
 *
 *  - Theme: the site's light / dark / autumn and accent, read from the SAME localStorage keys the
 *    website uses ('campus-theme', 'campus-accent'), so the panel always matches the site.
 *  - Side drawer: the section list; on narrow screens it slides in from the cat button.
 *  - Room editor: "View Room Details", "View Dorm" and "Edit" open it in place; the old
 *    /admin/home/view_room/<id> and /admin/edit-room/<id> links land here with ?room=<id>.
 */
(function () {
  'use strict';

  // ───── theme (mirrors Website/app.js) ─────
  var THEME_KEY = 'campus-theme', ACCENT_KEY = 'campus-accent';
  var THEMES = ['light', 'dark', 'autumn'];
  var SUN = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/></svg>';
  var MOON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/></svg>';
  var LEAF = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M20 3c-7 0-13 3-16 9 3-1 6-1 9 0-4 1-7 3-9 8 6-2 12-4 14-9 1-2.5 2-5 2-8z"/><path d="M4 20l9-9"/></svg>';

  function store(k, v) { try { if (v === undefined) return localStorage.getItem(k); localStorage.setItem(k, v); } catch (e) { return null; } }
  function hexRgb(h) { h = h.replace('#', ''); return [0, 2, 4].map(function (i) { return parseInt(h.slice(i, i + 2), 16); }); }

  function applyTheme(t) {
    if (THEMES.indexOf(t) < 0) t = 'light';
    document.documentElement.setAttribute('data-theme', t);
    syncToggles();
  }
  function applyAccent(c) {
    if (!c || !/^#[0-9a-f]{6}$/i.test(c)) return;
    var s = document.documentElement.style, rgb = hexRgb(c);
    s.setProperty('--red', c);
    s.setProperty('--red-dark', 'rgb(' + rgb.map(function (x) { return x * 0.82 | 0; }).join(',') + ')');
    s.setProperty('--red-soft', 'rgba(' + rgb.join(',') + ',0.15)');
  }
  function current() { return document.documentElement.getAttribute('data-theme') || 'light'; }
  function nextTheme() { return THEMES[(THEMES.indexOf(current()) + 1) % THEMES.length]; }
  function syncToggles() {
    var n = nextTheme();
    var label = { light: SUN + '<span>Light mode</span>', dark: MOON + '<span>Dark mode</span>', autumn: LEAF + '<span>Autumn mode</span>' }[n];
    var icon = { light: SUN, dark: MOON, autumn: LEAF }[n];
    document.querySelectorAll('.cd-theme-toggle').forEach(function (b) { b.innerHTML = label; });
    document.querySelectorAll('.theme-icon-btn').forEach(function (b) { b.innerHTML = icon; b.title = 'Switch to ' + n + ' mode'; b.setAttribute('aria-label', b.title); });
    document.querySelectorAll('.cd-mode').forEach(function (b) { b.classList.toggle('active', b.dataset.theme === current()); });
  }
  function cycleTheme() { var n = nextTheme(); store(THEME_KEY, n); applyTheme(n); }

  applyTheme(store(THEME_KEY) || 'light');
  applyAccent(store(ACCENT_KEY));
  // Another tab (the website, or a second panel tab) changed the theme: follow it.
  window.addEventListener('storage', function (e) {
    if (e.key === THEME_KEY) applyTheme(e.newValue || 'light');
    if (e.key === ACCENT_KEY) applyAccent(e.newValue);
  });

  // ───── drawer ─────
  function setDrawer(open) {
    var side = document.getElementById('adminSidebar'), scrim = document.getElementById('adminScrim');
    if (side) side.classList.toggle('open', open);
    if (scrim) scrim.classList.toggle('open', open);
  }

  function ready(fn) { if (document.readyState !== 'loading') fn(); else document.addEventListener('DOMContentLoaded', fn); }

  ready(function () {
    syncToggles();
    document.querySelectorAll('.cd-theme-toggle, .theme-icon-btn').forEach(function (b) { b.addEventListener('click', cycleTheme); });
    document.querySelectorAll('.cd-mode').forEach(function (b) {
      b.addEventListener('click', function () { store(THEME_KEY, b.dataset.theme); applyTheme(b.dataset.theme); });
    });
    var burger = document.getElementById('adminBurger');
    if (burger) burger.addEventListener('click', function () {
      var side = document.getElementById('adminSidebar');
      setDrawer(!(side && side.classList.contains('open')));
    });
    var scrim = document.getElementById('adminScrim');
    if (scrim) scrim.addEventListener('click', function () { setDrawer(false); });
    // Picking a section on a phone closes the drawer.
    document.querySelectorAll('#adminSidebar .tab').forEach(function (t) {
      t.addEventListener('click', function () { if (window.innerWidth <= 900) setDrawer(false); });
    });

    // Room links open the editor in place instead of leaving the panel.
    document.addEventListener('click', function (e) {
      var a = e.target.closest && e.target.closest('a[href*="/admin/home/view_room/"], a[href*="/admin/edit-room/"]');
      if (!a) return;
      var id = (a.getAttribute('href').match(/\/(\d+)\/?$/) || [])[1];
      if (!id) return;
      e.preventDefault();
      openRoomEditor(id);
    });

    var q = new URLSearchParams(location.search).get('room');
    if (q && /^\d+$/.test(q)) {
      openRoomEditor(q);
      history.replaceState(null, '', location.pathname);
    }
  });

  // ───── room editor ─────
  function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
  var ACCESS = ['Private', 'Public', 'Unlisted', 'Dev only', 'Dev unlisted'];

  function modal() {
    var m = document.getElementById('room-editor');
    if (m) return m;
    m = document.createElement('div');
    m.id = 'room-editor';
    // Its own overlay class: the panel's start-up code hides every .popup-overlay, which closed
    // an editor opened from a ?room= link the moment the page finished loading.
    m.className = 're-overlay';
    m.style.cssText = 'display:none; position:fixed; inset:0; z-index:2000; align-items:center; justify-content:center; padding:16px;'
      + ' background:rgba(10,8,20,.5); backdrop-filter:blur(3px);';
    m.innerHTML = '<div class="popup" style="max-width:780px; width:100%; max-height:90vh; overflow:auto; padding:22px 24px; position:relative;">'
      + '<button type="button" class="icon-btn" id="re-close" aria-label="Close" style="position:absolute; top:12px; right:12px;">'
      + '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M6 6l12 12M18 6L6 18"/></svg></button>'
      + '<div id="re-body">Loading…</div></div>';
    document.body.appendChild(m);
    m.addEventListener('click', function (e) { if (e.target === m) close(); });
    m.querySelector('#re-close').addEventListener('click', close);
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape' && m.style.display === 'flex') close(); });
    function close() { m.style.display = 'none'; }
    return m;
  }

  function openRoomEditor(roomId) {
    var m = modal(), body = m.querySelector('#re-body');
    m.style.display = 'flex';
    body.innerHTML = '<p style="color:var(--muted)">Loading room ' + esc(roomId) + '…</p>';
    fetch('/admin/api/room/' + encodeURIComponent(roomId), { credentials: 'same-origin' })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        if (!d.success) { body.innerHTML = '<h3>Room ' + esc(roomId) + '</h3><p style="color:var(--danger)">' + esc(d.message || 'Not found.') + '</p>'; return; }
        var r = d.room;
        var subs = (r.SubRooms || []).map(function (s) { return '<span class="re-chip">' + esc(s.Name || ('subroom ' + s.SubRoomId)) + ' · ' + s.MaxPlayers + ' max</span>'; }).join('');
        body.innerHTML =
          '<h3 style="margin:0 36px 4px 0;">^' + esc(r.Name) + '</h3>'
          + '<div class="re-meta">'
          + '<span class="re-chip">id ' + r.RoomId + '</span>'
          + '<span class="re-chip">by ' + (r.CreatorName ? '@' + esc(r.CreatorName) : '#' + r.CreatorAccountId) + '</span>'
          + '<span class="re-chip">' + r.PlayersNow + ' playing now</span>'
          + (r.IsDorm ? '<span class="re-chip">dorm</span>' : '') + (r.IsRRO ? '<span class="re-chip">Rec Room Original</span>' : '')
          + subs + '</div>'
          + '<img class="re-img" src="/img/' + esc(r.ImageName || 'DefaultRoomImage.jpg') + '?width=640" alt="">'
          + '<div class="re-grid" style="margin-top:16px;">'
          + '<label class="full">Name<input type="text" id="re-name" value="' + esc(r.Name) + '"></label>'
          + '<label class="full">Description<textarea id="re-desc" rows="3">' + esc(r.Description) + '</textarea></label>'
          + '<label>Visibility<select id="re-acc">' + ACCESS.map(function (n, i) { return '<option value="' + i + '"' + (i === r.Accessibility ? ' selected' : '') + '>' + n + '</option>'; }).join('') + '</select></label>'
          + '<label>Max players<input type="number" id="re-max" min="1" max="100" value="' + r.MaxPlayers + '"></label>'
          + '<label class="full" style="flex-direction:row; align-items:center; gap:10px;"><input type="checkbox" id="re-clone"' + (r.CloningAllowed ? ' checked' : '') + '> Allow cloning</label>'
          + '<label class="full" style="flex-direction:row; align-items:center; gap:10px;"><input type="checkbox" id="re-dev"' + (r.DevRoom ? ' checked' : '') + '> Dev room (developer chips)</label>'
          + '</div>'
          + '<div style="display:flex; gap:10px; justify-content:flex-end; margin-top:18px; flex-wrap:wrap;">'
          + '<a class="btn-login" href="/room/' + encodeURIComponent(r.Name) + '" target="_blank" rel="noopener">View on site</a>'
          + '<button type="button" class="btn btn-primary" id="re-save">Save changes</button>'
          + '</div><p id="re-msg" style="margin:10px 0 0; text-align:right;"></p>'
          + '<div id="re-people" style="margin-top:22px; border-top:1px solid var(--line); padding-top:16px;"><p style="color:var(--muted)">Loading people…</p></div>';
        body.querySelector('#re-save').addEventListener('click', function () { save(r.RoomId); });
        loadPeople(r.RoomId);
      })
      .catch(function () { body.innerHTML = '<p style="color:var(--danger)">Could not load the room.</p>'; });
  }

  function save(roomId) {
    var f = new FormData(), $ = function (id) { return document.getElementById(id); };
    f.append('roomId', roomId);
    f.append('name', $('re-name').value);
    f.append('description', $('re-desc').value);
    f.append('accessibility', $('re-acc').value);
    f.append('maxPlayers', $('re-max').value);
    f.append('cloningAllowed', $('re-clone').checked ? 'true' : 'false');
    f.append('devRoom', $('re-dev').checked ? 'true' : 'false');
    var msg = $('re-msg'), btn = $('re-save');
    btn.disabled = true; msg.style.color = 'var(--muted)'; msg.textContent = 'Saving…';
    fetch('/admin/api/edit_room', { method: 'POST', body: f, credentials: 'same-origin' })
      .then(function (r) { return r.json(); })
      .then(function (d) { msg.style.color = d.success ? 'var(--ok)' : 'var(--danger)'; msg.textContent = d.message || (d.success ? 'Saved.' : 'Failed.'); })
      .catch(function () { msg.style.color = 'var(--danger)'; msg.textContent = 'Save failed.'; })
      .finally(function () { btn.disabled = false; });
  }

  // ───── people: owner, roles, bans (AdminRoomPeopleController) ─────
  function who(w) {
    if (!w || !w.accountId) return '<i>nobody</i>';
    return (w.username ? '@' + esc(w.username) : '#' + w.accountId)
      + (w.displayName && w.displayName !== w.username ? ' <span style="color:var(--muted)">(' + esc(w.displayName) + ')</span>' : '')
      + ' <span style="color:var(--muted); font-size:.85em;">#' + w.accountId + '</span>';
  }
  function when(t) { return t ? new Date(t).toLocaleString() : ''; }
  var BAN_LENGTHS = [[0, 'Until unbanned'], [60, '1 hour'], [1440, '1 day'], [10080, '1 week'], [43200, '30 days']];

  function peoplePost(roomId, what, fields) {
    var f = new FormData();
    Object.keys(fields).forEach(function (k) { f.append(k, fields[k]); });
    var msg = document.getElementById('rp-msg');
    if (msg) { msg.style.color = 'var(--muted)'; msg.textContent = 'Working…'; }
    return fetch('/admin/api/room/' + roomId + '/' + what, { method: 'POST', body: f, credentials: 'same-origin' })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        loadPeople(roomId, d.message, d.success);
        return d;
      })
      .catch(function () { if (msg) { msg.style.color = 'var(--danger)'; msg.textContent = 'That failed.'; } });
  }

  function loadPeople(roomId, note, ok) {
    var box = document.getElementById('re-people');
    if (!box) return;
    fetch('/admin/api/room/' + encodeURIComponent(roomId) + '/people', { credentials: 'same-origin' })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        if (!d.success) { box.innerHTML = '<p style="color:var(--danger)">' + esc(d.message || 'Could not load people.') + '</p>'; return; }
        var roleOpts = function (sel) {
          return d.grantable.map(function (g) { return '<option value="' + g.value + '"' + (g.value === sel ? ' selected' : '') + '>' + esc(g.name) + '</option>'; }).join('');
        };
        var rows = d.roles.length ? d.roles.map(function (r) {
          return '<div class="rp-row" style="display:flex; gap:10px; align-items:center; justify-content:space-between; padding:6px 0; border-bottom:1px solid var(--line);">'
            + '<span>' + who(r.who) + (r.pending ? ' <span class="re-chip">invited as ' + esc(r.pendingRole) + '</span>' : '') + '</span>'
            + '<span style="display:flex; gap:6px; align-items:center;"><select class="rp-role" data-id="' + r.who.accountId + '">' + roleOpts(r.role) + '</select>'
            + '<button type="button" class="btn btn-danger rp-ban" data-id="' + r.who.accountId + '">Ban</button></span></div>';
        }).join('') : '<p style="color:var(--muted); margin:6px 0;">Nobody else has a role on this room.</p>';
        var bans = d.bans.length ? d.bans.map(function (b) {
          return '<div style="display:flex; gap:10px; align-items:center; justify-content:space-between; padding:6px 0; border-bottom:1px solid var(--line);">'
            + '<span>' + who(b.who) + '<br><span style="color:var(--muted); font-size:.85em;">by ' + who(b.by) + ' · ' + when(b.start)
            + (b.end ? ' · until ' + when(b.end) : ' · until unbanned') + (b.reason ? ' · “' + esc(b.reason) + '”' : '') + '</span></span>'
            + '<button type="button" class="btn btn-primary rp-unban" data-id="' + b.who.accountId + '">Unban</button></div>';
        }).join('') : '<p style="color:var(--muted); margin:6px 0;">Nobody is banned from this room.</p>';
        var hist = d.history.length ? '<details style="margin-top:8px;"><summary style="cursor:pointer; color:var(--muted);">Earlier bans (' + d.history.length + ')</summary>'
          + d.history.map(function (h) {
            return '<div style="font-size:.85em; padding:4px 0; border-bottom:1px solid var(--line);">' + who(h.who) + ' - banned by ' + who(h.by) + ' ' + when(h.start)
              + (h.reason ? ' “' + esc(h.reason) + '”' : '') + ' - <b>' + esc(h.lifted) + '</b>' + (h.unbannedBy ? ' by ' + who(h.unbannedBy) : '') + '</div>';
          }).join('') + '</details>' : '';

        box.innerHTML =
          '<h4 style="margin:0 0 8px;">People</h4>'
          + '<p id="rp-msg" style="margin:0 0 10px; min-height:1.2em; color:' + (ok === false ? 'var(--danger)' : 'var(--ok)') + ';">' + esc(note || '') + '</p>'
          + '<div style="margin-bottom:14px;"><b>Owner:</b> ' + who(d.owner) + '</div>'
          + (d.isDorm ? '<p style="color:var(--muted); margin:0 0 12px;">This is a dorm - it belongs to its player and can\'t be given away.</p>'
            : '<div class="re-grid" style="margin-bottom:16px;">'
              + '<label>Transfer ownership to<input type="text" id="rp-owner" placeholder="@username or account id"></label>'
              + '<label style="justify-content:flex-end;"><span style="display:flex; gap:8px; align-items:center;"><input type="checkbox" id="rp-keep" checked> keep the old owner as co-owner</span>'
              + '<button type="button" class="btn btn-warning" id="rp-transfer" style="margin-top:6px;">Transfer</button></label></div>')
          + '<h5 style="margin:6px 0;">Roles</h5>' + rows
          + '<div class="re-grid" style="margin:10px 0 18px;">'
          + '<label>Give a role to<input type="text" id="rp-rolewho" placeholder="@username or account id"></label>'
          + '<label>Role<span style="display:flex; gap:6px;"><select id="rp-rolepick">' + roleOpts(20).replace('value="0"', 'value="0" disabled') + '</select>'
          + '<button type="button" class="btn btn-primary" id="rp-roleadd">Give role</button></span></label></div>'
          + '<h5 style="margin:6px 0;">Bans</h5>' + bans + hist
          + '<div class="re-grid" style="margin-top:10px;">'
          + '<label>Ban<input type="text" id="rp-banwho" placeholder="@username or account id"></label>'
          + '<label>For<select id="rp-banlen">' + BAN_LENGTHS.map(function (x) { return '<option value="' + x[0] + '">' + x[1] + '</option>'; }).join('') + '</select></label>'
          + '<label class="full">Reason (optional)<span style="display:flex; gap:6px;"><input type="text" id="rp-banwhy" placeholder="shown in the ban history" style="flex:1;">'
          + '<button type="button" class="btn btn-danger" id="rp-banadd">Ban from room</button></span></label></div>';

        var $ = function (id) { return document.getElementById(id); };
        box.querySelectorAll('.rp-role').forEach(function (sel) {
          sel.addEventListener('change', function () {
            var label = sel.options[sel.selectedIndex].text;
            if (sel.value === '0' && !confirm('Remove their role?')) { loadPeople(roomId); return; }
            peoplePost(roomId, 'role', { player: sel.dataset.id, role: sel.value });
          });
        });
        box.querySelectorAll('.rp-ban').forEach(function (b) {
          b.addEventListener('click', function () { $('rp-banwho').value = b.dataset.id; $('rp-banwho').scrollIntoView({ block: 'center' }); $('rp-banwhy').focus(); });
        });
        box.querySelectorAll('.rp-unban').forEach(function (b) {
          b.addEventListener('click', function () { peoplePost(roomId, 'unban', { player: b.dataset.id }); });
        });
        var t = $('rp-transfer');
        if (t) t.addEventListener('click', function () {
          var to = $('rp-owner').value.trim();
          if (!to) { $('rp-owner').focus(); return; }
          if (!confirm('Give ^' + d.name + ' to ' + to + '? They become its owner.')) return;
          peoplePost(roomId, 'owner', { player: to, keepOld: $('rp-keep').checked ? 'true' : 'false' });
        });
        $('rp-roleadd').addEventListener('click', function () {
          var to = $('rp-rolewho').value.trim();
          if (!to) { $('rp-rolewho').focus(); return; }
          peoplePost(roomId, 'role', { player: to, role: $('rp-rolepick').value });
        });
        $('rp-banadd').addEventListener('click', function () {
          var to = $('rp-banwho').value.trim();
          if (!to) { $('rp-banwho').focus(); return; }
          if (!confirm('Ban ' + to + ' from ^' + d.name + '? If they are in it now they are sent out.')) return;
          peoplePost(roomId, 'ban', { player: to, minutes: $('rp-banlen').value, reason: $('rp-banwhy').value });
        });
      })
      .catch(function () { box.innerHTML = '<p style="color:var(--danger)">Could not load people.</p>'; });
  }

  window.openRoomEditor = openRoomEditor;
})();
