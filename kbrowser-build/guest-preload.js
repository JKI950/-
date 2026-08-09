'use strict';
const { ipcRenderer } = require('electron');

function findUsername(form, passwordInput) {
  const selectors = [
    'input[autocomplete="username"]',
    'input[type="email"]',
    'input[name*="user" i]',
    'input[id*="user" i]',
    'input[name*="login" i]',
    'input[id*="login" i]',
    'input[type="text"]'
  ];
  for (const selector of selectors) {
    const el = form.querySelector(selector);
    if (el && el !== passwordInput && el.value) return el.value;
  }
  return '';
}

// target=_blank 링크는 Electron의 빈 팝업을 먼저 만들지 않고 KBrowser 탭으로 전달한다.
document.addEventListener('click', (event) => {
  try {
    if (event.defaultPrevented || event.button !== 0) return;
    const a = event.target?.closest?.('a[href]');
    if (!a) return;
    const target = String(a.target || '').toLowerCase();
    if (target !== '_blank') return;
    const url = new URL(a.href, location.href).href;
    if (!/^https?:/i.test(url)) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    ipcRenderer.sendToHost('open-url-in-tab', url);
  } catch {}
}, true);

// 중간 버튼으로 링크를 열 때도 새 탭으로 전달한다.
document.addEventListener('auxclick', (event) => {
  try {
    if (event.button !== 1) return;
    const a = event.target?.closest?.('a[href]');
    if (!a) return;
    const url = new URL(a.href, location.href).href;
    if (!/^https?:/i.test(url)) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    ipcRenderer.sendToHost('open-url-in-tab', url);
  } catch {}
}, true);

document.addEventListener('submit', (event) => {
  try {
    const form = event.target;
    if (!(form instanceof HTMLFormElement)) return;
    const passwordInput = form.querySelector('input[type="password"]');
    if (!passwordInput?.value) return;
    ipcRenderer.sendToHost('credentials-submitted', {
      url: location.href,
      username: findUsername(form, passwordInput),
      password: passwordInput.value
    });
  } catch {}
}, true);
