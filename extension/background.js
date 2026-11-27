// VoxStride browser extension — service worker.
// Connects to the VoxStride desktop app through the registered native host "com.voxstride.bridge".
// Acts only on the ACTIVE tab, only on sites the user explicitly allowed (popup), and refuses if the tab's
// origin changed since VoxStride looked at it. Never reads cookies, passwords, storage or other tabs.
const HOST = 'com.voxstride.bridge';
const PROTOCOL = 1;
let port = null;
let connected = false;

function setBadge(text, color) {
  chrome.action.setBadgeText({ text });
  if (color) chrome.action.setBadgeBackgroundColor({ color });
}

function connect() {
  if (port) return;
  try { port = chrome.runtime.connectNative(HOST); } catch (e) { port = null; return; }
  port.onMessage.addListener(onMessage);
  port.onDisconnect.addListener(() => {
    port = null; connected = false; setBadge('', null);
    chrome.storage.session.set({ status: 'disconnected', error: chrome.runtime.lastError ? chrome.runtime.lastError.message : '' });
  });
  const ua = navigator.userAgent.match(/(Edg|Chrome)\/[\d.]+/);
  port.postMessage({ type: 'hello', version: PROTOCOL, browser: ua ? ua[0] : 'Chromium' });
}

const originOf = url => { try { const u = new URL(url); return u.origin; } catch { return ''; } };

async function activeTab(tabId) {
  if (tabId && /^\d+$/.test(String(tabId))) { try { return await chrome.tabs.get(Number(tabId)); } catch { return null; } }
  const [t] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  return t || null;
}

async function allowed(origin) {
  if (!/^https?:/.test(origin)) return false;
  return chrome.permissions.contains({ origins: [origin + '/*'] });
}

async function inPage(tabId, fn, args) {
  await chrome.scripting.executeScript({ target: { tabId }, files: ['voxstride-page.js'] });
  const [r] = await chrome.scripting.executeScript({ target: { tabId }, func: fn, args });
  return r ? r.result : null;
}

async function handle(op, a) {
  const tab = await activeTab(a.tabId);
  if (!tab || !tab.url || !/^https?:/.test(tab.url)) {
    if (op === 'navigate' && tab) { /* allowed below */ } else return { ok: false, error: 'no_tab' };
  }
  if (op === 'navigate') {
    const target = originOf(a.url || '');
    if (!a.url || !a.url.startsWith('https://') || !(await allowed(target))) return { ok: false, error: 'not_allowed_on_site' };
    await chrome.tabs.update(tab.id, { url: a.url });
    return { ok: true, url: a.url, verified: false };
  }
  const origin = originOf(tab.url);
  if (!(await allowed(origin))) return { ok: false, error: 'not_allowed_on_site' };
  if (a.origin && a.origin !== origin) return { ok: false, error: 'origin_changed' };
  setBadge('…', '#4F46E5');
  try {
    if (op === 'observe') {
      const page = await inPage(tab.id, m => window.__voxstride.observe(m), [Math.min(Number(a.max) || 120, 300)]);
      await chrome.storage.session.set({ ['nonce:' + tab.id]: page.nonce });
      return { ok: true, tabId: String(tab.id), page };
    }
    const nonce = (await chrome.storage.session.get('nonce:' + tab.id))['nonce:' + tab.id];
    if (!nonce) return { ok: false, error: 'element_gone' };
    if (op === 'click') {
      const r = await inPage(tab.id, (n, id) => window.__voxstride.click(n, id), [nonce, String(a.elementId)]);
      const t = await chrome.tabs.get(tab.id);
      return Object.assign({ url: t.url, title: t.title, verified: !!(r && r.ok) }, r);
    }
    if (op === 'fill') {
      const r = await inPage(tab.id, (n, id, v, e) => window.__voxstride.fill(n, id, v, e), [nonce, String(a.elementId), String(a.text || ''), !!a.enter]);
      return Object.assign({ verified: !!(r && r.ok && r.value === String(a.text || '')) }, r);
    }
    if (op === 'back') { await chrome.tabs.goBack(tab.id); return { ok: true, verified: true }; }
    return { ok: false, error: 'unknown_op' };
  } finally {
    setBadge(connected ? 'ON' : '', '#14B8A6');
  }
}

async function onMessage(m) {
  if (m.type === 'welcome') { connected = true; setBadge('ON', '#14B8A6'); chrome.storage.session.set({ status: 'connected', error: '' }); return; }
  if (m.type === 'error') { chrome.storage.session.set({ status: 'error', error: m.error }); return; }
  if (m.type !== 'request' || typeof m.id !== 'string') return;
  let reply;
  try { reply = await handle(m.op, m.args || {}); }
  catch (e) { reply = { ok: false, error: String(e && e.message || e).slice(0, 200) }; }
  if (port) port.postMessage(Object.assign({ id: m.id }, reply));
}

chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);
chrome.alarms.create('voxstride-reconnect', { periodInMinutes: 1 });
chrome.alarms.onAlarm.addListener(a => { if (a.name === 'voxstride-reconnect' && !port) connect(); });
chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
  if (msg === 'reconnect') { connect(); reply(true); }
  return false;
});
connect();
