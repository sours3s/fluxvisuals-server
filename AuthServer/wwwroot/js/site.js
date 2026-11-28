// FluxVisuals — общие хелперы сайта
const API_BASE = '/api';
const TOKEN_KEY = 'flux_token';

function getToken() { return localStorage.getItem(TOKEN_KEY) || ''; }
function saveToken(t) {
  localStorage.setItem(TOKEN_KEY, t);
  localStorage.setItem('authToken', t);
}
function clearToken() {
  localStorage.removeItem(TOKEN_KEY);
  localStorage.removeItem('authToken');
}

async function api(path, options = {}) {
  const headers = { 'Content-Type': 'application/json', ...(options.headers || {}) };
  const token = getToken();
  if (token) headers['Authorization'] = 'Bearer ' + token;
  return fetch(API_BASE + path, { ...options, headers });
}

async function apiJson(path, options = {}) {
  const res = await api(path, options);
  const isJson = (res.headers.get('content-type') || '').includes('json');
  const data = isJson ? await res.json() : null;
  return { res, data };
}

function esc(s) {
  return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

function fmtDate(iso) {
  if (!iso) return '—';
  return new Date(iso).toLocaleDateString('ru-RU', { day: 'numeric', month: 'long', year: 'numeric' });
}

// ---------- fv-single-url v1: страницы открываются без смены адреса ----------
const FV_VIEWS = {
  login: '/login.html',
  register: '/register.html',
  account: '/account.html',
  admin: '/admin/'
};
const FV_VIEW_BY_PATH = {
  '/login.html': 'login',
  '/register.html': 'register',
  '/account.html': 'account',
  '/admin/': 'admin',
  '/admin/index.html': 'admin'
};

function fvViewFromHref(href) {
  try {
    const u = new URL(href, location.href);
    if (u.origin !== location.origin) return null;
    return FV_VIEW_BY_PATH[u.pathname] || null;
  } catch (e) {
    return null;
  }
}

function fvPathFor(target) {
  if (FV_VIEWS[target]) return FV_VIEWS[target];
  const view = fvViewFromHref(target);
  return view ? FV_VIEWS[view] : target;
}

const fvStyle = document.createElement('style');
fvStyle.textContent = 'html.fv-view-open body > *:not(#fv-view-frame){display:none !important}' +
  'html.fv-view-open body{overflow:hidden}';
document.head.appendChild(fvStyle);

function fvCloseView() {
  const frame = document.getElementById('fv-view-frame');
  if (frame) frame.remove();
  document.documentElement.classList.remove('fv-view-open');
}

function fvBindFrame(frame) {
  let doc;
  try { doc = frame.contentDocument; } catch (e) { return; }
  if (!doc || !doc.documentElement || doc.documentElement.dataset.fvBound === '1') return;
  doc.documentElement.dataset.fvBound = '1';
  doc.addEventListener('click', e => {
    const a = e.target && e.target.closest ? e.target.closest('a[href]') : null;
    if (!a) return;
    const href = a.getAttribute('href');
    const view = fvViewFromHref(href);
    if (view) {
      e.preventDefault();
      fvOpenView(view);
      return;
    }
    try {
      const u = new URL(href, doc.location.href);
      if (u.origin === location.origin && (u.pathname === '/' || u.pathname === '')) {
        e.preventDefault();
        fvCloseView();
      }
    } catch (err) {}
  }, true);
}

function fvOpenView(target) {
  const path = fvPathFor(target);
  if (!path || typeof path !== 'string' || !path.startsWith('/')) {
    location.href = path;
    return;
  }
  let frame = document.getElementById('fv-view-frame');
  if (!frame) {
    frame = document.createElement('iframe');
    frame.id = 'fv-view-frame';
    frame.title = 'FluxVisuals';
    frame.setAttribute('style', 'position:fixed;inset:0;width:100vw;height:100vh;border:0;margin:0;padding:0;z-index:2147483000;background:#0b0d12;display:block');
    frame.addEventListener('load', () => fvBindFrame(frame));
    document.body.appendChild(frame);
  }
  document.documentElement.classList.add('fv-view-open');
  const absolute = new URL(path, location.href).href;
  if (frame.src !== absolute) frame.src = path;
  history.replaceState(null, '', '/');
}

function fvGo(target) {
  if (target === '/' || target === 'home') {
    if (window.parent && window.parent !== window && typeof window.parent.fvCloseView === 'function') {
      window.parent.fvCloseView();
    } else {
      fvCloseView();
    }
    return;
  }
  const path = fvPathFor(target);
  if (window.parent && window.parent !== window && typeof window.parent.fvOpenView === 'function') {
    window.parent.fvOpenView(path);
    return;
  }
  fvOpenView(path);
}

window.fvGo = fvGo;
window.fvOpenView = fvOpenView;
window.fvCloseView = fvCloseView;

document.addEventListener('click', e => {
  if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
  const a = e.target && e.target.closest ? e.target.closest('a[href]') : null;
  if (!a || (a.target && a.target !== '_self')) return;
  const view = fvViewFromHref(a.getAttribute('href'));
  if (!view) return;
  e.preventDefault();
  fvOpenView(view);
}, true);

const fvPendingView = sessionStorage.getItem('fv_view');
if (fvPendingView) {
  sessionStorage.removeItem('fv_view');
  if (FV_VIEWS[fvPendingView]) fvOpenView(fvPendingView);
}

// Шапка сайта: состояние входа
function initNav(page) {
  const box = document.getElementById('navActions');
  if (!box) return;
  const token = getToken();
  if (token) {
    box.innerHTML = `
      <a class="btn btn-ghost btn-sm" href="/account.html">Кабинет</a>
      <button class="btn btn-primary btn-sm" onclick="siteLogout()">Выйти</button>`;
  } else {
    box.innerHTML = `
      <a class="btn btn-ghost btn-sm" href="/login.html">Войти</a>
      <a class="btn btn-primary btn-sm" href="/register.html">Регистрация</a>`;
  }
}

async function siteLogout() {
  clearToken();
  fvGo('/');
}

// Защита страниц кабинета
async function requireAuth() {
  if (!getToken()) { fvGo('login'); return null; }
  const { res, data } = await apiJson('/auth/verify');
  if (!res.ok || !data) { clearToken(); fvGo('login'); return null; }
  return data;
}

// ---------- Catal в кабинете: вкладка «Поддержка» ----------
function initSiteCatal() {
  const input = document.getElementById('siteAiInput');
  if (!input) return;

  const box = document.getElementById('siteAiMessages');
  const sendBtn = document.getElementById('siteAiSend');
  const statusEl = document.getElementById('siteAiStatus');
  const attachment = document.getElementById('siteAiAttachment');
  const thumb = document.getElementById('siteAiAttachmentThumb');
  const attachmentName = document.getElementById('siteAiAttachmentName');
  const fileInput = document.getElementById('siteAiImageInput');

  let history = [];
  let busy = false;
  let image = null;
  let typing = null;

  function setStatus(available, reason) {
    if (!statusEl) return;
    statusEl.textContent = available ? 'На связи · видит скриншоты' : (reason || 'Сейчас недоступен');
    statusEl.style.color = available ? 'var(--ok)' : 'var(--warn)';
  }

  function renderAi(text) {
    const holder = document.createElement('div');
    holder.textContent = text || '';
    let html = holder.innerHTML;
    html = html.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
    html = html.replace(/`([^`]+)`/g, '<code>$1</code>');
    html = html.replace(/(https?:\/\/[^\s<]+)/g, '<a href="$1" target="_blank" rel="noopener">$1</a>');
    return html;
  }

  function scrollAi() {
    if (box) box.scrollTop = box.scrollHeight;
  }

  function appendAi(role, text, shot) {
    const empty = document.getElementById('siteAiEmpty');
    if (empty) empty.remove();
    const el = document.createElement('div');
    el.className = 'ai-msg-site ' + (role === 'user' ? 'user' : role === 'error' ? 'error' : 'model');
    el.innerHTML = renderAi(text);
    if (shot) {
      const img = document.createElement('img');
      img.className = 'ai-msg-image-site';
      img.src = shot;
      img.alt = 'Прикреплённый скриншот';
      el.appendChild(img);
    }
    if (box) box.appendChild(el);
    scrollAi();
  }

  function setSendState() {
    if (sendBtn) sendBtn.disabled = busy || !((input.value || '').trim() || image);
  }

  function setBusy(value) {
    busy = value;
    setSendState();
    if (busy && box) {
      if (!typing) {
        typing = document.createElement('div');
        typing.className = 'ai-typing-site';
        typing.innerHTML = '<i></i><i></i><i></i>';
        box.appendChild(typing);
      }
      scrollAi();
    } else if (typing) {
      typing.remove();
      typing = null;
    }
  }

  function updateAttachment() {
    if (!attachment) return;
    attachment.hidden = !image;
    if (thumb) { thumb.hidden = !image; thumb.src = image || ''; }
    if (attachmentName) attachmentName.textContent = image ? 'Скриншот прикреплён' : '';
    setSendState();
  }

  function trimHistory() {
    if (history.length > 10) history = history.slice(-10);
  }

  function prepareImage(file) {
    return new Promise((resolve, reject) => {
      if (!file || !(file.type || '').startsWith('image/')) { reject(new Error('Нужен файл изображения.')); return; }
      if (file.size > 20 * 1024 * 1024) { reject(new Error('Изображение слишком большое — максимум 20 МБ.')); return; }
      const reader = new FileReader();
      reader.onerror = () => reject(new Error('Не удалось прочитать изображение.'));
      reader.onload = () => {
        const img = new Image();
        img.onerror = () => reject(new Error('Не удалось открыть изображение.'));
        img.onload = () => {
          const max = 1600;
          let w = img.naturalWidth || img.width;
          let h = img.naturalHeight || img.height;
          const scale = Math.min(1, max / Math.max(w, h));
          w = Math.max(1, Math.round(w * scale));
          h = Math.max(1, Math.round(h * scale));
          const canvas = document.createElement('canvas');
          canvas.width = w; canvas.height = h;
          const ctx = canvas.getContext('2d');
          if (!ctx) { reject(new Error('Браузер не смог обработать изображение.')); return; }
          ctx.drawImage(img, 0, 0, w, h);
          resolve(canvas.toDataURL('image/jpeg', 0.82));
        };
        img.src = reader.result;
      };
      reader.readAsDataURL(file);
    });
  }

  async function attachImage(file) {
    if (!file) return;
    try {
      image = await prepareImage(file);
      updateAttachment();
    } catch (err) {
      appendAi('error', err.message || 'Не удалось прикрепить изображение.');
    }
  }

  async function sendAi() {
    if (busy) return;
    const text = (input.value || '').trim();
    if (!text && !image) return;
    const shot = image;
    const shown = text || '[скриншот]';
    const requestHistory = history.slice();
    history.push({ role: 'user', text: shown });
    trimHistory();
    appendAi('user', shown, shot);
    input.value = '';
    input.style.height = '';
    image = null;
    updateAttachment();
    setBusy(true);
    try {
      const { res, data } = await apiJson('/ai/ask', {
        method: 'POST',
        body: JSON.stringify({
          message: text || 'Посмотри на скриншот и помоги разобраться.',
          history: requestHistory,
          image: shot
        })
      });
      if (!res.ok) throw new Error(data?.error || 'Catal не ответил. Попробуйте ещё раз.');
      const answer = (data?.answer || '').trim();
      if (!answer) throw new Error('Catal вернул пустой ответ. Попробуйте ещё раз.');
      appendAi('model', answer);
      history.push({ role: 'model', text: answer });
      trimHistory();
    } catch (err) {
      appendAi('error', err.message || 'Catal не отвечает. Попробуйте позже.');
    } finally {
      setBusy(false);
    }
  }

  sendBtn?.addEventListener('click', sendAi);
  input.addEventListener('input', () => {
    input.style.height = 'auto';
    input.style.height = Math.min(input.scrollHeight, 130) + 'px';
    setSendState();
  });
  input.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendAi(); }
  });

  // Ctrl+V со скриншотом работает только во вкладке «Поддержка».
  document.addEventListener('paste', (e) => {
    const view = document.getElementById('view-support');
    if (view && view.hidden) return;
    const items = e.clipboardData && e.clipboardData.items;
    if (!items) return;
    for (const item of items) {
      if (item.kind !== 'file' || !(item.type || '').startsWith('image/')) continue;
      const blob = item.getAsFile();
      if (!blob) continue;
      e.preventDefault();
      attachImage(blob);
      return;
    }
  }, true);

  document.getElementById('siteAiAttach')?.addEventListener('click', () => fileInput?.click());
  fileInput?.addEventListener('change', (e) => {
    const file = e.target.files && e.target.files[0];
    e.target.value = '';
    attachImage(file);
  });
  document.getElementById('siteAiAttachmentRemove')?.addEventListener('click', () => {
    image = null;
    if (fileInput) fileInput.value = '';
    updateAttachment();
  });

  setSendState();
  apiJson('/ai/status').then(({ res, data }) => {
    if (!res.ok) { setStatus(false, data?.error || 'Сейчас недоступен'); return; }
    setStatus(!!data?.available);
  }).catch(() => setStatus(false, 'Не удалось проверить доступность'));
}

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initSiteCatal);
else initSiteCatal();
