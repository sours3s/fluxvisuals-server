// Общие хелперы сайта FluxVisuals
const API_BASE = '/api';
const TOKEN_KEY = 'flux_token';

function getToken() { return localStorage.getItem(TOKEN_KEY) || ''; }
function saveToken(t) { localStorage.setItem(TOKEN_KEY, t); }
function clearToken() { localStorage.removeItem(TOKEN_KEY); }

async function api(path, options = {}) {
  const headers = { 'Content-Type': 'application/json', ...(options.headers || {}) };
  const token = getToken();
  if (token) headers['Authorization'] = 'Bearer ' + token;
  const res = await fetch(API_BASE + path, { ...options, headers });
  return res;
}

async function apiJson(path, options = {}) {
  const res = await api(path, options);
  const isJson = (res.headers.get('content-type') || '').includes('json');
  const data = isJson ? await res.json() : null;
  return { res, data };
}

function esc(s) {
  return String(s ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c]));
}

function fmtDate(iso) {
  if (!iso) return '—';
  return new Date(iso).toLocaleDateString('ru-RU', { day: 'numeric', month: 'long', year: 'numeric' });
}

// ---- Страница: навбар с состоянием входа ----
function initNav() {
  const links = document.getElementById('navLinks');
  if (!links) return;
  const token = getToken();
  if (token) {
    links.insertAdjacentHTML('beforeend', `
      <a href="/account.html">Кабинет</a>
      <button class="btn btn-ghost" onclick="siteLogout()">Выйти</button>
    `);
  } else {
    links.insertAdjacentHTML('beforeend', `
      <a href="/login.html">Войти</a>
      <a class="btn btn-primary" href="/register.html">Регистрация</a>
    `);
  }
}

async function siteLogout() {
  clearToken();
  location.href = '/';
}

// ---- Страница кабинета: guard ----
async function requireAuth() {
  if (!getToken()) { location.href = '/login.html'; return null; }
  const { res, data } = await apiJson('/auth/verify');
  if (!res.ok || !data) { clearToken(); location.href = '/login.html'; return null; }
  return data;
}
