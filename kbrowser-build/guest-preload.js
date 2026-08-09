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
