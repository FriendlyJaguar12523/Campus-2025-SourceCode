// ───── SETTINGS > SECURITY: two-factor authentication ─────
// Backed by SiteController's /api/site/2fa/* (TwoFactorDB - the same store the admin panel's own 2FA uses, one row per
// account). QR rendering is 100% client-side (qrcode-generator, loaded just before this file) - the secret never leaves
// the browser except in the /setup response itself, so there's no third party in the loop.
(function () {
  const stateText = document.getElementById('tfaStatusText');
  if (!stateText) return;   // not on this page

  const onBtn = document.getElementById('tfaOnBtn'), offBtn = document.getElementById('tfaOffBtn');
  const setupBox = document.getElementById('tfaSetup'), recoveryBox = document.getElementById('tfaRecovery');
  const qrBox = document.getElementById('tfaQr'), secretEl = document.getElementById('tfaSecret');
  const codeInput = document.getElementById('tfaCode'), setupErr = document.getElementById('tfaSetupError');

  function show(which) {
    onBtn.style.display = which === 'off' ? 'inline-block' : 'none';
    offBtn.style.display = which === 'on' ? 'inline-block' : 'none';
    setupBox.style.display = which === 'setup' ? 'block' : 'none';
    recoveryBox.style.display = which === 'recovery' ? 'block' : 'none';
  }

  async function load() {
    try {
      const r = await fetch('/api/site/2fa/status', { credentials: 'same-origin' });
      const d = await r.json();
      if (!d.ok) { stateText.textContent = 'Log in to manage this.'; return; }
      stateText.textContent = d.enabled ? 'On - a code from your app is needed to log in.' : 'Off - your password alone signs you in.';
      show(d.enabled ? 'on' : 'off');
    } catch { stateText.textContent = 'Could not load.'; }
  }

  onBtn.addEventListener('click', async () => {
    const r = await fetch('/api/site/2fa/setup', { method: 'POST', credentials: 'same-origin' });
    const d = await r.json();
    if (!d.ok) { alert(d.error || 'Could not start setup.'); return; }
    secretEl.textContent = d.secret;
    qrBox.innerHTML = '';
    try {
      const qr = qrcode(0, 'M');   // type 0 = auto-size for the data length
      qr.addData(d.otpauth);
      qr.make();
      qrBox.innerHTML = qr.createSvgTag({ scalable: true });
    } catch { qrBox.innerHTML = '<div style="color:#333; font-size:12px; padding:8px;">QR unavailable - use the key.</div>'; }
    codeInput.value = ''; setupErr.textContent = '';
    show('setup');
  });

  document.getElementById('tfaCancelBtn').addEventListener('click', () => show('off'));

  document.getElementById('tfaConfirmBtn').addEventListener('click', async () => {
    const code = codeInput.value.trim();
    if (!code) return;
    const r = await fetch('/api/site/2fa/confirm', { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code }) });
    const d = await r.json();
    if (!d.ok) { setupErr.textContent = d.error || 'Wrong code.'; return; }
    document.getElementById('tfaRecoveryList').textContent = d.recoveryCodes.join('\n');
    show('recovery');
  });

  document.getElementById('tfaRecoveryDoneBtn').addEventListener('click', () => { show('on'); load(); });

  offBtn.addEventListener('click', async () => {
    if (!confirm('Turn off two-factor authentication? Your password alone will sign you in again.')) return;
    await fetch('/api/site/2fa/disable', { method: 'POST', credentials: 'same-origin' });
    load();
  });

  // Only the Security tab needs this, and it's cheap - load once the page is ready rather than waiting for the tab click.
  load();
})();
