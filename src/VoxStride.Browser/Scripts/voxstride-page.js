// VoxStride page helper (shared by VoxStride's own browser window and the VoxStride browser extension).
// Reads only what is needed to describe interactive elements; never reads cookies, storage or password values.
// Everything it returns is untrusted page content.
(() => {
  if (window.__voxstride && window.__voxstride.version === 1) return;
  const SENSITIVE = /(cc-|card.?num|credit.?card|cvv|cvc|security.?code|expir|iban|swift|ssn|social.?security|routing|account.?num|one-time|otp|2fa|passcode|\bpin\b)/i;
  const SEARCH = /(search|query|^q$|^s$|find)/i;
  const SELECTOR = 'a[href],button,input,select,textarea,summary,[role=button],[role=link],[role=textbox],[role=searchbox],[role=checkbox],[role=radio],[role=tab],[role=menuitem],[role=option],[role=switch],[contenteditable=""],[contenteditable=true]';

  const text = (s, n) => (s || '').replace(/\s+/g, ' ').trim().slice(0, n);

  function nameOf(el) {
    const aria = el.getAttribute('aria-label');
    if (aria) return text(aria, 120);
    const by = el.getAttribute('aria-labelledby');
    if (by) { const t = by.split(/\s+/).map(id => document.getElementById(id)).filter(Boolean).map(n => n.innerText).join(' '); if (t.trim()) return text(t, 120); }
    if (el.id) { const l = document.querySelector('label[for="' + CSS.escape(el.id) + '"]'); if (l && l.innerText.trim()) return text(l.innerText, 120); }
    const wrap = el.closest('label'); if (wrap && wrap.innerText.trim()) return text(wrap.innerText, 120);
    for (const a of ['placeholder', 'title', 'alt', 'name']) { const v = el.getAttribute(a); if (v) return text(v, 120); }
    if (el.tagName === 'INPUT' && /^(button|submit|reset)$/i.test(el.type)) return text(el.value, 120);
    const img = el.querySelector && el.querySelector('img[alt]'); 
    const inner = text(el.innerText, 120);
    return inner || (img ? text(img.alt, 120) : '');
  }

  function roleOf(el) {
    const r = el.getAttribute('role'); if (r) return r;
    const t = el.tagName.toLowerCase();
    if (t === 'a') return 'link';
    if (t === 'button' || t === 'summary') return 'button';
    if (t === 'select') return 'combobox';
    if (t === 'textarea') return 'textarea';
    if (el.isContentEditable) return 'textbox';
    if (t === 'input') {
      const ty = (el.type || 'text').toLowerCase();
      if (/^(button|submit|reset|image)$/.test(ty)) return 'button';
      if (ty === 'checkbox') return 'checkbox';
      if (ty === 'radio') return 'radio';
      if (ty === 'search') return 'searchbox';
      return 'textbox';
    }
    return t;
  }

  function visible(el) {
    if (el.tagName === 'INPUT' && el.type === 'hidden') return false;
    const r = el.getBoundingClientRect();
    if (r.width < 2 || r.height < 2) return false;
    const s = getComputedStyle(el);
    return s.visibility !== 'hidden' && s.display !== 'none' && Number(s.opacity) > 0.05;
  }

  function observe(max) {
    const nonce = Math.random().toString(36).slice(2, 10);
    const out = [];
    let truncated = false, n = 0;
    for (const el of document.querySelectorAll(SELECTOR)) {
      if (!visible(el)) continue;
      if (out.length >= max) { truncated = true; break; }
      const id = 'b' + (++n);
      el.setAttribute('data-voxstride', nonce + ':' + id);
      const role = roleOf(el);
      const isPassword = el.tagName === 'INPUT' && el.type === 'password';
      const hints = [el.getAttribute('autocomplete'), el.name, el.id, el.getAttribute('aria-label'), el.getAttribute('placeholder')].join(' ');
      const isSensitive = !isPassword && (SENSITIVE.test(hints) || /^cc-|one-time-code/.test(el.getAttribute('autocomplete') || ''));
      const form = el.form || el.closest('form');
      const isSearch = role === 'searchbox' || (form && (form.getAttribute('role') === 'search' || SEARCH.test(form.action || ''))) || SEARCH.test([el.name, el.id, el.getAttribute('placeholder'), el.getAttribute('aria-label')].join(' '));
      const submitsForm = !!form && ((el.tagName === 'BUTTON' && (el.type || 'submit') === 'submit') || (el.tagName === 'INPUT' && /^(submit|image)$/i.test(el.type)));
      let value = null;
      if (!isPassword && !isSensitive && ('value' in el) && /textbox|searchbox|combobox|textarea/.test(role)) value = text(el.value, 200);
      if (el.isContentEditable) value = text(el.innerText, 200);
      const r = el.getBoundingClientRect();
      out.push({ id, role, name: nameOf(el), value, href: el.tagName === 'A' ? el.href : null, enabled: !el.disabled && el.getAttribute('aria-disabled') !== 'true',
                 isPassword, isSensitive, isSearch: !!isSearch, submitsForm, x: Math.round(r.left), y: Math.round(r.top), w: Math.round(r.width), h: Math.round(r.height) });
    }
    return { nonce, url: location.href, title: document.title, elements: out, text: text(document.body ? document.body.innerText : '', 3000), truncated };
  }

  const find = (nonce, id) => document.querySelector('[data-voxstride="' + nonce + ':' + id + '"]');

  function click(nonce, id) {
    const el = find(nonce, id);
    if (!el) return { ok: false, error: 'element_gone' };
    el.scrollIntoView({ block: 'center' });
    el.click();
    return { ok: true };
  }

  function fill(nonce, id, value, enter) {
    const el = find(nonce, id);
    if (!el) return { ok: false, error: 'element_gone' };
    if (el.tagName === 'INPUT' && el.type === 'password') return { ok: false, error: 'sensitive_field' };
    el.scrollIntoView({ block: 'center' });
    el.focus();
    if (el.isContentEditable) { el.textContent = value; }
    else {
      // Use the native setter so frameworks (React, Vue…) see the change.
      const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : el.tagName === 'SELECT' ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
      Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, value);
    }
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    const read = el.isContentEditable ? el.innerText : el.value;
    if (enter) {
      el.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', bubbles: true }));
      const form = el.form || el.closest('form');
      if (form) { if (form.requestSubmit) form.requestSubmit(); else form.submit(); }
    }
    return { ok: true, value: read };
  }

  window.__voxstride = { version: 1, observe, click, fill };
})();
