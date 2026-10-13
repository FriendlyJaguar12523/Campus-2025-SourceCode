// ───── STORAGE SAFETY + SCHEMA VERSION ─────
// Wrap localStorage.setItem so a full quota (or privacy-mode block) never throws
// and breaks the page — it degrades gracefully with a warning instead.
(function () {
  if (!window.localStorage) return;
  const _set = Storage.prototype.setItem;
  Storage.prototype.setItem = function (k, v) {
    try { return _set.call(this, k, v); }
    catch (e) { console.warn('[campus] storage write failed for', k, e && e.name); }
  };
})();

// Bump this when the shape of stored data changes incompatibly; old state is
// cleared once so users don't get stuck with a broken/mismatched cache.
const SCHEMA_VERSION = '1';
try {
  const prev = localStorage.getItem('campus-schema-version');
  if (prev && prev !== SCHEMA_VERSION) {
    // future migrations would run here; for now we just record the new version
  }
  localStorage.setItem('campus-schema-version', SCHEMA_VERSION);
} catch {}

// Global image fallback: if an avatar (DiceBear) or content image (Picsum) fails
// to load — e.g. the external service is down — show a graceful placeholder
// instead of a broken-image icon.
function initialsAvatar(seed) {
  const ch = (String(seed || '?').replace(/[^a-zA-Z0-9]/g, '')[0] || '?').toUpperCase();
  const colors = ['#ff5a3c', '#f0453f', '#ff6b9d', '#e84393', '#c44569', '#d63031'];
  const bg = colors[ch.charCodeAt(0) % colors.length];
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100"><rect width="100" height="100" fill="${bg}"/><text x="50" y="50" dy=".35em" text-anchor="middle" font-family="Inter,sans-serif" font-size="48" font-weight="700" fill="#fff">${ch}</text></svg>`;
  return 'data:image/svg+xml;utf8,' + encodeURIComponent(svg);
}
document.addEventListener('error', e => {
  const img = e.target;
  if (!img || img.tagName !== 'IMG' || img.dataset.failed) return;
  img.dataset.failed = '1';
  if (img.hasAttribute('data-av')) {
    img.src = initialsAvatar(img.getAttribute('data-av') || img.getAttribute('alt'));
  } else {
    img.classList.add('img-failed');
  }
}, true);

// ───── NAV TOGGLE (mobile) + cat spin ─────
const burger = document.getElementById('burger');
const burgerCat = document.getElementById('burgerCat');
// force a consistent cat size on every page, overriding any cached CSS
if (burgerCat) {
  burgerCat.style.setProperty('width', '40px', 'important');
  burgerCat.style.setProperty('height', '40px', 'important');
  burgerCat.style.setProperty('object-fit', 'contain', 'important');
}
const scrim = document.getElementById('scrim');
// home uses the cat drawer, download uses #sidebar, else the top nav
const navTarget = document.getElementById('catDrawer')
  || document.getElementById('sidebar')
  || document.getElementById('topNav');

let spin = 0;

// inject the "Campus" brand text next to the cat icon
let topbarBrand = null;
if (burger) {
  topbarBrand = document.createElement('span');
  topbarBrand.className = 'topbar-brand';
  topbarBrand.textContent = 'Campus';
  burger.insertAdjacentElement('afterend', topbarBrand);
}

function setNav(open) {
  if (navTarget) {
    navTarget.classList.toggle('open', open);
    // when closed, take the off-screen drawer out of the tab order entirely
    navTarget.inert = !open;
  }
  if (scrim) scrim.classList.toggle('open', open);
  if (topbarBrand) topbarBrand.classList.toggle('show', open);
  document.body.style.overflow = open ? 'hidden' : '';
  if (open && burgerCat) {
    spin += 360;
    burgerCat.style.transform = `rotate(${spin}deg)`;
  }
  // move focus into the drawer when opening, back to the burger when closing
  if (open && navTarget) navTarget.querySelector('a, button')?.focus();
  else if (!open && burger && document.activeElement && navTarget?.contains(document.activeElement)) burger.focus();
}

if (burger && navTarget) {
  navTarget.inert = !navTarget.classList.contains('open'); // closed by default
  burger.addEventListener('click', () => setNav(!navTarget.classList.contains('open')));
  if (scrim) scrim.addEventListener('click', () => setNav(false));
  navTarget.querySelectorAll('a').forEach(a => a.addEventListener('click', () => setNav(false)));
}

// skip-to-content link: move real focus to the target so keyboard/screen-reader
// users land in the main content, not just scroll there
document.querySelector('.skip-link')?.addEventListener('click', e => {
  // preventDefault matters here: with <base href="/"> on clean-URL pages, the browser would
  // otherwise resolve "#main" to "/#main" and navigate to the home page instead of scrolling.
  e.preventDefault();
  const target = document.querySelector(e.currentTarget.getAttribute('href'));
  if (target) { target.setAttribute('tabindex', '-1'); target.focus(); target.scrollIntoView(); }
});

// Esc closes the drawer and any open dropdown menus
document.addEventListener('keydown', e => {
  if (e.key !== 'Escape') return;
  if (navTarget && navTarget.classList.contains('open')) setNav(false);
  const notif = document.querySelector('.notif.open');
  if (notif) {
    notif.classList.remove('open');
    const nb = notif.querySelector('#notifBtn');
    if (nb) { nb.setAttribute('aria-expanded', 'false'); nb.focus(); }
  }
  const prof = document.querySelector('.profile.open');
  if (prof) {
    prof.classList.remove('open');
    const av = prof.querySelector('.profile-av');
    if (av) { av.setAttribute('aria-expanded', 'false'); av.focus(); }
  }
});

// arrow-key roving focus within an open dropdown menu
function enableMenuArrowKeys(menu, itemSelector, returnFocusEl) {
  if (!menu) return;
  menu.addEventListener('keydown', e => {
    const items = [...menu.querySelectorAll(itemSelector)];
    if (!items.length) return;
    const i = items.indexOf(document.activeElement);
    if (e.key === 'ArrowDown') { e.preventDefault(); items[(i + 1 + items.length) % items.length].focus(); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); items[(i - 1 + items.length) % items.length].focus(); }
    else if (e.key === 'Home') { e.preventDefault(); items[0].focus(); }
    else if (e.key === 'End') { e.preventDefault(); items[items.length - 1].focus(); }
    else if (e.key === 'Escape' && returnFocusEl) { returnFocusEl.focus(); }
  });
}

// ───── DEMO AUTH ─────
const DEMO_USER = 'catlover';
const DEMO_PASS = 'meow123';
const USER_KEY = 'campus-user';
const BLANK_PX = 'data:image/gif;base64,R0lGODlhAQABAAAAACH5BAEKAAEALAAAAAABAAEAAAICTAEAOw==';

// apply a custom (cropped-photo) image onto an <img>
function applyCropToImg(img, d) {
  if (!img || !d) return;
  img.removeAttribute('srcset');
  img.src = d.image;
  img.style.objectFit = 'cover';
  img.style.objectPosition = (d.pos && d.pos !== 'center') ? d.pos : 'center';
}

// ───── PLAIN TEXT ONLY ─────
// The server refuses fancy-font letters, emoji and other symbols in anything the site sends
// (SitePlainTextMiddleware). Every text box follows the same rule as you type or paste: keyboard
// punctuation a phone swaps in (curly quotes, dashes, …) becomes plain, anything else is removed.
// Password boxes are never touched.
function toPlainText(s) {
  return s.replace(/[‘’‚′]/g, "'").replace(/[“”„″]/g, '"')
    .replace(/[–—−]/g, '-').replace(/…/g, '...').replace(/[  ]/g, ' ')
    .replace(/[^\x20-\x7E\n\r\t]/g, '');
}
let PLAIN_TOAST_AT = 0;
document.addEventListener('input', e => {
  const el = e.target;
  const box = el instanceof HTMLTextAreaElement ||
    (el instanceof HTMLInputElement && /^(text|search|email|url|tel)$/.test(el.type));
  if (!box) return;
  const v = el.value, p = toPlainText(v);
  if (p === v) return;
  let pos = null;
  try { pos = el.selectionStart - (v.length - p.length); } catch {}
  el.value = p;
  if (pos != null) { try { el.setSelectionRange(pos, pos); } catch {} }
  // Say why once per few seconds; plain punctuation swaps are silent.
  if (toPlainText(v.replace(/[‘’‚′“”„″–—−…  ]/g, '')) !== v.replace(/[‘’‚′“”„″–—−…  ]/g, '')
      && Date.now() - PLAIN_TOAST_AT > 4000 && typeof toast === 'function') {
    PLAIN_TOAST_AT = Date.now();
    toast('Only plain text is allowed - no special fonts, symbols or emoji.');
  }
}, true);

// ───── BACKEND ─────
// True when the Campus C# server is answering. apiBase is '' (same-origin), which is
// falsy, so it can't double as the "is the API up?" flag - that's what apiEnabled is for.
function apiOn() {
  return !!(window.CAMPUS_DATA && window.CAMPUS_DATA.apiEnabled);
}
function apiUrl(path) {
  return ((window.CAMPUS_DATA && window.CAMPUS_DATA.apiBase) || '') + path;
}
// The session lives in an HttpOnly cookie the server sets, so it is never readable from
// here - every call just needs credentials to ride along.
async function apiPost(path, body) {
  const resp = await fetch(apiUrl(path), {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body || {}),
  });
  const out = await resp.json().catch(() => ({}));
  banRefused(resp, out);
  return { ok: resp.ok && out.ok !== false, status: resp.status, data: out };
}

// A banned account is signed in but can't change anything: the server answers every write with
// 403 {banned: true, error}. Say so, whatever the caller does with the failure.
function banRefused(resp, out) {
  if (resp && resp.status === 403 && out && out.banned) { toast(out.error || 'This account is banned.'); return true; }
  return false;
}

// DELETE an API path. Same envelope as apiPost.
async function apiDelete(path) {
  const resp = await fetch(apiUrl(path), { method: 'DELETE', credentials: 'same-origin' });
  const out = await resp.json().catch(() => ({}));
  banRefused(resp, out);
  return { ok: resp.ok && out.ok !== false, status: resp.status, data: out };
}

// A photo's numeric server id from the element it was rendered in (data-photo), or null for
// demo content - which stays local-only.
function photoIdOf(el) {
  const host = el && el.closest('[data-photo]');
  const id = host && host.dataset.photo;
  return id && /^\d+$/.test(id) ? id : null;
}

// GET an API path as JSON. Returns null on any failure so callers can fall back.
async function apiGet(path) {
  try {
    const resp = await fetch(apiUrl(path), { credentials: 'same-origin' });
    if (!resp.ok) return null;
    return await resp.json();
  } catch { return null; }
}

// ───── CLEAN URLS (/user/{h}, /room/{id}, /image/{id}) ─────
// Pages are served at clean paths by WebController; the page JS reads its id/handle from
// the path here. Query params (?u=, ?id=) still work as a fallback for old links.
function userUrl(h) { return '/user/' + encodeURIComponent(String(h || '').replace(/^@/, '')); }

// ───── EVENTS (a profile's Events tab and a room's Events tab) ─────
// Cards come from /api/site/profile/{h}/events and /api/site/rooms/{id}/events: { id, name, description, image,
// start, end (UTC ISO), status live|upcoming|ended, room{id,name}, host{...}, going, interested, myResponse }.
// Times are shown in the visitor's own timezone. Signed-in visitors can RSVP (Going / Interested) right on the card.
function eventWhen(e) {
  // "Jan 12th, 1:00pm - 2:00pm" (a multi-day event: "Jan 12th, 9:00pm - Jan 13th, 1:00am"), in the visitor's timezone
  const s = new Date(e.start), en = new Date(e.end);
  const MON = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
  const ord = n => { const v = n % 100; return n + ((v >= 11 && v <= 13) ? 'th' : (['th', 'st', 'nd', 'rd'][n % 10] || 'th')); };
  const day = d => MON[d.getMonth()] + ' ' + ord(d.getDate());
  const tm = d => { let h = d.getHours(); const ap = h >= 12 ? 'pm' : 'am'; h = h % 12 || 12; return h + ':' + String(d.getMinutes()).padStart(2, '0') + ap; };
  if (e.status === 'live') return 'Live now \u00b7 until ' + tm(en);
  return s.toDateString() === en.toDateString()
    ? `${day(s)}, ${tm(s)} - ${tm(en)}`
    : `${day(s)}, ${tm(s)} - ${day(en)}, ${tm(en)}`;
}

function eventCardHtml(e, opts) {
  opts = opts || {};
  const href = '/event/' + encodeURIComponent(e.id);
  const hostName = e.host ? (e.host.username || '') : '';
  const av = (e.host && e.host.profile_image) || avatarUrl(hostName || 'player');
  const img = e.image ? `background-image:url('${escapeHtml(e.image)}')` : 'background-image:linear-gradient(135deg,#ff5a3c,#7b2ff7)';
  const badge = e.status === 'live' ? '<span class="pevent-live">LIVE</span>' : e.status === 'ended' ? '<span class="pevent-tag">ENDED</span>' : '';
  const canRsvp = e.status !== 'ended' && !!(typeof CAMPUS_ME !== 'undefined' && CAMPUS_ME);
  const rsvp = canRsvp
    ? `<div class="pevent-rsvp"><button type="button" data-r="going" class="${e.myResponse === 'going' ? 'on' : ''}">Going</button>`
      + `<button type="button" data-r="interested" class="${e.myResponse === 'interested' ? 'on' : ''}">Interested</button></div>`
    : '';
  // the Rec Room style card: picture + title + @host, a big picture, when, N ATTENDING
  return `<article class="evc event-card${e.status === 'ended' ? ' event-ended' : ''}" data-event="${e.id}" data-href="${href}">
      <a class="evc-head" href="${href}">
        <img src="${escapeHtml(av)}" alt="" onerror="this.style.visibility='hidden'">
        <span class="evc-titles"><b>${escapeHtml(e.name)}</b><small>${hostName ? '@' + escapeHtml(hostName) : ''}</small></span>
      </a>
      <a class="evc-img" href="${href}" style="${img}">${badge}</a>
      <div class="evc-meta">
        <span class="evc-when">${escapeHtml(eventWhen(e))}</span>
        <span class="evc-att"><b>${e.going || 0}</b> ATTENDING</span>
      </div>
      ${rsvp}
    </article>`;
}

function renderEvents(el, events, opts, emptyText) {
  if (!events || !events.length) { el.innerHTML = `<p class="profile-empty">${escapeHtml(emptyText || 'No events.')}</p>`; return; }
  el.innerHTML = events.map(e => eventCardHtml(e, opts)).join('');
  el.querySelectorAll('.event-card').forEach(c => c.addEventListener('click', ev => {
    if (ev.target.closest('a, button')) return;
    location.href = c.dataset.href;
  }));
  el.querySelectorAll('.pevent-rsvp button').forEach(btn => btn.addEventListener('click', async () => {
    const card = btn.closest('.event-card');
    const id = card && card.dataset.event;
    const ev = events.find(x => String(x.id) === String(id));
    if (!ev) return;
    // pressing the answer you already gave takes it back
    const want = ev.myResponse === btn.dataset.r ? 'none' : btn.dataset.r;
    btn.disabled = true;
    const r = await apiPost('/api/site/events/' + encodeURIComponent(id) + '/respond', { response: want });
    btn.disabled = false;
    if (r && r.ok && r.data && r.data.event) {
      Object.assign(ev, r.data.event);
      renderEvents(el, events, opts, emptyText);   // re-draw the list with the new counts and your answer
    } else toast((r && r.data && r.data.error) || 'Could not save your answer');
  }));
}

async function loadEvents(el, path, opts, emptyText) {
  if (!el) return;
  if (!apiOn()) { el.innerHTML = `<p class="profile-empty">${escapeHtml(emptyText || 'No events.')}</p>`; return; }
  el.innerHTML = '<div class="feed-loading">Loading events\u2026</div>';
  // (the later apiGet in this file THROWS on a non-OK answer, the earlier one returns null: handle both)
  let data = null;
  try { data = await apiGet(path); } catch { data = null; }
  renderEvents(el, data && data.ok ? data.events : [], opts, emptyText);
}


// Settings header: your own live status (Online · room / Offline) from the same profile API the
// profile page uses; re-run on every "profile" push about you.
async function renderSettingsStatus() {
  const el = document.getElementById('suStatus');
  const me = localStorage.getItem(USER_KEY);
  if (!el || !me || !apiOn()) return;
  const r = await apiGet('/api/site/profile/' + encodeURIComponent(me));
  const st = r && r.status;
  if (r && r.user && r.user.id) siteWatch(me, r.user.id);
  const dot = el.querySelector('.su-dot');
  if (dot) dot.style.background = st && st.online ? '#2ecc71' : '#9a9aa6';
  el.lastChild.textContent = ' ' + (st ? (st.online ? ('Online' + (st.where ? ' · ' + st.where : '')) : 'Offline') : '');
}

// ---- realtime: which profile this tab is looking at ------------------------------------------
// The site socket (ensureNotifSocket) tells the server, and the server pushes "profile" (status,
// room, name, counts changed - re-fetch) and "spotify" (a new track / pause / seek, already
// filtered for this viewer) frames for it. SITE_WATCH is the handle; SITE_WATCH_ID the account id
// once a fetch has told us (the frames carry the id).
let SITE_WATCH = null, SITE_WATCH_ID = null;
function siteWatch(handle, id) {
  if (handle) SITE_WATCH = String(handle).replace(/^@/, '');
  if (id) SITE_WATCH_ID = String(id);
  if (!SITE_WATCH) return;
  try { if (NOTIF_WS && NOTIF_WS.readyState === 1) NOTIF_WS.send(JSON.stringify({ type: 'watch', profile: SITE_WATCH })); } catch {}
}

// ---- socials --------------------------------------------------------------------------
// Icon per platform (simple monochrome glyphs, currentColor). Keys match SiteSocials.
const SOCIAL_ICONS = {
  discord: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M20.3 4.4A19.8 19.8 0 0 0 15.4 3l-.25.5a14.5 14.5 0 0 1 4.3 1.4 16.6 16.6 0 0 0-14.9 0A14.5 14.5 0 0 1 8.85 3.5L8.6 3a19.8 19.8 0 0 0-4.9 1.4C.6 9 .07 13.5.34 17.9a19.9 19.9 0 0 0 6 3l.5-.7c-.66-.25-1.28-.55-1.86-.9l.46-.36a14.2 14.2 0 0 0 12.12 0l.46.36c-.58.35-1.2.65-1.86.9l.5.7a19.9 19.9 0 0 0 6-3c.33-5.13-.53-9.6-3.86-13.5ZM8.3 15.3c-1.17 0-2.13-1.08-2.13-2.4 0-1.32.94-2.4 2.13-2.4s2.15 1.08 2.13 2.4c0 1.32-.95 2.4-2.13 2.4Zm7.4 0c-1.17 0-2.13-1.08-2.13-2.4 0-1.32.94-2.4 2.13-2.4s2.15 1.08 2.13 2.4c0 1.32-.94 2.4-2.13 2.4Z"/></svg>',
  x: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M17.5 3h3.1l-6.8 7.8L21.8 21h-6.3l-4.9-6.4L5 21H1.9l7.3-8.3L1.5 3h6.4l4.4 5.9L17.5 3Zm-1.1 16.2h1.7L7.1 4.7H5.3l11.1 14.5Z"/></svg>',
  youtube: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M23 7.2a2.9 2.9 0 0 0-2-2C19.2 4.7 12 4.7 12 4.7s-7.2 0-9 .5a2.9 2.9 0 0 0-2 2C.5 9 .5 12 .5 12s0 3 .5 4.8a2.9 2.9 0 0 0 2 2c1.8.5 9 .5 9 .5s7.2 0 9-.5a2.9 2.9 0 0 0 2-2c.5-1.8.5-4.8.5-4.8s0-3-.5-4.8ZM9.7 15.1V8.9l6 3.1-6 3.1Z"/></svg>',
  tiktok: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M16.6 5.8A4.3 4.3 0 0 1 15.5 3h-3.1v12.4a2.6 2.6 0 1 1-2.6-2.6c.3 0 .5 0 .8.1V9.7a5.7 5.7 0 1 0 4.9 5.7V9.2a7.4 7.4 0 0 0 4.3 1.4V7.5a4.3 4.3 0 0 1-3.2-1.7Z"/></svg>',
  instagram: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="3" width="18" height="18" rx="5"/><circle cx="12" cy="12" r="4"/><circle cx="17.5" cy="6.5" r="1" fill="currentColor" stroke="none"/></svg>',
  twitch: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M4.3 2 2.5 6.5V20h5v3h2.8l2.8-3h4.2l5.2-5.2V2H4.3Zm16.4 11.9-3.2 3.2h-5.2l-2.8 2.8v-2.8H5.3V4h15.4v9.9ZM16.9 7.2h-2v5.6h2V7.2Zm-5.2 0h-2v5.6h2V7.2Z"/></svg>',
  steam: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M12 2a10 10 0 0 0-10 9.3l5.4 2.2a2.8 2.8 0 0 1 1.6-.5h.2l2.4-3.5v-.1a3.8 3.8 0 1 1 3.8 3.8h-.1l-3.4 2.4v.2a2.8 2.8 0 0 1-5.6.3L2.4 14.5A10 10 0 1 0 12 2Zm-3.7 15.2-1.2-.5a2.1 2.1 0 0 0 3.9-.9 2.1 2.1 0 0 0-2-2.2l1.3.5a1.6 1.6 0 1 1-2 3.1Zm7.1-5.5a2.5 2.5 0 1 1 0-5.1 2.5 2.5 0 0 1 0 5.1Zm0-4.4a1.9 1.9 0 1 0 0 3.8 1.9 1.9 0 0 0 0-3.8Z"/></svg>',
  github: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M12 .5A11.5 11.5 0 0 0 .5 12c0 5.1 3.3 9.4 7.9 10.9.6.1.8-.2.8-.6v-2c-3.2.7-3.9-1.4-3.9-1.4-.5-1.3-1.3-1.7-1.3-1.7-1-.7.1-.7.1-.7 1.2.1 1.8 1.2 1.8 1.2 1 1.8 2.7 1.3 3.4 1 .1-.8.4-1.3.7-1.6-2.6-.3-5.3-1.3-5.3-5.7 0-1.3.5-2.3 1.2-3.1-.1-.3-.5-1.5.1-3.1 0 0 1-.3 3.2 1.2a11 11 0 0 1 5.8 0c2.2-1.5 3.2-1.2 3.2-1.2.6 1.6.2 2.8.1 3.1.8.8 1.2 1.8 1.2 3.1 0 4.4-2.7 5.4-5.3 5.7.4.4.8 1.1.8 2.2v3.2c0 .3.2.7.8.6A11.5 11.5 0 0 0 23.5 12 11.5 11.5 0 0 0 12 .5Z"/></svg>',
  reddit: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M22 12.1a2.2 2.2 0 0 0-3.7-1.6 10.8 10.8 0 0 0-5.8-1.8l1-4.6 3.2.7a1.5 1.5 0 1 0 .2-1l-3.6-.8a.5.5 0 0 0-.6.4L11.6 8.7a10.8 10.8 0 0 0-5.9 1.8 2.2 2.2 0 1 0-2.4 3.6 4.3 4.3 0 0 0 0 .7c0 3.4 3.9 6.1 8.7 6.1s8.7-2.7 8.7-6.1a4.3 4.3 0 0 0 0-.7 2.2 2.2 0 0 0 1.3-2ZM7 13.6a1.5 1.5 0 1 1 3 0 1.5 1.5 0 0 1-3 0Zm8.6 4.1a5.7 5.7 0 0 1-3.6 1.1 5.7 5.7 0 0 1-3.6-1.1.4.4 0 0 1 .6-.6 4.9 4.9 0 0 0 3 .9 4.9 4.9 0 0 0 3-.9.4.4 0 0 1 .6.6Zm-.3-2.6a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3Z"/></svg>',
  spotify: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M12 1.5a10.5 10.5 0 1 0 0 21 10.5 10.5 0 0 0 0-21Zm4.8 15.2a.65.65 0 0 1-.9.2c-2.5-1.5-5.6-1.9-9.3-1a.65.65 0 1 1-.3-1.3c4-.9 7.5-.5 10.3 1.2.3.2.4.6.2.9Zm1.3-2.9a.8.8 0 0 1-1.1.3c-2.8-1.7-7.2-2.2-10.5-1.2a.8.8 0 1 1-.5-1.6c3.8-1.1 8.6-.6 11.9 1.4.4.2.5.7.2 1.1Zm.1-3a1 1 0 0 1-1.3.3C13.5 9.2 8 9 4.8 10a1 1 0 0 1-.6-1.9c3.7-1.1 9.8-.9 13.7 1.4a1 1 0 0 1 .3 1.3Z"/></svg>'
};
function renderSocials(el, rows) {
  if (!el) return;
  const list = Array.isArray(rows) ? rows : [];
  if (!list.length) { el.hidden = true; el.innerHTML = ''; return; }
  el.innerHTML = list.map(r => {
    const ico = SOCIAL_ICONS[r.platform] || '';
    const inner = ico + '<span class="sc-handle">' + escapeHtml(r.handle || '') + '</span>';
    return r.url
      ? '<a class="social-chip" href="' + escapeHtml(r.url) + '" target="_blank" rel="noopener" title="' + escapeHtml(r.label || '') + '">' + inner + '</a>'
      : '<span class="social-chip static" title="' + escapeHtml(r.label || '') + ' (click to copy)" data-copy="' + escapeHtml(r.handle || '') + '">' + inner + '</span>';
  }).join('');
  el.hidden = false;
  el.querySelectorAll('.social-chip.static').forEach(ch => ch.addEventListener('click', async () => {
    try { await navigator.clipboard.writeText(ch.dataset.copy || ''); toast('Copied ' + (ch.dataset.copy || '')); } catch {}
  }));
}

// ---- Spotify "listening to" ----------------------------------------------------------------
// np = {is_playing, track, artists, album, album_art, url, progress_ms, duration_ms, as_of} from
// the server (null = nothing to show). The bar keeps moving between polls: progress is advanced
// locally while the track is playing.
const SP_ICON = '<svg class="sp-ic" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M12 1.5C6.2 1.5 1.5 6.2 1.5 12S6.2 22.5 12 22.5 22.5 17.8 22.5 12 17.8 1.5 12 1.5zm4.8 15.2a.65.65 0 0 1-.9.2c-2.5-1.5-5.6-1.9-9.3-1a.65.65 0 1 1-.3-1.3c4-.9 7.5-.5 10.3 1.2.3.2.4.6.2.9zm1.3-2.9a.8.8 0 0 1-1.1.3c-2.8-1.7-7.1-2.2-10.5-1.2a.8.8 0 1 1-.5-1.6c3.8-1.2 8.5-.6 11.8 1.4.4.2.5.7.3 1.1zm.1-3a1 1 0 0 1-1.3.3C13.6 9.2 8.3 9 5.1 10a1 1 0 1 1-.6-1.9c3.7-1.1 9.6-.9 13.4 1.4.5.3.6.9.3 1.3z"/></svg>';
function spFmt(ms) { ms = Math.max(0, ms | 0); const s = Math.floor(ms / 1000); return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0'); }
function renderSpotify(el, np) {
  if (!el) return;
  if (!np || !np.track) { el.hidden = true; el.innerHTML = ''; el._np = null; return; }
  const esc = (typeof escapeHtml === 'function') ? escapeHtml : (x => String(x));
  el.hidden = false;
  el.classList.toggle('paused', !np.is_playing);
  if (el.tagName === 'A') { if (np.url) el.href = np.url; else el.removeAttribute('href'); }
  el.innerHTML = (np.album_art ? '<img class="sp-art" src="' + esc(np.album_art) + '" alt="">' : '')
    + '<div class="sp-meta">'
    + '<div class="sp-line">' + SP_ICON + '<span class="sp-track">' + (np.is_playing ? 'Listening to ' : 'Paused · ') + '<b>' + esc(np.track) + '</b></span>'
    + '<span class="sp-by">— ' + esc(np.artists || np.album || '') + '</span></div>'
    + '<div class="sp-bar"><i></i></div>'
    + '<div class="sp-time"><span class="sp-cur"></span><span class="sp-dur">' + spFmt(np.duration_ms) + '</span></div>'
    + '</div>';
  el._np = Object.assign({}, np, { at: Date.now() });
  spTick(el);
}
function spTick(el) {
  const np = el && el._np; if (!np) return;
  let p = np.progress_ms + (np.is_playing ? (Date.now() - np.at) : 0);
  if (np.duration_ms > 0) p = Math.min(p, np.duration_ms);
  const bar = el.querySelector('.sp-bar i'), cur = el.querySelector('.sp-cur');
  if (bar) bar.style.width = (np.duration_ms > 0 ? (100 * p / np.duration_ms) : 0).toFixed(2) + '%';
  if (cur) cur.textContent = spFmt(p);
}
setInterval(() => { document.querySelectorAll('.spotify-now').forEach(el => { if (!el.hidden) spTick(el); }); }, 1000);
function roomUrl(id) { return '/room/' + encodeURIComponent(id); }
function imageUrl(id) { return '/image/' + encodeURIComponent(id); }

// The slug for the current clean path of a given kind, or null.
function routeSlug(kind) {
  const m = location.pathname.match(new RegExp('^/' + kind + '/([^/?#]+)'));
  return m ? decodeURIComponent(m[1]) : null;
}
// Which viewed user/room/image this page is about (clean path first, then ?u=/?id=).
function viewedHandle() { return routeSlug('user') || new URLSearchParams(location.search).get('u'); }
function viewedRoomId() { return routeSlug('room') || new URLSearchParams(location.search).get('id'); }
function viewedImageId() { return routeSlug('image') || new URLSearchParams(location.search).get('id'); }

// Turn an old .html href into its clean equivalent.
function cleanHref(href) {
  if (!href || !/\.html/i.test(href)) return href;
  try {
    const u = new URL(href, location.origin);
    if (u.origin !== location.origin) return href;
    const page = u.pathname.replace(/^.*\//, '');   // filename only
    const qs = u.searchParams;
    if (page === 'profile.html') { const h = qs.get('u'); return h ? userUrl(h) : '/profile'; }
    if (page === 'room.html') { const id = qs.get('id'); return id ? roomUrl(id) : '/'; }
    if (page === 'image.html') { const id = qs.get('id'); return id ? imageUrl(id) : '/'; }
    const map = {
      'index.html': '/', 'login.html': '/login', 'settings.html': '/settings', 'stats.html': '/stats',
      'download.html': '/download', 'accessibility.html': '/accessibility',
      'news.html': '/news', 'patch-notes.html': '/patch-notes', 'weekly.html': '/weekly',
      'support.html': '/support',
    };
    if (map[page]) return map[page] + (page.startsWith('news') || page.startsWith('patch') || page.startsWith('weekly') ? u.search : '');
    return href;
  } catch { return href; }
}
// Rewrite every .html anchor under root (or the whole document) to a clean URL.
function rewriteLinks(root) {
  (root || document).querySelectorAll('a[href]').forEach(a => {
    const h = a.getAttribute('href');
    const c = cleanHref(h);
    if (c && c !== h) a.setAttribute('href', c);
  });
}
// Static nav is present now (app.js runs after the DOM).
rewriteLinks();
// Catch dynamically-rendered links too (feed, profile, search, rails) without editing every
// generator: rewrite any .html anchor as it's added to the DOM.
try {
  new MutationObserver(muts => {
    for (const m of muts) {
      m.addedNodes.forEach(n => {
        if (n.nodeType !== 1) return;
        if (n.tagName === 'A') { const h = n.getAttribute('href'); const c = cleanHref(h); if (c && c !== h) n.setAttribute('href', c); }
        if (n.querySelectorAll) rewriteLinks(n);
      });
    }
  }).observe(document.documentElement, { childList: true, subtree: true });
} catch {}

// The signed-in account, as last reported by the server. localStorage still holds the
// username so the header can render before /api/site/me answers, but the SERVER is the
// authority: syncSession() clears the local copy if the session is gone or expired.
let CAMPUS_ME = null;
let CAMPUS_ADMIN = false;

// All the localStorage keys that describe WHO is signed in. They're stored under fixed
// (not per-user) keys, so switching accounts must overwrite them or the header/settings/tab
// keep showing the previous user. setIdentityCache mirrors the server's account; clearIdentity
// wipes them on logout/401.
function setIdentityCache(u) {
  if (!u || !u.username) return;
  try {
    localStorage.setItem(USER_KEY, u.username);
    localStorage.setItem('campus-username', u.username);
    localStorage.setItem('campus-mynick', u.display_name || u.username);
    localStorage.setItem('campus-mybio', u.bio || '');
  } catch {}
}
function clearIdentityCache() {
  try {
    ['campus-user', 'campus-username', 'campus-mynick', 'campus-mybio'].forEach(k => localStorage.removeItem(k));
  } catch {}
}
// Repaint the already-rendered header chip + settings header from the current cache, so a
// server correction that lands AFTER the first paint (renderAuth runs before syncSession
// resolves) still shows the right name/@handle without needing a manual reload.
function refreshIdentityUI() {
  const me = localStorage.getItem(USER_KEY);
  if (!me) return;
  const name = myDisplayName(me), handle = '@' + myHandle(me);
  document.querySelectorAll('.pm-name').forEach(el => el.textContent = name);
  document.querySelectorAll('.pm-handle').forEach(el => el.textContent = handle);
  const suName = document.getElementById('suName'); if (suName) suName.textContent = name;
  const suHandle = document.getElementById('suHandle'); if (suHandle) suHandle.textContent = handle;
  const u = document.getElementById('setUsername'); if (u && !u.value) u.placeholder = myHandle(me);
  const n = document.getElementById('setNickname'); if (n && !n.value) n.placeholder = myDisplayName(me);
}

async function syncSession() {
  if (!apiOn()) return null;
  try {
    const resp = await fetch(apiUrl('/api/site/me'), { credentials: 'same-origin' });
    if (!resp.ok) {
      // 401 = signed out or the 12h token expired. Drop the stale local identity so the
      // UI doesn't keep showing a logged-in header for a session the server has dropped.
      if (resp.status === 401) { CAMPUS_ME = null; clearIdentityCache(); }
      return null;
    }
    const out = await resp.json();
    CAMPUS_ME = out.user || null;
    CAMPUS_ADMIN = !!out.admin;   // dev account on an admin-allowed network: shows the Admin button
    // The SERVER session is the authority. Overwrite the cached identity keys every load so
    // switching accounts - or creating a new one without logging out first - can never leave
    // the previous user's name/@handle/bio showing in the header, settings or tab.
    if (CAMPUS_ME && CAMPUS_ME.username) setIdentityCache(CAMPUS_ME);
    return CAMPUS_ME;
  } catch {
    return null;   // server unreachable: leave whatever the page already rendered
  }
}

// handle the login form (+ its 2FA second step, for an account that turned it on)
const loginForm = document.getElementById('loginForm');
const login2faForm = document.getElementById('login2faForm');
let loginPendingTicket = '';

function showLogin2faStep(pending) {
  loginPendingTicket = pending;
  loginForm.style.display = 'none';
  document.getElementById('loginFoot').style.display = 'none';
  login2faForm.style.display = 'block';
  document.getElementById('login2faCode').focus();
}

if (loginForm) {
  loginForm.addEventListener('submit', async e => {
    e.preventDefault();
    const u = (document.getElementById('loginUser').value || '').trim();
    const p = document.getElementById('loginPass').value || '';
    const err = document.getElementById('loginError');
    const btn = loginForm.querySelector('.login-submit');

    const fail = msg => {
      err.textContent = msg;
      err.classList.add('show');
      if (btn) { btn.disabled = false; btn.textContent = 'Log in'; }
    };

    if (!u || !p) return fail('Enter your username and password.');

    // Offline demo mode (apiEnabled: false) keeps the old hardcoded account.
    if (!apiOn()) {
      if (u.toLowerCase() === DEMO_USER && p === DEMO_PASS) {
        localStorage.setItem(USER_KEY, 'catlover');
        window.location.href = '/';
      } else {
        fail('Incorrect username or password - try the demo login below.');
      }
      return;
    }

    if (btn) { btn.disabled = true; btn.textContent = 'Logging in…'; }
    err.classList.remove('show');

    try {
      const { ok, data } = await apiPost('/api/site/login', { username: u, password: p });
      if (data && data.need2fa) { if (btn) { btn.disabled = false; btn.textContent = 'Log in'; } showLogin2faStep(data.pending); return; }
      if (!ok) return fail(data.error || 'Incorrect username or password.');
      if (data.user && data.user.username) { clearIdentityCache(); setIdentityCache(data.user); }
      window.location.href = '/';
    } catch {
      fail('Could not reach the server. Try again in a moment.');
    }
  });
}

if (login2faForm) {
  document.getElementById('login2faBack').addEventListener('click', () => {
    login2faForm.style.display = 'none';
    loginForm.style.display = 'block';
    document.getElementById('loginFoot').style.display = '';
    document.getElementById('loginError').textContent = '';
    document.getElementById('loginError').classList.remove('show');
  });
  login2faForm.addEventListener('submit', async e => {
    e.preventDefault();
    const err = document.getElementById('login2faError');
    const btn = login2faForm.querySelector('.login-submit');
    err.classList.remove('show'); err.textContent = '';
    if (btn) { btn.disabled = true; btn.textContent = 'Verifying…'; }
    try {
      const { ok, data } = await apiPost('/api/site/login/2fa', { pending: loginPendingTicket, code: document.getElementById('login2faCode').value });
      if (btn) { btn.disabled = false; btn.textContent = 'Verify'; }
      if (data && data.pending) loginPendingTicket = data.pending;
      if (!ok) { err.textContent = data.error || 'Wrong code.'; err.classList.add('show'); document.getElementById('login2faCode').value = ''; return; }
      if (data.user && data.user.username) { clearIdentityCache(); setIdentityCache(data.user); }
      window.location.href = '/';
    } catch {
      if (btn) { btn.disabled = false; btn.textContent = 'Verify'; }
      err.textContent = 'Could not reach the server. Try again in a moment.'; err.classList.add('show');
    }
  });
}

// handle the create-account (signup) form
const signupForm = document.getElementById('signupForm');
if (signupForm) {
  signupForm.addEventListener('submit', async e => {
    e.preventDefault();
    const val = id => (document.getElementById(id)?.value || '');
    const username = val('suUser').trim();
    const birthday  = val('suDob').trim();   // YYYY-MM-DD from <input type=date>
    const pass      = val('suPass');
    const confirm   = val('suConfirm');
    const email     = val('suEmail').trim();
    const website   = val('suWebsite');   // honeypot - stays empty for humans
    const err = document.getElementById('signupError');
    const btn = signupForm.querySelector('.login-submit');

    const fail = msg => {
      err.textContent = msg;
      err.classList.add('show');
      if (btn) { btn.disabled = false; btn.textContent = 'Create account'; }
    };

    // Client-side mirrors of the server rules, for instant feedback. The server re-checks all.
    // NOTE: under-13 accounts get a random @name assigned by the server and are set to junior,
    // so the username rules below are only a courtesy for 13+ (the server re-validates anyway).
    if (username.length < 3 || username.length > 20) return fail('Username must be 3-20 characters.');
    if (!/^[A-Za-z0-9_]+$/.test(username)) return fail('Username can only use letters, numbers and underscores.');
    if (!birthday) return fail('Enter your date of birth.');
    if (pass.length < 6) return fail('Password must be at least 6 characters.');
    if (pass !== confirm) return fail("The passwords don't match.");

    if (!apiOn()) return fail('Sign-up needs the live server.');

    if (btn) { btn.disabled = true; btn.textContent = 'Creating…'; }
    err.classList.remove('show');

    try {
      const { ok, data } = await apiPost('/api/site/register', {
        username, password: pass,
        confirm_password: confirm, email, birthday, website
      });
      if (!ok) return fail(data.error || 'Could not create account.');
      if (data.user && data.user.username) { clearIdentityCache(); setIdentityCache(data.user); }
      window.location.href = '/';
    } catch {
      fail('Could not reach the server. Try again in a moment.');
    }
  });
}

// display name / handle for the logged-in user (username + nickname can be customised)
function myDisplayName(login) {
  return localStorage.getItem('campus-mynick') || localStorage.getItem('campus-username') || cap(login || '');
}
function myHandle(login) {
  return localStorage.getItem('campus-username') || login || '';
}

// reflect logged-in state in the UI (runs on every page)
function renderAuth() {
  const user = localStorage.getItem(USER_KEY);
  if (!user) return;

  // swap "Log in" for the profile picture + dropdown menu
  const loginBtn = document.querySelector('.btn-login');
  if (loginBtn) {
    const av = avatarUrl(user);
    const chip = document.createElement('div');
    chip.className = 'profile';
    chip.innerHTML =
      `<img class="profile-av" data-av="${avKey(user)}" src="${av}" alt="${user}" tabindex="0" role="button" aria-haspopup="true" aria-expanded="false" aria-label="Account menu">
       <div class="profile-menu" role="menu">
         <div class="pm-head">
           <img class="pm-av" data-av="${avKey(user)}" src="${av}" alt="">
           <div class="pm-info"><span class="pm-name">${myDisplayName(user)}</span><span class="pm-handle">@${myHandle(user)}</span></div>
         </div>
         <div class="pm-tokens">
           <span class="pm-token-ic"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M10 7.5v11"/><path d="M10 11c0-2.2 2.2-3.5 4.6-2.9"/><path d="M7 13.6h6.5"/><path d="M7 16.2h6.5"/></svg></span>
           <span class="pm-token-val">1,250</span>
         </div>
         <a href="/profile" class="pm-item"><svg viewBox="0 0 20 20" fill="none"><circle cx="10" cy="7" r="3" stroke="currentColor" stroke-width="1.6"/><path d="M4 17c0-3 2.7-5 6-5s6 2 6 5" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/></svg>Profile</a>
         <a href="#" class="pm-item"><svg viewBox="0 0 20 20" fill="none"><path d="M4 6h12l-1 9a2 2 0 0 1-2 1.7H7A2 2 0 0 1 5 15L4 6Z" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round"/><path d="M7 6a3 3 0 0 1 6 0" stroke="currentColor" stroke-width="1.6"/></svg>Shop</a>
         <a href="/stats" class="pm-item"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M4 19V10"/><path d="M10 19V4"/><path d="M16 19v-7"/><path d="M22 19H2"/></svg>Stats</a>
         <a href="/settings" class="pm-item"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/></svg>Settings</a>
         <button class="pm-item pm-logout"><svg viewBox="0 0 20 20" fill="none"><path d="M8 17H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h3M13 14l4-4-4-4M17 10H8" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>Log out</button>
       </div>`;
    loginBtn.replaceWith(chip);

    const avBtn = chip.querySelector('.profile-av');
    function toggleProfile(open) {
      document.querySelector('.notif')?.classList.remove('open');
      const isOpen = open ?? !chip.classList.contains('open');
      chip.classList.toggle('open', isOpen);
      avBtn.setAttribute('aria-expanded', isOpen ? 'true' : 'false');
      if (isOpen) chip.querySelector('.pm-item')?.focus();
    }
    avBtn.addEventListener('click', e => { e.stopPropagation(); toggleProfile(); });
    avBtn.addEventListener('keydown', e => {
      if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggleProfile(); }
      else if (e.key === 'ArrowDown') { e.preventDefault(); toggleProfile(true); }
    });
    enableMenuArrowKeys(chip.querySelector('.profile-menu'), '.pm-item', avBtn);
    chip.querySelector('.pm-logout').addEventListener('click', async () => {
      // Tell the server first so it clears the session cookie; the local wipe and the
      // reload happen either way, so a failed request still logs you out of this browser.
      if (apiOn()) { try { await apiPost('/api/site/logout'); } catch {} }
      clearIdentityCache();
      window.location.reload();
    });
    document.addEventListener('click', e => {
      if (!chip.contains(e.target)) toggleProfile(false);
    });
  }

  // hide the top-bar Download button while logged in
  const dlBtn = document.querySelector('.top-right .btn-dl');
  if (dlBtn) dlBtn.style.display = 'none';
}

// Render immediately from the cached username so the header doesn't flash, then confirm
// with the server. If the session turned out to be dead, syncSession cleared USER_KEY and
// this second pass leaves the "Log in" button in place.
renderAuth();
const SESSION_READY = syncSession();
SESSION_READY.then(me => renderBanBanner(me));
SESSION_READY.then(me => {
  const alreadyRendered = !!document.querySelector('.profile');
  if (me && !alreadyRendered) renderAuth();
  // syncSession may have corrected the cached identity (account switch / new account); repaint
  // the header + settings text so it doesn't keep showing the previous user.
  if (me) refreshIdentityUI();
  // Now the server has answered, swap the REAL profile picture in wherever the logged-in
  // user's own avatar shows: the top-right nav chip and the settings header. /api/site/me
  // hands back a raw image name, so turn it into a /img/ URL first.
  applyMyAvatar();
});

// The logged-in user's real profile-picture URL (from CAMPUS_ME), or null.
function myPfpUrl() {
  const raw = CAMPUS_ME && CAMPUS_ME.profile_image;
  if (!raw) return null;
  return /^https?:|^\//.test(raw) ? raw : (((window.CAMPUS_DATA && window.CAMPUS_DATA.imgBase) || '/img/') + raw);
}
// Point every "me" avatar (nav chip + settings) at the real picture, and show the real token
// balance in the account menu.
async function applyMyAvatar() {
  const me = localStorage.getItem(USER_KEY);
  if (!me) return;

  // Avatar: the in-game profile picture, taken from the PUBLIC profile endpoint - it returns
  // profile_image as a ready-to-use /img/ URL (the same one that makes the profile-page avatar
  // work), so this is the source of truth. CAMPUS_ME's raw name is only a last-ditch fallback.
  let url = null;
  if (apiOn()) {
    const r = await apiGet('/api/site/profile/' + encodeURIComponent(me));
    if (r && r.ok && r.user && r.user.profile_image) url = r.user.profile_image;
  }
  if (!url) url = myPfpUrl();
  if (url) {
    // Persist so avatarUrl() hands back the real picture on EVERY page (comments, feed, nav,
    // profile - anywhere the user's avatar is drawn), and update every already-rendered avatar
    // for this user on THIS page live. The 'storage' listener carries the change to other tabs.
    try { localStorage.setItem('campus-pfp-' + avKey(me), JSON.stringify({ image: url })); } catch {}
    refreshAvatars(avKey(me), url);
    document.querySelectorAll('.profile-av, .pm-av, #suAv').forEach(im => {
      im.removeAttribute('srcset'); im.dataset.failed = ''; im.src = url; im.style.objectFit = 'cover';
    });
  }

  // Tokens (RecCenterTokens) from the authed /me - matches what the game shows. Left as the
  // placeholder if the website session isn't authed (a balance must not come from a public call).
  const tokens = CAMPUS_ME && CAMPUS_ME.tokens;
  if (tokens != null) document.querySelectorAll('.pm-token-val').forEach(el => { el.textContent = Number(tokens).toLocaleString(); });
}

// ───── NOTIFICATIONS (real, persistent, game-synced inbox) ─────
// Reads /api/site/notifications, which aggregates the two server-side sources of truth so the
// website shows exactly what the game does: incoming friend requests (accept/decline here) and
// saved messages (cheers, coach messages…). Game / party / join invites can't be actioned in a
// browser, so they show "Launch the game to view." Actions hit the same endpoints the game uses,
// so dismissing / accepting stays in sync. NOTIF_ITEMS === null means signed out.
let NOTIF_ITEMS = [];
let NOTIF_WRAP = null, NOTIF_MENU = null, NOTIF_BADGE = null, NOTIF_BTN = null;

function notifTimeAgo(iso) {
  if (!iso) return '';
  const t = new Date(iso).getTime();
  if (isNaN(t)) return '';
  const s = Math.max(0, Math.floor((Date.now() - t) / 1000));
  if (s < 60) return 'just now';
  const m = Math.floor(s / 60); if (m < 60) return m + 'm ago';
  const h = Math.floor(m / 60); if (h < 24) return h + 'h ago';
  const d = Math.floor(h / 24); if (d < 7) return d + 'd ago';
  return new Date(iso).toLocaleDateString();
}

function notifAvatarHtml(item) {
  const seed = (item.fromPlayer && item.fromPlayer.username) || 'player';
  const src = (item.fromPlayer && item.fromPlayer.profile_image) || avatarUrl(seed);
  return `<img class="nm-av" data-av="${avKey(seed)}" src="${src}" alt="">`;
}

async function fetchNotifications() {
  // apiGet THROWS on 401 (signed out) / any non-ok, so catch and treat as "no inbox" (null).
  try {
    const data = await apiGet('/api/site/notifications');
    NOTIF_ITEMS = (data && data.ok !== false && Array.isArray(data.items)) ? data.items : null;
  } catch {
    NOTIF_ITEMS = null;
  }
}

function syncNotifBadge() {
  if (!NOTIF_BTN) return;
  const n = Array.isArray(NOTIF_ITEMS) ? NOTIF_ITEMS.length : 0;
  NOTIF_BTN.setAttribute('aria-label', n > 0 ? `Notifications, ${n} unread` : 'Notifications');
  if (!NOTIF_BADGE) return;
  if (n > 0) { NOTIF_BADGE.textContent = n > 99 ? '99+' : n; NOTIF_BADGE.classList.remove('hidden'); }
  else { NOTIF_BADGE.textContent = ''; NOTIF_BADGE.classList.add('hidden'); }
}

function renderNotifMenu() {
  if (!NOTIF_MENU) return;
  let rows;
  if (NOTIF_ITEMS === null) {
    rows = '<p class="nm-empty">Log in to see your notifications</p>';
  } else if (NOTIF_ITEMS.length === 0) {
    rows = '<p class="nm-empty">You\'re all caught up</p>';
  } else {
    rows = NOTIF_ITEMS.map(n => {
      const time = n.time ? `<span class="nm-time">${notifTimeAgo(n.time)}</span>` : '';
      if (n.kind === 'friend_request') {
        return `<div class="nm-item nm-fr" data-id="${escapeHtml(n.id)}">
            ${notifAvatarHtml(n)}
            <div class="nm-body">
              <span class="nm-text">${escapeHtml(n.text)}</span>
              <div class="nm-actions">
                <button class="nm-accept" type="button" data-target="${n.targetId}">Accept</button>
                <button class="nm-decline" type="button" data-target="${n.targetId}">Decline</button>
              </div>
            </div>
          </div>`;
      }
      if (n.kind === 'support') {
        return `<a class="nm-item nm-support" data-id="${escapeHtml(n.id)}" href="${escapeHtml(n.link || '/support')}">
            <div class="nm-icon" aria-hidden="true">🎫</div>
            <div class="nm-body"><span class="nm-text">${escapeHtml(n.text)}</span>${time}</div>
            ${n.id === 'supq' ? '' : '<button class="nm-dismiss" type="button" aria-label="Dismiss">&times;</button>'}
          </a>`;
      }
      if (n.kind === 'launch') {
        return `<div class="nm-item nm-launch" data-id="${escapeHtml(n.id)}">
            <div class="nm-icon" aria-hidden="true">🎮</div>
            <div class="nm-body"><span class="nm-text">${escapeHtml(n.text)}</span>${time}</div>
            <button class="nm-dismiss" type="button" aria-label="Dismiss">&times;</button>
          </div>`;
      }
      return `<div class="nm-item" data-id="${escapeHtml(n.id)}">
          ${notifAvatarHtml(n)}
          <div class="nm-body"><span class="nm-text">${escapeHtml(n.text)}</span>${time}</div>
          <button class="nm-dismiss" type="button" aria-label="Dismiss">&times;</button>
        </div>`;
    }).join('');
  }
  const hasDismissable = Array.isArray(NOTIF_ITEMS) && NOTIF_ITEMS.some(n => n.kind !== 'friend_request');
  const clear = hasDismissable ? '<button class="nm-clear" type="button">Mark all read</button>' : '';
  NOTIF_MENU.innerHTML =
    `<div class="nm-head"><span>Notifications</span>${clear}</div>
     <div class="nm-list">${rows}</div>`;
  if (typeof refreshAvatars === 'function') refreshAvatars();
}

async function refreshNotifications() {
  await fetchNotifications();
  syncNotifBadge();
  renderNotifMenu();
  ensureNotifSocket();   // (re)connect the realtime channel once we know we're signed in
}

// ── realtime push ──────────────────────────────────────────────────────────
// Opens a websocket to /api/site/notifications/ws (cookie-authed, separate from the game's
// hub so it never disturbs the player's game socket). The server sends {"type":"refresh"}
// the instant a notification is created for this player; we just re-fetch the inbox. Falls
// back gracefully to the 30s poll if the socket can't connect.
let NOTIF_WS = null, NOTIF_WS_TIMER = null;
function ensureNotifSocket() {
  // Only hold a socket while signed in (NOTIF_ITEMS === null means signed out).
  if (NOTIF_ITEMS === null) { if (NOTIF_WS) { try { NOTIF_WS.close(); } catch {} NOTIF_WS = null; } return; }
  if (NOTIF_WS && (NOTIF_WS.readyState === 0 || NOTIF_WS.readyState === 1)) return; // connecting/open
  try {
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const ws = new WebSocket(proto + '//' + location.host + '/api/site/notifications/ws');
    NOTIF_WS = ws;
    ws.onopen = () => { siteWatch(SITE_WATCH); };
    ws.onmessage = (ev) => {
      // ignore keep-alive pings; refresh on a real "refresh" (or anything else)
      let m = {}; try { m = JSON.parse(ev.data) || {}; } catch {}
      const t = m.type || '';
      if (t === 'ping') return;
      // Typed frames about the profile this tab is watching (see siteWatch).
      if (t === 'spotify') {
        if (String(m.id) === String(SITE_WATCH_ID || '') || !SITE_WATCH_ID) {
          renderSpotify(document.getElementById('profileSpotify'), m.now_playing);
          renderSpotify(document.getElementById('spotifyNowPreview'), m.now_playing);
        }
        return;
      }
      if (t === 'profile') {
        if (String(m.id) === String(SITE_WATCH_ID || '')) {
          if (document.getElementById('profilePhotos') && typeof renderProfile === 'function') renderProfile();
          if (typeof renderSettingsStatus === 'function') renderSettingsStatus();
        }
        return;
      }
      {
        refreshNotifications();
        // Anything social changed (a friend came online, a request / favourite / subscription
        // moved): redraw what's on screen. Cheap - one GET each - and only while relevant.
        const fm = document.getElementById('friendsModal');
        if (fm && fm.classList.contains('open') && typeof fm.__reload === 'function') fm.__reload();
        const sm = document.getElementById('subsModal');
        if (sm && sm.classList.contains('open') && typeof sm.__reload === 'function') sm.__reload();
        if (document.getElementById('profilePhotos') && typeof renderProfile === 'function') renderProfile();
        if (typeof hydrateOnline === 'function') hydrateOnline();
      }
    };
    ws.onclose = () => {
      NOTIF_WS = null;
      clearTimeout(NOTIF_WS_TIMER);
      // reconnect only while still signed in
      NOTIF_WS_TIMER = setTimeout(() => { if (NOTIF_ITEMS !== null) ensureNotifSocket(); }, 5000);
    };
    ws.onerror = () => { try { ws.close(); } catch {} };
  } catch { NOTIF_WS = null; }
}

async function onNotifMenuClick(e) {
  const accept = e.target.closest('.nm-accept');
  const decline = e.target.closest('.nm-decline');
  const dismiss = e.target.closest('.nm-dismiss');
  const clear = e.target.closest('.nm-clear');

  if (accept || decline) {
    e.stopPropagation();
    if (!requireLogin('manage friend requests')) return;
    const btn = accept || decline;
    const target = Number(btn.dataset.target);
    btn.disabled = true;
    const path = accept ? '/api/site/friends/accept' : '/api/site/friends/remove';
    const { ok } = await apiPost(path, { id: target });
    if (!ok) { btn.disabled = false; toast('Something went wrong.'); return; }
    toast(accept ? 'Friend request accepted' : 'Friend request declined');
    await refreshNotifications();
    return;
  }
  if (dismiss) {
    e.stopPropagation();
    e.preventDefault();   // a support item is a link - dismissing it must not follow it
    const id = dismiss.closest('.nm-item').dataset.id;
    await apiPost('/api/site/notifications/dismiss', { id });
    await refreshNotifications();
    return;
  }
  if (clear) {
    e.stopPropagation();
    // One call: the server deletes every read-able notification and tells the game to drop them.
    const { ok } = await apiPost('/api/site/notifications/dismissall');
    if (!ok) {
      const ids = (NOTIF_ITEMS || []).filter(n => n.kind !== 'friend_request').map(n => n.id);
      for (const id of ids) await apiPost('/api/site/notifications/dismiss', { id });
    }
    await refreshNotifications();
  }
}

function initNotifications() {
  const btn = document.getElementById('notifBtn');
  if (!btn) return;
  NOTIF_BTN = btn;
  NOTIF_BADGE = document.getElementById('notifBadge');

  // wrap the bell so the menu can anchor to it
  const wrap = document.createElement('div');
  wrap.className = 'notif';
  btn.replaceWith(wrap);
  wrap.appendChild(btn);
  const menu = document.createElement('div');
  menu.className = 'notif-menu';
  menu.setAttribute('role', 'menu');
  wrap.appendChild(menu);
  NOTIF_WRAP = wrap; NOTIF_MENU = menu;

  btn.setAttribute('aria-haspopup', 'true');
  btn.setAttribute('aria-expanded', 'false');

  function toggleNotif(open) {
    document.querySelector('.profile')?.classList.remove('open');
    const isOpen = open ?? !wrap.classList.contains('open');
    wrap.classList.toggle('open', isOpen);
    btn.setAttribute('aria-expanded', isOpen ? 'true' : 'false');
    if (isOpen) refreshNotifications();   // refresh on open so it's always current
  }
  btn.addEventListener('click', e => { e.stopPropagation(); toggleNotif(); });
  btn.addEventListener('keydown', e => {
    if (e.key === 'ArrowDown') { e.preventDefault(); toggleNotif(true); }
  });
  menu.addEventListener('click', onNotifMenuClick);
  document.addEventListener('click', e => {
    if (!wrap.contains(e.target) && wrap.classList.contains('open')) toggleNotif(false);
  });

  renderNotifMenu();       // initial paint (loading/empty)
  refreshNotifications();  // initial fetch
  // Light polling keeps the badge in sync with the game while the tab is open.
  setInterval(() => { if (document.visibilityState === 'visible') refreshNotifications(); }, 30000);
}
initNotifications();

// ───── FRIENDS ─────
const FRIENDS_KEY = 'campus-friends';
function getFriends() { try { return JSON.parse(localStorage.getItem(FRIENDS_KEY) || '{}'); } catch { return {}; } }
function saveFriends(f) { localStorage.setItem(FRIENDS_KEY, JSON.stringify(f)); }
function isFriend(h) { const f = getFriends()[h]; return !!f && f.status !== 'pending'; }
function isPending(h) { const f = getFriends()[h]; return !!f && f.status === 'pending'; }
function addFriend(h) { const f = getFriends(); if (!f[h]) f[h] = { status: 'pending', nickname: '', pinned: false }; saveFriends(f); }
function acceptFriend(h) { const f = getFriends(); if (f[h]) { f[h].status = 'accepted'; saveFriends(f); } }
function removeFriend(h) { const f = getFriends(); delete f[h]; saveFriends(f); }
function setFriendNick(h, n) { const f = getFriends(); if (f[h]) { f[h].nickname = n; saveFriends(f); } }
function toggleFriendPin(h) { const f = getFriends(); if (f[h]) { f[h].pinned = !f[h].pinned; saveFriends(f); return f[h].pinned; } return false; }
function isFavourite(h) { const f = getFriends()[h]; return !!f && !!f.favourite; }
function toggleFriendFavourite(h) { const f = getFriends(); if (f[h]) { f[h].favourite = !f[h].favourite; saveFriends(f); return f[h].favourite; } return false; }

// pinned friends widget on the home right rail
async function renderPinned() {
  const rail = document.querySelector('.feed-layout .rail');
  if (!rail) return;
  rail.querySelector('.rail-pinned')?.remove();
  if (apiOn()) {
    if (!localStorage.getItem(USER_KEY)) return;
    const d = await apiGet('/api/site/me/pinned');
    const rows = (d && d.ok && d.pinned) || [];
    if (!rows.length) return;
    const items = rows.map(p => `<a class="rail-item" href="${userUrl(p.username)}">
      <span class="rail-on-av"><img class="rail-pin-av" data-av="${avKey(p.username)}" src="${p.profile_image || avatarUrl(p.username)}" alt="">${p.online ? '<i class="online-dot"></i>' : ''}</span>
      <div class="rail-meta"><span class="rail-name">${escapeHtml(p.nickname || p.display_name || p.username)}</span><span class="rail-by">${p.online ? (p.where ? 'In ^' + escapeHtml(p.where) : 'Online') : '@' + escapeHtml(p.username)}</span></div>
    </a>`).join('');
    const html = `<div class="rail-pinned"><h3 class="rail-title">Pinned friends</h3>${items}</div>`;
    rail.insertAdjacentHTML('afterbegin', html);
    return;
  }
  const f = getFriends();
  const pinned = Object.keys(f).filter(h => f[h].pinned);
  if (!pinned.length) return;
  const items = pinned.map(h => {
    const nick = f[h].nickname || cap(h);
    return `<a class="rail-item" href="${userUrl(h)}">
      <img class="rail-pin-av" data-av="${avKey(h)}" src="${avatarUrl(h)}" alt="">
      <div class="rail-meta"><span class="rail-name">${escapeHtml(nick)}</span><span class="rail-by">@${h}</span></div>
    </a>`;
  }).join('');
  rail.insertAdjacentHTML('afterbegin',
    `<div class="rail-pinned"><h3 class="rail-title">Pinned friends</h3>${items}</div>`);
}

// online friends widget on the home right rail
async function renderOnlineFriends() {
  const rail = document.querySelector('.feed-layout .rail');
  if (!rail) return;

  // Real "who's in-game right now" from /api/site/online; demo data as a fallback.
  let list = null;
  if (apiOn()) {
    const data = await apiGet('/api/site/online');
    if (data && data.ok && Array.isArray(data.results)) {
      list = data.results.map(o => ({ h: o.username, name: o.display_name || o.username, where: o.where || 'Online now', avatar: o.profile_image }));
    }
  }
  if (!list) {
    const f = getFriends();
    list = ((window.CAMPUS_DATA && window.CAMPUS_DATA.onlineFriends) || []).map(d => ({ h: d.h, name: (f[d.h] && f[d.h].nickname) || cap(d.h), where: d.where, avatar: null }));
    Object.keys(f).filter(h => f[h].status !== 'pending')
      .forEach(h => { if (!list.some(d => d.h === h)) list.unshift({ h, name: (f[h] && f[h].nickname) || cap(h), where: 'Online now', avatar: null }); });
  }

  rail.querySelector('.rail-online')?.remove();
  const items = list.map(d => {
    const av = d.avatar || avatarUrl(d.h);
    return `<a class="rail-item" href="${userUrl(d.h)}">
      <span class="rail-on-av"><img class="rail-pin-av" data-av="${avKey(d.h)}" src="${av}" alt=""><i class="online-dot"></i></span>
      <div class="rail-meta"><span class="rail-name">${escapeHtml(d.name)}</span><span class="rail-by">${escapeHtml(d.where)}</span></div>
    </a>`;
  }).join('');

  rail.insertAdjacentHTML('afterbegin',
    `<div class="rail-online"><h3 class="rail-title">Online Friends · ${list.length}</h3>${items}</div>`);
}

// ───── UPDATES PAGE (Patch Notes / Weekly / News) ─────
function renderUpdates() {
  const list = document.getElementById('updatesList');
  if (!list) return;
  const all = (window.CAMPUS_DATA && window.CAMPUS_DATA.updates) || {};
  // type comes from the page itself (data-updates) or the ?type= param
  let type = list.dataset.updates || new URLSearchParams(location.search).get('type') || 'patch';
  if (!all[type]) type = 'patch';
  const section = all[type] || { title: 'Updates', blurb: '', posts: [] };

  document.title = `${section.title} - Campus`;
  document.getElementById('updatesTitle').textContent = section.title;
  document.getElementById('updatesBlurb').textContent = section.blurb || '';
  document.querySelectorAll('.utab').forEach(t => t.classList.toggle('active', t.dataset.type === type));

  const fmtDate = d => {
    try { return new Date(d).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' }); }
    catch { return d; }
  };
  list.innerHTML = (section.posts || []).map(p => `
    <article class="update-card">
      <div class="update-meta">
        ${p.tag ? `<span class="update-tag">${escapeHtml(p.tag)}</span>` : ''}
        <span class="update-date">${escapeHtml(fmtDate(p.date))}</span>
      </div>
      <h2 class="update-title">${escapeHtml(p.title)}</h2>
      <p class="update-body">${escapeHtml(p.body)}</p>
    </article>
  `).join('') || '<p class="search-empty">Nothing here yet.</p>';
}
renderUpdates();

// ───── ROOM PAGE ─────
const ROOMS_DATA = (window.CAMPUS_DATA && window.CAMPUS_DATA.rooms) || {};

async function renderRoom() {
  const cover = document.getElementById('roomCover');
  if (!cover) return;
  const id = viewedRoomId();

  // Real room from /api/site/rooms/{id}; demo ROOMS_DATA only as a fallback.
  let r = null;
  if (apiOn() && id != null) {
    const data = await apiGet('/api/site/rooms/' + encodeURIComponent(id));
    if (data && data.ok && data.room) {
      const rm = data.room;
      r = { id: rm.id, name: rm.name, creator: rm.creator || 'someone', desc: rm.description || '', image: rm.image, visits: rm.visits || 0, cheers: rm.cheers || 0, favorites: rm.favorites || 0, players: rm.players || 0, photos: data.photos || [] };
    }
  }
  if (!r) r = ROOMS_DATA[id];
  if (!r) { document.getElementById('roomPage').innerHTML = '<p class="image-missing">Room not found.</p>'; return; }

  document.title = `^${r.name} - Campus`;
  if (r.image) { cover.style.backgroundImage = `url('${r.image}')`; cover.style.backgroundSize = 'cover'; cover.style.backgroundPosition = 'center'; }
  else cover.style.background = `linear-gradient(135deg, ${r.a || '#ff5a3c'}, ${r.b || '#7b2ff7'})`;
  document.getElementById('roomName').textContent = '^' + r.name;
  const by = document.getElementById('roomBy');
  by.textContent = 'by ' + r.creator;
  by.href = userUrl(r.creator);
  document.getElementById('roomDesc').textContent = r.desc || '';
  if (apiOn() && id != null) {
    let slot = document.getElementById('roomAdmin');
    if (!slot) { slot = document.createElement('div'); slot.id = 'roomAdmin'; slot.className = 'room-admin'; document.getElementById('roomDesc').after(slot); }
    mountAdminButton('room', r.id || id, slot);
  }
  const EYE = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M1.5 12S5.5 5 12 5s10.5 7 10.5 7-4 7-10.5 7S1.5 12 1.5 12z"/><circle cx="12" cy="12" r="3.2"/></svg>';
  const THUMB = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M7 10v11H3V10zM7 10l4.2-6.6a1.6 1.6 0 0 1 2.9 1.2L13.4 9H19a2 2 0 0 1 2 2.4l-1.5 7.5A2.5 2.5 0 0 1 17 21H7"/></svg>';
  const STAR = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linejoin="round"><path d="M12 2.5l2.9 6 6.6.9-4.8 4.6 1.2 6.5L12 17.4 6.1 20.5l1.2-6.5L2.5 9.4l6.6-.9z"/></svg>';
  document.getElementById('roomStats').innerHTML =
    `<div class="room-stat">${EYE}<strong>${fmt(r.visits || 0)}</strong><small>visits</small></div>
     <div class="room-stat">${THUMB}<strong>${fmt(r.cheers || 0)}</strong><small>cheers</small></div>
     <div class="room-stat">${STAR}<strong>${fmt(r.favorites || 0)}</strong><small>favorites</small></div>`;

  // the room's photos, as the same cards the home feed uses (cheers / comments work here too)
  const feed = document.getElementById('roomPhotos');
  if (feed) {
    const posts = (r.photos || []).map(x => ({
      id: x.id, image: x.image,
      user: (x.author && (x.author.display_name || x.author.username)) || 'Player',
      handle: (x.author && x.author.username) || 'player',
      avatar: x.author && x.author.profile_image,
      caption: x.description || '', room: r.name,
      cheers: x.cheers || 0, cheered: !!x.cheered, comments: x.comments || 0, takenAt: x.createdAt,
      tagged: x.taggedPlayers || [],
    }));
    if (!posts.length) feed.innerHTML = '<div class="feed-loading">No photos taken in this room yet.</div>';
    else {
      const keep = postList; // renderPosts writes into the home feed element; borrow it for this page
      feed.innerHTML = posts.map(p => {
        const [a, b] = avColor(p.user || '?');
        const tags = (p.tagged || []).map(t => `<a class="room-tag" href="${userUrl(t.username)}"><img src="${t.profile_image || avatarUrl(t.username)}" alt="">${escapeHtml(t.display_name || t.username)}</a>`).join('');
        return `<article class="post" data-photo="${p.id}">
          <div class="post-head">
            <a class="post-av" href="${userUrl(p.handle)}" style="--a:${a};--b:${b}"><img data-av="${avKey(p.handle)}" src="${p.avatar || avatarUrl(p.handle)}" alt="" onerror="this.remove()">${(p.user || '?')[0].toUpperCase()}</a>
            <div class="post-meta"><a class="post-user" href="${userUrl(p.handle)}">${escapeHtml(p.user)}</a><span class="post-handle">@${escapeHtml(p.handle)}</span></div>
            <span class="post-time">${p.takenAt ? timeAgo(p.takenAt) : ''}</span>
          </div>
          <a class="post-img" href="${imageUrl(p.id)}" style="background-image:url('${p.image}')"></a>
          <div class="post-foot">
            <span class="post-cap"><span class="room-chip">^${escapeHtml(p.room)}</span>${tags ? `<span class="room-in">In this photo:</span>${tags}` : ''}${p.caption ? ` <span class="room-caption">${escapeHtml(p.caption)}</span>` : ''}</span>
            <div class="post-stats">
              <button class="pstat cheer${p.cheered ? ' liked' : ''}" data-n="${(p.cheers || 0) - (p.cheered ? 1 : 0)}">${CHEER_ICON}<span>${fmt(p.cheers || 0)}</span></button>
              <button class="pstat comment-toggle" data-n="${p.comments || 0}">${CHAT}<span>${p.comments || 0}</span></button>
            </div>
          </div>
          ${commentsBlock(p)}
        </article>`;
      }).join('');
    }
  }
  // the room's events (hosted by players, booked in this room)
  const roomEventsEl = document.querySelector('.room-panel[data-panel="events"]');
  if (roomEventsEl && apiOn() && r.id != null) {
    roomEventsEl.innerHTML = '<div class="room-events profile-events" id="roomEvents"></div>';
    loadEvents(document.getElementById('roomEvents'), '/api/site/rooms/' + encodeURIComponent(r.id) + '/events', { showHost: true }, 'No events scheduled in this room.');
  }
  // tabs
  document.querySelectorAll('.room-tab').forEach(t => t.addEventListener('click', () => {
    document.querySelectorAll('.room-tab').forEach(x => x.classList.toggle('active', x === t));
    document.querySelectorAll('.room-panel').forEach(p => p.classList.toggle('active', p.dataset.panel === t.dataset.panel));
  }));
}
renderRoom();

// ───── EVENT PAGE (/event/{id}) - laid out like the photo page ─────
async function renderEventPage() {
  const root = document.getElementById('eventPage');
  if (!root) return;
  const id = routeSlug('event');
  let data = null;
  try { data = await apiGet('/api/site/events/' + encodeURIComponent(id)); } catch { data = null; }
  if (!data || !data.ok || !data.event) { root.innerHTML = '<p class="image-missing">This event does not exist, or is private.</p>'; return; }
  await SESSION_READY;
  let e = data.event, going = data.going_players || [];
  document.title = e.name + ' - Campus';
  const $ = x => document.getElementById(x);
  const pad = n => String(n).padStart(2, '0');
  const icsStamp = d => d.getUTCFullYear() + pad(d.getUTCMonth() + 1) + pad(d.getUTCDate()) + 'T' + pad(d.getUTCHours()) + pad(d.getUTCMinutes()) + pad(d.getUTCSeconds()) + 'Z';

  // the "..." menu: copy link / add to calendar (wired once)
  const menu = $('evMenu'), menuBtn = $('evMenuBtn');
  menuBtn.addEventListener('click', ev => { ev.stopPropagation(); menu.classList.toggle('open'); });
  document.addEventListener('click', () => menu.classList.remove('open'));
  $('evShare').onclick = async () => {
    menu.classList.remove('open');
    try { await navigator.clipboard.writeText(location.origin + '/event/' + e.id); toast('Link copied'); }
    catch { prompt('Copy this link', location.origin + '/event/' + e.id); }
  };
  $('evCal').onclick = () => {
    menu.classList.remove('open');
    const s = new Date(e.start), en = new Date(e.end);
    const ics = ['BEGIN:VCALENDAR', 'VERSION:2.0', 'PRODID:-//Campus//Events//EN', 'BEGIN:VEVENT',
      'UID:event-' + e.id + '@campus', 'DTSTAMP:' + icsStamp(new Date()), 'DTSTART:' + icsStamp(s), 'DTEND:' + icsStamp(en),
      'SUMMARY:' + String(e.name).replace(/[\r\n]+/g, ' '),
      'DESCRIPTION:' + ((e.description || '') + (e.room ? '\\nIn ^' + e.room.name : '') + '\\n' + location.origin + '/event/' + e.id).replace(/[\r\n]+/g, '\\n'),
      'URL:' + location.origin + '/event/' + e.id, 'END:VEVENT', 'END:VCALENDAR'].join('\r\n');
    const a = document.createElement('a');
    a.href = URL.createObjectURL(new Blob([ics], { type: 'text/calendar' }));
    a.download = 'campus-event-' + e.id + '.ics';
    document.body.appendChild(a); a.click(); a.remove();
  };

  // JOIN: if you are online in the game, the server sends your game to the event
  const join = $('evJoin');
  join.addEventListener('click', async () => {
    if (!CAMPUS_ME) { location.href = '/login'; return; }
    join.disabled = true; const label = join.textContent; join.textContent = 'Joining\u2026';
    const r = await apiPost('/api/site/events/' + encodeURIComponent(e.id) + '/join', {});
    join.textContent = label; join.disabled = false;
    toast(r && r.ok ? ((r.data && r.data.message) || 'Sending you to the event\u2026') : ((r && r.data && r.data.error) || 'Could not join the event'));
  });

  const draw = () => {
    const s = new Date(e.start);
    // the picture (photo-page style), with the status chip on it
    $('evCover').style.backgroundImage = e.image ? `url('${e.image}')` : 'linear-gradient(135deg,#ff5a3c,#7b2ff7)';
    $('evBadge').innerHTML = e.status === 'live' ? '<span class="pevent-live">LIVE NOW</span>' : e.status === 'ended' ? '<span class="pevent-tag">ENDED</span>' : '';
    // who is hosting
    const host = $('evHost');
    if (e.host) {
      host.hidden = false; host.href = userUrl(e.host.username);
      const av = $('evHostAv'); av.src = e.host.profile_image || avatarUrl(e.host.username); av.onerror = () => { av.style.visibility = 'hidden'; };
      $('evHostName').textContent = e.host.display_name || e.host.username;
      $('evHostHandle').textContent = '@' + e.host.username;
    } else host.hidden = true;
    // title + one meta line: when, how many are going, where
    $('evName').textContent = e.name;
    const meta = $('evMeta');
    meta.innerHTML = `<span>${escapeHtml(eventWhen(e))}</span><span>\u00b7</span><span>${e.going || 0} attending${e.interested ? ', ' + e.interested + ' interested' : ''}</span>`
      + (e.room ? `<span>\u00b7</span><a class="image-room" href="${roomUrl(e.room.id)}">^${escapeHtml(e.room.name)}</a>` : '');

    // Join: only while you could actually go in; a signed-out visitor is sent to log in
    join.hidden = e.status === 'ended';
    const canGo = !!(CAMPUS_ME && e.canJoin);
    join.classList.toggle('ev-join-off', !!CAMPUS_ME && !canGo);
    join.title = !CAMPUS_ME ? 'Log in to join' : canGo ? 'Send me to this event (you need to be online in Campus)' : 'Opens when the event starts';
    join.disabled = !!CAMPUS_ME && !canGo;

    // RSVP pills in the photo page's button style
    const rsvp = $('evRsvp');
    if (e.status === 'ended' || !CAMPUS_ME) rsvp.innerHTML = '';
    else {
      const b = (r, label) => `<button type="button" class="img-report${e.myResponse === r ? ' on' : ''}" data-r="${r}">${label}</button>`;
      rsvp.innerHTML = b('going', 'Going') + b('interested', 'Interested') + b('not_interested', 'Not interested');
      rsvp.querySelectorAll('button').forEach(btn => btn.addEventListener('click', async () => {
        rsvp.querySelectorAll('button').forEach(x => x.disabled = true);
        const r = await apiPost('/api/site/events/' + encodeURIComponent(e.id) + '/respond', { response: e.myResponse === btn.dataset.r ? 'none' : btn.dataset.r });
        if (r && r.ok && r.data && r.data.event) {
          e = r.data.event;
          let again = null; try { again = await apiGet('/api/site/events/' + encodeURIComponent(e.id)); } catch {}
          if (again && again.going_players) going = again.going_players;
          draw();
        } else { rsvp.querySelectorAll('button').forEach(x => x.disabled = false); toast((r && r.data && r.data.error) || 'Could not save your answer'); }
      }));
    }

    // below: about + who is going (the photo page's "Comments" section look)
    const about = $('evAbout');
    if (e.description && e.description.trim() && e.description.trim().toLowerCase() !== String(e.name).trim().toLowerCase()) { $('evDesc').textContent = e.description; about.hidden = false; } else about.hidden = true;
    $('evGoingTitle').textContent = going.length ? `Attending \u00b7 ${e.going || going.length}` : 'Attending';
    $('evPeople').innerHTML = going.length
      ? going.map(p => `<a class="ev-person" href="${userUrl(p.username)}"><img src="${escapeHtml(p.profile_image || avatarUrl(p.username))}" alt="" onerror="this.style.visibility='hidden'"><span>${escapeHtml(p.display_name || p.username)}</span></a>`).join('')
      : '<p class="profile-empty">No one has said they are going yet.</p>';
  };
  draw();
}
renderEventPage();

// featured-rooms slideshow on the home page.
// Data-driven: called once with the demo rooms at load, then again with the real rooms
// once /api/site/rooms/hot answers. The previous instance's timer is cleared on re-init,
// otherwise the two would fight over the same element.
let FEATURED_TIMER = null;
function initFeaturedSlideshow(rooms) {
  const el = document.getElementById('featuredSlideshow');
  if (!el) return;

  if (FEATURED_TIMER) { clearInterval(FEATURED_TIMER); FEATURED_TIMER = null; }

  // No argument = the original demo path, keyed into ROOMS_DATA by slug.
  const list = (rooms && rooms.length)
    ? rooms
    : ['whisker-heights', 'midnight-mews', 'yarn-quest', 'haunted-cattery', 'deep-dive-reef']
        .map(id => Object.assign({ id }, ROOMS_DATA[id] || {}));

  if (!list.length) return;
  const ids = list.map(r => r.id);
  let i = 0, timer = null;

  el.innerHTML = `
    <a class="featured" id="featuredSlide" href="#"></a>
    <button class="farrow farrow-prev" aria-label="Previous room"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M15 6l-6 6 6 6"/></svg></button>
    <button class="farrow farrow-next" aria-label="Next room"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M9 6l6 6-6 6"/></svg></button>
    <div class="featured-dots" id="featuredDots"></div>`;
  const slide = document.getElementById('featuredSlide');
  const dots = document.getElementById('featuredDots');
  dots.innerHTML = ids.map((_, idx) => `<button class="fdot" data-i="${idx}" aria-label="Slide ${idx + 1}"></button>`).join('');

  function render() {
    const r = list[i];
    slide.href = roomUrl(ids[i]);
    // A real room has a server image; fall back to its gradient when it has none.
    const bg = r.image
      ? `background-image:url('${r.image}');background-size:cover;background-position:center`
      : `background:linear-gradient(135deg, ${r.a || '#ff5a3c'}, ${r.b || '#7b2ff7'})`;
    slide.innerHTML = `
      <div class="featured-img" style="${bg}"></div>
      <div class="featured-body">
        <span class="featured-tag">Featured room</span>
        <h2>^${r.name || ''}</h2>
        <p class="featured-by">by @${r.creator || 'someone'}</p>
        <div class="featured-row">
          <span class="featured-live"><i class="dot"></i>${(Number(r.players) || 0).toLocaleString()} playing now</span>
          <span class="featured-cta">Visit room →</span>
        </div>
      </div>`;
    slide.classList.remove('fade'); void slide.offsetWidth; slide.classList.add('fade');
    dots.querySelectorAll('.fdot').forEach((d, idx) => d.classList.toggle('active', idx === i));
  }
  function start() { timer = setInterval(() => { i = (i + 1) % ids.length; render(); }, 5000); FEATURED_TIMER = timer; }
  function stop() { clearInterval(timer); }

  render();
  start();
  function go(delta) { i = (i + delta + ids.length) % ids.length; render(); stop(); start(); }
  dots.addEventListener('click', e => {
    const b = e.target.closest('.fdot');
    if (!b) return;
    i = +b.dataset.i; render(); stop(); start();
  });
  el.querySelector('.farrow-prev').addEventListener('click', () => go(-1));
  el.querySelector('.farrow-next').addEventListener('click', () => go(1));
  el.addEventListener('mouseenter', stop);
  el.addEventListener('mouseleave', start);
}

// ───── PROFILE PAGE ─────
const PROFILE_PHOTOS = [
  { id: 'a', pos: '8% 45%',  size: '230%' },
  { id: 'b', pos: '40% 50%', size: '250%' },
  { id: 'c', pos: '70% 48%', size: '240%' },
  { id: 'd', pos: '95% 50%', size: '260%' },
  { id: 'e', pos: '20% 60%', size: '220%' },
  { id: 'f', pos: '55% 40%', size: '270%' },
];

function cap(s) { return s ? s[0].toUpperCase() + s.slice(1) : s; }

// per-user photo galleries (random generated images via Lorem Picsum)
const USER_PHOTOS = {
  catlover: [
    { id: 'g1', image: 'https://picsum.photos/seed/catlover-aurora/700/700', title: 'Caught a nice view' },
    { id: 'g2', image: 'https://picsum.photos/seed/catlover-sunset/700/700', title: 'Late night session' },
  ],
};

// per-user profile stats
const PROFILE_DATA = {
  catlover:    { friends: 16,  subscribers: 2 },
  bubbles:     { friends: 48,  subscribers: 312 },
  spookytom:   { friends: 27,  subscribers: 1530 },
  mittens:     { friends: 12,  subscribers: 89 },
  clawful:     { friends: 64,  subscribers: 12400 },
  velvetpaw:   { friends: 41,  subscribers: 4800 },
  tinker9:     { friends: 33,  subscribers: 760 },
  stitchwitch: { friends: 58,  subscribers: 9100 },
  pounce:      { friends: 9,   subscribers: 34 },
};

async function renderProfile() {
  const photosEl = document.getElementById('profilePhotos');
  if (!photosEl) return;

  const me = localStorage.getItem(USER_KEY);
  const viewed = (viewedHandle() || me || 'catlover').toLowerCase();
  const isOwn = !!me && me.toLowerCase() === viewed;
  siteWatch(viewed);

  // Real profile from the Campus server: api.user (profile_image / display_name /
  // username / bio / banner_image), api.friends, api.subscribers, api.photos, api.rooms.
  // Falls back to demo data only when the API is off or the player isn't found.
  let api = null;
  if (apiOn()) {
    // Direct fetch (not apiGet) so we can read a 403 body: if the owner has blocked this viewer
    // the server answers { blocked: true } and we show the notice instead of the profile.
    try {
      const resp = await fetch(apiUrl('/api/site/profile/' + encodeURIComponent(viewed)), { credentials: 'same-origin' });
      const data = await resp.json().catch(() => null);
      if (data && data.blocked) {
        const main = document.getElementById('main') || document.querySelector('.profile-page');
        if (main) main.innerHTML =
          `<div class="profile-blocked" style="max-width:540px;margin:120px auto;text-align:center;padding:0 20px">
             <h1 style="font-size:1.4rem;margin-bottom:10px">This Profile cannot be viewed at this time</h1>
             <p style="color:var(--muted)"><a href="/" style="color:var(--primary)">Back to home</a></p>
           </div>`;
        document.title = 'Profile unavailable - Campus';
        return;
      }
      if (resp.ok && data && data.ok) api = data;
    } catch {}
  }
  const u = api && api.user;
  if (u && u.id) siteWatch(viewed, u.id);
  const data = api
    ? { friends: api.friends || 0, subscribers: api.subscribers || 0 }
    : (PROFILE_DATA[viewed] || { friends: 8, subscribers: 24 });

  const displayName = u ? (u.display_name || u.username) : (isOwn ? myDisplayName(viewed) : cap(viewed));
  const handleText = u ? u.username : (isOwn ? myHandle(viewed) : viewed);

  document.title = `${displayName} - Campus`;
  const picEl = document.getElementById('profilePic');
  picEl.src = (u && u.profile_image) ? u.profile_image : `https://api.dicebear.com/9.x/thumbs/svg?seed=${viewed}`;
  picEl.dataset.av = avKey(viewed);
  const unameEl = document.getElementById('profileUsername');
  unameEl.textContent = displayName;
  if (!isOwn && isFavourite(viewed)) {
    unameEl.insertAdjacentHTML('beforeend', ' <span class="fav-star" title="Favourite" aria-label="Favourite">★</span>');
  }
  document.getElementById('profileHandle').textContent = '@' + handleText;
  // "You call them X" - the viewer's nickname for this friend, if they set one
  function renderNickChip() {
    document.getElementById('profileNick')?.remove();
    const n = api ? api.nickname : ((getFriends()[viewed] || {}).nickname || '');
    if (!n) return;
    document.getElementById('profileHandle').insertAdjacentHTML('afterend', `<span class="profile-nick" id="profileNick" title="Your nickname for them">aka ${escapeHtml(n)}</span>`);
  }
  renderNickChip();
  const bioEl = document.getElementById('profileBio');
  if (bioEl) {
    const myBio = isOwn ? localStorage.getItem('campus-mybio') : null;
    bioEl.textContent = myBio || (u && u.bio) || (isOwn ? 'No bio yet — add one in Settings.' : '');
  }
  renderSocials(document.getElementById('profileSocials'), u && u.socials);

  // Real joined date from the account's created_at (was hardcoded "Joined June 2025").
  const joinedText = document.querySelector('#profileJoined .joined-text');
  if (joinedText) {
    let jd = '';
    if (u && u.created_at) { const d = new Date(u.created_at); if (!isNaN(d)) jd = d.toLocaleDateString(undefined, { month: 'long', year: 'numeric' }); }
    joinedText.textContent = jd ? ('Joined ' + jd) : 'Joined';
  }

  // Status: shown ONLY when the server disclosed it, which it does only for a signed-in viewer.
  // Signed out -> api.status is null -> the whole line stays hidden (you can't see anyone's status).
  const statusEl = document.getElementById('profileStatus');
  if (statusEl) {
    const st = api && api.status;
    if (st) {
      statusEl.hidden = false;
      const dot = statusEl.querySelector('.status-dot');
      const txt = statusEl.querySelector('.status-text');
      if (dot) dot.style.background = st.online ? '#2ecc71' : '#8a8f98';
      if (txt) txt.textContent = st.online ? ('Online' + (st.where ? ' · ' + st.where : '')) : ('Offline' + (st.lastOnline ? ' · last online ' + lastOnlineText(st.lastOnline) : ''));
    } else {
      statusEl.hidden = true;
    }
    // Spotify "listening to" under the status - only what the server disclosed for THIS viewer
    // (the owner's "show to" choice). Live updates arrive over the site socket; this poll is
    // only the fallback for a tab whose socket is down.
    const spEl = document.getElementById('profileSpotify');
    if (spEl) {
      renderSpotify(spEl, st ? st.spotify : null);
      clearInterval(spEl._poll);
      if (st && u && (u.username || u.id)) {
        const handle = u.username || String(u.id);
        spEl._poll = setInterval(async () => {
          if (document.hidden) return;
          try {
            const r = await fetch('/api/site/spotify/nowplaying/' + encodeURIComponent(handle), { credentials: 'same-origin' });
            if (r.ok) { const j = await r.json(); renderSpotify(spEl, j.now_playing); }
          } catch {}
        }, 60000);
      }
    }
  }

  // Real banner from the server, if the account has one.
  {
    const b = document.querySelector('.profile-banner');
    const src = (u && u.banner_image) || 'assets/banner.png';
    if (b) { b.style.backgroundImage = `url('${src}')`; b.style.backgroundSize = 'cover'; b.style.backgroundPosition = 'center'; b.style.backgroundRepeat = 'no-repeat'; }
  }

  // custom profile picture / banner (set from a photo). Only used as a local override when
  // the server has no image of its own, so a real server picture always wins.
  try {
    const pfp = JSON.parse(localStorage.getItem('campus-pfp-' + viewed) || 'null');
    if (pfp && !(u && u.profile_image)) applyCropToImg(picEl, pfp);
  } catch {}
  try {
    const bn = null; // banners are the site-wide banner.png now
    if (bn && !(u && u.banner_image)) {
      const b = document.querySelector('.profile-banner');
      b.style.backgroundImage = `url('${bn.image}')`;
      b.style.backgroundSize = 'cover';
      b.style.backgroundPosition = bn.pos;
      b.style.backgroundRepeat = 'no-repeat';
      if (bn.id) {
        b.style.cursor = 'pointer';
        b.title = 'View photo';
        b.setAttribute('role', 'button');
        b.setAttribute('tabindex', '0');
        b.setAttribute('aria-label', 'View banner photo');
        const goPhoto = () => { window.location.href = imageUrl(bn.id); };
        b.addEventListener('click', goPhoto);
        b.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); goPhoto(); } });
      }
    }
  } catch {}

  // stats box
  const subKey = 'campus-sub-' + viewed;
  let subscribed = api ? !!api.subscribed : localStorage.getItem(subKey) === '1';
  const statFriends = document.getElementById('statFriends');
  const statSubs = document.getElementById('statSubs');
  if (statFriends) statFriends.textContent = fmt(data.friends);
  // With the API the count already includes the viewer's own subscription.
  const subCount = () => api ? data.subscribers : data.subscribers + (subscribed ? 1 : 0);
  if (statSubs) statSubs.textContent = fmt(subCount());

  // top action: gear (own) or Add-friend (others)
  const actions = document.getElementById('profileActions');
  // dev accounts: the quick-admin button, after whatever buttons this branch adds
  setTimeout(() => mountAdminButton('player', (u && u.id) || viewed, actions), 0);
  if (isOwn) {
    actions.innerHTML = `<a href="/stats" class="profile-gear" title="Stats" aria-label="Stats" style="margin-right:6px;"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><path d="M4 19V10"/><path d="M10 19V4"/><path d="M16 19v-7"/><path d="M22 19H2"/></svg></a><a href="/settings" class="profile-gear" title="Settings" aria-label="Settings"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/></svg></a>`;
    // clicking your own "Friends" stat opens the friends list
    const friendRow = document.getElementById('statFriendsRow');
    if (friendRow) {
      friendRow.classList.add('statbox-clickable');
      friendRow.setAttribute('role', 'button');
      friendRow.tabIndex = 0;
      // renderProfile re-runs on every live refresh: replace the badge, never stack another.
      friendRow.querySelectorAll('.statbox-badge').forEach(b => b.remove());
      const pendingN = (NOTIF_ITEMS || []).filter(n => n.kind === 'friend_request').length;
      if (pendingN) friendRow.insertAdjacentHTML('beforeend', ` <span class="statbox-badge">${pendingN}</span>`);
      if (!friendRow.dataset.wired) {
        friendRow.dataset.wired = '1';
        friendRow.addEventListener('click', () => openFriendsList(viewed));
        friendRow.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openFriendsList(viewed); } });
      }
    }
  } else {
    actions.innerHTML = '';
    const rbtn = document.createElement('button');
    rbtn.className = 'profile-report-btn';
    rbtn.textContent = 'Report';
    rbtn.addEventListener('click', () => openReport(`Report ${cap(viewed)}`, () => {
      rbtn.textContent = 'Reported';
      rbtn.disabled = true;
    }, { type: 'player', id: viewed }));
    actions.appendChild(rbtn);

    // Block / Unblock (server-backed, the same block as in-game). Only YOUR block is yours to
    // lift; if they've also blocked you, that stays when you unblock.
    if (api && u && u.id) {
      const bbtn = document.createElement('button');
      bbtn.className = 'profile-report-btn profile-block-btn';
      let blocked = !!api.blockedByMe;
      const paint = () => { bbtn.textContent = blocked ? 'Unblock' : 'Block'; bbtn.classList.toggle('on', blocked); };
      paint();
      bbtn.addEventListener('click', () => {
        if (!requireLogin('block players')) return;
        const go = async () => {
          bbtn.disabled = true;
          const { ok, data } = await apiPost('/api/site/friends/block', { id: u.id, on: !blocked });
          bbtn.disabled = false;
          if (!ok) { toast((data && data.error) || 'Could not update the block'); return; }
          blocked = !!data.blocked; api.blockedByMe = blocked; paint();
          toast(blocked ? `${escapeHtml(displayName)} blocked` : `${escapeHtml(displayName)} unblocked`);
          if (blocked) setTimeout(() => renderProfile(), 300);   // friendship ends with a block; repaint the friend controls
        };
        if (blocked) go();
        else openConfirm({ title: `Block ${escapeHtml(displayName)}?`, message: 'They won\'t be able to friend, message or join you, and any friendship ends. You can unblock them here later.', confirmText: 'Block', onConfirm: go });
      });
      actions.appendChild(bbtn);
    }

    // friend control replaces the "Friends" row in the stats box
    const friendRow = document.getElementById('statFriendsRow');
    if (friendRow) {
      friendRow.classList.add('statbox-friend');
      const fbtn = document.createElement('button');
      const targetId = u && u.id;
      // REAL relationship state from the server: None | FriendRequestSent | FriendRequestReceived
      // | Friend. Falls back to the old local flags only when there's no API profile.
      let rel = (api && api.relationship) || (isFriend(viewed) ? 'Friend' : (isPending(viewed) ? 'FriendRequestSent' : 'None'));
      const setFbtn = () => {
        if (rel === 'FriendRequestSent') { fbtn.className = 'friend-row-btn pending'; fbtn.textContent = 'Pending friend request'; }
        else if (rel === 'FriendRequestReceived') { fbtn.className = 'friend-row-btn'; fbtn.textContent = 'Accept friend request'; }
        else if (rel === 'Friend') { fbtn.className = 'friend-row-btn is-friend'; fbtn.textContent = 'Friends'; }
        else { fbtn.className = 'friend-row-btn'; fbtn.textContent = 'Add friend'; }
      };
      setFbtn();
      // Hit the real site friend endpoints (they run the same FriendsDB ops the game uses, so a
      // request sent here shows up in-game). action is request|accept|remove.
      async function act(action) {
        if (!targetId) { toast('Could not identify this player.'); return; }
        fbtn.disabled = true;
        const { ok, data } = await apiPost('/api/site/friends/' + action, { id: targetId });
        fbtn.disabled = false;
        if (!ok) { toast((data && data.error) || 'Something went wrong.'); return; }
        rel = data.relationship || (action === 'request' ? 'FriendRequestSent' : action === 'accept' ? 'Friend' : 'None');
        setFbtn();
        toast(action === 'request' ? `Friend request sent to ${escapeHtml(displayName)}`
            : action === 'accept' ? `You and ${escapeHtml(displayName)} are now friends`
            : 'Friendship updated');
      }
      fbtn.addEventListener('click', () => {
        if (!requireLogin('send friend requests')) return;
        if (rel === 'FriendRequestSent') {
          openConfirm({ title: 'Cancel friend request?', message: `Cancel your friend request to ${escapeHtml(displayName)}?`, confirmText: 'Cancel request', onConfirm: () => act('remove') });
        } else if (rel === 'Friend') {
          openConfirm({ title: 'Remove friend?', message: `Remove ${escapeHtml(displayName)} from your friends?`, confirmText: 'Remove friend', onConfirm: () => act('remove') });
        } else if (rel === 'FriendRequestReceived') {
          act('accept');
        } else {
          act('request');
        }
      });
      friendRow.innerHTML = '';
      friendRow.appendChild(fbtn);
    }
  }

  // friend options (nickname + pin) - others, only when friended
  // Anyone's "Friends" count opens their list (accepted friends only for others).
  if (!isOwn) {
    const fr = document.getElementById('statFriendsRow');
    if (fr) {
      const n = fr.querySelector('strong');
      if (n && !n.dataset.wired) { n.dataset.wired = '1'; n.style.cursor = 'pointer'; n.title = 'See friends'; n.addEventListener('click', e => { e.stopPropagation(); openFriendsList(viewed); }); }
    }
  }

  function renderFriendPanel() {
    const fp = document.getElementById('friendPanel');
    if (!fp) return;
    const serverFriend = api ? api.relationship === 'Friend' : isFriend(viewed);
    if (isOwn || !serverFriend) { fp.innerHTML = ''; return; }
    const d = Object.assign({ nickname: '', pinned: false }, getFriends()[viewed] || {});
    if (api) { d.favourite = !!api.favourite; d.nickname = api.nickname || ''; d.pinned = !!api.pinned; }
    const starSvg = '<svg viewBox="0 0 24 24" width="16" height="16" fill="currentColor" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round"><path d="M12 3.5l2.6 5.3 5.9.9-4.2 4.1 1 5.8L12 17l-5.3 2.8 1-5.8-4.2-4.1 5.9-.9z"/></svg>';
    fp.innerHTML = `
      <span class="fo-title">Friend options</span>
      <button class="fo-fav${d.favourite ? ' on' : ''}">${starSvg}<span>${d.favourite ? 'Favourited' : 'Favourite'}</span></button>
      <button class="fo-nick-btn">${d.nickname ? 'Edit nickname' : 'Set nickname'}</button>
      <button class="fo-pin${d.pinned ? ' on' : ''}">${d.pinned ? 'Pinned to home' : 'Pin to home'}</button>`;
    fp.querySelector('.fo-fav').addEventListener('click', async e => {
      const btn = e.currentTarget;
      let fav;
      if (api && u && u.id) {
        const want = !btn.classList.contains('on');
        const { ok, data } = await apiPost('/api/site/friends/favourite', { id: u.id, on: want });
        if (!ok) { toast('Could not update favourites'); return; }
        fav = !!data.favourite;
      } else {
        fav = toggleFriendFavourite(viewed);
      }
      btn.classList.toggle('on', fav);
      btn.querySelector('span').textContent = fav ? 'Favourited' : 'Favourite';
      toast(fav ? `Added ${cap(viewed)} to favourites` : `Removed ${cap(viewed)} from favourites`);
    });
    fp.querySelector('.fo-nick-btn').addEventListener('click', () => openNickname(viewed, () => { renderFriendPanel(); renderNickChip(); },
      api && u && u.id ? { id: u.id, current: api.nickname || '', onSaved: n => { api.nickname = n || null; } } : null));
    fp.querySelector('.fo-pin').addEventListener('click', async e => {
      const btn = e.currentTarget;
      let pinned;
      if (api && u && u.id) {
        const want = !btn.classList.contains('on');
        const { ok, data } = await apiPost('/api/site/friends/pin', { id: u.id, on: want });
        if (!ok) { toast((data && data.error) || 'Could not update pins'); return; }
        pinned = !!data.pinned; api.pinned = pinned;
      } else {
        pinned = toggleFriendPin(viewed);
      }
      btn.classList.toggle('on', pinned);
      btn.textContent = pinned ? 'Pinned to home' : 'Pin to home';
      toast(pinned ? `${cap(viewed)} pinned to your home page` : `${cap(viewed)} unpinned`);
    });
  }
  renderFriendPanel();

  // "Subscribers" row opens the list of people subscribed to this profile (anyone's).
  const subsRow = document.getElementById('statSubsRow');
  if (subsRow && !subsRow.dataset.wired) {
    subsRow.dataset.wired = '1';
    subsRow.classList.add('statbox-clickable');
    subsRow.setAttribute('role', 'button');
    subsRow.tabIndex = 0;
    subsRow.addEventListener('click', () => openSubsList(viewed, 'subscribers'));
    subsRow.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openSubsList(viewed, 'subscribers'); } });
  }

  // third stat row: "Subscriptions" (own - opens who you subscribe to) or a Subscribe button (others)
  const subWrap = document.getElementById('profileSubscribe');
  if (subWrap) {
    if (isOwn) {
      // Same markup as before (so it keeps the row's own styling); the ROW is the button.
      subWrap.innerHTML = '<span class="sub-text">Subscriptions</span>';
      if (!subWrap.dataset.wired) {
        subWrap.dataset.wired = '1';
        subWrap.style.cursor = 'pointer';
        subWrap.setAttribute('role', 'button');
        subWrap.tabIndex = 0;
        subWrap.title = 'See who you subscribe to';
        subWrap.addEventListener('click', () => openSubsList(viewed, 'subscriptions'));
        subWrap.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openSubsList(viewed, 'subscriptions'); } });
      }
    } else {
      const btn = document.createElement('button');
      btn.className = 'subscribe-btn' + (subscribed ? ' subscribed' : '');
      btn.textContent = subscribed ? 'Subscribed' : 'Subscribe';
      btn.addEventListener('click', async () => {
        if (!requireLogin('subscribe')) return;
        if (!rateLimit('subscribe', 12, 60000)) { toast('You\'re doing that too fast.'); return; }
        if (api && u && u.id) {
          const { ok, data: r } = await apiPost('/api/site/subscribe', { id: u.id, on: !subscribed });
          if (!ok) { toast((r && r.error) || 'Could not update the subscription'); return; }
          subscribed = !!r.subscribed;
          data.subscribers = typeof r.subscribers === 'number' ? r.subscribers : data.subscribers;
        } else {
          subscribed = !subscribed;
          localStorage.setItem(subKey, subscribed ? '1' : '0');
        }
        btn.textContent = subscribed ? 'Subscribed' : 'Subscribe';
        btn.classList.toggle('subscribed', subscribed);
        if (statSubs) statSubs.textContent = fmt(subCount());
      });
      subWrap.innerHTML = '';
      subWrap.appendChild(btn);
    }
  }

  // photos - real from the API (each links to /image/{id}); demo fallback respects local deletions
  const delKey = 'campus-deleted-' + viewed;
  const deleted = JSON.parse(localStorage.getItem(delKey) || '[]');
  const photosTaggedEl = document.getElementById('profilePhotosTagged');
  if (api && Array.isArray(api.photos)) {
    const lockBadge = '<span class="pphoto-lock" aria-label="Private" title="Only you can see this" style="position:absolute;top:8px;right:8px;width:26px;height:26px;display:flex;align-items:center;justify-content:center;background:rgba(0,0,0,.55);border-radius:50%;color:#fff"><svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="5" y="11" width="14" height="9" rx="2"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/></svg></span>';
    const tile = p =>
      `<a class="pphoto${p.isPrivate ? ' is-private' : ''}" href="${imageUrl(p.id)}" data-id="${p.id}" style="position:relative;background-image:url('${p.image}');background-size:cover;background-position:center">${p.isPrivate ? lockBadge : ''}</a>`;
    // "Photos taken" = ones this player took; "Tagged in" = ones they're tagged in (theirs or others').
    // Older photos may lack the flags: if neither flag is present on any photo, treat all as taken.
    const hasFlags = api.photos.some(p => 'taken' in p || 'tagged' in p);
    const taken  = hasFlags ? api.photos.filter(p => p.taken) : api.photos;
    const tagged = hasFlags ? api.photos.filter(p => p.tagged) : [];
    photosEl.innerHTML = taken.length ? taken.map(tile).join('') : `<p class="profile-empty">No photos taken yet.</p>`;
    if (photosTaggedEl) photosTaggedEl.innerHTML = tagged.length ? tagged.map(tile).join('') : `<p class="profile-empty">Not tagged in any photos yet.</p>`;
  } else {
    if (photosTaggedEl) photosTaggedEl.innerHTML = `<p class="profile-empty">Not tagged in any photos yet.</p>`;
    const source = USER_PHOTOS[viewed] || PROFILE_PHOTOS;
    const visible = source.filter(p => !deleted.includes(p.id));
    photosEl.innerHTML = visible.length
      ? visible.map(p => {
          const bg = p.image
            ? `background-image:url('${p.image}');background-size:cover;background-position:center`
            : `background-image:url('assets/hero-bg.jpg');background-position:${p.pos};background-size:${p.size}`;
          return `<a class="pphoto" href="${imageUrl(viewed + '__' + p.id)}" data-id="${p.id}" style="${bg}"></a>`;
        }).join('')
      : `<p class="profile-empty">No photos yet.</p>`;
  }

  // delete handler
  photosEl.addEventListener('click', e => {
    const del = e.target.closest('.pphoto-del');
    if (!del) return;
    e.preventDefault();
    e.stopPropagation();
    const tile = del.closest('.pphoto');
    const id = tile.dataset.id;
    const d = JSON.parse(localStorage.getItem(delKey) || '[]');
    if (!d.includes(id)) { d.push(id); localStorage.setItem(delKey, JSON.stringify(d)); }
    tile.style.opacity = '0';
    tile.style.transform = 'scale(.9)';
    setTimeout(() => { tile.remove(); refreshPhotoCount(); }, 180);
  });

  function refreshPhotoCount() {
    const n = photosEl.querySelectorAll('.pphoto').length;
    if (n === 0) photosEl.innerHTML = `<p class="profile-empty">No photos yet.</p>`;
  }

  // ── Rooms tab (rec.net-style room cards) ──
  const roomsEl = document.getElementById('profileRooms');
  if (roomsEl) {
    // visits = an eye, same glyph as the Rooms page and room page stat strip
    const PERSON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M1.5 12S5.5 5 12 5s10.5 7 10.5 7-4 7-10.5 7S1.5 12 1.5 12z"/><circle cx="12" cy="12" r="3.2"/></svg>';
    const avSrc = (u && u.profile_image) || avatarUrl(viewed);
    if (api && Array.isArray(api.rooms)) {
      roomsEl.innerHTML = api.rooms.length
        ? api.rooms.map(r => {
            const cover = r.image
              ? `background-image:url('${r.image}');background-size:cover;background-position:center`
              : `background:linear-gradient(135deg,#ff5a3c,#7b2ff7)`;
            return `<a class="room-card" href="${roomUrl(r.name || r.id)}">
              <div class="room-card-head">
                <img class="room-card-av" src="${avSrc}" alt="">
                <div class="room-card-id"><span class="room-card-name">^${escapeHtml(r.name || 'Room')}</span><span class="room-card-by">@${escapeHtml(r.creator || handleText)}</span></div>
              </div>
              <div class="room-card-cover" style="${cover}"></div>
              <div class="room-card-foot">
                <span class="room-card-stat">${PERSON}${fmt(r.visits || 0)}</span>
              </div>
            </a>`;
          }).join('')
        : `<p class="profile-empty">No rooms yet.</p>`;
    } else {
      const roomIds = ['whisker-heights', 'midnight-mews', 'litterbox-lab', 'yarn-quest', 'deep-dive-reef', 'haunted-cattery'];
      roomsEl.innerHTML = roomIds.map(rid => {
        const r = ROOMS_DATA[rid];
        return `<a class="room-card" href="${roomUrl(rid)}">
          <div class="room-card-head">
            <img class="room-card-av" src="${avSrc}" alt="">
            <div class="room-card-id"><span class="room-card-name">^${r.name}</span><span class="room-card-by">@${viewed}</span></div>
          </div>
          <div class="room-card-cover" style="background:linear-gradient(135deg,${r.a},${r.b})"></div>
          <div class="room-card-foot">
            <span class="room-card-stat">${CHEER_ICON}${fmt(r.players)}</span>
            <span class="room-card-stat">${PERSON}${r.visits}</span>
          </div>
        </a>`;
      }).join('');
    }
  }

  // ── Events tab ──
  const eventsEl = document.getElementById('profileEvents');
  if (eventsEl) loadEvents(eventsEl, '/api/site/profile/' + encodeURIComponent(viewed) + '/events', { showRoom: true }, isOwn ? "You aren't hosting any events." : 'No upcoming events.');

  // ── tab switching ──
  const ptabs = document.querySelectorAll('.profile-tabs .ptab');
  const ppanels = document.querySelectorAll('.profile-panel');
  ptabs.forEach(tab => {
    tab.addEventListener('click', () => {
      ptabs.forEach(t => t.classList.remove('active'));
      tab.classList.add('active');
      const which = tab.dataset.tab;
      ppanels.forEach(p => p.classList.toggle('active', p.dataset.panel === which));
    });
  });
}
renderProfile();

// ───── IMAGE PAGE ─────
async function renderImage() {
  const photoEl = document.getElementById('imagePhoto');
  if (!photoEl) return;

  const id = viewedImageId();
  // Real photo from /api/site/photos/{id} (numeric id); demo getPhoto() as a fallback for
  // the old "user__letter" ids.
  let ph = null;
  if (apiOn() && id != null && /^\d+$/.test(String(id))) {
    const data = await apiGet('/api/site/photos/' + encodeURIComponent(id));
    if (data && data.ok && data.photo) {
      const p = data.photo, a = p.author || {};
      ph = {
        user: a.display_name || a.username || 'Player',
        handle: a.username || 'player',
        avatar: a.profile_image,
        image: p.image, pos: 'center', size: 'cover',
        title: p.description || 'Photo', takenAt: p.createdAt,
        cheers: p.cheers || 0, comments: p.comments || 0, cheered: !!p.cheered,
        commentList: Array.isArray(p.commentList) ? p.commentList.map(serverComment) : [],
        tagged: Array.isArray(p.taggedPlayers) ? p.taggedPlayers : [],
        room: p.room || null,
        isOwner: !!p.isOwner, visibility: p.visibility || 'public',
      };
    }
  }
  if (!ph) ph = getPhoto(id);

  if (!ph) {
    document.getElementById('imagePage').innerHTML = '<p class="image-missing">Photo not found.</p>';
    return;
  }

  document.title = `${ph.title || 'Photo'} - Campus`;

  const author = document.getElementById('imageAuthor');
  author.href = userUrl(ph.handle);
  const imgAvEl = document.getElementById('imageAuthorAv');
  imgAvEl.src = ph.avatar || avatarUrl(ph.handle);
  imgAvEl.dataset.av = avKey(ph.handle);
  document.getElementById('imageAuthorName').textContent = ph.user;
  document.getElementById('imageAuthorHandle').textContent = '@' + ph.handle;

  photoEl.style.backgroundImage = `url('${ph.image || 'assets/hero-bg.jpg'}')`;
  photoEl.style.backgroundPosition = ph.pos || 'center';
  photoEl.style.backgroundSize = ph.size || 'cover';

  // ── tagged players: a little people button on the photo that opens a list ──
  const tagged = Array.isArray(ph.tagged) ? ph.tagged.slice() : [];
  if (tagged.length) {
    const btn = document.createElement('button');
    btn.className = 'tagbtn'; btn.type = 'button';
    btn.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M23 21v-2a4 4 0 0 0-3-3.87"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/></svg><span class="tagbtn-n"></span>';
    const pop = document.createElement('div'); pop.className = 'tagpop';
    photoEl.appendChild(btn); photoEl.appendChild(pop);
    const renderTags = () => {
      const n = btn.querySelector('.tagbtn-n'); if (n) n.textContent = tagged.length;
      if (!tagged.length) { btn.remove(); pop.remove(); return; }
      pop.innerHTML = '<div class="tp-head">In this photo</div>' + tagged.map(t =>
        `<div class="tp-row" data-pid="${t.id}"><span class="tp-av" style="background-image:url('${t.profile_image || ''}')"></span>`
        + `<a class="tp-name" href="${userUrl(t.username)}">@${escapeHtml(t.username)}</a>`
        + (ph.isOwner ? `<button class="tp-x" data-remove="${t.id}" title="Remove tag" aria-label="Remove @${escapeHtml(t.username)}">&times;</button>` : '')
        + `</div>`).join('');
    };
    renderTags();
    btn.addEventListener('click', e => { e.stopPropagation(); pop.classList.toggle('open'); });
    document.addEventListener('click', () => pop.classList.remove('open'));
    pop.addEventListener('click', async e => {
      e.stopPropagation();
      const rm = e.target.closest('.tp-x'); if (!rm) return;
      const pid = rm.dataset.remove; rm.disabled = true;
      const res = await apiPost('/api/site/photos/' + encodeURIComponent(id) + '/untag', { playerId: Number(pid) });
      if (res && res.ok) {
        const idx = tagged.findIndex(t => String(t.id) === String(pid));
        if (idx >= 0) tagged.splice(idx, 1);
        renderTags();
        if (!tagged.length) pop.classList.remove('open');
        toast('Tag removed');
      } else { rm.disabled = false; toast('Could not remove tag'); }
    });
  }

  // ── owner: photo visibility (private / friends / public), synced to the server + in-game ──
  if (ph.isOwner) {
    const infoText = document.querySelector('.image-info-text');
    if (infoText) {
      const seg = document.createElement('div'); seg.className = 'vis-seg';
      const opts = [
        ['private', 'Private', '<rect x="5" y="11" width="14" height="9" rx="2"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/>'],
        ['friends', 'Friends', '<path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M23 21v-2a4 4 0 0 0-3-3.87"/>'],
        ['public', 'Public', '<circle cx="12" cy="12" r="9"/><path d="M3 12h18"/><path d="M12 3a15 15 0 0 1 0 18 15 15 0 0 1 0-18"/>'],
      ];
      seg.innerHTML = opts.map(([v, label, path]) =>
        `<button type="button" data-vis="${v}"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">${path}</svg>${label}</button>`).join('');
      infoText.appendChild(seg);
      const mark = v => seg.querySelectorAll('button').forEach(b => b.classList.toggle('active', b.dataset.vis === v));
      mark(ph.visibility || 'public');
      seg.addEventListener('click', async e => {
        const b = e.target.closest('button'); if (!b) return;
        const v = b.dataset.vis; if (v === ph.visibility) return;
        const prev = ph.visibility; mark(v);
        const res = await apiPost('/api/site/photos/' + encodeURIComponent(id) + '/visibility', { visibility: v });
        if (res && res.ok) { ph.visibility = (res.data && res.data.visibility) || v; mark(ph.visibility); toast('Photo set to ' + (ph.visibility === 'friends' ? 'friends only' : ph.visibility)); }
        else { mark(prev); toast('Could not update visibility'); }
      });
    }
  }

  const imgExport = document.getElementById('imgExport');
  if (imgExport) imgExport.addEventListener('click', async () => {
    const src = ph.image || '';
    if (!src) { toast('Nothing to download'); return; }
    imgExport.disabled = true;
    try {
      const name = (src.split('/').pop() || 'photo.jpg').split('?')[0];
      await saveFile(await fetchBlob(src), photoFileName(name, ph.title !== 'Photo' ? ph.title : ''));
    } catch { toast('Could not download the photo'); }
    imgExport.disabled = false;
  });

  const imgReport = document.getElementById('imgReport');
  if (imgReport) imgReport.addEventListener('click', () => openReport('Report photo', () => {
    imgReport.textContent = 'Reported';
    imgReport.disabled = true;
  }, /^\d+$/.test(String(id)) ? { type: 'photo', id } : null));

  // title (with a custom override) + edit pencil for the owner
  const titleEl = document.getElementById('imageTitle');
  const savedTitle = localStorage.getItem('campus-title-' + id);
  if (savedTitle && !/^\d+$/.test(String(id))) ph.title = savedTitle;
  titleEl.textContent = ph.title || 'Untitled photo';
  document.title = `${ph.title || 'Photo'} - Campus`;

  const owner = localStorage.getItem(USER_KEY);
  if (owner && owner.toLowerCase() === (ph.handle || '').toLowerCase()) {
    // wrap title in a row with a pencil button
    const row = document.createElement('div');
    row.className = 'title-row';
    titleEl.parentNode.insertBefore(row, titleEl);
    row.appendChild(titleEl);

    const pencil = document.createElement('button');
    pencil.className = 'title-edit';
    pencil.setAttribute('aria-label', 'Edit title');
    pencil.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M12 20h9"/><path d="M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1 1-4 12.5-12.5z"/></svg>';
    row.appendChild(pencil);

    pencil.addEventListener('click', () => {
      const current = titleEl.textContent;
      const editor = document.createElement('div');
      editor.className = 'title-editor';
      editor.innerHTML = `<input class="title-input" type="text" maxlength="80" value="${escapeHtml(current)}"><button class="title-save">Save</button><button class="title-cancel">Cancel</button>`;
      row.style.display = 'none';
      row.parentNode.insertBefore(editor, row);
      const input = editor.querySelector('.title-input');
      input.focus(); input.select();
      const finish = () => { editor.remove(); row.style.display = ''; };
      editor.querySelector('.title-save').addEventListener('click', async () => {
        const v = input.value.trim() || 'Untitled photo';
        if (apiOn() && /^\d+$/.test(String(id))) {
          const r = await apiPost('/api/site/photos/' + encodeURIComponent(id) + '/description', { description: input.value.trim() });
          if (!r || !r.ok) { toast('Could not save the title'); return; }
          const saved = (r.data && r.data.description) || '';
          titleEl.textContent = saved || 'Untitled photo';
          document.title = `${saved || 'Photo'} - Campus`;
        } else {
          titleEl.textContent = v;
          document.title = `${v} - Campus`;
          localStorage.setItem('campus-title-' + id, v);
        }
        toast('Title updated');
        finish();
      });
      editor.querySelector('.title-cancel').addEventListener('click', finish);
      input.addEventListener('keydown', e => { if (e.key === 'Enter') editor.querySelector('.title-save').click(); if (e.key === 'Escape') finish(); });
    });
  }

  const metaEl = document.getElementById('imageMeta');
  metaEl.innerHTML = (ph.takenAt ? `<span>${timeAgo(ph.takenAt)} ago</span>` : '')
    + (ph.room && ph.room.name ? `<span class="image-room-sep">·</span><a class="image-room" href="${roomUrl(ph.room.name)}">^${escapeHtml(ph.room.name)}</a>` : '');

  // comments (reuse the comment system; persist per photo)
  // ⋯ menu - set this photo as profile picture / banner
  const menuBtn = document.getElementById('imageMenuBtn');
  const menu = document.getElementById('imageMenu');
  const me = localStorage.getItem(USER_KEY);
  if (menuBtn && menu) {
    if (!me) {
      menuBtn.style.display = 'none'; // must be logged in to set your own
    } else {
      // owner of the photo gets a delete option
      const isOwner = me.toLowerCase() === (ph.handle || '').toLowerCase();
      if (isOwner) {
        const delItem = document.createElement('button');
        delItem.className = 'image-menu-item danger';
        delItem.dataset.act = 'delete';
        delItem.textContent = 'Delete photo';
        menu.appendChild(delItem);
      }
      menuBtn.addEventListener('click', e => { e.stopPropagation(); menu.classList.toggle('open'); });
      document.addEventListener('click', () => menu.classList.remove('open'));
      menu.addEventListener('click', e => {
        const item = e.target.closest('.image-menu-item');
        if (!item) return;
        menu.classList.remove('open');
        const act = item.dataset.act;
        const cropData = { id: id, image: ph.image || 'assets/hero-bg.jpg', pos: ph.pos, size: ph.size };
        const data = JSON.stringify(cropData);
        if (act === 'pfp') {
          localStorage.setItem('campus-pfp-' + me, data);
          refreshAvatars(avKey(me), cropData.image); // update every avatar instantly (optimistic)
          if (apiOn() && /^\d+$/.test(String(id))) {
            // a real photo: the server points your profile picture at it (no download / re-upload from the browser)
            apiPost('/api/site/me/pfp', { photoId: Number(id) }).then(r => {
              if (r && r.ok) {
                toast('Profile picture updated');
                const url = r.data && r.data.profile_image;
                if (url) refreshAvatars(avKey(me), url);
              } else toast((r && r.data && r.data.error) || 'Could not set the profile picture');
            });
          } else {
            toast('Profile picture updated');
            syncProfileImageToApi(cropData.image); // also push to the backend
          }
        } else if (act === 'banner') {
          if (apiOn() && /^\d+$/.test(String(id))) {
            apiPost('/api/site/me/banner', { photoId: Number(id) }).then(r => toast(r && r.ok ? 'Profile banner updated' : ((r && r.data && r.data.error) || 'Could not set the banner')));
          } else {
            localStorage.setItem('campus-banner-' + me, data);
            toast('Profile banner updated');
          }
        } else if (act === 'delete') {
          openConfirm({
            title: 'Delete photo?',
            message: 'This photo will be removed from your profile. This can\'t be undone.',
            confirmText: 'Delete',
            onConfirm: async () => {
              if (apiOn() && /^\d+$/.test(String(id))) {
                const r = await apiDelete('/api/site/photos/' + encodeURIComponent(id));
                if (!r || !r.ok) { toast('Could not delete the photo'); return; }
              }
              if (id && String(id).includes('__')) {
                const [u, letter] = id.split('__');
                const key = 'campus-deleted-' + u;
                const d = JSON.parse(localStorage.getItem(key) || '[]');
                if (!d.includes(letter)) { d.push(letter); localStorage.setItem(key, JSON.stringify(d)); }
              }
              toast('Photo deleted');
              setTimeout(() => { window.location.href = userUrl(ph.handle); }, 650);
            }
          });
        }
      });
    }
  }

  const host = document.getElementById('imageCommentsHost');
  if (/^\d+$/.test(String(id))) host.dataset.photo = String(id);
  const block = commentsBlock({ thread: ph.commentList || [] });
  host.innerHTML = block.replace(' hidden', ''); // always visible here
  if (host.dataset.photo) { const l = host.querySelector('.comment-list'); if (l) l.dataset.loaded = '1'; }

  // cheer button just above the comment form / Post button
  const cheerBtn = `<button class="pstat cheer${ph.cheered ? ' liked' : ''}" data-n="${(ph.cheers || 0) - (ph.cheered ? 1 : 0)}">${CHEER_ICON}<span>${fmt(ph.cheers || 0)}</span></button>`;
  const tools = document.getElementById('imageTools');
  if (tools) tools.insertAdjacentHTML('afterbegin', cheerBtn);
  else {
    const form = host.querySelector('.comment-form');
    if (form) form.insertAdjacentHTML('beforebegin', `<div class="image-actions">${cheerBtn}</div>`);
    else host.querySelector('.post-comments')?.insertAdjacentHTML('beforeend', `<div class="image-actions">${cheerBtn}</div>`);
  }
  const ch = document.getElementById('imageComments')?.querySelector('h2');
  if (ch && (ph.commentList || []).length) ch.textContent = `Comments (${ph.commentList.length})`;
}

// ───── EXPORT (Settings > Export, and the image page's Export button) ─────
// Everything happens in the browser: the page lists the photos, fetches each /img/ file,
// zips them with JSZip (loaded from cdnjs on first use) and hands the zip to the browser's
// download. The server never builds or stores an archive.
async function fetchBlob(url) {
  const r = await fetch(url, { credentials: 'same-origin' });
  if (!r.ok) throw new Error('HTTP ' + r.status);
  return await r.blob();
}
function saveFile(blob, filename) {
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob); a.download = filename;
  document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(a.href), 4000);
}
// "<id>_<title>.<ext>" - keeps the server's name so the file is unique, adds the title if any.
function photoFileName(serverName, title) {
  const m = /^(.*?)(\.[a-z0-9]+)?$/i.exec(serverName || 'photo.jpg');
  const base = m[1] || 'photo', ext = m[2] || '.jpg';
  const t = (title || '').replace(/[^\w\- ]+/g, '').trim().slice(0, 40);
  return t ? `${base}_${t.replace(/\s+/g, '_')}${ext}` : base + ext;
}
let _jszip = null;
function loadJSZip() {
  if (window.JSZip) return Promise.resolve(window.JSZip);
  if (_jszip) return _jszip;
  _jszip = new Promise((res, rej) => {
    const sc = document.createElement('script');
    sc.src = 'https://cdnjs.cloudflare.com/ajax/libs/jszip/3.10.1/jszip.min.js';
    sc.onload = () => res(window.JSZip); sc.onerror = () => { _jszip = null; rej(new Error('jszip')); };
    document.head.appendChild(sc);
  });
  return _jszip;
}
function openExportPhotos() {
  if (!localStorage.getItem(USER_KEY)) { toast('Log in to export your photos'); return; }
  let scrim = document.getElementById('exportScrim');
  let modal = document.getElementById('exportModal');
  if (!modal) {
    scrim = document.createElement('div'); scrim.id = 'exportScrim'; scrim.className = 'report-scrim';
    modal = document.createElement('div'); modal.id = 'exportModal'; modal.className = 'report-modal';
    document.body.appendChild(scrim); document.body.appendChild(modal);
  }
  const close = () => { modal.classList.remove('open'); scrim.classList.remove('open'); document.body.style.overflow = ''; };
  modal.innerHTML = `
    <div class="report-card export-card">
      <button class="report-close" aria-label="Close">✕</button>
      <h2 class="report-title">Export Photos</h2>
      <p class="report-sub">Choose what to include. The photos are zipped in your browser.</p>
      <label class="export-opt"><input type="checkbox" id="expTaken" checked><span class="export-check"></span><span><b>Photos taken by you</b><small>Every photo you took, including private ones.</small></span></label>
      <label class="export-opt"><input type="checkbox" id="expTagged" checked><span class="export-check"></span><span><b>Photos including you</b><small>Photos other players took that you're tagged in.</small></span></label>
      <p class="export-progress" id="expProgress" hidden></p>
      <div class="report-actions">
        <button class="report-cancel export-cancel">Cancel</button>
        <button class="report-submit export-go">Continue</button>
      </div>
    </div>`;
  const card = modal.querySelector('.export-card');
  let busy = false;
  const tryClose = () => { if (!busy) close(); };
  scrim.onclick = tryClose; modal.onclick = e => { if (e.target === modal) tryClose(); };
  card.querySelector('.report-close').addEventListener('click', tryClose);
  card.querySelector('.export-cancel').addEventListener('click', tryClose);
  const go = card.querySelector('.export-go');
  const prog = card.querySelector('#expProgress');
  const say = t => { prog.hidden = false; prog.textContent = t; };
  go.addEventListener('click', async () => {
    const taken = card.querySelector('#expTaken').checked, tagged = card.querySelector('#expTagged').checked;
    if (!taken && !tagged) { say('Pick at least one option.'); return; }
    busy = true; go.disabled = true; card.querySelectorAll('input').forEach(i => i.disabled = true);
    try {
      say('Loading your photo list…');
      const d = await apiGet(`/api/site/me/photos?taken=${taken}&tagged=${tagged}`);
      const list = (d && d.ok && d.photos) || [];
      if (!list.length) { say('No photos to export.'); busy = false; go.disabled = false; card.querySelectorAll('input').forEach(i => i.disabled = false); return; }
      const JSZip = await loadJSZip();
      const zip = new JSZip();
      const takenDir = zip.folder('taken-by-me'), taggedDir = zip.folder('photos-of-me');
      let n = 0, failed = 0;
      for (const ph of list) {
        n++; say(`Downloading ${n} / ${list.length}…`);
        try {
          const blob = await fetchBlob(ph.image);
          const name = photoFileName(ph.file || (ph.image || '').split('/').pop(), ph.description);
          (ph.taken ? takenDir : taggedDir).file(name, blob);
        } catch { failed++; }
      }
      say('Zipping…');
      const out = await zip.generateAsync({ type: 'blob', compression: 'STORE' });
      const me = localStorage.getItem(USER_KEY) || 'campus';
      saveFile(out, `campus-photos-${me}-${new Date().toISOString().slice(0, 10)}.zip`);
      say(`Done - ${list.length - failed} photo${list.length - failed === 1 ? '' : 's'} exported${failed ? `, ${failed} could not be downloaded` : ''}.`);
      go.textContent = 'Close'; go.disabled = false; busy = false;
      go.onclick = close;
    } catch (e) {
      say('Export failed - ' + (e && e.message === 'jszip' ? 'could not load the zip library.' : 'please try again.'));
      busy = false; go.disabled = false; card.querySelectorAll('input').forEach(i => i.disabled = false);
    }
  });
  modal.classList.add('open'); scrim.classList.add('open'); document.body.style.overflow = 'hidden';
}

// Export Data: the player's own account as one .json file (built by the server from their account; nothing is stored).
async function openExportData() {
  if (!localStorage.getItem(USER_KEY)) { toast('Log in to export your data'); return; }
  const btn = document.getElementById('exportData');
  if (btn) btn.disabled = true;
  try {
    const r = await fetch('/api/site/me/export', { credentials: 'same-origin' });
    if (!r.ok) throw new Error('HTTP ' + r.status);
    const blob = await r.blob();
    const me = localStorage.getItem(USER_KEY) || 'campus';
    saveFile(blob, `campus-data-${me}-${new Date().toISOString().slice(0, 10)}.json`);
    toast('Your data was downloaded');
  } catch { toast('Could not export your data - please try again'); }
  if (btn) btn.disabled = false;
}

// Export Rooms: pick from the rooms you CREATED, the browser fetches each room's files and zips them. The folder layout
// is the one the admin Archive importer reads, so an export can be imported back.
function openExportRooms() {
  if (!localStorage.getItem(USER_KEY)) { toast('Log in to export your rooms'); return; }
  let scrim = document.getElementById('exportScrim');
  let modal = document.getElementById('exportModal');
  if (!modal) {
    scrim = document.createElement('div'); scrim.id = 'exportScrim'; scrim.className = 'report-scrim';
    modal = document.createElement('div'); modal.id = 'exportModal'; modal.className = 'report-modal';
    document.body.appendChild(scrim); document.body.appendChild(modal);
  }
  const close = () => { modal.classList.remove('open'); scrim.classList.remove('open'); document.body.style.overflow = ''; };
  modal.innerHTML = `
    <div class="report-card export-card">
      <button class="report-close" aria-label="Close">&#10005;</button>
      <h2 class="report-title">Export Rooms</h2>
      <p class="report-sub">Rooms you created. Each room is saved with its settings, picture and every saved build.</p>
      <div id="expRoomList" style="max-height:280px;overflow:auto"></div>
      <p class="export-progress" id="expProgress"></p>
      <div class="report-actions">
        <button class="report-cancel export-cancel">Cancel</button>
        <button class="report-submit export-go" disabled>Export</button>
      </div>
    </div>`;
  const card = modal.querySelector('.export-card');
  const list = card.querySelector('#expRoomList');
  const prog = card.querySelector('#expProgress');
  const go = card.querySelector('.export-go');
  const say = t => { prog.hidden = false; prog.textContent = t; };
  let busy = false;
  const tryClose = () => { if (!busy) close(); };
  scrim.onclick = tryClose; modal.onclick = e => { if (e.target === modal) tryClose(); };
  card.querySelector('.report-close').addEventListener('click', tryClose);
  card.querySelector('.export-cancel').addEventListener('click', tryClose);
  modal.classList.add('open'); scrim.classList.add('open'); document.body.style.overflow = 'hidden';
  say('Loading your rooms...');

  const boxes = () => Array.from(list.querySelectorAll('input[data-room]'));
  const refresh = () => { go.disabled = busy || !boxes().some(b => b.checked); };

  apiGet('/api/site/me/rooms').then(d => {
    const rooms = (d && d.ok && d.rooms) || [];
    if (!rooms.length) { say('You have not created any rooms.'); return; }
    prog.hidden = true;
    const esc = s => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
    list.innerHTML = `<label class="export-opt"><input type="checkbox" id="expAllRooms" checked><span class="export-check"></span><span><b>All rooms (${rooms.length})</b></span></label>` +
      rooms.map(r => `<label class="export-opt"><input type="checkbox" data-room="${esc(r.id)}" data-name="${esc(r.name)}" checked><span class="export-check"></span><span><b>${esc(r.name)}</b><small>${r.isDorm ? 'Your dorm - ' : ''}${r.subrooms} subroom${r.subrooms === 1 ? '' : 's'}</small></span></label>`).join('');
    list.querySelector('#expAllRooms').addEventListener('change', e => { boxes().forEach(b => b.checked = e.target.checked); refresh(); });
    boxes().forEach(b => b.addEventListener('change', refresh));
    refresh();
  });

  go.addEventListener('click', async () => {
    const picked = boxes().filter(b => b.checked);
    if (!picked.length) return;
    busy = true; go.disabled = true; card.querySelectorAll('input').forEach(i => i.disabled = true);
    try {
      const JSZip = await loadJSZip();
      const zip = new JSZip();
      let failed = 0, done = 0;
      for (const b of picked) {
        done++;
        say(`Room ${done} / ${picked.length}: ${b.dataset.name}...`);
        const man = await apiGet(`/api/site/me/rooms/${b.dataset.room}/manifest`);
        if (!man || !man.ok) { failed++; continue; }
        const dir = zip.folder(man.folder);
        dir.file('RoomDetails.json', man.details);
        for (const f of man.files) {
          try {
            const blob = await fetchBlob(`/api/site/me/rooms/${b.dataset.room}/file?name=${encodeURIComponent(f.name)}`);
            dir.file(f.path, blob);
          } catch { failed++; }
        }
      }
      say('Zipping...');
      const out = await zip.generateAsync({ type: 'blob', compression: 'STORE' });
      const me = localStorage.getItem(USER_KEY) || 'campus';
      saveFile(out, `campus-rooms-${me}-${new Date().toISOString().slice(0, 10)}.zip`);
      say(`Done - ${picked.length} room${picked.length === 1 ? '' : 's'} exported${failed ? `, ${failed} file${failed === 1 ? '' : 's'} could not be downloaded` : ''}.`);
      go.textContent = 'Close'; go.disabled = false; busy = false;
      go.onclick = close;
    } catch (e) {
      say('Export failed - ' + (e && e.message === 'jszip' ? 'could not load the zip library.' : 'please try again.'));
      busy = false; card.querySelectorAll('input').forEach(i => i.disabled = false); refresh();
    }
  });
}

// ───── QUICK ADMIN (profile + room pages) ─────
// The "Admin" button, for dev accounts only: /api/site/me says admin:true when the account is a
// developer AND this network could reach the admin panel. Every action goes through
// /api/site/admin/*, which re-checks both and runs the admin panel's own handler, so it bans,
// kicks and logs exactly like the panel does.
async function mountAdminButton(kind, key, container) {
  if (!container || !key || !apiOn()) return;
  await SESSION_READY;
  if (!CAMPUS_ADMIN || container.querySelector('.admin-quick-btn')) return;
  const b = document.createElement('button');
  b.className = 'profile-report-btn admin-quick-btn';
  b.textContent = 'Admin';
  b.addEventListener('click', () => openAdminPopup(kind, key));
  container.appendChild(b);
}

function adminDur(sec) {
  if (sec == null) return 'permanently';
  const d = Math.floor(sec / 86400), h = Math.floor(sec % 86400 / 3600), m = Math.ceil(sec % 3600 / 60);
  return d ? `${d}d ${h}h left` : h ? `${h}h ${m}m left` : `${m}m left`;
}

async function openAdminPopup(kind, key) {
  let scrim = document.getElementById('adminScrim');
  let modal = document.getElementById('adminModal');
  if (!modal) {
    scrim = document.createElement('div'); scrim.id = 'adminScrim'; scrim.className = 'report-scrim';
    modal = document.createElement('div'); modal.id = 'adminModal'; modal.className = 'report-modal';
    document.body.appendChild(scrim); document.body.appendChild(modal);
  }
  const close = () => { modal.classList.remove('open'); scrim.classList.remove('open'); document.body.style.overflow = ''; };
  scrim.onclick = close; modal.onclick = e => { if (e.target === modal) close(); };
  const esc = s => escapeHtml(String(s == null ? '' : s));
  const base = `/api/site/admin/${kind}/${encodeURIComponent(key)}`;
  const ACC = ['Private', 'Public', 'Unlisted', 'Dev only', 'Dev unlisted'];

  const playerHtml = d => {
    const flags = [d.dev && 'Developer', d.mod && 'Moderator', d.junior && 'Junior'].filter(Boolean);
    const where = d.online ? (d.room ? `Online in ^${esc(d.room.name)}${d.room.isPrivate ? ' (private)' : ''}` : 'Online') : 'Offline';
    const noBan = d.dev || d.is_self;
    return `<h2 class="report-title">Admin · @${esc(d.username)}</h2>
      <p class="report-sub">#${d.id} · ${where} · Level ${d.level} · ${fmt(d.tokens)} tokens${flags.length ? ' · ' + flags.join(', ') : ''}</p>
      ${d.ban ? `<p class="adm-status">Banned ${d.ban.permanent ? 'permanently' : '(' + adminDur(d.ban.remaining_seconds) + ')'}${d.ban.reason ? ': ' + esc(d.ban.reason) : ''}</p>` : ''}
      ${d.mic_ban ? `<p class="adm-status">Mic banned ${d.mic_ban.permanent ? 'permanently' : '(' + adminDur(d.mic_ban.remaining_seconds) + ')'}</p>` : ''}
      <div class="adm-sec"><h3>Room</h3><div class="adm-row">
        <button class="adm-btn" data-act="kick" ${d.is_self ? 'disabled' : ''}>Kick</button>
        <button class="adm-btn" data-act="join" ${d.is_self || !d.room ? 'disabled' : ''}>Go to them</button>
        <button class="adm-btn" data-act="bring" ${d.is_self ? 'disabled' : ''}>Bring to me</button></div></div>
      <div class="adm-sec"><h3>Ban</h3>
        ${d.ban ? `<div class="adm-row"><button class="adm-btn" data-act="unban">Unban</button></div>` : `
        <div class="adm-row"><select class="adm-in" id="admBanDur" aria-label="Ban length">
          <option value="3600">1 hour</option><option value="86400">1 day</option><option value="259200">3 days</option>
          <option value="604800" selected>7 days</option><option value="2592000">30 days</option><option value="permanent">Permanent</option></select>
          <input class="adm-in grow" id="admBanReason" maxlength="200" placeholder="Reason (they see this)" aria-label="Ban reason"></div>
        <div class="adm-row"><label class="adm-chk"><input type="checkbox" id="admBanDevice"> Device</label>
          <label class="adm-chk"><input type="checkbox" id="admBanIp"> IP</label>
          <button class="adm-btn danger" data-act="ban" ${noBan ? 'disabled title="Developers can’t be banned"' : ''}>Ban</button></div>`}</div>
      <div class="adm-sec"><h3>Mic</h3><div class="adm-row">
        ${d.mic_ban ? `<button class="adm-btn" data-act="micunban">Lift mic ban</button>` : `
        <select class="adm-in" id="admMicMin" aria-label="Mic ban length"><option value="60">1 hour</option><option value="1440" selected>1 day</option><option value="10080">7 days</option><option value="0">Permanent</option></select>
        <input class="adm-in grow" id="admMicReason" maxlength="200" placeholder="Reason" aria-label="Mic ban reason">
        <button class="adm-btn" data-act="micban" ${d.is_self ? 'disabled' : ''}>Mic ban</button>`}</div></div>
      <div class="adm-sec"><h3>Tokens</h3><div class="adm-row">
        <input class="adm-in grow" id="admTokens" type="number" step="1" placeholder="Amount (negative takes away)" aria-label="Token amount">
        <button class="adm-btn" data-act="tokens">Give</button></div></div>`;
  };

  const roomHtml = d => `<h2 class="report-title">Admin · ^${esc(d.name)}</h2>
      <p class="report-sub">#${d.id} · by ${esc(d.creator || 'nobody')} · ${d.players_now} playing now${d.featured ? ' · Featured' : ''}${d.dorm ? ' · Dorm' : ''}</p>
      <div class="adm-sec"><h3>Details</h3>
        <div class="adm-row"><input class="adm-in grow" id="admRoomName" maxlength="60" value="${esc(d.name)}" placeholder="Name" aria-label="Room name"></div>
        <div class="adm-row"><textarea class="adm-in grow" id="admRoomDesc" rows="2" maxlength="500" placeholder="Description" aria-label="Description">${esc(d.description)}</textarea></div>
        <div class="adm-row"><select class="adm-in" id="admRoomAcc" aria-label="Who can join">${ACC.map((n, i) => `<option value="${i}"${i === d.accessibility ? ' selected' : ''}>${n}</option>`).join('')}</select>
          <input class="adm-in adm-num" id="admRoomMax" type="number" min="1" max="100" value="${d.max_players}" title="Max players" aria-label="Max players">
          <label class="adm-chk"><input type="checkbox" id="admRoomClone"${d.cloning ? ' checked' : ''}> Cloning</label>
          <button class="adm-btn" data-act="edit">Save</button></div></div>
      <div class="adm-sec"><h3>Room</h3><div class="adm-row">
        <button class="adm-btn" data-act="${d.featured ? 'unfeature' : 'feature'}">${d.featured ? 'Unfeature' : 'Feature'}</button>
        <button class="adm-btn danger" data-act="delete" ${d.dorm ? 'disabled title="A dorm can’t be deleted"' : ''}>Delete room</button></div></div>`;

  const paintNote = (t, bad) => {
    const n = modal.querySelector('.adm-note');
    if (n) { n.hidden = false; n.textContent = t; n.classList.toggle('bad', !!bad); }
  };

  const act = async (btn, d) => {
    const a = btn.dataset.act;
    const v = id => { const el = modal.querySelector('#' + id); return el ? el.value : ''; };
    const ck = id => { const el = modal.querySelector('#' + id); return !!(el && el.checked); };
    let body = {};
    if (a === 'ban') {
      if (!v('admBanReason').trim()) { paintNote('Give a reason for the ban.', true); return; }
      body = { duration: v('admBanDur'), reason: v('admBanReason'), device: ck('admBanDevice'), ip: ck('admBanIp') };
      if (!confirm(`Ban @${d.username}${body.duration === 'permanent' ? ' permanently' : ''}?`)) return;
    } else if (a === 'micban') {
      body = { minutes: v('admMicMin'), reason: v('admMicReason') };
    } else if (a === 'tokens') {
      const t = v('admTokens').trim();
      if (!/^-?\d+$/.test(t) || +t === 0) { paintNote('Enter a whole number of tokens.', true); return; }
      body = { amount: t };
    } else if (a === 'edit') {
      body = { name: v('admRoomName'), description: v('admRoomDesc'), accessibility: v('admRoomAcc'), maxPlayers: v('admRoomMax'), cloningAllowed: ck('admRoomClone') ? 'true' : 'false' };
    } else if (a === 'delete') {
      if (!confirm(`Delete ^${d.name}? This can’t be undone.`)) return;
    } else if (a === 'kick') {
      if (!confirm(`Kick @${d.username} from their room?`)) return;
    }
    btn.disabled = true;
    const { ok, data } = await apiPost(`${base}/${a}`, body);
    const msg = (data && (data.message || data.error)) || (ok ? 'Done.' : 'That didn’t work.');
    if (a === 'delete' && ok) { close(); toast(msg); setTimeout(() => { location.href = '/rooms'; }, 900); return; }
    await paint(msg, !ok);
  };

  const paint = async (note, bad) => {
    // apiGet is redefined further down to THROW on a non-OK answer (a lapsed session is a 403).
    let d = null;
    try { d = await apiGet(base); } catch { d = null; }
    if (!d || !d.ok) {
      modal.innerHTML = `<div class="report-card adm-card"><button class="report-close" aria-label="Close">✕</button>
        <h2 class="report-title">Admin</h2><p class="report-sub">Couldn’t load this ${kind}. Your admin access may have ended - reload the page.</p></div>`;
      modal.querySelector('.report-close').onclick = close;
      return;
    }
    modal.innerHTML = `<div class="report-card adm-card"><button class="report-close" aria-label="Close">✕</button>
      ${kind === 'player' ? playerHtml(d) : roomHtml(d)}
      <p class="adm-note${bad ? ' bad' : ''}" ${note ? '' : 'hidden'}>${esc(note || '')}</p>
      <a class="adm-panel-link" href="/admin/home/v4?${kind}=${d.id}" target="_blank" rel="noopener">Open ${kind === 'player' ? '@' + esc(d.username) : '^' + esc(d.name)} in the admin panel</a></div>`;
    const card = modal.querySelector('.adm-card');
    card.querySelector('.report-close').onclick = close;
    card.querySelectorAll('[data-act]').forEach(b => b.addEventListener('click', () => act(b, d)));
    // The panel's login redirect drops ?player= / ?room=, so leave the target for it to pick up.
    card.querySelector('.adm-panel-link').addEventListener('click', () => {
      try { localStorage.setItem('campus-admin-open', JSON.stringify({ kind, id: d.id, at: Date.now() })); } catch {}
    });
  };

  modal.innerHTML = `<div class="report-card adm-card"><p class="report-sub">Loading…</p></div>`;
  modal.classList.add('open'); scrim.classList.add('open'); document.body.style.overflow = 'hidden';
  await paint();
}

// ───── SETTINGS PAGE ─────
function initSettings() {
  const nav = document.querySelector('.settings-nav');
  if (!nav) return;
  const me = localStorage.getItem(USER_KEY);
  if (!me) {
    // not logged in — show a 401
    const main = document.querySelector('.settings-page');
    if (main) main.innerHTML =
      `<div class="forbidden">
        <h1>401</h1>
        <p>You need to be logged in to view your settings.</p>
        <a href="/login" class="btn-dl">Log in</a>
      </div>`;
    document.title = '401 - Campus';
    return;
  }

  // header
  const suAv = document.getElementById('suAv');
  if (suAv) { suAv.src = avatarUrl(me); suAv.dataset.av = avKey(me); }
  const suName = document.getElementById('suName');
  if (suName) suName.textContent = myDisplayName(me);
  if (me) { siteWatch(me); renderSettingsStatus(); }
  const suHandle = document.getElementById('suHandle');
  if (suHandle) suHandle.textContent = '@' + myHandle(me);

  // profile fields — show current values as faded placeholders (clear when typing)
  const username = document.getElementById('setUsername');
  if (username) { username.value = ''; username.placeholder = myHandle(me); }
  const nick = document.getElementById('setNickname');
  if (nick) { nick.value = ''; nick.placeholder = myDisplayName(me); }
  const bio = document.getElementById('setBio');
  if (bio) bio.value = localStorage.getItem('campus-mybio') || '';

  document.getElementById('exportPhotos')?.addEventListener('click', openExportPhotos);
  document.getElementById('exportData')?.addEventListener('click', openExportData);
  document.getElementById('exportRooms')?.addEventListener('click', openExportRooms);

  const themeSel = document.getElementById('setTheme');
  if (themeSel) {
    themeSel.value = localStorage.getItem(THEME_KEY) || 'light';
    themeSel.addEventListener('change', () => { localStorage.setItem(THEME_KEY, themeSel.value); applyTheme(themeSel.value); syncThemeToggles(); });
  }

  // nav switching
  document.querySelectorAll('.snav[data-panel]').forEach(b => {
    b.addEventListener('click', () => {
      document.querySelector('.snav.active')?.classList.remove('active');
      b.classList.add('active');
      const p = b.dataset.panel;
      document.querySelectorAll('.spanel').forEach(s => s.classList.toggle('active', s.dataset.panel === p));
    });
  });

  // ---- Discord linking tab ----
  // Isolated flow: generate a code here, run /link <code> with the Campus bot. The server only
  // records a Discord<->account link (no admin access). See SiteController /api/site/discord/*.
  (function initDiscordTab() {
    const stText = document.getElementById('discordStatusText');
    if (!stText) return;
    const linkBtn   = document.getElementById('discordLinkBtn');
    const unlinkBtn = document.getElementById('discordUnlinkBtn');

    async function jfetch(path, method) {
      try {
        const r = await fetch(path, { method: method || 'GET', credentials: 'same-origin' });
        return r.ok ? await r.json() : null;
      } catch { return null; }
    }
    const show = (el, on) => { if (el) el.style.display = on ? '' : 'none'; };

    async function load() {
      stText.textContent = 'Checking…';
      show(linkBtn, false); show(unlinkBtn, false);
      const s = await jfetch('/api/site/discord/status');
      if (!s || !s.ok) { stText.textContent = 'Could not load status.'; return; }
      if (s.linked) {
        stText.textContent = 'Connected' + (s.discordUsername ? (' as ' + s.discordUsername) : '');
        show(unlinkBtn, true);
      } else {
        stText.textContent = 'Not connected';
        show(linkBtn, true);
      }
    }

    // "Link Discord" -> full-page redirect to Discord's authorize page (OAuth). The callback
    // links the account and returns to /settings?discord=linked. Same app as the admin login, but
    // the link-scoped state means it NEVER logs you into the admin panel.
    document.getElementById('discordLinkBtn')?.addEventListener('click', () => {
      window.location.href = '/api/site/discord/oauth/start';
    });
    document.getElementById('discordUnlinkBtn')?.addEventListener('click', async () => {
      const r = await jfetch('/api/site/discord/unlink', 'POST');
      if (!r || !r.ok) { toast('Could not unlink.'); return; }
      toast('Discord unlinked'); load(); if (typeof initSocialCards === 'function') initSocialCards();
    });

    // Coming back from the OAuth round-trip: toast the outcome and open the Discord tab.
    const dq = new URLSearchParams(location.search).get('discord');
    if (dq) {
      const msg = { linked: 'Discord linked!', expired: 'Link timed out — try again.',
                    inuse: 'That Discord is already linked to another account.',
                    error: 'Could not link Discord.' }[dq] || null;
      if (msg) toast(msg);
      document.querySelector('.snav[data-panel="services"]')?.click();
      history.replaceState(null, '', location.pathname);
    }

    // Load status the first time the Services tab is opened.
    let loaded = false;
    document.querySelector('.snav[data-panel="services"]')?.addEventListener('click', () => {
      if (!loaded) { loaded = true; load(); }
    });
  })();

  // ---- Spotify (Settings > Services) ----
  // OAuth against the server's Spotify app (SiteController /api/site/spotify/*). The card shows
  // the linked account, a live "listening to" preview and the "show to" choice.
  (function initSpotifyTab() {
    const stText = document.getElementById('spotifyStatusText');
    if (!stText) return;
    const linkBtn = document.getElementById('spotifyLinkBtn');
    const unlinkBtn = document.getElementById('spotifyUnlinkBtn');
    const visRow = document.getElementById('spotifyVisRow');
    const visSel = document.getElementById('spotifyVis');
    const preview = document.getElementById('spotifyNowPreview');
    const show = (el, on) => { if (el) el.style.display = on ? '' : 'none'; };
    let timer = null;

    async function jfetch(path, method, body) {
      try {
        const r = await fetch(path, { method: method || 'GET', credentials: 'same-origin',
          headers: body ? { 'Content-Type': 'application/json' } : undefined, body: body ? JSON.stringify(body) : undefined });
        return r.ok ? await r.json() : null;
      } catch { return null; }
    }

    async function load() {
      stText.textContent = 'Checking…';
      show(linkBtn, false); show(unlinkBtn, false); visRow.hidden = true;
      const s = await jfetch('/api/site/spotify/status');
      if (!s || !s.ok) { stText.textContent = 'Could not load status.'; return; }
      if (!s.configured) { stText.textContent = 'Spotify linking is not set up on this server yet.'; return; }
      if (s.linked) {
        if (s.notAllowed) {
          // The token is real but Spotify refuses this user: the Campus app is in Spotify's
          // Development Mode, which only serves allow-listed users (User Management, max 25).
          stText.textContent = 'Connected, but Spotify is not allowing this account yet — the Campus Spotify app is in development mode, so a Campus dev has to add your Spotify email under User Management. Your song will appear automatically once they have.';
        } else {
          stText.textContent = (s.broken ? 'Link expired — link again' : 'Connected') + (s.displayName ? (' as ' + s.displayName) : '');
        }
        show(unlinkBtn, true); if (s.broken) show(linkBtn, true);
        visRow.hidden = false; visSel.value = String(s.visibility ?? 1);
        renderSpotify(preview, s.now_playing);
        clearInterval(timer);
        timer = setInterval(async () => {
          if (document.hidden) return;
          const r = await jfetch('/api/site/spotify/status');
          if (r && r.ok) renderSpotify(preview, r.now_playing);
        }, 15000);
      } else {
        stText.textContent = 'Not connected';
        show(linkBtn, true); renderSpotify(preview, null);
      }
    }

    linkBtn?.addEventListener('click', () => { window.location.href = '/api/site/spotify/oauth/start'; });
    unlinkBtn?.addEventListener('click', async () => {
      const r = await jfetch('/api/site/spotify/unlink', 'POST');
      if (!r || !r.ok) { toast('Could not unlink.'); return; }
      toast('Spotify unlinked'); clearInterval(timer); load(); if (typeof initSocialCards === 'function') initSocialCards();
    });
    visSel?.addEventListener('change', async () => {
      const r = await jfetch('/api/site/spotify/visibility', 'POST', { visibility: Number(visSel.value) });
      toast(r && r.ok ? 'Saved' : 'Could not save.');
    });

    const sq = new URLSearchParams(location.search).get('spotify');
    if (sq) {
      const msg = { linked: 'Spotify linked!', notallowed: 'Linked, but Spotify is not allowing this account yet — ask a Campus dev to add your Spotify email to the app.',
                    expired: 'Link timed out — try again.', denied: 'Spotify access was declined.',
                    inuse: 'That Spotify account is already linked to another player.',
                    unconfigured: 'Spotify linking is not set up on this server yet.', error: 'Could not link Spotify.' }[sq] || null;
      if (msg) toast(msg);
      document.querySelector('.snav[data-panel="services"]')?.click();
      history.replaceState(null, '', location.pathname);
    }
    let loaded = false;
    document.querySelector('.snav[data-panel="services"]')?.addEventListener('click', () => {
      if (!loaded) { loaded = true; load(); initSocialCards(); }
    });
  })();

  // Services: every linkable social as a card (sign in with the service -> verified handle),
  // each with its own "Show on my profile" switch; Discord and Spotify keep their own cards and
  // only get the switch. Data from /api/site/social/status.
  async function initSocialCards() {
    const host = document.getElementById('socialCards');
    if (!host || !apiOn()) return;
    let r = null;
    try { r = await apiGet('/api/site/social/status'); } catch {}
    if (!r || !r.ok) { host.innerHTML = '<p style="opacity:.7;margin-top:14px">Could not load your services.</p>'; return; }
    const cards = (r.providers || []).filter(p => p.oauth);
    host.innerHTML = cards.map(p => {
      const status = !p.configured ? 'Not set up on this server yet'
        : p.linked ? ('Connected as ' + (p.url ? '<a href="' + escapeHtml(p.url) + '" target="_blank" rel="noopener">' + escapeHtml(p.handle) + '</a>' : escapeHtml(p.handle)))
        : 'Not connected';
      return '<div class="discord-card svc-card svc-' + p.key + '" data-svc="' + p.key + '">'
        + '<div class="discord-logo" aria-hidden="true" style="color:#fff">' + (SOCIAL_ICONS[p.key] || '').replace('<svg ', '<svg width="26" height="26" ') + '</div>'
        + '<div class="discord-info"><div class="discord-title">' + escapeHtml(p.label) + '</div><div class="discord-status">' + status + '</div>'
        + (p.linked ? '<label class="svc-show" data-svc="' + p.key + '"><span>Show on my profile</span><span class="switch"><input type="checkbox" class="svc-show-toggle" data-svc="' + p.key + '"' + (p.show ? ' checked' : '') + '><span class="slider"></span></span></label>' : '')
        + '</div>'
        + (p.linked
            ? '<button class="discord-unlink-btn svc-unlink" data-svc="' + p.key + '">Unlink</button>'
            : '<button class="discord-link-btn svc-link" data-svc="' + p.key + '"' + (p.configured ? '' : ' disabled') + '>Link ' + escapeHtml(p.label) + '</button>')
        + '</div>';
    }).join('');
    // Discord / Spotify switches (their cards are static in the HTML)
    for (const p of (r.providers || []).filter(p => !p.oauth)) {
      const row = document.querySelector('.svc-show[data-svc="' + p.key + '"]');
      const box = row && row.querySelector('.svc-show-toggle');
      if (row) row.hidden = !p.linked;
      if (box) box.checked = !!p.show;
    }
    host.querySelectorAll('.svc-link').forEach(b => b.addEventListener('click', () => { window.location.href = '/api/site/social/' + b.dataset.svc + '/start'; }));
    host.querySelectorAll('.svc-unlink').forEach(b => b.addEventListener('click', async () => {
      const { ok } = await apiPost('/api/site/social/' + b.dataset.svc + '/unlink');
      if (!ok) { toast('Could not unlink.'); return; }
      toast('Unlinked'); initSocialCards();
    }));
    document.querySelectorAll('.svc-show-toggle').forEach(t => {
      if (t.dataset.wired) return; t.dataset.wired = '1';
      t.addEventListener('change', async () => {
        const { ok } = await apiPost('/api/site/social/' + t.dataset.svc + '/show', { show: t.checked });
        toast(ok ? (t.checked ? 'Shown on your profile' : 'Hidden from your profile') : 'Could not save.');
      });
    });
  }
  // Back from a service's sign-in: toast the outcome, open Services.
  const sq2 = new URLSearchParams(location.search).get('social');
  if (sq2) {
    const [prov, outcome] = sq2.split(':');
    const label = prov ? prov.charAt(0).toUpperCase() + prov.slice(1) : 'Service';
    const msg = { linked: label + ' linked!', expired: 'Link timed out — try again.', denied: label + ' access was declined.',
                  inuse: 'That ' + label + ' account is already linked to another player.',
                  unconfigured: label + ' linking is not set up on this server yet.', error: 'Could not link ' + label + '.' }[outcome] || null;
    if (msg) toast(msg);
    document.querySelector('.snav[data-panel="services"]')?.click();
    history.replaceState(null, '', location.pathname);
  }

  // save buttons — Profile now POSTs to the server (was localStorage-only, so @name / display
  // name / bio never reached the game or another browser). The server persists them and pushes
  // an AccountUpdate so the change shows in-game without a relog.
  // Email panel: load the account's current address, save it to the server.
  const acctEmail = document.getElementById('acctEmail');
  if (acctEmail && apiOn()) {
    (async () => {
      try { const r = await apiGet('/api/site/me'); if (r && r.ok) { acctEmail.value = r.email || ''; acctEmail.dataset.saved = r.email || ''; } } catch {}
    })();
  }

  document.querySelectorAll('.ssave').forEach(btn => {
    btn.addEventListener('click', async () => {
      if (btn.dataset.save === 'Email' && acctEmail && apiOn()) {
        const email = acctEmail.value.trim();
        if (email === (acctEmail.dataset.saved || '')) { toast('Email settings saved'); return; }
        btn.disabled = true;
        const { ok, data } = await apiPost('/api/site/email', { email });
        btn.disabled = false;
        if (!ok) { toast((data && data.error) || 'Could not save your email.'); return; }
        acctEmail.dataset.saved = data.email || '';
        toast(email ? 'Email saved' : 'Email removed');
        return;
      }
      if (btn.dataset.save === 'Password' && apiOn()) {
        const cur = document.getElementById('pwCur'), nw = document.getElementById('pwNew'), cf = document.getElementById('pwConf');
        if (!cur.value) { toast('Enter your current password.'); return; }
        if (nw.value.length < 6) { toast('Password must be at least 6 characters.'); return; }
        if (nw.value !== cf.value) { toast("The passwords don't match."); return; }
        btn.disabled = true;
        const { ok, data } = await apiPost('/api/site/password', { current_password: cur.value, new_password: nw.value, confirm_password: cf.value });
        btn.disabled = false;
        if (!ok) { toast((data && data.error) || 'Could not change your password.'); return; }
        cur.value = nw.value = cf.value = '';
        toast('Password changed. Use it next time you log in, on the website and in the game.');
        return;
      }
      if (btn.dataset.save !== 'Profile') { toast(`${btn.dataset.save} settings saved`); return; }

      const payload = {};
      if (username && username.value.trim()) payload.username = username.value.trim().replace(/[^a-zA-Z0-9_.]/g, '');
      if (nick && nick.value.trim()) payload.display_name = nick.value.trim();
      if (bio) payload.bio = bio.value.trim();   // bio can be cleared, so always send it
      if (!Object.keys(payload).length) { toast('Nothing to change'); return; }

      btn.disabled = true;
      const { ok, data } = await apiPost('/api/site/profile', payload);
      btn.disabled = false;
      if (!ok) { toast((data && data.error) || 'Could not save your profile.'); return; }

      // Reflect the SERVER's truth everywhere.
      if (data.username) localStorage.setItem('campus-username', data.username);
      if (data.display_name) localStorage.setItem('campus-mynick', data.display_name);
      localStorage.setItem('campus-mybio', data.bio || '');
      if (typeof CAMPUS_ME === 'object' && CAMPUS_ME) {
        if (data.username) CAMPUS_ME.username = data.username;
        if (data.display_name) CAMPUS_ME.display_name = data.display_name;
      }
      if (username) { username.value = ''; username.placeholder = data.username || myHandle(me); }
      if (nick) { nick.value = ''; nick.placeholder = data.display_name || myDisplayName(me); }
      if (suName) suName.textContent = data.display_name || myDisplayName(me);
      if (suHandle) suHandle.textContent = '@' + (data.username || myHandle(me));
      toast('Profile settings saved');
    });
  });

  // logout actions
  const logout = document.getElementById('setLogout');
  if (logout) logout.addEventListener('click', async () => { if (apiOn()) { try { await apiPost('/api/site/logout'); } catch {} } clearIdentityCache(); window.location.href = '/'; });
  const logoutAll = document.getElementById('logoutAll');
  if (logoutAll) logoutAll.addEventListener('click', async () => { if (apiOn()) { try { await apiPost('/api/site/logout'); } catch {} } clearIdentityCache(); window.location.href = '/'; });
}

// ───── THEME (light/dark + accent) ─────
const THEME_KEY = 'campus-theme';
const ACCENT_KEY = 'campus-accent';

function hexRgb(h) { h = h.replace('#', ''); return [0, 2, 4].map(i => parseInt(h.slice(i, i + 2), 16)); }
function darken(h, f) { const [r, g, b] = hexRgb(h); return `rgb(${r * f | 0},${g * f | 0},${b * f | 0})`; }
function softA(h, a) { const [r, g, b] = hexRgb(h); return `rgba(${r},${g},${b},${a})`; }

const THEMES = ['light', 'dark', 'autumn', 'halloween'];
function applyTheme(t) {
  if (!THEMES.includes(t)) t = 'light';
  document.documentElement.setAttribute('data-theme', t);
  document.querySelectorAll('.cd-mode').forEach(b => b.classList.toggle('active', b.dataset.theme === t));
  autumnLeaves(t === 'autumn');
  halloweenFx(t === 'halloween');
}

// ───── SEASONS ─────
// October is Halloween: the first visit of each October switches the site to the Halloween theme
// (once - pick another theme and it sticks). After October a Halloween theme goes back to dark.
const HALLOWEEN_SEEN_KEY = 'campus-halloween-seen';
function applySeason() {
  const now = new Date();
  const october = now.getMonth() === 9;
  let saved = null;
  try { saved = localStorage.getItem(THEME_KEY); } catch { }
  try {
    if (october && localStorage.getItem(HALLOWEEN_SEEN_KEY) !== String(now.getFullYear())) {
      localStorage.setItem(HALLOWEEN_SEEN_KEY, String(now.getFullYear()));
      localStorage.setItem(THEME_KEY, 'halloween');
      return 'halloween';
    }
    if (!october && saved === 'halloween') { localStorage.setItem(THEME_KEY, 'dark'); return 'dark'; }
  } catch { }
  return saved || 'light';
}

// ───── HALLOWEEN: cobwebs in the corners, spiders on threads and spiders crawling the page ─────
// Pure decoration in a fixed overlay under the top bar: pointer-events are off, so nothing ever
// blocks a click. Crawlers wander between random spots, pause, and scuttle away from the mouse.
// Reduce motion (site setting or the OS) keeps the cobwebs and drops every spider.
const WEB_SVG = '<svg viewBox="0 0 200 200" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round">'
  + '<path d="M0 0L200 200M0 0L200 92M0 0L92 200M0 0L200 22M0 0L22 200"/>'
  + [34, 68, 104, 142, 182].map(r => {
      const pts = [[r, 0], [r * .97, r * .11], [r * .9, r * .44], [r * .7, r * .7], [r * .44, r * .9], [r * .11, r * .97], [0, r]];
      let d = 'M' + pts[0].join(' ');
      for (let i = 1; i < pts.length; i++) {
        const a = pts[i - 1], b = pts[i];
        d += ' Q' + ((a[0] + b[0]) / 2 * .9).toFixed(1) + ' ' + ((a[1] + b[1]) / 2 * .9).toFixed(1) + ' ' + b[0].toFixed(1) + ' ' + b[1].toFixed(1);
      }
      return '<path d="' + d + '"/>';
    }).join('')
  + '</svg>';
const SPIDER_SVG = '<svg viewBox="0 0 40 40" aria-hidden="true">'
  + '<g fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">'
  + '<g class="legs-a"><path d="M16 17L9 11L5 5"/><path d="M15 23L7 23L2 27"/><path d="M24 20L32 18L37 13"/><path d="M25 26L31 31L33 37"/></g>'
  + '<g class="legs-b"><path d="M15 20L8 18L3 14"/><path d="M16 26L10 31L8 37"/><path d="M24 17L31 11L35 5"/><path d="M25 23L33 23L38 27"/></g>'
  + '</g>'
  + '<ellipse cx="20" cy="26" rx="6.5" ry="8" fill="currentColor"/>'
  + '<circle cx="20" cy="15.5" r="4.6" fill="currentColor"/>'
  + '<circle class="eye" cx="18.2" cy="13.8" r="1.1"/><circle class="eye" cx="21.8" cy="13.8" r="1.1"/>'
  + '</svg>';

let _hwRaf = 0, _hwBugs = [], _hwMouse = { x: -999, y: -999 }, _hwLast = 0, _hwBound = false;
function halloweenFx(on) {
  let fx = document.querySelector('.hw-fx');
  if (!on) { fx?.remove(); cancelAnimationFrame(_hwRaf); _hwRaf = 0; _hwBugs = []; return; }
  if (fx) return;
  fx = document.createElement('div'); fx.className = 'hw-fx'; fx.setAttribute('aria-hidden', 'true');
  fx.innerHTML = ['tl', 'tr', 'br', 'bl'].map(c => '<div class="hw-web ' + c + '">' + WEB_SVG + '</div>').join('');
  const phone = window.innerWidth < 640;
  const bare = !document.querySelector('.topbar');      // log in / sign up: no top bar - the overlay starts at the top and nothing dangles over the form
  if (bare) fx.classList.add('hw-bare');
  // spiders on threads
  for (let i = 0; i < (bare ? 0 : phone ? 1 : 3); i++) {
    const d = document.createElement('div'); d.className = 'hw-dangle';
    d.style.setProperty('--x', (12 + Math.random() * 76).toFixed(1) + '%');
    d.style.setProperty('--y', (90 + Math.random() * 220).toFixed(0) + 'px');
    d.style.setProperty('--d', (9 + Math.random() * 7).toFixed(1) + 's');
    d.style.setProperty('--delay', (-Math.random() * 12).toFixed(1) + 's');
    d.innerHTML = '<div class="hw-thread"></div><div class="hw-spider">' + SPIDER_SVG + '</div>';
    fx.appendChild(d);
  }
  document.body.appendChild(fx);
  // crawlers
  const W = () => fx.clientWidth, H = () => fx.clientHeight;
  _hwBugs = [];
  for (let i = 0; i < (phone ? 2 : 4); i++) {
    const el = document.createElement('div'); el.className = 'hw-spider walking'; el.innerHTML = SPIDER_SVG;
    const scale = .7 + Math.random() * .6;
    fx.appendChild(el);
    el.style.opacity = '0';   // placed on the first frame the overlay has a real size
    _hwBugs.push({ el, scale, placed: false, x: 0, y: 0, a: Math.random() * Math.PI * 2, tx: 0, ty: 0, speed: 0, pause: Math.random() * 2, flee: 0 });
  }
  if (!_hwBound) {
    _hwBound = true;
    window.addEventListener('pointermove', e => { _hwMouse.x = e.clientX; _hwMouse.y = e.clientY; }, { passive: true });
    document.addEventListener('visibilitychange', () => { _hwLast = 0; });
  }
  _hwLast = 0;
  const step = t => {
    _hwRaf = requestAnimationFrame(step);
    if (document.hidden) return;
    const dt = _hwLast ? Math.min(.05, (t - _hwLast) / 1000) : 0; _hwLast = t;
    const w = W(), h = H(), top = fx.getBoundingClientRect().top;
    for (const b of _hwBugs) {
      // A page that loads in a background tab has no size yet - wait for one before placing.
      if (!Number.isFinite(b.x) || !Number.isFinite(b.y)) b.placed = false;
      if (!b.placed) {
        if (w < 50 || h < 50) continue;
        b.x = 20 + Math.random() * (w - 40); b.y = 20 + Math.random() * (h - 40);
        b.placed = true; b.el.style.opacity = ''; hwRetarget(b, w, h);
      }
      // scuttle away from the cursor
      let mx = _hwMouse.x - b.x, my = (_hwMouse.y - top) - b.y, md = Math.hypot(mx, my);
      if (md < .5) { mx = Math.cos(b.a); my = Math.sin(b.a); md = 1; }   // cursor dead centre: no direction to flee, so just pick one
      if (md < 110 && b.flee <= 0) {
        b.flee = .9; b.pause = 0;
        b.tx = Math.max(10, Math.min(w - 10, b.x - mx / md * 220));
        b.ty = Math.max(10, Math.min(h - 10, b.y - my / md * 220));
        b.speed = 230 + Math.random() * 80;
      }
      if (b.flee > 0) b.flee -= dt;
      if (b.pause > 0) {
        b.pause -= dt; b.el.classList.remove('walking');
      } else {
        const dx = b.tx - b.x, dy = b.ty - b.y, dist = Math.hypot(dx, dy);
        if (dist < 4) { b.pause = Math.random() < .55 ? .6 + Math.random() * 2.4 : 0; hwRetarget(b, w, h); }
        else {
          // turn toward the target gradually, with a little wobble
          const want = Math.atan2(dy, dx);
          let da = want - b.a; while (da > Math.PI) da -= 2 * Math.PI; while (da < -Math.PI) da += 2 * Math.PI;
          b.a += da * Math.min(1, dt * 6) + (Math.random() - .5) * dt * 1.5;
          const v = b.speed * dt;
          b.x += Math.cos(b.a) * v; b.y += Math.sin(b.a) * v;
          b.el.classList.add('walking');
        }
      }
      b.x = Math.max(-20, Math.min(w + 20, b.x)); b.y = Math.max(-20, Math.min(h + 20, b.y));
      // the SVG faces up, so +90deg turns it to face its heading
      b.el.style.transform = 'translate(' + (b.x - 17).toFixed(1) + 'px,' + (b.y - 17).toFixed(1) + 'px) rotate(' + (b.a * 180 / Math.PI + 90).toFixed(1) + 'deg) scale(' + b.scale + ')';
    }
  };
  _hwRaf = requestAnimationFrame(step);
}
function hwRetarget(b, w, h) {
  // mostly short hops; now and then a long run across the screen
  const far = Math.random() < .3;
  const r = far ? 400 + Math.random() * 500 : 60 + Math.random() * 200, a = Math.random() * Math.PI * 2;
  b.tx = Math.max(15, Math.min(w - 15, b.x + Math.cos(a) * r));
  b.ty = Math.max(15, Math.min(h - 15, b.y + Math.sin(a) * r));
  b.speed = 40 + Math.random() * 70;
}
// Autumn: a few leaves drift down the home hero. Pure decoration; reduce-motion stops them via CSS.
function autumnLeaves(on) {
  const hero = document.querySelector('.home-hero');
  if (!hero) return;
  let box = hero.querySelector('.autumn-leaves');
  if (!on) { box?.remove(); return; }
  if (box) return;
  box = document.createElement('div'); box.className = 'autumn-leaves'; box.setAttribute('aria-hidden', 'true');
  const LEAF = '<svg viewBox="0 0 24 24"><path d="M20 3c-7 0-13 3-16 9 3-1 6-1 9 0-4 1-7 3-9 8 6-2 12-4 14-9 1-2.5 2-5 2-8z"/></svg>';
  for (let i = 0; i < 9; i++) {
    const l = document.createElement('span'); l.innerHTML = LEAF;
    l.style.setProperty('--x', (5 + Math.random() * 90).toFixed(1) + '%');
    l.style.setProperty('--d', (9 + Math.random() * 9).toFixed(1) + 's');
    l.style.setProperty('--delay', (-Math.random() * 14).toFixed(1) + 's');
    l.style.setProperty('--s', (.6 + Math.random() * .7).toFixed(2));
    l.style.setProperty('--c', ['#f08a3c', '#e0722e', '#c95a22', '#f2b04a', '#d94f5c'][i % 5]);
    box.appendChild(l);
  }
  hero.appendChild(box);
}
function applyAccent(c) {
  const s = document.documentElement.style;
  s.setProperty('--red', c);
  s.setProperty('--red-dark', darken(c, 0.82));
  s.setProperty('--red-soft', softA(c, 0.15));
  document.querySelectorAll('.cd-accent').forEach(b =>
    b.classList.toggle('active', b.dataset.accent.toLowerCase() === c.toLowerCase()));
}

// apply saved choices (runs on every page so theme persists)
applyTheme(applySeason());
const savedAccent = localStorage.getItem(ACCENT_KEY);
if (savedAccent) applyAccent(savedAccent);

document.querySelectorAll('.cd-mode').forEach(b =>
  b.addEventListener('click', () => { const t = b.dataset.theme; localStorage.setItem(THEME_KEY, t); applyTheme(t); syncThemeToggles(); }));
document.querySelectorAll('.cd-accent').forEach(b =>
  b.addEventListener('click', () => { const c = b.dataset.accent; localStorage.setItem(ACCENT_KEY, c); applyAccent(c); }));

// drawer Light/Dark toggle (under Discord)
const SUN_SVG = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/></svg>';
const MOON_SVG = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/></svg>';
const LEAF_SVG = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M20 3c-7 0-13 3-16 9 3-1 6-1 9 0-4 1-7 3-9 8 6-2 12-4 14-9 1-2.5 2-5 2-8z"/><path d="M4 20l9-9"/></svg>';
const PUMPKIN_SVG = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M12 7c-1.2-2-.4-3.6 1-4.2"/><path d="M12 7c-5 0-8 2.6-8 6.5S7 20 12 20s8-2.6 8-6.5S17 7 12 7z"/><path d="M9 7.6C7.6 9.6 7.6 17 9 19.4M15 7.6c1.4 2 1.4 9.4 0 11.8"/></svg>';
// The drawer button cycles light -> dark -> autumn -> halloween and always names the NEXT theme.
function nextTheme() {
  const cur = document.documentElement.getAttribute('data-theme') || 'light';
  return THEMES[(THEMES.indexOf(cur) + 1) % THEMES.length];
}
function syncThemeToggles() {
  const next = nextTheme();
  const label = { light: `${SUN_SVG}<span>Light mode</span>`, dark: `${MOON_SVG}<span>Dark mode</span>`, autumn: `${LEAF_SVG}<span>Autumn mode</span>`, halloween: `${PUMPKIN_SVG}<span>Halloween mode</span>` }[next];
  document.querySelectorAll('.cd-theme-toggle').forEach(btn => { btn.innerHTML = label; });
}
document.querySelectorAll('.cd-theme-toggle').forEach(btn => {
  btn.addEventListener('click', () => {
    const next = nextTheme();
    localStorage.setItem(THEME_KEY, next);
    applyTheme(next);
    syncThemeToggles();
  });
});
syncThemeToggles();

// ───── ACCESSIBILITY PREFERENCES ─────
const A11Y_KEY = 'campus-a11y';
// map each toggle key to the <html> data-attribute it controls
const A11Y_ATTRS = {
  motion:   'data-reduce-motion',
  text:     'data-large-text',
  contrast: 'data-high-contrast',
  links:    'data-underline-links',
  spacing:  'data-readable-spacing',
  dyslexia: 'data-dyslexia-font',
  focus:    'data-focus-rings',
  cursor:   'data-big-cursor',
  taptargets: 'data-tap-targets',
};
function getA11y() { try { return JSON.parse(localStorage.getItem(A11Y_KEY) || '{}'); } catch { return {}; } }
function applyA11y(prefs) {
  const root = document.documentElement;
  Object.keys(A11Y_ATTRS).forEach(k => root.toggleAttribute(A11Y_ATTRS[k], !!prefs[k]));
  document.querySelectorAll('.cd-a11y').forEach(btn => {
    const on = !!prefs[btn.dataset.a11y];
    btn.classList.toggle('active', on);
    btn.setAttribute('aria-pressed', on ? 'true' : 'false');
  });
}
applyA11y(getA11y());
document.querySelectorAll('.cd-a11y').forEach(btn => {
  btn.addEventListener('click', () => {
    const prefs = getA11y();
    const k = btn.dataset.a11y;
    prefs[k] = !prefs[k];
    localStorage.setItem(A11Y_KEY, JSON.stringify(prefs));
    applyA11y(prefs);
    syncToggleAll();
  });
});
const a11yReset = document.getElementById('a11yReset');
if (a11yReset) a11yReset.addEventListener('click', () => {
  localStorage.removeItem(A11Y_KEY);
  applyA11y({});
  syncToggleAll();
});

// "Enable all / Disable all" — flips every accessibility setting at once
const a11yToggleAll = document.getElementById('a11yToggleAll');
function allA11yOn() { const p = getA11y(); return Object.keys(A11Y_ATTRS).every(k => !!p[k]); }
function syncToggleAll() {
  if (a11yToggleAll) a11yToggleAll.textContent = allA11yOn() ? 'Disable all' : 'Enable all';
}
if (a11yToggleAll) {
  a11yToggleAll.addEventListener('click', () => {
    const turnOn = !allA11yOn();
    const prefs = {};
    Object.keys(A11Y_ATTRS).forEach(k => { prefs[k] = turnOn; });
    localStorage.setItem(A11Y_KEY, JSON.stringify(prefs));
    applyA11y(prefs);
    syncToggleAll();
  });
  syncToggleAll();
}

initSettings();

// ───── PHOTO FEED (API-driven) ─────
//
// Point this at your "recent photos" endpoint. It should return JSON like:
//   [ { image, user, handle, room, cheers, comments, takenAt }, ... ]
// where `takenAt` is an ISO date string. Leave blank to use the fallback below.
const PHOTOS_API = ''; // e.g. 'https://api.campus.net/v1/photos/recent'

const postList = document.getElementById('postList');

// gradient palette for the author avatars (by first letter, just for colour)
const AV = [
  ['#ff5a3c', '#ffb142'], ['#f0453f', '#ff8a5c'], ['#ff6b9d', '#ffc371'],
  ['#e84393', '#ff7f6e'], ['#c44569', '#ff7f50'], ['#d63031', '#ff7675'],
];
function avColor(name) { return AV[(name.charCodeAt(0)) % AV.length]; }
function avKey(seed) { return (seed || 'player').toLowerCase().replace(/[^a-z0-9]/g, ''); }
function avatarUrl(seed) {
  const key = avKey(seed);
  try {
    const pfp = JSON.parse(localStorage.getItem('campus-pfp-' + key) || 'null');
    if (pfp && pfp.image) return pfp.image; // use the user's chosen profile picture
  } catch {}
  return `https://api.dicebear.com/9.x/thumbs/svg?seed=${encodeURIComponent(key)}`;
}

// Push the chosen profile image to the Campus server, which decodes it, stores it under
// Data/Images/player_images and points the account row at it. The optimistic UI already
// updated; this makes it stick (and makes it visible in-game).
//
// The old JSON {imageUrl} fallback is gone with server.py: that asked the backend to go
// fetch a URL on the browser's behalf, which is a server-side request forgery primitive.
// The server only accepts bytes it was handed.
async function syncProfileImageToApi(imageUrl) {
  // Deliberately NOT gated on CAMPUS_ME: syncSession() is async, so a quick change right
  // after page load would race it and silently drop the upload. Let the server decide -
  // it answers 401 when there's no session and the catch below surfaces that.
  if (!apiOn()) return;   // offline demo mode

  try {
    const blob = await (await fetch(imageUrl)).blob();
    const form = new FormData();
    form.append('image', blob, 'profile.png');

    const resp = await fetch(apiUrl('/api/profile-image'), {
      method: 'POST',
      credentials: 'same-origin',   // carries the HttpOnly session cookie
      body: form,
    });

    const out = await resp.json().catch(() => ({}));
    if (banRefused(resp, out)) return;
    if (!resp.ok || !out.ok) {
      toast('Saved locally, but the server update failed');
      console.warn('[campus] profile image API error:', out.error || resp.status);
    }
  } catch (e) {
    toast('Saved locally, but the server was unreachable');
    console.warn('[campus] profile image API unreachable:', e);
  }
}

// update every avatar image for a given user, live
function refreshAvatars(key, src) {
  document.querySelectorAll(`img[data-av="${key}"]`).forEach(im => {
    im.src = src;
    im.style.objectFit = 'cover';
  });
}
// cross-tab: when the profile picture changes in another tab, update here too
window.addEventListener('storage', e => {
  if (!e.key || !e.key.startsWith('campus-pfp-')) return;
  const key = e.key.slice('campus-pfp-'.length);
  let src = null;
  try { const d = JSON.parse(e.newValue || 'null'); if (d && d.image) src = d.image; } catch {}
  if (!src) src = `https://api.dicebear.com/9.x/thumbs/svg?seed=${encodeURIComponent(key)}`;
  refreshAvatars(key, src);
});

// "2 minutes ago", "3 hours ago", "5 days ago" - the profile's last-online line.
function lastOnlineText(iso) {
  const s = Math.floor((Date.now() - new Date(iso)) / 1000);
  if (isNaN(s) || s < 0) return '';
  if (s < 60) return 'just now';
  const unit = (n, w) => `${n} ${w}${n === 1 ? '' : 's'} ago`;
  if (s < 3600) return unit(Math.floor(s / 60), 'minute');
  if (s < 86400) return unit(Math.floor(s / 3600), 'hour');
  if (s < 86400 * 30) return unit(Math.floor(s / 86400), 'day');
  if (s < 86400 * 365) return unit(Math.floor(s / (86400 * 30)), 'month');
  return unit(Math.floor(s / (86400 * 365)), 'year');
}
function timeAgo(iso) {
  const s = Math.floor((Date.now() - new Date(iso)) / 1000);
  if (isNaN(s)) return '';
  if (s < 60) return s + 's';
  if (s < 3600) return Math.floor(s / 60) + 'm';
  if (s < 86400) return Math.floor(s / 3600) + 'h';
  return Math.floor(s / 86400) + 'd';
}
function fmt(n) { return n >= 1000 ? (n / 1000).toFixed(1).replace('.0', '') + 'k' : n; }

const HEART = '<svg viewBox="0 0 16 16" fill="none"><path d="M8 14s6-3.7 6-8a3 3 0 0 0-6-1.2A3 3 0 0 0 2 6c0 4.3 6 8 6 8Z" stroke="currentColor" stroke-width="1.5"/></svg>';
const CHEER_ICON = '<svg class="cheer-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M7 10v11H3V10zM7 10l4.2-6.6a1.6 1.6 0 0 1 2.9 1.2L13.4 9H19a2 2 0 0 1 2 2.4l-1.5 7.5A2.5 2.5 0 0 1 17 21H7"/></svg>';
const CHAT  = '<svg viewBox="0 0 16 16" fill="none"><path d="M3 4h10v7H6l-3 2.5V4Z" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round"/></svg>';

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

// basic profanity filter
const BANNED = ['fuck','shit','bitch','asshole','bastard','cunt','dick','piss','slut','whore','nigger','nigga','faggot','retard','rape','cock','pussy','wanker','prick','twat'];
function isInappropriate(text) {
  const t = text.toLowerCase();
  const stripped = t.replace(/[^a-z]/g, ''); // catches sp4ced / punctuated evasions
  return BANNED.some(w => new RegExp(`\\b${w}\\b`, 'i').test(t) || stripped.includes(w));
}
function commentRow(c) {
  const me = localStorage.getItem(USER_KEY);
  const mine = !!c.mine || (me && me.toLowerCase() === (c.handle || c.user || '').toLowerCase());
  const action = mine
    ? `<button class="comment-del" title="Delete comment" aria-label="Delete comment"><svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><path d="M2.5 4h11M6 4V2.7h4V4M5 4l.4 8.4a1 1 0 0 0 1 .9h3.2a1 1 0 0 0 1-.9L11 4"/></svg></button>`
    : `<button class="comment-report" title="Report comment" aria-label="Report comment"><svg viewBox="0 0 16 16" fill="none"><path d="M3 14V3M3 3h8l-1.5 3L11 9H3" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"/></svg></button>`;
  const h = String(c.handle || c.user || 'player').replace(/^@/, '');
  return `<div class="comment"${c.id != null ? ` data-cid="${c.id}"` : ''}>
    <a class="comment-av-link" href="${userUrl(h)}" aria-label="@${escapeHtml(h)}"><img class="comment-av" data-av="${avKey(h)}" src="${c.avatar || avatarUrl(h)}" alt=""></a>
    <div class="comment-body"><a class="comment-user" href="${userUrl(h)}">@${escapeHtml(h)}</a> ${escapeHtml(c.text)}</div>
    ${action}
  </div>`;
}
function serverComment(c) {
  const me = (localStorage.getItem(USER_KEY) || '').toLowerCase();
  return { id: c.id, user: c.display_name || c.username, handle: c.username, avatar: c.profile_image, text: c.text,
           mine: (c.username || '').toLowerCase() === me };
}
async function loadCommentsInto(box, photoId) {
  const list = box.querySelector('.comment-list');
  if (!list || !photoId || !apiOn()) return;
  const d = await apiGet('/api/site/photos/' + photoId + '/comments');
  if (!d || !d.ok) return;
  const rows = (d.comments || []).map(serverComment);
  list.innerHTML = rows.map(commentRow).join('') || '<p class="no-comments">No comments yet - be the first!</p>';
  list.dataset.loaded = '1';
}
function commentsBlock(p) {
  const me = localStorage.getItem(USER_KEY);
  if (!me) {
    return `<div class="post-comments" hidden><a class="comment-login" href="/login">Log in to view and add comments</a></div>`;
  }
  const list = (p.thread || []).map(commentRow).join('') || '<p class="no-comments">No comments yet - be the first!</p>';
  return `<div class="post-comments" hidden>
    <div class="comment-list">${list}</div>
    <form class="comment-form">
      <img class="comment-av" data-av="${avKey(me)}" src="${avatarUrl(me)}" alt="">
      <input class="comment-input" type="text" placeholder="Add a comment…" maxlength="200" autocomplete="off">
      <button type="submit" class="comment-send">Post</button>
    </form>
    <p class="comment-error"></p>
  </div>`;
}

function renderPosts(posts) {
  if (!postList) return;
  if (!posts.length) { postList.innerHTML = '<div class="feed-loading">No photos yet - check back soon.</div>'; return; }
  postList.innerHTML = posts.map(p => {
    const [a, b] = avColor(p.user || '?');
    const ph = (p.handle || p.user || 'player');
    const av = p.avatar || avatarUrl(ph);   // real profile_image when the API provides one
    return `<article class="post"${p.id != null && /^\d+$/.test(String(p.id)) ? ` data-photo="${p.id}"` : ''}>
      <div class="post-head">
        <a class="post-av" href="${userUrl(ph)}" style="--a:${a};--b:${b}"><img data-av="${avKey(ph)}" src="${av}" alt="" onerror="this.remove()">${(p.user || '?')[0].toUpperCase()}</a>
        <div class="post-meta">
          <a class="post-user" href="${userUrl(ph)}">${escapeHtml(p.user || 'Anonymous')}</a>
          <span class="post-handle">@${escapeHtml(p.handle || 'player')}${p.room ? ` in <a href="#">^${escapeHtml(p.room)}</a>` : ''}</span>
        </div>
        <span class="post-time">${p.takenAt ? timeAgo(p.takenAt) : (p.time || '')}</span>
      </div>
      <a class="post-img" href="${imageUrl(p.id || '')}" style="background-image:url('${p.image}')"></a>
      <div class="post-foot">
        <span class="post-cap">${p.caption || ''}</span>
        <div class="post-stats">
          <button class="pstat cheer${p.cheered ? ' liked' : ''}" data-n="${(p.cheers || 0) - (p.cheered ? 1 : 0)}">${CHEER_ICON}<span>${fmt(p.cheers || 0)}</span></button>
          <button class="pstat comment-toggle" data-n="${nComments(p)}">${CHAT}<span>${nComments(p)}</span></button>
        </div>
      </div>
      ${commentsBlock(p)}
    </article>`;
  }).join('');
}

// Comment count: the live feed sends a number, the demo data carries a thread array.
function nComments(p) {
  return (typeof p.comments === 'number') ? p.comments : (p.thread || []).length;
}

// Fallback content used until the API is wired up. Each photo is a different
// crop/zoom of the game art so the feed doesn't look like one repeated image.
const FALLBACK_POSTS = [
  { id: 'clutch-round',   user: 'Clawful',     handle: 'clawful',     room: 'Paws Paintball', title: 'Clutch round on Paws Paintball 🎯', caption: 'gg everyone 🎯',          cheers: 1284, comments: 86,  takenAt: minsAgo(4),   crop: '8% 42% / 220%' },
  { id: 'midnight-build', user: 'Velvetpaw',   handle: 'velvetpaw',   room: 'Midnight Mews',  title: 'Late night build session 🌙',       caption: 'late night session 🌙',   cheers: 842,  comments: 37,  takenAt: minsAgo(22),  crop: '38% 50% / 240%' },
  { id: 'contraption',    user: 'Tinker9',     handle: 'tinker9',     room: 'Litterbox Lab',  title: 'New contraption just dropped',      caption: 'new contraption just dropped', cheers: 2613, comments: 158, takenAt: minsAgo(63),  crop: '66% 55% / 230%' },
  { id: 'final-boss',     user: 'Stitchwitch', handle: 'stitchwitch', room: 'Yarn Quest',     title: 'Finally beat the final boss 🧶',     caption: 'finally beat the final boss 🧶', cheers: 4097, comments: 289, takenAt: minsAgo(190), crop: '94% 48% / 250%' },
];
function minsAgo(m) { return new Date(Date.now() - m * 60000).toISOString(); }

// look up a photo by id (feed photos, or profile photos encoded as "user__letter")
function getPhoto(id) {
  if (!id) return null;
  const feed = FALLBACK_POSTS.find(p => p.id === id);
  if (feed) {
    const [pos, size] = feed.crop.split(' / ');
    return { user: feed.user, handle: feed.handle, image: 'assets/hero-bg.jpg', pos, size, title: feed.title, takenAt: feed.takenAt, cheers: feed.cheers };
  }
  if (id.includes('__')) {
    const [u, letter] = id.split('__');
    const up = (USER_PHOTOS[u] || []).find(x => x.id === letter);
    if (up) return { user: cap(u), handle: u, image: up.image, pos: 'center', size: 'cover', title: up.title, takenAt: null, cheers: 0 };
    const pp = PROFILE_PHOTOS.find(x => x.id === letter);
    if (pp) return { user: cap(u), handle: u, image: 'assets/hero-bg.jpg', pos: pp.pos, size: pp.size, title: 'Untitled photo', takenAt: null, cheers: 0 };
  }
  return null;
}

// special renderer for the fallback so we can apply the per-post crop
function renderFallback(posts) {
  if (!postList) return;
  postList.innerHTML = posts.map(p => {
    const [a, b] = avColor(p.user);
    const [pos, size] = p.crop.split(' / ');
    return `<article class="post">
      <div class="post-head">
        <a class="post-av" href="${userUrl(p.handle)}" style="--a:${a};--b:${b}"><img data-av="${avKey(p.handle)}" src="${avatarUrl(p.handle)}" alt="" onerror="this.remove()">${p.user[0]}</a>
        <div class="post-meta">
          <a class="post-user" href="${userUrl(p.handle)}">${p.user}</a>
          <span class="post-handle">@${p.handle} in <a href="#">^${p.room}</a></span>
        </div>
        <span class="post-time">${timeAgo(p.takenAt)}</span>
      </div>
      <a class="post-img" href="${imageUrl(p.id)}" style="background-image:url('assets/hero-bg.jpg');background-position:${pos};background-size:${size}"></a>
      <div class="post-foot">
        <span class="post-cap">${p.caption}</span>
        <div class="post-stats">
          <button class="pstat cheer" data-n="${p.cheers}">${CHEER_ICON}<span>${fmt(p.cheers)}</span></button>
          <button class="pstat comment-toggle" data-n="${(p.thread || []).length}">${CHAT}<span>${(p.thread || []).length}</span></button>
        </div>
      </div>
      ${commentsBlock(p)}
    </article>`;
  }).join('');
}

async function loadFeed() {
  if (!postList) return;
  // Real, newest-first photos from the Campus server (/api/site/photos/recent is Public +
  // ShareCamera + sorted newest-first). Falls back to the demo posts only when the API is off
  // or unreachable.
  if (apiOn()) {
    const data = await apiGet('/api/site/photos/recent?take=24');
    if (data && data.ok && Array.isArray(data.results)) {
      const posts = data.results.map(r => ({
        id: r.id,
        image: r.image,
        user: (r.author && (r.author.display_name || r.author.username)) || 'Player',
        handle: (r.author && r.author.username) || 'player',
        avatar: r.author && r.author.profile_image,
        caption: r.description || '',
        cheers: r.cheers || 0,
        cheered: !!r.cheered,
        comments: r.comments || 0,
        takenAt: r.createdAt,
      }));
      renderPosts(posts);
      return;
    }
  }
  renderFallback(FALLBACK_POSTS);
}
loadFeed();
renderImage(); // image.html - runs here so FALLBACK_POSTS/getPhoto are ready
// NOTE: renderOnlineFriends() is intentionally NOT called - hydrateOnline() (below) is the real
// one. It shows only YOUR online friends when signed in (not every online player), and writes the
// same .rail-online section, so calling both produced a duplicate "Online Friends" list.
renderPinned(); // home - pinned friends widget
initFeaturedSlideshow(); // home - featured rooms slideshow (demo first, then real below)

// Featured Rooms slideshow: shows the SAME admin-curated featured rooms the game shows
// (/api/site/rooms/featured). Falls back to hot rooms only when nothing is featured yet, so the
// showcase is never empty. Change what's featured in the admin panel and it updates both here
// and in-game.
(async function loadFeatured() {
  if (!apiOn() || !document.getElementById('featuredSlideshow')) return;
  const map = r => ({
    id: r.id, name: r.name, creator: r.creator || 'someone',
    image: r.image, players: r.players || 0, visits: r.visits || 0,
  });

  let data = await apiGet('/api/site/rooms/featured?take=8');
  if (data && data.ok && Array.isArray(data.results) && data.results.length) {
    initFeaturedSlideshow(data.results.map(map));
    return;
  }
  // Nothing featured — fall back to the hottest rooms so the showcase still has content.
  data = await apiGet('/api/site/rooms/hot?take=6');
  if (data && data.ok && Array.isArray(data.results) && data.results.length) {
    initFeaturedSlideshow(data.results.map(map));
  }
})();

// ───── SEARCH PANEL ─────
const searchBtn = document.getElementById('searchBtn');
const searchPanel = document.getElementById('searchPanel');
const searchScrim = document.getElementById('searchScrim');
const searchInput = document.getElementById('searchInput');
const searchResults = document.getElementById('searchResults');

const PEOPLE = [
  { name: 'Clawful',     sub: '@clawful · 12.4k subscribers',   a: '#ff5a3c', b: '#ffb142' },
  { name: 'Velvetpaw',   sub: '@velvetpaw · 8.1k subscribers',  a: '#6c5ce7', b: '#a29bfe' },
  { name: 'Tinker9',     sub: '@tinker9 · 3.3k subscribers',    a: '#00b894', b: '#55efc4' },
  { name: 'Stitchwitch', sub: '@stitchwitch · 21k subscribers', a: '#e84393', b: '#fd79a8' },
  { name: 'Bubbles',     sub: '@bubbles · 970 subscribers',     a: '#0984e3', b: '#74b9ff' },
  { name: 'SpookyTom',   sub: '@spooky.tom · 45k subscribers',  a: '#d63031', b: '#ff7675' },
  { name: 'Mittens',     sub: '@mittens · 1.2k subscribers',    a: '#fdcb6e', b: '#ff7675' },
  { name: 'Pounce',      sub: '@pounce · 660 subscribers',      a: '#00cec9', b: '#81ecec' },
];

const ROOMS = [
  { name: 'Whisker Heights',   sub: 'by @clawful · 67k visits',     a: '#ff5a3c', b: '#7b2ff7' },
  { name: 'Paws Paintball',    sub: 'by @clawful · 48k visits',     a: '#ff5a3c', b: '#ffb142' },
  { name: 'Midnight Mews',     sub: 'by @velvetpaw · 31k visits',   a: '#6c5ce7', b: '#a29bfe' },
  { name: 'The Litterbox Lab', sub: 'by @tinker9 · 22k visits',     a: '#00b894', b: '#55efc4' },
  { name: 'Yarn Quest',        sub: 'by @stitchwitch · 67k visits', a: '#e84393', b: '#fd79a8' },
  { name: 'Deep Dive Reef',    sub: 'by @bubbles · 19k visits',     a: '#0984e3', b: '#74b9ff' },
  { name: 'Haunted Cattery',   sub: 'by @spooky.tom · 91k visits',  a: '#d63031', b: '#ff7675' },
];

let activeTab = 'people';

function searchRow(m) {
  if (activeTab === 'people') {
    const handle = m.handle || m.name.toLowerCase();
    const av = m.avatar || avatarUrl(handle);
    return `<a href="${userUrl(handle)}" class="sresult">
      <div class="sresult-avatar" style="--a:${m.a};--b:${m.b};position:relative;overflow:hidden"><img data-av="${avKey(handle)}" src="${av}" alt="" onerror="this.remove()">${escapeHtml(m.name[0] || '?')}</div>
      <div class="sresult-text"><span class="sresult-name">${escapeHtml(m.name)}</span><span class="sresult-sub">${escapeHtml(m.sub)}</span></div>
      <svg class="sresult-arrow" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="m6 4 4 4-4 4"/></svg>
    </a>`;
  }
  // Link rooms by NAME (how they're shared/searched), falling back to the id. The server
  // resolves /room/{slug} and /api/site/rooms/{slug} by name first.
  const roomSlug = m.name || m.roomId;
  const href = roomSlug ? `room.html?id=${encodeURIComponent(roomSlug)}` : '#';
  const thumbStyle = m.img
    ? `background-image:url('${m.img}');background-size:cover;background-position:center`
    : `--a:${m.a};--b:${m.b}`;
  return `<a href="${href}" class="sresult">
    <div class="sresult-thumb" style="${thumbStyle}"></div>
    <div class="sresult-text"><span class="sresult-name">${escapeHtml(m.name)}</span><span class="sresult-sub">${escapeHtml(m.sub)}</span></div>
    <svg class="sresult-arrow" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="m6 4 4 4-4 4"/></svg>
  </a>`;
}

// Empty/idle state only. Real results come from liveSearch(); there are no demo placeholders.
function renderResults() {
  if (!searchResults) return;
  const q = searchInput.value.trim();
  if (searchClear) searchClear.style.display = q ? 'flex' : 'none';
  if (!q) {
    searchResults.innerHTML = `<p class="search-empty">Start typing to search ${activeTab === 'people' ? 'players' : 'rooms'}.</p>`;
    return;
  }
  searchResults.innerHTML = apiOn()
    ? '<p class="search-empty">Searching…</p>'
    : '<p class="search-empty">Search is unavailable right now.</p>';
}

// normalise a raw API result object into the shape searchRow() expects
function normalizeResult(r) {
  const imgBase = (window.CAMPUS_DATA && window.CAMPUS_DATA.imgBase) || '/img/';
  const toUrl = raw => raw ? (/^https?:|^\//.test(raw) ? raw : imgBase + raw) : '';
  if (activeTab === 'people') {
    // The site search returns camelCase: { accountId, username, displayName, profileImage }.
    // (This used to read PascalCase, so every real player came back as "Unknown".)
    const handle = r.username || '';
    const name = r.displayName || handle || 'Unknown';
    return { name, sub: handle ? '@' + handle : '', handle, accountId: r.accountId, avatar: toUrl(r.profileImage), a: '#ff5a3c', b: '#ffb142' };
  }
  // Rooms come back PascalCase: { RoomId, Name, ImageName, Stats:{ VisitCount } }.
  const name = r.Name || 'Untitled room';
  const st = r.Stats || {};
  const sub = st.VisitCount != null ? fmt(st.VisitCount) + ' visits' : '';
  return { name, sub, roomId: r.RoomId, img: toUrl(r.ImageName), a: '#ff5a3c', b: '#7b2ff7' };
}

// live search against the Campus server (SiteController reads RoomDB/PlayerDB directly)
let searchTimer = null;
async function liveSearch(q) {
  const path = activeTab === 'people'
    ? `/api/search/players?name=${encodeURIComponent(q)}`
    : `/api/search/rooms?query=${encodeURIComponent(q)}`;
  try {
    const resp = await fetch(apiUrl(path), { credentials: 'same-origin' });
    const out = await resp.json();
    if (!out.ok) throw new Error(out.error || 'search failed');
    let list = out.results;
    if (list && !Array.isArray(list)) list = list.Results || list.results || list.Accounts || list.Rooms || [];
    list = (list || []).map(normalizeResult);
    if (!list.length) { searchResults.innerHTML = '<p class="search-empty">No results found</p>'; return; }
    searchResults.innerHTML = list.map(searchRow).join('');
  } catch (e) {
    console.warn('[campus] live search failed:', e);
    searchResults.innerHTML = '<p class="search-empty">Search failed - try again.</p>';
  }
}

// Real search only - no demo fallback.
function handleSearchInput() {
  const q = searchInput.value.trim();
  if (searchClear) searchClear.style.display = q ? 'flex' : 'none';
  clearTimeout(searchTimer);
  if (!q) { renderResults(); return; }                 // empty -> hint
  if (!apiOn()) { searchResults.innerHTML = '<p class="search-empty">Search is unavailable right now.</p>'; return; }
  searchResults.innerHTML = '<p class="search-empty">Searching…</p>';
  searchTimer = setTimeout(() => liveSearch(q), 250);
}

function openSearch() {
  searchPanel.classList.add('open');
  searchPanel.inert = false;
  searchScrim.classList.add('open');
  document.body.style.overflow = 'hidden';
  renderResults();
  setTimeout(() => searchInput.focus(), 50);
}
function closeSearch() {
  const wasOpen = searchPanel.classList.contains('open');
  searchPanel.classList.remove('open');
  searchPanel.inert = true;
  searchScrim.classList.remove('open');
  document.body.style.overflow = '';
  if (wasOpen && searchBtn) searchBtn.focus(); // return focus to the trigger
}

let searchClear = null;

if (searchPanel) searchPanel.inert = !searchPanel.classList.contains('open'); // closed by default
if (searchBtn) {
  // inject a clear (✕) button into the search field
  const field = searchInput.parentElement;
  if (field) {
    searchClear = document.createElement('button');
    searchClear.className = 'search-clear';
    searchClear.type = 'button';
    searchClear.setAttribute('aria-label', 'Clear');
    searchClear.innerHTML = '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M4 4l8 8M12 4l-8 8"/></svg>';
    searchClear.style.display = 'none';
    field.appendChild(searchClear);
    searchClear.addEventListener('click', () => { searchInput.value = ''; handleSearchInput(); searchInput.focus(); });
  }

  searchBtn.addEventListener('click', () => {
    searchPanel.classList.contains('open') ? closeSearch() : openSearch();
  });
  searchScrim.addEventListener('click', closeSearch);
  searchInput.addEventListener('input', handleSearchInput);

  document.querySelectorAll('.stab').forEach(tab => {
    tab.addEventListener('click', () => {
      document.querySelector('.stab.active')?.classList.remove('active');
      tab.classList.add('active');
      activeTab = tab.dataset.tab;
      searchInput.placeholder = activeTab === 'people' ? 'Search people…' : 'Search rooms…';
      handleSearchInput();
    });
  });

  document.addEventListener('keydown', e => { if (e.key === 'Escape') closeSearch(); });

  // click the dimmed background (outside the card) to close
  searchPanel.addEventListener('click', e => {
    if (e.target === searchPanel) closeSearch();
  });
}

// ───── FAQ ACCORDION ─────
document.querySelectorAll('.dl-faq .faq-q').forEach(q => {
  q.addEventListener('click', () => q.closest('.faq-item').classList.toggle('open'));
});

// ───── RATE LIMITING + TOAST ─────
// returns true if the action is allowed, false if the limit is hit
function rateLimit(action, max, windowMs) {
  const key = 'campus-rl-' + action;
  const now = Date.now();
  let arr = [];
  try { arr = JSON.parse(localStorage.getItem(key) || '[]'); } catch { arr = []; }
  arr = arr.filter(t => now - t < windowMs);
  if (arr.length >= max) { localStorage.setItem(key, JSON.stringify(arr)); return false; }
  arr.push(now);
  localStorage.setItem(key, JSON.stringify(arr));
  return true;
}

// ───── BAN BANNER ─────
// A banned account can sign in but can't change anything (the server refuses its writes). Every
// page shows this strip under the top bar: how long is left (live) and the reason. /api/site/me
// carries it as user.ban = { permanent, remaining_seconds, reason } - null when not banned.
function banLeftText(sec) {
  sec = Math.max(0, Math.floor(sec));
  const d = Math.floor(sec / 86400), h = Math.floor(sec % 86400 / 3600), m = Math.floor(sec % 3600 / 60), s = sec % 60;
  const parts = [];
  if (d) parts.push(d + (d === 1 ? ' day' : ' days'));
  if (d || h) parts.push(h + (h === 1 ? ' hour' : ' hours'));
  if (!d) parts.push(m + (m === 1 ? ' minute' : ' minutes'));
  if (!d && !h) parts.push(s + (s === 1 ? ' second' : ' seconds'));
  return parts.join(' ');
}
function renderBanBanner(me) {
  const ban = me && me.ban;
  const old = document.getElementById('siteBanBanner');
  if (!ban) { if (old) old.remove(); document.body.classList.remove('site-banned'); document.body.style.removeProperty('--ban-h'); return; }
  const bar = old || document.createElement('div');
  bar.id = 'siteBanBanner';
  bar.className = 'site-ban-banner';
  bar.setAttribute('role', 'alert');
  bar.innerHTML = '<span class="sbb-icon" aria-hidden="true">&#9888;</span>'
    + '<span class="sbb-text"><strong>This account is currently banned</strong> <span class="sbb-left"></span>'
    + '<span class="sbb-reason"></span><span class="sbb-note">You can look around, but you can\'t change anything until the ban ends. Think it\'s a mistake? <a href="/support?new=appeal">Appeal it</a>.</span></span>';
  bar.querySelector('.sbb-reason').textContent = ban.reason ? 'Reason: ' + ban.reason : '';
  if (!old) document.body.insertBefore(bar, document.body.firstChild);
  document.body.classList.add('site-banned');
  const left = bar.querySelector('.sbb-left');
  const ends = ban.permanent ? null : Date.now() + (ban.remaining_seconds || 0) * 1000;
  const tick = () => {
    if (!ends) { left.textContent = '- permanently'; return; }
    const sec = (ends - Date.now()) / 1000;
    if (sec <= 0) { left.textContent = '- the ban has ended, refresh the page'; clearInterval(bar._timer); return; }
    left.textContent = '- ' + banLeftText(sec) + ' left';
  };
  clearInterval(bar._timer);
  tick();
  bar._timer = setInterval(tick, 1000);
  // Push the page down by the banner's real height (it wraps on phones).
  const fit = () => document.body.style.setProperty('--ban-h', bar.offsetHeight + 'px');
  fit();
  window.addEventListener('resize', fit);
}

function toast(msg) {
  let t = document.getElementById('toast');
  if (!t) { t = document.createElement('div'); t.id = 'toast'; t.className = 'toast'; document.body.appendChild(t); }
  t.textContent = msg;
  t.classList.add('show');
  clearTimeout(t._timer);
  t._timer = setTimeout(() => t.classList.remove('show'), 2800);
}

// returns true if logged in; otherwise prompts to log in
function requireLogin(action) {
  if (localStorage.getItem(USER_KEY)) return true;
  toast(`Log in to ${action}`);
  return false;
}

// ───── REPORT SYSTEM ─────
const REPORT_REASONS = ['Spam or scam', 'Harassment or bullying', 'Inappropriate content', 'Hate speech', 'Impersonation', 'Other'];

function closeReport() {
  document.getElementById('reportModal')?.classList.remove('open');
  document.getElementById('reportScrim')?.classList.remove('open');
  document.body.style.overflow = '';
}

// ctx = {type: 'player'|'photo'|'comment', id} - what is being reported. With the API on,
// the report is filed to the server (it lands in the admin panel's Reports queue); the
// onSubmit callback only updates the button that opened it.
function openReport(title, onSubmit, ctx) {
  if (ctx && apiOn() && !localStorage.getItem(USER_KEY)) { toast('Log in to send a report'); return; }
  let scrim = document.getElementById('reportScrim');
  let modal = document.getElementById('reportModal');
  if (!modal) {
    scrim = document.createElement('div'); scrim.id = 'reportScrim'; scrim.className = 'report-scrim';
    modal = document.createElement('div'); modal.id = 'reportModal'; modal.className = 'report-modal';
    document.body.appendChild(scrim); document.body.appendChild(modal);
    scrim.addEventListener('click', closeReport);
    // click the dimmed area outside the card to close
    modal.addEventListener('click', e => { if (e.target === modal) closeReport(); });
  }

  modal.innerHTML = `
    <div class="report-card">
      <button class="report-close" aria-label="Close">✕</button>
      <h2 class="report-title">${title}</h2>
      <p class="report-sub">Tell us what's wrong. Reports are anonymous.</p>
      <div class="report-reasons">
        ${REPORT_REASONS.map(r => `<button type="button" class="report-reason" data-r="${r}">${r}</button>`).join('')}
      </div>
      <textarea class="report-details" placeholder="Add more details (optional)" maxlength="300"></textarea>
      <div class="report-actions">
        <button class="report-cancel">Cancel</button>
        <button class="report-submit" disabled>Submit report</button>
      </div>
    </div>`;

  const card = modal.querySelector('.report-card');
  let selected = null;
  card.querySelector('.report-reasons').addEventListener('click', e => {
    const b = e.target.closest('.report-reason');
    if (!b) return;
    card.querySelectorAll('.report-reason').forEach(x => x.classList.remove('sel'));
    b.classList.add('sel');
    selected = b.dataset.r;
    card.querySelector('.report-submit').disabled = false;
  });
  card.querySelector('.report-close').addEventListener('click', closeReport);
  card.querySelector('.report-cancel').addEventListener('click', closeReport);
  card.querySelector('.report-submit').addEventListener('click', async () => {
    if (!rateLimit('report', 5, 10 * 60 * 1000)) {
      toast('You\'ve sent too many reports. Please try again later.');
      closeReport();
      return;
    }
    const submit = card.querySelector('.report-submit');
    if (ctx && apiOn()) {
      submit.disabled = true; submit.textContent = 'Sending…';
      const r = await apiPost('/api/site/report', { type: ctx.type, id: String(ctx.id), photoId: ctx.photoId ? Number(ctx.photoId) : null, reason: selected, details: card.querySelector('.report-details').value.trim() });
      if (!r || !r.ok) {
        submit.disabled = false; submit.textContent = 'Submit report';
        toast((r && r.data && r.data.error) || 'Could not send the report');
        return;
      }
    }
    if (typeof onSubmit === 'function') onSubmit(selected);
    card.innerHTML = `
      <div class="report-done">
        <div class="report-check"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12.5l4.5 4.5L19 7"/></svg></div>
        <h2 class="report-title">Report submitted</h2>
        <p class="report-sub">Thanks - our moderation team will review this shortly.</p>
        <button class="report-submit report-ok">Done</button>
      </div>`;
    card.querySelector('.report-ok').addEventListener('click', closeReport);
  });

  modal.classList.add('open');
  scrim.classList.add('open');
  document.body.style.overflow = 'hidden';
}

// ───── NICKNAME MODAL ─────
function closeNick() {
  document.getElementById('nickModal')?.classList.remove('open');
  document.getElementById('nickScrim')?.classList.remove('open');
  document.body.style.overflow = '';
}
function openNickname(handle, onChange, ctx) {
  let scrim = document.getElementById('nickScrim');
  let modal = document.getElementById('nickModal');
  if (!modal) {
    scrim = document.createElement('div'); scrim.id = 'nickScrim'; scrim.className = 'report-scrim';
    modal = document.createElement('div'); modal.id = 'nickModal'; modal.className = 'report-modal';
    document.body.appendChild(scrim); document.body.appendChild(modal);
    scrim.addEventListener('click', closeNick);
    modal.addEventListener('click', e => { if (e.target === modal) closeNick(); });
  }
  const current = ctx ? (ctx.current || '') : ((getFriends()[handle] || {}).nickname || '');
  modal.innerHTML = `
    <div class="report-card">
      <button class="report-close" aria-label="Close">✕</button>
      <h2 class="report-title">Nickname</h2>
      <p class="report-sub">Set a custom nickname for @${handle}. You will see it on the website and in the game. Nobody else will - it never changes their real name.</p>
      <input class="nick-input" type="text" maxlength="24" placeholder="Enter a nickname" value="${escapeHtml(current)}">
      <div class="report-actions">
        <button class="report-cancel nick-remove">Remove nickname</button>
        <button class="report-submit nick-set">Set nickname</button>
      </div>
    </div>`;
  const card = modal.querySelector('.report-card');
  const input = card.querySelector('.nick-input');
  card.querySelector('.report-close').addEventListener('click', closeNick);
  async function apply(value) {
    if (ctx) {
      const { ok, data } = await apiPost('/api/site/friends/nickname', { id: ctx.id, nickname: value });
      if (!ok) { toast((data && data.error) || 'Could not save the nickname'); return; }
      if (ctx.onSaved) ctx.onSaved(data.nickname || '');
      toast(data.nickname ? `Nickname set to ${data.nickname}` : 'Nickname removed');
    } else {
      setFriendNick(handle, value);
    }
    closeNick();
    if (onChange) onChange();
  }
  card.querySelector('.nick-set').addEventListener('click', () => apply(input.value.trim()));
  card.querySelector('.nick-remove').addEventListener('click', () => apply(''));
  input.addEventListener('keydown', e => { if (e.key === 'Enter') apply(input.value.trim()); });
  modal.classList.add('open');
  scrim.classList.add('open');
  document.body.style.overflow = 'hidden';
  setTimeout(() => input.focus(), 50);
}

// ───── FRIENDS LIST (own profile) ─────
// deterministic fake presence so each friend has a stable online/last-seen value
function friendPresence(h) {
  let n = 0; for (const c of String(h)) n = (n * 31 + c.charCodeAt(0)) >>> 0;
  const online = n % 3 === 0;
  const buckets = ['just now', '5 min', '12 min', '1 hour', '3 hours', '8 hours', '1 day', '2 days', '4 days', '1 week'];
  return { online, last: buckets[n % buckets.length] };
}
function closeFriendsList() {
  document.getElementById('friendsModal')?.classList.remove('open');
  document.getElementById('friendsScrim')?.classList.remove('open');
  document.body.style.overflow = '';
}
// The friends list is REAL: /api/site/profile/{handle}/friends (All / Favourites, plus
// Requests when it's your own). Presence is the game's own (online = connected to the hub),
// favourites are the same flag the in-game star sets, and it re-renders itself when the
// server pushes a refresh (a friend comes online, a request arrives, a star is toggled in-game).
let FRIENDS_MODAL_HANDLE = null;
function openFriendsList(handle) {
  let scrim = document.getElementById('friendsScrim');
  let modal = document.getElementById('friendsModal');
  if (!modal) {
    scrim = document.createElement('div'); scrim.id = 'friendsScrim'; scrim.className = 'report-scrim';
    modal = document.createElement('div'); modal.id = 'friendsModal'; modal.className = 'report-modal';
    document.body.appendChild(scrim); document.body.appendChild(modal);
    scrim.addEventListener('click', closeFriendsList);
    modal.addEventListener('click', e => { if (e.target === modal) closeFriendsList(); });
  }
  const me = localStorage.getItem(USER_KEY);
  FRIENDS_MODAL_HANDLE = (handle || viewedHandle() || me || '').toLowerCase();
  let filter = 'all';
  let cache = null;

  async function load() {
    let r = null;
    try { if (apiOn()) r = await apiGet('/api/site/profile/' + encodeURIComponent(FRIENDS_MODAL_HANDLE) + '/friends'); } catch {}
    cache = (r && r.ok) ? r : { own: false, friends: [], pending: [], sent: [] };
    render();
  }

  function row(f, pending) {
    const name = escapeHtml(f.display_name || f.username);
    const av = f.profile_image || avatarUrl(f.username);
    const star = f.favourite ? ' <span class="fav-star">★</span>' : '';
    if (pending) {
      return `<div class="fl-item">
        <img class="fl-av" src="${av}" alt="">
        <div class="fl-meta"><span class="fl-name">${name}</span><span class="fl-sub">Wants to be your friend</span></div>
        <div class="fl-actions"><button class="fl-accept" data-id="${f.id}" data-n="${name}">Accept</button><button class="fl-decline" data-id="${f.id}">Decline</button></div>
      </div>`;
    }
    const status = (f.online
      ? `<span class="fl-online"><i></i>Online${f.where ? ' · ' + escapeHtml(f.where) : ''}</span>`
      : 'Offline')
      + (f.spotify && f.spotify.track ? `<br><span class="fl-spotify">${SP_ICON}${f.spotify.is_playing ? '' : 'Paused · '}${escapeHtml(f.spotify.track)} — ${escapeHtml(f.spotify.artists || '')}</span>` : '');
    const favBtn = cache.own
      ? `<button class="fl-fav${f.favourite ? ' on' : ''}" data-id="${f.id}" data-on="${f.favourite ? 1 : 0}" title="${f.favourite ? 'Remove from favourites' : 'Add to favourites'}">${f.favourite ? '★' : '☆'}</button>`
      : '';
    return `<div class="fl-item">
      <a class="fl-link" href="${userUrl(f.username)}" style="display:flex;align-items:center;gap:12px;flex:1;min-width:0;text-decoration:none;color:inherit">
        <img class="fl-av" src="${av}" alt="">
        <div class="fl-meta"><span class="fl-name">${name}${star}</span><span class="fl-sub">${status}</span></div>
      </a>${favBtn}
    </div>`;
  }

  function render() {
    if (!cache) {
      modal.innerHTML = `<div class="report-card fl-card"><button class="report-close" aria-label="Close">✕</button><h2 class="report-title">Friends</h2><p class="fl-empty">Loading…</p></div>`;
      modal.querySelector('.report-close').addEventListener('click', closeFriendsList);
      return;
    }
    const friends = cache.friends || [];
    const pending = cache.pending || [];
    const favs = friends.filter(f => f.favourite);
    const onlineCount = friends.filter(f => f.online).length;
    const show = filter === 'favourites' ? favs : filter === 'pending' ? pending : friends;
    const rows = show.length ? show.map(f => row(f, filter === 'pending')).join('')
      : `<p class="fl-empty">${filter === 'pending' ? 'No pending requests.' : filter === 'favourites' ? 'No favourites yet.' : 'No friends yet.'}</p>`;

    modal.innerHTML = `
      <div class="report-card fl-card">
        <button class="report-close" aria-label="Close">✕</button>
        <h2 class="report-title">${cache.own ? 'Friends' : escapeHtml(cap(FRIENDS_MODAL_HANDLE)) + "'s friends"}</h2>
        <p class="report-sub">${friends.length} friend${friends.length === 1 ? '' : 's'} · ${onlineCount} online</p>
        <div class="fl-tabs">
          <button class="fl-tab${filter === 'all' ? ' active' : ''}" data-f="all">All</button>
          <button class="fl-tab${filter === 'favourites' ? ' active' : ''}" data-f="favourites">Favourites${favs.length ? ` (${favs.length})` : ''}</button>
          ${cache.own ? `<button class="fl-tab${filter === 'pending' ? ' active' : ''}" data-f="pending">Requests${pending.length ? ` (${pending.length})` : ''}</button>` : ''}
        </div>
        <div class="fl-list">${rows}</div>
      </div>`;

    modal.querySelector('.report-close').addEventListener('click', closeFriendsList);
    modal.querySelectorAll('.fl-tab').forEach(t => t.addEventListener('click', () => { filter = t.dataset.f; render(); }));
    modal.querySelectorAll('.fl-accept').forEach(b => b.addEventListener('click', async () => {
      const { ok } = await apiPost('/api/site/friends/accept', { id: Number(b.dataset.id) });
      toast(ok ? `You and ${b.dataset.n} are now friends` : 'Could not accept right now');
      load();
    }));
    modal.querySelectorAll('.fl-decline').forEach(b => b.addEventListener('click', async () => {
      await apiPost('/api/site/friends/remove', { id: Number(b.dataset.id) }); load();
    }));
    modal.querySelectorAll('.fl-fav').forEach(b => b.addEventListener('click', async () => {
      const on = b.dataset.on !== '1';
      const { ok } = await apiPost('/api/site/friends/favourite', { id: Number(b.dataset.id), on });
      if (!ok) { toast('Could not update favourites'); return; }
      toast(on ? 'Added to favourites' : 'Removed from favourites');
      load();
    }));
  }
  modal.__reload = load;
  render();
  load();
  modal.classList.add('open');
  scrim.classList.add('open');
  document.body.style.overflow = 'hidden';
}

// Subscribers / Subscriptions popup. `kind` is 'subscribers' (who subscribes to this
// profile) or 'subscriptions' (who this profile subscribes to). Same modal chrome as the
// friends list, real data from /api/site/profile/{handle}/{kind}, and it re-renders itself on a
// server refresh push (someone subscribes / unsubscribes while it's open).
let SUBS_MODAL = null;
function openSubsList(handle, kind) {
  let scrim = document.getElementById('subsScrim');
  let modal = document.getElementById('subsModal');
  if (!modal) {
    scrim = document.createElement('div'); scrim.id = 'subsScrim'; scrim.className = 'report-scrim';
    modal = document.createElement('div'); modal.id = 'subsModal'; modal.className = 'report-modal';
    document.body.appendChild(scrim); document.body.appendChild(modal);
    scrim.addEventListener('click', closeSubsList);
    modal.addEventListener('click', e => { if (e.target === modal) closeSubsList(); });
  }
  const me = localStorage.getItem(USER_KEY);
  const h = (handle || viewedHandle() || me || '').toLowerCase();
  let cache = null;

  async function load() {
    let r = null;
    try { if (apiOn()) r = await apiGet('/api/site/profile/' + encodeURIComponent(h) + '/' + kind); } catch {}
    cache = (r && r.ok) ? r : { own: false, results: [] };
    render();
  }

  function render() {
    const title = kind === 'subscribers'
      ? (cache && cache.own ? 'Your subscribers' : escapeHtml(cap(h)) + "'s subscribers")
      : (cache && cache.own ? 'Your subscriptions' : escapeHtml(cap(h)) + ' subscribes to');
    const list = cache ? (cache.results || []) : null;
    const rows = !list ? '<p class="fl-empty">Loading…</p>'
      : list.length ? list.map(p => {
          const name = escapeHtml(p.display_name || p.username);
          const av = p.profile_image || avatarUrl(p.username);
          const status = p.online ? `<span class="fl-online"><i></i>Online${p.where ? ' · ' + escapeHtml(p.where) : ''}</span>`
                                  : `${fmt(p.subscribers)} subscriber${p.subscribers === 1 ? '' : 's'}`;
          const canSub = me && p.username && p.username.toLowerCase() !== me.toLowerCase();
          const btn = canSub ? `<button class="fl-sub-btn${p.subscribed ? ' on' : ''}" data-id="${p.id}" data-on="${p.subscribed ? 1 : 0}">${p.subscribed ? 'Subscribed' : 'Subscribe'}</button>` : '';
          return `<div class="fl-item">
            <a class="fl-link" href="${userUrl(p.username)}" style="display:flex;align-items:center;gap:12px;flex:1;min-width:0;text-decoration:none;color:inherit">
              <img class="fl-av" src="${av}" alt="">
              <div class="fl-meta"><span class="fl-name">${name}</span><span class="fl-sub">${status}</span></div>
            </a>${btn}
          </div>`;
        }).join('')
      : `<p class="fl-empty">${kind === 'subscribers' ? 'No subscribers yet.' : 'Not subscribed to anyone yet.'}</p>`;

    modal.innerHTML = `
      <div class="report-card fl-card">
        <button class="report-close" aria-label="Close">✕</button>
        <h2 class="report-title">${title}</h2>
        <p class="report-sub">${list ? `${list.length} ${kind === 'subscribers' ? 'subscriber' : 'subscription'}${list.length === 1 ? '' : 's'}` : ''}</p>
        <div class="fl-list">${rows}</div>
      </div>`;
    modal.querySelector('.report-close').addEventListener('click', closeSubsList);
    modal.querySelectorAll('.fl-sub-btn').forEach(b => b.addEventListener('click', async () => {
      if (!requireLogin('subscribe')) return;
      const on = b.dataset.on !== '1';
      const { ok, data } = await apiPost('/api/site/subscribe', { id: Number(b.dataset.id), on });
      if (!ok) { toast((data && data.error) || 'Could not update the subscription'); return; }
      toast(on ? 'Subscribed' : 'Unsubscribed');
      load();
    }));
  }
  modal.__reload = load;
  render();
  load();
  modal.classList.add('open');
  scrim.classList.add('open');
  document.body.style.overflow = 'hidden';
}
function closeSubsList() {
  document.getElementById('subsModal')?.classList.remove('open');
  document.getElementById('subsScrim')?.classList.remove('open');
  document.body.style.overflow = '';
}

// ───── CONFIRM DIALOG ─────
function closeConfirm() {
  document.getElementById('confirmModal')?.classList.remove('open');
  document.getElementById('confirmScrim')?.classList.remove('open');
  document.body.style.overflow = '';
}
function openConfirm(opts) {
  const { title, message, confirmText, onConfirm } = opts;
  let scrim = document.getElementById('confirmScrim');
  let modal = document.getElementById('confirmModal');
  if (!modal) {
    scrim = document.createElement('div'); scrim.id = 'confirmScrim'; scrim.className = 'report-scrim';
    modal = document.createElement('div'); modal.id = 'confirmModal'; modal.className = 'report-modal';
    document.body.appendChild(scrim); document.body.appendChild(modal);
    scrim.addEventListener('click', closeConfirm);
    modal.addEventListener('click', e => { if (e.target === modal) closeConfirm(); });
  }
  modal.innerHTML = `
    <div class="report-card confirm-card">
      <h2 class="report-title">${title}</h2>
      <p class="report-sub">${message}</p>
      <div class="report-actions">
        <button class="report-cancel confirm-no">Cancel</button>
        <button class="report-submit confirm-yes">${confirmText}</button>
      </div>
    </div>`;
  modal.querySelector('.confirm-no').addEventListener('click', closeConfirm);
  modal.querySelector('.confirm-yes').addEventListener('click', () => {
    closeConfirm();
    if (typeof onConfirm === 'function') onConfirm();
  });
  modal.classList.add('open');
  scrim.classList.add('open');
  document.body.style.overflow = 'hidden';
}

document.addEventListener('keydown', e => { if (e.key === 'Escape') { closeReport(); closeNick(); closeConfirm(); } });

// ───── INTERACTIONS (cheer + follow + comments) ─────
document.addEventListener('click', e => {
  // cheer a photo
  const cheer = e.target.closest('.cheer');
  if (cheer) {
    e.preventDefault();
    if (!requireLogin('cheer photos')) return;
    if (!rateLimit('cheer', 15, 10000)) { toast('Whoa! Slow down on the cheers.'); return; }
    const liked = cheer.classList.toggle('liked');
    const base = +cheer.dataset.n;
    const span = cheer.querySelector('span');
    if (span) span.textContent = fmt(base + (liked ? 1 : 0));
    const pid = photoIdOf(cheer);
    if (pid && apiOn()) {
      // The same cheer the game records: what the site shows is what in-game shows.
      apiPost('/api/site/photos/' + pid + '/cheer').then(r => {
        if (r && r.ok && r.data) {
          cheer.classList.toggle('liked', !!r.data.cheered);
          cheer.dataset.n = (r.data.cheers || 0) - (r.data.cheered ? 1 : 0);
          if (span) span.textContent = fmt(r.data.cheers || 0);
        } else if (r && r.status === 401) { cheer.classList.toggle('liked', !liked); if (span) span.textContent = fmt(base); toast('Log in to cheer photos'); }
      });
    }
    return;
  }
  // toggle comments
  const ct = e.target.closest('.comment-toggle');
  if (ct) {
    e.preventDefault();
    const box = ct.closest('.post').querySelector('.post-comments');
    if (box) {
      box.hidden = !box.hidden;
      const list = box.querySelector('.comment-list');
      if (!box.hidden && list && !list.dataset.loaded) loadCommentsInto(box, photoIdOf(ct));
    }
    return;
  }
  // delete your own comment
  const cdel = e.target.closest('.comment-del');
  if (cdel) {
    const comment = cdel.closest('.comment');
    const list = cdel.closest('.comment-list');
    const post = cdel.closest('.post') || cdel.closest('[data-photo]');
    const pidDel = photoIdOf(cdel), cid = comment.dataset.cid;
    if (pidDel && cid && apiOn()) apiDelete('/api/site/photos/' + pidDel + '/comments/' + cid).then(r => { if (!r.ok) toast('Could not delete the comment'); });
    comment.remove();
    if (post) {
      const toggle = post.querySelector('.comment-toggle');
      if (toggle) {
        toggle.dataset.n = Math.max(0, (+toggle.dataset.n || 0) - 1);
        const s = toggle.querySelector('span');
        if (s) s.textContent = fmt(+toggle.dataset.n);
      }
    }
    if (list && !list.querySelector('.comment')) {
      list.innerHTML = '<p class="no-comments">No comments yet - be the first!</p>';
    }
    return;
  }
  // report a comment
  const rep = e.target.closest('.comment-report');
  if (rep) {
    const comment = rep.closest('.comment');
    if (comment.classList.contains('reported')) return;
    openReport('Report comment', () => {
      comment.classList.add('reported');
      rep.outerHTML = '<span class="comment-reported">Reported</span>';
    }, comment.dataset.cid ? { type: 'comment', id: comment.dataset.cid, photoId: photoIdOf(comment) } : null);
    return;
  }
  // follow / unfollow
  const fb = e.target.closest('.follow-btn');
  if (fb) {
    const on = fb.classList.toggle('following');
    fb.textContent = on ? 'Following' : 'Follow';
  }
});

// add a comment
document.addEventListener('submit', e => {
  const form = e.target.closest('.comment-form');
  if (!form) return;
  e.preventDefault();
  const me = localStorage.getItem(USER_KEY);
  if (!me) return;
  const input = form.querySelector('.comment-input');
  const text = input.value.trim();
  if (!text) return;

  const box = form.closest('.post-comments');
  const err = box.querySelector('.comment-error');

  // block inappropriate content
  if (isInappropriate(text)) {
    if (err) { err.textContent = 'This content is too inappropriate to send'; err.classList.add('show'); }
    return;
  }
  // rate limit: max 5 comments per 30s
  if (!rateLimit('comment', 5, 30000)) {
    if (err) { err.textContent = 'You\'re commenting too fast - please wait a moment.'; err.classList.add('show'); }
    return;
  }
  if (err) { err.classList.remove('show'); err.textContent = ''; }

  const list = box.querySelector('.comment-list');
  const empty = list.querySelector('.no-comments');
  if (empty) empty.remove();

  const pidNew = photoIdOf(form);
  if (pidNew && apiOn()) {
    input.value = '';
    apiPost('/api/site/photos/' + pidNew + '/comments', { text }).then(r => {
      if (r && r.ok && r.data && r.data.comment) list.insertAdjacentHTML('beforeend', commentRow(serverComment(r.data.comment)));
      else { if (err) { err.textContent = (r && r.data && r.data.error) || 'Could not post the comment'; err.classList.add('show'); } if (!list.querySelector('.comment')) list.innerHTML = '<p class="no-comments">No comments yet - be the first!</p>'; }
    });
  } else {
    list.insertAdjacentHTML('beforeend', commentRow({ user: me, handle: me, text }));
    input.value = '';
  }

  // bump the comment count
  const toggle = form.closest('.post').querySelector('.comment-toggle');
  if (toggle) {
    toggle.dataset.n = (+toggle.dataset.n || 0) + 1;
    const span = toggle.querySelector('span');
    if (span) span.textContent = fmt(+toggle.dataset.n);
  }
});

// ─────────────────────────────────────────────────────────────────────────────
// LIVE DATA
//
// Everything above renders the demo content in data.js so the page has something
// to show instantly. This section then replaces it with what is actually on the
// server: real rooms, real players, real photos, real avatars.
//
// Replace-after-render rather than render-only-after-fetch, deliberately: the page
// is useful immediately and never shows a bank of spinners, and if the API is down
// the demo content simply stays. Every fetch below is allowed to fail silently for
// that reason.
// ─────────────────────────────────────────────────────────────────────────────

// handle -> real profile image URL, filled in from whatever endpoint mentioned them.
const LIVE_AVATARS = new Map();

// avatarUrl is called all over the file and defaults to a generated DiceBear face.
// Wrap it so a real profile picture wins when we know one, without touching the
// dozen call sites.
const _avatarUrlBase = avatarUrl;
avatarUrl = function (seed) {
  const real = LIVE_AVATARS.get(avKey(seed));
  return real || _avatarUrlBase(seed);
};

// Remember an account's picture and repaint any avatar already on screen for them.
function noteAvatar(user) {
  if (!user || !user.username) return;
  const key = avKey(user.username);
  if (!user.profile_image) return;
  if (LIVE_AVATARS.get(key) === user.profile_image) return;
  LIVE_AVATARS.set(key, user.profile_image);
  refreshAvatars(key, user.profile_image);
}

async function apiGet(path) {
  const resp = await fetch(apiUrl(path), { credentials: 'same-origin' });
  if (!resp.ok) throw new Error(path + ' -> ' + resp.status);
  const out = await resp.json();
  if (out && out.ok === false) throw new Error(out.error || path);
  return out;
}

// ---- rooms ------------------------------------------------------------------

function roomThumb(r) {
  return r.image
    ? `<div class="rail-thumb" style="background-image:url('${r.image}');background-size:cover;background-position:center"></div>`
    : `<div class="rail-thumb" style="--a:#ff5a3c;--b:#7b2ff7"></div>`;
}

async function hydrateRooms() {
  if (!apiOn()) return;

  let rooms;
  try {
    rooms = (await apiGet('/api/site/rooms/hot?take=12')).results || [];
  } catch (e) {
    console.warn('[campus] rooms unavailable, keeping demo rooms:', e.message);
    return;
  }
  if (!rooms.length) return;   // empty server: demo content is better than a blank rail

  // Room names come back with the leading '^' the game uses; strip it for display
  // and re-add it in the label so it matches the client.
  rooms.forEach(r => { r.displayName = (r.name || '').replace(/^\^/, ''); });

  // Left rail. The markup for this is hardcoded in index.html, so replace the
  // items but keep the heading.
  const rail = document.getElementById('rooms');
  if (rail) {
    // This rail holds the online-friends section (inserted by hydrateOnline) ABOVE the rooms.
    // Preserve it, and always use a fixed "Top Rooms" heading - previously this reused whatever
    // .rail-title was first in the rail, which was the online section's, so the rooms ended up
    // mislabelled "Online Friends".
    const online = rail.querySelector('.rail-online');
    const pinnedBlock = rail.querySelector('.rail-pinned');   // pinned friends (renderPinned) live above the rooms too
    rail.innerHTML = (pinnedBlock ? pinnedBlock.outerHTML : '') + (online ? online.outerHTML : '')
      + '<h3 class="rail-title">Top Rooms</h3>'
      + rooms.slice(0, 8).map(r => `
        <a href="${roomUrl(r.id)}" class="rail-item">
          ${roomThumb(r)}
          <div class="rail-meta">
            <span class="rail-name">^${escapeHtml(r.displayName)}</span>
            <span class="rail-by">${r.creator ? '@' + escapeHtml(r.creator) : ''}</span>
          </div>
        </a>`).join('');
  }

  // NOTE: the featured slideshow is NOT set here anymore. It's owned by loadFeatured(), which
  // shows the admin-curated FEATURED rooms (same as in-game), falling back to hot only when
  // nothing is featured. Setting it here too would overwrite that with the top rooms.
}

// ---- presence ---------------------------------------------------------------

async function hydrateOnline() {
  if (!apiOn()) return;

  const rail = document.querySelector('.feed-layout .rail');
  if (!rail) return;

  // Signed OUT: there are no "your friends" to show, so remove the section entirely (no generic
  // "Online Now" list of strangers).
  if (!CAMPUS_ME) { rail.querySelector('.rail-online')?.remove(); return; }

  // Signed in: ONLY your own online friends (never every online player).
  let people = [];
  try {
    const f = await apiGet('/api/site/friends');
    people = (f.results || []).filter(p => p.online);
  } catch (e) {
    console.warn('[campus] presence unavailable, keeping demo list:', e.message);
    return;
  }

  people.forEach(noteAvatar);

  const existing = rail.querySelector('.rail-online');
  const heading = CAMPUS_ME ? 'Online Friends' : 'Online Now';

  if (!people.length) {
    // Real answer, and it is "nobody". Say so instead of showing invented friends.
    const emptyMsg = CAMPUS_ME ? 'None of your friends are online right now.' : 'No one is online right now.';
    const html = `<div class="rail-online"><h3 class="rail-title">${heading} · 0</h3>
      <p class="rail-by" style="padding:.5rem .25rem">${emptyMsg}</p></div>`;
    if (existing) existing.outerHTML = html; else rail.insertAdjacentHTML('afterbegin', html);
    return;
  }

  const items = people.map(p => {
    const handle = p.username || String(p.id);
    const name = p.display_name || p.username || 'Player';
    return `<a class="rail-item" href="${userUrl(handle)}">
      <span class="rail-on-av"><img class="rail-pin-av" data-av="${avKey(handle)}" src="${avatarUrl(handle)}" alt=""><i class="online-dot"></i></span>
      <div class="rail-meta">
        <span class="rail-name">${escapeHtml(name)}</span>
        <span class="rail-by">${escapeHtml(p.where || 'Online now')}</span>
      </div>
    </a>`;
  }).join('');

  const html = `<div class="rail-online"><h3 class="rail-title">${heading} · ${people.length}</h3>${items}</div>`;
  if (existing) existing.outerHTML = html; else rail.insertAdjacentHTML('afterbegin', html);
}

// ---- photos -----------------------------------------------------------------

async function hydrateFeed() {
  if (!apiOn() || !postList) return;

  let photos;
  try {
    photos = (await apiGet('/api/site/photos/recent?take=24')).results || [];
  } catch (e) {
    console.warn('[campus] photo feed unavailable, keeping fallback:', e.message);
    return;
  }

  if (!photos.length) {
    postList.innerHTML = '<div class="feed-loading">No photos have been shared yet.</div>';
    return;
  }

  photos.forEach(p => noteAvatar(p.author));

  // Map the server shape onto what renderPosts expects.
  renderPosts(photos.map(p => ({
    id: p.id,
    user: (p.author && (p.author.display_name || p.author.username)) || 'Unknown',
    handle: (p.author && p.author.username) || 'player',
    room: null,               // room NAME is not in this projection; the id alone is not useful
    image: p.image,
    caption: p.description || '',
    cheers: p.cheers || 0,
    thread: [],               // the thread itself is a separate fetch
    comments: p.comments || 0, // count comes from the server (see nComments)
    takenAt: p.createdAt,
  })));
}

// ---- go ---------------------------------------------------------------------

// syncSession() has already run above; wait for it so hydrateOnline knows whether
// anyone is signed in, then fill everything in parallel.
SESSION_READY.then(() => {
  hydrateRooms();
  hydrateOnline();
  hydrateFeed();
  if (CAMPUS_ME) noteAvatar(CAMPUS_ME);
});

// ---- topbar restyle (look only) --------------------------------------------------------------
// The brand, an inline search field and the Rooms / News / Support links in the top bar on every
// page. Nothing new behind them: the field just opens the existing search panel, the links go
// where the drawer's do.
(function topbarChrome() {
  const bar = document.querySelector('.topbar');
  if (!bar || bar.querySelector('.top-mid')) return;
  const left = bar.querySelector('.top-left');
  const right = bar.querySelector('.top-right');
  if (topbarBrand) {
    topbarBrand.classList.add('always');
    topbarBrand.addEventListener('click', () => { window.location.href = '/'; });
    topbarBrand.setAttribute('role', 'link');
    topbarBrand.tabIndex = 0;
  }
  if (left && document.getElementById('searchBtn')) {
    const mid = document.createElement('div');
    mid.className = 'top-mid';
    mid.innerHTML = '<label class="top-search"><svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2.3" stroke-linecap="round"><circle cx="11" cy="11" r="7"/><line x1="21" y1="21" x2="16.7" y2="16.7"/></svg><input type="text" placeholder="Search…" aria-label="Search" autocomplete="off"></label>';
    left.insertAdjacentElement('afterend', mid);
    const fake = mid.querySelector('input');
    const open = () => {
      if (typeof openSearch !== 'function' || !document.getElementById('searchPanel')) return;
      openSearch();
      const real = document.getElementById('searchInput');
      if (real) {
        if (fake.value) { real.value = fake.value; real.dispatchEvent(new Event('input', { bubbles: true })); }
        real.focus();
      }
      fake.value = '';
      fake.blur();
    };
    fake.addEventListener('focus', open);
    fake.addEventListener('click', open);
  }
  if (right) {
    const links = document.createElement('nav');
    links.className = 'top-nav-links';
    links.setAttribute('aria-label', 'Main');
    links.innerHTML =
      '<a href="/rooms"><span>Rooms</span><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 11l9-8 9 8"/><path d="M5 10v10h14V10"/></svg></a>' +
      '<a href="/news"><span>News</span><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 5h13v14a1 1 0 0 1-1 1H4z"/><path d="M17 8h2a1 1 0 0 1 1 1v9a2 2 0 0 1-2 2"/><path d="M8 9h5M8 13h5"/></svg></a>' +
      '<a href="/support"><span>Support</span><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/></svg></a>';
    right.insertAdjacentElement('afterbegin', links);
  }
})();

// Home hero: "N players online right now in Campus!" from the same presence API the online list
// uses. Left as the neutral wording when the API is off or answers nothing.
(function heroOnline() {
  const el = document.getElementById('heroOnline');
  if (!el || !apiOn()) return;
  const paint = async () => {
    try {
      const d = await apiGet('/api/site/online?take=1');
      if (d && d.ok && typeof d.total === 'number') {
        el.textContent = d.total === 1 ? '1 player is' : `${d.total.toLocaleString()} players are`;
      }
    } catch {}
  };
  paint();
  setInterval(paint, 60000);
})();


// ---- /rooms: browse every public room (Hot / New / Originals, with a filter) -----------------
(function roomsPage() {
  const grid = document.getElementById('roomsGrid');
  if (!grid) return;
  const tabs = Array.from(document.querySelectorAll('.rtab'));
  const filter = document.getElementById('roomsFilter');
  const more = document.getElementById('roomsMore');
  const count = document.getElementById('roomsCount');
  const PAGE = 24;
  let tab = 'hot', q = '', skip = 0, total = 0, busy = false, seq = 0;

  // Deep link: /rooms?tab=rro&q=dodge
  try {
    const p = new URLSearchParams(location.search);
    if (['hot', 'new', 'rro'].includes(p.get('tab'))) tab = p.get('tab');
    if (p.get('q')) { q = p.get('q'); filter.value = q; }
  } catch {}

  const ICON_VISITS = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M1.5 12S5.5 5 12 5s10.5 7 10.5 7-4 7-10.5 7S1.5 12 1.5 12z"/><circle cx="12" cy="12" r="3.2"/></svg>';
  const ICON_CHEER = CHEER_ICON;

  function card(r) {
    const name = String(r.name || '').replace(/^\^/, '');
    const img = r.image
      ? `<div class="rc-img" style="background-image:url('${r.image}')"></div>`
      : `<div class="rc-img rc-img-empty">^${escapeHtml(name)}</div>`;
    return `<a class="rc" href="${roomUrl(r.name || r.id)}">
      ${img}
      <div class="rc-body">
        <span class="rc-name">^${escapeHtml(name)}</span>
        <span class="rc-by">${r.creator ? '@' + escapeHtml(r.creator) : ''}</span>
        <span class="rc-stats"><span>${ICON_VISITS}${fmt(r.visits || 0)}</span><span>${ICON_CHEER}${fmt(r.cheers || 0)}</span></span>
      </div>
    </a>`;
  }

  function fallback() {
    const list = Object.keys(window.ROOMS_DATA || {}).map(id => Object.assign({ id, name: (ROOMS_DATA[id] || {}).name || id }, ROOMS_DATA[id] || {}));
    grid.innerHTML = list.length ? list.map(card).join('') : '<div class="feed-loading">No rooms to show yet.</div>';
    more.hidden = true;
    count.textContent = '';
  }

  async function load(reset) {
    if (busy) return;
    if (!apiOn()) { fallback(); return; }
    busy = true;
    const my = ++seq;
    if (reset) { skip = 0; grid.innerHTML = '<div class="feed-loading">Loading rooms…</div>'; }
    more.disabled = true;
    try {
      const d = await apiGet(`/api/site/rooms/browse?tab=${encodeURIComponent(tab)}&q=${encodeURIComponent(q)}&skip=${skip}&take=${PAGE}`);
      if (my !== seq) return;                       // a newer request superseded this one
      if (!d || !d.ok) throw new Error((d && d.error) || 'failed');
      total = d.total || 0;
      const html = (d.results || []).map(card).join('');
      if (reset) grid.innerHTML = html || '<div class="feed-loading">No rooms match that.</div>';
      else grid.insertAdjacentHTML('beforeend', html);
      skip += (d.results || []).length;
      more.hidden = skip >= total;
      count.textContent = total ? `${total.toLocaleString()} room${total === 1 ? '' : 's'}` : '';
    } catch (e) {
      if (my === seq && reset) grid.innerHTML = '<div class="feed-loading">Could not load rooms.</div>';
      more.hidden = true;
    } finally {
      if (my === seq) { busy = false; more.disabled = false; }
    }
  }

  function setTab(next) {
    tab = next;
    tabs.forEach(b => {
      const on = b.dataset.tab === tab;
      b.classList.toggle('active', on);
      b.setAttribute('aria-selected', on ? 'true' : 'false');
    });
    try {
      const u = new URL(location.href);
      u.searchParams.set('tab', tab);
      if (q) u.searchParams.set('q', q); else u.searchParams.delete('q');
      history.replaceState(null, '', u.pathname + (u.search || ''));
    } catch {}
    load(true);
  }

  tabs.forEach(b => b.addEventListener('click', () => { if (b.dataset.tab !== tab) setTab(b.dataset.tab); }));
  let t = null;
  filter.addEventListener('input', () => {
    clearTimeout(t);
    t = setTimeout(() => { q = filter.value.trim(); setTab(tab); }, 250);
  });
  more.addEventListener('click', () => load(false));
  setTab(tab);
})();
