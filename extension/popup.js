const $ = id => document.getElementById(id);

async function render() {
  const s = await chrome.storage.session.get(['status', 'error']);
  const st = $('status');
  if (s.status === 'connected') { st.textContent = '● Connected to VoxStride'; st.className = 'status ok'; }
  else {
    st.className = 'status bad';
    st.textContent = s.error === 'voxstride_not_running' ? 'VoxStride isn\'t running. Start it, then click Reconnect.' : 'Not connected to VoxStride.';
    const b = document.createElement('button'); b.textContent = 'Reconnect'; b.style.marginLeft = '6px';
    b.onclick = async () => { await chrome.runtime.sendMessage('reconnect'); setTimeout(render, 800); };
    st.appendChild(b);
  }
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  const site = $('site');
  site.textContent = '';
  let origin = '';
  try { origin = new URL(tab.url).origin; } catch { }
  if (!/^https?:/.test(origin)) { site.textContent = 'This page can\'t be used by VoxStride.'; return; }
  const has = await chrome.permissions.contains({ origins: [origin + '/*'] });
  const label = document.createElement('div');
  label.textContent = (has ? 'Allowed on ' : 'Not allowed on ') + new URL(origin).host;
  const btn = document.createElement('button');
  btn.className = has ? '' : 'primary';
  btn.textContent = has ? 'Stop allowing this site' : 'Allow on this site';
  btn.style.marginTop = '6px';
  btn.onclick = async () => {
    if (has) await chrome.permissions.remove({ origins: [origin + '/*'] });
    else await chrome.permissions.request({ origins: [origin + '/*'] }); // the browser shows its own permission prompt
    render();
  };
  site.append(label, btn);
}
render();
