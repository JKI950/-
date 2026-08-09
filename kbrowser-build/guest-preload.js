'use strict';
const { ipcRenderer } = require('electron');

function openInHost(value) {
  try {
    const url = new URL(String(value || ''), location.href).href;
    if (/^https?:/i.test(url) && url !== 'about:blank') {
      ipcRenderer.sendToHost('open-url-in-tab', url);
      return true;
    }
  } catch {}
  return false;
}

function findUsername(form, passwordInput) {
  const selectors = [
    'input[autocomplete="username"]','input[type="email"]','input[name*="user" i]',
    'input[id*="user" i]','input[name*="login" i]','input[id*="login" i]','input[type="text"]'
  ];
  for (const selector of selectors) {
    const el = form.querySelector(selector);
    if (el && el !== passwordInput && el.value) return el.value;
  }
  return '';
}

// target=_blank 링크를 호스트 탭으로 직접 보냅니다.
document.addEventListener('click', (event) => {
  try {
    if (event.defaultPrevented || event.button !== 0) return;
    const a = event.target?.closest?.('a[href]');
    if (!a || String(a.target || '').toLowerCase() !== '_blank') return;
    if (!openInHost(a.href)) return;
    event.preventDefault();
    event.stopImmediatePropagation();
  } catch {}
}, true);

// target=_blank 폼도 새 KBrowser 탭에서 처리합니다.
document.addEventListener('submit', (event) => {
  try {
    const form = event.target;
    if (!(form instanceof HTMLFormElement)) return;
    const passwordInput = form.querySelector('input[type="password"]');
    if (passwordInput?.value) {
      ipcRenderer.sendToHost('credentials-submitted', {
        url: location.href,
        username: findUsername(form, passwordInput),
        password: passwordInput.value
      });
    }
    if (String(form.target || '').toLowerCase() !== '_blank') return;
    const action = form.action || location.href;
    if ((form.method || 'get').toLowerCase() === 'get') {
      const u = new URL(action, location.href);
      new FormData(form).forEach((v, k) => u.searchParams.append(k, String(v)));
      if (openInHost(u.href)) event.preventDefault();
    }
  } catch {}
}, true);

// 일부 사이트는 window.open('about:blank') 후 location을 바꿉니다.
// 빈 팝업을 실제 BrowserWindow로 만들지 않고 location 변경을 감지하는 가상 창을 돌려줍니다.
try {
  const nativeOpen = window.open;
  window.open = function(url, target, features) {
    if (url && String(url) !== 'about:blank') {
      if (openInHost(url)) return null;
      return nativeOpen.call(window, url, target, features);
    }
    let closed = false;
    const fakeLocation = {
      href: 'about:blank',
      assign(v) { if (openInHost(v)) this.href = String(v); },
      replace(v) { if (openInHost(v)) this.href = String(v); },
      toString() { return this.href; }
    };
    return new Proxy({}, {
      get(_t, p) {
        if (p === 'location') return fakeLocation;
        if (p === 'closed') return closed;
        if (p === 'close') return () => { closed = true; };
        if (p === 'focus' || p === 'blur') return () => {};
        return undefined;
      },
      set(_t, p, v) {
        if (p === 'location') { openInHost(v); return true; }
        return true;
      }
    });
  };
} catch {}
